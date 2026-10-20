namespace ContentFactory.Infrastructure.Persistence.Entities;

public sealed class CostEntry
{
    public Guid Id { get; set; }
    public string OperationId { get; set; } = string.Empty;
    public string IdempotencyKey { get; set; } = string.Empty;
    public string EntryType { get; set; } = string.Empty;
    public decimal AmountUsd { get; set; }
    public string Certainty { get; set; } = "estimated";
    public Guid? ReservationId { get; set; }
    public Guid? ProviderRequestId { get; set; }
    public Guid? PriceSnapshotId { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
}
