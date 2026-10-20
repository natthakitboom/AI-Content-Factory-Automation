namespace ContentFactory.Contracts;

public sealed record ScriptRequest(
    Guid RevisionId, ProductSnapshot Product, string Locale,
    VideoMode Mode, int TargetDurationMs, string PromptVersion);
