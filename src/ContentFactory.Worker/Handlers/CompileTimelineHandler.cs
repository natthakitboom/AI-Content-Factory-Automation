using System.Text.Json;
using ContentFactory.Contracts;
using ContentFactory.Core.Abstractions;
using ContentFactory.Core.Exceptions;
using ContentFactory.Infrastructure.Media;
using ContentFactory.Infrastructure.Persistence;
using ContentFactory.Infrastructure.Persistence.Entities;
using Microsoft.EntityFrameworkCore;

namespace ContentFactory.Worker.Handlers;

/// <summary>
/// Compiles the frame-grid manifest from persisted facts only: the validated script, the
/// scene visuals, the measured narration and (hybrid) the downloaded motion asset. It
/// creates one immutable video version holding the manifest and its canonical hash.
/// </summary>
public sealed class CompileTimelineHandler : IJobHandler
{
    public const string JobTypeName = "compile_timeline";

    private readonly AppDbContext _db;
    private readonly ITimelineCompiler _compiler;

    public CompileTimelineHandler(AppDbContext db, ITimelineCompiler compiler)
    {
        _db = db ?? throw new ArgumentNullException(nameof(db));
        _compiler = compiler ?? throw new ArgumentNullException(nameof(compiler));
    }

    public string JobType => JobTypeName;

    public async Task<JobOutcome> ExecuteAsync(LeasedJob job, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(job);
        ct.ThrowIfCancellationRequested();

        var payload = RunPayload.TryRead(job.PayloadJson);
        if (payload is null)
        {
            return Blocked("TIMELINE_PAYLOAD_INVALID");
        }

        var revision = await _db.ContentRevisions
            .AsNoTracking()
            .SingleOrDefaultAsync(x => x.Id == payload.RevisionId, ct);
        if (revision?.ScriptJson is null)
        {
            return Blocked("SCRIPT_MISSING");
        }

        var existing = await _db.VideoVersions
            .Where(x => x.ContentRevisionId == revision.Id && x.Status == "building")
            .OrderByDescending(x => x.VersionNo)
            .FirstOrDefaultAsync(ct);
        if (existing is not null)
        {
            return Succeeded(payload, existing.Id);
        }

        var script = JsonSerializer.Deserialize<ScriptDocument>(
            revision.ScriptJson, JsonDefaults.Options)!;
        var rows = await (
                from scene in _db.SceneAssets.AsNoTracking()
                join asset in _db.Assets.AsNoTracking() on scene.AssetId equals asset.Id
                where scene.ContentRevisionId == revision.Id
                select new { scene.SceneId, scene.Role, Asset = asset })
            .ToListAsync(ct);

        var visuals = rows
            .Where(x => x.Role == PrepareVisualsHandler.VisualRole)
            .ToDictionary(x => x.SceneId, x => ToRef(x.Asset), StringComparer.Ordinal);
        var voices = rows
            .Where(x => x.Role == SynthesizeVoiceHandler.VoiceRole)
            .Select(x => new VoiceSegment(x.SceneId, ToRef(x.Asset), x.Asset.DurationMs ?? 0))
            .ToList();

        if (payload.VideoRequest is { } motion)
        {
            var output = await _db.ProviderRequests
                .AsNoTracking()
                .Where(x => x.ClientOperationId == motion.OperationId)
                .Select(x => x.OutputAssetId)
                .SingleOrDefaultAsync(ct);
            var asset = output is null
                ? null
                : await _db.Assets.AsNoTracking().SingleOrDefaultAsync(x => x.Id == output, ct);
            if (asset is null || asset.Kind != AssetKind.Video)
            {
                return Blocked("MOTION_ASSET_MISSING");
            }

            visuals[motion.SceneId] = ToRef(asset);
        }

        var versionId = Guid.NewGuid();
        RenderManifest manifest;
        try
        {
            manifest = _compiler.Compile(
                versionId, script, visuals, voices, ProductBasicTemplate.Version);
        }
        catch (ValidationException exception)
        {
            return Blocked(exception.Code);
        }

        var versionNo = (await _db.VideoVersions
            .Where(x => x.ContentRevisionId == revision.Id)
            .MaxAsync(x => (int?)x.VersionNo, ct) ?? 0) + 1;
        _db.VideoVersions.Add(new VideoVersion
        {
            Id = versionId,
            ContentRevisionId = revision.Id,
            VersionNo = versionNo,
            ManifestJson = JsonSerializer.Serialize(manifest, JsonDefaults.Options),
            ManifestSha256 = ManifestHasher.Compute(manifest),
            Status = "building",
            CreatedAt = DateTimeOffset.UtcNow
        });
        await _db.SaveChangesAsync(ct);
        return Succeeded(payload, versionId);
    }

    private static JobOutcome Succeeded(RunPayload payload, Guid versionId)
        => new(
            "succeeded",
            null,
            null,
            versionId.ToString("D"),
            payload.With(("videoVersionId", versionId)));

    private static AssetRef ToRef(Asset asset)
        => new(asset.Id, asset.Sha256, asset.RelativePath, asset.Kind, asset.Mime, asset.Bytes, asset.DurationMs);

    private static JobOutcome Blocked(string code) => new("blocked", null, code, null);
}
