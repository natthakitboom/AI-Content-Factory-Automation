using System.Net;
using Microsoft.AspNetCore.Http;

namespace ContentFactory.Api.Middleware;

/// <summary>
/// Rejects requests that did not arrive over loopback with a loopback Host/Origin.
/// This is a defence against DNS rebinding and accidental public exposure, not a
/// replacement for authentication.
/// </summary>
public sealed class LocalRequestGuard
{
    private readonly RequestDelegate _next;

    public LocalRequestGuard(RequestDelegate next)
    {
        _next = next;
    }

    public async Task InvokeAsync(HttpContext context)
    {
        if (!IsLoopbackHost(context.Request.Host))
        {
            await ApiProblem.Create(
                StatusCodes.Status400BadRequest,
                "LOCAL_ONLY",
                "Only loopback hosts are allowed.")
                .ExecuteAsync(context);
            return;
        }

        var origin = context.Request.Headers.Origin.ToString();
        if (!string.IsNullOrEmpty(origin) && !IsLoopbackOrigin(origin))
        {
            await ApiProblem.Create(
                StatusCodes.Status400BadRequest,
                "LOCAL_ONLY_ORIGIN",
                "Cross-origin requests must originate from loopback.")
                .ExecuteAsync(context);
            return;
        }

        var remote = context.Connection.RemoteIpAddress;
        if (remote is not null && !IPAddress.IsLoopback(remote))
        {
            await ApiProblem.Create(
                StatusCodes.Status400BadRequest,
                "LOCAL_ONLY_REMOTE",
                "Only loopback clients are allowed.")
                .ExecuteAsync(context);
            return;
        }

        await _next(context);
    }

    private static bool IsLoopbackHost(HostString host)
    {
        if (!host.HasValue)
        {
            return false;
        }

        var name = host.Host.Trim('[', ']');
        return string.Equals(name, "127.0.0.1", StringComparison.Ordinal)
            || string.Equals(name, "localhost", StringComparison.OrdinalIgnoreCase)
            || string.Equals(name, "::1", StringComparison.Ordinal);
    }

    private static bool IsLoopbackOrigin(string origin)
    {
        if (!Uri.TryCreate(origin, UriKind.Absolute, out var uri))
        {
            return false;
        }

        if (uri.Scheme is not ("http" or "https"))
        {
            return false;
        }

        var name = uri.Host.Trim('[', ']');
        return string.Equals(name, "127.0.0.1", StringComparison.Ordinal)
            || string.Equals(name, "localhost", StringComparison.OrdinalIgnoreCase)
            || string.Equals(name, "::1", StringComparison.Ordinal);
    }
}
