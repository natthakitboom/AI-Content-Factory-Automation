using System.Data.Common;
using System.Text.Json;
using ContentFactory.Contracts;
using ContentFactory.Core.Abstractions;
using ContentFactory.Infrastructure.Persistence;
using ContentFactory.Infrastructure.Persistence.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Npgsql;

namespace ContentFactory.Infrastructure.Jobs;

/// <summary>
/// Lease, heartbeat and watchdog intervals from C3. Defaults are fixed; a worker may
/// override them for tests but production values stay explicit.
/// </summary>
public sealed record JobLeaseOptions
{
    public TimeSpan LeaseDuration { get; init; } = TimeSpan.FromSeconds(120);
    public TimeSpan HeartbeatInterval { get; init; } = TimeSpan.FromSeconds(20);
    public TimeSpan WatchdogInterval { get; init; } = TimeSpan.FromSeconds(30);
}

/// <summary>
/// PostgreSQL-backed job store. Claiming uses a short transaction with
/// <c>FOR UPDATE SKIP LOCKED</c>; every mutation carries id, fence, owner and an
/// unexpired lease in its predicate. A stale worker therefore cannot write state or
/// finalize a job after another worker has claimed it.
/// </summary>
public sealed class PostgresJobStore : IJobStore
{
    private static readonly string[] FinishStatuses =
        ["succeeded", "retry_wait", "paused", "failed", "cancelled", "blocked"];

    private readonly AppDbContext _db;
    private readonly TimeProvider _clock;
    private readonly JobLeaseOptions _options;

    public PostgresJobStore(AppDbContext db, TimeProvider clock, JobLeaseOptions? options = null)
    {
        _db = db;
        _clock = clock;
        _options = options ?? new JobLeaseOptions();
        ValidateOptions(_options);
    }

    public async Task<LeasedJob?> TryLeaseAsync(
        string worker,
        JobLane lane,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(worker) || worker.Length > 128)
        {
            throw new ArgumentException(
                "A worker name is required and must be 128 characters or fewer.",
                nameof(worker));
        }

        var now = _clock.GetUtcNow();
        await using var transaction = await _db.Database.BeginTransactionAsync(ct);

        var id = await SelectEligibleJobIdAsync(worker, lane, now, ct);
        if (id is null)
        {
            await transaction.RollbackAsync(ct);
            return null;
        }

        var job = await _db.Jobs.SingleAsync(x => x.Id == id.Value, ct);
        var fence = job.Fence + 1;
        var attemptNo = job.Attempts + 1;
        var leaseUntil = now.Add(_options.LeaseDuration);

        job.Status = "running";
        job.LeaseOwner = worker;
        job.LeaseUntil = leaseUntil;
        job.Fence = fence;
        job.Attempts = attemptNo;
        job.UpdatedAt = now;

        _db.JobAttempts.Add(new JobAttempt
        {
            Id = Guid.NewGuid(),
            JobId = job.Id,
            AttemptNo = attemptNo,
            Fence = fence,
            StartedAt = now
        });

        _db.JobEvents.Add(NewEvent(
            job.Id,
            "leased",
            new { worker, fence, leaseUntil },
            now));

        await _db.SaveChangesAsync(ct);
        await transaction.CommitAsync(ct);

        return new LeasedJob(
            job.Id,
            job.JobType,
            job.PayloadJson,
            job.PayloadVersion,
            worker,
            fence,
            leaseUntil);
    }

    public async Task<bool> RenewAsync(LeasedJob job, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(job);

        var now = _clock.GetUtcNow();
        var leaseUntil = now.Add(_options.LeaseDuration);

        var affected = await _db.Jobs
            .Where(x => x.Id == job.Id
                && x.Status == "running"
                && x.Fence == job.Fence
                && x.LeaseOwner == job.LeaseOwner
                && x.LeaseUntil != null
                && x.LeaseUntil > now)
            .ExecuteUpdateAsync(
                setters => setters
                    .SetProperty(x => x.LeaseUntil, leaseUntil)
                    .SetProperty(x => x.UpdatedAt, now),
                ct);

        return affected == 1;
    }

    public async Task<bool> FinishAsync(
        LeasedJob job,
        JobOutcome outcome,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(job);
        ArgumentNullException.ThrowIfNull(outcome);

        if (!FinishStatuses.Contains(outcome.Status, StringComparer.Ordinal))
        {
            throw new ArgumentException(
                "Outcome status must be succeeded, retry_wait, paused, failed, cancelled or blocked.",
                nameof(outcome));
        }

        if (outcome.Status == "retry_wait" && outcome.NextRunAt is null)
        {
            throw new ArgumentException(
                "A retry_wait outcome requires NextRunAt.",
                nameof(outcome));
        }

        var now = _clock.GetUtcNow();
        await using var transaction = await _db.Database.BeginTransactionAsync(ct);

        var affected = await _db.Jobs
            .Where(x => x.Id == job.Id
                && x.Status == "running"
                && x.Fence == job.Fence
                && x.LeaseOwner == job.LeaseOwner
                && x.LeaseUntil != null
                && x.LeaseUntil > now)
            .ExecuteUpdateAsync(
                setters => setters
                    .SetProperty(x => x.Status, outcome.Status)
                    .SetProperty(x => x.LeaseOwner, (string?)null)
                    .SetProperty(x => x.LeaseUntil, (DateTimeOffset?)null)
                    .SetProperty(x => x.LastErrorCode, outcome.ErrorCode)
                    .SetProperty(x => x.RunAfter, x => outcome.NextRunAt ?? x.RunAfter)
                    .SetProperty(x => x.UpdatedAt, now),
                ct);

        if (affected != 1)
        {
            await transaction.RollbackAsync(ct);
            return false;
        }

        var attemptUpdated = await _db.JobAttempts
            .Where(x => x.JobId == job.Id
                && x.Fence == job.Fence
                && x.EndedAt == null)
            .ExecuteUpdateAsync(
                setters => setters
                    .SetProperty(x => x.EndedAt, now)
                    .SetProperty(x => x.ErrorCode, outcome.ErrorCode),
                ct);

        if (attemptUpdated != 1)
        {
            await transaction.RollbackAsync(ct);
            return false;
        }

        _db.JobEvents.Add(NewEvent(
            job.Id,
            "finished",
            new
            {
                status = outcome.Status,
                outcome.ErrorCode,
                outcome.OutputReference,
                outcome.NextRunAt,
                fence = job.Fence,
                worker = job.LeaseOwner
            },
            now));

        await _db.SaveChangesAsync(ct);
        await transaction.CommitAsync(ct);
        return true;
    }

    /// <summary>
    /// Inserts a queued job. The caller supplies the dedupe key so the outbox can derive
    /// it from the source event id and replay without creating a duplicate.
    /// </summary>
    public async Task<Guid> EnqueueAsync(
        NewJob job,
        string dedupeKey,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(job);
        ValidateNewJob(job, dedupeKey);

        var now = _clock.GetUtcNow();
        var id = Guid.NewGuid();
        var runAfter = job.RunAfter ?? now;

        await _db.Database.ExecuteSqlInterpolatedAsync(
            $"""
            INSERT INTO app.jobs
                (id, job_type, payload_json, payload_version, dedupe_key, lane, status,
                 priority, run_after, attempts, max_attempts, fence, created_at, updated_at)
            VALUES
                ({id}, {job.JobType}, {job.PayloadJson}::jsonb, {job.PayloadVersion},
                 {dedupeKey}, {ToDbLane(job.Lane)}, 'queued', {job.Priority}, {runAfter},
                 0, {job.MaxAttempts}, 0, {now}, {now})
            ON CONFLICT (dedupe_key) DO NOTHING
            """,
            ct);

        return await _db.Jobs
            .AsNoTracking()
            .Where(x => x.DedupeKey == dedupeKey)
            .Select(x => x.Id)
            .SingleAsync(ct);
    }

    internal static JobEvent NewEvent(
        Guid jobId,
        string eventType,
        object details,
        DateTimeOffset now)
        => new()
        {
            Id = Guid.NewGuid(),
            JobId = jobId,
            EventType = eventType,
            DetailsJson = JsonSerializer.Serialize(details, JsonDefaults.Options),
            OccurredAt = now
        };

    internal static string ToDbLane(JobLane lane) => lane switch
    {
        JobLane.Control => "control",
        JobLane.HeavyLocal => "heavy_local",
        JobLane.CloudSubmit => "cloud_submit",
        JobLane.CloudPoll => "cloud_poll",
        JobLane.Download => "download",
        _ => throw new ArgumentOutOfRangeException(nameof(lane), lane, "Unknown job lane.")
    };

    internal static void ValidateOptions(JobLeaseOptions options)
    {
        if (options.LeaseDuration <= TimeSpan.Zero
            || options.HeartbeatInterval <= TimeSpan.Zero
            || options.WatchdogInterval <= TimeSpan.Zero
            || options.HeartbeatInterval >= options.LeaseDuration
            || options.WatchdogInterval >= options.LeaseDuration)
        {
            throw new ArgumentException(
                "Lease, heartbeat and watchdog intervals must be positive and shorter than the lease.",
                nameof(options));
        }
    }

    private async Task<Guid?> SelectEligibleJobIdAsync(
        string worker,
        JobLane lane,
        DateTimeOffset now,
        CancellationToken ct)
    {
        var connection = _db.Database.GetDbConnection();
        await using var command = connection.CreateCommand();
        command.Transaction = _db.Database.CurrentTransaction!.GetDbTransaction();
        command.CommandText =
            """
            SELECT id
            FROM app.jobs
            WHERE lane = @lane
              AND status IN ('queued', 'retry_wait')
              AND run_after <= @now
              AND attempts < max_attempts
            ORDER BY priority ASC, run_after ASC, created_at ASC
            FOR UPDATE SKIP LOCKED
            LIMIT 1
            """;
        command.Parameters.Add(new NpgsqlParameter("lane", ToDbLane(lane)));
        command.Parameters.Add(new NpgsqlParameter("now", now));

        var value = await command.ExecuteScalarAsync(ct);
        return value is Guid id ? id : null;
    }

    private static void ValidateNewJob(NewJob job, string dedupeKey)
    {
        if (string.IsNullOrWhiteSpace(job.JobType) || job.JobType.Length > 64)
        {
            throw new ArgumentException(
                "A job type is required and must be 64 characters or fewer.",
                nameof(job));
        }

        if (string.IsNullOrWhiteSpace(dedupeKey) || dedupeKey.Length > 256)
        {
            throw new ArgumentException(
                "A dedupe key is required and must be 256 characters or fewer.",
                nameof(dedupeKey));
        }

        if (job.PayloadVersion <= 0 || job.MaxAttempts <= 0)
        {
            throw new ArgumentException(
                "Payload version and max attempts must be positive.",
                nameof(job));
        }

        if (job.Priority < 0)
        {
            throw new ArgumentException("Priority cannot be negative.", nameof(job));
        }
    }
}
