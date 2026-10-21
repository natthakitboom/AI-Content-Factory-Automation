using System.Globalization;
using System.Net;
using System.Text.Json;
using ContentFactory.Contracts;
using ContentFactory.Core.Abstractions;
using ContentFactory.Core.Campaigns;
using ContentFactory.Core.Exceptions;
using ContentFactory.Core.Jobs;
using ContentFactory.Infrastructure.Billing;
using ContentFactory.Infrastructure.Jobs;
using ContentFactory.Infrastructure.Media;
using ContentFactory.Infrastructure.Persistence;
using ContentFactory.Infrastructure.Persistence.Entities;
using ContentFactory.Infrastructure.Providers;
using ContentFactory.Infrastructure.Providers.Fal;
using ContentFactory.Infrastructure.Providers.Mock;
using ContentFactory.Infrastructure.Security;
using ContentFactory.Infrastructure.Storage;
using ContentFactory.IntegrationTests.Infrastructure;
using ContentFactory.Worker;
using ContentFactory.Worker.Handlers;
using ContentFactory.Worker.Health;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using SkiaSharp;

namespace ContentFactory.IntegrationTests;

[CollectionDefinition(Name)]
public sealed class EndToEndPipelineCollection : ICollectionFixture<PostgresSchemaFixture>
{
    public const string Name = "postgres-end-to-end";
}

/// <summary>
/// T15 offline pipeline tests. The whole chain (plan, visuals, narration, optional mock
/// cloud motion, timeline, native render, QA) runs against PostgreSQL and real FFmpeg with
/// mock providers behind the same interfaces as live. No external network is reachable.
/// </summary>
[Collection(EndToEndPipelineCollection.Name)]
public sealed class EndToEndPipelineTests : IAsyncLifetime
{
    private readonly PostgresSchemaFixture _fixture;
    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        "contentfactory-e2e-tests",
        Guid.NewGuid().ToString("N"));

    public EndToEndPipelineTests(PostgresSchemaFixture fixture)
    {
        _fixture = fixture;
        Directory.CreateDirectory(_root);
    }

    public Task InitializeAsync() => Task.CompletedTask;

    public Task DisposeAsync()
    {
        try
        {
            if (Directory.Exists(_root))
            {
                Directory.Delete(_root, recursive: true);
            }
        }
        catch (IOException)
        {
            // Best-effort cleanup.
        }

        return Task.CompletedTask;
    }

    [PipelineFact]
    public async Task TemplateOnlyProducesReviewableMp4()
    {
        await using var db = _fixture.CreateContext();
        var harness = new Harness(db, _root);
        var campaignId = await harness.SeedCampaignAsync(VideoMode.TemplateOnly);

        var run = await harness.EnqueueAsync(campaignId);
        await harness.PumpAsync();

        await harness.AssertNoFailedJobsAsync();
        var version = await db.VideoVersions
            .AsNoTracking()
            .SingleAsync(x => x.ContentRevisionId == run.RevisionId);
        Assert.Equal("awaiting_review", version.Status);
        Assert.NotNull(version.OutputAssetId);
        Assert.NotNull(version.ManifestSha256);

        var probed = await harness.ProbeAsync(version.OutputAssetId!.Value);
        Assert.Equal("h264", probed.VideoCodec);
        Assert.Equal(1080, probed.Width);
        Assert.Equal(1920, probed.Height);
        Assert.Equal("aac", probed.AudioCodec);

        Assert.Equal(0, harness.Media.ExternalRequestCount);
        Assert.Equal(0, harness.Video.SubmitCount);
        Assert.Equal(0, await db.ProviderRequests.CountAsync());
        Assert.Equal(0, await db.BudgetReservations.CountAsync());
        Assert.Equal(
            "awaiting_review",
            await db.VideoVersions
                .Where(x => x.ContentRevisionId == run.RevisionId)
                .Select(x => x.Status)
                .SingleAsync());
        Assert.False(await db.Approvals.AnyAsync());
    }

    [PipelineFact]
    public async Task HybridMockUsesMotionAsset()
    {
        await using var db = _fixture.CreateContext();
        var harness = new Harness(db, _root);
        var campaignId = await harness.SeedCampaignAsync(VideoMode.HybridMotion);

        var run = await harness.EnqueueAsync(campaignId, cloudVideoSceneLimit: 1);
        await harness.PumpAsync();

        await harness.AssertNoFailedJobsAsync();
        var request = await db.ProviderRequests.AsNoTracking().SingleAsync();
        Assert.Equal("mock", request.Provider);
        Assert.Equal("succeeded", request.Status);
        Assert.NotNull(request.OutputAssetId);
        var motion = await db.Assets.AsNoTracking().SingleAsync(x => x.Id == request.OutputAssetId);
        Assert.Equal(AssetKind.Video, motion.Kind);

        var version = await db.VideoVersions
            .AsNoTracking()
            .SingleAsync(x => x.ContentRevisionId == run.RevisionId);
        Assert.Equal("awaiting_review", version.Status);
        var manifest = JsonSerializer.Deserialize<RenderManifest>(
            version.ManifestJson!, JsonDefaults.Options)!;
        Assert.Contains(manifest.Segments, segment => segment.Visual.Id == motion.Id);

        Assert.Equal(1, harness.Video.SubmitCount);
        Assert.Equal(0, harness.Video.BillableRequestCount);
        Assert.Equal(0, harness.Media.ExternalRequestCount);
        Assert.Equal(0, await db.BudgetReservations.CountAsync());
        Assert.Equal("h264", (await harness.ProbeAsync(version.OutputAssetId!.Value)).VideoCodec);
    }

    [Fact]
    public async Task MockCannotOpenExternalNetwork()
    {
        var media = new MockMediaHandler(_ => Task.FromResult(new byte[] { 1 }));
        using var http = new HttpClient(media);

        await Assert.ThrowsAsync<HttpRequestException>(() =>
            http.GetAsync("https://example.com/video.mp4"));
        await Assert.ThrowsAsync<HttpRequestException>(() =>
            http.GetAsync("https://queue.fal.run/fal-ai/x"));

        Assert.Equal(2, media.ExternalRequestCount);

        var resolver = new MockHostResolver();
        var addresses = await resolver.ResolveAsync("mock.fal.media", default);
        Assert.All(addresses, address => Assert.True(SafeMediaDownloader.IsPublicAddress(address)));
    }

    [Fact]
    public async Task PaidFlagOffBlocksLiveAdapterEvenWithKeyPresent()
    {
        var environment = new Dictionary<string, string?>
        {
            ["FAL_KEY"] = "key-that-must-not-select-live",
            ["ALLOW_PAID_APIS"] = "false"
        };
        var selected = ProviderModes.Resolve(name => environment.GetValueOrDefault(name));
        Assert.True(selected.IsMock);
        Assert.Equal("mock", selected.Video);
        Assert.Equal("MOCK MODE", selected.Banner);

        // Even when the owner explicitly selects the live adapter, the paid flag blocks it
        // before any HTTP request leaves the process.
        environment["VIDEO_PROVIDER"] = "fal";
        Assert.Equal("fal", ProviderModes.Resolve(name => environment.GetValueOrDefault(name)).Video);

        await using var db = _fixture.CreateContext();
        var network = new CountingHandler();
        var falOptions = new FalOptions { ApiKey = "key-that-must-not-select-live" };
        var provider = new FalVideoProvider(
            new HttpClient(network),
            falOptions,
            new FalRequestBuilder(new FileSystemAssetStore(
                db,
                new StorageOptions { DataRoot = Path.Combine(_root, "paid-off") },
                TimeProvider.System), falOptions));
        var budget = new BudgetOptions { AllowPaidApis = false };
        var handler = new SubmitVideoHandler(
            db,
            provider,
            new PostgresBudgetGuard(db, budget, new LiveApprovalGuard(budget, TimeProvider.System)),
            new PriceSnapshotService(db, budget),
            falOptions,
            TimeProvider.System,
            budget);
        var harness = new Harness(db, Path.Combine(_root, "paid-off-seed"));
        var campaignId = await harness.SeedCampaignAsync(VideoMode.HybridMotion);
        var image = await db.Assets.AsNoTracking().FirstAsync(x => x.Kind == AssetKind.Image);
        var request = new VideoGenerationRequest(
            "paid-off-op",
            Guid.NewGuid(),
            "demo",
            new AssetRef(image.Id, image.Sha256, image.RelativePath, image.Kind, image.Mime, image.Bytes, null),
            "Subtle camera movement.",
            "720p",
            "9:16",
            Guid.NewGuid());

        var outcome = await handler.ExecuteAsync(
            new LeasedJob(
                Guid.NewGuid(),
                "submit_video",
                JsonSerializer.Serialize(
                    new { request, campaignId, videoScopeId = Guid.NewGuid(), configHash = "x" },
                    JsonDefaults.Options),
                1,
                "test",
                1,
                DateTimeOffset.UtcNow.AddMinutes(1)),
            default);

        Assert.Equal("blocked", outcome.Status);
        Assert.Equal("PAID_APIS_DISABLED", outcome.ErrorCode);
        Assert.Equal(0, network.Count);
        Assert.Equal(0, await db.ProviderRequests.CountAsync(x => x.ClientOperationId == "paid-off-op"));
    }

    [Fact]
    public void UnknownProviderNamesAreRejected()
    {
        var error = Assert.Throws<ValidationException>(() =>
            ProviderModes.Resolve(name => name == "VIDEO_PROVIDER" ? "falcon" : null));
        Assert.Equal("PROVIDER_MODE_INVALID", error.Code);
    }

    private sealed class CountingHandler : HttpMessageHandler
    {
        public int Count { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Count++;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.InternalServerError));
        }
    }

    private sealed record Probed(
        string VideoCodec, int Width, int Height, string AudioCodec, double DurationSeconds);

    private sealed class Harness
    {
        private readonly AppDbContext _db;
        private readonly string _root;
        private readonly StorageOptions _storage;
        private readonly SafeProcessRunner _runner = new();
        private readonly PostgresJobStore _store;
        private readonly JobDispatcher _dispatcher;
        private readonly OutboxDispatcher _outbox;
        private readonly FileSystemAssetStore _assets;
        private readonly DateTimeOffset _startedAt = DateTimeOffset.UtcNow;

        public Harness(AppDbContext db, string root)
        {
            _db = db;
            _root = root;
            Directory.CreateDirectory(root);
            _storage = new StorageOptions { DataRoot = Path.Combine(root, "data") };
            _assets = new FileSystemAssetStore(db, _storage, TimeProvider.System);
            _store = new PostgresJobStore(db, TimeProvider.System);
            _outbox = new OutboxDispatcher(db, _store, TimeProvider.System);

            Media = new MockMediaHandler(token => MockClipGenerator.GenerateAsync(_runner, token));
            Video = new MockVideoProvider();

            var fontFingerprint = new string('a', 64);
            var budget = new BudgetOptions { AllowPaidApis = false };
            var guard = new PostgresBudgetGuard(
                db, budget, new LiveApprovalGuard(budget, TimeProvider.System));
            var falOptions = new FalOptions();
            var captions = new CaptionLayout(
                new ConservativeTextMeasurer(), new CaptionLayoutOptions());
            var renderer = new FfmpegVideoRenderer(
                _assets,
                _storage,
                _runner,
                new EncoderProbe(
                    _runner,
                    _storage,
                    new EncoderProbeOptions { FontFingerprint = fontFingerprint }),
                captions,
                new FfmpegRenderOptions());
            var voice = new VoiceAssembler(
                _assets,
                new FfprobeAudioProbe(_assets, _storage, _runner),
                new FfmpegAudioConcatenator(_assets, _storage, _runner),
                new MemoryVoiceAssetCache());

            IJobHandler[] handlers =
            [
                new PlanScriptHandler(db, new MockScriptPlanner(), new ScriptValidator(), TimeProvider.System),
                new PrepareVisualsHandler(db, TimeProvider.System),
                new SynthesizeVoiceHandler(db, voice, new MockVoiceProvider(_assets), new VoiceHandlerOptions("mock", "mock", "mock-v1", "mock-voice-1")),
                new SubmitVideoHandler(db, Video, guard, new PriceSnapshotService(db, budget), falOptions, TimeProvider.System),
                new PollVideoHandler(db, Video, TimeProvider.System),
                new DownloadVideoHandler(
                    db,
                    Video,
                    new SafeMediaDownloader(
                        new HttpClient(Media),
                        new SafeMediaDownloaderOptions(),
                        new MockHostResolver()),
                    _assets,
                    guard,
                    TimeProvider.System),
                new CompileTimelineHandler(
                    db,
                    new TimelineCompiler(new TimelineOptions { FontFingerprint = fontFingerprint })),
                new RenderVideoHandler(db, renderer),
                new TechnicalQaHandler(db, new TechnicalQa(_assets, _storage, _runner), TimeProvider.System),
                new PrepareReviewHandler(db)
            ];
            _dispatcher = new JobDispatcher(
                _store,
                handlers,
                new ResourceGovernor(),
                NullLogger<JobDispatcher>.Instance,
                TimeProvider.System);
        }

        public MockMediaHandler Media { get; }

        public MockVideoProvider Video { get; }

        public async Task<Guid> SeedCampaignAsync(VideoMode mode)
        {
            var now = DateTimeOffset.UtcNow;
            var product = new Product
            {
                Id = Guid.NewGuid(),
                Title = "สินค้าทดสอบ pipeline",
                Market = "TH",
                Locale = "th-TH",
                Version = 1,
                CreatedAt = now,
                UpdatedAt = now
            };
            var channel = new Channel
            {
                Id = Guid.NewGuid(),
                Platform = "tiktok",
                DisplayName = "Fixture channel",
                Market = "TH",
                Locale = "th-TH",
                Timezone = "Asia/Bangkok",
                Status = "active",
                CapabilitiesJson = "{}"
            };
            var campaign = new Campaign
            {
                Id = Guid.NewGuid(),
                ProductId = product.Id,
                ChannelId = channel.Id,
                Mode = mode,
                Locale = "th-TH",
                TargetDurationMs = 25_000,
                BudgetUsd = 0m,
                Status = "active",
                Version = 1,
                CreatedAt = now,
                UpdatedAt = now
            };
            _db.Products.Add(product);
            _db.Channels.Add(channel);
            _db.Campaigns.Add(campaign);
            foreach (var text in new[] { "ช่วยให้ผิวชุ่มชื้น", "เนื้อบางเบาและใช้ง่าย" })
            {
                _db.ProductClaims.Add(new ProductClaim
                {
                    Id = Guid.NewGuid(),
                    ProductId = product.Id,
                    Text = text,
                    SourceReference = "owner-sheet-e2e",
                    Status = "approved",
                    VerifiedAt = now,
                    CreatedAt = now
                });
            }

            await _db.SaveChangesAsync();

            await using var image = new MemoryStream(CreateJpeg());
            await new AssetIngestService(_db, _assets).IngestAsync(
                new AssetIngestRequest(
                    image,
                    "product.jpg",
                    AssetKind.Image,
                    "image/jpeg",
                    "owner_attested",
                    "synthetic fixture generated by the test",
                    CloudUseAllowed: true,
                    ProductId: product.Id),
                default);
            return campaign.Id;
        }

        public Task<RunEnqueued> EnqueueAsync(Guid campaignId, int cloudVideoSceneLimit = 0)
        {
            var campaign = _db.Campaigns.AsNoTracking().Single(x => x.Id == campaignId);
            return new RunEnqueuer(_db, TimeProvider.System).EnqueueAsync(
                new RunEnqueueCommand(
                    campaignId,
                    "e2e-" + Guid.NewGuid().ToString("N"),
                    campaign.Mode,
                    campaign.TargetDurationMs,
                    AutoGenerateVoice: true,
                    CloudVideoSceneLimit: cloudVideoSceneLimit,
                    AllowPaidCloud: cloudVideoSceneLimit > 0),
                default);
        }

        public async Task PumpAsync()
        {
            JobLane[] lanes =
            [
                JobLane.Control, JobLane.HeavyLocal, JobLane.CloudSubmit,
                JobLane.CloudPoll, JobLane.Download
            ];
            for (var round = 0; round < 200; round++)
            {
                var worked = false;
                foreach (var lane in lanes)
                {
                    var result = await _dispatcher.RunOnceAsync("e2e-worker", lane, default);
                    worked |= result.Leased;
                }

                worked |= await _outbox.DispatchOnceAsync(
                    new WorkflowOutboxHandler(), default) > 0;
                if (!worked)
                {
                    return;
                }
            }

            throw new Xunit.Sdk.XunitException("The pipeline did not settle within 200 rounds.");
        }

        public async Task AssertNoFailedJobsAsync()
        {
            _db.ChangeTracker.Clear();
            var bad = await _db.Jobs
                .AsNoTracking()
                .Where(x => x.CreatedAt >= _startedAt && x.Status != "succeeded")
                .Select(x => x.JobType + ":" + x.Status + ":" + x.LastErrorCode)
                .ToListAsync();
            var qa = await _db.VideoVersions.AsNoTracking()
                .Where(x => x.CreatedAt >= _startedAt
                    && (x.Status == "qa_failed" || x.Status == "blocked"))
                .Select(x => x.QaJson)
                .ToListAsync();
            Assert.True(
                bad.Count == 0,
                "Unfinished jobs: " + string.Join(", ", bad) + " QA: " + string.Join(" | ", qa));
        }

        public async Task<Probed> ProbeAsync(Guid assetId)
        {
            var asset = await _db.Assets.AsNoTracking().SingleAsync(x => x.Id == assetId);
            var path = Path.Combine(_storage.DataRoot, asset.RelativePath);
            var result = await _runner.RunAsync(
                "ffprobe",
                [
                    "-v", "error", "-show_entries",
                    "stream=codec_type,codec_name,width,height", "-show_entries",
                    "format=duration", "-of", "json", path
                ],
                TimeSpan.FromSeconds(30),
                default);
            Assert.Equal(0, result.ExitCode);
            using var document = JsonDocument.Parse(result.StandardOutput);
            var streams = document.RootElement.GetProperty("streams").EnumerateArray().ToList();
            var video = streams.First(x => x.GetProperty("codec_type").GetString() == "video");
            var audio = streams.First(x => x.GetProperty("codec_type").GetString() == "audio");
            return new Probed(
                video.GetProperty("codec_name").GetString()!,
                video.GetProperty("width").GetInt32(),
                video.GetProperty("height").GetInt32(),
                audio.GetProperty("codec_name").GetString()!,
                double.Parse(
                    document.RootElement.GetProperty("format").GetProperty("duration").GetString()!,
                    CultureInfo.InvariantCulture));
        }

        private static byte[] CreateJpeg()
        {
            using var bitmap = new SKBitmap(540, 960);
            using var canvas = new SKCanvas(bitmap);
            canvas.Clear(new SKColor(40, 90, 160));
            using var paint = new SKPaint { Color = new SKColor(240, 240, 240), IsAntialias = true };
            canvas.DrawCircle(270, 480, 160, paint);
            using var image = SKImage.FromBitmap(bitmap);
            return image.Encode(SKEncodedImageFormat.Jpeg, 90).ToArray();
        }
    }
}
