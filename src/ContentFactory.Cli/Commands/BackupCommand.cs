using ContentFactory.Core.Exceptions;
using ContentFactory.Infrastructure.Jobs;
using ContentFactory.Infrastructure.Persistence;
using ContentFactory.Infrastructure.Storage;
using Npgsql;

namespace ContentFactory.Cli.Commands;

/// <summary>
/// <c>backup &lt;new-directory&gt;</c>, <c>cleanup</c> and <c>resume-paid-lanes</c>. The backup
/// holds no environment secrets and is written with owner-only permissions; keep an
/// encrypted copy off this device yourself (see docs/runbook).
/// </summary>
internal sealed class BackupCommand
{
    private readonly AppDbContext _db;

    public BackupCommand(AppDbContext db)
    {
        _db = db;
    }

    public async Task<int> BackupAsync(string[] args)
    {
        if (args.Length < 2)
        {
            Console.Error.WriteLine("Usage: backup <new-backup-directory>");
            return 2;
        }

        var database = Environment.GetEnvironmentVariable("DATABASE_NAME") ?? "content_factory";
        var service = new BackupService(
            _db,
            StorageOptions.Create(Environment.GetEnvironmentVariable("DATA_ROOT")),
            Tool(),
            new SystemFlagStore(_db),
            new PostgresJobStore(_db, TimeProvider.System),
            TimeProvider.System);
        return await RunAsync(async () =>
        {
            var manifest = await service.CreateAsync(args[1], database, CancellationToken.None);
            Console.WriteLine($"Backup written: {manifest.Assets.Count} assets, dump hash {manifest.DatabaseDumpSha256[..12]}.");
            Console.WriteLine(manifest.Note);
        });
    }

    public async Task<int> CleanupAsync()
    {
        var service = new RetentionService(
            _db, StorageOptions.Create(Environment.GetEnvironmentVariable("DATA_ROOT")), TimeProvider.System);
        return await RunAsync(async () =>
        {
            var report = await service.CleanupAsync(CancellationToken.None);
            Console.WriteLine($"Deleted {report.DeletedAssetIds.Count} unreferenced assets and {report.DeletedTempFiles} stale temp files.");
            if (report.TempSkippedBecauseJobsRunning)
            {
                Console.WriteLine("Temp cleanup was skipped because a job is running.");
            }
        });
    }

    public async Task<int> ResumePaidLanesAsync(string[] args)
    {
        if (args.Length < 2 || !string.Equals(args[1], "--reconciled", StringComparison.Ordinal))
        {
            Console.Error.WriteLine(
                "Run only after the cost ledger matches the provider history: resume-paid-lanes --reconciled");
            return 2;
        }

        await new SystemFlagStore(_db).SetAsync(SystemFlagStore.PaidLanesPaused, null);
        _db.AuditEvents.Add(new ContentFactory.Infrastructure.Persistence.Entities.AuditEvent
        {
            Id = Guid.NewGuid(), Actor = ReconcileCommand.Actor, Action = "system.paid_lanes_resumed",
            TargetType = "system", DetailsJson = "{}", CreatedAt = DateTimeOffset.UtcNow
        });
        await _db.SaveChangesAsync();
        Console.WriteLine("Paid lanes are no longer paused. Paused jobs still need an explicit resume.");
        return 0;
    }

    public static async Task<int> RestoreAsync(string[] args)
    {
        // restore <backup-dir> <new-database> <new-data-root> --confirm "restore <new-database>"
        if (args.Length < 6 || args[4] != "--confirm")
        {
            Console.Error.WriteLine(
                "Usage: restore <backup-dir> <new-database> <new-data-root> --confirm \"restore <new-database>\"");
            return 2;
        }

        var admin = new NpgsqlConnectionStringBuilder(DatabaseConnection.FromEnvironment()) { Database = "postgres" };
        var service = new RestoreService(Tool(), admin.ConnectionString, TimeProvider.System);
        return await RunAsync(async () =>
        {
            var report = await service.RestoreAsync(args[1], args[2], args[3], args[5], CancellationToken.None);
            Console.WriteLine($"Restored {report.AssetCount} assets into database {report.TargetDatabase} at {report.TargetDataRoot}.");
            foreach (var warning in report.Warnings)
            {
                Console.WriteLine("! " + warning);
            }
        });
    }

    private static DockerPostgresTool Tool()
        => new(
            Environment.GetEnvironmentVariable("POSTGRES_CONTAINER") ?? "content-factory-postgres",
            Environment.GetEnvironmentVariable("DATABASE_USER") ?? "content_factory");

    private static async Task<int> RunAsync(Func<Task> action)
    {
        try
        {
            await action();
            return 0;
        }
        catch (Exception exception) when (exception is ValidationException or ConflictException)
        {
            Console.Error.WriteLine(exception.Message);
            return 2;
        }
    }
}
