namespace ContentFactory.Api.Middleware;

/// <summary>
/// Refuses to start with a non-loopback binding. The MVP must never expose the API on a
/// LAN or public interface, even if an environment variable says so.
/// </summary>
public static class LocalBindingGuard
{
    public static void Validate(string? urls)
    {
        if (string.IsNullOrWhiteSpace(urls))
        {
            return;
        }

        foreach (var url in urls.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (!Uri.TryCreate(url, UriKind.Absolute, out var uri))
            {
                throw new InvalidOperationException($"ASPNETCORE_URLS contains an invalid URL: {url}");
            }

            var host = uri.Host.Trim('[', ']');
            var loopback = string.Equals(host, "127.0.0.1", StringComparison.Ordinal)
                || string.Equals(host, "localhost", StringComparison.OrdinalIgnoreCase)
                || string.Equals(host, "::1", StringComparison.Ordinal);

            if (!loopback)
            {
                throw new InvalidOperationException(
                    $"Refusing to bind to non-loopback host '{host}'. Use 127.0.0.1 for this local-only API.");
            }
        }
    }
}
