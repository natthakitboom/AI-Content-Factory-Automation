using ContentFactory.Contracts;
using ContentFactory.Core.Abstractions;
using ContentFactory.Infrastructure.Jobs;

namespace ContentFactory.Worker.Handlers;

/// <summary>Runs one scheduling pass. Disabled schedules are never touched.</summary>
public sealed class ScheduleTickHandler : IJobHandler
{
    public const string JobTypeName = "schedule_tick";

    private readonly ScheduleTicker _ticker;
    private readonly TimeProvider _clock;

    public ScheduleTickHandler(ScheduleTicker ticker, TimeProvider clock)
    {
        _ticker = ticker ?? throw new ArgumentNullException(nameof(ticker));
        _clock = clock ?? throw new ArgumentNullException(nameof(clock));
    }

    public string JobType => JobTypeName;

    public async Task<JobOutcome> ExecuteAsync(LeasedJob job, CancellationToken ct)
    {
        var result = await _ticker.TickAsync(_clock.GetUtcNow(), ct);
        return new JobOutcome(
            "succeeded", null, null,
            $"enqueued={result.Enqueued};missed={result.Missed};skipped={result.Skipped};failed={result.Failed}");
    }
}
