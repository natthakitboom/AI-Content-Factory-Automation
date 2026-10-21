using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using ContentFactory.Contracts;
using ContentFactory.Core.Abstractions;
using ContentFactory.Core.Exceptions;
using ContentFactory.Infrastructure.Billing;
using ContentFactory.Infrastructure.Persistence;
using ContentFactory.Infrastructure.Persistence.Entities;
using ContentFactory.Infrastructure.Providers.Fal;
using ContentFactory.IntegrationTests.Infrastructure;
using ContentFactory.Worker.Handlers;
using Microsoft.EntityFrameworkCore;
using SkiaSharp;

namespace ContentFactory.IntegrationTests;

[CollectionDefinition(Name)]
public sealed class FalSubmitCollection : ICollectionFixture<PostgresSchemaFixture>
{
    public const string Name = "postgres-fal-submit";
}

/// <summary>
/// T13 fal submit adapter tests. All HTTP traffic goes to a fake fal queue server. No
/// real FAL_KEY or paid call is made; ALLOW_PAID_APIS is enabled only for the in-memory
/// budget reservation helper and a local test approval file.
/// </summary>
[Collection(FalSubmitCollection.Name)]
public sealed class FalSubmitTests : IAsyncLifetime
{
    private const string Provider = "fal";
    private const string Model = "fal-ai/wan/v2.2-a14b/image-to-video/turbo";

    private readonly PostgresSchemaFixture _fixture;
    private readonly string _directory = Path.Combine(
        Path.GetTempPath(),
        "contentfactory-fal-submit-tests",
        Guid.NewGuid().ToString("N"));
    private readonly InMemoryAssetStore _assets = new();

    public FalSubmitTests(PostgresSchemaFixture fixture)
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
    public async Task OnlySchemaFieldsAreSent()
    {
        await using var db = _fixture.CreateContext();
        var (campaignId, videoScopeId, sourceImage, configHash) = await SeedAsync(db);
        var server = new FakeFalServer();
        using var http = new HttpClient(server, disposeHandler: false);
        var handler = CreateHandler(db, server, http);

        var outcome = await handler.ExecuteAsync(
            await JobAsync(db, sourceImage, campaignId, videoScopeId, configHash),
            CancellationToken.None);

        Assert.Equal("succeeded", outcome.Status);
        Assert.Single(server.Posts);
        var body = server.Posts[0].Body;
        using var document = JsonDocument.Parse(body);
        var propertyNames = document.RootElement.EnumerateObject()
            .Select(property => property.Name)
            .OrderBy(name => name, StringComparer.Ordinal)
            .ToList();
        Assert.Equal(
            new[]
            {
                "acceleration",
                "aspect_ratio",
                "enable_output_safety_checker",
                "enable_prompt_expansion",
                "enable_safety_checker",
                "image_url",
                "prompt",
                "resolution",
                "video_quality",
                "video_write_mode"
            },
            propertyNames);
    }

    [Fact]
    public async Task DurationParameterIsNeverInvented()
    {
        await using var db = _fixture.CreateContext();
        var (campaignId, videoScopeId, sourceImage, configHash) = await SeedAsync(db);
        var server = new FakeFalServer();
        using var http = new HttpClient(server, disposeHandler: false);
        var handler = CreateHandler(db, server, http);

        await handler.ExecuteAsync(
            await JobAsync(db, sourceImage, campaignId, videoScopeId, configHash),
            CancellationToken.None);

        using var document = JsonDocument.Parse(server.Posts[0].Body);
        var names = document.RootElement.EnumerateObject()
            .Select(property => property.Name)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        Assert.DoesNotContain("duration", names);
    }

    [Fact]
    public async Task SmallImageUsesDataUri()
    {
        await using var db = _fixture.CreateContext();
        var (campaignId, videoScopeId, sourceImage, configHash) = await SeedAsync(db);
        var server = new FakeFalServer();
        using var http = new HttpClient(server, disposeHandler: false);
        var handler = CreateHandler(db, server, http);

        await handler.ExecuteAsync(
            await JobAsync(db, sourceImage, campaignId, videoScopeId, configHash),
            CancellationToken.None);

        using var document = JsonDocument.Parse(server.Posts[0].Body);
        var imageUrl = document.RootElement.GetProperty("image_url").GetString();
        Assert.NotNull(imageUrl);
        Assert.StartsWith("data:image/jpeg;base64,", imageUrl, StringComparison.Ordinal);
    }

    [Fact]
    public async Task CloudConsentRequired()
    {
        await using var db = _fixture.CreateContext();
        var (campaignId, videoScopeId, sourceImage, configHash) = await SeedAsync(
            db,
            cloudUseAllowed: false);
        var server = new FakeFalServer();
        using var http = new HttpClient(server, disposeHandler: false);
        var handler = CreateHandler(db, server, http);

        var outcome = await handler.ExecuteAsync(
            await JobAsync(db, sourceImage, campaignId, videoScopeId, configHash),
            CancellationToken.None);

        Assert.Equal("blocked", outcome.Status);
        Assert.Equal("CLOUD_USE_NOT_ALLOWED", outcome.ErrorCode);
        Assert.Empty(server.Posts);
    }

    [Fact]
    public async Task TimeoutAfterAcceptanceIsUnknown()
    {
        await using var db = _fixture.CreateContext();
        var (campaignId, videoScopeId, sourceImage, configHash) = await SeedAsync(db);
        var server = new FakeFalServer(dropAfterAccept: true);
        using var http = new HttpClient(server, disposeHandler: false);
        var handler = CreateHandler(db, server, http);

        var outcome = await handler.ExecuteAsync(
            await JobAsync(db, sourceImage, campaignId, videoScopeId, configHash),
            CancellationToken.None);

        Assert.Equal(1, server.AcceptedPostCount);
        Assert.Equal("failed", outcome.Status);
        Assert.Equal("SUBMISSION_UNKNOWN", outcome.ErrorCode);

        var request = await db.ProviderRequests.SingleAsync(
            x => x.ClientOperationId == sourceImage.Id.ToString("N"));
        Assert.Equal("submission_unknown", request.Status);

        var reservation = await db.BudgetReservations.SingleAsync(
            x => x.OperationId == sourceImage.Id.ToString("N"));
        Assert.Equal("reserved", reservation.Status);
    }

    [Fact]
    public async Task ProviderKeyNeverReachesBrowser()
    {
        await using var db = _fixture.CreateContext();
        var (campaignId, videoScopeId, sourceImage, configHash) = await SeedAsync(db);
        var server = new FakeFalServer();
        using var http = new HttpClient(server, disposeHandler: false);
        const string apiKey = "fal-key-that-must-not-leak";
        var handler = CreateHandler(db, server, http, apiKey: apiKey);

        await handler.ExecuteAsync(
            await JobAsync(db, sourceImage, campaignId, videoScopeId, configHash),
            CancellationToken.None);

        Assert.Equal($"Key {apiKey}", server.Posts[0].Authorization?.ToString());
        Assert.DoesNotContain(apiKey, server.Posts[0].Body);

        var persisted = await db.ProviderRequests
            .AsNoTracking()
            .Where(x => x.ClientOperationId == sourceImage.Id.ToString("N"))
            .Select(x => new { x.Provider, x.Model, x.StatusUrl, x.ResponseUrl })
            .SingleAsync();
        Assert.DoesNotContain(apiKey, persisted.Provider);
        Assert.DoesNotContain(apiKey, persisted.Model);
        Assert.DoesNotContain(apiKey, persisted.StatusUrl ?? string.Empty);
        Assert.DoesNotContain(apiKey, persisted.ResponseUrl ?? string.Empty);
    }

    [Fact]
    public async Task ReplayAfterUnknownDoesNotResubmit()
    {
        await using var db = _fixture.CreateContext();
        var (campaignId, videoScopeId, sourceImage, configHash) = await SeedAsync(db);
        var server = new FakeFalServer(dropAfterAccept: true);
        using var http = new HttpClient(server, disposeHandler: false);
        var handler = CreateHandler(db, server, http);
        var job = await JobAsync(db, sourceImage, campaignId, videoScopeId, configHash);

        var first = await handler.ExecuteAsync(job, CancellationToken.None);
        Assert.Equal("failed", first.Status);
        Assert.Equal(1, server.AcceptedPostCount);

        var second = await handler.ExecuteAsync(job, CancellationToken.None);
        Assert.Equal("failed", second.Status);
        Assert.Equal("SUBMISSION_UNKNOWN", second.ErrorCode);
        Assert.Equal(1, server.AcceptedPostCount);
    }

    private static async Task<LeasedJob> JobAsync(
        AppDbContext db,
        AssetRef sourceImage,
        Guid campaignId,
        Guid videoScopeId,
        string configHash)
    {
        var request = new VideoGenerationRequest(
            sourceImage.Id.ToString("N"),
            Guid.NewGuid(),
            "scene-1",
            sourceImage,
            "Subtle camera movement. Preserve the object geometry and labels.",
            "720p",
            "9:16",
            Guid.NewGuid());
        var payload = JsonSerializer.Serialize(
            new { request, campaignId, videoScopeId, configHash },
            JsonDefaults.Options);
        var jobId = Guid.NewGuid();
        var now = DateTimeOffset.UtcNow;
        db.Jobs.Add(new Job
        {
            Id = jobId,
            JobType = "submit_video",
            PayloadJson = payload,
            PayloadVersion = 1,
            DedupeKey = request.OperationId,
            Lane = JobLane.CloudSubmit,
            Status = "running",
            Priority = 100,
            RunAfter = now,
            Attempts = 1,
            MaxAttempts = 5,
            LeaseOwner = "test-worker",
            LeaseUntil = now.AddMinutes(5),
            Fence = 1,
            CreatedAt = now,
            UpdatedAt = now
        });
        await db.SaveChangesAsync();
        return new LeasedJob(
            jobId,
            "submit_video",
            payload,
            1,
            "test-worker",
            1,
            now.AddMinutes(5));
    }

    private async Task<SeedResult> SeedAsync(
        AppDbContext db,
        bool cloudUseAllowed = true)
    {
        var now = DateTimeOffset.UtcNow;
        var productId = Guid.NewGuid();
        var channelId = Guid.NewGuid();
        var campaignId = Guid.NewGuid();
        var videoScopeId = Guid.NewGuid();

        db.Products.Add(new Product
        {
            Id = productId,
            Title = "Fal fixture product",
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
            Mode = VideoMode.TemplateOnly,
            Locale = "th-TH",
            TargetDurationMs = 25_000,
            BudgetUsd = 10.00m,
            Status = "active",
            Version = 1,
            CreatedAt = now,
            UpdatedAt = now
        });

        var imageBytes = CreateJpeg(64, 64);
        var imageHash = Hash(imageBytes);
        var imageId = Guid.NewGuid();
        var image = new AssetRef(
            imageId,
            imageHash,
            $"assets/sha256/{imageHash[..2]}/{imageHash}",
            AssetKind.Image,
            "image/jpeg",
            imageBytes.Length,
            null);
        _assets.Add(imageId, imageBytes);
        db.Assets.Add(new Asset
        {
            Id = imageId,
            Sha256 = imageHash,
            RelativePath = image.RelativePath,
            Kind = AssetKind.Image,
            Mime = "image/jpeg",
            Bytes = imageBytes.Length,
            Width = 64,
            Height = 64,
            RightsStatus = "owner_attested",
            SourceReference = "fixture",
            CloudUseAllowed = cloudUseAllowed,
            CreatedAt = now
        });

        var configHash = Guid.NewGuid().ToString("N");
        var priceId = Guid.NewGuid();
        db.PriceSnapshots.Add(new PriceSnapshot
        {
            Id = priceId,
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
        return new SeedResult(campaignId, videoScopeId, image, configHash);
    }

    private SubmitVideoHandler CreateHandler(
        AppDbContext db,
        FakeFalServer server,
        HttpClient http,
        string? apiKey = null)
    {
        var approvalFile = WriteApproval();
        var budgetOptions = new BudgetOptions
        {
            AllowPaidApis = true,
            LiveApprovalFile = approvalFile,
            DailyBudgetUsd = 10.00m,
            MonthlyBudgetUsd = 10.00m,
            MaxVideoCostUsd = 10.00m,
            MaxSingleRequestUsd = 10.00m,
            ReservationMultiplier = 1.20m
        };
        var guard = new PostgresBudgetGuard(
            db,
            budgetOptions,
            new LiveApprovalGuard(budgetOptions, TimeProvider.System));
        var priceService = new PriceSnapshotService(db, budgetOptions);
        var options = new FalOptions
        {
            ApiKey = apiKey ?? "test-fal-key",
            ModelId = Model,
            BaseUrl = new Uri("https://queue.fal.run/"),
            SubmitTimeout = TimeSpan.FromSeconds(5)
        };
        var builder = new FalRequestBuilder(_assets, options);
        var provider = new FalVideoProvider(http, options, builder);
        return new SubmitVideoHandler(
            db,
            provider,
            guard,
            priceService,
            options,
            TimeProvider.System);
    }

    private string WriteApproval(
        int maxRequestCount = 100,
        decimal maxTotalUsd = 100.00m)
    {
        var path = Path.Combine(_directory, $"{Guid.NewGuid():N}.json");
        var payload = new
        {
            approvalId = $"test-approval-{Guid.NewGuid():N}",
            provider = Provider,
            model = Model,
            maxRequestCount,
            maxTotalUsd,
            expiresAt = DateTimeOffset.UtcNow.AddHours(1),
            allowFallbackProvider = false,
            approvedBy = "owner-test"
        };
        File.WriteAllText(path, JsonSerializer.Serialize(payload, JsonDefaults.Options));
        return path;
    }

    private static byte[] CreateJpeg(int width, int height)
    {
        using var bitmap = new SKBitmap(width, height);
        var random = RandomNumberGenerator.GetBytes(width * height * 4);
        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < width; x++)
            {
                var offset = (y * width + x) * 4;
                var color = new SKColor(
                    random[offset],
                    random[offset + 1],
                    random[offset + 2],
                    random[offset + 3]);
                bitmap.SetPixel(x, y, color);
            }
        }

        using var stream = new MemoryStream();
        bitmap.Encode(stream, SKEncodedImageFormat.Jpeg, 90);
        return stream.ToArray();
    }

    private static string Hash(byte[] bytes)
        => Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();

    private sealed record SeedResult(
        Guid CampaignId,
        Guid VideoScopeId,
        AssetRef SourceImage,
        string ConfigHash);

    private sealed class InMemoryAssetStore : IAssetStore
    {
        private readonly Dictionary<Guid, byte[]> _bytes = [];

        public void Add(Guid id, byte[] bytes)
            => _bytes[id] = bytes;

        public Task<AssetRef> PutAsync(
            Stream input,
            AssetKind kind,
            string mime,
            CancellationToken ct)
        {
            using var memory = new MemoryStream();
            input.CopyTo(memory);
            var bytes = memory.ToArray();
            var id = Guid.NewGuid();
            _bytes[id] = bytes;
            var hash = Hash(bytes);
            return Task.FromResult(new AssetRef(
                id,
                hash,
                $"assets/sha256/{hash[..2]}/{hash}",
                kind,
                mime,
                bytes.Length,
                null));
        }

        public Task<Stream> OpenReadAsync(Guid assetId, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            return Task.FromResult<Stream>(new MemoryStream(_bytes[assetId], writable: false));
        }
    }

    private sealed record PostRecord(
        HttpMethod Method,
        Uri Uri,
        string Body,
        AuthenticationHeaderValue? Authorization);

    private sealed class FakeFalServer(bool dropAfterAccept = false) : HttpMessageHandler
    {
        public List<PostRecord> Posts { get; } = [];
        public int AcceptedPostCount { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            if (request.Method != HttpMethod.Post)
            {
                return new HttpResponseMessage(HttpStatusCode.MethodNotAllowed);
            }

            var body = request.Content is null
                ? string.Empty
                : await request.Content.ReadAsStringAsync(cancellationToken);
            AcceptedPostCount++;
            Posts.Add(new PostRecord(
                request.Method,
                request.RequestUri!,
                body,
                request.Headers.Authorization));

            if (dropAfterAccept)
            {
                throw new HttpRequestException("Connection reset after request was accepted.");
            }

            var requestId = Guid.NewGuid().ToString("D");
            var json = JsonSerializer.Serialize(new
            {
                request_id = requestId,
                status_url = $"https://queue.fal.run/{Model}/requests/{requestId}/status",
                response_url = $"https://queue.fal.run/{Model}/requests/{requestId}",
                cancel_url = $"https://queue.fal.run/{Model}/requests/{requestId}/cancel"
            }, JsonDefaults.Options);
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(json, Encoding.UTF8, "application/json")
            };
        }
    }
}
