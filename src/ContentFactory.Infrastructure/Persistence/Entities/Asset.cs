using ContentFactory.Contracts;

namespace ContentFactory.Infrastructure.Persistence.Entities;

public sealed class Asset
{
    public Guid Id { get; set; }
    public string Sha256 { get; set; } = string.Empty;
    public string RelativePath { get; set; } = string.Empty;
    public AssetKind Kind { get; set; }
    public string Mime { get; set; } = string.Empty;
    public long Bytes { get; set; }
    public int? Width { get; set; }
    public int? Height { get; set; }
    public long? DurationMs { get; set; }
    public string RightsStatus { get; set; } = "unknown";
    public string SourceReference { get; set; } = string.Empty;
    public bool CloudUseAllowed { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
}
