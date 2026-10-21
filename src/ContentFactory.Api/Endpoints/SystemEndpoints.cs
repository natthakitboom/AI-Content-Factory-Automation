using ContentFactory.Infrastructure.Jobs;
using ContentFactory.Infrastructure.Providers;

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
        group.MapGet("/mode", GetMode);

        return endpoints;
    }

    /// <summary>
    /// Which providers this install runs. The banner comes from explicit provider names
    /// only, so a stray credential in the environment can never relabel a mock run as live.
    /// </summary>
    private static IResult GetMode()
    {
        var modes = ProviderModes.FromEnvironment();
        return Results.Ok(new
        {
            banner = modes.Banner,
            mock = modes.IsMock,
            text = modes.Text,
            video = modes.Video,
            voice = modes.Voice
        });
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
