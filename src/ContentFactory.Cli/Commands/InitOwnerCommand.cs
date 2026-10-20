using System.Text.RegularExpressions;
using ContentFactory.Infrastructure.Persistence;
using ContentFactory.Infrastructure.Persistence.Entities;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;

namespace ContentFactory.Cli.Commands;

/// <summary>
/// Creates or updates the single local owner. The password is read without echo and is
/// never written to logs, files or the shell history by this command.
/// </summary>
public sealed class InitOwnerCommand
{
    private readonly AppDbContext _db;
    private readonly IPasswordHasher<OwnerUser> _hasher;

    public InitOwnerCommand(AppDbContext db, IPasswordHasher<OwnerUser> hasher)
    {
        _db = db;
        _hasher = hasher;
    }

    public async Task<int> ExecuteAsync(string username, string password, CancellationToken ct = default)
    {
        if (!Regex.IsMatch(username, "^[A-Za-z0-9._-]{3,64}$", RegexOptions.CultureInvariant))
        {
            Console.Error.WriteLine("Username must be 3-64 chars: letters, digits, dot, underscore or hyphen.");
            return 2;
        }

        if (password.Length < 12)
        {
            Console.Error.WriteLine("Password must be at least 12 characters.");
            return 2;
        }

        var owner = await _db.OwnerUsers.SingleOrDefaultAsync(x => x.Username == username, ct);
        if (owner is null)
        {
            owner = new OwnerUser
            {
                Id = Guid.NewGuid(),
                Username = username,
                Active = true,
                CreatedAt = DateTimeOffset.UtcNow
            };
            _db.OwnerUsers.Add(owner);
            Console.WriteLine("Owner account created.");
        }
        else
        {
            owner.Active = true;
            owner.SessionVersion++;
            Console.WriteLine("Owner account updated; previous sessions were invalidated.");
        }

        owner.PasswordHash = _hasher.HashPassword(owner, password);
        await _db.SaveChangesAsync(ct);
        return 0;
    }

    public static string ReadHiddenPassword(string prompt)
    {
        Console.Write(prompt);
        if (Console.IsInputRedirected)
        {
            Console.Error.WriteLine();
            Console.Error.WriteLine("Refusing to read a password from redirected input; run interactively.");
            return string.Empty;
        }

        var buffer = new List<char>();
        while (true)
        {
            var key = Console.ReadKey(intercept: true);
            if (key.Key == ConsoleKey.Enter)
            {
                Console.WriteLine();
                return new string(buffer.ToArray());
            }

            if (key.Key == ConsoleKey.Backspace)
            {
                if (buffer.Count > 0)
                {
                    buffer.RemoveAt(buffer.Count - 1);
                }

                continue;
            }

            if (!char.IsControl(key.KeyChar))
            {
                buffer.Add(key.KeyChar);
            }
        }
    }
}
