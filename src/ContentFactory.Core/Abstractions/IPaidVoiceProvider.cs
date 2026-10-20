using ContentFactory.Contracts;

namespace ContentFactory.Core.Abstractions;

/// <summary>
/// A voice provider that may incur a provider charge. The unscoped
/// <see cref="IVoiceProvider.SynthesizeAsync"/> method is not enough to authorize paid
/// work: callers must supply the campaign and video/revision scope so the same budget and
/// live-approval guards used for video also cover speech synthesis.
/// </summary>
public interface IPaidVoiceProvider : IVoiceProvider
{
    Task<AssetRef> SynthesizePaidAsync(
        VoiceRequest request,
        Guid campaignId,
        Guid videoScopeId,
        CancellationToken ct);
}
