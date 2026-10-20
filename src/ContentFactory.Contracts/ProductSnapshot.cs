namespace ContentFactory.Contracts;

public sealed record ProductSnapshot(
    Guid ProductId, long Version, string Title, string Market,
    string Locale, IReadOnlyList<ApprovedClaim> Claims,
    IReadOnlyList<AssetRef> Assets, DateTimeOffset CapturedAt);
