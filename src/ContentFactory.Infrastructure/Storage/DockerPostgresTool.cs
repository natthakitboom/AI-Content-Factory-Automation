using System.Diagnostics;
using System.Text.RegularExpressions;
using ContentFactory.Core.Exceptions;

namespace ContentFactory.Infrastructure.Storage;

/// <summary>Logical dump and restore of one database.</summary>
public interface IDatabaseTool
{
    Task DumpAsync(string database, string outputFile, CancellationToken ct);

    Task RestoreAsync(string database, string dumpFile, CancellationToken ct);
}

/// <summary>
/// Runs the PostgreSQL client tools inside the project's own container, so the dump always
/// matches the server version and no client has to be installed on the host. Arguments are
/// passed as a list (no shell), names are validated, and no password is ever on a command line.
/// </summary>
public sealed partial class DockerPostgresTool : IDatabaseTool
{
    private readonly string _container;
    private readonly string _user;

    public DockerPostgresTool(string container, string user)
    {
        if (!SafeName().IsMatch(container) || !SafeName().IsMatch(user))
        {
            throw new ValidationException("BACKUP_TOOL_CONFIG_INVALID", "The container and user names are not safe.");
        }

        _container = container;
        _user = user;
    }

    public async Task DumpAsync(string database, string outputFile, CancellationToken ct)
    {
        RequireDatabase(database);
        await using var output = new FileStream(outputFile, FileMode.CreateNew, FileAccess.Write, FileShare.None);
        OwnerOnlyPermissions.File(outputFile);
        await RunAsync(
            ["exec", _container, "pg_dump", "-U", _user, "-d", database, "-Fc", "--no-owner", "--no-acl"],
            stdin: null, stdout: output, ct);
    }

    public async Task RestoreAsync(string database, string dumpFile, CancellationToken ct)
    {
        RequireDatabase(database);
        await using var input = new FileStream(dumpFile, FileMode.Open, FileAccess.Read, FileShare.Read);
        await RunAsync(
            ["exec", "-i", _container, "pg_restore", "-U", _user, "-d", database, "--no-owner", "--no-acl", "--exit-on-error"],
            stdin: input, stdout: null, ct);
    }

    private static async Task RunAsync(string[] arguments, Stream? stdin, Stream? stdout, CancellationToken ct)
    {
        var info = new ProcessStartInfo("docker")
        {
            RedirectStandardInput = stdin is not null,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false
        };
        foreach (var argument in arguments)
        {
            info.ArgumentList.Add(argument);
        }

        using var process = Process.Start(info)
            ?? throw new ValidationException("BACKUP_TOOL_FAILED", "Docker could not be started.");
        var error = process.StandardError.ReadToEndAsync(ct);
        var copyOut = stdout is null
            ? process.StandardOutput.BaseStream.CopyToAsync(Stream.Null, ct)
            : process.StandardOutput.BaseStream.CopyToAsync(stdout, ct);
        if (stdin is not null)
        {
            await stdin.CopyToAsync(process.StandardInput.BaseStream, ct);
            process.StandardInput.Close();
        }

        await copyOut;
        await process.WaitForExitAsync(ct);
        var message = await error;
        if (process.ExitCode != 0)
        {
            throw new ValidationException(
                "BACKUP_TOOL_FAILED",
                $"The PostgreSQL tool exited with code {process.ExitCode}: {Truncate(message)}");
        }
    }

    private static string Truncate(string text) => text.Length <= 400 ? text : text[..400];

    private static void RequireDatabase(string database)
    {
        if (!DatabaseName().IsMatch(database))
        {
            throw new ValidationException(
                "BACKUP_DATABASE_NAME_INVALID", "A database name uses letters, digits and underscores only.");
        }
    }

    [GeneratedRegex("^[A-Za-z0-9_.-]{1,63}$")]
    private static partial Regex SafeName();

    [GeneratedRegex("^[A-Za-z0-9_]{1,63}$")]
    private static partial Regex DatabaseName();
}
