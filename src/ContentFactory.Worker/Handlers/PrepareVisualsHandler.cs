using System.Text.Json;
using ContentFactory.Contracts;
using ContentFactory.Core.Abstractions;
using ContentFactory.Infrastructure.Media;
using ContentFactory.Infrastructure.Persistence;
using ContentFactory.Infrastructure.Persistence.Entities;
using Microsoft.EntityFrameworkCore;

namespace ContentFactory.Worker.Handlers;

/// <summary>
/// Assigns one product image to every scene and, in hybrid mode, prepares the single cloud
/// motion request. Only attested product images from the frozen snapshot are used; this
/// stage never calls a provider. A hybrid run without a motion scene fails closed.
/// </summary>
public sealed class PrepareVisualsHandler : IJobHandler
{
    public const string JobTypeName = "prepare_visuals";
    public const string VisualRole = "visual";

    private readonly AppDbContext _db;
    private readonly TimeProvider _clock;

    public PrepareVisualsHandler(AppDbContext db, TimeProvider clock)
    {
        _db = db ?? throw new ArgumentNullException(nameof(db));
        _clock = clock ?? throw new ArgumentNullException(nameof(clock));
    }

    public string JobType => JobTypeName;

    public async Task<JobOutcome> ExecuteAsync(LeasedJob job, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(job);
        ct.ThrowIfCancellationRequested();

        var payload = RunPayload.TryRead(job.PayloadJson);
        if (payload is null)
        {
            return Blocked("VISUALS_PAYLOAD_INVALID");
        }

        var revision = await _db.ContentRevisions
            .AsNoTracking()
            .SingleOrDefaultAsync(x => x.Id == payload.RevisionId, ct);
        if (revision?.ScriptJson is null)
        {
            return Blocked("SCRIPT_MISSING");
        }

        var script = JsonSerializer.Deserialize<ScriptDocument>(
            revision.ScriptJson, JsonDefaults.Options);
        var images = payload.Request.Product.Assets
            .Where(asset => asset.Kind == AssetKind.Image)
            .ToList();
        if (script is null || images.Count == 0)
        {
            return Blocked("NO_PRODUCT_IMAGE");
        }

        var existing = (await _db.SceneAssets
                .Where(x => x.ContentRevisionId == revision.Id && x.Role == VisualRole)
                .ToListAsync(ct))
            .ToDictionary(x => x.SceneId, StringComparer.Ordinal);
        var chosen = new Dictionary<string, AssetRef>(StringComparer.Ordinal);
        foreach (var scene in script.Scenes)
        {
            var asset = images.FirstOrDefault(x => x.Id == scene.PreferredAssetId) ?? images[0];
            chosen[scene.SceneId] = asset;
            if (!existing.ContainsKey(scene.SceneId))
            {
                _db.SceneAssets.Add(new SceneAsset
                {
                    Id = Guid.NewGuid(),
                    ContentRevisionId = revision.Id,
                    SceneId = scene.SceneId,
                    Role = VisualRole,
                    AssetId = asset.Id
                });
            }
        }

        await _db.SaveChangesAsync(ct);

        if (payload.Request.Mode != VideoMode.HybridMotion)
        {
            return new JobOutcome("succeeded", null, null, revision.Id.ToString("D"));
        }

        var motionScene = payload.CloudVideoSceneLimit > 0
            ? script.Scenes.FirstOrDefault(x => !string.IsNullOrWhiteSpace(x.MotionPrompt))
            : null;
        if (motionScene is null)
        {
            return Blocked("HYBRID_NO_MOTION_SCENE");
        }

        // One motion scene per run is wired; further scenes stay on still images.
        var request = new VideoGenerationRequest(
            $"video:{revision.Id:N}:{motionScene.SceneId}",
            revision.Id,
            motionScene.SceneId,
            chosen[motionScene.SceneId],
            motionScene.MotionPrompt!,
            "720p",
            "9:16",
            Guid.Empty);
        var configHash = ManifestHasher.HashCanonicalJson(
            JsonSerializer.Serialize(
                new { resolution = request.Resolution, aspectRatio = request.AspectRatio },
                JsonDefaults.Options));
        _ = _clock;

        return new JobOutcome(
            "succeeded",
            null,
            null,
            revision.Id.ToString("D"),
            payload.With(
                ("request", request),
                ("campaignId", revision.CampaignId),
                ("videoScopeId", revision.Id),
                ("configHash", configHash)));
    }

    private static JobOutcome Blocked(string code) => new("blocked", null, code, null);
}
