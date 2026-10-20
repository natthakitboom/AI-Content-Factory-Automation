using Npgsql;

namespace ContentFactory.IntegrationTests.Infrastructure;

/// <summary>
/// Creates and drops an isolated PostgreSQL database named cf_test_&lt;runid&gt;.
/// It requires an explicit maintenance connection and never falls back to app settings.
/// </summary>
public sealed class PostgresTestDatabase : IAsyncDisposable
{
    private const string Prefix = "cf_test_";
    private readonly string _adminConnectionString;
    private int _disposed;

    private PostgresTestDatabase(string adminConnectionString, string databaseName)
    {
        _adminConnectionString = adminConnectionString;
        DatabaseName = databaseName;

        var builder = new NpgsqlConnectionStringBuilder(adminConnectionString)
        {
            Database = databaseName,
            Pooling = false,
            Timeout = 10,
            CommandTimeout = 60,
            IncludeErrorDetail = true
        };
        ConnectionString = builder.ConnectionString;
    }

    public string DatabaseName { get; }
    public string ConnectionString { get; }

    public static async Task<PostgresTestDatabase> CreateAsync(CancellationToken ct = default)
    {
        var admin = Environment.GetEnvironmentVariable("CF_TEST_POSTGRES");
        if (string.IsNullOrWhiteSpace(admin))
        {
            throw new InvalidOperationException(
                "CF_TEST_POSTGRES is not set. Export an explicit maintenance connection string, " +
                "for example Host=127.0.0.1;Port=54329;Database=postgres;Username=content_factory;Password=...");
        }

        var builder = new NpgsqlConnectionStringBuilder(admin);
        if (string.IsNullOrWhiteSpace(builder.Database))
        {
            builder.Database = "postgres";
        }

        if (string.Equals(builder.Database, "content_factory", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                "CF_TEST_POSTGRES must point at a maintenance database, not the app database.");
        }

        var databaseName = Prefix + Guid.NewGuid().ToString("N")[..12];
        if (!databaseName.StartsWith(Prefix, StringComparison.Ordinal))
        {
            throw new InvalidOperationException("Generated test database name lost its prefix.");
        }

        var database = new PostgresTestDatabase(builder.ConnectionString, databaseName);
        await database.CreateDatabaseAsync(ct);
        return database;
    }

    private async Task CreateDatabaseAsync(CancellationToken ct)
    {
        await using var connection = new NpgsqlConnection(_adminConnectionString);
        await connection.OpenAsync(ct);
        await using var command = connection.CreateCommand();
        command.CommandText = $"CREATE DATABASE \"{DatabaseName}\" TEMPLATE template0 ENCODING 'UTF8'";
        await command.ExecuteNonQueryAsync(ct);
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 1)
        {
            return;
        }

        NpgsqlConnection.ClearAllPools();

        for (var attempt = 0; attempt < 3; attempt++)
        {
            try
            {
                await using var connection = new NpgsqlConnection(_adminConnectionString);
                await connection.OpenAsync();

                await using (var terminate = connection.CreateCommand())
                {
                    terminate.CommandText =
                        "SELECT pg_terminate_backend(pid) FROM pg_stat_activity " +
                        "WHERE datname = @name AND pid <> pg_backend_pid()";
                    terminate.Parameters.AddWithValue("name", DatabaseName);
                    await terminate.ExecuteNonQueryAsync();
                }

                await using var drop = connection.CreateCommand();
                drop.CommandText = $"DROP DATABASE IF EXISTS \"{DatabaseName}\" WITH (FORCE)";
                await drop.ExecuteNonQueryAsync();
                return;
            }
            catch (PostgresException) when (attempt < 2)
            {
                await Task.Delay(200 * (attempt + 1));
            }
        }
    }
}
