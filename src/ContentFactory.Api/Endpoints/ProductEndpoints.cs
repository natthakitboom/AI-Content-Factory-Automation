using System.Security.Claims;
using System.Text.RegularExpressions;
using ContentFactory.Api.Middleware;
using ContentFactory.Contracts;
using ContentFactory.Core.Exceptions;
using ContentFactory.Infrastructure.Persistence;
using ContentFactory.Infrastructure.Persistence.Entities;
using Microsoft.EntityFrameworkCore;

namespace ContentFactory.Api.Endpoints;

public sealed record CreateProductRequest(
    string? ExternalId,
    string? Title,
    string? Market,
    string? Locale,
    string? Category);

public sealed record CreateClaimRequest(
    string? Text,
    string? SourceReference,
    string? Status,
    DateTimeOffset? ExpiresAt);

public sealed record CreateOfferRequest(
    string? Market,
    string? ProductUrl,
    decimal Price,
    string? Currency,
    decimal CommissionRate,
    string? Availability,
    DateTimeOffset? CheckedAt);

public sealed record ClaimResponse(
    Guid Id,
    Guid ProductId,
    string Text,
    string SourceReference,
    string Status,
    DateTimeOffset? VerifiedAt,
    Guid? ApprovedBy,
    DateTimeOffset? ExpiresAt,
    DateTimeOffset CreatedAt);

public sealed record OfferResponse(
    Guid Id,
    Guid ProductId,
    string Market,
    string ProductUrl,
    decimal Price,
    string Currency,
    decimal CommissionRate,
    string Availability,
    DateTimeOffset CheckedAt);

public sealed record ProductResponse(
    Guid Id,
    string? ExternalId,
    string Title,
    string Market,
    string Locale,
    string? Category,
    long Version,
    DateTimeOffset? ArchivedAt,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    IReadOnlyList<ClaimResponse> Claims,
    IReadOnlyList<AssetRef> Assets,
    IReadOnlyList<OfferResponse> Offers);

public static partial class ProductEndpoints
{
    private static readonly string[] ClaimStatuses = ["pending", "approved", "rejected", "expired"];

    public static IEndpointRouteBuilder MapProductEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var group = endpoints.MapGroup("/api/v1/products").RequireAuthorization();

        group.MapPost("/", CreateProductAsync);
        group.MapGet("/{productId:guid}", GetProductAsync);
        group.MapPost("/{productId:guid}/claims", CreateClaimAsync);
        group.MapPost("/{productId:guid}/offers", CreateOfferAsync);

        return endpoints;
    }

    private static async Task<IResult> CreateProductAsync(
        CreateProductRequest request,
        AppDbContext db,
        TimeProvider clock,
        CancellationToken ct)
    {
        var title = Required(request.Title, 512, "PRODUCT_TITLE_INVALID", "A product title is required.");
        var market = Required(request.Market, 16, "PRODUCT_MARKET_INVALID", "A product market is required.")
            .ToUpperInvariant();
        if (!MarketPattern().IsMatch(market))
        {
            throw Invalid("PRODUCT_MARKET_INVALID", "The product market must be a two-letter ISO code.");
        }

        var locale = Required(request.Locale, 32, "PRODUCT_LOCALE_INVALID", "A product locale is required.");
        var externalId = Optional(request.ExternalId, 128, "PRODUCT_EXTERNAL_ID_INVALID");
        var category = Optional(request.Category, 128, "PRODUCT_CATEGORY_INVALID");

        if (externalId is not null)
        {
            var duplicate = await db.Products
                .AsNoTracking()
                .AnyAsync(x => x.Market == market && x.ExternalId == externalId, ct);

            if (duplicate)
            {
                throw new ConflictException(
                    "PRODUCT_EXTERNAL_ID_EXISTS",
                    "A product with this market and external id already exists.");
            }
        }

        var now = clock.GetUtcNow();
        var product = new Product
        {
            Id = Guid.NewGuid(),
            ExternalId = externalId,
            Title = title,
            Market = market,
            Locale = locale,
            Category = category,
            Version = 1,
            CreatedAt = now,
            UpdatedAt = now
        };

        db.Products.Add(product);
        await db.SaveChangesAsync(ct);

        return Results.Created(
            $"/api/v1/products/{product.Id}",
            new ProductResponse(
                product.Id,
                product.ExternalId,
                product.Title,
                product.Market,
                product.Locale,
                product.Category,
                product.Version,
                product.ArchivedAt,
                product.CreatedAt,
                product.UpdatedAt,
                [],
                [],
                []));
    }

    private static async Task<IResult> GetProductAsync(
        Guid productId,
        AppDbContext db,
        CancellationToken ct)
    {
        var product = await db.Products
            .AsNoTracking()
            .SingleOrDefaultAsync(x => x.Id == productId, ct)
            ?? throw new NotFoundException("PRODUCT_NOT_FOUND", "Product not found.");

        var claims = await db.ProductClaims
            .AsNoTracking()
            .Where(x => x.ProductId == productId)
            .OrderBy(x => x.CreatedAt)
            .ThenBy(x => x.Id)
            .Select(x => new ClaimResponse(
                x.Id,
                x.ProductId,
                x.Text,
                x.SourceReference,
                x.Status,
                x.VerifiedAt,
                x.ApprovedBy,
                x.ExpiresAt,
                x.CreatedAt))
            .ToListAsync(ct);

        var assets = await (
                from link in db.ProductAssets.AsNoTracking()
                join asset in db.Assets.AsNoTracking() on link.AssetId equals asset.Id
                where link.ProductId == productId
                orderby link.DisplayOrder, asset.Id
                select new AssetRef(
                    asset.Id,
                    asset.Sha256,
                    asset.RelativePath,
                    asset.Kind,
                    asset.Mime,
                    asset.Bytes,
                    asset.DurationMs))
            .ToListAsync(ct);

        var offers = await db.ProductOffers
            .AsNoTracking()
            .Where(x => x.ProductId == productId)
            .OrderBy(x => x.Market)
            .ThenBy(x => x.CheckedAt)
            .Select(x => new OfferResponse(
                x.Id,
                x.ProductId,
                x.Market,
                x.ProductUrl,
                x.Price,
                x.Currency,
                x.CommissionRate,
                x.Availability,
                x.CheckedAt))
            .ToListAsync(ct);

        return Results.Ok(new ProductResponse(
            product.Id,
            product.ExternalId,
            product.Title,
            product.Market,
            product.Locale,
            product.Category,
            product.Version,
            product.ArchivedAt,
            product.CreatedAt,
            product.UpdatedAt,
            claims,
            assets,
            offers));
    }

    private static async Task<IResult> CreateClaimAsync(
        Guid productId,
        CreateClaimRequest request,
        ClaimsPrincipal user,
        AppDbContext db,
        TimeProvider clock,
        CancellationToken ct)
    {
        var product = await db.Products
            .AsNoTracking()
            .SingleOrDefaultAsync(x => x.Id == productId, ct)
            ?? throw new NotFoundException("PRODUCT_NOT_FOUND", "Product not found.");

        if (product.ArchivedAt is not null)
        {
            throw new ConflictException("PRODUCT_ARCHIVED", "An archived product cannot gain claims.");
        }

        var text = Required(request.Text, 4000, "CLAIM_TEXT_INVALID", "Claim text is required.");
        var source = Required(
            request.SourceReference,
            1024,
            "CLAIM_SOURCE_REQUIRED",
            "A claim requires a source reference.");

        var status = string.IsNullOrWhiteSpace(request.Status)
            ? "pending"
            : request.Status.Trim().ToLowerInvariant();

        if (!ClaimStatuses.Contains(status, StringComparer.Ordinal))
        {
            throw Invalid(
                "CLAIM_STATUS_INVALID",
                "Claim status must be pending, approved, rejected or expired.");
        }

        var now = clock.GetUtcNow();
        Guid? approvedBy = null;
        if (status == "approved")
        {
            var ownerValue = user.FindFirstValue(ClaimTypes.NameIdentifier);
            if (!Guid.TryParse(ownerValue, out var ownerId))
            {
                throw new ConflictException(
                    "CLAIM_APPROVER_REQUIRED",
                    "An approved claim requires a signed-in owner.");
            }

            approvedBy = ownerId;
        }

        var claim = new ProductClaim
        {
            Id = Guid.NewGuid(),
            ProductId = productId,
            Text = text,
            SourceReference = source,
            Status = status,
            VerifiedAt = status == "approved" ? now : null,
            ApprovedBy = approvedBy,
            ExpiresAt = request.ExpiresAt,
            CreatedAt = now
        };

        db.ProductClaims.Add(claim);
        await db.SaveChangesAsync(ct);

        return Results.Created(
            $"/api/v1/products/{productId}",
            new ClaimResponse(
                claim.Id,
                claim.ProductId,
                claim.Text,
                claim.SourceReference,
                claim.Status,
                claim.VerifiedAt,
                claim.ApprovedBy,
                claim.ExpiresAt,
                claim.CreatedAt));
    }

    private static async Task<IResult> CreateOfferAsync(
        Guid productId,
        CreateOfferRequest request,
        AppDbContext db,
        TimeProvider clock,
        CancellationToken ct)
    {
        var exists = await db.Products
            .AsNoTracking()
            .AnyAsync(x => x.Id == productId && x.ArchivedAt == null, ct);

        if (!exists)
        {
            throw new NotFoundException("PRODUCT_NOT_FOUND", "Product not found.");
        }

        var market = Required(request.Market, 16, "OFFER_MARKET_INVALID", "An offer market is required.")
            .ToUpperInvariant();
        if (!MarketPattern().IsMatch(market))
        {
            throw Invalid("OFFER_MARKET_INVALID", "The offer market must be a two-letter ISO code.");
        }

        var productUrl = Required(
            request.ProductUrl,
            2048,
            "OFFER_URL_INVALID",
            "A product URL is required.");

        if (!Uri.TryCreate(productUrl, UriKind.Absolute, out var uri)
            || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
        {
            throw Invalid("OFFER_URL_INVALID", "The product URL must be absolute http or https.");
        }

        if (request.Price < 0)
        {
            throw Invalid("OFFER_PRICE_INVALID", "The price cannot be negative.");
        }

        var currency = Required(
            request.Currency,
            3,
            "OFFER_CURRENCY_INVALID",
            "A currency is required.").ToUpperInvariant();
        if (!CurrencyPattern().IsMatch(currency))
        {
            throw Invalid("OFFER_CURRENCY_INVALID", "The currency must be a three-letter ISO code.");
        }

        if (request.CommissionRate is < 0 or > 1)
        {
            throw Invalid("OFFER_COMMISSION_INVALID", "The commission rate must be between 0 and 1.");
        }

        var availability = Required(
            request.Availability,
            64,
            "OFFER_AVAILABILITY_INVALID",
            "An availability value is required.");

        var offer = new ProductOffer
        {
            Id = Guid.NewGuid(),
            ProductId = productId,
            Market = market,
            ProductUrl = productUrl,
            Price = request.Price,
            Currency = currency,
            CommissionRate = request.CommissionRate,
            Availability = availability,
            CheckedAt = request.CheckedAt ?? clock.GetUtcNow(),
            CreatedAt = clock.GetUtcNow()
        };

        db.ProductOffers.Add(offer);
        await db.SaveChangesAsync(ct);

        return Results.Created(
            $"/api/v1/products/{productId}",
            new OfferResponse(
                offer.Id,
                offer.ProductId,
                offer.Market,
                offer.ProductUrl,
                offer.Price,
                offer.Currency,
                offer.CommissionRate,
                offer.Availability,
                offer.CheckedAt));
    }

    private static string Required(string? value, int maxLength, string code, string message)
    {
        var trimmed = value?.Trim();
        if (string.IsNullOrEmpty(trimmed))
        {
            throw Invalid(code, message);
        }

        if (trimmed.Length > maxLength)
        {
            throw Invalid(
                code,
                $"The value must be {maxLength} characters or fewer.");
        }

        return trimmed;
    }

    private static string? Optional(string? value, int maxLength, string code)
    {
        var trimmed = value?.Trim();
        if (string.IsNullOrEmpty(trimmed))
        {
            return null;
        }

        if (trimmed.Length > maxLength)
        {
            throw Invalid(code, $"The value must be {maxLength} characters or fewer.");
        }

        return trimmed;
    }

    private static ValidationException Invalid(string code, string message) => new(code, message);

    [GeneratedRegex("^[A-Z]{2}$", RegexOptions.CultureInvariant)]
    private static partial Regex MarketPattern();

    [GeneratedRegex("^[A-Z]{3}$", RegexOptions.CultureInvariant)]
    private static partial Regex CurrencyPattern();
}
