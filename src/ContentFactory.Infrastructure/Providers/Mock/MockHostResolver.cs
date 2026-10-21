using System.Net;
using ContentFactory.Infrastructure.Security;

namespace ContentFactory.Infrastructure.Providers.Mock;

/// <summary>Resolves without DNS so mock downloads never touch a resolver.</summary>
public sealed class MockHostResolver : IHostAddressResolver
{
    public Task<IReadOnlyList<IPAddress>> ResolveAsync(string host, CancellationToken ct)
        => Task.FromResult<IReadOnlyList<IPAddress>>([IPAddress.Parse("93.184.216.34")]);
}
