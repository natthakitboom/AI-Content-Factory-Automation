namespace ContentFactory.Infrastructure.Security;

/// <summary>
/// Small in-process login throttle for the single-owner local API. It is not a
/// distributed rate limiter; the API is loopback-only and single-instance in the MVP.
/// </summary>
public sealed class LoginThrottle
{
    private static readonly TimeSpan Window = TimeSpan.FromMinutes(5);
    private const int MaxFailures = 5;

    private readonly Lock _gate = new();
    private readonly Dictionary<string, Entry> _entries = new(StringComparer.Ordinal);

    public bool IsBlocked(string key, DateTimeOffset now)
    {
        lock (_gate)
        {
            if (!_entries.TryGetValue(key, out var entry))
            {
                return false;
            }

            if (now - entry.WindowStart >= Window)
            {
                _entries.Remove(key);
                return false;
            }

            return entry.Failures >= MaxFailures;
        }
    }

    public void RecordFailure(string key, DateTimeOffset now)
    {
        lock (_gate)
        {
            if (!_entries.TryGetValue(key, out var entry) || now - entry.WindowStart >= Window)
            {
                _entries[key] = new Entry(now, 1);
                return;
            }

            _entries[key] = entry with { Failures = entry.Failures + 1 };
        }
    }

    public void Reset(string key)
    {
        lock (_gate)
        {
            _entries.Remove(key);
        }
    }

    private sealed record Entry(DateTimeOffset WindowStart, int Failures);
}
