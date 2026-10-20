namespace ContentFactory.Infrastructure.Billing;

public sealed record LiveApprovalEnvelope(
    string ApprovalId,
    string Provider,
    string Model,
    int MaxRequestCount,
    decimal MaxTotalUsd,
    DateTimeOffset ExpiresAt,
    string ApprovedBy);
