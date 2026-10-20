namespace ContentFactory.Contracts;

public sealed record AssetRef(
    Guid Id, string Sha256, string RelativePath,
    AssetKind Kind, string MimeType, long Bytes, long? DurationMs);
