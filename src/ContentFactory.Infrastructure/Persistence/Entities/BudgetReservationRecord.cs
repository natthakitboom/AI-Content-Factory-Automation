namespace ContentFactory.Infrastructure.Persistence.Entities;

/// <summary>
/// One paid-operation reservation. Allocations keep the exact budget-period ids and
/// amounts that were debited, so settle/release can lock the same rows in the same order
/// without re-deriving campaign or period context.
/// </summary>
public sealed class BudgetReservationRecord
{
    public Guid Id { get; set; }
    public string OperationId { get; set; } = string.Empty;
    public Guid CampaignId { get; set; }
    public Guid VideoScopeId { get; set; }
    public string Provider { get; set; } = string.Empty;
    public string Model { get; set; } = string.Empty;
    public Guid PriceSnapshotId { get; set; }
    public string ApprovalId { get; set; } = string.Empty;
    public decimal ReservedUsd { get; set; }
    public decimal? SettledUsd { get; set; }
    public string Status { get; set; } = "reserved";
    public string AllocationsJson { get; set; } = "[]";
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset? SettledAt { get; set; }
    public DateTimeOffset? ReleasedAt { get; set; }
    public string? ReleaseReason { get; set; }
}
