#pragma warning disable CA1416 // These checks read Unix file modes and run on macOS/Linux only.
using System.Diagnostics;
using System.Text.RegularExpressions;

namespace ContentFactory.UnitTests;

/// <summary>
/// T23 release checks. They read the repository's scripts, pipeline and docs, so they run
/// without Docker, a database or FFmpeg, and they prove the safety promises of the release.
/// </summary>
public sealed class ReleaseReadinessTests
{
    private static string Root()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "ContentFactory.slnx")))
        {
            directory = directory.Parent;
        }

        return directory?.FullName ?? throw new InvalidOperationException("Repository root not found.");
    }

    private static string Read(string relative)
    {
        var path = Path.Combine(Root(), relative);
        Assert.True(File.Exists(path), $"Expected {relative} to exist.");
        return File.ReadAllText(path);
    }

    private static bool IsExecutable(string relative)
        => (File.GetUnixFileMode(Path.Combine(Root(), relative)) & UnixFileMode.UserExecute) != 0;

    [Fact]
    public void BuildNeverRequiresPaidKeys()
    {
        var pipeline = Read("azure-pipelines.yml");
        var verify = Read("scripts/verify-all.sh");

        foreach (var text in new[] { pipeline, verify })
        {
            Assert.DoesNotMatch(@"\$\((FAL_KEY|AZURE_SPEECH_KEY|LM_STUDIO_API_TOKEN)\)", text);
            Assert.DoesNotMatch(@"(?m)^\s*-?\s*(FAL_KEY|AZURE_SPEECH_KEY)\s*:\s*\S+", text);
        }

        // Verification pins every provider to mock and keeps paid work off.
        Assert.Contains("ALLOW_PAID_APIS=false", verify, StringComparison.Ordinal);
        Assert.Contains("VIDEO_PROVIDER=mock", verify, StringComparison.Ordinal);
        Assert.Contains("FAL_KEY=", verify, StringComparison.Ordinal);
        Assert.Contains("ALLOW_PAID_APIS: 'false'", pipeline, StringComparison.Ordinal);
        Assert.Contains("VIDEO_PROVIDER: 'mock'", pipeline, StringComparison.Ordinal);
    }

    [Fact]
    public void ProductionDefaultsRemainSafe()
    {
        var env = Read(".env.example");
        foreach (var line in new[]
                 {
                     "ALLOW_PAID_APIS=false", "AUTO_PUBLISH=false", "PUBLISHING_MODE=manual_export",
                     "TEXT_PROVIDER=mock", "VIDEO_PROVIDER=mock", "ENABLE_SCHEDULES=false"
                 })
        {
            Assert.Contains(line, env, StringComparison.Ordinal);
        }

        Assert.Matches(@"(?m)^FAL_KEY=$", env);
        Assert.Matches(@"(?m)^LIVE_APPROVAL_FILE=$", env);
        Assert.Contains("127.0.0.1", Read("infra/compose.yaml"), StringComparison.Ordinal);
        Assert.DoesNotContain("0.0.0.0", Read("scripts/dev-start.sh"), StringComparison.Ordinal);
        Assert.Contains("release: mock/local-only", Read("docs/operator-runbook.md"), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void FreshCloneCanRunMockFlow()
    {
        var makefile = Read("Makefile");
        foreach (var target in new[] { "verify:", "dev:", "backup:", "restore-test:", "dev-stop:" })
        {
            Assert.Matches("(?m)^" + Regex.Escape(target), makefile);
        }

        foreach (var script in new[]
                 {
                     "scripts/verify-all.sh", "scripts/verify-no-secrets.sh", "scripts/dev-start.sh",
                     "scripts/dev-stop.sh", "scripts/restore-test.sh"
                 })
        {
            Assert.True(IsExecutable(script), $"{script} must be executable.");
        }

        var runbook = Read("docs/operator-runbook.md");
        foreach (var heading in new[]
                 {
                     "Fresh clone", "Create the owner", "Run the mock flow", "Turn live mode on and off",
                     "Backup and restore", "Cost troubleshooting", "Sleep and wake", "Release checklist"
                 })
        {
            Assert.Contains(heading, runbook, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void StopDoesNotDeleteDatabase()
    {
        var stop = Read("scripts/dev-stop.sh");
        var code = string.Join('\n', stop.Split('\n').Where(l => !l.TrimStart().StartsWith('#')));

        Assert.DoesNotMatch(@"compose[^\n]*\bdown\b", code);
        Assert.DoesNotMatch(@"docker[^\n]*(\s-v\b|--volumes)", code);
        Assert.DoesNotContain("volume rm", code, StringComparison.Ordinal);
        Assert.DoesNotContain("volume prune", code, StringComparison.Ordinal);
        Assert.DoesNotContain("rm -rf", code, StringComparison.Ordinal);
        Assert.DoesNotContain("DROP ", code, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("NEVER", stop, StringComparison.Ordinal);
        Assert.Contains("were not deleted", code, StringComparison.Ordinal);
    }

    [Fact]
    public void PipelineIsExplicitAboutWhatItDoesNotProve()
    {
        var pipeline = Read("azure-pipelines.yml");

        Assert.Contains("libx264", pipeline, StringComparison.Ordinal);
        Assert.Contains("VideoToolbox", pipeline, StringComparison.Ordinal);
        Assert.DoesNotContain("deploy", pipeline, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("git push", pipeline, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void SecretScanFindsNothingInTheRepository()
    {
        var result = RunScript("scripts/verify-no-secrets.sh", Root());

        Assert.Equal(0, result.ExitCode);
        Assert.Contains("0 findings", result.Output, StringComparison.Ordinal);
    }

    [Fact]
    public void SecretScanDetectsAPlantedSecret()
    {
        var temp = Path.Combine(Path.GetTempPath(), "cf-secret-scan-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(temp);
        try
        {
            Run("git", ["init", "-q"], temp);
            File.WriteAllText(Path.Combine(temp, "notes.txt"), "FAL_KEY=abcd1234efgh5678ijkl\n");
            File.WriteAllText(Path.Combine(temp, "key.pem"), "-----BEGIN PRIVATE KEY-----\nabc\n");
            Run("git", ["add", "-A"], temp);

            var result = RunScript("scripts/verify-no-secrets.sh", temp);

            Assert.NotEqual(0, result.ExitCode);
            Assert.Contains("notes.txt", result.Output, StringComparison.Ordinal);
            Assert.Contains("key.pem", result.Output, StringComparison.Ordinal);
            Assert.DoesNotContain("abcd1234efgh5678ijkl", result.Output, StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(temp, recursive: true);
        }
    }

    private static (int ExitCode, string Output) RunScript(string script, string workingDirectory)
        => Run("bash", [Path.Combine(Root(), script)], workingDirectory);

    private static (int ExitCode, string Output) Run(string file, string[] args, string workingDirectory)
    {
        var info = new ProcessStartInfo(file)
        {
            WorkingDirectory = workingDirectory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false
        };
        foreach (var arg in args)
        {
            info.ArgumentList.Add(arg);
        }

        using var process = Process.Start(info)!;
        var output = process.StandardOutput.ReadToEnd() + process.StandardError.ReadToEnd();
        process.WaitForExit(60_000);
        return (process.ExitCode, output);
    }
}
