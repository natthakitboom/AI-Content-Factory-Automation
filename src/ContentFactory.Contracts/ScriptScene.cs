namespace ContentFactory.Contracts;

public sealed record ScriptScene(
    string SceneId, string Role, string Narration,
    string Caption, IReadOnlyList<Guid> ClaimIds,
    Guid? PreferredAssetId, string? MotionPrompt);
