using ContentFactory.Contracts;
using ContentFactory.Infrastructure.Jobs;
using ContentFactory.Infrastructure.Operations;
using ContentFactory.Infrastructure.Storage;
using ContentFactory.Infrastructure.Persistence;
using ContentFactory.Worker.Handlers;
using ContentFactory.Worker.Health;
using Microsoft.Extensions.DependencyInjection;

namespace ContentFactory.Worker;

/// <summary>
/// One worker process polls independent lanes. A cloud lane never consumes the
/// heavy-local semaphore. Shutdown stops taking new leases and cancels the current local
/// handler; the host waits up to 30 seconds for process cleanup before exiting.
/// </summary>
public sealed class Worker(
    IServiceScopeFactory scopeFactory,
    ResourceGovernor governor,
    TimeProvider clock,
    ILogger<Worker> logger) : BackgroundService
{
    private static readonly JobLane[] LaneOrder =
    [
        JobLane.Control,
        JobLane.HeavyLocal,
        JobLane.CloudPoll,
        JobLane.Download,
        JobLane.CloudSubmit
    ];

    private readonly string _workerName =
        $"worker-{Environment.MachineName}-{Environment.ProcessId}";

    private readonly bool _schedulesEnabled = string.Equals(
        Environment.GetEnvironmentVariable("ENABLE_SCHEDULES"), "true", StringComparison.OrdinalIgnoreCase);

    private DateTimeOffset _nextWatchdog = DateTimeOffset.MinValue;
    private DateTimeOffset _nextScheduleTick = DateTimeOffset.MinValue;
    private DateTimeOffset _nextHeartbeat = DateTimeOffset.MinValue;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            var didWork = false;

            try
            {
                await using var scope = scopeFactory.CreateAsyncScope();
                var dispatcher = scope.ServiceProvider.GetRequiredService<JobDispatcher>();
                var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

                var now = clock.GetUtcNow();
                if (now >= _nextWatchdog)
                {
                    var watchdog = scope.ServiceProvider.GetRequiredService<LeaseWatchdog>();
                    didWork |= await watchdog.SweepAsync(stoppingToken) > 0;
                    _nextWatchdog = now.AddSeconds(30);
                }

                if (_schedulesEnabled && now >= _nextScheduleTick)
                {
                    // One tick job per minute; the dedupe key makes a restart harmless.
                    var jobs = scope.ServiceProvider.GetRequiredService<PostgresJobStore>();
                    await jobs.EnqueueAsync(
                        new NewJob("schedule_tick", "{}", JobLane.Control),
                        "schedule-tick:" + now.UtcDateTime.ToString(
                            "yyyyMMddHHmm", System.Globalization.CultureInfo.InvariantCulture),
                        stoppingToken);
                    _nextScheduleTick = now.AddMinutes(1);
                }

                var flags = scope.ServiceProvider.GetRequiredService<SystemFlagStore>();
                if (now >= _nextHeartbeat)
                {
                    await flags.SetAsync(
                        OperationsService.HeartbeatKey,
                        now.ToString("O", System.Globalization.CultureInfo.InvariantCulture));
                    _nextHeartbeat = now.AddSeconds(15);
                }

                var queuePaused = await flags.GetAsync(OperationsService.QueuePausedKey) == "true";

                foreach (var lane in LaneOrder)
                {
                    if (stoppingToken.IsCancellationRequested)
                    {
                        break;
                    }

                    if (queuePaused && lane is JobLane.HeavyLocal or JobLane.CloudSubmit)
                    {
                        // Pause withholds new heavy and paid work; running cloud requests continue.
                        continue;
                    }

                    if (lane is JobLane.CloudSubmit or JobLane.CloudPoll
                        && await governor.CountCloudInFlightAsync(db, stoppingToken)
                            >= governor.Options.MaxCloudInFlight)
                    {
                        continue;
                    }

                    var result = await dispatcher.RunOnceAsync(
                        _workerName,
                        lane,
                        stoppingToken);

                    didWork |= result.Leased;
                    if (result.Leased && !result.Finished)
                    {
                        // Lease loss or shutdown: do not take another job in this pass.
                        break;
                    }
                }

                if (!stoppingToken.IsCancellationRequested)
                {
                    var outbox = scope.ServiceProvider.GetRequiredService<OutboxDispatcher>();
                    var handler = scope.ServiceProvider.GetRequiredService<WorkflowOutboxHandler>();

                    for (var index = 0; index < 20; index++)
                    {
                        if (await outbox.DispatchOnceAsync(handler, stoppingToken) == 0)
                        {
                            break;
                        }

                        didWork = true;
                    }
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception exception)
            {
                logger.LogError(exception, "Worker loop failed; retrying after a short delay.");
            }

            if (!didWork)
            {
                await Task.Delay(TimeSpan.FromMilliseconds(500), clock, stoppingToken);
            }
        }
    }
}
