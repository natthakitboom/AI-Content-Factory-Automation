using ContentFactory.Contracts;

namespace ContentFactory.Core.Abstractions;

public interface IReviewService
{
    Task RecordAsync(ReviewDecision decision, Guid reviewerId, CancellationToken ct);
}
