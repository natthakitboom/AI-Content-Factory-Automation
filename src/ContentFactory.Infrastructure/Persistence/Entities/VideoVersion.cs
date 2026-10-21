namespace ContentFactory.Infrastructure.Persistence.Entities;

public sealed class VideoVersion
{
    public Guid Id { get; set; }
    public Guid ContentRevisionId { get; set; }
    public int VersionNo { get; set; }
    public string? ManifestJson { get; set; }
    public string? ManifestSha256 { get; set; }
    public Guid? OutputAssetId { get; set; }
    public Guid? ThumbnailAssetId { get; set; }
    public Guid? SubtitlesAssetId { get; set; }
    public string? QaJson { get; set; }
    public string Status { get; set; } = "draft";
    public DateTimeOffset CreatedAt { get; set; }
}
