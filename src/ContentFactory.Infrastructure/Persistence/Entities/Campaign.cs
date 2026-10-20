using ContentFactory.Contracts;

namespace ContentFactory.Infrastructure.Persistence.Entities;

public sealed class Campaign
{
    public Guid Id { get; set; }
    public Guid ProductId { get; set; }
    public Guid ChannelId { get; set; }
    public VideoMode Mode { get; set; }
    public string Locale { get; set; } = string.Empty;
    public int TargetDurationMs { get; set; }
    public decimal BudgetUsd { get; set; }
    public string Status { get; set; } = "draft";
    public long Version { get; set; } = 1;
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
}
