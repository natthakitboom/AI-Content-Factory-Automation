namespace ContentFactory.Infrastructure.Persistence.Entities;

public sealed class Publication
{
    public Guid Id { get; set; }
    public Guid VideoVersionId { get; set; }
    public Guid ChannelId { get; set; }
    public string PostUrl { get; set; } = string.Empty;
    public string? ExternalPostId { get; set; }
    public DateTimeOffset PublishedAt { get; set; }
    public string ProductLinkStatus { get; set; } = "unknown";
    public string? EvidenceReference { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
}
