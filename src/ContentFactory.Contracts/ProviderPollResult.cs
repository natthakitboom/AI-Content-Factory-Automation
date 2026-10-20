namespace ContentFactory.Contracts;

public sealed record ProviderPollResult(
    ProviderRunStatus Status, Uri? OutputUrl,
    string? ErrorCode, int? Seed);
