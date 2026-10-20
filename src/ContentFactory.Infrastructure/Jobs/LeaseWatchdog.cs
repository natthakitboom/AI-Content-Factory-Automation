using System.Data.Common;
using ContentFactory.Infrastructure.Persistence;
using ContentFactory.Infrastructure.Persistence.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Npgsql;

namespace ContentFactory.Infrastructure.Jobs;

/// <summary>
/// Reclaims running jobs whose lease expired. Pure/local work returns to
/// <c>retry_wait</c>; any job with a provider request is blocked for reconciliation
/// instead of being submitted again, because an uncertain provider call is never safe to
/// repeat blindly.
/// </summary>
public sealed class LeaseWatchdog
{
    private readonly AppDbContext _db;
    private readonly TimeProvider _clock;
    private readonly JobLeaseOptions _options;

    public LeaseWatchdog(AppDbContext db, TimeProvider clock, JobLeaseOptions? options = null)
    {
        _db = db;
        _clock = clock;
        _options = options ?? new JobLeaseOptions();
        PostgresJobStore.ValidateOptions(_options);
    }

    public async Task<int> SweepAsync(CancellationToken ct)
    {
        var now = _clock.GetUtcNow();
        await using var transaction = await _db.Database.BeginTransactionAsync(ct);

        var ids = await SelectExpiredJobIdsAsync(now, ct);
        if (ids.Count == 0)
        {
            await transaction.RollbackAsync(ct);
            return 0;
        }

        var idSet = ids.ToHashSet();
        var jobs = await _db.Jobs
            .Where(x => idSet.Contains(x.Id) && x.Status == "running")
            .ToListAsync(ct);

        var providerJobIds = await _db.ProviderRequests
            .AsNoTracking()
            .Where(x => x.JobId != null && idSet.Contains(x.JobId.Value))
            .Select(x => x.JobId!.Value)
            .Distinct()
            .ToListAsync(ct);

        var providerSet = providerJobIds.ToHashSet();

        foreach (var job in jobs)
        {
            var uncertainProvider = providerSet.Contains(job.Id);
            var exhausted = !uncertainProvider && job.Attempts >= job.MaxAttempts;
            var errorCode = uncertainProvider
                ? "PROVIDER_STATE_UNCERTAIN"
                : exhausted
                    ? "ATTEMPTS_EXHAUSTED"
                    : "LEASE_EXPIRED";
            var previousOwner = job.LeaseOwner;

            job.Status = uncertainProvider
                ? "blocked"
                : exhausted
                    ? "failed"
                    : "retry_wait";
            job.RunAfter = now;
            job.LastErrorCode = errorCode;
            job.LeaseOwner = null;
            job.LeaseUntil = null;
            job.UpdatedAt = now;

            _db.JobEvents.Add(PostgresJobStore.NewEvent(
                job.Id,
                uncertainProvider ? "submission_reconciliation_required" : "lease_reclaimed",
                new
                {
                    previousOwner,
                    fence = job.Fence,
                    reason = errorCode
                },
                now));

            await _db.JobAttempts
                .Where(x => x.JobId == job.Id
                    && x.Fence == job.Fence
                    && x.EndedAt == null)
                .ExecuteUpdateAsync(
                    setters => setters
                        .SetProperty(x => x.EndedAt, now)
                        .SetProperty(x => x.ErrorCode, errorCode),
                    ct);
        }

        await _db.SaveChangesAsync(ct);
        await transaction.CommitAsync(ct);
        return jobs.Count;
    }

    public async Task RunAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            await SweepAsync(ct);
            await Task.Delay(_options.WatchdogInterval, _clock, ct);
        }
    }

    private async Task<IReadOnlyList<Guid>> SelectExpiredJobIdsAsync(
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
            WHERE status = 'running'
              AND lease_until IS NOT NULL
              AND lease_until <= @now
            ORDER BY lease_until ASC, id ASC
            FOR UPDATE SKIP LOCKED
            LIMIT 100
            """;
        command.Parameters.Add(new NpgsqlParameter("now", now));

        var ids = new List<Guid>();
        await using var reader = await command.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            ids.Add(reader.GetGuid(0));
        }

        return ids;
    }
}
