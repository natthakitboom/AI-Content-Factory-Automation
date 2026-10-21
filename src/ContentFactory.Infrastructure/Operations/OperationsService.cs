using System.Text.Json;
using ContentFactory.Contracts;
using ContentFactory.Core.Exceptions;
using ContentFactory.Infrastructure.Billing;
using ContentFactory.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace ContentFactory.Infrastructure.Operations;

public sealed record WorkerHeartbeat(DateTimeOffset? LastSeenAt, bool Stale);

public sealed record SystemStatus(
    bool HasData,
    bool Paused,
    bool PaidLanesPaused,
    int Queued,
    int Running,
    int PausedJobs,
    int Blocked,
    int Failed,
    int AwaitingReview,
    WorkerHeartbeat WorkerHeartbeat);

public sealed record JobView(
    Guid Id,
    string JobType,
    string Lane,
    string Status,
    int Attempts,
    int MaxAttempts,
    string? LastErrorCode,
    string? Explanation,
    bool CanRetry,
    bool NeedsReconciliation,
    DateTimeOffset RunAfter,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt);

public sealed record JobEventView(string EventType, DateTimeOffset OccurredAt, JsonElement Details);

public sealed record JobAttemptView(int AttemptNo, DateTimeOffset StartedAt, DateTimeOffset? EndedAt, string? ErrorCode);

public sealed record JobDetail(
    Guid Id, string JobType, string Status, string? LastErrorCode, string? Explanation,
    bool CanRetry, bool NeedsReconciliation,
    IReadOnlyList<JobEventView> Events, IReadOnlyList<JobAttemptView> Attempts);

public sealed record PriceSnapshotView(
    string Provider, string Model, decimal UnitPriceUsd, DateTimeOffset VerifiedAt, DateTimeOffset ValidUntil, bool Stale);

public sealed record CostSummary(
    string Currency,
    decimal ReservedUsd,
    decimal SettledUsd,
    decimal UnknownHeldUsd,
    decimal MonthlyBudgetUsd,
    decimal DailyBudgetUsd,
    decimal RemainingMonthlyUsd,
    PriceSnapshotView? PriceSnapshot,
    string Note);

/// <summary>
/// Read-only operator views over persisted state. Nothing here invents a number: an empty
/// system reports empty, reserved and settled money are separate, and no job offers a blind
/// retry for an uncertain cloud request.
/// </summary>
public sealed class OperationsService
{
    public const string HeartbeatKey = "worker_heartbeat";
    public const string QueuePausedKey = "queue_paused";
    public static readonly TimeSpan HeartbeatStaleAfter = TimeSpan.FromMinutes(2);

    private static readonly Dictionary<string, string> Explanations = new(StringComparer.Ordinal)
    {
        ["SUBMISSION_UNKNOWN"] = "The cloud provider may have accepted this request. It is held for you to reconcile and is never retried automatically.",
        ["UNKNOWN_REQUIRES_RECONCILIATION"] = "This request's cloud state is unknown. Attach the existing request id or confirm no charge; it is never retried automatically.",
        ["PAID_APIS_DISABLED"] = "Paid APIs are turned off, so no cloud request was made.",
        ["PAID_LANES_PAUSED"] = "Paid work is paused after a restore until you reconcile the cost ledger with the provider.",
        ["DISK_PRESSURE"] = "Free disk space is below the safety floor, so no new render was started. Free space, then resume.",
        ["QA_FAILED"] = "Technical quality checks failed. The output was kept for inspection and cannot be approved.",
        ["CLOUD_USE_NOT_ALLOWED"] = "The source image is not approved for cloud use.",
        ["CAPTION_FONT_NOT_CONFIGURED"] = "Set CAPTION_FONT_FILE to the licensed caption font before rendering.",
        ["HYBRID_NO_MOTION_SCENE"] = "A hybrid run needs one scene with a motion prompt and a cloud scene allowance.",
        ["VOICE_UPLOAD_NOT_WIRED"] = "Automatic narration was not requested for this run, and uploaded narration is not connected to the worker yet.",
        ["VOICE_PROVIDER_NOT_CONFIGURED"] = "The selected narration provider is not wired for automatic generation.",
        ["VOICE_VOICE_ID_INVALID"] = "The narration voice id is empty or invalid. Check VOICE_PROVIDER in .env.",
        ["PROVIDER_DELAYED"] = "The provider is taking longer than 30 minutes. The original request is still being polled; nothing was resubmitted."
    };

    private static readonly string[] ReconcileCodes = ["SUBMISSION_UNKNOWN", "UNKNOWN_REQUIRES_RECONCILIATION"];

    private readonly AppDbContext _db;
    private readonly BudgetOptions _budget;
    private readonly TimeProvider _clock;

    public OperationsService(AppDbContext db, BudgetOptions budget, TimeProvider clock)
    {
        _db = db ?? throw new ArgumentNullException(nameof(db));
        _budget = budget ?? throw new ArgumentNullException(nameof(budget));
        _clock = clock ?? throw new ArgumentNullException(nameof(clock));
    }

    public async Task<SystemStatus> StatusAsync(CancellationToken ct)
    {
        var counts = await _db.Jobs.AsNoTracking()
            .GroupBy(x => x.Status)
            .Select(g => new { Status = g.Key, Count = g.Count() })
            .ToDictionaryAsync(x => x.Status, x => x.Count, ct);
        int Count(string status) => counts.GetValueOrDefault(status);

        var review = await _db.VideoVersions.AsNoTracking().CountAsync(x => x.Status == "awaiting_review", ct);
        var flags = await _db.SystemFlags.AsNoTracking()
            .Where(x => x.Key == QueuePausedKey || x.Key == "paid_lanes_paused" || x.Key == HeartbeatKey)
            .ToDictionaryAsync(x => x.Key, x => x.Value, ct);

        DateTimeOffset? seen = flags.TryGetValue(HeartbeatKey, out var raw)
            && DateTimeOffset.TryParse(raw, System.Globalization.CultureInfo.InvariantCulture,
                System.Globalization.DateTimeStyles.RoundtripKind, out var parsed)
                ? parsed
                : null;
        var hasData = counts.Count > 0 || review > 0
            || await _db.BudgetReservations.AsNoTracking().AnyAsync(ct);

        return new SystemStatus(
            hasData,
            flags.GetValueOrDefault(QueuePausedKey) == "true",
            flags.GetValueOrDefault("paid_lanes_paused") == "true",
            Count("queued") + Count("retry_wait"),
            Count("running"),
            Count("paused"),
            Count("blocked"),
            Count("failed"),
            review,
            new WorkerHeartbeat(seen, seen is null || _clock.GetUtcNow() - seen > HeartbeatStaleAfter));
    }

    public async Task<IReadOnlyList<JobView>> JobsAsync(string? status, int limit, CancellationToken ct)
    {
        limit = Math.Clamp(limit, 1, 200);
        var query = _db.Jobs.AsNoTracking().AsQueryable();
        if (!string.IsNullOrWhiteSpace(status))
        {
            query = query.Where(x => x.Status == status);
        }

        var jobs = await query.OrderByDescending(x => x.UpdatedAt).Take(limit).ToListAsync(ct);
        return jobs.Select(ToView).ToList();
    }

    public async Task<JobDetail> JobDetailAsync(Guid id, CancellationToken ct)
    {
        var job = await _db.Jobs.AsNoTracking().SingleOrDefaultAsync(x => x.Id == id, ct)
            ?? throw new NotFoundException("JOB_NOT_FOUND", "Job not found.");
        var view = ToView(job);
        var events = await _db.JobEvents.AsNoTracking()
            .Where(x => x.JobId == id).OrderBy(x => x.OccurredAt).Take(200).ToListAsync(ct);
        var attempts = await _db.JobAttempts.AsNoTracking()
            .Where(x => x.JobId == id).OrderBy(x => x.AttemptNo).ToListAsync(ct);
        return new JobDetail(
            job.Id, job.JobType, job.Status, job.LastErrorCode, view.Explanation, view.CanRetry,
            view.NeedsReconciliation,
            events.Select(x =>
            {
                using var details = JsonDocument.Parse(x.DetailsJson);
                return new JobEventView(x.EventType, x.OccurredAt, details.RootElement.Clone());
            }).ToList(),
            attempts.Select(x => new JobAttemptView(x.AttemptNo, x.StartedAt, x.EndedAt, x.ErrorCode)).ToList());
    }

    public async Task<CostSummary> CostsAsync(CancellationToken ct)
    {
        var now = _clock.GetUtcNow();
        var monthStart = new DateTimeOffset(now.Year, now.Month, 1, 0, 0, 0, TimeSpan.Zero);
        var reserved = await _db.BudgetReservations.AsNoTracking()
            .Where(x => x.Status == "reserved").SumAsync(x => (decimal?)x.ReservedUsd, ct) ?? 0m;
        var settled = await _db.BudgetReservations.AsNoTracking()
            .Where(x => x.Status == "settled" && x.CreatedAt >= monthStart)
            .SumAsync(x => (decimal?)x.SettledUsd, ct) ?? 0m;
        var unknown = await (
                from reservation in _db.BudgetReservations.AsNoTracking()
                join request in _db.ProviderRequests.AsNoTracking()
                    on reservation.OperationId equals request.ClientOperationId
                where reservation.Status == "reserved" && request.Status == "submission_unknown"
                select (decimal?)reservation.ReservedUsd)
            .SumAsync(ct) ?? 0m;
        var snapshot = await _db.PriceSnapshots.AsNoTracking()
            .OrderByDescending(x => x.VerifiedAt).FirstOrDefaultAsync(ct);

        return new CostSummary(
            "USD",
            reserved,
            settled,
            unknown,
            _budget.MonthlyBudgetUsd,
            _budget.DailyBudgetUsd,
            _budget.MonthlyBudgetUsd - reserved - settled,
            snapshot is null
                ? null
                : new PriceSnapshotView(
                    snapshot.Provider, snapshot.Model, snapshot.UnitPriceUsd, snapshot.VerifiedAt,
                    snapshot.ValidUntil, snapshot.ValidUntil <= now),
            "Reserved is held for requests in flight or uncertain; settled is money recorded as spent this month. They are never added into one 'cost' figure.");
    }

    private static JobView ToView(Persistence.Entities.Job job)
    {
        var reconcile = job.LastErrorCode is not null && ReconcileCodes.Contains(job.LastErrorCode);
        return new JobView(
            job.Id, job.JobType, job.Lane.ToString(), job.Status, job.Attempts, job.MaxAttempts,
            job.LastErrorCode, Explain(job.LastErrorCode),
            CanRetry: false,
            reconcile, job.RunAfter, job.CreatedAt, job.UpdatedAt);
    }

    private static string? Explain(string? code)
        => code is null ? null : Explanations.GetValueOrDefault(code, $"Stopped with code {code}.");
}
