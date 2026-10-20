using ContentFactory.Contracts;

namespace ContentFactory.Core.Abstractions;

public interface IJobStore
{
    Task<LeasedJob?> TryLeaseAsync(string worker, JobLane lane, CancellationToken ct);
    Task<bool> RenewAsync(LeasedJob job, CancellationToken ct);
    Task<bool> FinishAsync(LeasedJob job, JobOutcome outcome, CancellationToken ct);
}
