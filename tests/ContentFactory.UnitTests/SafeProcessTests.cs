using ContentFactory.Core.Exceptions;
using ContentFactory.Infrastructure.Media;

namespace ContentFactory.UnitTests;

/// <summary>
/// T04 process-boundary tests. Children are started with ArgumentList and no shell, receive
/// a sanitized environment, have capped output and are killed as a tree on timeout or
/// cancellation.
/// </summary>
public sealed class SafeProcessTests
{
    [Fact]
    public async Task ShellMetacharactersRemainArguments()
    {
        var runner = CreateRunner("/usr/bin/printf");
        var arguments = new[]
        {
            "%s\n",
            "; rm -rf /",
            "$(whoami)",
            "`id`",
            "a b",
            "quote\"inside",
            "รูปสินค้า; & | > < * ?"
        };

        var result = await runner.RunAsync(
            "/usr/bin/printf",
            arguments,
            TimeSpan.FromSeconds(10),
            CancellationToken.None);

        Assert.Equal(0, result.ExitCode);
        Assert.False(result.TimedOut);
        var lines = result.StandardOutput
            .Split('\n', StringSplitOptions.RemoveEmptyEntries);
        Assert.Equal(arguments.Skip(1), lines);
    }

    [Fact]
    public async Task CancellationStopsChildProcess()
    {
        using var sandbox = new SandboxDirectory();
        var script = Path.Combine(sandbox.Path, "tick.sh");
        var marker = Path.Combine(sandbox.Path, "ticks.txt");
        await File.WriteAllTextAsync(
            script,
            "#!/bin/sh\nwhile true; do echo tick >> \"$1\"; sleep 0.1; done\n");

        var runner = CreateRunner("/bin/sh");
        using var cancellation = new CancellationTokenSource();
        var run = runner.RunAsync(
            "/bin/sh",
            [script, marker],
            TimeSpan.FromSeconds(30),
            cancellation.Token);

        await WaitForContentAsync(marker, TimeSpan.FromSeconds(5));
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => run);

        await Task.Delay(500);
        var before = (await File.ReadAllTextAsync(marker)).Length;
        await Task.Delay(700);
        var after = (await File.ReadAllTextAsync(marker)).Length;

        Assert.Equal(before, after);
    }

    [Fact]
    public async Task TimeoutStopsChildProcess()
    {
        var runner = CreateRunner("/bin/sleep");

        var result = await runner.RunAsync(
            "/bin/sleep",
            ["30"],
            TimeSpan.FromMilliseconds(200),
            CancellationToken.None);

        Assert.True(result.TimedOut);
        Assert.True(result.Duration < TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task OutputIsCapped()
    {
        var runner = new SafeProcessRunner(new SafeProcessRunnerOptions
        {
            AllowedExecutables = ["/usr/bin/yes"],
            MaxOutputChars = 1024
        });

        var result = await runner.RunAsync(
            "/usr/bin/yes",
            [],
            TimeSpan.FromMilliseconds(300),
            CancellationToken.None);

        Assert.True(result.TimedOut);
        Assert.True(result.OutputTruncated);
        Assert.True(result.StandardOutput.Length <= 1024);
    }

    [Fact]
    public async Task SecretEnvironmentIsNotPassedToChild()
    {
        const string name = "CF_TEST_SECRET_ENV";
        const string value = "do-not-leak-123";
        var previous = Environment.GetEnvironmentVariable(name);

        try
        {
            Environment.SetEnvironmentVariable(name, value);
            var runner = CreateRunner("/usr/bin/env");

            var result = await runner.RunAsync(
                "/usr/bin/env",
                [],
                TimeSpan.FromSeconds(10),
                CancellationToken.None);

            Assert.Equal(0, result.ExitCode);
            Assert.DoesNotContain(value, result.StandardOutput, StringComparison.Ordinal);
            Assert.DoesNotContain(name, result.StandardOutput, StringComparison.Ordinal);
        }
        finally
        {
            Environment.SetEnvironmentVariable(name, previous);
        }
    }

    [Fact]
    public async Task ExecutableOutsideAllowlistIsRejected()
    {
        var runner = CreateRunner("/usr/bin/printf");

        var exception = await Assert.ThrowsAsync<ValidationException>(() => runner.RunAsync(
            "/bin/echo",
            ["secret-value"],
            TimeSpan.FromSeconds(5),
            CancellationToken.None));

        Assert.Equal("PROCESS_NOT_ALLOWED", exception.Code);
        Assert.DoesNotContain("secret-value", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task NullOrNulArgumentIsRejected()
    {
        var runner = CreateRunner("/usr/bin/printf");

        await Assert.ThrowsAnyAsync<ArgumentException>(() => runner.RunAsync(
            "/usr/bin/printf",
            ["%s", null!],
            TimeSpan.FromSeconds(5),
            CancellationToken.None));

        await Assert.ThrowsAnyAsync<ArgumentException>(() => runner.RunAsync(
            "/usr/bin/printf",
            ["%s", "safe\0unsafe"],
            TimeSpan.FromSeconds(5),
            CancellationToken.None));
    }

    private static SafeProcessRunner CreateRunner(params string[] allowed)
        => new(new SafeProcessRunnerOptions
        {
            AllowedExecutables = allowed,
            MaxOutputChars = 4096
        });

    private static async Task WaitForContentAsync(string path, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            if (File.Exists(path) && new FileInfo(path).Length > 0)
            {
                return;
            }

            await Task.Delay(20);
        }

        throw new TimeoutException("The child process did not write its marker in time.");
    }

    private sealed class SandboxDirectory : IDisposable
    {
        public string Path { get; } = System.IO.Path.Combine(
            System.IO.Path.GetTempPath(),
            "cf-process-" + Guid.NewGuid().ToString("N"));

        public SandboxDirectory()
        {
            Directory.CreateDirectory(Path);
        }

        public void Dispose()
        {
            try
            {
                Directory.Delete(Path, recursive: true);
            }
            catch (IOException)
            {
                // Best-effort cleanup for a test sandbox.
            }
        }
    }
}
