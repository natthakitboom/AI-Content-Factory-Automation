namespace ContentFactory.Infrastructure.Review;

public sealed record ReviewChecklistItem(string Key, string Label);

public sealed record ReviewSceneView(
    string SceneId, string Role, string Narration, string Caption, IReadOnlyList<Guid> ClaimIds);

public sealed record ReviewClaimView(Guid Id, string Text, string SourceReference);

public sealed record ReviewAssetView(Guid Id, string Mime, string Url);

public sealed record ReviewCostView(decimal ReservedUsd, decimal SettledUsd, string Currency);

public sealed record ReviewDecisionView(
    string Decision, Guid ReviewerId, DateTimeOffset DecidedAt, string ManifestSha256);

/// <summary>
/// Everything the owner needs to judge one exact video version. The manifest hash is the
/// identity that an approval will be bound to.
/// </summary>
public sealed record VideoReviewDetail(
    Guid Id,
    Guid RevisionId,
    Guid CampaignId,
    int VersionNo,
    string Status,
    string? ManifestSha256,
    string? MediaUrl,
    string Title,
    string Disclosure,
    IReadOnlyList<ReviewSceneView> Scenes,
    IReadOnlyList<ReviewClaimView> Claims,
    IReadOnlyList<ReviewAssetView> ProductAssets,
    bool UsesAiMotion,
    string? AiMotionWarning,
    ReviewCostView Cost,
    System.Text.Json.JsonElement? Qa,
    IReadOnlyList<ReviewChecklistItem> Checklist,
    ReviewDecisionView? Decision);

public sealed record VideoQueueItem(
    Guid Id, int VersionNo, string Status, string Title, DateTimeOffset CreatedAt);

public sealed record CaptionEditResult(Guid RevisionId, Guid JobId);
