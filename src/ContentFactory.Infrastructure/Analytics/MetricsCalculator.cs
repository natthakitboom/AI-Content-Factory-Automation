namespace ContentFactory.Infrastructure.Analytics;

public sealed record PublicationMetrics(
    Guid PublicationId,
    DateTimeOffset AsOf,
    string Source,
    long? Views,
    long? ProductClicks,
    long? Orders,
    decimal? CommissionPending,
    decimal? CommissionSettled,
    string Currency);

public sealed record CurrencyCommission(string Currency, decimal? Pending, decimal? Settled);

public sealed record MetricsOverview(
    long? Views,
    long? ProductClicks,
    long? Orders,
    decimal? Ctr,
    decimal? Cvr,
    IReadOnlyList<CurrencyCommission> Commission,
    int Publications,
    DateTimeOffset? EarliestAsOf,
    DateTimeOffset? LatestAsOf,
    IReadOnlyList<string> Sources);

/// <summary>
/// Honest aggregation. Snapshots are cumulative, so only the latest per publication counts.
/// A number nobody reported stays null, never zero. Ratios are null for a missing or zero
/// denominator and only use publications that reported both numbers. Currencies are never
/// added together.
/// </summary>
public static class MetricsCalculator
{
    public static MetricsOverview Overview(IEnumerable<PublicationMetrics> snapshots)
    {
        ArgumentNullException.ThrowIfNull(snapshots);

        var latest = snapshots
            .GroupBy(x => x.PublicationId)
            .Select(group => group.OrderByDescending(x => x.AsOf).First())
            .ToList();
        if (latest.Count == 0)
        {
            return new MetricsOverview(null, null, null, null, null, [], 0, null, null, []);
        }

        var withViewsAndClicks = latest.Where(x => x.Views is not null && x.ProductClicks is not null).ToList();
        var withClicksAndOrders = latest.Where(x => x.ProductClicks is not null && x.Orders is not null).ToList();

        var commission = latest
            .Where(x => !string.IsNullOrEmpty(x.Currency))
            .GroupBy(x => x.Currency, StringComparer.Ordinal)
            .OrderBy(group => group.Key, StringComparer.Ordinal)
            .Select(group => new CurrencyCommission(
                group.Key,
                SumOrNull(group.Select(x => x.CommissionPending)),
                SumOrNull(group.Select(x => x.CommissionSettled))))
            .Where(x => x.Pending is not null || x.Settled is not null)
            .ToList();

        return new MetricsOverview(
            SumOrNull(latest.Select(x => x.Views)),
            SumOrNull(latest.Select(x => x.ProductClicks)),
            SumOrNull(latest.Select(x => x.Orders)),
            Ratio(withViewsAndClicks.Sum(x => x.ProductClicks!.Value), withViewsAndClicks.Sum(x => x.Views!.Value)),
            Ratio(withClicksAndOrders.Sum(x => x.Orders!.Value), withClicksAndOrders.Sum(x => x.ProductClicks!.Value)),
            commission,
            latest.Count,
            latest.Min(x => x.AsOf),
            latest.Max(x => x.AsOf),
            latest.Select(x => x.Source).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToList());
    }

    public static decimal? Ratio(long? numerator, long? denominator)
        => numerator is null || denominator is null or <= 0
            ? null
            : (decimal)numerator.Value / denominator.Value;

    private static long? SumOrNull(IEnumerable<long?> values)
    {
        var present = values.Where(x => x is not null).Select(x => x!.Value).ToList();
        return present.Count == 0 ? null : present.Sum();
    }

    private static decimal? SumOrNull(IEnumerable<decimal?> values)
    {
        var present = values.Where(x => x is not null).Select(x => x!.Value).ToList();
        return present.Count == 0 ? null : present.Sum();
    }
}
