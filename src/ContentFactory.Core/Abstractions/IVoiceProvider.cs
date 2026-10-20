using ContentFactory.Contracts;

namespace ContentFactory.Core.Abstractions;

public interface IVoiceProvider
{
    Task<AssetRef> SynthesizeAsync(VoiceRequest request, CancellationToken ct);
}
