using ContentFactory.Infrastructure.Persistence;
using ContentFactory.Infrastructure.Storage;
using Microsoft.EntityFrameworkCore;

namespace ContentFactory.Api.Middleware;

/// <summary>
/// While a backup holds the write-quiesce flag, every mutating request is refused with a
/// retryable 503 so the database and files cannot change between the dump and the copy.
/// Reads and health checks are untouched.
/// </summary>
public sealed class QuiesceMiddleware
{
    private readonly RequestDelegate _next;

    public QuiesceMiddleware(RequestDelegate next)
    {
        _next = next;
    }

    public async Task InvokeAsync(HttpContext context, AppDbContext db)
    {
        var method = context.Request.Method;
        var mutating = HttpMethods.IsPost(method) || HttpMethods.IsPut(method)
            || HttpMethods.IsPatch(method) || HttpMethods.IsDelete(method);
        if (mutating
            && !context.Request.Path.StartsWithSegments("/health", StringComparison.OrdinalIgnoreCase)
            && await db.SystemFlags.AsNoTracking().AnyAsync(
                x => x.Key == SystemFlagStore.WriteQuiesce && x.Value == "true", context.RequestAborted))
        {
            context.Response.Headers["Retry-After"] = "30";
            await ApiProblem.Create(
                StatusCodes.Status503ServiceUnavailable,
                "SYSTEM_QUIESCED",
                "A backup is running. Try again in a moment.")
                .ExecuteAsync(context);
            return;
        }

        await _next(context);
    }
}
