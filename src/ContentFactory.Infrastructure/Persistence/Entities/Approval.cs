namespace ContentFactory.Infrastructure.Persistence.Entities;

public sealed class Approval
{
    public Guid Id { get; set; }
    public Guid VideoVersionId { get; set; }
    public string ManifestSha256 { get; set; } = string.Empty;
    public Guid ReviewerId { get; set; }
    public string Decision { get; set; } = string.Empty;
    public string ChecklistJson { get; set; } = "{}";
    public DateTimeOffset DecidedAt { get; set; }
}
