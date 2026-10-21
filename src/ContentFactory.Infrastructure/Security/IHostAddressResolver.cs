using System.Net;

namespace ContentFactory.Infrastructure.Security;

public interface IHostAddressResolver
{
    Task<IReadOnlyList<IPAddress>> ResolveAsync(string host, CancellationToken ct);
}

public sealed class DnsHostAddressResolver : IHostAddressResolver
{
    public async Task<IReadOnlyList<IPAddress>> ResolveAsync(string host, CancellationToken ct)
        => await Dns.GetHostAddressesAsync(host, ct);
}
