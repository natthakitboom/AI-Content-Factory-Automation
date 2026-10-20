namespace ContentFactory.Infrastructure.Persistence.Entities;

public sealed class ScheduleOccurrence
{
    public Guid Id { get; set; }
    public Guid ScheduleId { get; set; }
    public string SlotKey { get; set; } = string.Empty;
    public Guid JobId { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
}
