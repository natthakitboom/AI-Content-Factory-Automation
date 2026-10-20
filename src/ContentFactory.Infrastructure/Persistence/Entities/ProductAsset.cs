namespace ContentFactory.Infrastructure.Persistence.Entities;

public sealed class ProductAsset
{
    public Guid ProductId { get; set; }
    public Guid AssetId { get; set; }
    public int DisplayOrder { get; set; }
}
