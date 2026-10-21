using ContentFactory.Contracts;

namespace ContentFactory.Core.Abstractions;

public interface IVideoProvider
{
    string Name { get; }

    /// <summary>
    /// False only for offline providers. A non-billable provider never needs a price
    /// snapshot or budget reservation; every live adapter keeps the default.
    /// </summary>
    bool IsBillable => true;
    Task<ProviderSubmission> SubmitAsync(VideoGenerationRequest request, CancellationToken ct);
    Task<ProviderPollResult> PollAsync(ProviderSubmission submission, CancellationToken ct);
    Task RequestCancelAsync(ProviderSubmission submission, CancellationToken ct);
}
