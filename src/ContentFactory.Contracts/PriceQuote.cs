namespace ContentFactory.Contracts;

public sealed record PriceQuote(
    Guid PriceSnapshotId, string Provider, string Model,
    decimal EstimatedUsd, decimal ReservationUsd,
    DateTimeOffset ValidUntil);
