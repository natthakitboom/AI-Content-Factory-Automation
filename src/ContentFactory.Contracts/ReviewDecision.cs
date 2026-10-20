namespace ContentFactory.Contracts;

public sealed record ReviewDecision(
    Guid VideoVersionId, string ManifestSha256,
    bool Approved, IReadOnlyDictionary<string, bool> Checklist,
    string? Comment);
