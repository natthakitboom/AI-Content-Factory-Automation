using ContentFactory.Contracts;

namespace ContentFactory.Core.Abstractions;

public interface IBudgetGuard
{
    Task<BudgetReservation> ReserveAsync(
        string operationId, Guid campaignId, PriceQuote quote, CancellationToken ct);

    /// <summary>
    /// Reserves with an explicit video/revision scope so the per-video cap aggregates all
    /// paid scene requests for one video. The three-argument overload remains valid and
    /// uses the campaign id as a conservative fallback scope.
    /// </summary>
    Task<BudgetReservation> ReserveAsync(
        string operationId, Guid campaignId, Guid videoScopeId, PriceQuote quote,
        CancellationToken ct)
        => ReserveAsync(operationId, campaignId, quote, ct);

    Task SettleAsync(Guid reservationId, decimal amountUsd,
        string certainty, CancellationToken ct);
    Task ReleaseAsync(Guid reservationId, string confirmedReason, CancellationToken ct);
}
