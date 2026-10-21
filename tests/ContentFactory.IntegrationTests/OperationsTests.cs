using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using ContentFactory.Contracts;
using ContentFactory.Infrastructure.Billing;
using ContentFactory.Infrastructure.Operations;
using ContentFactory.Infrastructure.Persistence;
using ContentFactory.Infrastructure.Persistence.Entities;
using ContentFactory.IntegrationTests.Api;
using ContentFactory.IntegrationTests.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace ContentFactory.IntegrationTests;

/// <summary>
/// T21 backend: the operator views show persisted state only. Reserved and spent money are
/// separate, an uncertain cloud job offers reconciliation and never a retry, the pause state
/// survives a refresh, and no secret value ever appears in a payload.
/// </summary>
[Collection(ApiCollection.Name)]
public sealed class OperationsTests
{
    private readonly ApiTestFixture _fixture;

    public OperationsTests(ApiTestFixture fixture)
    {
        _fixture = fixture;
    }

    [Fact]
    public async Task EmptyDashboardShowsNoData()
    {
        await using var database = await PostgresTestDatabase.CreateAsync();
        await using var db = new AppDbContext(
            new DbContextOptionsBuilder<AppDbContext>().UseNpgsql(database.ConnectionString).Options,
            TimeProvider.System);
        await db.Database.MigrateAsync();
        var service = new OperationsService(db, new BudgetOptions(), TimeProvider.System);

        var status = await service.StatusAsync(default);
        var jobs = await service.JobsAsync(null, 50, default);
        var costs = await service.CostsAsync(default);

        Assert.False(status.HasData);
        Assert.Equal(0, status.Queued);
        Assert.Equal(0, status.AwaitingReview);
        Assert.Null(status.WorkerHeartbeat.LastSeenAt);
        Assert.True(status.WorkerHeartbeat.Stale);
        Assert.Empty(jobs);
        Assert.Equal(0m, costs.ReservedUsd);
        Assert.Equal(0m, costs.SettledUsd);
        Assert.Null(costs.PriceSnapshot);
    }

    [Fact]
    public async Task ReservedAndSpentAreDistinct()
    {
        var client = await _fixture.CreateLoggedInClientAsync();
        await using var seedScope = _fixture.Factory.Services.CreateAsyncScope();
        var seedDb = seedScope.ServiceProvider.GetRequiredService<AppDbContext>();
        var before = await client.GetFromJsonAsync<JsonElement>("/api/v1/system/costs");
        var reservedBefore = before.GetProperty("reservedUsd").GetDecimal();
        var settledBefore = before.GetProperty("settledUsd").GetDecimal();
        var now = DateTimeOffset.UtcNow;
        var campaignId = (await ReviewSeed.CreateAsync(seedDb)).CampaignId;
        var priceId = Guid.NewGuid();
        seedDb.PriceSnapshots.Add(new PriceSnapshot
        {
            Id = priceId, Provider = "fal", Model = "m", ConfigHash = "h", Unit = "request", UnitPriceUsd = 0.1m,
            SourceUrl = "https://fal.ai/pricing", VerifiedAt = now.AddMinutes(-10), ValidUntil = now.AddMinutes(-1)
        });
        foreach (var (status, reserved, settled) in new[] { ("reserved", 0.12m, (decimal?)null), ("settled", 0.12m, 0.10m) })
        {
            seedDb.BudgetReservations.Add(new BudgetReservationRecord
            {
                Id = Guid.NewGuid(), OperationId = "ops-" + Guid.NewGuid().ToString("N"), CampaignId = campaignId,
                VideoScopeId = Guid.NewGuid(), Provider = "fal", Model = "m", PriceSnapshotId = priceId,
                ApprovalId = "a", ReservedUsd = reserved, SettledUsd = settled, Status = status,
                AllocationsJson = "[]", CreatedAt = now
            });
        }

        await seedDb.SaveChangesAsync();

        var costs = await client.GetFromJsonAsync<JsonElement>("/api/v1/system/costs");

        Assert.Equal(reservedBefore + 0.12m, costs.GetProperty("reservedUsd").GetDecimal());
        Assert.Equal(settledBefore + 0.10m, costs.GetProperty("settledUsd").GetDecimal());
        Assert.True(costs.GetProperty("priceSnapshot").GetProperty("stale").GetBoolean());
        Assert.True(costs.TryGetProperty("monthlyBudgetUsd", out _));
        Assert.True(costs.TryGetProperty("remainingMonthlyUsd", out _));
    }

    [Fact]
    public async Task UnknownJobHasNoBlindRetryButton()
    {
        var client = await _fixture.CreateLoggedInClientAsync();
        var id = Guid.NewGuid();
        await using (var scope = _fixture.Factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            db.Jobs.Add(new Job
            {
                Id = id, JobType = "submit_video", PayloadJson = "{}", DedupeKey = "ops-unknown-" + id.ToString("N"),
                Lane = JobLane.CloudSubmit, Status = "failed", LastErrorCode = "SUBMISSION_UNKNOWN",
                RunAfter = DateTimeOffset.UtcNow, CreatedAt = DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow
            });
            await db.SaveChangesAsync();
        }

        var jobs = await client.GetFromJsonAsync<JsonElement>("/api/v1/system/jobs?limit=200");
        var job = jobs.EnumerateArray().Single(x => x.GetProperty("id").GetGuid() == id);

        Assert.False(job.GetProperty("canRetry").GetBoolean());
        Assert.True(job.GetProperty("needsReconciliation").GetBoolean());
        Assert.Contains("never retried automatically", job.GetProperty("explanation").GetString());

        var retry = await client.PostAsync($"/api/v1/system/jobs/{id}/retry", null);
        Assert.True(retry.StatusCode is HttpStatusCode.NotFound or HttpStatusCode.MethodNotAllowed);
    }

    [Fact]
    public async Task PausedStatePersistsAfterRefresh()
    {
        var client = await _fixture.CreateLoggedInClientAsync();
        try
        {
            Assert.Equal(HttpStatusCode.NoContent, (await client.PostAsync("/api/v1/system/pause", null)).StatusCode);

            var first = await client.GetFromJsonAsync<JsonElement>("/api/v1/system/status");
            var second = await client.GetFromJsonAsync<JsonElement>("/api/v1/system/status");
            Assert.True(first.GetProperty("paused").GetBoolean());
            Assert.True(second.GetProperty("paused").GetBoolean());
        }
        finally
        {
            await client.PostAsync("/api/v1/system/resume", null);
        }

        var after = await client.GetFromJsonAsync<JsonElement>("/api/v1/system/status");
        Assert.False(after.GetProperty("paused").GetBoolean());
    }

    [Fact]
    public async Task KeysAreNeverInPagePayload()
    {
        var client = await _fixture.CreateLoggedInClientAsync();
        Environment.SetEnvironmentVariable("FAL_KEY", ApiTestFixture.TestSecret);
        try
        {
            var bodies = new List<string>();
            foreach (var path in new[]
                     {
                         "/api/v1/system/status", "/api/v1/system/jobs", "/api/v1/system/costs",
                         "/api/v1/system/diagnostics", "/api/v1/system/mode"
                     })
            {
                var response = await client.GetAsync(path);
                Assert.Equal(HttpStatusCode.OK, response.StatusCode);
                bodies.Add(await response.Content.ReadAsStringAsync());
            }

            Assert.All(bodies, body => Assert.DoesNotContain(ApiTestFixture.TestSecret, body));
            using var diagnostics = JsonDocument.Parse(bodies[3]);
            Assert.True(diagnostics.RootElement.GetProperty("configured").GetProperty("falKey").GetBoolean());
            Assert.Equal("unavailable", diagnostics.RootElement.GetProperty("thermal").GetProperty("status").GetString());
            Assert.True(diagnostics.RootElement.GetProperty("disk").TryGetProperty("freeBytes", out _));
        }
        finally
        {
            Environment.SetEnvironmentVariable("FAL_KEY", null);
        }
    }

    [Fact]
    public async Task JobDetailShowsTimelineAndAttempts()
    {
        var client = await _fixture.CreateLoggedInClientAsync();
        var id = Guid.NewGuid();
        await using (var scope = _fixture.Factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            db.Jobs.Add(new Job
            {
                Id = id, JobType = "render_video", PayloadJson = "{}", DedupeKey = "ops-detail-" + id.ToString("N"),
                Lane = JobLane.HeavyLocal, Status = "blocked", LastErrorCode = "DISK_PRESSURE",
                RunAfter = DateTimeOffset.UtcNow, CreatedAt = DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow
            });
            db.JobEvents.Add(new JobEvent
            {
                Id = Guid.NewGuid(), JobId = id, EventType = "finished", DetailsJson = "{\"status\":\"blocked\"}",
                OccurredAt = DateTimeOffset.UtcNow
            });
            await db.SaveChangesAsync();
        }

        var detail = await client.GetFromJsonAsync<JsonElement>($"/api/v1/system/jobs/{id}");

        Assert.Equal("DISK_PRESSURE", detail.GetProperty("lastErrorCode").GetString());
        Assert.Contains("disk", detail.GetProperty("explanation").GetString(), StringComparison.OrdinalIgnoreCase);
        Assert.Equal(1, detail.GetProperty("events").GetArrayLength());
    }
}
