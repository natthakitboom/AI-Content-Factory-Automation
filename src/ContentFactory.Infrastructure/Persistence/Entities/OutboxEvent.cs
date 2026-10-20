namespace ContentFactory.Infrastructure.Persistence.Entities;

public sealed class OutboxEvent
{
    public Guid Id { get; set; }
    public string EventType { get; set; } = string.Empty;
    public Guid AggregateId { get; set; }
    public string PayloadJson { get; set; } = "{}";
    public DateTimeOffset? PublishedAt { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
}
