using ContentFactory.Contracts;
using ContentFactory.Core.Abstractions;

namespace ContentFactory.Infrastructure.Providers.Mock;

/// <summary>
/// Offline stand-in for the cloud image-to-video provider behind the same interface. It
/// is never billable: there is no price, reservation or network. Its output URL points at
/// the mock media host, which <see cref="MockMediaHandler"/> serves from a local clip.
/// </summary>
public sealed class MockVideoProvider : IVideoProvider
{
    public const string Host = "mock.fal.media";

    private int _submitCount;

    public string Name => "mock";

    public bool IsBillable => false;

    public int SubmitCount => _submitCount;

    /// <summary>Always zero by construction: this provider has no billable path.</summary>
    public int BillableRequestCount => 0;

    public Task<ProviderSubmission> SubmitAsync(VideoGenerationRequest request, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);
        ct.ThrowIfCancellationRequested();
        Interlocked.Increment(ref _submitCount);

        var id = "mock-" + Convert.ToHexString(
            System.Security.Cryptography.SHA256.HashData(
                System.Text.Encoding.UTF8.GetBytes(request.OperationId)))[..24].ToLowerInvariant();
        return Task.FromResult(new ProviderSubmission(
            Name,
            "mock",
            id,
            new Uri($"https://{Host}/queue/{id}/status"),
            new Uri($"https://{Host}/queue/{id}"),
            new Uri($"https://{Host}/queue/{id}/cancel")));
    }

    public Task<ProviderPollResult> PollAsync(ProviderSubmission submission, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(submission);
        ct.ThrowIfCancellationRequested();
        return Task.FromResult(new ProviderPollResult(
            ProviderRunStatus.Succeeded,
            new Uri($"https://{Host}/files/{submission.RequestId}.mp4"),
            null,
            0));
    }

    public Task RequestCancelAsync(ProviderSubmission submission, CancellationToken ct)
        => Task.CompletedTask;
}
