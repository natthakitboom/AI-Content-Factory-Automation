namespace ContentFactory.Infrastructure.Persistence.Entities;

public sealed class Product
{
    public Guid Id { get; set; }
    public string? ExternalId { get; set; }
    public string Title { get; set; } = string.Empty;
    public string Market { get; set; } = string.Empty;
    public string Locale { get; set; } = string.Empty;
    public string? Category { get; set; }
    public long Version { get; set; } = 1;
    public DateTimeOffset? ArchivedAt { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
}
