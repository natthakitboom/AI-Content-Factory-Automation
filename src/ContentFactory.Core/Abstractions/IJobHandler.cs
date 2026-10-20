using ContentFactory.Contracts;

namespace ContentFactory.Core.Abstractions;

public interface IJobHandler
{
    string JobType { get; }
    Task<JobOutcome> ExecuteAsync(LeasedJob job, CancellationToken ct);
}
