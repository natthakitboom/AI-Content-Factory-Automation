using ContentFactory.Contracts;
using ContentFactory.Core.Abstractions;
using ContentFactory.Core.Exceptions;
using ContentFactory.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace ContentFactory.Worker.Handlers;

/// <summary>
/// Polls the existing provider request. It never submits: an uncertain or unreferenced
/// request is blocked for owner reconciliation, and a poll failure only reschedules.
/// </summary>
public sealed class PollVideoHandler : IJobHandler
{
    public const string JobTypeName = "poll_video";

    internal static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(15);
    internal static readonly TimeSpan DelayedPollInterval = TimeSpan.FromMinutes(2);
    internal static readonly TimeSpan DelayedAfter = TimeSpan.FromMinutes(30);

    private readonly AppDbContext _db;
    private readonly IVideoProvider _videoProvider;
    private readonly TimeProvider _clock;

    public PollVideoHandler(AppDbContext db, IVideoProvider videoProvider, TimeProvider clock)
    {
        _db = db ?? throw new ArgumentNullException(nameof(db));
        _videoProvider = videoProvider ?? throw new ArgumentNullException(nameof(videoProvider));
        _clock = clock ?? throw new ArgumentNullException(nameof(clock));
    }

    public string JobType => JobTypeName;

    public async Task<JobOutcome> ExecuteAsync(LeasedJob job, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(job);
        ct.ThrowIfCancellationRequested();

        var operationId = ProviderJobPayload.ReadOperationId(job.PayloadJson);
        if (operationId is null)
        {
            return Blocked("POLL_PAYLOAD_INVALID");
        }

        var row = await _db.ProviderRequests
            .SingleOrDefaultAsync(x => x.ClientOperationId == operationId, ct);
        if (row is null)
        {
            return Blocked("PROVIDER_REQUEST_NOT_FOUND");
        }

        switch (row.Status)
        {
            case "succeeded":
                return new JobOutcome("succeeded", null, null, row.Id.ToString("D"));
            case "failed_confirmed":
                return new JobOutcome("failed", null, "FAL_GENERATION_FAILED", row.Id.ToString("D"));
            case "cancelled_confirmed":
                return new JobOutcome("cancelled", null, null, row.Id.ToString("D"));
        }

        if (ProviderJobPayload.NeedsReconciliation(row))
        {
            return Blocked("UNKNOWN_REQUIRES_RECONCILIATION");
        }

        ProviderPollResult result;
        try
        {
            result = await _videoProvider.PollAsync(ProviderJobPayload.ToSubmission(row), ct);
        }
        catch (ProviderRejectedException exception)
        {
            return Blocked(exception.Code);
        }
        catch (ValidationException exception)
        {
            return Blocked(exception.Code);
        }

        var now = _clock.GetUtcNow();
        row.LastCheckedAt = now;

        switch (result.Status)
        {
            case ProviderRunStatus.InProgress:
            case ProviderRunStatus.Submitted:
                if (row.Status == "submitted")
                {
                    row.Status = "in_progress";
                }

                await _db.SaveChangesAsync(ct);
                var delayed = row.SubmittedAt is { } submittedAt && now - submittedAt > DelayedAfter;
                return new JobOutcome(
                    "retry_wait",
                    now + (delayed ? DelayedPollInterval : PollInterval),
                    delayed ? "PROVIDER_DELAYED" : null,
                    row.Id.ToString("D"));

            case ProviderRunStatus.Succeeded:
                row.Status = "succeeded";
                await _db.SaveChangesAsync(ct);
                return new JobOutcome("succeeded", null, null, row.Id.ToString("D"));

            case ProviderRunStatus.FailedConfirmed:
                // A failed generation is not proof of no charge, so the reservation stays
                // held until the owner reconciles it.
                row.Status = "failed_confirmed";
                await _db.SaveChangesAsync(ct);
                return new JobOutcome(
                    "failed", null, result.ErrorCode ?? "FAL_GENERATION_FAILED", row.Id.ToString("D"));

            default:
                await _db.SaveChangesAsync(ct);
                return Blocked(result.ErrorCode ?? "FAL_STATUS_UNRECOGNISED");
        }
    }

    private static JobOutcome Blocked(string code) => new("blocked", null, code, null);
}
