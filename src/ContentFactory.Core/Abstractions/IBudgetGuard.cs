using ContentFactory.Contracts;

namespace ContentFactory.Core.Abstractions;

public interface IBudgetGuard
{
    Task<BudgetReservation> ReserveAsync(
        string operationId, Guid campaignId, PriceQuote quote, CancellationToken ct);
    Task SettleAsync(Guid reservationId, decimal amountUsd,
        string certainty, CancellationToken ct);
    Task ReleaseAsync(Guid reservationId, string confirmedReason, CancellationToken ct);
}
