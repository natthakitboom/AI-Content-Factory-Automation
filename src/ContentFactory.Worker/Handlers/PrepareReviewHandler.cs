using ContentFactory.Contracts;
using ContentFactory.Core.Abstractions;
using ContentFactory.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace ContentFactory.Worker.Handlers;

/// <summary>
/// Confirms the run is parked at owner review. It approves nothing: approval is an owner
/// action bound to the exact manifest hash, and a version that did not pass QA never
/// reaches this state.
/// </summary>
public sealed class PrepareReviewHandler : IJobHandler
{
    public const string JobTypeName = "prepare_review";

    private readonly AppDbContext _db;

    public PrepareReviewHandler(AppDbContext db)
    {
        _db = db ?? throw new ArgumentNullException(nameof(db));
    }

    public string JobType => JobTypeName;

    public async Task<JobOutcome> ExecuteAsync(LeasedJob job, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(job);
        ct.ThrowIfCancellationRequested();

        var payload = RunPayload.TryRead(job.PayloadJson);
        if (payload?.VideoVersionId is not { } versionId)
        {
            return new JobOutcome("blocked", null, "REVIEW_PAYLOAD_INVALID", null);
        }

        var status = await _db.VideoVersions
            .AsNoTracking()
            .Where(x => x.Id == versionId)
            .Select(x => x.Status)
            .SingleOrDefaultAsync(ct);
        return status == "awaiting_review"
            ? new JobOutcome("succeeded", null, null, versionId.ToString("D"))
            : new JobOutcome("blocked", null, "REVIEW_NOT_READY", versionId.ToString("D"));
    }
}
