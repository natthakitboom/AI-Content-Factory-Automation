using System.Security.Cryptography;
using Microsoft.AspNetCore.Http;

namespace ContentFactory.Api.Middleware;

/// <summary>
/// Double-submit CSRF protection for every mutating request. The cookie is HttpOnly;
/// the same token is returned once by GET /api/v1/auth/csrf for the client to send in
/// the X-CSRF-TOKEN header. Login is included because a login CSRF is still a real risk.
/// </summary>
public sealed class CsrfMiddleware
{
    public const string CookieName = "cf_csrf";
    public const string HeaderName = "X-CSRF-TOKEN";

    private readonly RequestDelegate _next;

    public CsrfMiddleware(RequestDelegate next)
    {
        _next = next;
    }

    public static string CreateToken()
        => Convert.ToBase64String(RandomNumberGenerator.GetBytes(32))
            .TrimEnd('=')
            .Replace('+', '-')
            .Replace('/', '_');

    public async Task InvokeAsync(HttpContext context)
    {
        if (!IsMutating(context.Request.Method) || IsExemptPath(context.Request.Path))
        {
            await _next(context);
            return;
        }

        var cookie = context.Request.Cookies[CookieName];
        var header = context.Request.Headers[HeaderName].ToString();

        if (string.IsNullOrEmpty(cookie) || string.IsNullOrEmpty(header) || !FixedTimeEquals(cookie, header))
        {
            await ApiProblem.Create(
                StatusCodes.Status400BadRequest,
                "CSRF_INVALID",
                "A valid CSRF token is required for this request.")
                .ExecuteAsync(context);
            return;
        }

        await _next(context);
    }

    private static bool IsMutating(string method)
        => HttpMethods.IsPost(method)
        || HttpMethods.IsPut(method)
        || HttpMethods.IsPatch(method)
        || HttpMethods.IsDelete(method);

    private static bool IsExemptPath(PathString path)
        => path.StartsWithSegments("/health", StringComparison.OrdinalIgnoreCase);

    private static bool FixedTimeEquals(string left, string right)
    {
        var leftBytes = System.Text.Encoding.UTF8.GetBytes(left);
        var rightBytes = System.Text.Encoding.UTF8.GetBytes(right);
        return leftBytes.Length == rightBytes.Length
            && CryptographicOperations.FixedTimeEquals(leftBytes, rightBytes);
    }
}
