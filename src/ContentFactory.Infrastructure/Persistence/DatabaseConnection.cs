using Npgsql;

namespace ContentFactory.Infrastructure.Persistence;

public static class DatabaseConnection
{
    /// <summary>
    /// Builds the local PostgreSQL connection string from environment values. This is
    /// deliberately loopback-only and never discovers credentials elsewhere.
    /// </summary>
    public static string FromEnvironment()
    {
        var password = Environment.GetEnvironmentVariable("DATABASE_PASSWORD") ?? string.Empty;
        if (string.IsNullOrWhiteSpace(password))
        {
            throw new InvalidOperationException(
                "DATABASE_PASSWORD is empty. Copy .env.example to .env and set a local password.");
        }

        return new NpgsqlConnectionStringBuilder
        {
            Host = Environment.GetEnvironmentVariable("DATABASE_HOST") ?? "127.0.0.1",
            Port = int.TryParse(Environment.GetEnvironmentVariable("DATABASE_PORT"), out var port) ? port : 54329,
            Database = Environment.GetEnvironmentVariable("DATABASE_NAME") ?? "content_factory",
            Username = Environment.GetEnvironmentVariable("DATABASE_USER") ?? "content_factory",
            Password = password,
            Pooling = true,
            MaxPoolSize = 20,
            Timeout = 10,
            CommandTimeout = 60
        }.ConnectionString;
    }
}
