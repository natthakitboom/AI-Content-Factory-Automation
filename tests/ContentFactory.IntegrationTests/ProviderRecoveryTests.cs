using System.Net;
using System.Text;
using System.Text.Json;
using ContentFactory.Contracts;
using ContentFactory.Core.Exceptions;
using ContentFactory.Infrastructure.Billing;
using ContentFactory.Infrastructure.Persistence;
using ContentFactory.Infrastructure.Persistence.Entities;
using ContentFactory.Infrastructure.Providers.Fal;
using ContentFactory.Infrastructure.Security;
using ContentFactory.Infrastructure.Storage;
using ContentFactory.IntegrationTests.Infrastructure;
using ContentFactory.Worker.Handlers;
using Microsoft.EntityFrameworkCore;

namespace ContentFactory.IntegrationTests;

[CollectionDefinition(Name)]
public sealed class ProviderRecoveryCollection : ICollectionFixture<PostgresSchemaFixture>
{
    public const string Name = "postgres-provider-recovery";
}

/// <summary>
/// T14 poll, safe download, reconciliation and cancellation tests. All traffic goes to a
/// fake in-process network; no real FAL_KEY, paid call or DNS lookup is made.
/// </summary>
[Collection(ProviderRecoveryCollection.Name)]
public sealed class ProviderRecoveryTests : IAsyncLifetime
{
    private const string Provider = "fal";
    private const string Model = "fal-ai/wan/v2.2-a14b/image-to-video/turbo";
    private const string ApiKey = "test-fal-key";

    private readonly PostgresSchemaFixture _fixture;
    private readonly string _directory = Path.Combine(
        Path.GetTempPath(),
        "contentfactory-provider-recovery-tests",
        Guid.NewGuid().ToString("N"));

    public ProviderRecoveryTests(PostgresSchemaFixture fixture)
    {
        _fixture = fixture;
        Directory.CreateDirectory(_directory);
    }

    public Task InitializeAsync() => Task.CompletedTask;

    public Task DisposeAsync()
    {
        try
        {
            if (Directory.Exists(_directory))
            {
                Directory.Delete(_directory, recursive: true);
            }
        }
        catch (IOException)
        {
            // Best-effort cleanup.
        }

        return Task.CompletedTask;
    }

    [Fact]
    public async Task InProgressPollReschedules()
    {
        await using var db = _fixture.CreateContext();
        var network = new FakeNetwork();
        network.StatusReplies.Enqueue(Json("""{"status":"IN_PROGRESS"}"""));
        var env = Create(db, network);
        var seeded = await SeedAsync(db, "submitted");

        var before = DateTimeOffset.UtcNow;
        var outcome = await env.Poll.ExecuteAsync(Job("poll_video", seeded.OperationId), default);

        Assert.Equal("retry_wait", outcome.Status);
        Assert.NotNull(outcome.NextRunAt);
        Assert.True(outcome.NextRunAt > before);
        Assert.Equal("in_progress", (await Reload(db, seeded)).Status);
        Assert.Equal(0, network.PostCount);
        Assert.All(
            network.Requests.Where(x => x.Host == "queue.fal.run"),
            x => Assert.Equal($"Key {ApiKey}", x.Authorization));
    }

    [Fact]
    public async Task PollRetriesTooManyRequestsHonoringRetryAfter()
    {
        await using var db = _fixture.CreateContext();
        var network = new FakeNetwork();
        network.StatusReplies.Enqueue(new FakeReply(
            HttpStatusCode.TooManyRequests, "{}", RetryAfterSeconds: 7));
        network.StatusReplies.Enqueue(new FakeReply(HttpStatusCode.BadGateway, "{}"));
        network.StatusReplies.Enqueue(Json("""{"status":"IN_PROGRESS"}"""));
        var env = Create(db, network);
        var seeded = await SeedAsync(db, "submitted");

        var outcome = await env.Poll.ExecuteAsync(Job("poll_video", seeded.OperationId), default);

        Assert.Equal("retry_wait", outcome.Status);
        Assert.Equal(3, network.Requests.Count(x => x.Path.EndsWith("/status")));
        Assert.True(env.Delays[0] >= TimeSpan.FromSeconds(7));
        Assert.Equal(2, env.Delays.Count);
        Assert.Equal(0, network.PostCount);
    }

    [Fact]
    public async Task PollFailureNeverBecomesPaidSubmit()
    {
        await using var db = _fixture.CreateContext();
        var network = new FakeNetwork();
        for (var i = 0; i < 10; i++)
        {
            network.StatusReplies.Enqueue(new FakeReply(HttpStatusCode.InternalServerError, "{}"));
        }

        var env = Create(db, network);
        var seeded = await SeedAsync(db, "submitted");

        var outcome = await env.Poll.ExecuteAsync(Job("poll_video", seeded.OperationId), default);

        Assert.Equal("retry_wait", outcome.Status);
        Assert.Equal(0, network.PostCount);
        var row = await Reload(db, seeded);
        Assert.Equal(seeded.ProviderRequestId, row.ProviderRequestId);
        Assert.NotEqual("failed_confirmed", row.Status);
    }

    [Fact]
    public async Task PollPastThirtyMinutesMarksDelayedWithoutResubmit()
    {
        await using var db = _fixture.CreateContext();
        var network = new FakeNetwork();
        network.StatusReplies.Enqueue(Json("""{"status":"IN_QUEUE"}"""));
        var env = Create(db, network);
        var seeded = await SeedAsync(db, "submitted", submittedAgo: TimeSpan.FromMinutes(45));

        var outcome = await env.Poll.ExecuteAsync(Job("poll_video", seeded.OperationId), default);

        Assert.Equal("retry_wait", outcome.Status);
        Assert.Equal("PROVIDER_DELAYED", outcome.ErrorCode);
        Assert.Equal(0, network.PostCount);
    }

    [Fact]
    public async Task CompletedResultDownloadedOnce()
    {
        await using var db = _fixture.CreateContext();
        var network = new FakeNetwork();
        var env = Create(db, network);
        var seeded = await SeedAsync(db, "submitted");

        var first = await env.Download.ExecuteAsync(
            Job("download_video", seeded.OperationId), default);
        var second = await env.Download.ExecuteAsync(
            Job("download_video", seeded.OperationId), default);

        Assert.Equal("succeeded", first.Status);
        Assert.Equal("succeeded", second.Status);
        Assert.Equal(first.OutputReference, second.OutputReference);
        Assert.Equal(1, network.Requests.Count(x => x.Host == "v3.fal.media"));
        Assert.Equal(0, network.PostCount);

        var row = await Reload(db, seeded);
        Assert.Equal("succeeded", row.Status);
        Assert.NotNull(row.OutputAssetId);
        Assert.Equal(row.OutputAssetId.Value.ToString("D"), first.OutputReference);
        var asset = await db.Assets.SingleAsync(x => x.Id == row.OutputAssetId);
        Assert.Equal(AssetKind.Video, asset.Kind);

        var reservation = await db.BudgetReservations
            .SingleAsync(x => x.OperationId == seeded.OperationId);
        Assert.Equal("settled", reservation.Status);
    }

    [Fact]
    public async Task RestartAfterDownloadBeforeFinalizeDoesNotDuplicateAsset()
    {
        await using var db = _fixture.CreateContext();
        var network = new FakeNetwork();
        var env = Create(db, network);
        var seeded = await SeedAsync(db, "submitted");

        // Simulate a crash after the blob was stored but before provider_requests was updated.
        await env.Download.ExecuteAsync(Job("download_video", seeded.OperationId), default);
        var storedAssetId = (await Reload(db, seeded)).OutputAssetId;
        await db.ProviderRequests
            .Where(x => x.ClientOperationId == seeded.OperationId)
            .ExecuteUpdateAsync(s => s
                .SetProperty(x => x.OutputAssetId, (Guid?)null)
                .SetProperty(x => x.Status, "submitted"));
        db.ChangeTracker.Clear();

        var outcome = await env.Download.ExecuteAsync(
            Job("download_video", seeded.OperationId), default);

        Assert.Equal("succeeded", outcome.Status);
        Assert.Equal(storedAssetId, (await Reload(db, seeded)).OutputAssetId);
        Assert.Equal(0, network.PostCount);
        Assert.Equal(
            1,
            await db.Assets.CountAsync(x => x.Kind == AssetKind.Video
                && x.Sha256 == FakeNetwork.Mp4Sha256));
    }

    [Fact]
    public async Task RedirectToPrivateIpRejected()
    {
        await using var db = _fixture.CreateContext();
        var network = new FakeNetwork();
        network.MediaUrls.Enqueue("https://v3.fal.media/files/redirect.mp4");
        network.Redirects["/files/redirect.mp4"] = "https://internal.fal.media/secret.mp4";
        var env = Create(db, network);
        env.Resolver.Map["internal.fal.media"] = [System.Net.IPAddress.Parse("10.0.0.5")];
        var seeded = await SeedAsync(db, "submitted");

        var outcome = await env.Download.ExecuteAsync(
            Job("download_video", seeded.OperationId), default);

        Assert.Equal("blocked", outcome.Status);
        Assert.Equal("MEDIA_URL_REJECTED", outcome.ErrorCode);
        Assert.DoesNotContain(network.Requests, x => x.Host == "internal.fal.media");
        Assert.Null((await Reload(db, seeded)).OutputAssetId);
    }

    [Theory]
    [InlineData("http://v3.fal.media/files/a.mp4")]
    [InlineData("https://127.0.0.1/files/a.mp4")]
    [InlineData("https://evil.example.com/files/a.mp4")]
    [InlineData("https://v3.fal.media.evil.example.com/files/a.mp4")]
    [InlineData("https://user:pw@v3.fal.media/files/a.mp4")]
    public async Task UnapprovedMediaUrlsAreRejected(string url)
    {
        await using var db = _fixture.CreateContext();
        var network = new FakeNetwork();
        var env = Create(db, network);

        var error = await Assert.ThrowsAsync<ValidationException>(() =>
            env.Downloader.DownloadAsync(
                new Uri(url), env.Store, AssetKind.Video, default));

        Assert.Equal("MEDIA_URL_REJECTED", error.Code);
        Assert.Empty(network.Requests);
    }

    [Theory]
    [InlineData("64:ff9b::a00:1", false)]
    [InlineData("64:ff9b::7f00:1", false)]
    [InlineData("2002:0a00:0001::1", false)]
    [InlineData("2001:0:4136:e378:8000:63bf:3fff:fdd2", false)]
    [InlineData("2001:db8::1", false)]
    [InlineData("::ffff:10.0.0.1", false)]
    [InlineData("fd00::1", false)]
    [InlineData("fe80::1", false)]
    [InlineData("::1", false)]
    [InlineData("100.64.0.1", false)]
    [InlineData("169.254.169.254", false)]
    [InlineData("2606:4700:4700::1111", true)]
    [InlineData("93.184.216.34", true)]
    public void PublicAddressPolicy(string address, bool expected)
        => Assert.Equal(
            expected,
            SafeMediaDownloader.IsPublicAddress(System.Net.IPAddress.Parse(address)));

    [Fact]
    public async Task MediaHostNeverReceivesFalCredentials()
    {
        await using var db = _fixture.CreateContext();
        var network = new FakeNetwork();
        var env = Create(db, network);
        var seeded = await SeedAsync(db, "submitted");

        await env.Download.ExecuteAsync(Job("download_video", seeded.OperationId), default);

        var media = Assert.Single(network.Requests, x => x.Host == "v3.fal.media");
        Assert.Null(media.Authorization);
    }

    [Fact]
    public async Task OversizedDownloadIsRejected()
    {
        await using var db = _fixture.CreateContext();
        var network = new FakeNetwork { MediaPadding = 4096 };
        var env = Create(db, network, maxDownloadBytes: 1024);

        var error = await Assert.ThrowsAsync<ValidationException>(() =>
            env.Downloader.DownloadAsync(
                new Uri("https://v3.fal.media/files/a.mp4"),
                env.Store,
                AssetKind.Video,
                default));

        Assert.Equal("MEDIA_TOO_LARGE", error.Code);
    }

    [Fact]
    public async Task SignedUrlRefreshUsesExistingRequest()
    {
        await using var db = _fixture.CreateContext();
        var network = new FakeNetwork();
        network.MediaUrls.Enqueue("https://v3.fal.media/files/expired.mp4");
        network.MediaUrls.Enqueue("https://v3.fal.media/files/fresh.mp4");
        network.Expired.Add("/files/expired.mp4");
        var env = Create(db, network);
        var seeded = await SeedAsync(db, "submitted");

        var outcome = await env.Download.ExecuteAsync(
            Job("download_video", seeded.OperationId), default);

        Assert.Equal("succeeded", outcome.Status);
        Assert.Equal(0, network.PostCount);
        Assert.Equal(
            2,
            network.Requests.Count(x => x.Path == $"/{Model}/requests/{seeded.ProviderRequestId}"));
        var row = await Reload(db, seeded);
        Assert.Equal(seeded.ProviderRequestId, row.ProviderRequestId);
        Assert.Equal(1, await db.ProviderRequests.CountAsync(
            x => x.ClientOperationId == seeded.OperationId));
    }

    [Fact]
    public async Task CancelledDoesNotImplyRefund()
    {
        await using var db = _fixture.CreateContext();
        var network = new FakeNetwork();
        var env = Create(db, network);
        var seeded = await SeedAsync(db, "in_progress");

        await env.Reconciler.RequestCancelAsync(seeded.RowId, "owner-test", default);

        var row = await Reload(db, seeded);
        Assert.Equal("cancellation_requested", row.Status);
        Assert.Contains(network.Requests, x => x.Method == "PUT" && x.Path.EndsWith("/cancel"));
        var reservation = await db.BudgetReservations
            .AsNoTracking()
            .SingleAsync(x => x.OperationId == seeded.OperationId);
        Assert.Equal("reserved", reservation.Status);
        Assert.Null(reservation.ReleasedAt);
        Assert.True(await db.AuditEvents.AnyAsync(
            x => x.Action == "provider_request.cancel_requested" && x.TargetId == seeded.RowId));
    }

    [Fact]
    public async Task UnknownCannotAutoFallback()
    {
        await using var db = _fixture.CreateContext();
        var network = new FakeNetwork();
        var env = Create(db, network);
        var seeded = await SeedAsync(db, "submission_unknown", withQueueReferences: false);

        var poll = await env.Poll.ExecuteAsync(Job("poll_video", seeded.OperationId), default);
        var download = await env.Download.ExecuteAsync(
            Job("download_video", seeded.OperationId), default);

        Assert.Equal("blocked", poll.Status);
        Assert.Equal("UNKNOWN_REQUIRES_RECONCILIATION", poll.ErrorCode);
        Assert.Equal("blocked", download.Status);
        Assert.Empty(network.Requests);
        var reservation = await db.BudgetReservations
            .AsNoTracking()
            .SingleAsync(x => x.OperationId == seeded.OperationId);
        Assert.Equal("reserved", reservation.Status);
    }

    [Fact]
    public async Task OwnerAttachesExistingRequestIdWithAudit()
    {
        await using var db = _fixture.CreateContext();
        var network = new FakeNetwork();
        var env = Create(db, network);
        var seeded = await SeedAsync(db, "submission_unknown", withQueueReferences: false);
        var requestId = Guid.NewGuid().ToString("D");

        await env.Reconciler.AttachExistingRequestAsync(
            seeded.RowId,
            "owner-test",
            requestId,
            new Uri($"https://queue.fal.run/{Model}/requests/{requestId}/status"),
            new Uri($"https://queue.fal.run/{Model}/requests/{requestId}"),
            new Uri($"https://queue.fal.run/{Model}/requests/{requestId}/cancel"),
            default);

        var row = await Reload(db, seeded);
        Assert.Equal("submitted", row.Status);
        Assert.Equal(requestId, row.ProviderRequestId);
        Assert.Equal(0, network.PostCount);
        Assert.True(await db.AuditEvents.AnyAsync(
            x => x.Action == "provider_request.reconcile_attach" && x.TargetId == seeded.RowId));
    }

    [Fact]
    public async Task ReconcileRejectsForeignHostsAndMismatchedIds()
    {
        await using var db = _fixture.CreateContext();
        var env = Create(db, new FakeNetwork());
        var seeded = await SeedAsync(db, "submission_unknown", withQueueReferences: false);
        var requestId = Guid.NewGuid().ToString("D");

        var foreign = await Assert.ThrowsAsync<ValidationException>(() =>
            env.Reconciler.AttachExistingRequestAsync(
                seeded.RowId,
                "owner-test",
                requestId,
                new Uri($"https://evil.example.com/{Model}/requests/{requestId}/status"),
                new Uri($"https://evil.example.com/{Model}/requests/{requestId}"),
                null,
                default));
        var mismatched = await Assert.ThrowsAsync<ValidationException>(() =>
            env.Reconciler.AttachExistingRequestAsync(
                seeded.RowId,
                "owner-test",
                requestId,
                new Uri($"https://queue.fal.run/{Model}/requests/other/status"),
                new Uri($"https://queue.fal.run/{Model}/requests/other"),
                null,
                default));
        var notUnknown = await SeedAsync(db, "submitted");
        var wrongState = await Assert.ThrowsAsync<ConflictException>(() =>
            env.Reconciler.AttachExistingRequestAsync(
                notUnknown.RowId,
                "owner-test",
                requestId,
                new Uri($"https://queue.fal.run/{Model}/requests/{requestId}/status"),
                new Uri($"https://queue.fal.run/{Model}/requests/{requestId}"),
                null,
                default));

        Assert.Equal("RECONCILE_REFERENCE_INVALID", foreign.Code);
        Assert.Equal("RECONCILE_REFERENCE_INVALID", mismatched.Code);
        Assert.Equal("RECONCILE_NOT_UNKNOWN", wrongState.Code);
        Assert.Equal("submission_unknown", (await Reload(db, seeded)).Status);
    }

    [Fact]
    public async Task OwnerConfirmsNoChargeReleasesReservationWithAudit()
    {
        await using var db = _fixture.CreateContext();
        var env = Create(db, new FakeNetwork());
        var seeded = await SeedAsync(db, "submission_unknown", withQueueReferences: false);

        await Assert.ThrowsAsync<ValidationException>(() =>
            env.Reconciler.ConfirmNoChargeAsync(seeded.RowId, "owner-test", " ", default));
        await env.Reconciler.ConfirmNoChargeAsync(
            seeded.RowId, "owner-test", "fal dashboard shows no request", default);

        var row = await Reload(db, seeded);
        Assert.Equal("failed_confirmed", row.Status);
        var reservation = await db.BudgetReservations
            .AsNoTracking()
            .SingleAsync(x => x.OperationId == seeded.OperationId);
        Assert.Equal("released", reservation.Status);
        Assert.True(await db.AuditEvents.AnyAsync(
            x => x.Action == "provider_request.reconcile_no_charge" && x.TargetId == seeded.RowId));
    }

    private async Task<ProviderRequest> Reload(AppDbContext db, Seeded seeded)
    {
        db.ChangeTracker.Clear();
        return await db.ProviderRequests
            .AsNoTracking()
            .SingleAsync(x => x.Id == seeded.RowId);
    }

    private static FakeReply Json(string body) => new(HttpStatusCode.OK, body);

    private static LeasedJob Job(string jobType, string operationId)
        => new(
            Guid.NewGuid(),
            jobType,
            JsonSerializer.Serialize(
                new { request = new { operationId } },
                JsonDefaults.Options),
            1,
            "test-worker",
            1,
            DateTimeOffset.UtcNow.AddMinutes(5));

    private Env Create(
        AppDbContext db,
        FakeNetwork network,
        long maxDownloadBytes = 200L * 1024 * 1024)
    {
        var delays = new List<TimeSpan>();
        var falOptions = new FalOptions
        {
            ApiKey = ApiKey,
            ModelId = Model,
            BaseUrl = new Uri("https://queue.fal.run/"),
            Delay = (delay, _) =>
            {
                delays.Add(delay);
                return Task.CompletedTask;
            },
            Jitter = () => 0d
        };
        var http = new HttpClient(network, disposeHandler: false);
        var provider = new FalVideoProvider(
            http, falOptions, new FalRequestBuilder(new NullAssetStore(), falOptions));

        var resolver = new FakeResolver();
        var downloader = new SafeMediaDownloader(
            new HttpClient(network, disposeHandler: false),
            new SafeMediaDownloaderOptions { MaxBytes = maxDownloadBytes },
            resolver);
        var store = new FileSystemAssetStore(
            db,
            new StorageOptions { DataRoot = Path.Combine(_directory, "data") },
            TimeProvider.System);

        var budgetOptions = new BudgetOptions
        {
            AllowPaidApis = true,
            LiveApprovalFile = WriteApproval(),
            DailyBudgetUsd = 10.00m,
            MonthlyBudgetUsd = 10.00m,
            MaxVideoCostUsd = 10.00m,
            MaxSingleRequestUsd = 10.00m,
            ReservationMultiplier = 1.20m
        };
        var guard = new PostgresBudgetGuard(
            db, budgetOptions, new LiveApprovalGuard(budgetOptions, TimeProvider.System));

        return new Env(
            new PollVideoHandler(db, provider, TimeProvider.System),
            new DownloadVideoHandler(db, provider, downloader, store, guard, TimeProvider.System),
            new ProviderReconciler(db, provider, guard, falOptions, TimeProvider.System),
            downloader,
            store,
            resolver,
            delays,
            budgetOptions);
    }

    private string WriteApproval()
    {
        var path = Path.Combine(_directory, $"{Guid.NewGuid():N}.json");
        File.WriteAllText(
            path,
            JsonSerializer.Serialize(
                new
                {
                    approvalId = $"test-approval-{Guid.NewGuid():N}",
                    provider = Provider,
                    model = Model,
                    maxRequestCount = 100,
                    maxTotalUsd = 100.00m,
                    expiresAt = DateTimeOffset.UtcNow.AddHours(1),
                    allowFallbackProvider = false,
                    approvedBy = "owner-test"
                },
                JsonDefaults.Options));
        return path;
    }

    private async Task<Seeded> SeedAsync(
        AppDbContext db,
        string status,
        bool withQueueReferences = true,
        TimeSpan? submittedAgo = null)
    {
        var now = DateTimeOffset.UtcNow;
        var productId = Guid.NewGuid();
        var channelId = Guid.NewGuid();
        var campaignId = Guid.NewGuid();
        var videoScopeId = Guid.NewGuid();
        var configHash = Guid.NewGuid().ToString("N");

        db.Products.Add(new Product
        {
            Id = productId,
            Title = "Recovery fixture product",
            Market = "TH",
            Locale = "th-TH",
            Version = 1,
            CreatedAt = now,
            UpdatedAt = now
        });
        db.Channels.Add(new Channel
        {
            Id = channelId,
            Platform = "tiktok",
            DisplayName = "Fixture channel",
            Market = "TH",
            Locale = "th-TH",
            Timezone = "Asia/Bangkok",
            Status = "active",
            CapabilitiesJson = "{}"
        });
        db.Campaigns.Add(new Campaign
        {
            Id = campaignId,
            ProductId = productId,
            ChannelId = channelId,
            Mode = VideoMode.HybridMotion,
            Locale = "th-TH",
            TargetDurationMs = 25_000,
            BudgetUsd = 10.00m,
            Status = "active",
            Version = 1,
            CreatedAt = now,
            UpdatedAt = now
        });
        db.PriceSnapshots.Add(new PriceSnapshot
        {
            Id = Guid.NewGuid(),
            Provider = Provider,
            Model = Model,
            ConfigHash = configHash,
            Unit = "request",
            UnitPriceUsd = 0.10m,
            SourceUrl = "https://fal.ai/pricing",
            VerifiedAt = now.AddMinutes(-5),
            ValidUntil = now.AddHours(1)
        });
        await db.SaveChangesAsync();

        var operationId = $"op-{Guid.NewGuid():N}";
        var budgetOptions = new BudgetOptions
        {
            AllowPaidApis = true,
            LiveApprovalFile = WriteApproval(),
            DailyBudgetUsd = 10.00m,
            MonthlyBudgetUsd = 10.00m,
            MaxVideoCostUsd = 10.00m,
            MaxSingleRequestUsd = 10.00m,
            ReservationMultiplier = 1.20m
        };
        var guard = new PostgresBudgetGuard(
            db, budgetOptions, new LiveApprovalGuard(budgetOptions, TimeProvider.System));
        var quote = await new PriceSnapshotService(db, budgetOptions)
            .GetQuoteAsync(Provider, Model, configHash, default);
        await guard.ReserveAsync(operationId, campaignId, videoScopeId, quote, default);

        var providerRequestId = Guid.NewGuid().ToString("D");
        var row = new ProviderRequest
        {
            Id = Guid.NewGuid(),
            Provider = Provider,
            Model = Model,
            ClientOperationId = operationId,
            RequestHash = new string('a', 64),
            Status = status,
            CreatedAt = now.AddMinutes(-1),
            SubmittedAt = withQueueReferences ? now - (submittedAgo ?? TimeSpan.FromSeconds(30)) : null
        };
        if (withQueueReferences)
        {
            row.ProviderRequestId = providerRequestId;
            row.StatusUrl = $"https://queue.fal.run/{Model}/requests/{providerRequestId}/status";
            row.ResponseUrl = $"https://queue.fal.run/{Model}/requests/{providerRequestId}";
            row.CancelUrl = $"https://queue.fal.run/{Model}/requests/{providerRequestId}/cancel";
        }

        db.ProviderRequests.Add(row);
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();
        return new Seeded(row.Id, operationId, withQueueReferences ? providerRequestId : null);
    }

    private sealed record Seeded(Guid RowId, string OperationId, string? ProviderRequestId);

    private sealed record Env(
        PollVideoHandler Poll,
        DownloadVideoHandler Download,
        ProviderReconciler Reconciler,
        SafeMediaDownloader Downloader,
        FileSystemAssetStore Store,
        FakeResolver Resolver,
        List<TimeSpan> Delays,
        BudgetOptions Budget);

    private sealed record FakeReply(
        HttpStatusCode Status, string Body, int? RetryAfterSeconds = null);

    private sealed record SeenRequest(
        string Method, string Host, string Path, string? Authorization);

    private sealed class FakeResolver : IHostAddressResolver
    {
        public Dictionary<string, System.Net.IPAddress[]> Map { get; } = [];

        public Task<IReadOnlyList<System.Net.IPAddress>> ResolveAsync(
            string host, CancellationToken ct)
            => Task.FromResult<IReadOnlyList<System.Net.IPAddress>>(
                Map.TryGetValue(host, out var found)
                    ? found
                    : [System.Net.IPAddress.Parse("93.184.216.34")]);
    }

    private sealed class NullAssetStore : ContentFactory.Core.Abstractions.IAssetStore
    {
        public Task<AssetRef> PutAsync(
            Stream input, AssetKind kind, string mime, CancellationToken ct)
            => throw new InvalidOperationException("Not used by T14 tests.");

        public Task<Stream> OpenReadAsync(Guid assetId, CancellationToken ct)
            => throw new InvalidOperationException("Not used by T14 tests.");
    }

    private sealed class FakeNetwork : HttpMessageHandler
    {
        private static readonly byte[] Mp4Header =
        [
            0, 0, 0, 0x18, (byte)'f', (byte)'t', (byte)'y', (byte)'p',
            (byte)'m', (byte)'p', (byte)'4', (byte)'2', 0, 0, 0, 0,
            (byte)'m', (byte)'p', (byte)'4', (byte)'2', (byte)'i', (byte)'s', (byte)'o', (byte)'m'
        ];

        public static string Mp4Sha256 { get; } = Convert.ToHexString(
            System.Security.Cryptography.SHA256.HashData(Mp4Header)).ToLowerInvariant();

        public List<SeenRequest> Requests { get; } = [];
        public Queue<FakeReply> StatusReplies { get; } = new();
        public Queue<string> MediaUrls { get; } = new();
        public Dictionary<string, string> Redirects { get; } = [];
        public HashSet<string> Expired { get; } = [];
        public int MediaPadding { get; init; }
        public int PostCount => Requests.Count(x => x.Method == "POST");

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var uri = request.RequestUri!;
            Requests.Add(new SeenRequest(
                request.Method.Method,
                uri.Host,
                uri.AbsolutePath,
                request.Headers.Authorization?.ToString()));

            if (uri.Host == "queue.fal.run")
            {
                return Task.FromResult(HandleQueue(request, uri));
            }

            if (Redirects.TryGetValue(uri.AbsolutePath, out var target))
            {
                var redirect = new HttpResponseMessage(HttpStatusCode.Redirect);
                redirect.Headers.Location = new Uri(target);
                return Task.FromResult(redirect);
            }

            if (Expired.Contains(uri.AbsolutePath))
            {
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.Forbidden));
            }

            var bytes = Mp4Header.Concat(new byte[MediaPadding]).ToArray();
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new ByteArrayContent(bytes)
            });
        }

        private HttpResponseMessage HandleQueue(HttpRequestMessage request, Uri uri)
        {
            if (request.Method == HttpMethod.Put && uri.AbsolutePath.EndsWith("/cancel"))
            {
                return Reply(new FakeReply(HttpStatusCode.OK, """{"status":"CANCELLATION_REQUESTED"}"""));
            }

            if (request.Method != HttpMethod.Get)
            {
                return new HttpResponseMessage(HttpStatusCode.MethodNotAllowed);
            }

            if (uri.AbsolutePath.EndsWith("/status"))
            {
                return Reply(StatusReplies.Count > 0
                    ? StatusReplies.Dequeue()
                    : new FakeReply(HttpStatusCode.OK, """{"status":"COMPLETED"}"""));
            }

            var mediaUrl = MediaUrls.Count > 0
                ? MediaUrls.Dequeue()
                : "https://v3.fal.media/files/a.mp4";
            return Reply(new FakeReply(
                HttpStatusCode.OK,
                JsonSerializer.Serialize(new { video = new { url = mediaUrl }, seed = 42 })));
        }

        private static HttpResponseMessage Reply(FakeReply reply)
        {
            var response = new HttpResponseMessage(reply.Status)
            {
                Content = new StringContent(reply.Body, Encoding.UTF8, "application/json")
            };
            if (reply.RetryAfterSeconds is { } seconds)
            {
                response.Headers.RetryAfter = new System.Net.Http.Headers.RetryConditionHeaderValue(
                    TimeSpan.FromSeconds(seconds));
            }

            return response;
        }
    }
}
