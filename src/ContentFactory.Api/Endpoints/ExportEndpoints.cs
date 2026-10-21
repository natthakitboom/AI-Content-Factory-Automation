using ContentFactory.Core.Abstractions;
using ContentFactory.Infrastructure.Export;
using ContentFactory.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace ContentFactory.Api.Endpoints;

public sealed record CreateExportRequest(Guid ChannelId);

public sealed record RecordPublicationRequest(
    Guid ChannelId,
    string PostUrl,
    string? ExternalPostId,
    string? ProductLinkStatus,
    string? EvidenceReference);

/// <summary>
/// Export builds a ZIP for manual posting; recording a publication is a separate owner
/// action made after a real post. Neither endpoint talks to a platform.
/// </summary>
public static class ExportEndpoints
{
    public static IEndpointRouteBuilder MapExportEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var group = endpoints.MapGroup("/api/v1/videos/{videoVersionId:guid}").RequireAuthorization();

        group.MapPost("/exports", CreateAsync);
        group.MapGet("/exports", ListAsync);
        group.MapPost("/publications", RecordAsync);
        group.MapGet("/publications", ListPublicationsAsync);

        return endpoints;
    }

    private static async Task<IResult> CreateAsync(
        Guid videoVersionId, CreateExportRequest request, IExportService service, CancellationToken ct)
    {
        var package = await service.CreateAsync(videoVersionId, request.ChannelId, ct);
        return Results.Created(
            $"/api/v1/assets/{package.Id}/content",
            new
            {
                assetId = package.Id,
                sha256 = package.Sha256,
                bytes = package.Bytes,
                downloadUrl = $"/api/v1/assets/{package.Id}/content"
            });
    }

    private static async Task<IResult> ListAsync(
        Guid videoVersionId, AppDbContext db, CancellationToken ct)
    {
        var packages = await (
                from package in db.ExportPackages.AsNoTracking()
                join asset in db.Assets.AsNoTracking() on package.RelativePath equals asset.RelativePath
                where package.VideoVersionId == videoVersionId
                orderby package.CreatedAt descending
                select new
                {
                    package.Id,
                    package.ChannelId,
                    package.PackageHash,
                    package.CreatedAt,
                    assetId = asset.Id,
                    downloadUrl = "/api/v1/assets/" + asset.Id + "/content"
                })
            .ToListAsync(ct);
        return Results.Ok(packages);
    }

    private static async Task<IResult> RecordAsync(
        Guid videoVersionId, RecordPublicationRequest request, PublicationRecorder recorder, CancellationToken ct)
    {
        var publication = await recorder.RecordAsync(
            videoVersionId,
            request.ChannelId,
            request.PostUrl,
            request.ExternalPostId,
            request.ProductLinkStatus,
            request.EvidenceReference,
            ct);
        return Results.Ok(new
        {
            publication.Id,
            publication.PostUrl,
            publication.ProductLinkStatus,
            publication.PublishedAt
        });
    }

    private static async Task<IResult> ListPublicationsAsync(
        Guid videoVersionId, AppDbContext db, CancellationToken ct)
        => Results.Ok(await db.Publications.AsNoTracking()
            .Where(x => x.VideoVersionId == videoVersionId)
            .OrderByDescending(x => x.PublishedAt)
            .Select(x => new { x.Id, x.ChannelId, x.PostUrl, x.ProductLinkStatus, x.EvidenceReference, x.PublishedAt })
            .ToListAsync(ct));
}
