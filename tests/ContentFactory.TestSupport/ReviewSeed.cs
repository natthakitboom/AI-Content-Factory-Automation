using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using ContentFactory.Contracts;
using ContentFactory.Core.Campaigns;
using ContentFactory.Infrastructure.Media;
using ContentFactory.Infrastructure.Persistence;
using ContentFactory.Infrastructure.Persistence.Entities;
using ContentFactory.Core.Abstractions;
using ContentFactory.Infrastructure.Providers.Mock;
using Microsoft.EntityFrameworkCore;
using SkiaSharp;

namespace ContentFactory.IntegrationTests.Infrastructure;

public sealed record SeededVersion(
    Guid ProductId,
    Guid CampaignId,
    Guid RevisionId,
    Guid VersionId,
    string ManifestSha256,
    ScriptDocument Script,
    RenderManifest Manifest);

/// <summary>
/// Persists a complete reviewable video version (frozen product facts, validated script,
/// scene assets and a real compiled manifest) without rendering any media, so review,
/// export and cleanup tests can start from a known, honest database state.
/// </summary>
public static class ReviewSeed
{
    public const string FontFingerprint =
        "ffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffff";

    public static async Task<SeededVersion> CreateAsync(
        AppDbContext db,
        string status = "awaiting_review",
        bool qaPassed = true,
        VideoMode mode = VideoMode.TemplateOnly)
    {
        var now = DateTimeOffset.UtcNow;
        var productId = Guid.NewGuid();
        var channelId = Guid.NewGuid();
        var campaignId = Guid.NewGuid();
        var revisionId = Guid.NewGuid();
        var versionId = Guid.NewGuid();

        var image = NewAsset(AssetKind.Image, "image/jpeg", 1_000, null);
        var claimId = Guid.NewGuid();
        var snapshot = new ProductSnapshot(
            productId,
            1,
            "สินค้าสำหรับรีวิว",
            "TH",
            "th-TH",
            [new ApprovedClaim(claimId, "ช่วยให้ผิวชุ่มชื้น", "owner-sheet-review")],
            [image],
            now);
        var request = new ScriptRequest(
            revisionId, snapshot, "th-TH", mode, 25_000, "mock.v1");
        var script = await new MockScriptPlanner().GenerateAsync(request, default);
        var report = new ScriptValidator().Validate(script, snapshot);
        if (!report.Passed)
        {
            throw new InvalidOperationException("The seeded script must validate.");
        }

        var voices = script.Scenes
            .Select(scene =>
            {
                var audio = NewAsset(AssetKind.Audio, "audio/wav", 2_000, 5_200);
                return (scene, audio, segment: new VoiceSegment(scene.SceneId, audio, 5_200));
            })
            .ToList();
        var manifest = new TimelineCompiler(new TimelineOptions { FontFingerprint = FontFingerprint })
            .Compile(
                versionId,
                script,
                script.Scenes.ToDictionary(x => x.SceneId, _ => image, StringComparer.Ordinal),
                voices.Select(x => x.segment).ToList(),
                ProductBasicTemplate.Version);
        var video = NewAsset(AssetKind.Video, "video/mp4", 4_000, manifest.Segments.Sum(x => x.DurationMs));

        var scriptJson = JsonSerializer.Serialize(script, JsonDefaults.Options);
        db.Products.Add(new Product
        {
            Id = productId,
            Title = snapshot.Title,
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
            Mode = mode,
            Locale = "th-TH",
            TargetDurationMs = 25_000,
            BudgetUsd = 0m,
            Status = "active",
            Version = 1,
            CreatedAt = now,
            UpdatedAt = now
        });
        db.Assets.AddRange(
            new[] { ToEntity(image, now), ToEntity(video, now) }
                .Concat(voices.Select(x => ToEntity(x.audio, now))));
        db.ContentRevisions.Add(new ContentRevision
        {
            Id = revisionId,
            CampaignId = campaignId,
            Revision = 1,
            ProductSnapshotJson = JsonSerializer.Serialize(snapshot, JsonDefaults.Options),
            ScriptJson = scriptJson,
            ScriptHash = ManifestHasher.HashCanonicalJson(scriptJson),
            PromptVersion = "mock.v1",
            Status = "validated",
            Version = 1,
            CreatedAt = now
        });
        await db.SaveChangesAsync();

        foreach (var scene in script.Scenes)
        {
            db.SceneAssets.Add(new SceneAsset
            {
                Id = Guid.NewGuid(),
                ContentRevisionId = revisionId,
                SceneId = scene.SceneId,
                Role = "visual",
                AssetId = image.Id
            });
        }

        foreach (var voice in voices)
        {
            db.SceneAssets.Add(new SceneAsset
            {
                Id = Guid.NewGuid(),
                ContentRevisionId = revisionId,
                SceneId = voice.scene.SceneId,
                Role = "voice",
                AssetId = voice.audio.Id
            });
        }

        var hash = ManifestHasher.Compute(manifest);
        db.VideoVersions.Add(new VideoVersion
        {
            Id = versionId,
            ContentRevisionId = revisionId,
            VersionNo = 1,
            ManifestJson = JsonSerializer.Serialize(manifest, JsonDefaults.Options),
            ManifestSha256 = hash,
            OutputAssetId = video.Id,
            QaJson = JsonSerializer.Serialize(
                new { passed = qaPassed, errors = Array.Empty<object>(), warnings = Array.Empty<object>() },
                JsonDefaults.Options),
            Status = status,
            CreatedAt = now
        });
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();
        return new SeededVersion(productId, campaignId, revisionId, versionId, hash, script, manifest);
    }

    /// <summary>
    /// Replaces the fake output rows with real stored files (a minimal MP4 container, a JPEG
    /// thumbnail and an SRT file) so export tests can verify exact bytes and checksums.
    /// </summary>
    public static async Task<(byte[] Video, byte[] Thumbnail, byte[] Subtitles)> AttachRealOutputsAsync(
        AppDbContext db, IAssetStore store, SeededVersion seeded)
    {
        var video = new byte[]
        {
            0, 0, 0, 0x18, (byte)'f', (byte)'t', (byte)'y', (byte)'p', (byte)'m', (byte)'p', (byte)'4', (byte)'2',
            0, 0, 0, 0, (byte)'m', (byte)'p', (byte)'4', (byte)'2', (byte)'i', (byte)'s', (byte)'o', (byte)'m'
        }.Concat(RandomNumberGenerator.GetBytes(256)).ToArray();
        using var bitmap = new SKBitmap(32, 32);
        bitmap.Erase(new SKColor(10, 120, 200));
        using var skImage = SKImage.FromBitmap(bitmap);
        var thumbnail = skImage.Encode(SKEncodedImageFormat.Jpeg, 80).ToArray();
        var subtitles = Encoding.UTF8.GetBytes(
            "1\n00:00:00,000 --> 00:00:04,000\n" + seeded.Script.Scenes[0].Caption + "\n");

        var videoRef = await store.PutAsync(new MemoryStream(video), AssetKind.Video, "video/mp4", default);
        var thumbRef = await store.PutAsync(new MemoryStream(thumbnail), AssetKind.Thumbnail, "image/jpeg", default);
        var subtitleRef = await store.PutAsync(
            new MemoryStream(subtitles), AssetKind.Subtitle, "application/x-subrip", default);
        await db.VideoVersions
            .Where(x => x.Id == seeded.VersionId)
            .ExecuteUpdateAsync(s => s
                .SetProperty(x => x.OutputAssetId, videoRef.Id)
                .SetProperty(x => x.ThumbnailAssetId, thumbRef.Id)
                .SetProperty(x => x.SubtitlesAssetId, subtitleRef.Id));
        db.ChangeTracker.Clear();
        return (video, thumbnail, subtitles);
    }

    public static Dictionary<string, bool> FullChecklist()
        => new[]
        {
            "product_unchanged", "claims_match_evidence", "narration_and_captions_correct",
            "rights_confirmed", "disclosure_prepared", "offer_checked", "version_matches_preview"
        }.ToDictionary(key => key, _ => true);

    private static AssetRef NewAsset(AssetKind kind, string mime, long bytes, long? durationMs)
    {
        var hash = Convert.ToHexString(RandomNumberGenerator.GetBytes(32)).ToLowerInvariant();
        return new AssetRef(
            Guid.NewGuid(), hash, $"assets/sha256/{hash[..2]}/{hash}", kind, mime, bytes, durationMs);
    }

    private static Asset ToEntity(AssetRef asset, DateTimeOffset now)
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
}
