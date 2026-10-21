using System.Security.Claims;
using ContentFactory.Contracts;
using ContentFactory.Core.Exceptions;
using ContentFactory.Infrastructure.Review;

namespace ContentFactory.Api.Endpoints;

/// <summary>
/// The body never carries a reviewer: the identity is the signed-in owner. Unknown body
/// fields (for example a forged reviewerId) are ignored by binding.
/// </summary>
public sealed record SubmitReviewRequest(
    string ManifestSha256,
    bool Approved,
    Dictionary<string, bool>? Checklist,
    string? Comment);

public sealed record EditCaptionRequest(string SceneId, string Caption);

public static class ReviewEndpoints
{
    public static IEndpointRouteBuilder MapReviewEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var group = endpoints.MapGroup("/api/v1/videos").RequireAuthorization();

        group.MapGet("/", ListAsync);
        group.MapGet("/{videoVersionId:guid}", GetAsync);
        group.MapPost("/{videoVersionId:guid}/reviews", SubmitAsync);
        group.MapPost("/{videoVersionId:guid}/caption-edits", EditCaptionAsync);

        return endpoints;
    }

    private static async Task<IResult> ListAsync(
        string? status, ReviewService service, CancellationToken ct)
        => Results.Ok(await service.ListAsync(status, ct));

    private static async Task<IResult> GetAsync(
        Guid videoVersionId, ReviewService service, CancellationToken ct)
        => Results.Ok(await service.GetDetailAsync(videoVersionId, ct));

    private static async Task<IResult> SubmitAsync(
        Guid videoVersionId,
        SubmitReviewRequest request,
        ClaimsPrincipal user,
        ReviewService service,
        CancellationToken ct)
    {
        await service.RecordAsync(
            new ReviewDecision(
                videoVersionId,
                request.ManifestSha256,
                request.Approved,
                request.Checklist ?? new Dictionary<string, bool>(),
                request.Comment),
            ReviewerId(user),
            ct);
        return Results.NoContent();
    }

    private static async Task<IResult> EditCaptionAsync(
        Guid videoVersionId,
        EditCaptionRequest request,
        ClaimsPrincipal user,
        ReviewService service,
        CancellationToken ct)
    {
        var result = await service.EditCaptionAsync(
            videoVersionId, request.SceneId, request.Caption, ReviewerId(user), ct);
        return Results.Accepted(
            value: new { revisionId = result.RevisionId, jobId = result.JobId });
    }

    private static Guid ReviewerId(ClaimsPrincipal user)
        => Guid.TryParse(user.FindFirstValue(ClaimTypes.NameIdentifier), out var id)
            ? id
            : throw new ValidationException(
                "REVIEW_REVIEWER_INVALID", "The reviewer must be the signed-in owner.");
}
