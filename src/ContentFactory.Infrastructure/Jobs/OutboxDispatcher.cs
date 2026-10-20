using System.Data.Common;
using System.Text.Json;
using ContentFactory.Contracts;
using ContentFactory.Infrastructure.Persistence;
using ContentFactory.Infrastructure.Persistence.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Npgsql;

namespace ContentFactory.Infrastructure.Jobs;

/// <summary>A downstream job proposed by an outbox handler.</summary>
public sealed record NewJob(
    string JobType,
    string PayloadJson,
    JobLane Lane,
    int Priority = 100,
    DateTimeOffset? RunAfter = null,
    int PayloadVersion = 1,
    int MaxAttempts = 5);

/// <summary>An unpublished outbox event handed to a local handler.</summary>
public sealed record OutboxMessage(
    Guid Id,
    string EventType,
    Guid AggregateId,
    string PayloadJson,
    DateTimeOffset CreatedAt);

/// <summary>
/// Pure local transition from an outbox event to zero or more downstream jobs. Handlers
/// must not call a provider; they only describe work from persisted facts.
/// </summary>
public interface IOutboxHandler
{
    string EventType { get; }
    Task<IReadOnlyList<NewJob>> HandleAsync(OutboxMessage message, CancellationToken ct);
}

/// <summary>
/// Claims one unpublished event with <c>FOR UPDATE SKIP LOCKED</c>, asks the matching
/// handler for downstream jobs, inserts them and marks the event published in one
/// transaction. Dedupe keys are derived from the event id, so replay cannot create a
/// second downstream job.
/// </summary>
public sealed class OutboxDispatcher
{
    private readonly AppDbContext _db;
    private readonly PostgresJobStore _jobs;
    private readonly TimeProvider _clock;

    public OutboxDispatcher(AppDbContext db, PostgresJobStore jobs, TimeProvider clock)
    {
        _db = db;
        _jobs = jobs;
        _clock = clock;
    }

    public async Task<int> DispatchOnceAsync(IOutboxHandler handler, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(handler);

        if (string.IsNullOrWhiteSpace(handler.EventType) || handler.EventType.Length > 64)
        {
            throw new ArgumentException(
                "An outbox handler needs an event type of 64 characters or fewer.",
                nameof(handler));
        }

        await using var transaction = await _db.Database.BeginTransactionAsync(ct);
        var eventId = await SelectEventIdAsync(handler.EventType, ct);
        if (eventId is null)
        {
            await transaction.RollbackAsync(ct);
            return 0;
        }

        var outbox = await _db.OutboxEvents.SingleAsync(x => x.Id == eventId.Value, ct);
        var message = new OutboxMessage(
            outbox.Id,
            outbox.EventType,
            outbox.AggregateId,
            outbox.PayloadJson,
            outbox.CreatedAt);

        var jobs = await handler.HandleAsync(message, ct) ?? [];
        var created = 0;

        for (var index = 0; index < jobs.Count; index++)
        {
            var dedupeKey = $"outbox:{outbox.Id:N}:{index:D4}";
            var exists = await _db.Jobs
                .AsNoTracking()
                .AnyAsync(x => x.DedupeKey == dedupeKey, ct);

            await _jobs.EnqueueAsync(jobs[index], dedupeKey, ct);

            if (!exists)
            {
                created++;
            }
        }

        outbox.PublishedAt = _clock.GetUtcNow();
        await _db.SaveChangesAsync(ct);
        await transaction.CommitAsync(ct);
        return created;
    }

    private async Task<Guid?> SelectEventIdAsync(string eventType, CancellationToken ct)
    {
        var connection = _db.Database.GetDbConnection();
        await using var command = connection.CreateCommand();
        command.Transaction = _db.Database.CurrentTransaction!.GetDbTransaction();
        command.CommandText =
            """
            SELECT id
            FROM app.outbox_events
            WHERE event_type = @eventType
              AND published_at IS NULL
            ORDER BY created_at ASC, id ASC
            FOR UPDATE SKIP LOCKED
            LIMIT 1
            """;
        command.Parameters.Add(new NpgsqlParameter("eventType", eventType));

        var value = await command.ExecuteScalarAsync(ct);
        return value is Guid id ? id : null;
    }
}
