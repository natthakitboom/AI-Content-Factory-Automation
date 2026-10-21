namespace ContentFactory.Infrastructure.Security;

/// <summary>
/// Limits for provider output downloads. Hosts are an explicit allowlist: an entry matches
/// the exact host or any subdomain of it, never a mere string suffix.
/// </summary>
public sealed class SafeMediaDownloaderOptions
{
    public IReadOnlyList<string> AllowedHosts { get; init; } = ["fal.media", "fal.run"];

    public int MaxRedirects { get; init; } = 3;

    public long MaxBytes { get; init; } = 200L * 1024 * 1024;

    public TimeSpan Timeout { get; init; } = TimeSpan.FromSeconds(180);
}
