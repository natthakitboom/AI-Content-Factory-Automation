using System.Diagnostics;
using System.Net;
using System.Text.Json;
using ContentFactory.Contracts;
using ContentFactory.Core.Abstractions;
using ContentFactory.Core.Jobs;
using ContentFactory.Infrastructure.Jobs;
using ContentFactory.Infrastructure.Media;
using ContentFactory.Infrastructure.Persistence;
using ContentFactory.Infrastructure.Persistence.Entities;
using ContentFactory.IntegrationTests.Api;
using ContentFactory.Worker;
using ContentFactory.Worker.Handlers;
using ContentFactory.Worker.Health;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace ContentFactory.IntegrationTests;

/// <summary>
/// T07 recovery checks use real PostgreSQL and real local child processes. A fake step
/// proves finalize+outbox recovery; a local process proves lease loss cancels work rather
/// than committing a stale result.
/// </summary>
[Collection(ApiCollection.Name)]
public sealed class WorkerRecoveryTests
{
    private readonly ApiTestFixture _fixture;
    private readonly string _runId = Guid.NewGuid().ToString("N")[..8];

    public WorkerRecoveryTests(ApiTestFixture fixture)
    {
        _fixture = fixture;
    }

    [Fact]
    public async Task PausePreventsNewHeavyWork()
    {
        var client = await _fixture.CreateLoggedInClientAsync();
        var now = DateTimeOffset.UtcNow;
        var jobId = await SeedJobAsync(
            "plan_script",
            JobLane.HeavyLocal,
            "pause-heavy",
            payload: "{\"mode\":\"template_only\"}",
            runAfter: now.AddMinutes(-1));
        var runningCloudId = await SeedJobAsync(
            "poll_video",
            JobLane.CloudPoll,
            "pause-cloud",
            payload: "{\"mode\":\"hybrid_motion\"}",
            status: "running",
            attempts: 1,
            leaseOwner: "cloud-worker",
            leaseUntil: now.AddMinutes(5),
            fence: 1);

        try
        {
            var pause = await client.PostAsync("/api/v1/system/pause", content: null);
            Assert.Equal(HttpStatusCode.NoContent, pause.StatusCode);

            await using (var scope = _fixture.Factory.Services.CreateAsyncScope())
            {
                var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
                var store = new PostgresJobStore(db, TimeProvider.System);

                Assert.Null(await store.TryLeaseAsync(
                    "pause-worker",
                    JobLane.HeavyLocal,
                    CancellationToken.None));

                db.ChangeTracker.Clear();
                var cloud = await db.Jobs.AsNoTracking().SingleAsync(x => x.Id == runningCloudId);
                Assert.Equal("running", cloud.Status);
                Assert.Equal("cloud-worker", cloud.LeaseOwner);
            }

            var resume = await client.PostAsync("/api/v1/system/resume", content: null);
            Assert.Equal(HttpStatusCode.NoContent, resume.StatusCode);

            await using (var scope = _fixture.Factory.Services.CreateAsyncScope())
            {
                var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
                var store = new PostgresJobStore(db, TimeProvider.System);
                var leased = await store.TryLeaseAsync(
                    "pause-worker",
                    JobLane.HeavyLocal,
                    CancellationToken.None);

                Assert.NotNull(leased);
                Assert.Equal(jobId, leased!.Id);
            }
        }
        finally
        {
            // Never leave the shared fixture paused or with test jobs queued.
            await client.PostAsync("/api/v1/system/resume", content: null);
            await CleanupAsync([jobId, runningCloudId]);
        }
    }

    [Fact]
    public async Task SleepRecoveryDoesNotFanOut()
    {
        var jobId = await SeedJobAsync(
            "plan_script",
            JobLane.HeavyLocal,
            "sleep-recovery",
            payload: "{\"mode\":\"template_only\"}",
            runAfter: DateTimeOffset.UtcNow.AddMinutes(-1));
        Guid? outboxId = null;

        try
        {
            await using (var scope = _fixture.Factory.Services.CreateAsyncScope())
            {
                var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
                var store = new PostgresJobStore(db, TimeProvider.System);
                var governor = new ResourceGovernor();
                var dispatcher = new JobDispatcher(
                    store,
                    [new SucceedingHandler("plan_script")],
                    governor,
                    NullLogger<JobDispatcher>.Instance,
                    TimeProvider.System);

                var result = await dispatcher.RunOnceAsync(
                    "sleep-worker",
                    JobLane.HeavyLocal,
                    CancellationToken.None);

                Assert.True(result.Leased);
                Assert.True(result.Finished);
                Assert.Equal(jobId, result.JobId);

                db.ChangeTracker.Clear();
                var outbox = await db.OutboxEvents
                    .AsNoTracking()
                    .SingleAsync(x => x.AggregateId == jobId
                        && x.EventType == WorkflowDefinition.StageSucceededEvent);
                outboxId = outbox.Id;
                Assert.Null(outbox.PublishedAt);

                Assert.Equal(
                    0,
                    await db.Jobs.CountAsync(x =>
                        x.DedupeKey.StartsWith($"outbox:{outboxId:N}:")));
            }

            // The worker slept after finalize but before dispatching its outbox. A restart
            // dispatches the same event twice and must still create exactly one child.
            await using (var scope = _fixture.Factory.Services.CreateAsyncScope())
            {
                var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
                var store = new PostgresJobStore(db, TimeProvider.System);
                var outbox = new OutboxDispatcher(db, store, TimeProvider.System);
                var handler = new WorkflowOutboxHandler();

                Assert.Equal(
                    1,
                    await outbox.DispatchOnceAsync(handler, CancellationToken.None));
                Assert.Equal(
                    0,
                    await outbox.DispatchOnceAsync(handler, CancellationToken.None));

                db.ChangeTracker.Clear();
                var children = await db.Jobs
                    .AsNoTracking()
                    .Where(x => x.DedupeKey.StartsWith($"outbox:{outboxId:N}:"))
                    .ToListAsync();

                Assert.Single(children);
                Assert.Equal("prepare_visuals", children[0].JobType);
                Assert.Equal(JobLane.HeavyLocal, children[0].Lane);
                Assert.Equal("queued", children[0].Status);
                Assert.Equal("template_only", ReadMode(children[0].PayloadJson));
            }
        }
        finally
        {
            await CleanupAsync([jobId]);
        }
    }

    [Fact]
    public async Task LostLeaseCancelsLocalProcess()
    {
        var t0 = DateTimeOffset.UtcNow;
        var clock = new ManualTimeProvider(t0);
        var marker = Path.Combine(
            Path.GetTempPath(),
            $"contentfactory-t07-{Guid.NewGuid():N}.pid");
        var jobId = await SeedJobAsync(
            "render_video",
            JobLane.HeavyLocal,
            "lost-lease",
            payload: "{\"mode\":\"template_only\"}",
            runAfter: t0.AddMinutes(-1));

        try
        {
            await using var scope = _fixture.Factory.Services.CreateAsyncScope();
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var store = new PostgresJobStore(
                db,
                clock,
                new JobLeaseOptions
                {
                    LeaseDuration = TimeSpan.FromSeconds(120),
                    HeartbeatInterval = TimeSpan.FromMilliseconds(20),
                    WatchdogInterval = TimeSpan.FromSeconds(30)
                });
            var governor = new ResourceGovernor();
            var handler = new LocalProcessHandler(marker);
            var dispatcher = new JobDispatcher(
                store,
                [handler],
                governor,
                NullLogger<JobDispatcher>.Instance,
                TimeProvider.System,
                new JobDispatcherOptions
                {
                    HeartbeatInterval = TimeSpan.FromMilliseconds(20)
                });

            var dispatch = dispatcher.RunOnceAsync(
                "lost-lease-worker",
                JobLane.HeavyLocal,
                CancellationToken.None);

            var pid = await WaitForProcessIdAsync(marker, TimeSpan.FromSeconds(5));
            Assert.True(IsProcessAlive(pid), "The fake local process did not start.");

            // No database write races the store: advance the injected clock past the
            // lease, and the next heartbeat must reject the lease.
            clock.Advance(TimeSpan.FromSeconds(121));

            await handler.Cancelled.Task.WaitAsync(TimeSpan.FromSeconds(5));
            var result = await dispatch.WaitAsync(TimeSpan.FromSeconds(5));

            Assert.True(result.Leased);
            Assert.False(result.Finished);
            Assert.True(await WaitForProcessExitAsync(pid, TimeSpan.FromSeconds(5)));

            await using var verifyScope = _fixture.Factory.Services.CreateAsyncScope();
            var verifyDb = verifyScope.ServiceProvider.GetRequiredService<AppDbContext>();
            var row = await verifyDb.Jobs.AsNoTracking().SingleAsync(x => x.Id == jobId);
            Assert.Equal("running", row.Status);
            var attempt = await verifyDb.JobAttempts.AsNoTracking().SingleAsync(
                x => x.JobId == jobId && x.Fence == row.Fence);
            Assert.Null(attempt.EndedAt);
        }
        finally
        {
            await CleanupAsync([jobId]);
            TryDelete(marker);
        }
    }

    [Fact]
    public async Task GracefulShutdownStopsNewLeasesAndCancelsLocalProcess()
    {
        var marker = Path.Combine(
            Path.GetTempPath(),
            $"contentfactory-t07-shutdown-{Guid.NewGuid():N}.pid");
        var jobId = await SeedJobAsync(
            "render_video",
            JobLane.HeavyLocal,
            "shutdown",
            payload: "{\"mode\":\"template_only\"}",
            runAfter: DateTimeOffset.UtcNow.AddMinutes(-1));

        try
        {
            await using var scope = _fixture.Factory.Services.CreateAsyncScope();
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var store = new PostgresJobStore(db, TimeProvider.System);
            var governor = new ResourceGovernor();
            var handler = new LocalProcessHandler(marker);
            var dispatcher = new JobDispatcher(
                store,
                [handler],
                governor,
                NullLogger<JobDispatcher>.Instance,
                TimeProvider.System,
                new JobDispatcherOptions
                {
                    HeartbeatInterval = TimeSpan.FromMilliseconds(50)
                });

            using var shutdown = new CancellationTokenSource();
            var dispatch = dispatcher.RunOnceAsync(
                "shutdown-worker",
                JobLane.HeavyLocal,
                shutdown.Token);

            var pid = await WaitForProcessIdAsync(marker, TimeSpan.FromSeconds(5));
            shutdown.Cancel();

            await handler.Cancelled.Task.WaitAsync(TimeSpan.FromSeconds(5));
            var result = await dispatch.WaitAsync(TimeSpan.FromSeconds(5));

            Assert.True(result.Leased);
            Assert.False(result.Finished);
            Assert.True(await WaitForProcessExitAsync(pid, TimeSpan.FromSeconds(5)));

            await Assert.ThrowsAnyAsync<OperationCanceledException>(
                () => dispatcher.RunOnceAsync(
                    "shutdown-worker",
                    JobLane.HeavyLocal,
                    shutdown.Token));
        }
        finally
        {
            await CleanupAsync([jobId]);
            TryDelete(marker);
        }
    }

    [Fact]
    public async Task PollRescheduleDoesNotConsumeRetryLimit()
    {
        var jobId = await SeedJobAsync(
            "poll_video",
            JobLane.CloudPoll,
            "poll-reschedule",
            payload: "{\"mode\":\"hybrid_motion\"}",
            runAfter: DateTimeOffset.UtcNow.AddMinutes(-1),
            maxAttempts: 5);

        try
        {
            await using var scope = _fixture.Factory.Services.CreateAsyncScope();
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var store = new PostgresJobStore(db, TimeProvider.System);
            var governor = new ResourceGovernor();
            var dispatcher = new JobDispatcher(
                store,
                [new RetryWaitHandler()],
                governor,
                NullLogger<JobDispatcher>.Instance,
                TimeProvider.System);

            var before = await db.Jobs.AsNoTracking().SingleAsync(x => x.Id == jobId);
            var attemptsBeforePoll = before.Attempts;

            var result = await dispatcher.RunOnceAsync(
                "poll-worker",
                JobLane.CloudPoll,
                CancellationToken.None);

            Assert.True(result.Finished);
            db.ChangeTracker.Clear();
            var after = await db.Jobs.AsNoTracking().SingleAsync(x => x.Id == jobId);
            var attemptsAfterInProgressPoll = after.Attempts;

            Assert.Equal(attemptsBeforePoll, attemptsAfterInProgressPoll);
            Assert.Equal("retry_wait", after.Status);
            Assert.True(after.RunAfter > DateTimeOffset.UtcNow);
            Assert.Equal(5, after.MaxAttempts);

            var attempt = await db.JobAttempts.AsNoTracking().SingleAsync(
                x => x.JobId == jobId && x.Fence == after.Fence);
            Assert.Equal(1, attempt.AttemptNo);
            Assert.NotNull(attempt.EndedAt);
        }
        finally
        {
            await CleanupAsync([jobId]);
        }
    }

    private async Task<Guid> SeedJobAsync(
        string jobType,
        JobLane lane,
        string name,
        string payload = "{}",
        string status = "queued",
        int priority = 0,
        int maxAttempts = 5,
        DateTimeOffset? runAfter = null,
        int attempts = 0,
        string? leaseOwner = null,
        DateTimeOffset? leaseUntil = null,
        long fence = 0)
    {
        await using var scope = _fixture.Factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var now = DateTimeOffset.UtcNow;
        var id = Guid.NewGuid();

        db.Jobs.Add(new Job
        {
            Id = id,
            JobType = jobType,
            PayloadJson = payload,
            PayloadVersion = 1,
            DedupeKey = $"t07:{_runId}:{name}:{id:N}",
            Lane = lane,
            Status = status,
            Priority = priority,
            RunAfter = runAfter ?? now.AddMinutes(-1),
            Attempts = attempts,
            MaxAttempts = maxAttempts,
            LeaseOwner = leaseOwner,
            LeaseUntil = leaseUntil,
            Fence = fence,
            CreatedAt = now,
            UpdatedAt = now
        });

        await db.SaveChangesAsync();
        return id;
    }

    private async Task CleanupAsync(IReadOnlyCollection<Guid> jobIds)
    {
        if (jobIds.Count == 0)
        {
            return;
        }

        await using var scope = _fixture.Factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var ids = jobIds.ToHashSet();

        var eventIds = await db.OutboxEvents
            .Where(x => ids.Contains(x.AggregateId))
            .Select(x => x.Id)
            .ToListAsync();

        foreach (var eventId in eventIds)
        {
            var prefix = $"outbox:{eventId:N}:";
            var childIds = await db.Jobs
                .Where(x => x.DedupeKey.StartsWith(prefix))
                .Select(x => x.Id)
                .ToListAsync();
            foreach (var childId in childIds)
            {
                ids.Add(childId);
            }
        }

        await db.Jobs.Where(x => ids.Contains(x.Id)).ExecuteDeleteAsync();
        if (eventIds.Count > 0)
        {
            await db.OutboxEvents.Where(x => eventIds.Contains(x.Id)).ExecuteDeleteAsync();
        }
    }

    private static string? ReadMode(string payloadJson)
    {
        using var document = JsonDocument.Parse(payloadJson);
        return document.RootElement.TryGetProperty("mode", out var mode)
            ? mode.GetString()
            : null;
    }

    private static async Task<int> WaitForProcessIdAsync(string marker, TimeSpan timeout)
    {
        var deadline = DateTimeOffset.UtcNow.Add(timeout);
        while (DateTimeOffset.UtcNow < deadline)
        {
            if (File.Exists(marker))
            {
                var text = await File.ReadAllTextAsync(marker);
                if (int.TryParse(text.Trim(), out var pid) && pid > 0)
                {
                    return pid;
                }
            }

            await Task.Delay(25);
        }

        throw new TimeoutException("The local process did not write its PID file.");
    }

    private static bool IsProcessAlive(int pid)
    {
        try
        {
            using var process = Process.GetProcessById(pid);
            return !process.HasExited;
        }
        catch (ArgumentException)
        {
            return false;
        }
    }

    private static async Task<bool> WaitForProcessExitAsync(int pid, TimeSpan timeout)
    {
        var deadline = DateTimeOffset.UtcNow.Add(timeout);
        while (DateTimeOffset.UtcNow < deadline)
        {
            if (!IsProcessAlive(pid))
            {
                return true;
            }

            await Task.Delay(50);
        }

        return !IsProcessAlive(pid);
    }

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (IOException)
        {
            // Best-effort cleanup of a test marker.
        }
    }

    private sealed class SucceedingHandler(string jobType) : IJobHandler
    {
        public string JobType => jobType;

        public Task<JobOutcome> ExecuteAsync(LeasedJob job, CancellationToken ct)
            => Task.FromResult(new JobOutcome("succeeded", null, null, $"result-{job.Id:N}"));
    }

    private sealed class RetryWaitHandler : IJobHandler
    {
        public string JobType => "poll_video";

        public Task<JobOutcome> ExecuteAsync(LeasedJob job, CancellationToken ct)
            => Task.FromResult(new JobOutcome(
                "retry_wait",
                DateTimeOffset.UtcNow.AddSeconds(30),
                null,
                null));
    }

    private sealed class LocalProcessHandler : IJobHandler
    {
        private readonly string _marker;
        private readonly SafeProcessRunner _runner = new(new SafeProcessRunnerOptions
        {
            AllowedExecutables = ["/bin/sh"]
        });

        public LocalProcessHandler(string marker)
        {
            _marker = marker;
        }

        public string JobType => "render_video";

        public TaskCompletionSource Cancelled { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public async Task<JobOutcome> ExecuteAsync(LeasedJob job, CancellationToken ct)
        {
            try
            {
                await _runner.RunAsync(
                    "/bin/sh",
                    ["-c", $"printf '%s' $$ > '{_marker}'; exec sleep 30"],
                    TimeSpan.FromMinutes(1),
                    ct);

                return new JobOutcome("succeeded", null, null, "unexpected");
            }
            catch (OperationCanceledException)
            {
                Cancelled.TrySetResult();
                throw;
            }
        }
    }

    private sealed class ManualTimeProvider : TimeProvider
    {
        private DateTimeOffset _now;

        public ManualTimeProvider(DateTimeOffset now)
        {
            _now = now;
        }

        public override DateTimeOffset GetUtcNow() => _now;

        public void Advance(TimeSpan duration) => _now = _now.Add(duration);
    }
}
