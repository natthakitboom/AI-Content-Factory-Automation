namespace ContentFactory.Contracts;

public sealed record ScriptDocument(
    int SchemaVersion, Guid RevisionId, string Locale,
    string Title, string Disclosure, string CaptionText,
    IReadOnlyList<ScriptScene> Scenes);
