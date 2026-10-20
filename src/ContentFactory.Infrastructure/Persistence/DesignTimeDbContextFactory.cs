using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;
using Npgsql;

namespace ContentFactory.Infrastructure.Persistence;

/// <summary>
/// Used only by `dotnet ef` for local development and migrations. It never falls back to
/// a production secret and never reads the test connection string.
/// </summary>
public sealed class DesignTimeDbContextFactory : IDesignTimeDbContextFactory<AppDbContext>
{
    public AppDbContext CreateDbContext(string[] args)
    {
        var connectionString = Environment.GetEnvironmentVariable("ConnectionStrings__App");
        if (string.IsNullOrWhiteSpace(connectionString))
        {
            connectionString = new NpgsqlConnectionStringBuilder
            {
                Host = Environment.GetEnvironmentVariable("DATABASE_HOST") ?? "127.0.0.1",
                Port = int.TryParse(Environment.GetEnvironmentVariable("DATABASE_PORT"), out var port) ? port : 54329,
                Database = Environment.GetEnvironmentVariable("DATABASE_NAME") ?? "content_factory",
                Username = Environment.GetEnvironmentVariable("DATABASE_USER") ?? "content_factory",
                Password = Environment.GetEnvironmentVariable("DATABASE_PASSWORD") ?? string.Empty,
                Pooling = false,
                Timeout = 10
            }.ConnectionString;
        }

        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseNpgsql(connectionString)
            .Options;

        return new AppDbContext(options);
    }
}
