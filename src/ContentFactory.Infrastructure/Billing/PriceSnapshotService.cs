using ContentFactory.Contracts;
using ContentFactory.Core.Exceptions;
using ContentFactory.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace ContentFactory.Infrastructure.Billing;

/// <summary>
/// Quotes a paid operation from an explicit, verified price snapshot. The quote includes
/// the pilot reservation margin; callers must not invent a price or reuse an expired one.
/// </summary>
public sealed class PriceSnapshotService
{
    private readonly AppDbContext _db;
    private readonly BudgetOptions _options;

    public PriceSnapshotService(AppDbContext db, BudgetOptions options)
    {
        _db = db;
        _options = options;
        _options.Validate();
    }

    public async Task<PriceQuote> GetQuoteAsync(
        string provider,
        string model,
        string configHash,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(provider) ||
            string.IsNullOrWhiteSpace(model) ||
            string.IsNullOrWhiteSpace(configHash))
        {
            throw new ValidationException(
                "PRICE_REQUEST_INVALID",
                "Provider, model and config hash are required to quote a price.");
        }

        if (provider.Length > 32 || model.Length > 256 || configHash.Length > 64)
        {
            throw new ValidationException(
                "PRICE_REQUEST_INVALID",
                "The price lookup fields exceed their configured limits.");
        }

        var now = await BillingDatabaseTime.GetNowAsync(_db, ct);
        var snapshot = await _db.PriceSnapshots
            .AsNoTracking()
            .Where(x => x.Provider == provider &&
                x.Model == model &&
                x.ConfigHash == configHash &&
                x.VerifiedAt <= now &&
                x.ValidUntil > now)
            .OrderByDescending(x => x.VerifiedAt)
            .FirstOrDefaultAsync(ct);

        if (snapshot is null)
        {
            throw new PriceStaleException(
                "No verified, unexpired price snapshot matches this provider, model and configuration.");
        }

        var estimatedUsd = snapshot.UnitPriceUsd;
        var reservationUsd = decimal.Round(
            estimatedUsd * _options.ReservationMultiplier,
            6,
            MidpointRounding.AwayFromZero);

        return new PriceQuote(
            snapshot.Id,
            snapshot.Provider,
            snapshot.Model,
            estimatedUsd,
            reservationUsd,
            snapshot.ValidUntil);
    }
}
