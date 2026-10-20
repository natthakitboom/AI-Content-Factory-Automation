namespace ContentFactory.Contracts;

public sealed record VoiceSegment(
    string SceneId, AssetRef Audio, long DurationMs);
