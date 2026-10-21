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
        var harness = new PipelineHarness(db, _root);
        var campaignId = await harness.SeedCampaignAsync(VideoMode.TemplateOnly);

        var run = await harness.EnqueueAsync(campaignId);
        await harness.PumpAsync();

        Assert.Null(await harness.UnfinishedWorkAsync());
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
        var harness = new PipelineHarness(db, _root);
        var campaignId = await harness.SeedCampaignAsync(VideoMode.HybridMotion);

        var run = await harness.EnqueueAsync(campaignId, cloudVideoSceneLimit: 1);
        await harness.PumpAsync();

        Assert.Null(await harness.UnfinishedWorkAsync());
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
        var harness = new PipelineHarness(db, Path.Combine(_root, "paid-off-seed"));
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
        Assert.Equal(
            "PROVIDER_MODE_INVALID",
            Assert.Throws<ValidationException>(() =>
                ProviderModes.Resolve(name => name == "VOICE_PROVIDER" ? "azure" : null)).Code);
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
}
