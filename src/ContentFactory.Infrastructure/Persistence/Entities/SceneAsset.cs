namespace ContentFactory.Infrastructure.Persistence.Entities;

public sealed class SceneAsset
{
    public Guid Id { get; set; }
    public Guid ContentRevisionId { get; set; }
    public string SceneId { get; set; } = string.Empty;
    public string Role { get; set; } = string.Empty;
    public Guid AssetId { get; set; }
}
