using System.Text.RegularExpressions;
using ContentFactory.Contracts;
using ContentFactory.Core.Exceptions;

namespace ContentFactory.Infrastructure.Storage;

/// <summary>
/// Derives every on-disk path from a SHA-256 hash and a canonical MIME extension.
/// A user filename is validated only as an upload label; it never reaches the filesystem.
/// </summary>
public static class AssetPathResolver
{
    private static readonly Regex Sha256Pattern = new("^[0-9a-f]{64}$", RegexOptions.Compiled);

    public static bool IsSafeUploadName(string? fileName)
    {
        if (string.IsNullOrWhiteSpace(fileName) || fileName.Length > 255)
        {
            return false;
        }

        if (fileName is "." or "..")
        {
            return false;
        }

        if (fileName.Contains("..", StringComparison.Ordinal)
            || fileName.Contains('/', StringComparison.Ordinal)
            || fileName.Contains('\\', StringComparison.Ordinal)
            || fileName.Contains(':', StringComparison.Ordinal)
            || fileName.Contains('\0', StringComparison.Ordinal)
            || fileName.Any(char.IsControl)
            || Path.IsPathRooted(fileName))
        {
            return false;
        }

        return true;
    }

    public static string RelativePathFor(string sha256, AssetKind kind, string mime)
    {
        if (!Sha256Pattern.IsMatch(sha256))
        {
            throw new ValidationException("ASSET_HASH_INVALID", "Asset hash must be 64 lowercase hex characters.");
        }

        var extension = ExtensionFor(kind, mime);
        return $"assets/sha256/{sha256[..2]}/{sha256}{extension}";
    }

    public static string ExtensionFor(AssetKind kind, string mime)
    {
        var normalized = mime.Trim().ToLowerInvariant();
        var extension = normalized switch
        {
            "image/jpeg" => ".jpg",
            "image/png" => ".png",
            "image/gif" => ".gif",
            "image/webp" => ".webp",
            "video/mp4" => ".mp4",
            "video/quicktime" => ".mov",
            "audio/mpeg" => ".mp3",
            "audio/wav" => ".wav",
            "audio/mp4" => ".m4a",
            "application/x-subrip" => ".srt",
            "text/vtt" => ".vtt",
            _ => null
        };

        if (extension is null)
        {
            throw new ValidationException("ASSET_MIME_UNSUPPORTED", $"Unsupported MIME type: {mime}");
        }

        if (kind == AssetKind.Subtitle && extension is not (".srt" or ".vtt"))
        {
            throw new ValidationException("ASSET_MIME_UNSUPPORTED", "Subtitle assets must be SRT or WebVTT.");
        }

        return extension;
    }

    /// <summary>
    /// Resolves a stored relative path under DATA_ROOT and rejects traversal or symlink
    /// escape in any path segment.
    /// </summary>
    public static string ResolveExisting(string dataRoot, string relativePath)
    {
        if (string.IsNullOrWhiteSpace(relativePath) || Path.IsPathRooted(relativePath))
        {
            throw new ValidationException("ASSET_PATH_UNSAFE", "Asset path must be relative.");
        }

        var normalizedRelative = relativePath.Replace('\\', '/');
        if (normalizedRelative.Split('/').Any(segment => segment is ".." or "." or ""))
        {
            throw new ValidationException("ASSET_PATH_UNSAFE", "Asset path contains unsafe segments.");
        }

        var root = Path.GetFullPath(dataRoot);
        var fullPath = Path.GetFullPath(Path.Combine(root, normalizedRelative));
        var rootPrefix = root.EndsWith(Path.DirectorySeparatorChar)
            ? root
            : root + Path.DirectorySeparatorChar;

        if (!fullPath.StartsWith(rootPrefix, StringComparison.Ordinal))
        {
            throw new ValidationException("ASSET_PATH_UNSAFE", "Asset path escapes DATA_ROOT.");
        }

        EnsureNoSymlinks(root, normalizedRelative);
        return fullPath;
    }

    public static void EnsureNoSymlinks(string dataRoot, string relativePath)
    {
        var current = Path.GetFullPath(dataRoot);
        foreach (var segment in relativePath.Replace('\\', '/').Split('/', StringSplitOptions.RemoveEmptyEntries))
        {
            current = Path.Combine(current, segment);

            if (new FileInfo(current).LinkTarget is not null
                || new DirectoryInfo(current).LinkTarget is not null)
            {
                throw new ValidationException("ASSET_PATH_UNSAFE", "Symlinks are not allowed in asset paths.");
            }
        }
    }
}
