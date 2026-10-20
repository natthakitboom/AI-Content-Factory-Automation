namespace ContentFactory.Infrastructure.Persistence.Entities;

public sealed class JobEvent
{
    public Guid Id { get; set; }
    public Guid JobId { get; set; }
    public string EventType { get; set; } = string.Empty;
    public string DetailsJson { get; set; } = "{}";
    public DateTimeOffset OccurredAt { get; set; }
}
