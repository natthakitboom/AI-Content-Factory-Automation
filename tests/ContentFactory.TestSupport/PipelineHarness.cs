using System.Globalization;
using System.Text.Json;
using ContentFactory.Contracts;
using ContentFactory.Core.Abstractions;
using ContentFactory.Core.Campaigns;
using ContentFactory.Core.Jobs;
using ContentFactory.Infrastructure.Billing;
using ContentFactory.Infrastructure.Jobs;
using ContentFactory.Infrastructure.Media;
using ContentFactory.Infrastructure.Persistence;
using ContentFactory.Infrastructure.Persistence.Entities;
using ContentFactory.Infrastructure.Providers.Fal;
using ContentFactory.Infrastructure.Providers.Mock;
using ContentFactory.Infrastructure.Security;
using ContentFactory.Infrastructure.Storage;
using ContentFactory.Worker;
using ContentFactory.Worker.Handlers;
using ContentFactory.Worker.Health;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using SkiaSharp;

namespace ContentFactory.IntegrationTests.Infrastructure;

public sealed record Probed(
    string VideoCodec, int Width, int Height, string AudioCodec, double DurationSeconds);


/// <summary>
/// Runs the whole offline pipeline (mock providers, real FFmpeg, real PostgreSQL) in-process by
/// pumping the job lanes and the stage outbox, exactly as the worker would.
/// </summary>
public sealed class PipelineHarness
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

    public PipelineHarness(AppDbContext db, string root)
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

    public async Task<Guid> SeedCampaignAsync(VideoMode mode, string title = "สินค้าทดสอบ pipeline")
    {
        var now = DateTimeOffset.UtcNow;
        var product = new Product
        {
            Id = Guid.NewGuid(),
            Title = title,
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

        throw new InvalidOperationException("The pipeline did not settle within 200 rounds.");
    }

    /// <summary>Describes unfinished jobs and blocked versions from this run, or null when all succeeded.</summary>
    public async Task<string?> UnfinishedWorkAsync()
    {
        _db.ChangeTracker.Clear();
        var bad = await _db.Jobs
            .AsNoTracking()
            .Where(x => x.CreatedAt >= _startedAt && x.Status != "succeeded")
            .Select(x => x.JobType + ":" + x.Status + ":" + x.LastErrorCode)
            .ToListAsync();
        if (bad.Count == 0)
        {
            return null;
        }

        var qa = await _db.VideoVersions.AsNoTracking()
            .Where(x => x.CreatedAt >= _startedAt
                && (x.Status == "qa_failed" || x.Status == "blocked"))
            .Select(x => x.QaJson)
            .ToListAsync();
        return "Unfinished jobs: " + string.Join(", ", bad) + " QA: " + string.Join(" | ", qa);
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
        if (result.ExitCode != 0)
        {
            throw new InvalidOperationException("ffprobe failed: " + result.StandardError);
        }

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
