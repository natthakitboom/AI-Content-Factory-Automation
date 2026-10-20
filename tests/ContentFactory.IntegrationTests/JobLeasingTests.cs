using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using ContentFactory.Contracts;
using ContentFactory.Infrastructure.Jobs;
using ContentFactory.Infrastructure.Persistence;
using ContentFactory.Infrastructure.Persistence.Entities;
using ContentFactory.IntegrationTests.Api;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace ContentFactory.IntegrationTests;

/// <summary>
/// T06 invariants are proven against real PostgreSQL: row locks, fence checks, lease expiry,
/// heartbeat renewal and outbox replay all depend on database transaction semantics.
/// In-memory providers cannot demonstrate any of them.
/// </summary>
[Collection(ApiCollection.Name)]
public sealed class JobLeasingTests
{
    private const string LeaseTestPrefix = "job-lease-test:";
    private readonly ApiTestFixture _fixture;
    private readonly string _runId = Guid.NewGuid().ToString("N")[..8];

    public JobLeasingTests(ApiTestFixture fixture)
    {
        _fixture = fixture;
    }

    [Fact]
    public async Task TwoWorkersNeverOwnSameFence()
    {
        const int jobCount = 100;
        var prefix = $"{LeaseTestPrefix}{_runId}:";
        await SeedQueuedJobsAsync(prefix, jobCount, JobLane.Download);

        var leased = new ConcurrentBag<LeasedJob>();
        var finished = new ConcurrentBag<Guid>();
        var start = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        async Task WorkAsync(string worker)
        {
            await using var scope = _fixture.Factory.Services.CreateAsyncScope();
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var store = new PostgresJobStore(db, TimeProvider.System);

            await start.Task;
            while (true)
            {
                var job = await store.TryLeaseAsync(worker, JobLane.Download, CancellationToken.None);
                if (job is null)
                {
                    break;
                }

                leased.Add(job);
                var finalized = await store.FinishAsync(
                    job,
                    new JobOutcome("succeeded", null, null, $"result-{job.Id:N}"),
                    CancellationToken.None);

                Assert.True(finalized, $"Worker {worker} lost its own fence for {job.Id}.");
                finished.Add(job.Id);
            }
        }

        var workers = new[] { WorkAsync("worker-1"), WorkAsync("worker-2") };
        start.SetResult();
        await Task.WhenAll(workers);

        Assert.Equal(jobCount, leased.Count);
        Assert.Equal(jobCount, leased.Select(x => x.Id).Distinct().Count());
        Assert.Equal(jobCount, leased.Select(x => (x.Id, x.Fence)).Distinct().Count());
        Assert.Equal(jobCount, finished.Count);
        Assert.Equal(jobCount, finished.Distinct().Count());
        Assert.All(leased, job => Assert.True(job.Fence > 0));

        await using var finalScope = _fixture.Factory.Services.CreateAsyncScope();
        var finalDb = finalScope.ServiceProvider.GetRequiredService<AppDbContext>();
        var rows = await finalDb.Jobs
            .AsNoTracking()
            .Where(x => x.DedupeKey.StartsWith(prefix))
            .ToListAsync();

        Assert.Equal(jobCount, rows.Count);
        Assert.All(rows, row =>
        {
            Assert.Equal("succeeded", row.Status);
            Assert.Equal(1, row.Attempts);
            Assert.Equal(1, row.Fence);
            Assert.Null(row.LeaseOwner);
            Assert.Null(row.LeaseUntil);
        });

        var ids = rows.Select(x => x.Id).ToArray();
        Assert.Equal(
            jobCount,
            await finalDb.JobAttempts.CountAsync(
                x => ids.Contains(x.JobId) && x.EndedAt != null && x.ErrorCode == null));
    }

    [Fact]
    public async Task ExpiredWorkerCannotFinalize()
    {
        var t0 = new DateTimeOffset(2026, 10, 20, 16, 0, 0, TimeSpan.Zero);
        var clock = new ManualTimeProvider(t0);
        var prefix = $"{LeaseTestPrefix}{_runId}:expired";
        await SeedQueuedJobsAsync(prefix, 1, JobLane.Download, t0.AddMinutes(-1));

        await using (var scope = _fixture.Factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var store = new PostgresJobStore(db, clock);
            var expired = await store.TryLeaseAsync("old-worker", JobLane.Download, CancellationToken.None);
            Assert.NotNull(expired);

            var row = await db.Jobs.SingleAsync(x => x.Id == expired!.Id);
            row.LeaseUntil = t0.AddSeconds(-1);
            await db.SaveChangesAsync();
            db.ChangeTracker.Clear();

            Assert.False(await store.RenewAsync(expired!, CancellationToken.None));
            Assert.False(await store.FinishAsync(
                expired!,
                new JobOutcome("succeeded", null, null, "stale-output"),
                CancellationToken.None));

            var watchdog = new LeaseWatchdog(db, clock);
            Assert.Equal(1, await watchdog.SweepAsync(CancellationToken.None));

            db.ChangeTracker.Clear();
            var reclaimed = await db.Jobs.AsNoTracking().SingleAsync(x => x.Id == expired!.Id);
            Assert.Equal("retry_wait", reclaimed.Status);
            Assert.Null(reclaimed.LeaseOwner);
            Assert.Null(reclaimed.LeaseUntil);

            Assert.False(await store.FinishAsync(
                expired!,
                new JobOutcome("succeeded", null, null, "stale-output"),
                CancellationToken.None));

            var next = await store.TryLeaseAsync("new-worker", JobLane.Download, CancellationToken.None);
            Assert.NotNull(next);
            Assert.Equal(expired!.Id, next!.Id);
            Assert.True(next.Fence > expired.Fence);

            Assert.False(await store.FinishAsync(
                expired,
                new JobOutcome("succeeded", null, null, "stale-output"),
                CancellationToken.None));

            Assert.True(await store.FinishAsync(
                next,
                new JobOutcome("succeeded", null, null, "new-output"),
                CancellationToken.None));
        }

        await using var finalScope = _fixture.Factory.Services.CreateAsyncScope();
        var finalDb = finalScope.ServiceProvider.GetRequiredService<AppDbContext>();
        var finalRow = await finalDb.Jobs.AsNoTracking().SingleAsync(x => x.DedupeKey == $"{prefix}:0000");
        Assert.Equal("succeeded", finalRow.Status);
        Assert.Equal(2, finalRow.Fence);
        Assert.Equal(2, finalRow.Attempts);
    }

    [Fact]
    public async Task UnknownProviderSubmissionBlocksRequeue()
    {
        var t0 = new DateTimeOffset(2026, 10, 20, 16, 15, 0, TimeSpan.Zero);
        var clock = new ManualTimeProvider(t0);
        var prefix = $"{LeaseTestPrefix}{_runId}:provider-unknown";
        await SeedQueuedJobsAsync(prefix, 1, JobLane.Download, t0.AddMinutes(-1));

        await using var scope = _fixture.Factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var store = new PostgresJobStore(db, clock);
        var leased = await store.TryLeaseAsync("cloud-worker", JobLane.Download, CancellationToken.None);
        Assert.NotNull(leased);

        var job = await db.Jobs.SingleAsync(x => x.Id == leased!.Id);
        job.LeaseUntil = t0.AddSeconds(-1);
        db.ProviderRequests.Add(new ProviderRequest
        {
            Id = Guid.NewGuid(),
            JobId = job.Id,
            Provider = "fake-video",
            Model = "fake-model",
            ClientOperationId = $"unknown-{_runId}",
            RequestHash = new string('a', 64),
            Status = "submission_unknown",
            CreatedAt = t0.AddSeconds(-10)
        });
        await db.SaveChangesAsync();

        var watchdog = new LeaseWatchdog(db, clock);
        Assert.Equal(1, await watchdog.SweepAsync(CancellationToken.None));

        db.ChangeTracker.Clear();
        var blocked = await db.Jobs.AsNoTracking().SingleAsync(x => x.Id == leased.Id);
        Assert.Equal("blocked", blocked.Status);
        Assert.Equal("PROVIDER_STATE_UNCERTAIN", blocked.LastErrorCode);
        Assert.Null(blocked.LeaseOwner);
        Assert.Null(blocked.LeaseUntil);

        Assert.Null(await store.TryLeaseAsync("retry-worker", JobLane.Download, CancellationToken.None));

        var attempt = await db.JobAttempts.AsNoTracking().SingleAsync(
            x => x.JobId == leased.Id && x.Fence == leased.Fence);
        Assert.NotNull(attempt.EndedAt);
        Assert.Equal("PROVIDER_STATE_UNCERTAIN", attempt.ErrorCode);
    }

    [Fact]
    public async Task RetryWaitRespectsNextRunAt()
    {
        var t0 = new DateTimeOffset(2026, 10, 20, 16, 20, 0, TimeSpan.Zero);
        var clock = new ManualTimeProvider(t0);
        var prefix = $"{LeaseTestPrefix}{_runId}:retry";
        await SeedQueuedJobsAsync(prefix, 1, JobLane.Download, t0.AddMinutes(-1));

        await using var scope = _fixture.Factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var store = new PostgresJobStore(db, clock);

        var first = await store.TryLeaseAsync("retry-worker", JobLane.Download, CancellationToken.None);
        Assert.NotNull(first);

        Assert.True(await store.FinishAsync(
            first!,
            new JobOutcome("retry_wait", t0.AddSeconds(30), "TRANSIENT", null),
            CancellationToken.None));

        db.ChangeTracker.Clear();
        var waiting = await db.Jobs.AsNoTracking().SingleAsync(x => x.Id == first.Id);
        Assert.Equal("retry_wait", waiting.Status);
        Assert.Equal(t0.AddSeconds(30), waiting.RunAfter);
        Assert.Equal(1, waiting.Attempts);

        Assert.Null(await store.TryLeaseAsync("retry-worker", JobLane.Download, CancellationToken.None));

        clock.Advance(TimeSpan.FromSeconds(31));
        var second = await store.TryLeaseAsync("retry-worker", JobLane.Download, CancellationToken.None);
        Assert.NotNull(second);
        Assert.Equal(first.Id, second!.Id);
        Assert.Equal(2, second.Fence);
        Assert.True(second.Fence > first.Fence);
    }

    [Fact]
    public async Task HeartbeatExtendsLease()
    {
        var t0 = new DateTimeOffset(2026, 10, 20, 16, 30, 0, TimeSpan.Zero);
        var clock = new ManualTimeProvider(t0);
        var prefix = $"{LeaseTestPrefix}{_runId}:heartbeat";
        await SeedQueuedJobsAsync(prefix, 1, JobLane.Download, t0.AddMinutes(-1));

        await using var scope = _fixture.Factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var store = new PostgresJobStore(
            db,
            clock,
            new JobLeaseOptions { LeaseDuration = TimeSpan.FromSeconds(120) });

        var job = await store.TryLeaseAsync("heartbeat-worker", JobLane.Download, CancellationToken.None);
        Assert.NotNull(job);
        Assert.Equal(t0.AddSeconds(120), job!.LeaseUntil);

        clock.Advance(TimeSpan.FromSeconds(20));
        Assert.True(await store.RenewAsync(job, CancellationToken.None));

        var extended = await db.Jobs.AsNoTracking().SingleAsync(x => x.Id == job.Id);
        Assert.Equal(t0.AddSeconds(140), extended.LeaseUntil);
        Assert.True(extended.LeaseUntil > job.LeaseUntil);

        clock.Advance(TimeSpan.FromSeconds(121));
        Assert.False(await store.RenewAsync(job, CancellationToken.None));
    }

    [Fact]
    public async Task OutboxReplayCreatesOneNextJob()
    {
        var now = DateTimeOffset.UtcNow;
        Guid eventId;

        await using (var scope = _fixture.Factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var outbox = new OutboxEvent
            {
                Id = Guid.NewGuid(),
                EventType = "fake_step_succeeded",
                AggregateId = Guid.NewGuid(),
                PayloadJson = "{\"revisionId\":\"11111111-1111-1111-1111-111111111111\"}",
                CreatedAt = now
            };
            db.OutboxEvents.Add(outbox);
            await db.SaveChangesAsync();
            eventId = outbox.Id;
        }

        var handler = new FakeOutboxHandler();
        await using (var scope = _fixture.Factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var store = new PostgresJobStore(db, TimeProvider.System);
            var dispatcher = new OutboxDispatcher(db, store, TimeProvider.System);

            Assert.Equal(1, await dispatcher.DispatchOnceAsync(handler, CancellationToken.None));

            var published = await db.OutboxEvents.AsNoTracking().SingleAsync(x => x.Id == eventId);
            Assert.NotNull(published.PublishedAt);
        }

        await using (var scope = _fixture.Factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var store = new PostgresJobStore(db, TimeProvider.System);
            var dispatcher = new OutboxDispatcher(db, store, TimeProvider.System);

            // Simulate a lost published marker after the downstream job committed.
            var outbox = await db.OutboxEvents.SingleAsync(x => x.Id == eventId);
            outbox.PublishedAt = null;
            await db.SaveChangesAsync();

            Assert.Equal(0, await dispatcher.DispatchOnceAsync(handler, CancellationToken.None));
        }

        await using (var scope = _fixture.Factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var jobs = await db.Jobs
                .AsNoTracking()
                .Where(x => x.DedupeKey.StartsWith($"outbox:{eventId:N}:"))
                .ToListAsync();

            Assert.Single(jobs);
            Assert.Equal("next_fake_step", jobs[0].JobType);
            Assert.Equal("queued", jobs[0].Status);
            Assert.Equal(JobLane.Control, jobs[0].Lane);
        }
    }

    [Fact]
    public async Task RollbackCreatesNeitherRevisionNorJob()
    {
        var ids = await SeedCampaignAsync();
        var client = await _fixture.CreateLoggedInClientAsync();
        var idempotencyKey = $"rollback-run-key-{_runId}";

        await using (var scope = _fixture.Factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            await db.Database.ExecuteSqlRawAsync(
                """
                CREATE OR REPLACE FUNCTION app.cf_test_fail_job() RETURNS trigger
                LANGUAGE plpgsql AS $$
                BEGIN
                    IF NEW.dedupe_key LIKE '%rollback-run-key-%' THEN
                        RAISE EXCEPTION 'forced job insert failure';
                    END IF;
                    RETURN NEW;
                END;
                $$;
                """);
            await db.Database.ExecuteSqlRawAsync(
                """
                DROP TRIGGER IF EXISTS cf_test_fail_job ON app.jobs;
                CREATE TRIGGER cf_test_fail_job
                    BEFORE INSERT ON app.jobs
                    FOR EACH ROW EXECUTE FUNCTION app.cf_test_fail_job();
                """);
        }

        try
        {
            using var request = new HttpRequestMessage(
                HttpMethod.Post,
                $"/api/v1/campaigns/{ids.CampaignId}/runs")
            {
                Content = JsonContent.Create(new
                {
                    mode = "template_only",
                    targetDurationMs = 25000,
                    autoGenerateVoice = false,
                    cloudVideoSceneLimit = 0
                })
            };
            request.Headers.Add("Idempotency-Key", idempotencyKey);

            var response = await client.SendAsync(request);
            Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
        }
        finally
        {
            await using var scope = _fixture.Factory.Services.CreateAsyncScope();
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            await db.Database.ExecuteSqlRawAsync(
                "DROP TRIGGER IF EXISTS cf_test_fail_job ON app.jobs;");
            await db.Database.ExecuteSqlRawAsync(
                "DROP FUNCTION IF EXISTS app.cf_test_fail_job();");
        }

        await using (var scope = _fixture.Factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            Assert.Equal(
                0,
                await db.ContentRevisions.CountAsync(x => x.CampaignId == ids.CampaignId));

            var dedupePrefix = $"campaign-run:{ids.CampaignId:N}:";
            Assert.Equal(
                0,
                await db.Jobs.CountAsync(x => x.DedupeKey.StartsWith(dedupePrefix)));
        }
    }

    private async Task SeedQueuedJobsAsync(
        string prefix,
        int count,
        JobLane lane,
        DateTimeOffset? runAfter = null)
    {
        await using var scope = _fixture.Factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var now = DateTimeOffset.UtcNow;
        var start = runAfter ?? now.AddMinutes(-1);

        for (var index = 0; index < count; index++)
        {
            db.Jobs.Add(new Job
            {
                Id = Guid.NewGuid(),
                JobType = "fake_download",
                PayloadJson = "{}",
                PayloadVersion = 1,
                DedupeKey = $"{prefix}:{index:D4}",
                Lane = lane,
                Status = "queued",
                Priority = 100,
                RunAfter = start,
                Attempts = 0,
                MaxAttempts = 5,
                Fence = 0,
                CreatedAt = now.AddSeconds(index),
                UpdatedAt = now.AddSeconds(index)
            });
        }

        await db.SaveChangesAsync();
    }

    private async Task<(Guid CampaignId, Guid ProductId)> SeedCampaignAsync()
    {
        await using var scope = _fixture.Factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var now = DateTimeOffset.UtcNow;

        var product = new Product
        {
            Id = Guid.NewGuid(),
            Title = "สินค้าทดสอบ rollback " + _runId,
            Market = "TH",
            Locale = "th-TH",
            Version = 1,
            CreatedAt = now,
            UpdatedAt = now
        };
        var claim = new ProductClaim
        {
            Id = Guid.NewGuid(),
            ProductId = product.Id,
            Text = "ข้อความที่อนุมัติแล้ว",
            SourceReference = "owner-sheet-rollback",
            Status = "approved",
            VerifiedAt = now,
            CreatedAt = now
        };
        var channel = new Channel
        {
            Id = Guid.NewGuid(),
            Platform = "tiktok",
            DisplayName = "rollback channel",
            Market = "TH",
            Locale = "th-TH",
            Timezone = "Asia/Bangkok",
            Status = "active",
            CreatedAt = now
        };
        var campaign = new Campaign
        {
            Id = Guid.NewGuid(),
            ProductId = product.Id,
            ChannelId = channel.Id,
            Mode = VideoMode.TemplateOnly,
            Locale = "th-TH",
            TargetDurationMs = 25000,
            BudgetUsd = 5m,
            Status = "draft",
            Version = 1,
            CreatedAt = now,
            UpdatedAt = now
        };

        db.Products.Add(product);
        db.ProductClaims.Add(claim);
        db.Channels.Add(channel);
        db.Campaigns.Add(campaign);
        await db.SaveChangesAsync();

        return (campaign.Id, product.Id);
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

    private sealed class FakeOutboxHandler : IOutboxHandler
    {
        public string EventType => "fake_step_succeeded";

        public Task<IReadOnlyList<NewJob>> HandleAsync(
            OutboxMessage message,
            CancellationToken ct)
        {
            Assert.Equal("fake_step_succeeded", message.EventType);
            Assert.Contains("revisionId", message.PayloadJson, StringComparison.Ordinal);

            IReadOnlyList<NewJob> jobs =
            [
                new NewJob(
                    "next_fake_step",
                    $"{{\"sourceEventId\":\"{message.Id:N}\"}}",
                    JobLane.Control)
            ];
            return Task.FromResult(jobs);
        }
    }
}
