using ContentFactory.Api.Middleware;
using ContentFactory.Contracts;
using ContentFactory.Core.Abstractions;
using ContentFactory.Core.Exceptions;
using ContentFactory.Infrastructure.Persistence;
using ContentFactory.Infrastructure.Storage;
using Microsoft.EntityFrameworkCore;

namespace ContentFactory.Api.Endpoints;

public static class AssetEndpoints
{
    private const long CopyBufferBytes = 81920;

    public static IEndpointRouteBuilder MapAssetEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var group = endpoints.MapGroup("/api/v1");

        group.MapPost("/products/{productId:guid}/assets", UploadProductAssetAsync)
            .RequireAuthorization();
        group.MapGet("/assets/{assetId:guid}/content", DownloadAsync)
            .RequireAuthorization();

        return endpoints;
    }

    private static async Task<IResult> UploadProductAssetAsync(
        Guid productId,
        HttpRequest request,
        AssetIngestService ingest,
        CancellationToken ct)
    {
        if (!request.HasFormContentType)
        {
            return ApiProblem.Create(
                StatusCodes.Status415UnsupportedMediaType,
                "ASSET_UPLOAD_NOT_MULTIPART",
                "Uploads must use multipart/form-data.");
        }

        var form = await request.ReadFormAsync(ct);
        var file = form.Files.GetFile("file");
        if (file is null || file.Length == 0)
        {
            return ApiProblem.Create(
                StatusCodes.Status400BadRequest,
                "ASSET_FILE_REQUIRED",
                "A non-empty 'file' field is required.");
        }

        if (!TryParseKind(form["kind"].ToString(), out var kind))
        {
            return ApiProblem.Create(
                StatusCodes.Status400BadRequest,
                "ASSET_KIND_INVALID",
                "kind must be image, thumbnail, audio, video or subtitle.");
        }

        var cloudUseAllowed = ParseBoolean(form["cloudUseAllowed"].ToString());

        await using var stream = file.OpenReadStream();
        var asset = await ingest.IngestAsync(
            new AssetIngestRequest(
                stream,
                file.FileName,
                kind,
                file.ContentType,
                form["rightsStatus"].ToString(),
                form["sourceReference"].ToString(),
                cloudUseAllowed,
                productId,
                RequireProductionRights: true),
            ct);

        return Results.Created($"/api/v1/assets/{asset.Id}/content", asset);
    }

    private static async Task<IResult> DownloadAsync(
        Guid assetId,
        HttpContext http,
        IAssetStore store,
        AppDbContext db,
        CancellationToken ct)
    {
        var asset = await db.Assets
            .AsNoTracking()
            .SingleOrDefaultAsync(x => x.Id == assetId, ct)
            ?? throw new NotFoundException("ASSET_NOT_FOUND", "Asset not found.");

        await using var stream = await store.OpenReadAsync(assetId, ct);
        if (!stream.CanSeek)
        {
            throw new ValidationException(
                "ASSET_CONTENT_UNSEEKABLE",
                "Stored asset content cannot be served with range requests.");
        }

        var length = stream.Length;
        var etag = $"\"{asset.Sha256}\"";

        http.Response.ContentType = asset.Mime;
        http.Response.Headers["Accept-Ranges"] = "bytes";
        http.Response.Headers["ETag"] = etag;
        http.Response.Headers["Cache-Control"] = "private, no-cache";
        http.Response.Headers["X-Content-Type-Options"] = "nosniff";
        if (asset.Kind == ContentFactory.Contracts.AssetKind.Archive)
        {
            http.Response.Headers.ContentDisposition = $"attachment; filename=\"export-{asset.Sha256[..12]}.zip\"";
        }

        if (MatchesETag(http.Request.Headers.IfNoneMatch.ToString(), etag))
        {
            http.Response.StatusCode = StatusCodes.Status304NotModified;
            return Results.Empty;
        }

        var parsed = ParseRange(http.Request.Headers.Range.ToString(), length);
        if (parsed.Unsatisfiable)
        {
            http.Response.StatusCode = StatusCodes.Status416RangeNotSatisfiable;
            http.Response.Headers["Content-Range"] = $"bytes */{length}";
            return Results.Empty;
        }

        if (!parsed.IsRange)
        {
            http.Response.StatusCode = StatusCodes.Status200OK;
            http.Response.ContentLength = length;
            await stream.CopyToAsync(http.Response.Body, (int)CopyBufferBytes, ct);
            return Results.Empty;
        }

        http.Response.StatusCode = StatusCodes.Status206PartialContent;
        http.Response.ContentLength = parsed.End - parsed.Start + 1;
        http.Response.Headers["Content-Range"] = $"bytes {parsed.Start}-{parsed.End}/{length}";
        await CopyRangeAsync(stream, http.Response.Body, parsed.Start, parsed.End - parsed.Start + 1, ct);
        return Results.Empty;
    }

    private static async Task CopyRangeAsync(
        Stream source,
        Stream destination,
        long start,
        long count,
        CancellationToken ct)
    {
        source.Seek(start, SeekOrigin.Begin);

        var buffer = new byte[CopyBufferBytes];
        var remaining = count;
        while (remaining > 0)
        {
            var read = await source.ReadAsync(
                buffer.AsMemory(0, (int)Math.Min(buffer.Length, remaining)),
                ct);

            if (read == 0)
            {
                throw new IOException("Asset content ended before the requested range.");
            }

            await destination.WriteAsync(buffer.AsMemory(0, read), ct);
            remaining -= read;
        }
    }

    private static bool TryParseKind(string value, out AssetKind kind)
    {
        if (Enum.TryParse(value, ignoreCase: true, out kind)
            && kind is AssetKind.Image
                or AssetKind.Thumbnail
                or AssetKind.Audio
                or AssetKind.Video
                or AssetKind.Subtitle)
        {
            return true;
        }

        kind = default;
        return false;
    }

    private static bool ParseBoolean(string value)
        => bool.TryParse(value, out var parsed)
            ? parsed
            : value switch
            {
                "1" => true,
                "0" => false,
                _ => false
            };

    private static bool MatchesETag(string ifNoneMatch, string etag)
    {
        if (string.IsNullOrWhiteSpace(ifNoneMatch))
        {
            return false;
        }

        if (ifNoneMatch.Trim() == "*")
        {
            return true;
        }

        return ifNoneMatch
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Any(candidate => string.Equals(candidate, etag, StringComparison.Ordinal));
    }

    private static RangeResult ParseRange(string header, long length)
    {
        if (string.IsNullOrWhiteSpace(header))
        {
            return RangeResult.None;
        }

        if (!header.StartsWith("bytes=", StringComparison.OrdinalIgnoreCase)
            || header.Contains(',')
            || length <= 0)
        {
            return RangeResult.Invalid;
        }

        var value = header["bytes=".Length..].Trim();
        var separator = value.IndexOf('-');
        if (separator < 0)
        {
            return RangeResult.Invalid;
        }

        var startText = value[..separator].Trim();
        var endText = value[(separator + 1)..].Trim();

        if (startText.Length == 0)
        {
            if (!long.TryParse(endText, out var suffix) || suffix <= 0)
            {
                return RangeResult.Invalid;
            }

            var start = suffix >= length ? 0 : length - suffix;
            return new RangeResult(true, start, length - 1, false);
        }

        if (!long.TryParse(startText, out var requestedStart)
            || requestedStart < 0
            || requestedStart >= length)
        {
            return RangeResult.Invalid;
        }

        if (endText.Length == 0)
        {
            return new RangeResult(true, requestedStart, length - 1, false);
        }

        if (!long.TryParse(endText, out var requestedEnd) || requestedEnd < requestedStart)
        {
            return RangeResult.Invalid;
        }

        var end = Math.Min(requestedEnd, length - 1);
        return new RangeResult(true, requestedStart, end, false);
    }

    private readonly record struct RangeResult(bool IsRange, long Start, long End, bool Unsatisfiable)
    {
        public static RangeResult None => new(false, 0, 0, false);
        public static RangeResult Invalid => new(false, 0, 0, true);
    }
}
