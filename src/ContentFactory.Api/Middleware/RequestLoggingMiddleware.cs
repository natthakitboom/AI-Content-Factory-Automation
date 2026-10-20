using ContentFactory.Infrastructure.Security;

namespace ContentFactory.Api.Middleware;

/// <summary>
/// Logs only the method and the redacted path/query. Headers and bodies are never logged,
/// and the query is passed through SecretRedactor because clients sometimes put tokens in it.
/// </summary>
public sealed class RequestLoggingMiddleware
{
    private readonly RequestDelegate _next;
    private readonly ILogger<RequestLoggingMiddleware> _logger;

    public RequestLoggingMiddleware(RequestDelegate next, ILogger<RequestLoggingMiddleware> logger)
    {
        _next = next;
        _logger = logger;
    }

    public async Task InvokeAsync(HttpContext context, SecretRedactor redactor)
    {
        var target = redactor.Redact(context.Request.Path + context.Request.QueryString.Value);
        _logger.LogInformation("HTTP {Method} {Target}", context.Request.Method, target);
        await _next(context);
    }
}
