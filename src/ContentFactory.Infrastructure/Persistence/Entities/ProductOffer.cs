namespace ContentFactory.Infrastructure.Persistence.Entities;

public sealed class ProductOffer
{
    public Guid Id { get; set; }
    public Guid ProductId { get; set; }
    public string Market { get; set; } = string.Empty;
    public string ProductUrl { get; set; } = string.Empty;
    public decimal Price { get; set; }
    public string Currency { get; set; } = string.Empty;
    public decimal CommissionRate { get; set; }
    public string Availability { get; set; } = string.Empty;
    public DateTimeOffset CheckedAt { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
}
