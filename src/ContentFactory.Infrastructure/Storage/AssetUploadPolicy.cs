using ContentFactory.Contracts;
using ContentFactory.Core.Exceptions;

namespace ContentFactory.Infrastructure.Storage;

/// <summary>
/// Pure upload-boundary rules shared by the filesystem store and its tests. The claimed
/// MIME and extension never decide the stored type; magic bytes do.
/// </summary>
public static class AssetUploadPolicy
{
    public const string OctetStream = "application/octet-stream";

    public static MimeSniffer.SniffResult ValidateHeader(
        string? fileName,
        AssetKind requestedKind,
        string? claimedMime,
        ReadOnlySpan<byte> header)
    {
        if (fileName is not null && !AssetPathResolver.IsSafeUploadName(fileName))
        {
            throw new ValidationException(
                "ASSET_FILENAME_UNSAFE",
                "Upload filename contains a path separator, traversal or control character.");
        }

        if (header.IsEmpty)
        {
            throw new ValidationException("ASSET_EMPTY", "The uploaded file is empty.");
        }

        var sniffed = MimeSniffer.Sniff(header)
            ?? throw new ValidationException(
                "ASSET_MIME_UNSUPPORTED",
                "The file type is not supported. SVG, HTML and executables are rejected.");

        if (!KindMatches(requestedKind, sniffed.Kind))
        {
            throw new ValidationException(
                "ASSET_KIND_MISMATCH",
                $"The uploaded content is {sniffed.Kind} but the request declared {requestedKind}.");
        }

        var normalizedClaim = NormalizeMime(claimedMime);
        if (normalizedClaim.Length > 0
            && !string.Equals(normalizedClaim, OctetStream, StringComparison.Ordinal)
            && !string.Equals(normalizedClaim, sniffed.Mime, StringComparison.Ordinal))
        {
            throw new ValidationException(
                "ASSET_MIME_MISMATCH",
                $"The uploaded content is {sniffed.Mime}, not {normalizedClaim}.");
        }

        return sniffed;
    }

    public static void EnsureWithinLimit(long bytes, AssetKind kind, StorageOptions options)
    {
        if (bytes < 0)
        {
            throw new ValidationException("ASSET_SIZE_INVALID", "The uploaded size is invalid.");
        }

        var limit = options.LimitFor(kind);
        if (bytes > limit)
        {
            throw new ValidationException(
                "ASSET_TOO_LARGE",
                $"The uploaded {kind} is larger than the {limit} byte limit.");
        }
    }

    public static string NormalizeMime(string? mime)
    {
        if (string.IsNullOrWhiteSpace(mime))
        {
            return string.Empty;
        }

        var separator = mime.IndexOf(';');
        var value = separator >= 0 ? mime[..separator] : mime;
        return value.Trim().ToLowerInvariant();
    }

    private static bool KindMatches(AssetKind requested, AssetKind sniffed) => requested switch
    {
        AssetKind.Image => sniffed == AssetKind.Image,
        AssetKind.Thumbnail => sniffed == AssetKind.Image,
        AssetKind.Audio => sniffed == AssetKind.Audio,
        AssetKind.Video => sniffed == AssetKind.Video,
        AssetKind.Subtitle => sniffed == AssetKind.Subtitle,
        AssetKind.Archive => sniffed == AssetKind.Archive,
        _ => false
    };
}
