using ContentFactory.Contracts;

namespace ContentFactory.Core.Abstractions;

public interface IAssetStore
{
    Task<AssetRef> PutAsync(Stream input, AssetKind kind, string mime, CancellationToken ct);
    Task<Stream> OpenReadAsync(Guid assetId, CancellationToken ct);
}
