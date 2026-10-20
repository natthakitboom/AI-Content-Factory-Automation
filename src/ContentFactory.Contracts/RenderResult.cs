namespace ContentFactory.Contracts;

public sealed record RenderResult(
    AssetRef Video, AssetRef Thumbnail, AssetRef Subtitles,
    string ManifestSha256, long DurationMs);
