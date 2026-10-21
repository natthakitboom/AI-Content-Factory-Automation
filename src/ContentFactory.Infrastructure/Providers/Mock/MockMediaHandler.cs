using System.Net;

namespace ContentFactory.Infrastructure.Providers.Mock;

/// <summary>
/// The only "network" mock mode has. It answers requests for the mock media host from a
/// local clip and refuses everything else, counting each refusal so tests can prove that
/// no external request was attempted.
/// </summary>
public sealed class MockMediaHandler : HttpMessageHandler
{
    private readonly Func<CancellationToken, Task<byte[]>> _clip;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private byte[]? _cached;
    private int _externalRequests;

    public MockMediaHandler(Func<CancellationToken, Task<byte[]>> clip)
    {
        _clip = clip ?? throw new ArgumentNullException(nameof(clip));
    }

    public int ExternalRequestCount => _externalRequests;

    protected override async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var uri = request.RequestUri;
        if (uri is null
            || !string.Equals(uri.Host, MockVideoProvider.Host, StringComparison.OrdinalIgnoreCase))
        {
            Interlocked.Increment(ref _externalRequests);
            throw new HttpRequestException("Mock mode does not allow external network access.");
        }

        await _gate.WaitAsync(cancellationToken);
        try
        {
            _cached ??= await _clip(cancellationToken);
        }
        finally
        {
            _gate.Release();
        }

        return new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new ByteArrayContent(_cached)
        };
    }
}
