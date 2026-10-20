using ContentFactory.Contracts;
using ContentFactory.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace ContentFactory.Worker.Health;

public sealed record ResourceGovernorOptions
{
    public int HeavyLocalConcurrency { get; init; } = 1;
    public int CloudSubmitConcurrency { get; init; } = 2;
    public int CloudPollConcurrency { get; init; } = 2;
    public int DownloadConcurrency { get; init; } = 2;
    public int ControlConcurrency { get; init; } = 1;
    public bool QuietMode { get; init; } = true;
    public int MaxCloudInFlight { get; init; } = 2;
}

/// <summary>Nullable readings; null means the platform did not provide a sensor.</summary>
public sealed record ResourceReadings(
    int? MemoryAvailableMb,
    double? ThermalCelsius,
    string? UnavailableReason);

/// <summary>
/// In-process lane limits plus a database-backed cloud in-flight count. Heavy-local work
/// (local inference and FFmpeg render) and cloud waiting use different semaphores, so a
/// poll waiting for a provider never holds the render slot. Quiet mode forces one
/// heavy-local slot and therefore serializes local inference with render.
/// </summary>
public sealed class ResourceGovernor
{
    private readonly Dictionary<JobLane, SemaphoreSlim> _lanes;

    public ResourceGovernor(ResourceGovernorOptions? options = null)
    {
        var requested = options ?? new ResourceGovernorOptions();
        Validate(requested);

        Options = requested with
        {
            HeavyLocalConcurrency = requested.QuietMode
                ? 1
                : requested.HeavyLocalConcurrency
        };

        _lanes = new Dictionary<JobLane, SemaphoreSlim>
        {
            [JobLane.Control] = new(Options.ControlConcurrency, Options.ControlConcurrency),
            [JobLane.HeavyLocal] = new(Options.HeavyLocalConcurrency, Options.HeavyLocalConcurrency),
            [JobLane.CloudSubmit] = new(Options.CloudSubmitConcurrency, Options.CloudSubmitConcurrency),
            [JobLane.CloudPoll] = new(Options.CloudPollConcurrency, Options.CloudPollConcurrency),
            [JobLane.Download] = new(Options.DownloadConcurrency, Options.DownloadConcurrency)
        };
    }

    public ResourceGovernorOptions Options { get; }

    public async Task<IAsyncDisposable> AcquireAsync(JobLane lane, CancellationToken ct)
    {
        if (!_lanes.TryGetValue(lane, out var semaphore))
        {
            throw new ArgumentOutOfRangeException(nameof(lane), lane, "Unknown job lane.");
        }

        await semaphore.WaitAsync(ct);
        return new LaneLease(semaphore);
    }

    /// <summary>
    /// Cross-process cloud in-flight count. The semaphore bounds this process; this count
    /// prevents two local worker processes from multiplying the configured cloud limit.
    /// </summary>
    public Task<int> CountCloudInFlightAsync(AppDbContext db, CancellationToken ct)
        => db.Jobs.CountAsync(
            job => job.Status == "running"
                && (job.Lane == JobLane.CloudSubmit || job.Lane == JobLane.CloudPoll),
            ct);

    /// <summary>
    /// The MVP has no portable memory/thermal sensor. Reporting unavailable is required;
    /// inventing a number would make a safety decision from fabricated data.
    /// </summary>
    public static ResourceReadings ReadReadings()
        => new(null, null, "sensor_not_supported");

    private static void Validate(ResourceGovernorOptions options)
    {
        if (options.HeavyLocalConcurrency <= 0
            || options.CloudSubmitConcurrency <= 0
            || options.CloudPollConcurrency <= 0
            || options.DownloadConcurrency <= 0
            || options.ControlConcurrency <= 0
            || options.MaxCloudInFlight <= 0)
        {
            throw new ArgumentException(
                "Resource lane limits and cloud in-flight limits must be positive.",
                nameof(options));
        }
    }

    private sealed class LaneLease(SemaphoreSlim semaphore) : IAsyncDisposable
    {
        private int _released;

        public ValueTask DisposeAsync()
        {
            if (Interlocked.Exchange(ref _released, 1) == 0)
            {
                semaphore.Release();
            }

            return ValueTask.CompletedTask;
        }
    }
}
