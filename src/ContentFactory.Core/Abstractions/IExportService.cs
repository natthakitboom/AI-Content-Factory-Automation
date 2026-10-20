using ContentFactory.Contracts;

namespace ContentFactory.Core.Abstractions;

public interface IExportService
{
    Task<AssetRef> CreateAsync(Guid videoVersionId, Guid channelId, CancellationToken ct);
}
