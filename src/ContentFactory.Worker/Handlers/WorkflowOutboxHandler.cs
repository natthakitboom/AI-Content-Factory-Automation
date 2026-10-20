using System.Text.Json;
using ContentFactory.Core.Jobs;
using ContentFactory.Infrastructure.Jobs;

namespace ContentFactory.Worker.Handlers;

/// <summary>
/// Converts one finalized-stage event into the next code-defined job. This handler only
/// reads persisted facts; it never calls a provider. The outbox event id provides the
/// dedupe key, so replay after a restart cannot fan out duplicate child jobs.
/// </summary>
public sealed class WorkflowOutboxHandler : IOutboxHandler
{
    public string EventType => WorkflowDefinition.StageSucceededEvent;

    public Task<IReadOnlyList<NewJob>> HandleAsync(
        OutboxMessage message,
        CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();

        using var document = JsonDocument.Parse(message.PayloadJson);
        var root = document.RootElement;

        if (!root.TryGetProperty("nextJobType", out var nextElement)
            || nextElement.ValueKind != JsonValueKind.String)
        {
            return Task.FromResult<IReadOnlyList<NewJob>>([]);
        }

        var nextJobType = nextElement.GetString();
        if (string.IsNullOrWhiteSpace(nextJobType))
        {
            return Task.FromResult<IReadOnlyList<NewJob>>([]);
        }

        var stage = WorkflowDefinition.FindByJobType(nextJobType)
            ?? throw new InvalidOperationException(
                $"The workflow definition has no stage for '{nextJobType}'.");

        if (!stage.Automatic)
        {
            throw new InvalidOperationException(
                $"Stage '{nextJobType}' is an owner action and cannot be enqueued automatically.");
        }

        var payload = root.TryGetProperty("jobPayload", out var payloadElement)
            && payloadElement.ValueKind == JsonValueKind.String
            ? payloadElement.GetString() ?? "{}"
            : "{}";

        IReadOnlyList<NewJob> jobs = [new NewJob(nextJobType, payload, stage.Lane)];
        return Task.FromResult(jobs);
    }
}
