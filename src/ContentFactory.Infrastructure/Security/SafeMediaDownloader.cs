using System.Net;
using System.Net.Sockets;
using ContentFactory.Contracts;
using ContentFactory.Core.Abstractions;
using ContentFactory.Core.Exceptions;

namespace ContentFactory.Infrastructure.Security;

/// <summary>
/// Downloads provider output that is untrusted input: HTTPS only, allowlisted hosts, every
/// redirect hop re-validated (including DNS results), private/loopback/link-local targets
/// denied, bounded redirects, size and time. No credentials are ever attached. The body is
/// streamed into the content-addressed asset store, so the same bytes dedupe by hash.
/// </summary>
public sealed class SafeMediaDownloader
{
    private readonly HttpClient _http;
    private readonly SafeMediaDownloaderOptions _options;
    private readonly IHostAddressResolver _resolver;

    public SafeMediaDownloader(
        HttpClient http,
        SafeMediaDownloaderOptions options,
        IHostAddressResolver resolver)
    {
        _http = http ?? throw new ArgumentNullException(nameof(http));
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _resolver = resolver ?? throw new ArgumentNullException(nameof(resolver));
    }

    /// <summary>
    /// Production handler: no automatic redirects, and the connection goes to an address
    /// that was validated at connect time, which closes the DNS-rebinding gap between the
    /// pre-check and the socket.
    /// </summary>
    public static SocketsHttpHandler CreateHandler(IHostAddressResolver resolver)
    {
        ArgumentNullException.ThrowIfNull(resolver);
        return new SocketsHttpHandler
        {
            AllowAutoRedirect = false,
            UseCookies = false,
            UseProxy = false,
            ConnectCallback = async (context, ct) =>
            {
                var addresses = await resolver.ResolveAsync(context.DnsEndPoint.Host, ct);
                var address = addresses.FirstOrDefault(IsPublicAddress)
                    ?? throw new HttpRequestException("No public address for media host.");
                var socket = new Socket(address.AddressFamily, SocketType.Stream, ProtocolType.Tcp)
                {
                    NoDelay = true
                };
                try
                {
                    await socket.ConnectAsync(new IPEndPoint(address, context.DnsEndPoint.Port), ct);
                    return new NetworkStream(socket, ownsSocket: true);
                }
                catch
                {
                    socket.Dispose();
                    throw;
                }
            }
        };
    }

    public async Task<AssetRef> DownloadAsync(
        Uri url,
        IAssetStore store,
        AssetKind kind,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(url);
        ArgumentNullException.ThrowIfNull(store);

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(_options.Timeout);

        try
        {
            var current = url;
            for (var hop = 0; ; hop++)
            {
                await ValidateAsync(current, timeout.Token);

                using var request = new HttpRequestMessage(HttpMethod.Get, current);
                using var response = await _http.SendAsync(
                    request, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
                var status = (int)response.StatusCode;

                if (status is >= 300 and < 400)
                {
                    var location = response.Headers.Location
                        ?? throw Rejected("A redirect had no Location header.");
                    if (hop >= _options.MaxRedirects)
                    {
                        throw Rejected("Too many redirects.");
                    }

                    current = location.IsAbsoluteUri ? location : new Uri(current, location);
                    continue;
                }

                if (status is 401 or 403 or 404 or 410)
                {
                    throw new MediaUrlExpiredException(
                        $"The media URL was refused with status {status}.");
                }

                response.EnsureSuccessStatusCode();

                if (response.Content.Headers.ContentLength is { } declared
                    && declared > _options.MaxBytes)
                {
                    throw TooLarge();
                }

                await using var body = await response.Content.ReadAsStreamAsync(timeout.Token);
                await using var limited = new LimitedStream(body, _options.MaxBytes, TooLarge);
                return await store.PutAsync(limited, kind, "video/mp4", timeout.Token);
            }
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new HttpRequestException("The media download timed out.");
        }
    }

    private async Task ValidateAsync(Uri url, CancellationToken ct)
    {
        if (!url.IsAbsoluteUri
            || url.Scheme != Uri.UriSchemeHttps
            || url.Port != 443
            || !string.IsNullOrEmpty(url.UserInfo)
            || IPAddress.TryParse(url.Host.Trim('[', ']'), out _)
            || !IsAllowedHost(url.IdnHost))
        {
            throw Rejected("The media URL is not an approved HTTPS host.");
        }

        var addresses = await _resolver.ResolveAsync(url.IdnHost, ct);
        if (addresses.Count == 0 || !addresses.All(IsPublicAddress))
        {
            throw Rejected("The media host resolves to a non-public address.");
        }
    }

    private bool IsAllowedHost(string host)
        => _options.AllowedHosts.Any(allowed =>
            host.Equals(allowed, StringComparison.OrdinalIgnoreCase)
            || host.EndsWith("." + allowed, StringComparison.OrdinalIgnoreCase));

    public static bool IsPublicAddress(IPAddress address)
    {
        if (address.IsIPv4MappedToIPv6)
        {
            address = address.MapToIPv4();
        }

        if (IPAddress.IsLoopback(address)
            || address.Equals(IPAddress.Any)
            || address.Equals(IPAddress.IPv6Any)
            || address.Equals(IPAddress.None)
            || address.IsIPv6LinkLocal
            || address.IsIPv6SiteLocal
            || address.IsIPv6Multicast)
        {
            return false;
        }

        var bytes = address.GetAddressBytes();
        if (address.AddressFamily == AddressFamily.InterNetworkV6)
        {
            return (bytes[0] & 0xFE) != 0xFC; // unique local fc00::/7
        }

        return bytes[0] switch
        {
            0 or 10 or 127 => false,
            100 => bytes[1] is < 64 or > 127,
            169 => bytes[1] != 254,
            172 => bytes[1] is < 16 or > 31,
            192 => !(bytes[1] == 168 || (bytes[1] == 0 && bytes[2] == 0)),
            198 => bytes[1] is not (18 or 19),
            >= 224 => false,
            _ => true
        };
    }

    private static ValidationException Rejected(string message)
        => new("MEDIA_URL_REJECTED", message);

    private static ValidationException TooLarge()
        => new("MEDIA_TOO_LARGE", "The media download exceeds the size cap.");

    private sealed class LimitedStream : Stream
    {
        private readonly Stream _inner;
        private readonly long _limit;
        private readonly Func<Exception> _overflow;
        private long _total;

        public LimitedStream(Stream inner, long limit, Func<Exception> overflow)
        {
            _inner = inner;
            _limit = limit;
            _overflow = overflow;
        }

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override async ValueTask<int> ReadAsync(
            Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            var read = await _inner.ReadAsync(buffer, cancellationToken);
            _total += read;
            if (_total > _limit)
            {
                throw _overflow();
            }

            return read;
        }

        public override int Read(byte[] buffer, int offset, int count)
        {
            var read = _inner.Read(buffer, offset, count);
            _total += read;
            if (_total > _limit)
            {
                throw _overflow();
            }

            return read;
        }

        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
