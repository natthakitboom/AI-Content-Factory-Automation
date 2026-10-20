namespace ContentFactory.Infrastructure.Storage;

/// <summary>
/// Local storage caps and the DATA_ROOT resolver. DATA_ROOT never defaults inside the
/// repository, and a leading tilde is expanded explicitly rather than by the .NET host.
/// </summary>
public sealed class StorageOptions
{
    public const long MaxImageBytes = 10L * 1024 * 1024;
    public const long MaxAudioBytes = 50L * 1024 * 1024;
    public const long MaxVideoBytes = 200L * 1024 * 1024;

    public string DataRoot { get; init; } = string.Empty;
    public long ImageBytesLimit { get; init; } = MaxImageBytes;
    public long AudioBytesLimit { get; init; } = MaxAudioBytes;
    public long VideoBytesLimit { get; init; } = MaxVideoBytes;

    public string AssetsRoot => Path.Combine(DataRoot, "assets", "sha256");
    public string TempRoot => Path.Combine(DataRoot, "temp");

    public static StorageOptions Create(string? dataRoot = null)
    {
        var resolved = string.IsNullOrWhiteSpace(dataRoot)
            ? Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                "Library",
                "Application Support",
                "ContentFactory")
            : ExpandHome(dataRoot);

        return new StorageOptions { DataRoot = Path.GetFullPath(resolved) };
    }

    public long LimitFor(ContentFactory.Contracts.AssetKind kind) => kind switch
    {
        ContentFactory.Contracts.AssetKind.Image or ContentFactory.Contracts.AssetKind.Thumbnail => ImageBytesLimit,
        ContentFactory.Contracts.AssetKind.Audio or ContentFactory.Contracts.AssetKind.Subtitle => AudioBytesLimit,
        ContentFactory.Contracts.AssetKind.Video => VideoBytesLimit,
        _ => ImageBytesLimit
    };

    private static string ExpandHome(string path)
    {
        if (string.Equals(path, "~", StringComparison.Ordinal))
        {
            return Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        }

        if (path.StartsWith("~/", StringComparison.Ordinal))
        {
            return Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                path[2..]);
        }

        return path;
    }
}
