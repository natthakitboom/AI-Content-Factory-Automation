using System.Text.Json;
using ContentFactory.Contracts;
using ContentFactory.Core.Abstractions;
using ContentFactory.Core.Exceptions;
using ContentFactory.Infrastructure.Media;
using ContentFactory.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace ContentFactory.Worker.Handlers;

/// <summary>
/// Renders exactly the persisted manifest. The stored hash is recomputed first, so a
/// manifest that changed after compilation never reaches the renderer.
/// </summary>
public sealed class RenderVideoHandler : IJobHandler
{
    public const string JobTypeName = "render_video";

    private readonly AppDbContext _db;
    private readonly IVideoRenderer _renderer;

    public RenderVideoHandler(AppDbContext db, IVideoRenderer renderer)
    {
        _db = db ?? throw new ArgumentNullException(nameof(db));
        _renderer = renderer ?? throw new ArgumentNullException(nameof(renderer));
    }

    public string JobType => JobTypeName;

    public async Task<JobOutcome> ExecuteAsync(LeasedJob job, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(job);
        ct.ThrowIfCancellationRequested();

        var payload = RunPayload.TryRead(job.PayloadJson);
        if (payload?.VideoVersionId is not { } versionId)
        {
            return Blocked("RENDER_PAYLOAD_INVALID");
        }

        var version = await _db.VideoVersions.SingleOrDefaultAsync(x => x.Id == versionId, ct);
        if (version is null || version.ManifestJson is null)
        {
            return Blocked("RENDER_VERSION_MISSING");
        }

        if (version.Status != "building")
        {
            return Blocked("RENDER_VERSION_LOCKED");
        }

        var manifest = JsonSerializer.Deserialize<RenderManifest>(
            version.ManifestJson, JsonDefaults.Options);
        if (manifest is null
            || !string.Equals(ManifestHasher.Compute(manifest), version.ManifestSha256, StringComparison.Ordinal))
        {
            version.Status = "blocked";
            await _db.SaveChangesAsync(ct);
            return Blocked("RENDER_MANIFEST_MISMATCH", version.Id);
        }

        RenderResult result;
        try
        {
            result = await _renderer.RenderAsync(manifest, ct);
        }
        catch (ValidationException exception)
        {
            version.Status = "blocked";
            await _db.SaveChangesAsync(ct);
            return Blocked(exception.Code, version.Id);
        }

        version.OutputAssetId = result.Video.Id;
        version.ThumbnailAssetId = result.Thumbnail.Id;
        version.SubtitlesAssetId = result.Subtitles.Id;
        await _db.SaveChangesAsync(ct);
        return new JobOutcome(
            "succeeded",
            null,
            null,
            version.Id.ToString("D"),
            payload.With(("videoVersionId", version.Id), ("renderResult", result)));
    }

    private static JobOutcome Blocked(string code, Guid? versionId = null)
        => new("blocked", null, code, versionId?.ToString("D"));
}
