using System.Text.Json;
using ContentFactory.Contracts;
using ContentFactory.Core.Abstractions;
using ContentFactory.Core.Exceptions;
using ContentFactory.Core.Jobs;
using ContentFactory.Worker.Health;
using Microsoft.Extensions.Logging;

namespace ContentFactory.Worker;

public sealed record JobDispatcherOptions
{
    public TimeSpan HeartbeatInterval { get; init; } = TimeSpan.FromSeconds(20);
}

public sealed record JobDispatchResult(
    bool Leased,
    bool Finished,
    Guid? JobId,
    string? JobType)
{
    public static JobDispatchResult NoJob { get; } = new(false, false, null, null);
}

/// <summary>
/// Leases one job in one lane, runs the matching local handler under a heartbeat, and
/// finalizes only while the fence and lease still belong to this worker. A failed
/// heartbeat cancels the handler and its child processes; it never commits stale output.
/// </summary>
public sealed class JobDispatcher
{
    private readonly IJobStore _store;
    private readonly IReadOnlyDictionary<string, IJobHandler> _handlers;
    private readonly ResourceGovernor _governor;
    private readonly ILogger<JobDispatcher> _logger;
    private readonly TimeProvider _clock;
    private readonly JobDispatcherOptions _options;

    public JobDispatcher(
        IJobStore store,
        IEnumerable<IJobHandler> handlers,
        ResourceGovernor governor,
        ILogger<JobDispatcher> logger,
        TimeProvider clock,
        JobDispatcherOptions? options = null)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
        ArgumentNullException.ThrowIfNull(handlers);
        _governor = governor ?? throw new ArgumentNullException(nameof(governor));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _clock = clock ?? throw new ArgumentNullException(nameof(clock));
        _options = options ?? new JobDispatcherOptions();

        if (_options.HeartbeatInterval <= TimeSpan.Zero)
        {
            throw new ArgumentException(
                "The heartbeat interval must be positive.",
                nameof(options));
        }

        var byType = new Dictionary<string, IJobHandler>(StringComparer.Ordinal);
        foreach (var handler in handlers)
        {
            if (string.IsNullOrWhiteSpace(handler.JobType))
            {
                throw new ArgumentException("Every handler needs a job type.", nameof(handlers));
            }

            if (!byType.TryAdd(handler.JobType, handler))
            {
                throw new ArgumentException(
                    $"Duplicate handler for job type '{handler.JobType}'.",
                    nameof(handlers));
            }
        }

        _handlers = byType;
    }

    public async Task<JobDispatchResult> RunOnceAsync(
        string worker,
        JobLane lane,
        CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();

        await using var laneLease = await _governor.AcquireAsync(lane, ct);
        var job = await _store.TryLeaseAsync(worker, lane, ct);
        if (job is null)
        {
            return JobDispatchResult.NoJob;
        }

        if (!_handlers.TryGetValue(job.JobType, out var handler))
        {
            var blocked = await _store.FinishAsync(
                job,
                new JobOutcome("blocked", null, "HANDLER_NOT_FOUND", null),
                ct);
            return new JobDispatchResult(true, blocked, job.Id, job.JobType);
        }

        using var jobCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var leaseLost = 0;
        var heartbeat = HeartbeatAsync(
            job,
            jobCts,
            () => Interlocked.Exchange(ref leaseLost, 1));

        try
        {
            var outcome = await handler.ExecuteAsync(job, jobCts.Token);

            // Stop renewing before touching the same DbContext for finalize. A heartbeat
            // racing the finalize update would be an invalid concurrent EF operation.
            jobCts.Cancel();
            await AwaitHeartbeatAsync(heartbeat);

            if (Volatile.Read(ref leaseLost) == 1 || ct.IsCancellationRequested)
            {
                return new JobDispatchResult(true, false, job.Id, job.JobType);
            }

            var finished = await FinalizeAsync(job, outcome, ct);
            return new JobDispatchResult(true, finished, job.Id, job.JobType);
        }
        catch (OperationCanceledException) when (jobCts.IsCancellationRequested)
        {
            // Lease loss or host shutdown: do not write a result under a stale fence.
            return new JobDispatchResult(true, false, job.Id, job.JobType);
        }
        catch (ValidationException exception)
        {
            if (Volatile.Read(ref leaseLost) == 0 && !ct.IsCancellationRequested)
            {
                await TryFinalizeFailureAsync(job, exception.Code, ct);
            }

            return new JobDispatchResult(true, false, job.Id, job.JobType);
        }
        catch (Exception exception)
        {
            _logger.LogError(
                exception,
                "Local handler failed for job {JobId} ({JobType}).",
                job.Id,
                job.JobType);

            if (Volatile.Read(ref leaseLost) == 0 && !ct.IsCancellationRequested)
            {
                await TryFinalizeFailureAsync(job, "HANDLER_FAILED", ct);
            }

            return new JobDispatchResult(true, false, job.Id, job.JobType);
        }
        finally
        {
            jobCts.Cancel();
            await AwaitHeartbeatAsync(heartbeat);
        }
    }

    private static async Task AwaitHeartbeatAsync(Task heartbeat)
    {
        try
        {
            await heartbeat;
        }
        catch (OperationCanceledException)
        {
            // Expected when the job finished or was cancelled.
        }
    }

    private async Task<bool> FinalizeAsync(
        LeasedJob job,
        JobOutcome outcome,
        CancellationToken ct)
    {
        if (outcome.Status == "succeeded"
            && _store is IJobOutboxWriter writer
            && WorkflowDefinition.TryReadMode(job.PayloadJson) is { } mode)
        {
            var nextJobType = WorkflowDefinition.NextJobType(job.JobType, mode);
            if (nextJobType is not null)
            {
                var draft = new OutboxEventDraft(
                    WorkflowDefinition.StageSucceededEvent,
                    job.Id,
                    JsonSerializer.Serialize(
                        new
                        {
                            jobId = job.Id,
                            jobType = job.JobType,
                            mode = WorkflowDefinition.ToDbMode(mode),
                            nextJobType,
                            notRequiredStages = WorkflowDefinition.NotRequiredStagesFor(mode),
                            jobPayload = outcome.NextPayloadJson ?? job.PayloadJson
                        },
                        JsonDefaults.Options));

                return await writer.FinishWithOutboxAsync(job, outcome, draft, ct);
            }
        }

        return await _store.FinishAsync(job, outcome, ct);
    }

    private async Task TryFinalizeFailureAsync(
        LeasedJob job,
        string errorCode,
        CancellationToken ct)
    {
        try
        {
            await _store.FinishAsync(
                job,
                new JobOutcome("failed", null, errorCode, null),
                ct);
        }
        catch (Exception exception)
        {
            _logger.LogError(
                exception,
                "Could not finalize failed job {JobId} ({JobType}).",
                job.Id,
                job.JobType);
        }
    }

    private async Task HeartbeatAsync(
        LeasedJob job,
        CancellationTokenSource jobCts,
        Action onLeaseLost)
    {
        try
        {
            while (!jobCts.IsCancellationRequested)
            {
                await Task.Delay(_options.HeartbeatInterval, _clock, jobCts.Token);
                if (jobCts.IsCancellationRequested)
                {
                    return;
                }

                bool renewed;
                try
                {
                    renewed = await _store.RenewAsync(job, jobCts.Token);
                }
                catch (OperationCanceledException) when (jobCts.IsCancellationRequested)
                {
                    return;
                }

                if (!renewed)
                {
                    onLeaseLost();
                    jobCts.Cancel();
                    return;
                }
            }
        }
        catch (OperationCanceledException) when (jobCts.IsCancellationRequested)
        {
            // Normal shutdown or lease loss.
        }
    }
}
