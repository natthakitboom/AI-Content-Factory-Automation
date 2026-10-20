using System.Security.Cryptography;
using System.Text.Json;
using ContentFactory.Contracts;
using ContentFactory.Core.Abstractions;
using ContentFactory.Core.Campaigns;
using ContentFactory.Core.Exceptions;
using ContentFactory.Infrastructure.Media;
using ContentFactory.Infrastructure.Persistence;
using ContentFactory.Infrastructure.Persistence.Entities;
using ContentFactory.Infrastructure.Providers.Mock;
using ContentFactory.Infrastructure.Providers.Voice;
using ContentFactory.IntegrationTests.Infrastructure;
using ContentFactory.Worker.Handlers;
using Microsoft.EntityFrameworkCore;

namespace ContentFactory.IntegrationTests;

[CollectionDefinition(Name)]
public sealed class ReviewReadyCollection : ICollectionFixture<PostgresSchemaFixture>
{
    public const string Name = "postgres-review-ready";
}

/// <summary>
/// T12 review-ready integration tests. Real PostgreSQL proves the status transition and
/// retained qa_json; the media boundary is injected so this test does not claim native
/// FFprobe execution.
/// </summary>
[Collection(ReviewReadyCollection.Name)]
public sealed class ReviewReadyTests
{
    private const string Locale = "th-TH";
    private readonly PostgresSchemaFixture _fixture;

    public ReviewReadyTests(PostgresSchemaFixture fixture)
    {
        _fixture = fixture;
    }

    [Fact]
    public async Task TemplateOnlyRunReachesAwaitingReviewWithMockPlannerAndUploadedNarration()
    {
        await using var db = _fixture.CreateContext();
        var pipeline = await BuildPipelineAsync();
        var version = await SeedAsync(db, pipeline);
        var qa = new FakeQa(new ValidationReport(true, [], []));
        var handler = new TechnicalQaHandler(db, qa, TimeProvider.System);

        var outcome = await handler.ExecuteAsync(
            Job(version.Id, pipeline.Result),
            CancellationToken.None);

        Assert.Equal("succeeded", outcome.Status);
        Assert.Equal(1, qa.Calls);

        await using var fresh = _fixture.CreateContext();
        var saved = await fresh.VideoVersions.SingleAsync(x => x.Id == version.Id);
        Assert.Equal("awaiting_review", saved.Status);
        Assert.Equal(pipeline.Result.Video.Id, saved.OutputAssetId);
        using var qaJson = JsonDocument.Parse(saved.QaJson!);
        Assert.True(qaJson.RootElement.GetProperty("passed").GetBoolean());
    }

    [Fact]
    public async Task FailedQaKeepsOutputAndMarksQaFailed()
    {
        await using var db = _fixture.CreateContext();
        var pipeline = await BuildPipelineAsync();
        var version = await SeedAsync(db, pipeline);
        var failure = new ValidationReport(
            false,
            [new ValidationIssue("QA_AUDIO_MISSING", "audio", "Audio stream is required.")],
            []);
        var handler = new TechnicalQaHandler(
            db,
            new FakeQa(failure),
            TimeProvider.System);

        var outcome = await handler.ExecuteAsync(
            Job(version.Id, pipeline.Result),
            CancellationToken.None);

        Assert.Equal("failed", outcome.Status);
        Assert.Equal("QA_FAILED", outcome.ErrorCode);

        await using var fresh = _fixture.CreateContext();
        var saved = await fresh.VideoVersions.SingleAsync(x => x.Id == version.Id);
        Assert.Equal("qa_failed", saved.Status);
        Assert.Equal(pipeline.Result.Video.Id, saved.OutputAssetId);
        Assert.Contains("QA_AUDIO_MISSING", saved.QaJson, StringComparison.Ordinal);
        Assert.True(await fresh.Assets.AnyAsync(x => x.Id == pipeline.Result.Video.Id));
    }

    [Fact]
    public async Task ManifestHashMismatchBlocksBeforeQa()
    {
        await using var db = _fixture.CreateContext();
        var pipeline = await BuildPipelineAsync();
        var version = await SeedAsync(
            db,
            pipeline,
            manifestShaOverride: new string('b', 64));
        var qa = new FakeQa(new ValidationReport(true, [], []));
        var handler = new TechnicalQaHandler(db, qa, TimeProvider.System);

        var outcome = await handler.ExecuteAsync(
            Job(version.Id, pipeline.Result),
            CancellationToken.None);

        Assert.Equal("blocked", outcome.Status);
        Assert.Equal("QA_MANIFEST_MISMATCH", outcome.ErrorCode);
        Assert.Equal(0, qa.Calls);
        Assert.True(await db.Assets.AnyAsync(x => x.Id == pipeline.Result.Video.Id));
    }

    [Fact]
    public async Task QaToolFailureMarksVideoBlocked()
    {
        await using var db = _fixture.CreateContext();
        var pipeline = await BuildPipelineAsync();
        var version = await SeedAsync(db, pipeline);
        var handler = new TechnicalQaHandler(
            db,
            new ThrowingQa(),
            TimeProvider.System);

        var outcome = await handler.ExecuteAsync(
            Job(version.Id, pipeline.Result),
            CancellationToken.None);

        Assert.Equal("blocked", outcome.Status);
        Assert.Equal("QA_TOOL_UNAVAILABLE", outcome.ErrorCode);

        await using var fresh = _fixture.CreateContext();
        var saved = await fresh.VideoVersions.SingleAsync(x => x.Id == version.Id);
        Assert.Equal("blocked", saved.Status);
        Assert.Contains("QA_TOOL_UNAVAILABLE", saved.QaJson, StringComparison.Ordinal);
        Assert.True(await fresh.Assets.AnyAsync(x => x.Id == pipeline.Result.Video.Id));
    }

    private static LeasedJob Job(Guid videoVersionId, RenderResult result)
    {
        var payload = JsonSerializer.Serialize(
            new { videoVersionId, renderResult = result },
            JsonDefaults.Options);
        return new LeasedJob(
            Guid.NewGuid(),
            "technical_qa",
            payload,
            1,
            "test-worker",
            1,
            DateTimeOffset.UtcNow.AddMinutes(5));
    }

    private static async Task<Pipeline> BuildPipelineAsync()
    {
        var revisionId = Guid.NewGuid();
        var productId = Guid.NewGuid();
        var imageHash = RandomHash();
        var image = new AssetRef(
            Guid.NewGuid(),
            imageHash,
            "assets/sha256/11/" + imageHash,
            AssetKind.Image,
            "image/jpeg",
            1_000,
            null);
        var snapshot = new ProductSnapshot(
            productId,
            1,
            "Product fixture",
            "TH",
            Locale,
            [
                new ApprovedClaim(
                    Guid.NewGuid(),
                    "ช่วยให้ผิวชุ่มชื้น",
                    "owner-approved-source")
            ],
            [image],
            DateTimeOffset.UtcNow);
        var request = new ScriptRequest(
            revisionId,
            snapshot,
            Locale,
            VideoMode.TemplateOnly,
            25_000,
            "mock.v1");
        var script = await new MockScriptPlanner().GenerateAsync(
            request,
            CancellationToken.None);
        var validation = new ScriptValidator().Validate(script, snapshot);
        Assert.True(validation.Passed, Describe(validation));

        var uploaded = script.Scenes.Select((scene, index) =>
        {
            var hash = new string((char)('a' + index), 64);
            var audio = new AssetRef(
                Guid.NewGuid(),
                hash,
                $"assets/sha256/{hash[..2]}/{hash}",
                AssetKind.Audio,
                "audio/wav",
                1_000,
                5_000);
            return new UploadedNarrationAsset(
                scene.SceneId,
                Locale,
                audio,
                TextSha256: VoiceOperationId.HashText(scene.Narration));
        }).ToArray();
        var voiceProvider = UploadedVoiceProvider.ForScenes(revisionId, uploaded);
        var voices = new List<VoiceSegment>();
        foreach (var scene in script.Scenes)
        {
            var operation = VoiceOperationId.ForScene(revisionId, scene.SceneId);
            var audio = await voiceProvider.SynthesizeAsync(
                new VoiceRequest(operation, Locale, "uploaded", scene.Narration, "uploaded-v1"),
                CancellationToken.None);
            voices.Add(new VoiceSegment(scene.SceneId, audio, audio.DurationMs ?? 0));
        }

        var visuals = script.Scenes.ToDictionary(
            scene => scene.SceneId,
            _ => image,
            StringComparer.Ordinal);
        var fontFingerprint = new string('f', 64);
        var manifest = new TimelineCompiler(
            new TimelineOptions { FontFingerprint = fontFingerprint })
            .Compile(
                Guid.NewGuid(),
                script,
                visuals,
                voices,
                ProductBasicTemplate.Version);

        var videoHash = RandomHash();
        var thumbnailHash = RandomHash();
        var subtitleHash = RandomHash();
        var result = new RenderResult(
            new AssetRef(
                Guid.NewGuid(),
                videoHash,
                $"assets/sha256/{videoHash[..2]}/{videoHash}",
                AssetKind.Video,
                "video/mp4",
                10_000,
                manifest.Segments.Sum(segment => segment.DurationMs)),
            new AssetRef(
                Guid.NewGuid(),
                thumbnailHash,
                $"assets/sha256/{thumbnailHash[..2]}/{thumbnailHash}",
                AssetKind.Thumbnail,
                "image/jpeg",
                2_000,
                null),
            new AssetRef(
                Guid.NewGuid(),
                subtitleHash,
                $"assets/sha256/{subtitleHash[..2]}/{subtitleHash}",
                AssetKind.Subtitle,
                "application/x-subrip",
                500,
                null),
            ManifestHasher.Compute(manifest),
            manifest.Segments.Sum(segment => segment.DurationMs));

        return new Pipeline(productId, revisionId, snapshot, script, manifest, result);
    }

    private static async Task<VideoVersion> SeedAsync(
        AppDbContext db,
        Pipeline pipeline,
        string? manifestShaOverride = null)
    {
        var now = DateTimeOffset.UtcNow;
        var channel = new Channel
        {
            Id = Guid.NewGuid(),
            Platform = "tiktok",
            DisplayName = "Fixture channel",
            Market = "TH",
            Locale = Locale,
            Timezone = "Asia/Bangkok",
            Status = "active",
            CapabilitiesJson = "{}"
        };
        var product = new Product
        {
            Id = pipeline.ProductId,
            Title = "Product fixture",
            Market = "TH",
            Locale = Locale,
            Version = 1,
            CreatedAt = now,
            UpdatedAt = now
        };
        var campaign = new Campaign
        {
            Id = Guid.NewGuid(),
            ProductId = product.Id,
            ChannelId = channel.Id,
            Mode = VideoMode.TemplateOnly,
            Locale = Locale,
            TargetDurationMs = 25_000,
            BudgetUsd = 0m,
            Status = "active",
            Version = 1,
            CreatedAt = now,
            UpdatedAt = now
        };
        var revision = new ContentRevision
        {
            Id = pipeline.RevisionId,
            CampaignId = campaign.Id,
            Revision = 1,
            ProductSnapshotJson = JsonSerializer.Serialize(pipeline.Snapshot, JsonDefaults.Options),
            ScriptJson = JsonSerializer.Serialize(pipeline.Script, JsonDefaults.Options),
            ScriptHash = Sha256(JsonSerializer.Serialize(pipeline.Script, JsonDefaults.Options)),
            PromptVersion = "mock.v1",
            Status = "validated",
            Version = 1,
            CreatedAt = now
        };
        var assets = new[]
        {
            Entity(pipeline.Result.Video, now),
            Entity(pipeline.Result.Thumbnail, now),
            Entity(pipeline.Result.Subtitles, now)
        };
        var version = new VideoVersion
        {
            Id = Guid.NewGuid(),
            ContentRevisionId = revision.Id,
            VersionNo = 1,
            ManifestJson = JsonSerializer.Serialize(pipeline.Manifest, JsonDefaults.Options),
            ManifestSha256 = manifestShaOverride ?? ManifestHasher.Compute(pipeline.Manifest),
            OutputAssetId = pipeline.Result.Video.Id,
            Status = "building",
            CreatedAt = now
        };

        db.Channels.Add(channel);
        db.Products.Add(product);
        db.Campaigns.Add(campaign);
        db.ContentRevisions.Add(revision);
        db.Assets.AddRange(assets);
        db.VideoVersions.Add(version);
        await db.SaveChangesAsync();
        return version;
    }

    private static Asset Entity(AssetRef asset, DateTimeOffset now)
        => new()
        {
            Id = asset.Id,
            Sha256 = asset.Sha256,
            RelativePath = asset.RelativePath,
            Kind = asset.Kind,
            Mime = asset.MimeType,
            Bytes = asset.Bytes,
            DurationMs = asset.DurationMs,
            RightsStatus = "owner_attested",
            SourceReference = "fixture",
            CloudUseAllowed = false,
            CreatedAt = now
        };

    private static string Sha256(string value)
        => Convert.ToHexString(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(value)))
            .ToLowerInvariant();

    private static string RandomHash()
        => Convert.ToHexString(RandomNumberGenerator.GetBytes(32)).ToLowerInvariant();

    private static string Describe(ValidationReport report)
        => string.Join("; ", report.Errors.Select(issue => $"{issue.Code}: {issue.Message}"));

    private sealed record Pipeline(
        Guid ProductId,
        Guid RevisionId,
        ProductSnapshot Snapshot,
        ScriptDocument Script,
        RenderManifest Manifest,
        RenderResult Result);

    private sealed class FakeQa(ValidationReport report) : ITechnicalQa
    {
        public int Calls { get; private set; }

        public Task<ValidationReport> CheckAsync(RenderResult result, CancellationToken ct)
        {
            Calls++;
            return Task.FromResult(report);
        }
    }

    private sealed class ThrowingQa : ITechnicalQa
    {
        public Task<ValidationReport> CheckAsync(RenderResult result, CancellationToken ct)
            => throw new ValidationException(
                "QA_TOOL_UNAVAILABLE",
                "FFprobe is not available.");
    }
}
