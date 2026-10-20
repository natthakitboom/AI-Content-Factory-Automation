using ContentFactory.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace ContentFactory.IntegrationTests.Infrastructure;

/// <summary>
/// One migrated PostgreSQL database shared by a test collection. A real server is used
/// because constraints, numeric precision and transactions cannot be proven in memory.
/// </summary>
public sealed class PostgresSchemaFixture : IAsyncLifetime
{
    private PostgresTestDatabase? _database;

    public DbContextOptions<AppDbContext> Options { get; private set; } = null!;
    public string DatabaseName => _database?.DatabaseName ?? "(not created)";

    public async Task InitializeAsync()
    {
        _database = await PostgresTestDatabase.CreateAsync();
        Options = new DbContextOptionsBuilder<AppDbContext>()
            .UseNpgsql(_database.ConnectionString, npgsql => npgsql.CommandTimeout(60))
            .Options;

        await using var db = CreateContext();
        await db.Database.MigrateAsync();
    }

    public AppDbContext CreateContext()
        => new(Options, TimeProvider.System);

    public async Task DisposeAsync()
    {
        if (_database is not null)
        {
            await _database.DisposeAsync();
        }
    }
}

[CollectionDefinition(Name)]
public sealed class PostgresSchemaCollection : ICollectionFixture<PostgresSchemaFixture>
{
    public const string Name = "postgres-schema";
}
