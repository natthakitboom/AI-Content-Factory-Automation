using System.Text.RegularExpressions;

namespace ContentFactory.UnitTests;

/// <summary>
/// T00 bootstrap checks. These tests deliberately inspect repository files instead of
/// starting services so they can run on a machine without FFmpeg, LM Studio or a database.
/// </summary>
public sealed class RepositoryBootstrapTests
{
    private static string RepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "ContentFactory.slnx")))
            {
                return directory.FullName;
            }

            directory = directory.Parent;
        }

        throw new InvalidOperationException("Could not locate repository root (ContentFactory.slnx).");
    }

    private static string ReadRepositoryFile(string relativePath)
    {
        var path = Path.Combine(RepositoryRoot(), relativePath);
        Assert.True(File.Exists(path), $"Expected repository file to exist: {relativePath}");
        return File.ReadAllText(path);
    }

    [Fact]
    public void RepositoryContainsRequiredSafetyDefaults()
    {
        var envExample = ReadRepositoryFile(".env.example");

        Assert.Contains("ALLOW_PAID_APIS=false", envExample, StringComparison.Ordinal);
        Assert.Contains("AUTO_PUBLISH=false", envExample, StringComparison.Ordinal);
        Assert.Contains("PUBLISHING_MODE=manual_export", envExample, StringComparison.Ordinal);
        Assert.Contains("TEXT_PROVIDER=mock", envExample, StringComparison.Ordinal);
        Assert.Contains("VIDEO_PROVIDER=mock", envExample, StringComparison.Ordinal);
        Assert.Contains("ENABLE_SCHEDULES=false", envExample, StringComparison.Ordinal);
    }

    [Fact]
    public void EnvExampleContainsNoRealSecrets()
    {
        var envExample = ReadRepositoryFile(".env.example");
        var sensitiveNames = new[]
        {
            "DATABASE_PASSWORD",
            "FAL_KEY",
            "AZURE_SPEECH_KEY",
            "LM_STUDIO_API_TOKEN",
            "LIVE_APPROVAL_FILE"
        };

        foreach (var line in envExample.Split('\n'))
        {
            var trimmed = line.Trim();
            if (trimmed.Length == 0 || trimmed.StartsWith('#'))
            {
                continue;
            }

            var separator = trimmed.IndexOf('=');
            Assert.True(separator > 0, $"Malformed .env.example line: {trimmed}");

            var name = trimmed[..separator];
            var value = trimmed[(separator + 1)..];

            if (sensitiveNames.Contains(name, StringComparer.Ordinal))
            {
                Assert.Equal(string.Empty, value);
            }

            Assert.DoesNotMatch(new Regex("(sk-|key-|secret-)[A-Za-z0-9]{8,}"), trimmed);
        }
    }

    [Fact]
    public void SolutionPinsDotNet10AndNode24()
    {
        var globalJson = ReadRepositoryFile("global.json");
        Assert.Contains("\"version\": \"10.0.402\"", globalJson, StringComparison.Ordinal);
        Assert.Contains("\"rollForward\": \"latestPatch\"", globalJson, StringComparison.Ordinal);

        var nodeVersion = ReadRepositoryFile(".node-version").Trim();
        Assert.Matches(new Regex(@"^24\.\d+\.\d+$"), nodeVersion);
    }

    [Fact]
    public void PackagesAreCentrallyManagedAndPinned()
    {
        var packages = ReadRepositoryFile("Directory.Packages.props");

        Assert.Contains("<ManagePackageVersionsCentrally>true</ManagePackageVersionsCentrally>", packages, StringComparison.Ordinal);
        Assert.Contains("Npgsql.EntityFrameworkCore.PostgreSQL", packages, StringComparison.Ordinal);
        Assert.Contains("PackageVersion Include=\"xunit\" Version=\"", packages, StringComparison.Ordinal);
        Assert.DoesNotContain("Version=\"*\"", packages, StringComparison.Ordinal);
        Assert.DoesNotContain("Version=\"latest\"", packages, StringComparison.Ordinal);

        var projectFiles = Directory.EnumerateFiles(
            Path.Combine(RepositoryRoot(), "src"),
            "*.csproj",
            SearchOption.AllDirectories);
        foreach (var projectFile in projectFiles)
        {
            var contents = File.ReadAllText(projectFile);
            Assert.DoesNotMatch(
                new Regex("<PackageReference[^>]*Version="),
                contents);
            Assert.DoesNotContain("<Version>", contents, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void WorkerIsASeparateProcessFromApi()
    {
        var apiProject = ReadRepositoryFile("src/ContentFactory.Api/ContentFactory.Api.csproj");
        var workerProject = ReadRepositoryFile("src/ContentFactory.Worker/ContentFactory.Worker.csproj");

        Assert.Contains("Microsoft.NET.Sdk.Web", apiProject, StringComparison.Ordinal);
        Assert.DoesNotContain("ContentFactory.Worker", apiProject, StringComparison.Ordinal);
        Assert.DoesNotContain("ContentFactory.Api", workerProject, StringComparison.Ordinal);
        Assert.Contains("Microsoft.NET.Sdk.Worker", workerProject, StringComparison.Ordinal);
    }

    [Fact]
    public void GitIgnoreExcludesSecretsAndRuntimeData()
    {
        var gitignore = ReadRepositoryFile(".gitignore");

        Assert.Contains(".env", gitignore, StringComparison.Ordinal);
        Assert.Contains("bin/", gitignore, StringComparison.Ordinal);
        Assert.Contains("obj/", gitignore, StringComparison.Ordinal);
        Assert.Contains("node_modules/", gitignore, StringComparison.Ordinal);
        Assert.Contains("_data/", gitignore, StringComparison.Ordinal);
    }

    [Fact]
    public void DoctorScriptExistsAndDocumentsExitCodes()
    {
        var doctorPath = Path.Combine(RepositoryRoot(), "scripts/doctor.sh");
        Assert.True(File.Exists(doctorPath), "scripts/doctor.sh must exist");

        var doctor = File.ReadAllText(doctorPath);
        Assert.Contains("uname -m", doctor, StringComparison.Ordinal);
        Assert.Contains("sw_vers", doctor, StringComparison.Ordinal);
        Assert.Contains("hw.memsize", doctor, StringComparison.Ordinal);
        Assert.Contains("dotnet --info", doctor, StringComparison.Ordinal);
        Assert.Contains("node --version", doctor, StringComparison.Ordinal);
        Assert.Contains("docker version", doctor, StringComparison.Ordinal);
        Assert.Contains("ffmpeg -version", doctor, StringComparison.Ordinal);
        Assert.Contains("ffprobe -version", doctor, StringComparison.Ordinal);
        Assert.Contains("exit 2", doctor, StringComparison.Ordinal);
        Assert.Contains("exit 1", doctor, StringComparison.Ordinal);

        if (!OperatingSystem.IsWindows())
        {
            var mode = File.GetUnixFileMode(doctorPath);
            Assert.True(
                mode.HasFlag(UnixFileMode.UserExecute),
                "scripts/doctor.sh must be executable");
        }
    }
}
