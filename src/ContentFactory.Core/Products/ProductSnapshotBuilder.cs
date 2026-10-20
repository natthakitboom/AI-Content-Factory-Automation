using System.Text.RegularExpressions;
using ContentFactory.Contracts;
using ContentFactory.Core.Exceptions;

namespace ContentFactory.Core.Products;

/// <summary>
/// Builds the immutable product facts that a run is planned from. Callers must pass only
/// rows that were read from PostgreSQL and approved for use; the builder validates the
/// shape and produces deterministic ordering so the same facts always hash the same way.
/// </summary>
public static partial class ProductSnapshotBuilder
{
    public static ProductSnapshot Build(
        Guid productId,
        long productVersion,
        string? title,
        string? market,
        string? locale,
        IEnumerable<ApprovedClaim>? claims,
        IEnumerable<AssetRef>? assets,
        DateTimeOffset capturedAt)
    {
        if (productId == Guid.Empty)
        {
            throw Invalid("The product id is required.");
        }

        if (productVersion <= 0)
        {
            throw Invalid("The product version must be positive.");
        }

        var normalizedTitle = RequireText(title, 512, "The product title is required.");
        var normalizedMarket = RequireText(market, 16, "The product market is required.").ToUpperInvariant();
        if (!MarketPattern().IsMatch(normalizedMarket))
        {
            throw Invalid("The product market must be a two-letter ISO code.");
        }

        var normalizedLocale = RequireText(locale, 32, "The product locale is required.");

        var approvedClaims = (claims ?? [])
            .GroupBy(claim => claim.Id)
            .Select(group => group.Single())
            .OrderBy(claim => claim.Id)
            .ToArray();

        if (approvedClaims.Length == 0)
        {
            throw new ConflictException(
                "RUN_FACTS_UNAPPROVED",
                "A run requires at least one approved and unexpired product claim.");
        }

        foreach (var claim in approvedClaims)
        {
            if (claim.Id == Guid.Empty)
            {
                throw Invalid("An approved claim must have an id.");
            }

            RequireText(claim.Text, 4000, "An approved claim must have text.");
            RequireText(
                claim.SourceReference,
                1024,
                "An approved claim must have a source reference.");
        }

        var snapshotAssets = (assets ?? [])
            .GroupBy(asset => asset.Id)
            .Select(group => group.Single())
            .OrderBy(asset => asset.Id)
            .ToArray();

        foreach (var asset in snapshotAssets)
        {
            if (asset.Id == Guid.Empty
                || !Sha256Pattern().IsMatch(asset.Sha256 ?? string.Empty)
                || string.IsNullOrWhiteSpace(asset.RelativePath))
            {
                throw Invalid("An asset reference is incomplete.");
            }
        }

        return new ProductSnapshot(
            productId,
            productVersion,
            normalizedTitle,
            normalizedMarket,
            normalizedLocale,
            approvedClaims,
            snapshotAssets,
            capturedAt.ToUniversalTime());
    }

    private static string RequireText(string? value, int maxLength, string message)
    {
        var trimmed = value?.Trim();
        if (string.IsNullOrEmpty(trimmed))
        {
            throw Invalid(message);
        }

        if (trimmed.Length > maxLength)
        {
            throw Invalid(message);
        }

        return trimmed;
    }

    private static ValidationException Invalid(string message)
        => new("RUN_FACTS_INVALID", message);

    [GeneratedRegex("^[A-Z]{2}$", RegexOptions.CultureInvariant)]
    private static partial Regex MarketPattern();

    [GeneratedRegex("^[0-9a-f]{64}$", RegexOptions.CultureInvariant)]
    private static partial Regex Sha256Pattern();
}
