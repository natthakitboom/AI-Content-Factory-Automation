using ContentFactory.Contracts;

namespace ContentFactory.Core.Abstractions;

/// <summary>An event that must be inserted in the same transaction as a job finalize.</summary>
public sealed record OutboxEventDraft(
    string EventType,
    Guid AggregateId,
    string PayloadJson);

/// <summary>
/// Optional store capability used by the worker. Finalize and child-event insertion share
/// one transaction, so a worker that sleeps after finalize cannot lose the transition and
/// a retry cannot create it twice.
/// </summary>
public interface IJobOutboxWriter
{
    Task<bool> FinishWithOutboxAsync(
        LeasedJob job,
        JobOutcome outcome,
        OutboxEventDraft outbox,
        CancellationToken ct);
}
