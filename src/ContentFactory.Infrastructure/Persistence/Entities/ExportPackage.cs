namespace ContentFactory.Infrastructure.Persistence.Entities;

public sealed class ExportPackage
{
    public Guid Id { get; set; }
    public Guid VideoVersionId { get; set; }
    public Guid ChannelId { get; set; }
    public string PackageHash { get; set; } = string.Empty;
    public string RelativePath { get; set; } = string.Empty;
    public DateTimeOffset CreatedAt { get; set; }
}
