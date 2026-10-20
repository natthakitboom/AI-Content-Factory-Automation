using System.Collections.Concurrent;
using System.Text.Json;
using ContentFactory.Contracts;
using ContentFactory.Core.Exceptions;
using ContentFactory.Infrastructure.Billing;
using ContentFactory.Infrastructure.Persistence;
using ContentFactory.Infrastructure.Persistence.Entities;
using ContentFactory.IntegrationTests.Infrastructure;
using Microsoft.EntityFrameworkCore;

namespace ContentFactory.IntegrationTests;

[CollectionDefinition(Name)]
public sealed class BudgetCollection : ICollectionFixture<PostgresSchemaFixture>
{
    public const string Name = "postgres-budget";
}

/// <summary>
/// T09 spending controls are proven on real PostgreSQL. Row locks, numeric precision,
/// unique operation ids and the approval-count transaction cannot be proven in memory.
/// No paid provider is called; the test approval file is a local fixture.
/// </summary>
[Collection(BudgetCollection.Name)]
public sealed class BudgetRaceTests : IAsyncLifetime
{
    private const string Provider = "fal";
    private const string Model = "fal-ai/wan/v2.2-a14b/image-to-video/turbo";

    private readonly PostgresSchemaFixture _fixture;
    private readonly string _directory = Path.Combine(
        Path.GetTempPath(),
        "contentfactory-budget-tests",
        Guid.NewGuid().ToString("N"));

    public BudgetRaceTests(PostgresSchemaFixture fixture)
    {
        _fixture = fixture;
        Directory.CreateDirectory(_directory);
    }

    public Task InitializeAsync() => Task.CompletedTask;

    [Fact]
    public async Task ConcurrentReservationsCannotExceedLimit()
    {
        await ResetBudgetStateAsync();
        var campaignId = await SeedCampaignAsync(1.00m);
        var price = await SeedPriceAsync(unitPriceUsd: 0.10m);
        var quote = Quote(price, 0.10m, 0.12m);
        var approvalFile = WriteApproval(maxRequestCount: 100, maxTotalUsd: 10.00m);
        var options = CreateOptions(
            approvalFile,
            daily: 1.00m,
            monthly: 1.00m,
            video: 1.00m,
            maxSingleRequest: 1.00m);
        var videoScopeId = Guid.NewGuid();

        var start = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var reservations = new ConcurrentBag<BudgetReservation>();
        var failures = new ConcurrentBag<Exception>();

        var tasks = Enumerable.Range(0, 20).Select(async index =>
        {
            await start.Task;
            await using var db = _fixture.CreateContext();
            var guard = CreateGuard(db, options);
            try
            {
                var reservation = await guard.ReserveAsync(
                    $"concurrent-{index}",
                    campaignId,
                    videoScopeId,
                    quote,
                    CancellationToken.None);
                reservations.Add(reservation);
            }
            catch (Exception exception)
            {
                failures.Add(exception);
            }
        }).ToArray();

        start.SetResult();
        await Task.WhenAll(tasks);

        Assert.Equal(8, reservations.Count);
        Assert.Equal(0.96m, reservations.Sum(x => x.ReservedUsd));
        Assert.All(failures, failure => Assert.IsType<BudgetExceededException>(failure));

        await using var finalDb = _fixture.CreateContext();
        var periods = await finalDb.BudgetPeriods.AsNoTracking().ToListAsync();
        Assert.Equal(4, periods.Count);
        Assert.All(periods, period =>
        {
            Assert.Equal(0.96m, period.ReservedUsd);
            Assert.Equal(0m, period.SpentUsd);
            Assert.True(period.ReservedUsd >= 0m);
        });

        Assert.Equal(8, await finalDb.BudgetReservations.CountAsync());
        Assert.Equal(8, await finalDb.CostEntries.CountAsync(x => x.EntryType == "reserve"));
        var reservationRows = await finalDb.BudgetReservations
            .AsNoTracking()
            .ToListAsync();
        Assert.All(
            reservationRows,
            reservation => Assert.Equal(price.Id, reservation.PriceSnapshotId));

        using var allocations = JsonDocument.Parse(reservationRows[0].AllocationsJson);
        Assert.Equal(
            new[] { "monthly", "daily", "campaign", "video" },
            allocations.RootElement
                .EnumerateArray()
                .Select(element => element.GetProperty("scopeType").GetString())
                .ToArray());
    }

    [Fact]
    public async Task SettlementIsIdempotent()
    {
        await ResetBudgetStateAsync();
        var campaignId = await SeedCampaignAsync(10.00m);
        var price = await SeedPriceAsync(unitPriceUsd: 0.10m);
        var quote = Quote(price, 0.10m, 0.12m);
        var options = CreateOptions(WriteApproval(maxRequestCount: 3, maxTotalUsd: 0.36m));

        await using var db = _fixture.CreateContext();
        var guard = CreateGuard(db, options);
        var reservation = await guard.ReserveAsync(
            "settle-once",
            campaignId,
            Guid.NewGuid(),
            quote,
            CancellationToken.None);

        await guard.SettleAsync(reservation.Id, 0.10m, "confirmed", CancellationToken.None);
        await guard.SettleAsync(reservation.Id, 0.10m, "confirmed", CancellationToken.None);

        db.ChangeTracker.Clear();
        var periods = await db.BudgetPeriods.AsNoTracking().ToListAsync();
        Assert.All(periods, period =>
        {
            Assert.Equal(0m, period.ReservedUsd);
            Assert.Equal(0.10m, period.SpentUsd);
        });

        Assert.Equal(1, await db.CostEntries.CountAsync(x => x.EntryType == "settle"));
        var record = await db.BudgetReservations.AsNoTracking().SingleAsync();
        Assert.Equal("settled", record.Status);
        Assert.Equal(0.10m, record.SettledUsd);
        Assert.Equal(price.Id, record.PriceSnapshotId);
    }

    [Fact]
    public async Task UnknownSubmissionKeepsReservation()
    {
        await ResetBudgetStateAsync();
        var campaignId = await SeedCampaignAsync(10.00m);
        var price = await SeedPriceAsync(unitPriceUsd: 0.10m);
        var quote = Quote(price, 0.10m, 0.12m);
        var options = CreateOptions(WriteApproval(maxRequestCount: 3, maxTotalUsd: 0.36m));

        await using var db = _fixture.CreateContext();
        var guard = CreateGuard(db, options);
        var reservation = await guard.ReserveAsync(
            "unknown-submit",
            campaignId,
            Guid.NewGuid(),
            quote,
            CancellationToken.None);

        var reservedBefore = await TotalReservedAsync(db);
        await guard.MarkUnknownAsync(reservation.Id, "provider response lost", CancellationToken.None);
        var reservedAfter = await TotalReservedAsync(db);

        Assert.Equal(0.12m, reservedBefore);
        Assert.Equal(reservedBefore, reservedAfter);

        db.ChangeTracker.Clear();
        var record = await db.BudgetReservations.AsNoTracking().SingleAsync();
        Assert.Equal("unknown", record.Status);
        Assert.Equal(0.12m, record.ReservedUsd);
        Assert.Equal(1, await db.CostEntries.CountAsync(x => x.EntryType == "reserve"));
        Assert.Equal(0, await db.CostEntries.CountAsync(x => x.EntryType == "settle"));
    }

    [Fact]
    public async Task StalePriceBlocksSpend()
    {
        await ResetBudgetStateAsync();
        var price = await SeedPriceAsync(
            unitPriceUsd: 0.10m,
            validUntil: DateTimeOffset.UtcNow.AddMinutes(-1));
        await using var serviceDb = _fixture.CreateContext();
        var service = new PriceSnapshotService(
            serviceDb,
            CreateOptions(WriteApproval()));

        var exception = await Assert.ThrowsAsync<PriceStaleException>(
            () => service.GetQuoteAsync(Provider, Model, price.ConfigHash, CancellationToken.None));

        Assert.Equal("PRICE_STALE", exception.Code);

        await using var db = _fixture.CreateContext();
        Assert.Equal(0, await db.BudgetPeriods.CountAsync());
        Assert.Equal(0, await db.BudgetReservations.CountAsync());
        Assert.Equal(0, await db.CostEntries.CountAsync());
    }

    [Fact]
    public async Task VerifiedPriceQuoteUsesMargin()
    {
        await ResetBudgetStateAsync();
        var price = await SeedPriceAsync(unitPriceUsd: 0.10m);
        await using var db = _fixture.CreateContext();
        var service = new PriceSnapshotService(db, CreateOptions(WriteApproval()));

        var quote = await service.GetQuoteAsync(
            Provider,
            Model,
            price.ConfigHash,
            CancellationToken.None);

        Assert.Equal(price.Id, quote.PriceSnapshotId);
        Assert.Equal(Provider, quote.Provider);
        Assert.Equal(Model, quote.Model);
        Assert.Equal(0.10m, quote.EstimatedUsd);
        Assert.Equal(0.12m, quote.ReservationUsd);
        Assert.Equal(price.ValidUntil, quote.ValidUntil);
    }

    [Fact]
    public async Task PaidDisabledBlocksEveryProvider()
    {
        await ResetBudgetStateAsync();
        var approvalFile = WriteApproval();
        var options = CreateOptions(approvalFile, allowPaidApis: false);
        await using var db = _fixture.CreateContext();
        var guard = CreateGuard(db, options);

        foreach (var provider in new[] { "fal", "azure", "openai", "lmstudio" })
        {
            var quote = new PriceQuote(
                Guid.NewGuid(),
                provider,
                "model",
                0.10m,
                0.12m,
                DateTimeOffset.UtcNow.AddHours(1));

            var exception = await Assert.ThrowsAsync<PaidApisDisabledException>(
                () => guard.ReserveAsync(
                    $"disabled-{provider}",
                    Guid.NewGuid(),
                    quote,
                    CancellationToken.None));

            Assert.Equal("PAID_APIS_DISABLED", exception.Code);
        }

        Assert.Equal(0, await db.BudgetPeriods.CountAsync());
        Assert.Equal(0, await db.BudgetReservations.CountAsync());
    }

    [Fact]
    public async Task DailyCapBlocksSpend()
        => await AssertScopeBlockedAsync("daily", daily: 0.11m);

    [Fact]
    public async Task MonthlyCapBlocksSpend()
        => await AssertScopeBlockedAsync("monthly", monthly: 0.11m);

    [Fact]
    public async Task CampaignCapBlocksSpend()
        => await AssertScopeBlockedAsync("campaign", campaignBudget: 0.11m);

    [Fact]
    public async Task VideoCapBlocksSpend()
        => await AssertScopeBlockedAsync("video", video: 0.11m);

    [Fact]
    public async Task SingleRequestCapBlocksSpend()
        => await AssertScopeBlockedAsync("request", maxSingleRequest: 0.09m);

    [Fact]
    public async Task ApprovalRequestAndTotalCapsApply()
    {
        await ResetBudgetStateAsync();
        var campaignId = await SeedCampaignAsync(10.00m);
        var price = await SeedPriceAsync(unitPriceUsd: 0.10m);
        var quote = Quote(price, 0.10m, 0.12m);
        var options = CreateOptions(
            WriteApproval(maxRequestCount: 2, maxTotalUsd: 0.24m));

        await using var db = _fixture.CreateContext();
        var guard = CreateGuard(db, options);

        await guard.ReserveAsync("approval-1", campaignId, Guid.NewGuid(), quote, CancellationToken.None);
        await guard.ReserveAsync("approval-2", campaignId, Guid.NewGuid(), quote, CancellationToken.None);

        var exception = await Assert.ThrowsAsync<ApprovalStaleException>(
            () => guard.ReserveAsync(
                "approval-3",
                campaignId,
                Guid.NewGuid(),
                quote,
                CancellationToken.None));

        Assert.Equal("APPROVAL_REQUEST_LIMIT", exception.Code);
        Assert.Equal(2, await db.BudgetReservations.CountAsync());
        Assert.Equal(0.24m, await TotalReservedAsync(db));
    }

    [Fact]
    public async Task ApprovalExpiresDuringBatch()
    {
        await ResetBudgetStateAsync();
        var t0 = DateTimeOffset.UtcNow;
        var clock = new ManualTimeProvider(t0);
        var campaignId = await SeedCampaignAsync(10.00m);
        var price = await SeedPriceAsync(unitPriceUsd: 0.10m);
        var quote = Quote(price, 0.10m, 0.12m);
        var options = CreateOptions(
            WriteApproval(maxRequestCount: 10, maxTotalUsd: 10.00m, expiresAt: t0.AddMinutes(1)));

        await using var db = _fixture.CreateContext();
        var guard = CreateGuard(db, options, clock);

        await guard.ReserveAsync("batch-1", campaignId, Guid.NewGuid(), quote, CancellationToken.None);
        clock.Advance(TimeSpan.FromMinutes(2));

        var exception = await Assert.ThrowsAsync<ApprovalStaleException>(
            () => guard.ReserveAsync(
                "batch-2",
                campaignId,
                Guid.NewGuid(),
                quote,
                CancellationToken.None));

        Assert.Equal("APPROVAL_EXPIRED", exception.Code);
        Assert.Equal(0.12m, await TotalReservedAsync(db));
        Assert.Equal(1, await db.BudgetReservations.CountAsync());
    }

    [Fact]
    public async Task ReleaseTwiceDoesNotGoNegative()
    {
        await ResetBudgetStateAsync();
        var campaignId = await SeedCampaignAsync(10.00m);
        var price = await SeedPriceAsync(unitPriceUsd: 0.10m);
        var quote = Quote(price, 0.10m, 0.12m);
        var options = CreateOptions(WriteApproval());

        await using var db = _fixture.CreateContext();
        var guard = CreateGuard(db, options);
        var reservation = await guard.ReserveAsync(
            "release-twice",
            campaignId,
            Guid.NewGuid(),
            quote,
            CancellationToken.None);

        await guard.ReleaseAsync(reservation.Id, "provider rejected before billing", CancellationToken.None);
        await guard.ReleaseAsync(reservation.Id, "duplicate release", CancellationToken.None);

        db.ChangeTracker.Clear();
        var periods = await db.BudgetPeriods.AsNoTracking().ToListAsync();
        Assert.All(periods, period =>
        {
            Assert.Equal(0m, period.ReservedUsd);
            Assert.Equal(0m, period.SpentUsd);
            Assert.True(period.ReservedUsd >= 0m);
        });

        var record = await db.BudgetReservations.AsNoTracking().SingleAsync();
        Assert.Equal("released", record.Status);
        Assert.Equal(1, await db.CostEntries.CountAsync(x => x.EntryType == "release"));
    }

    [Fact]
    public async Task ConfirmedRefundAdjustsSpendWithoutDoubleCounting()
    {
        await ResetBudgetStateAsync();
        var campaignId = await SeedCampaignAsync(10.00m);
        var price = await SeedPriceAsync(unitPriceUsd: 0.10m);
        var quote = Quote(price, 0.10m, 0.12m);
        var options = CreateOptions(WriteApproval());

        await using var db = _fixture.CreateContext();
        var guard = CreateGuard(db, options);
        var reservation = await guard.ReserveAsync(
            "refund-once",
            campaignId,
            Guid.NewGuid(),
            quote,
            CancellationToken.None);

        await guard.SettleAsync(reservation.Id, 0.10m, "confirmed", CancellationToken.None);
        await guard.SettleAsync(reservation.Id, 0.08m, "confirmed", CancellationToken.None);
        await guard.SettleAsync(reservation.Id, 0.08m, "confirmed", CancellationToken.None);

        db.ChangeTracker.Clear();
        var periods = await db.BudgetPeriods.AsNoTracking().ToListAsync();
        Assert.All(periods, period => Assert.Equal(0.08m, period.SpentUsd));

        Assert.Equal(1, await db.CostEntries.CountAsync(x => x.EntryType == "settle"));
        Assert.Equal(1, await db.CostEntries.CountAsync(x => x.EntryType == "refund"));
        var record = await db.BudgetReservations.AsNoTracking().SingleAsync();
        Assert.Equal(0.08m, record.SettledUsd);
    }

    public Task DisposeAsync()
    {
        try
        {
            Directory.Delete(_directory, recursive: true);
        }
        catch (IOException)
        {
            // Best-effort cleanup of a test-only directory.
        }

        return Task.CompletedTask;
    }

    private async Task AssertScopeBlockedAsync(
        string expectedScope,
        decimal daily = 10.00m,
        decimal monthly = 10.00m,
        decimal video = 10.00m,
        decimal maxSingleRequest = 10.00m,
        decimal campaignBudget = 10.00m)
    {
        await ResetBudgetStateAsync();
        var campaignId = await SeedCampaignAsync(campaignBudget);
        var price = await SeedPriceAsync(unitPriceUsd: 0.10m);
        var quote = Quote(price, 0.10m, 0.12m);
        var options = CreateOptions(
            WriteApproval(maxRequestCount: 10, maxTotalUsd: 10.00m),
            daily,
            monthly,
            video,
            maxSingleRequest);

        await using var db = _fixture.CreateContext();
        var guard = CreateGuard(db, options);

        var exception = await Assert.ThrowsAsync<BudgetExceededException>(
            () => guard.ReserveAsync(
                $"scope-{expectedScope}",
                campaignId,
                Guid.NewGuid(),
                quote,
                CancellationToken.None));

        Assert.Equal(expectedScope, exception.ScopeType);
        Assert.Equal(0, await db.BudgetPeriods.CountAsync());
        Assert.Equal(0, await db.BudgetReservations.CountAsync());
        Assert.Equal(0, await db.CostEntries.CountAsync());
    }

    private async Task ResetBudgetStateAsync()
    {
        await using var db = _fixture.CreateContext();
        await db.CostEntries.ExecuteDeleteAsync();
        await db.BudgetReservations.ExecuteDeleteAsync();
        await db.BudgetPeriods.ExecuteDeleteAsync();
    }

    private async Task<Guid> SeedCampaignAsync(decimal budgetUsd)
    {
        var now = DateTimeOffset.UtcNow;
        var productId = Guid.NewGuid();
        var channelId = Guid.NewGuid();
        var campaignId = Guid.NewGuid();

        await using var db = _fixture.CreateContext();
        db.Products.Add(new Product
        {
            Id = productId,
            Title = "Budget test product",
            Market = "TH",
            Locale = "th-TH",
            Version = 1,
            CreatedAt = now,
            UpdatedAt = now
        });
        db.Channels.Add(new Channel
        {
            Id = channelId,
            Platform = "tiktok",
            DisplayName = "Budget test channel",
            Market = "TH",
            Locale = "th-TH",
            Timezone = "Asia/Bangkok",
            Status = "active",
            CapabilitiesJson = "{}",
            CreatedAt = now
        });
        db.Campaigns.Add(new Campaign
        {
            Id = campaignId,
            ProductId = productId,
            ChannelId = channelId,
            Mode = VideoMode.TemplateOnly,
            Locale = "th-TH",
            TargetDurationMs = 25_000,
            BudgetUsd = budgetUsd,
            Status = "active",
            Version = 1,
            CreatedAt = now,
            UpdatedAt = now
        });

        await db.SaveChangesAsync();
        return campaignId;
    }

    private async Task<PriceSeed> SeedPriceAsync(
        decimal unitPriceUsd,
        DateTimeOffset? validUntil = null)
    {
        var now = DateTimeOffset.UtcNow;
        var configHash = Guid.NewGuid().ToString("N") + new string('a', 32);
        var id = Guid.NewGuid();

        await using var db = _fixture.CreateContext();
        db.PriceSnapshots.Add(new PriceSnapshot
        {
            Id = id,
            Provider = Provider,
            Model = Model,
            ConfigHash = configHash,
            Unit = "request",
            UnitPriceUsd = unitPriceUsd,
            SourceUrl = "https://fal.ai/pricing",
            VerifiedAt = now.AddMinutes(-5),
            ValidUntil = validUntil ?? now.AddHours(1)
        });
        await db.SaveChangesAsync();

        return new PriceSeed(id, configHash, validUntil ?? now.AddHours(1));
    }

    private static PriceQuote Quote(
        PriceSeed price,
        decimal estimatedUsd,
        decimal reservationUsd)
        => new(
            price.Id,
            Provider,
            Model,
            estimatedUsd,
            reservationUsd,
            price.ValidUntil);

    private string WriteApproval(
        int maxRequestCount = 3,
        decimal maxTotalUsd = 0.36m,
        DateTimeOffset? expiresAt = null)
    {
        var path = Path.Combine(_directory, $"{Guid.NewGuid():N}.json");
        var payload = new
        {
            approvalId = $"test-approval-{Guid.NewGuid():N}",
            provider = Provider,
            model = Model,
            maxRequestCount,
            maxTotalUsd,
            expiresAt = expiresAt ?? DateTimeOffset.UtcNow.AddHours(1),
            allowFallbackProvider = false,
            approvedBy = "owner-test"
        };

        File.WriteAllText(path, JsonSerializer.Serialize(payload, JsonDefaults.Options));
        return path;
    }

    private static BudgetOptions CreateOptions(
        string approvalFile,
        decimal daily = 10.00m,
        decimal monthly = 10.00m,
        decimal video = 10.00m,
        decimal maxSingleRequest = 10.00m,
        bool allowPaidApis = true)
        => new()
        {
            AllowPaidApis = allowPaidApis,
            LiveApprovalFile = approvalFile,
            DailyBudgetUsd = daily,
            MonthlyBudgetUsd = monthly,
            MaxVideoCostUsd = video,
            MaxSingleRequestUsd = maxSingleRequest,
            ReservationMultiplier = 1.20m
        };

    private static PostgresBudgetGuard CreateGuard(
        AppDbContext db,
        BudgetOptions options,
        TimeProvider? clock = null)
        => new(db, options, new LiveApprovalGuard(options, clock ?? TimeProvider.System));

    private static async Task<decimal> TotalReservedAsync(AppDbContext db)
    {
        var values = await db.BudgetPeriods.AsNoTracking()
            .Select(x => x.ReservedUsd)
            .ToListAsync();
        return values.Count == 0 ? 0m : values.Max();
    }

    private sealed record PriceSeed(Guid Id, string ConfigHash, DateTimeOffset ValidUntil);

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
