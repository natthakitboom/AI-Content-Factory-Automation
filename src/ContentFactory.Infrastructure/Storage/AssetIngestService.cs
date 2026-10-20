using ContentFactory.Contracts;
using ContentFactory.Core.Abstractions;
using ContentFactory.Core.Assets;
using ContentFactory.Core.Exceptions;
using ContentFactory.Infrastructure.Persistence;
using ContentFactory.Infrastructure.Persistence.Entities;
using Microsoft.EntityFrameworkCore;

namespace ContentFactory.Infrastructure.Storage;

public sealed record AssetIngestRequest(
    Stream Content,
    string? FileName,
    AssetKind Kind,
    string? ClaimedMime,
    string? RightsStatus,
    string? SourceReference,
    bool CloudUseAllowed,
    Guid? ProductId = null,
    bool RequireProductionRights = false);

/// <summary>
/// Applies rights, source and cloud-consent metadata to an immutable content-addressed
/// asset. The binary hash never implies usage rights. Attaching an upload to a product is
/// a production action and therefore requires an owner attestation with a source reference.
/// </summary>
public sealed class AssetIngestService
{
    private readonly AppDbContext _db;
    private readonly IAssetStore _store;

    public AssetIngestService(AppDbContext db, IAssetStore store)
    {
        _db = db;
        _store = store;
    }

    public async Task<AssetRef> IngestAsync(AssetIngestRequest request, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(request.Content);

        if (request.FileName is not null && !AssetPathResolver.IsSafeUploadName(request.FileName))
        {
            throw new ValidationException(
                "ASSET_FILENAME_UNSAFE",
                "Upload filename contains a path separator, traversal or control character.");
        }

        if (request.SourceReference is { Length: > 1024 })
        {
            throw new ValidationException(
                "ASSET_SOURCE_TOO_LONG",
                "The source reference must be 1024 characters or fewer.");
        }

        var rightsStatus = AssetRightsPolicy.Normalize(request.RightsStatus);
        if (rightsStatus == AssetRightsPolicy.OwnerAttested
            && string.IsNullOrWhiteSpace(request.SourceReference))
        {
            throw new ValidationException(
                "ASSET_RIGHTS_REQUIRED",
                "An owner attestation requires a source reference.");
        }

        if (request.RequireProductionRights)
        {
            AssetRightsPolicy.EnsureUsableInProduction(rightsStatus, request.SourceReference);
        }

        AssetRightsPolicy.EnsureCloudUseAllowed(rightsStatus, request.CloudUseAllowed);

        if (request.ProductId is { } productId)
        {
            var exists = await _db.Products
                .AsNoTracking()
                .AnyAsync(x => x.Id == productId, ct);

            if (!exists)
            {
                throw new NotFoundException("PRODUCT_NOT_FOUND", "Product not found.");
            }
        }

        var assetRef = await _store.PutAsync(
            request.Content,
            request.Kind,
            request.ClaimedMime ?? string.Empty,
            ct);

        var asset = await _db.Assets.SingleAsync(x => x.Id == assetRef.Id, ct);
        ApplyRights(asset, rightsStatus, request.SourceReference, request.CloudUseAllowed);

        if (request.ProductId is { } linkedProductId)
        {
            var alreadyLinked = await _db.ProductAssets
                .AnyAsync(x => x.ProductId == linkedProductId && x.AssetId == assetRef.Id, ct);

            if (!alreadyLinked)
            {
                var lastOrder = await _db.ProductAssets
                    .Where(x => x.ProductId == linkedProductId)
                    .MaxAsync(x => (int?)x.DisplayOrder, ct);

                _db.ProductAssets.Add(new ProductAsset
                {
                    ProductId = linkedProductId,
                    AssetId = assetRef.Id,
                    DisplayOrder = (lastOrder ?? -1) + 1
                });
            }
        }

        await _db.SaveChangesAsync(ct);
        return assetRef;
    }

    private static void ApplyRights(
        Asset asset,
        string rightsStatus,
        string? sourceReference,
        bool cloudUseAllowed)
    {
        switch (rightsStatus)
        {
            case AssetRightsPolicy.OwnerAttested:
                if (asset.RightsStatus != AssetRightsPolicy.OwnerAttested)
                {
                    asset.RightsStatus = AssetRightsPolicy.OwnerAttested;
                }

                if (string.IsNullOrWhiteSpace(asset.SourceReference))
                {
                    asset.SourceReference = sourceReference!.Trim();
                }

                if (cloudUseAllowed)
                {
                    asset.CloudUseAllowed = true;
                }

                break;

            case AssetRightsPolicy.Rejected:
                asset.RightsStatus = AssetRightsPolicy.Rejected;
                asset.CloudUseAllowed = false;
                break;

            default:
                // "unknown" never clears an existing attestation.
                break;
        }
    }
}
