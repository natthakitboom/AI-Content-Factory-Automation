namespace ContentFactory.Contracts;

public sealed record JobOutcome(
    string Status, DateTimeOffset? NextRunAt,
    string? ErrorCode, string? OutputReference);
