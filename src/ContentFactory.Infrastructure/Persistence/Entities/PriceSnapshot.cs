namespace ContentFactory.Infrastructure.Persistence.Entities;

public sealed class PriceSnapshot
{
    public Guid Id { get; set; }
    public string Provider { get; set; } = string.Empty;
    public string Model { get; set; } = string.Empty;
    public string ConfigHash { get; set; } = string.Empty;
    public string Unit { get; set; } = string.Empty;
    public decimal UnitPriceUsd { get; set; }
    public string SourceUrl { get; set; } = string.Empty;
    public DateTimeOffset VerifiedAt { get; set; }
    public DateTimeOffset ValidUntil { get; set; }
}
