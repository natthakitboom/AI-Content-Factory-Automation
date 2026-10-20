namespace ContentFactory.Infrastructure.Persistence.Entities;

public sealed class MetricSnapshot
{
    public Guid Id { get; set; }
    public Guid PublicationId { get; set; }
    public string Source { get; set; } = string.Empty;
    public DateTimeOffset AsOf { get; set; }
    public long? Views { get; set; }
    public long? ProductClicks { get; set; }
    public long? Orders { get; set; }
    public decimal? CommissionPending { get; set; }
    public decimal? CommissionSettled { get; set; }
    public string Currency { get; set; } = string.Empty;
    public string ImportKey { get; set; } = string.Empty;
}
