using ContentFactory.Contracts;

namespace ContentFactory.Core.Abstractions;

public interface IVideoProvider
{
    string Name { get; }
    Task<ProviderSubmission> SubmitAsync(VideoGenerationRequest request, CancellationToken ct);
    Task<ProviderPollResult> PollAsync(ProviderSubmission submission, CancellationToken ct);
    Task RequestCancelAsync(ProviderSubmission submission, CancellationToken ct);
}
