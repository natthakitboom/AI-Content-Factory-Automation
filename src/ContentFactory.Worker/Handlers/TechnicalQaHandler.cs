using System.Text.Json;
using ContentFactory.Contracts;
using ContentFactory.Core.Abstractions;
using ContentFactory.Core.Exceptions;
using ContentFactory.Infrastructure.Media;
using ContentFactory.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace ContentFactory.Worker.Handlers;

/// <summary>
/// Runs technical QA against an already rendered, immutable video version. The video row
/// keeps its output asset on failure for debugging/retention; only a passing report moves
/// the row to <c>awaiting_review</c>. The exact manifest hash is verified before QA so a
/// render cannot be approved against a different script/template identity.
/// </summary>
public sealed class TechnicalQaHandler : IJobHandler
{
    public const string JobTypeName = "technical_qa";

    private readonly AppDbContext _db;
    private readonly ITechnicalQa _qa;
    private readonly TimeProvider _clock;

    public TechnicalQaHandler(
        AppDbContext db,
        ITechnicalQa qa,
        TimeProvider clock)
    {
        _db = db ?? throw new ArgumentNullException(nameof(db));
        _qa = qa ?? throw new ArgumentNullException(nameof(qa));
        _clock = clock ?? throw new ArgumentNullException(nameof(clock));
    }

    public string JobType => JobTypeName;

    public async Task<JobOutcome> ExecuteAsync(LeasedJob job, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(job);
        ct.ThrowIfCancellationRequested();

        TechnicalQaJobPayload? payload;
        try
        {
            payload = JsonSerializer.Deserialize<TechnicalQaJobPayload>(
                job.PayloadJson,
                JsonDefaults.Options);
        }
        catch (JsonException)
        {
            return Blocked("QA_PAYLOAD_INVALID");
        }

        if (payload is null ||
            payload.VideoVersionId == Guid.Empty ||
            payload.RenderResult is null)
        {
            return Blocked("QA_PAYLOAD_INVALID");
        }

        var version = await _db.VideoVersions.SingleOrDefaultAsync(
            x => x.Id == payload.VideoVersionId,
            ct);
        if (version is null)
        {
            return Blocked("QA_VIDEO_VERSION_MISSING");
        }

        if (version.Status is "approved" or "rejected" or "cancelled")
        {
            return Blocked("QA_VIDEO_VERSION_LOCKED");
        }

        if (!await AssetsExistAsync(payload.RenderResult, ct))
        {
            return await BlockVersionAsync(
                version,
                "QA_ASSET_MISSING",
                "A render result asset row is missing.",
                ct);
        }

        if (version.OutputAssetId is { } outputAssetId &&
            outputAssetId != payload.RenderResult.Video.Id)
        {
            return await BlockVersionAsync(
                version,
                "QA_OUTPUT_MISMATCH",
                "The video version points at a different output asset.",
                ct);
        }

        if (!TryValidateManifest(
                version,
                payload.RenderResult,
                out var manifestError))
        {
            return await BlockVersionAsync(
                version,
                "QA_MANIFEST_MISMATCH",
                manifestError ?? "The render manifest does not match the video version.",
                ct);
        }

        ValidationReport report;
        try
        {
            report = await _qa.CheckAsync(payload.RenderResult, ct);
        }
        catch (ValidationException exception)
        {
            return await BlockVersionAsync(
                version,
                exception.Code,
                exception.Message,
                ct);
        }

        version.OutputAssetId = payload.RenderResult.Video.Id;
        version.QaJson = SerializeReport(report, payload.RenderResult);
        if (!report.Passed)
        {
            version.Status = "qa_failed";
            await _db.SaveChangesAsync(ct);
            return new JobOutcome(
                "failed",
                null,
                "QA_FAILED",
                version.Id.ToString("D"));
        }

        version.Status = "awaiting_review";
        await _db.SaveChangesAsync(ct);
        return new JobOutcome(
            "succeeded",
            null,
            null,
            version.Id.ToString("D"));
    }

    private async Task<bool> AssetsExistAsync(RenderResult result, CancellationToken ct)
    {
        var ids = new[]
        {
            result.Video.Id,
            result.Thumbnail.Id,
            result.Subtitles.Id
        }.Distinct().ToArray();
        var count = await _db.Assets.CountAsync(x => ids.Contains(x.Id), ct);
        return count == ids.Length;
    }

    private static bool TryValidateManifest(
        ContentFactory.Infrastructure.Persistence.Entities.VideoVersion version,
        RenderResult result,
        out string? error)
    {
        error = null;
        if (string.IsNullOrWhiteSpace(version.ManifestJson) ||
            string.IsNullOrWhiteSpace(version.ManifestSha256))
        {
            error = "The video version has no persisted manifest.";
            return false;
        }

        if (!string.Equals(
                version.ManifestSha256,
                result.ManifestSha256,
                StringComparison.Ordinal))
        {
            error = "The persisted manifest hash does not match the render result.";
            return false;
        }

        RenderManifest? manifest;
        try
        {
            manifest = JsonSerializer.Deserialize<RenderManifest>(
                version.ManifestJson,
                JsonDefaults.Options);
        }
        catch (JsonException)
        {
            error = "The persisted manifest is not valid JSON.";
            return false;
        }

        if (manifest is null)
        {
            error = "The persisted manifest is empty.";
            return false;
        }

        var computed = ManifestHasher.Compute(manifest);
        if (!string.Equals(computed, result.ManifestSha256, StringComparison.Ordinal))
        {
            error = "The persisted manifest content does not match the render result hash.";
            return false;
        }

        return true;
    }

    private async Task<JobOutcome> BlockVersionAsync(
        ContentFactory.Infrastructure.Persistence.Entities.VideoVersion version,
        string code,
        string message,
        CancellationToken ct)
    {
        version.Status = "blocked";
        version.QaJson = SerializeFailure(code, message);
        await _db.SaveChangesAsync(ct);
        return Blocked(code, version.Id);
    }

    private string SerializeReport(ValidationReport report, RenderResult result)
        => JsonSerializer.Serialize(
            new
            {
                passed = report.Passed,
                errors = report.Errors,
                warnings = report.Warnings,
                manifestSha256 = result.ManifestSha256,
                durationMs = result.DurationMs,
                checkedAt = _clock.GetUtcNow()
            },
            JsonDefaults.Options);

    private string SerializeFailure(string code, string message)
        => JsonSerializer.Serialize(
            new
            {
                passed = false,
                errors = new[]
                {
                    new ValidationIssue(code, "qa", message)
                },
                warnings = Array.Empty<ValidationIssue>(),
                checkedAt = _clock.GetUtcNow()
            },
            JsonDefaults.Options);

    private static JobOutcome Blocked(string code, Guid? videoVersionId = null)
        => new(
            "blocked",
            null,
            code,
            videoVersionId?.ToString("D"));

    private sealed record TechnicalQaJobPayload(
        Guid VideoVersionId,
        RenderResult? RenderResult);
}
