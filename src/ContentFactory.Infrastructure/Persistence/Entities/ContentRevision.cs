namespace ContentFactory.Infrastructure.Persistence.Entities;

public sealed class ContentRevision
{
    public Guid Id { get; set; }
    public Guid CampaignId { get; set; }
    public int Revision { get; set; }
    public string ProductSnapshotJson { get; set; } = "{}";
    public string? ScriptJson { get; set; }
    public string? ScriptHash { get; set; }
    public string PromptVersion { get; set; } = "script-planner.v1";
    public string? ModelId { get; set; }
    public string Status { get; set; } = "draft";
    public long Version { get; set; } = 1;
    public DateTimeOffset CreatedAt { get; set; }
}
