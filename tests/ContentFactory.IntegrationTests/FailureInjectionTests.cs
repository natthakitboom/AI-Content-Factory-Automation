using System.Net;
using System.Text;
using System.Text.Json;
using ContentFactory.Contracts;
using ContentFactory.Core.Exceptions;
using ContentFactory.Infrastructure.Billing;
using ContentFactory.Infrastructure.Jobs;
using ContentFactory.Infrastructure.Persistence;
using ContentFactory.Infrastructure.Persistence.Entities;
using ContentFactory.Infrastructure.Providers.Fal;
using ContentFactory.Infrastructure.Providers.Mock;
using ContentFactory.Infrastructure.Security;
using ContentFactory.Infrastructure.Storage;
using ContentFactory.IntegrationTests.Infrastructure;
using ContentFactory.Worker.Handlers;
using Microsoft.EntityFrameworkCore;

namespace ContentFactory.IntegrationTests;

[CollectionDefinition(Name)]
public sealed class FailureInjectionCollection : ICollectionFixture<PostgresSchemaFixture>
{
    public const string Name = "postgres-failure-injection";
}

/// <summary>
/// T22 (offline part): each failure is injected against real PostgreSQL and the real handlers
/// with a counting fake provider. The invariant is always the same: the number of billable
/// POSTs never grows in an ambiguous case, a reservation is never released without evidence,
/// and the state stays durable. FFmpeg is not needed here.
/// </summary>
[Collection(FailureInjectionCollection.Name)]
public sealed class FailureInjectionTests : IAsyncLifetime
{
    private const string Model = FalOptions.DefaultEndpointId;
    private readonly PostgresSchemaFixture _fixture;
    private readonly string _root = Path.Combine(
        Path.GetTempPath(), "contentfactory-failure-tests", Guid.NewGuid().ToString("N"));

    public FailureInjectionTests(PostgresSchemaFixture fixture)
    {
        _fixture = fixture;
    }

    public Task InitializeAsync() => Task.CompletedTask;

    public Task DisposeAsync()
    {
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
        }

        return Task.CompletedTask;
    }

    [Fact]
    public async Task KillBeforeSubmitReplaysExactlyOnePost()
    {
        await using var rig = await Rig.CreateAsync(_fixture, _root);

        var first = await rig.Submit.ExecuteAsync(rig.SubmitJob(), default);
        var replay = await rig.Submit.ExecuteAsync(rig.SubmitJob(), default);

        Assert.Equal("succeeded", first.Status);
        Assert.Equal("succeeded", replay.Status);
        Assert.Equal(1, rig.Network.PostCount);
        Assert.Equal(1, await rig.Db.ProviderRequests.CountAsync(x => x.ClientOperationId == rig.OperationId));
    }

    [Fact]
    public async Task KillAfterProviderAcceptanceNeverPostsAgain()
    {
        await using var rig = await Rig.CreateAsync(_fixture, _root);
        // The worker died after the row went to "submitting": the provider may have the request.
        await rig.Guard.ReserveAsync(rig.OperationId, rig.CampaignId, rig.ScopeId, rig.Quote, default);
        rig.Db.ProviderRequests.Add(new ProviderRequest
        {
            Id = Guid.NewGuid(), Provider = "fal", Model = Model, ClientOperationId = rig.OperationId,
            RequestHash = new string('c', 64), Status = "submitting", CreatedAt = DateTimeOffset.UtcNow
        });
        await rig.Db.SaveChangesAsync();

        var outcome = await rig.Submit.ExecuteAsync(rig.SubmitJob(), default);

        Assert.Equal("blocked", outcome.Status);
        Assert.Equal("SUBMIT_INCOMPLETE_ATTEMPT", outcome.ErrorCode);
        Assert.Equal(0, rig.Network.PostCount);
        await rig.AssertReservationAsync("reserved");
        var poll = await rig.Poll.ExecuteAsync(rig.FollowUpJob("poll_video"), default);
        Assert.Equal("UNKNOWN_REQUIRES_RECONCILIATION", poll.ErrorCode);
    }

    [Fact]
    public async Task LostResponseKeepsTheReservationAndNeverFallsBack()
    {
        await using var rig = await Rig.CreateAsync(_fixture, _root);
        rig.Network.DropAfterAccept = true;

        var first = await rig.Submit.ExecuteAsync(rig.SubmitJob(), default);
        rig.Network.DropAfterAccept = false;
        var replay = await rig.Submit.ExecuteAsync(rig.SubmitJob(), default);
        var poll = await rig.Poll.ExecuteAsync(rig.FollowUpJob("poll_video"), default);
        var download = await rig.Download.ExecuteAsync(rig.FollowUpJob("download_video"), default);

        Assert.Equal("SUBMISSION_UNKNOWN", first.ErrorCode);
        Assert.Equal("SUBMISSION_UNKNOWN", replay.ErrorCode);
        Assert.Equal(1, rig.Network.PostCount);
        Assert.Equal("UNKNOWN_REQUIRES_RECONCILIATION", poll.ErrorCode);
        Assert.Equal("UNKNOWN_REQUIRES_RECONCILIATION", download.ErrorCode);
        Assert.Equal(0, rig.Network.GetCount);
        await rig.AssertReservationAsync("reserved");
        Assert.Equal("submission_unknown", (await rig.RequestAsync()).Status);
    }

    [Fact]
    public async Task LeaseExpiryOnUncertainCloudWorkBlocksInsteadOfRequeueing()
    {
        await using var rig = await Rig.CreateAsync(_fixture, _root);
        var uncertain = await rig.AddRunningJobAsync(JobLane.CloudSubmit, withProviderRequest: true);
        var local = await rig.AddRunningJobAsync(JobLane.HeavyLocal, withProviderRequest: false);

        var swept = await new LeaseWatchdog(rig.Db, TimeProvider.System).SweepAsync(default);

        Assert.True(swept >= 2);
        rig.Db.ChangeTracker.Clear();
        var blocked = await rig.Db.Jobs.AsNoTracking().SingleAsync(x => x.Id == uncertain);
        var requeued = await rig.Db.Jobs.AsNoTracking().SingleAsync(x => x.Id == local);
        Assert.Equal("blocked", blocked.Status);
        Assert.Equal("PROVIDER_STATE_UNCERTAIN", blocked.LastErrorCode);
        Assert.Equal("retry_wait", requeued.Status);
        Assert.Equal(0, rig.Network.PostCount);
    }

    [Fact]
    public async Task DuplicateStatusAndCallbacksDownloadOnce()
    {
        await using var rig = await Rig.CreateAsync(_fixture, _root);
        await rig.Submit.ExecuteAsync(rig.SubmitJob(), default);

        for (var i = 0; i < 3; i++)
        {
            Assert.Equal("succeeded", (await rig.Poll.ExecuteAsync(rig.FollowUpJob("poll_video"), default)).Status);
        }

        var outputs = new List<string?>();
        for (var i = 0; i < 3; i++)
        {
            outputs.Add((await rig.Download.ExecuteAsync(rig.FollowUpJob("download_video"), default)).OutputReference);
        }

        Assert.Single(outputs.Distinct());
        Assert.Equal(1, rig.Network.MediaGetCount);
        Assert.Equal(1, rig.Network.PostCount);
        await rig.AssertReservationAsync("settled");
    }

    [Fact]
    public async Task TooManyRequestsAndOfflineNeverBecomeASecondSubmit()
    {
        await using var rig = await Rig.CreateAsync(_fixture, _root);
        await rig.Submit.ExecuteAsync(rig.SubmitJob(), default);
        rig.Network.StatusFailures = 6;
        rig.Network.StatusFailureKind = "429";

        var limited = await rig.Poll.ExecuteAsync(rig.FollowUpJob("poll_video"), default);
        rig.Network.StatusFailures = 6;
        rig.Network.StatusFailureKind = "offline";
        var offline = await rig.Poll.ExecuteAsync(rig.FollowUpJob("poll_video"), default);

        Assert.Equal("retry_wait", limited.Status);
        Assert.Equal("retry_wait", offline.Status);
        Assert.Equal(1, rig.Network.PostCount);
        Assert.Contains((await rig.RequestAsync()).Status, new[] { "submitted", "in_progress" });
    }

    [Fact]
    public async Task InvalidMediaIsRejectedAndNothingIsSettled()
    {
        await using var rig = await Rig.CreateAsync(_fixture, _root);
        await rig.Submit.ExecuteAsync(rig.SubmitJob(), default);
        rig.Network.InvalidMedia = true;

        var outcome = await rig.Download.ExecuteAsync(rig.FollowUpJob("download_video"), default);

        Assert.Equal("blocked", outcome.Status);
        Assert.StartsWith("ASSET_", outcome.ErrorCode);
        Assert.Null((await rig.RequestAsync()).OutputAssetId);
        await rig.AssertReservationAsync("reserved");
        Assert.Equal(1, rig.Network.PostCount);
    }

    [Fact]
    public async Task ExpiredPriceBlocksThePaidSubmit()
    {
        await using var rig = await Rig.CreateAsync(_fixture, _root, priceValid: false);

        await Assert.ThrowsAsync<PriceStaleException>(() => rig.Submit.ExecuteAsync(rig.SubmitJob(), default));

        Assert.Equal(0, rig.Network.PostCount);
        Assert.Equal(0, await rig.Db.ProviderRequests.CountAsync(x => x.ClientOperationId == rig.OperationId));
        Assert.Equal(0, await rig.Db.BudgetReservations.CountAsync(x => x.OperationId == rig.OperationId));
    }

    [Fact]
    public async Task RestoredSystemNeverReplaysQueuedPaidWork()
    {
        await using var rig = await Rig.CreateAsync(_fixture, _root);
        var flags = new SystemFlagStore(rig.Db);
        await flags.SetAsync(SystemFlagStore.PaidLanesPaused, "true");
        try
        {
            var outcome = await rig.Submit.ExecuteAsync(rig.SubmitJob(), default);

            Assert.Equal("PAID_LANES_PAUSED", outcome.ErrorCode);
            Assert.Equal(0, rig.Network.PostCount);
            Assert.Equal(0, await rig.Db.ProviderRequests.CountAsync(x => x.ClientOperationId == rig.OperationId));
        }
        finally
        {
            await flags.SetAsync(SystemFlagStore.PaidLanesPaused, null);
        }
    }

    private sealed class Rig : IAsyncDisposable
    {
        public AppDbContext Db { get; private init; } = null!;
        public FakeFal Network { get; private init; } = null!;
        public string OperationId { get; } = "failure-" + Guid.NewGuid().ToString("N");
        public Guid CampaignId { get; private init; }
        public Guid ScopeId { get; } = Guid.NewGuid();
        private Guid JobId { get; } = Guid.NewGuid();
        public PriceQuote Quote { get; private init; } = null!;
        public PostgresBudgetGuard Guard { get; private init; } = null!;
        public SubmitVideoHandler Submit { get; private init; } = null!;
        public PollVideoHandler Poll { get; private init; } = null!;
        public DownloadVideoHandler Download { get; private init; } = null!;
        private AssetRef Image { get; set; } = null!;
        private string ConfigHash { get; set; } = string.Empty;

        public static async Task<Rig> CreateAsync(PostgresSchemaFixture fixture, string root, bool priceValid = true)
        {
            var db = fixture.CreateContext();
            var dir = Path.Combine(root, Guid.NewGuid().ToString("N"));
            var harness = new PipelineHarness(db, dir);
            var campaignId = await harness.SeedCampaignAsync(VideoMode.HybridMotion);
            await db.Campaigns.Where(x => x.Id == campaignId)
                .ExecuteUpdateAsync(set => set.SetProperty(x => x.BudgetUsd, 10m));
            var asset = await (
                    from a in db.Assets
                    join l in db.ProductAssets on a.Id equals l.AssetId
                    join c in db.Campaigns on l.ProductId equals c.ProductId
                    where c.Id == campaignId
                    select a)
                .AsNoTracking().FirstAsync();

            var now = DateTimeOffset.UtcNow;
            var configHash = Guid.NewGuid().ToString("N");
            db.PriceSnapshots.Add(new PriceSnapshot
            {
                Id = Guid.NewGuid(), Provider = "fal", Model = Model, ConfigHash = configHash, Unit = "request",
                UnitPriceUsd = 0.10m, SourceUrl = "https://fal.ai/pricing",
                VerifiedAt = now.AddMinutes(priceValid ? -5 : -120),
                ValidUntil = priceValid ? now.AddHours(1) : now.AddMinutes(-60)
            });
            await db.SaveChangesAsync();

            var approval = Path.Combine(dir, "approval.json");
            await File.WriteAllTextAsync(approval, JsonSerializer.Serialize(new
            {
                approvalId = "failure-" + Guid.NewGuid().ToString("N"), provider = "fal", model = Model,
                maxRequestCount = 100, maxTotalUsd = 100m, expiresAt = now.AddHours(1),
                allowFallbackProvider = false, approvedBy = "owner-test"
            }, JsonDefaults.Options));
            var budget = new BudgetOptions
            {
                AllowPaidApis = true, LiveApprovalFile = approval, DailyBudgetUsd = 10m, MonthlyBudgetUsd = 10m,
                MaxVideoCostUsd = 10m, MaxSingleRequestUsd = 10m, ReservationMultiplier = 1.2m
            };
            var guard = new PostgresBudgetGuard(db, budget, new LiveApprovalGuard(budget, TimeProvider.System));
            var prices = new PriceSnapshotService(db, budget);
            var store = new FileSystemAssetStore(db, new StorageOptions { DataRoot = Path.Combine(dir, "data") }, TimeProvider.System);
            var fal = new FalOptions
            {
                ApiKey = "test-key", ModelId = Model, Delay = (_, _) => Task.CompletedTask, Jitter = () => 0d,
                SubmitTimeout = TimeSpan.FromSeconds(5)
            };
            var network = new FakeFal();
            var provider = new FalVideoProvider(new HttpClient(network), fal, new FalRequestBuilder(store, fal));
            var downloader = new SafeMediaDownloader(
                new HttpClient(network), new SafeMediaDownloaderOptions(), new MockHostResolver());

            PriceQuote? quote = null;
            if (priceValid)
            {
                quote = await prices.GetQuoteAsync("fal", Model, configHash, default);
            }

            var rig = new Rig
            {
                Db = db, Network = network, CampaignId = campaignId, Quote = quote!, Guard = guard,
                Image = new AssetRef(asset.Id, asset.Sha256, asset.RelativePath, asset.Kind, asset.Mime, asset.Bytes, null),
                ConfigHash = configHash,
                Submit = new SubmitVideoHandler(db, provider, guard, prices, fal, TimeProvider.System, budget),
                Poll = new PollVideoHandler(db, provider, TimeProvider.System),
                Download = new DownloadVideoHandler(db, provider, downloader, store, guard, TimeProvider.System)
            };
            db.Jobs.Add(new Job
            {
                Id = rig.JobId, JobType = "submit_video", PayloadJson = "{}", DedupeKey = "failure-job-" + rig.JobId.ToString("N"),
                Lane = JobLane.CloudSubmit, Status = "running", RunAfter = now, CreatedAt = now, UpdatedAt = now
            });
            await db.SaveChangesAsync();
            db.ChangeTracker.Clear();
            return rig;
        }

        public LeasedJob SubmitJob()
            => Lease("submit_video", JsonSerializer.Serialize(
                new
                {
                    request = new VideoGenerationRequest(
                        OperationId, Guid.NewGuid(), "demo", Image, "Subtle camera movement.", "720p", "9:16", Guid.NewGuid()),
                    campaignId = CampaignId, videoScopeId = ScopeId, configHash = ConfigHash
                },
                JsonDefaults.Options));

        public LeasedJob FollowUpJob(string type)
            => Lease(type, JsonSerializer.Serialize(new { request = new { operationId = OperationId } }, JsonDefaults.Options));

        public async Task<ProviderRequest> RequestAsync()
        {
            Db.ChangeTracker.Clear();
            return await Db.ProviderRequests.AsNoTracking().SingleAsync(x => x.ClientOperationId == OperationId);
        }

        public async Task AssertReservationAsync(string status)
        {
            Db.ChangeTracker.Clear();
            var reservation = await Db.BudgetReservations.AsNoTracking().SingleAsync(x => x.OperationId == OperationId);
            Assert.Equal(status, reservation.Status);
        }

        public async Task<Guid> AddRunningJobAsync(JobLane lane, bool withProviderRequest)
        {
            var id = Guid.NewGuid();
            var now = DateTimeOffset.UtcNow;
            Db.Jobs.Add(new Job
            {
                Id = id, JobType = lane == JobLane.CloudSubmit ? "submit_video" : "render_video", PayloadJson = "{}",
                DedupeKey = "lease-" + id.ToString("N"), Lane = lane, Status = "running", LeaseOwner = "dead-worker",
                LeaseUntil = now.AddMinutes(-10), Fence = 1, Attempts = 1, RunAfter = now, CreatedAt = now, UpdatedAt = now
            });
            await Db.SaveChangesAsync();
            Db.JobAttempts.Add(new JobAttempt { Id = Guid.NewGuid(), JobId = id, AttemptNo = 1, Fence = 1, StartedAt = now.AddMinutes(-11) });
            if (withProviderRequest)
            {
                Db.ProviderRequests.Add(new ProviderRequest
                {
                    Id = Guid.NewGuid(), JobId = id, Provider = "fal", Model = Model,
                    ClientOperationId = "lease-" + id.ToString("N"), RequestHash = new string('d', 64),
                    Status = "submitting", CreatedAt = now
                });
            }

            await Db.SaveChangesAsync();
            Db.ChangeTracker.Clear();
            return id;
        }

        public ValueTask DisposeAsync() => Db.DisposeAsync();

        private LeasedJob Lease(string type, string payload)
            => new(JobId, type, payload, 1, "w", 1, DateTimeOffset.UtcNow.AddMinutes(5));
    }

    private sealed class FakeFal : HttpMessageHandler
    {
        private int _posts;
        private int _gets;
        private int _mediaGets;
        public int PostCount => _posts;
        public int GetCount => _gets;
        public int MediaGetCount => _mediaGets;
        public bool DropAfterAccept { get; set; }
        public bool InvalidMedia { get; set; }
        public int StatusFailures { get; set; }
        public string StatusFailureKind { get; set; } = "429";

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var uri = request.RequestUri!;
            if (uri.Host != "queue.fal.run")
            {
                Interlocked.Increment(ref _mediaGets);
                var body = InvalidMedia
                    ? Encoding.UTF8.GetBytes("<html>not a video</html>")
                    : new byte[] { 0, 0, 0, 0x18, (byte)'f', (byte)'t', (byte)'y', (byte)'p', (byte)'m', (byte)'p', (byte)'4', (byte)'2', 0, 0, 0, 0, (byte)'m', (byte)'p', (byte)'4', (byte)'2', (byte)'i', (byte)'s', (byte)'o', (byte)'m' };
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(body) });
            }

            if (request.Method == HttpMethod.Post)
            {
                Interlocked.Increment(ref _posts);
                if (DropAfterAccept)
                {
                    throw new HttpRequestException("connection reset after the request was accepted");
                }

                var id = Guid.NewGuid().ToString("D");
                return Task.FromResult(Json(new
                {
                    request_id = id,
                    status_url = $"https://queue.fal.run/{Model}/requests/{id}/status",
                    response_url = $"https://queue.fal.run/{Model}/requests/{id}",
                    cancel_url = $"https://queue.fal.run/{Model}/requests/{id}/cancel"
                }));
            }

            Interlocked.Increment(ref _gets);
            if (StatusFailures > 0 && uri.AbsolutePath.EndsWith("/status"))
            {
                StatusFailures--;
                if (StatusFailureKind == "offline")
                {
                    throw new HttpRequestException("network is offline");
                }

                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.TooManyRequests));
            }

            return Task.FromResult(uri.AbsolutePath.EndsWith("/status")
                ? Json(new { status = "COMPLETED" })
                : Json(new { video = new { url = "https://v3.fal.media/files/out.mp4" }, seed = 1 }));
        }

        private static HttpResponseMessage Json(object value)
            => new(HttpStatusCode.OK)
            {
                Content = new StringContent(JsonSerializer.Serialize(value), Encoding.UTF8, "application/json")
            };
    }
}
