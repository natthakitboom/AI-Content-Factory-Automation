namespace ContentFactory.Infrastructure.Media;

/// <summary>
/// Narrow process boundary used by media probes and renderers. Production uses
/// <see cref="SafeProcessRunner"/>; tests can substitute a deterministic runner without
/// starting a child process or reaching the network.
/// </summary>
public interface IProcessRunner
{
    Task<ProcessResult> RunAsync(
        string executable,
        IReadOnlyList<string> args,
        TimeSpan timeout,
        CancellationToken ct);
}
