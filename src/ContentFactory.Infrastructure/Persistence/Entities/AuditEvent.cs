namespace ContentFactory.Infrastructure.Persistence.Entities;

public sealed class AuditEvent
{
    public Guid Id { get; set; }
    public string Actor { get; set; } = string.Empty;
    public string Action { get; set; } = string.Empty;
    public string TargetType { get; set; } = string.Empty;
    public Guid? TargetId { get; set; }
    public string DetailsJson { get; set; } = "{}";
    public DateTimeOffset CreatedAt { get; set; }
}
