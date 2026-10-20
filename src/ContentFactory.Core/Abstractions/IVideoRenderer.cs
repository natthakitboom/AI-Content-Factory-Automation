using ContentFactory.Contracts;

namespace ContentFactory.Core.Abstractions;

public interface IVideoRenderer
{
    Task<RenderResult> RenderAsync(RenderManifest manifest, CancellationToken ct);
}
