using ContentFactory.Contracts;
using ContentFactory.Infrastructure.Jobs;
using ContentFactory.Infrastructure.Persistence;
using ContentFactory.Infrastructure.Persistence.Entities;
using ContentFactory.IntegrationTests.Infrastructure;
using Microsoft.EntityFrameworkCore;

namespace ContentFactory.IntegrationTests;

[CollectionDefinition(Name)]
public sealed class ScheduleCollection : ICollectionFixture<PostgresSchemaFixture>
{
    public const string Name = "postgres-schedule";
}

/// <summary>
/// T18: slot state lives in PostgreSQL, so restarts, repeated ticks and a long sleep never
/// create more than the one allowed run, and a schedule can never open a paid path.
/// </summary>
[Collection(ScheduleCollection.Name)]
public sealed class ScheduleRecoveryTests
{
    private static readonly int[] Everyday = [0, 1, 2, 3, 4, 5, 6];
    private readonly PostgresSchemaFixture _fixture;

    public ScheduleRecoveryTests(PostgresSchemaFixture fixture)
    {
        _fixture = fixture;
    }

    [Fact]
    public async Task DisabledScheduleNeverRuns()
    {
        await using var db = _fixture.CreateContext();
        var schedule = await ArrangeAsync(db, enabled: false);

        var result = await Ticker(db).TickAsync(At(9, 10), default);

        Assert.Equal(0, result.Enqueued);
        Assert.Equal(0, await db.ScheduleOccurrences.CountAsync(x => x.ScheduleId == schedule.Id));
    }

    [Fact]
    public async Task SleepMissedSlotsDoNotBurst()
    {
        await using var db = _fixture.CreateContext();
        var schedule = await ArrangeAsync(db, enabled: true, lastSlot: At(9, 0).AddDays(-3));

        // Mac slept for three days and wakes 20 minutes after today's 09:00 Bangkok slot.
        var result = await Ticker(db).TickAsync(At(9, 20), default);

        Assert.Equal(1, result.Enqueued);
        Assert.Equal(2, result.Missed);
        Assert.Equal(1, await db.ScheduleOccurrences.CountAsync(x => x.ScheduleId == schedule.Id));
        Assert.Equal(
            2,
            await db.AuditEvents.CountAsync(x => x.TargetId == schedule.Id && x.Action == "schedule.missed"));
    }

    [Fact]
    public async Task SlotOlderThanSixtyMinutesIsNotReplayed()
    {
        await using var db = _fixture.CreateContext();
        var schedule = await ArrangeAsync(db, enabled: true, lastSlot: At(9, 0).AddDays(-1));

        var result = await Ticker(db).TickAsync(At(10, 30), default);

        Assert.Equal(0, result.Enqueued);
        Assert.Equal(1, result.Missed);
        Assert.Equal(0, await db.ScheduleOccurrences.CountAsync(x => x.ScheduleId == schedule.Id));
    }

    [Fact]
    public async Task RestartAndRepeatedTicksCreateOneRun()
    {
        await using var db = _fixture.CreateContext();
        var schedule = await ArrangeAsync(db, enabled: true);

        await Ticker(db).TickAsync(At(9, 5), default);
        await using var restarted = _fixture.CreateContext();
        await Ticker(restarted).TickAsync(At(9, 6), default);
        await Ticker(restarted).TickAsync(At(9, 30), default);

        Assert.Equal(1, await db.ScheduleOccurrences.CountAsync(x => x.ScheduleId == schedule.Id));
        Assert.Equal(1, await db.Jobs.CountAsync(x =>
            x.DedupeKey.StartsWith("campaign-run:" + schedule.CampaignId.ToString("N"))));
    }

    [Fact]
    public async Task DailyJobLimitStillAppliesAfterRestart()
    {
        await using var db = _fixture.CreateContext();
        var schedule = await ArrangeAsync(db, enabled: true);
        // An earlier manual occurrence on today's local date uses up the daily limit of 1.
        var prior = await new RunEnqueuer(db, TimeProvider.System).EnqueueAsync(
            new RunEnqueueCommand(
                schedule.CampaignId, "manual-prior-" + Guid.NewGuid().ToString("N"),
                VideoMode.TemplateOnly, 25_000, true, 0),
            default);
        db.ScheduleOccurrences.Add(new ScheduleOccurrence
        {
            Id = Guid.NewGuid(), ScheduleId = schedule.Id,
            SlotKey = "2026-10-21T08:00@Asia/Bangkok", JobId = prior.JobId, CreatedAt = DateTimeOffset.UtcNow
        });
        await db.SaveChangesAsync();

        await using var restarted = _fixture.CreateContext();
        var result = await Ticker(restarted).TickAsync(At(9, 5), default);

        Assert.Equal(0, result.Enqueued);
        Assert.Equal(1, result.Failed);
        Assert.Equal(1, await db.ScheduleOccurrences.CountAsync(x => x.ScheduleId == schedule.Id));
        Assert.Equal(1, await db.AuditEvents.CountAsync(
            x => x.TargetId == schedule.Id && x.Action == "schedule.limit_reached"));
    }

    [Fact]
    public async Task ScheduledRunNeverOpensAPaidPath()
    {
        await using var db = _fixture.CreateContext();
        var schedule = await ArrangeAsync(db, enabled: true);

        await Ticker(db).TickAsync(At(9, 5), default);

        var job = await db.Jobs.AsNoTracking().SingleAsync(x =>
            x.DedupeKey.StartsWith("campaign-run:" + schedule.CampaignId.ToString("N")));
        Assert.Contains("\"cloudVideoSceneLimit\":0", job.PayloadJson.Replace(" ", string.Empty));
        Assert.Equal(0, await db.BudgetReservations.CountAsync());
    }

    [Fact]
    public async Task UnrunnableCampaignIsSkippedWithAuditNotRetriedForever()
    {
        await using var db = _fixture.CreateContext();
        var schedule = await ArrangeAsync(db, enabled: true, approvedClaim: false);

        var result = await Ticker(db).TickAsync(At(9, 5), default);
        var again = await Ticker(db).TickAsync(At(9, 6), default);

        Assert.Equal(1, result.Failed);
        Assert.Equal(0, again.Failed);
        Assert.Equal(1, await db.AuditEvents.CountAsync(
            x => x.TargetId == schedule.Id && x.Action == "schedule.skipped"));
    }

    private static DateTimeOffset At(int hour, int minute)
        => new(2026, 10, 21, hour, minute, 0, TimeSpan.FromHours(7));

    private static ScheduleTicker Ticker(AppDbContext db)
        => new(db, new RunEnqueuer(db, TimeProvider.System));

    private static async Task<Schedule> ArrangeAsync(
        AppDbContext db, bool enabled, DateTimeOffset? lastSlot = null, bool approvedClaim = true)
    {
        var now = DateTimeOffset.UtcNow;
        var product = new Product
        {
            Id = Guid.NewGuid(), Title = "Scheduled product", Market = "TH", Locale = "th-TH",
            Version = 1, CreatedAt = now, UpdatedAt = now
        };
        var channel = new Channel
        {
            Id = Guid.NewGuid(), Platform = "tiktok", DisplayName = "c", Market = "TH", Locale = "th-TH",
            Timezone = "Asia/Bangkok", Status = "active", CapabilitiesJson = "{}"
        };
        var campaign = new Campaign
        {
            Id = Guid.NewGuid(), ProductId = product.Id, ChannelId = channel.Id, Mode = VideoMode.TemplateOnly,
            Locale = "th-TH", TargetDurationMs = 25_000, BudgetUsd = 0m, Status = "active", Version = 1,
            CreatedAt = now, UpdatedAt = now
        };
        var asset = new Asset
        {
            Id = Guid.NewGuid(), Sha256 = Guid.NewGuid().ToString("N") + Guid.NewGuid().ToString("N"),
            RelativePath = "assets/x", Kind = AssetKind.Image, Mime = "image/jpeg", Bytes = 10,
            RightsStatus = "owner_attested", SourceReference = "fixture", CloudUseAllowed = false, CreatedAt = now
        };
        var schedule = new Schedule
        {
            Id = Guid.NewGuid(), CampaignId = campaign.Id, Timezone = "Asia/Bangkok",
            LocalTime = new TimeOnly(9, 0), DaysOfWeek = Everyday, Enabled = enabled,
            MaxJobsPerDay = 1, LastSlot = lastSlot, Version = 1
        };
        db.Products.Add(product);
        db.Channels.Add(channel);
        db.Campaigns.Add(campaign);
        db.Assets.Add(asset);
        await db.SaveChangesAsync();
        db.ProductAssets.Add(new ProductAsset { ProductId = product.Id, AssetId = asset.Id, DisplayOrder = 0 });
        db.ProductClaims.Add(new ProductClaim
        {
            Id = Guid.NewGuid(), ProductId = product.Id, Text = "ช่วยให้ผิวชุ่มชื้น",
            SourceReference = "owner-sheet", Status = approvedClaim ? "approved" : "pending",
            VerifiedAt = now, CreatedAt = now
        });
        db.Schedules.Add(schedule);
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();
        return schedule;
    }
}
