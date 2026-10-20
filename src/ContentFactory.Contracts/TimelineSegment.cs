namespace ContentFactory.Contracts;

public sealed record TimelineSegment(
    string SceneId, long StartMs, long DurationMs,
    AssetRef Visual, AssetRef? Audio, string Caption,
    string Transform);
