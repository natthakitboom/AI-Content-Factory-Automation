using ContentFactory.Infrastructure.Persistence;
using ContentFactory.Infrastructure.Persistence.Entities;
using ContentFactory.IntegrationTests.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace ContentFactory.IntegrationTests;

[Collection(PostgresSchemaCollection.Name)]
public sealed class SchemaTests
{
    private readonly PostgresSchemaFixture _fixture;

    public SchemaTests(PostgresSchemaFixture fixture)
    {
        _fixture = fixture;
    }

    [Fact]
    public async Task DuplicateDedupeKeyRejected()
    {
        var dedupeKey = "dedupe-" + Guid.NewGuid().ToString("N");
        await using var db = _fixture.CreateContext();
        db.Jobs.Add(NewJob(dedupeKey));
        db.Jobs.Add(NewJob(dedupeKey));

        var exception = await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync());
        var postgres = FindPostgresException(exception);

        Assert.NotNull(postgres);
        Assert.Equal(PostgresErrorCodes.UniqueViolation, postgres!.SqlState);
    }

    [Fact]
    public async Task MoneyRoundTripsSixDecimals()
    {
        await using (var db = _fixture.CreateContext())
        {
            db.CostEntries.Add(new CostEntry
            {
                Id = Guid.NewGuid(),
                OperationId = "op-" + Guid.NewGuid().ToString("N"),
                IdempotencyKey = "idem-" + Guid.NewGuid().ToString("N"),
                EntryType = "settle",
                AmountUsd = 0.100001m,
                Certainty = "confirmed",
                CreatedAt = DateTimeOffset.UtcNow
            });
            await db.SaveChangesAsync();
        }

        await using (var db = _fixture.CreateContext())
        {
            var reloaded = await db.CostEntries.AsNoTracking().SingleAsync();
            Assert.Equal(0.100001m, reloaded.AmountUsd);
        }

        await using var check = _fixture.CreateContext();
        await check.Database.OpenConnectionAsync();
        await using var command = check.Database.GetDbConnection().CreateCommand();
        command.CommandText =
            "SELECT numeric_precision, numeric_scale FROM information_schema.columns " +
            "WHERE table_schema = 'app' AND table_name = 'cost_entries' AND column_name = 'amount_usd'";
        await using var reader = await command.ExecuteReaderAsync();
        Assert.True(await reader.ReadAsync());
        Assert.Equal(18, reader.GetInt32(0));
        Assert.Equal(6, reader.GetInt32(1));
    }

    [Fact]
    public async Task InvalidCommissionRateRejected()
    {
        await using var db = _fixture.CreateContext();
        var product = NewProduct();
        db.Products.Add(product);
        db.ProductOffers.Add(new ProductOffer
        {
            Id = Guid.NewGuid(),
            ProductId = product.Id,
            Market = "TH",
            ProductUrl = "https://example.invalid/product",
            Price = 199.00m,
            Currency = "THB",
            CommissionRate = 1.5m,
            Availability = "in_stock",
            CheckedAt = DateTimeOffset.UtcNow,
            CreatedAt = DateTimeOffset.UtcNow
        });

        var exception = await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync());
        var postgres = FindPostgresException(exception);

        Assert.NotNull(postgres);
        Assert.Equal(PostgresErrorCodes.CheckViolation, postgres!.SqlState);
    }

    [Fact]
    public async Task TimestampRetainsInstant()
    {
        var originalInstant = new DateTimeOffset(2026, 10, 20, 21, 0, 0, TimeSpan.FromHours(7));
        var product = NewProduct();
        product.CreatedAt = originalInstant;
        product.UpdatedAt = originalInstant;

        await using (var db = _fixture.CreateContext())
        {
            db.Products.Add(product);
            await db.SaveChangesAsync();
        }

        await using (var db = _fixture.CreateContext())
        {
            var reloaded = await db.Products.AsNoTracking().SingleAsync(x => x.Id == product.Id);
            Assert.Equal(originalInstant, reloaded.CreatedAt);
        }
    }

    [Fact]
    public async Task RequiredTablesAndEligibleJobIndexExist()
    {
        string[] expected =
        [
            "owner_users", "products", "product_claims", "product_offers", "assets",
            "product_assets", "channels", "campaigns", "content_revisions", "scene_assets",
            "video_versions", "jobs", "job_attempts", "job_events", "outbox_events",
            "provider_requests", "price_snapshots", "budget_periods", "cost_entries",
            "approvals", "export_packages", "publications", "metric_snapshots",
            "schedules", "schedule_occurrences", "audit_events"
        ];

        await using var db = _fixture.CreateContext();
        await db.Database.OpenConnectionAsync();

        await using (var command = db.Database.GetDbConnection().CreateCommand())
        {
            command.CommandText =
                "SELECT table_name FROM information_schema.tables WHERE table_schema = 'app'";
            var tables = new HashSet<string>(StringComparer.Ordinal);
            await using var reader = await command.ExecuteReaderAsync();
            while (await reader.ReadAsync())
            {
                tables.Add(reader.GetString(0));
            }

            foreach (var table in expected)
            {
                Assert.True(tables.Contains(table), $"Missing table app.{table}");
            }
        }

        await using (var command = db.Database.GetDbConnection().CreateCommand())
        {
            command.CommandText =
                "SELECT indexdef FROM pg_indexes WHERE schemaname = 'app' " +
                "AND tablename = 'jobs' AND indexname = 'ix_jobs_eligible'";
            var indexDefinition = (string?)await command.ExecuteScalarAsync();
            Assert.NotNull(indexDefinition);
            Assert.Contains("status", indexDefinition, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("queued", indexDefinition, StringComparison.OrdinalIgnoreCase);
        }
    }

    [Fact]
    public async Task MigratingAnAlreadyMigratedDatabaseIsNoOp()
    {
        await using var db = _fixture.CreateContext();
        await db.Database.MigrateAsync();

        var applied = await db.Database.GetAppliedMigrationsAsync();
        Assert.NotEmpty(applied);
    }

    private static Job NewJob(string dedupeKey)
    {
        var now = DateTimeOffset.UtcNow;
        return new Job
        {
            Id = Guid.NewGuid(),
            JobType = "test_job",
            PayloadJson = "{}",
            DedupeKey = dedupeKey,
            Lane = ContentFactory.Contracts.JobLane.Control,
            Status = "queued",
            RunAfter = now,
            CreatedAt = now,
            UpdatedAt = now
        };
    }

    private static Product NewProduct()
    {
        var now = DateTimeOffset.UtcNow;
        return new Product
        {
            Id = Guid.NewGuid(),
            Title = "Test product",
            Market = "TH",
            Locale = "th-TH",
            Version = 1,
            CreatedAt = now,
            UpdatedAt = now
        };
    }

    private static PostgresException? FindPostgresException(Exception exception)
    {
        for (var current = exception; current is not null; current = current.InnerException!)
        {
            if (current is PostgresException postgres)
            {
                return postgres;
            }
        }

        return null;
    }
}
