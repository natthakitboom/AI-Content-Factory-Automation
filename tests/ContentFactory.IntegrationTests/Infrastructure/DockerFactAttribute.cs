using System.Diagnostics;

namespace ContentFactory.IntegrationTests.Infrastructure;

/// <summary>
/// Backup tests run the real <c>pg_dump</c>/<c>pg_restore</c> inside the project's PostgreSQL
/// container. When Docker or the container is unavailable they are skipped, never passed.
/// </summary>
public sealed class DockerFactAttribute : FactAttribute
{
    public const string Container = "content-factory-postgres";
    private static readonly string? MissingReason = Probe();

    public DockerFactAttribute()
    {
        if (MissingReason is not null)
        {
            Skip = MissingReason;
        }
    }

    private static string? Probe()
    {
        try
        {
            using var process = Process.Start(new ProcessStartInfo("docker", $"exec {Container} pg_dump --version")
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false
            })!;
            process.StandardOutput.ReadToEnd();
            process.StandardError.ReadToEnd();
            process.WaitForExit(20_000);
            return process.ExitCode == 0
                ? null
                : $"The {Container} container is not running; backup tests are blocked.";
        }
        catch (Exception)
        {
            return "Docker is unavailable; backup tests are blocked.";
        }
    }
}
