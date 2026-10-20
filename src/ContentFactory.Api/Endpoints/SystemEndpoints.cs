using ContentFactory.Infrastructure.Jobs;

namespace ContentFactory.Api.Endpoints;

/// <summary>
/// Owner-only control surface. Pause withholds new heavy-local and cloud-submit jobs; it
/// is not a refund, a provider cancellation, or permission to ignore the paid-work guard.
/// </summary>
public static class SystemEndpoints
{
    public static IEndpointRouteBuilder MapSystemEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var group = endpoints.MapGroup("/api/v1/system").RequireAuthorization();

        group.MapPost("/pause", PauseAsync);
        group.MapPost("/resume", ResumeAsync);

        return endpoints;
    }

    private static async Task<IResult> PauseAsync(
        PostgresJobStore store,
        CancellationToken ct)
    {
        await store.PauseAsync(ct);
        return Results.NoContent();
    }

    private static async Task<IResult> ResumeAsync(
        PostgresJobStore store,
        CancellationToken ct)
    {
        await store.ResumeAsync(ct);
        return Results.NoContent();
    }
}
