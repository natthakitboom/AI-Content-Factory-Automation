using System.ComponentModel;
using System.Diagnostics;
using System.Text;
using ContentFactory.Core.Exceptions;

namespace ContentFactory.Infrastructure.Media;

public sealed class SafeProcessRunnerOptions
{
    public static IReadOnlyList<string> DefaultMediaExecutables { get; } = ["ffmpeg", "ffprobe"];

    public static IReadOnlyList<string> DefaultEnvironmentAllowlist { get; } =
    [
        "PATH",
        "HOME",
        "TMPDIR",
        "TEMP",
        "TMP",
        "LANG",
        "LC_ALL",
        "LC_CTYPE",
        "FONTCONFIG_PATH",
        "FONTCONFIG_FILE",
        "SystemRoot",
        "WINDIR",
        "COMSPEC",
        "PATHEXT"
    ];

    public IReadOnlyCollection<string> AllowedExecutables { get; init; } = DefaultMediaExecutables;

    public int MaxOutputChars { get; init; } = 64 * 1024;

    public IReadOnlyCollection<string> EnvironmentAllowlist { get; init; } =
        DefaultEnvironmentAllowlist;

    public string? WorkingDirectory { get; init; }

    internal void Validate()
    {
        if (MaxOutputChars <= 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(MaxOutputChars),
                "The output cap must be positive.");
        }

        if (AllowedExecutables is null || AllowedExecutables.Count == 0)
        {
            throw new ArgumentException(
                "At least one executable must be allowlisted.",
                nameof(AllowedExecutables));
        }

        foreach (var executable in AllowedExecutables)
        {
            if (string.IsNullOrWhiteSpace(executable) || executable.Contains('\0'))
            {
                throw new ArgumentException(
                    "Allowlisted executables must be non-empty names or absolute paths.",
                    nameof(AllowedExecutables));
            }
        }

        if (EnvironmentAllowlist is null)
        {
            throw new ArgumentException(
                "The environment allowlist cannot be null.",
                nameof(EnvironmentAllowlist));
        }
    }
}

/// <summary>
/// Runs allowlisted local executables without a shell. Arguments are passed through
/// <see cref="ProcessStartInfo.ArgumentList"/>, so metacharacters remain literal. The child
/// receives only an explicit environment allowlist, both output streams are capped, and a
/// timeout or cancellation kills the whole process tree.
/// </summary>
public sealed class SafeProcessRunner : IProcessRunner
{
    private static readonly TimeSpan DrainTimeout = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan KillTimeout = TimeSpan.FromSeconds(5);

    private readonly SafeProcessRunnerOptions _options;
    private readonly HashSet<string> _allowedNames = new(StringComparer.Ordinal);
    private readonly HashSet<string> _allowedPaths = new(StringComparer.Ordinal);

    public SafeProcessRunner(SafeProcessRunnerOptions? options = null)
    {
        _options = options ?? new SafeProcessRunnerOptions();
        _options.Validate();

        foreach (var entry in _options.AllowedExecutables)
        {
            if (Path.IsPathRooted(entry))
            {
                _allowedPaths.Add(Path.GetFullPath(entry));
                continue;
            }

            if (entry.Contains('/') || entry.Contains('\\'))
            {
                throw new ArgumentException(
                    "Relative executable paths are not allowed; use a bare name or an absolute path.",
                    nameof(options));
            }

            _allowedNames.Add(entry);
        }
    }

    public async Task<ProcessResult> RunAsync(
        string executable,
        IReadOnlyList<string> args,
        TimeSpan timeout,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(args);

        if (timeout != Timeout.InfiniteTimeSpan && timeout <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(
                nameof(timeout),
                "The timeout must be positive or infinite.");
        }

        ct.ThrowIfCancellationRequested();

        var resolved = ResolveExecutable(executable);
        var startInfo = new ProcessStartInfo
        {
            FileName = resolved,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
            WorkingDirectory = _options.WorkingDirectory ?? Environment.CurrentDirectory
        };

        ApplyEnvironment(startInfo);

        foreach (var argument in args)
        {
            if (argument is null)
            {
                throw new ArgumentException("Process arguments cannot be null.", nameof(args));
            }

            if (argument.Contains('\0'))
            {
                throw new ArgumentException(
                    "Process arguments cannot contain a NUL character.",
                    nameof(args));
            }

            startInfo.ArgumentList.Add(argument);
        }

        using var process = new Process { StartInfo = startInfo };

        try
        {
            if (!process.Start())
            {
                throw new ValidationException(
                    "PROCESS_START_FAILED",
                    "The allowlisted executable could not be started.");
            }
        }
        catch (Win32Exception)
        {
            throw new ValidationException(
                "PROCESS_START_FAILED",
                "The allowlisted executable could not be started.");
        }

        var stdout = ReadCappedAsync(process.StandardOutput, _options.MaxOutputChars);
        var stderr = ReadCappedAsync(process.StandardError, _options.MaxOutputChars);
        var stopwatch = Stopwatch.StartNew();

        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct);
        if (timeout != Timeout.InfiniteTimeSpan)
        {
            linked.CancelAfter(timeout);
        }

        var timedOut = false;
        try
        {
            await process.WaitForExitAsync(linked.Token);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            timedOut = true;
        }
        catch (OperationCanceledException)
        {
            await StopProcessTreeAsync(process);
            await AwaitOutputsAsync(stdout, stderr);
            throw new OperationCanceledException("Process execution was cancelled.", ct);
        }

        if (timedOut)
        {
            await StopProcessTreeAsync(process);
        }

        var outputs = await AwaitOutputsAsync(stdout, stderr);
        stopwatch.Stop();

        if (timedOut)
        {
            return new ProcessResult(
                ExitCode: -1,
                StandardOutput: outputs.StandardOutput.Text,
                StandardError: outputs.StandardError.Text,
                TimedOut: true,
                OutputTruncated: outputs.Truncated,
                Duration: stopwatch.Elapsed);
        }

        return new ProcessResult(
            ExitCode: process.ExitCode,
            StandardOutput: outputs.StandardOutput.Text,
            StandardError: outputs.StandardError.Text,
            TimedOut: false,
            OutputTruncated: outputs.Truncated,
            Duration: stopwatch.Elapsed);
    }

    private string ResolveExecutable(string executable)
    {
        if (string.IsNullOrWhiteSpace(executable) || executable.Contains('\0'))
        {
            throw new ValidationException(
                "PROCESS_NOT_ALLOWED",
                "The executable is not allowlisted.");
        }

        var looksLikePath = Path.IsPathRooted(executable)
            || executable.Contains('/')
            || executable.Contains('\\');

        if (looksLikePath)
        {
            string fullPath;
            try
            {
                fullPath = Path.GetFullPath(executable);
            }
            catch (Exception exception) when (exception is
                ArgumentException or NotSupportedException or PathTooLongException)
            {
                throw new ValidationException(
                    "PROCESS_NOT_ALLOWED",
                    "The executable path is not allowlisted.");
            }

            if (!_allowedPaths.Contains(fullPath))
            {
                throw new ValidationException(
                    "PROCESS_NOT_ALLOWED",
                    "The executable is not allowlisted.");
            }

            if (!File.Exists(fullPath))
            {
                throw new ValidationException(
                    "PROCESS_NOT_FOUND",
                    "The allowlisted executable was not found.");
            }

            return fullPath;
        }

        if (!_allowedNames.Contains(executable))
        {
            throw new ValidationException(
                "PROCESS_NOT_ALLOWED",
                "The executable is not allowlisted.");
        }

        return ResolveFromPath(executable)
            ?? throw new ValidationException(
                "PROCESS_NOT_FOUND",
                "The allowlisted executable was not found on PATH.");
    }

    private void ApplyEnvironment(ProcessStartInfo startInfo)
    {
        var allowed = new HashSet<string>(
            _options.EnvironmentAllowlist,
            StringComparer.Ordinal);

        startInfo.Environment.Clear();
        foreach (var key in allowed)
        {
            var value = Environment.GetEnvironmentVariable(key);
            if (value is not null)
            {
                startInfo.Environment[key] = value;
            }
        }
    }

    private static string? ResolveFromPath(string executable)
    {
        var path = Environment.GetEnvironmentVariable("PATH");
        if (string.IsNullOrWhiteSpace(path))
        {
            return null;
        }

        foreach (var directory in path.Split(
            Path.PathSeparator,
            StringSplitOptions.RemoveEmptyEntries))
        {
            var candidate = Path.Combine(directory.Trim(), executable);
            if (File.Exists(candidate))
            {
                return Path.GetFullPath(candidate);
            }
        }

        return null;
    }

    private static async Task StopProcessTreeAsync(Process process)
    {
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }
        }
        catch (InvalidOperationException)
        {
            // The process exited between the check and the kill.
        }
        catch (Win32Exception)
        {
            // The process could not be killed; the drain timeout below bounds the wait.
        }

        try
        {
            await process.WaitForExitAsync(CancellationToken.None).WaitAsync(KillTimeout);
        }
        catch (TimeoutException)
        {
            // Do not block the caller forever on an unkillable child.
        }
        catch (InvalidOperationException)
        {
            // Already disposed or exited.
        }
    }

    private static async Task<OutputPair> AwaitOutputsAsync(
        Task<OutputCapture> stdout,
        Task<OutputCapture> stderr)
    {
        try
        {
            await Task.WhenAll(stdout, stderr).WaitAsync(DrainTimeout);
        }
        catch (TimeoutException)
        {
            // A descendant may still hold a pipe; keep the captured prefix.
        }

        var standardOutput = stdout.IsCompletedSuccessfully
            ? stdout.Result
            : OutputCapture.Empty;
        var standardError = stderr.IsCompletedSuccessfully
            ? stderr.Result
            : OutputCapture.Empty;

        return new OutputPair(standardOutput, standardError);
    }

    private static async Task<OutputCapture> ReadCappedAsync(StreamReader reader, int maxChars)
    {
        var builder = new StringBuilder(Math.Min(maxChars, 4096));
        var buffer = new char[4096];
        var truncated = false;

        while (true)
        {
            var read = await reader.ReadAsync(
                buffer.AsMemory(0, buffer.Length),
                CancellationToken.None);

            if (read == 0)
            {
                break;
            }

            var remaining = maxChars - builder.Length;
            if (remaining > 0)
            {
                var take = Math.Min(read, remaining);
                builder.Append(buffer, 0, take);
                if (take < read)
                {
                    truncated = true;
                }
            }
            else
            {
                truncated = true;
            }
        }

        return new OutputCapture(builder.ToString(), truncated);
    }

    private sealed record OutputCapture(string Text, bool Truncated)
    {
        public static OutputCapture Empty { get; } = new(string.Empty, false);
    }

    private sealed record OutputPair(OutputCapture StandardOutput, OutputCapture StandardError)
    {
        public bool Truncated => StandardOutput.Truncated || StandardError.Truncated;
    }
}

public sealed record ProcessResult(
    int ExitCode,
    string StandardOutput,
    string StandardError,
    bool TimedOut,
    bool OutputTruncated,
    TimeSpan Duration);
