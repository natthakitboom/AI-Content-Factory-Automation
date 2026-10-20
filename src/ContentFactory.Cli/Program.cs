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

        Console.Error.WriteLine("Usage: ContentFactory.Cli init-owner [username]");
        Console.Error.WriteLine("Creates the single local owner with a hidden password prompt.");
        return 2;
    }
}
