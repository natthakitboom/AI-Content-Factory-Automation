namespace ContentFactory.Infrastructure.Persistence.Entities;

public sealed class ProductClaim
{
    public Guid Id { get; set; }
    public Guid ProductId { get; set; }
    public string Text { get; set; } = string.Empty;
    public string SourceReference { get; set; } = string.Empty;
    public DateTimeOffset? VerifiedAt { get; set; }
    public Guid? ApprovedBy { get; set; }
    public string Status { get; set; } = "pending";
    public DateTimeOffset? ExpiresAt { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
}
