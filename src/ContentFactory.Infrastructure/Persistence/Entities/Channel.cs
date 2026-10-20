namespace ContentFactory.Infrastructure.Persistence.Entities;

public sealed class Channel
{
    public Guid Id { get; set; }
    public string Platform { get; set; } = string.Empty;
    public string DisplayName { get; set; } = string.Empty;
    public string Market { get; set; } = string.Empty;
    public string Locale { get; set; } = string.Empty;
    public string Timezone { get; set; } = string.Empty;
    public string Status { get; set; } = "pending_review";
    public string CapabilitiesJson { get; set; } = "{}";
    public DateTimeOffset CreatedAt { get; set; }
}
