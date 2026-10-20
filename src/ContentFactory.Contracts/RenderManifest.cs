namespace ContentFactory.Contracts;

public sealed record RenderManifest(
    int SchemaVersion, Guid VideoVersionId,
    string ScriptSha256, string TemplateVersion,
    string RendererVersion, string FontFingerprint,
    int Width, int Height, int Fps,
    IReadOnlyList<TimelineSegment> Segments);
