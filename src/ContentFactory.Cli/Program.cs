using ContentFactory.Cli.Commands;
using ContentFactory.Infrastructure.Persistence;
using ContentFactory.Infrastructure.Persistence.Entities;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;

namespace ContentFactory.Cli;

internal static class CliEntryPoint
{
    private static async Task<int> Main(string[] args)
    {
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseNpgsql(DatabaseConnection.FromEnvironment())
            .Options;

        await using var db = new AppDbContext(options);

        if (args.Length > 0 && string.Equals(args[0], "init-owner", StringComparison.Ordinal))
        {
            var username = args.Length > 1 ? args[1] : "owner";

            var password = InitOwnerCommand.ReadHiddenPassword("Password: ");
            if (password.Length == 0)
            {
                return 2;
            }

            var confirmation = InitOwnerCommand.ReadHiddenPassword("Confirm password: ");
            if (!string.Equals(password, confirmation, StringComparison.Ordinal))
            {
                Console.Error.WriteLine("Passwords do not match.");
                return 2;
            }

            var hasher = new PasswordHasher<OwnerUser>();
            return await new InitOwnerCommand(db, hasher).ExecuteAsync(username, password);
        }

        if (args.Length > 0 && string.Equals(args[0], "reconcile-attach", StringComparison.Ordinal))
        {
            return await new ReconcileCommand(db).AttachAsync(args);
        }

        if (args.Length > 0 && string.Equals(args[0], "reconcile-no-charge", StringComparison.Ordinal))
        {
            return await new ReconcileCommand(db).NoChargeAsync(args);
        }

        if (args.Length > 0 && args[0] == "backup")
        {
            return await new BackupCommand(db).BackupAsync(args);
        }

        if (args.Length > 0 && args[0] == "restore")
        {
            return await BackupCommand.RestoreAsync(args);
        }

        if (args.Length > 0 && args[0] == "cleanup")
        {
            return await new BackupCommand(db).CleanupAsync();
        }

        if (args.Length > 0 && args[0] == "resume-paid-lanes")
        {
            return await new BackupCommand(db).ResumePaidLanesAsync(args);
        }

        Console.Error.WriteLine("Usage: ContentFactory.Cli init-owner [username]");
        Console.Error.WriteLine("Creates the single local owner with a hidden password prompt.");
        Console.Error.WriteLine("       ContentFactory.Cli reconcile-attach <row-id> <fal-request-id> <status-url> <response-url> [cancel-url]");
        Console.Error.WriteLine("       ContentFactory.Cli reconcile-no-charge <row-id> <reason...>");
        Console.Error.WriteLine("       ContentFactory.Cli backup <new-directory> | cleanup | resume-paid-lanes --reconciled");
        Console.Error.WriteLine("       ContentFactory.Cli restore <backup-dir> <new-database> <new-data-root> --confirm \"restore <new-database>\"");
        return 2;
    }
}
