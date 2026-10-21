using ContentFactory.Infrastructure.Jobs;
using ContentFactory.Infrastructure.Operations;
using ContentFactory.Infrastructure.Storage;
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
        group.MapGet("/status", async (OperationsService s, CancellationToken ct) => Results.Ok(await s.StatusAsync(ct)));
        group.MapGet("/jobs", async (string? status, int? limit, OperationsService s, CancellationToken ct)
            => Results.Ok(await s.JobsAsync(status, limit ?? 50, ct)));
        group.MapGet("/jobs/{id:guid}", async (Guid id, OperationsService s, CancellationToken ct)
            => Results.Ok(await s.JobDetailAsync(id, ct)));
        group.MapGet("/costs", async (OperationsService s, CancellationToken ct) => Results.Ok(await s.CostsAsync(ct)));
        group.MapGet("/diagnostics", async (DiagnosticsService d, CancellationToken ct) => Results.Ok(await d.RunAsync(ct)));

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
        SystemFlagStore flags,
        CancellationToken ct)
    {
        await flags.SetAsync(OperationsService.QueuePausedKey, "true");
        await store.PauseAsync(ct);
        return Results.NoContent();
    }

    private static async Task<IResult> ResumeAsync(
        PostgresJobStore store,
        SystemFlagStore flags,
        CancellationToken ct)
    {
        await flags.SetAsync(OperationsService.QueuePausedKey, null);
        await store.ResumeAsync(ct);
        return Results.NoContent();
    }
}
