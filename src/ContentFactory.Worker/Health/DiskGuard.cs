namespace ContentFactory.Worker.Health;

public sealed record DiskGuardOptions(long MinFreeBytes, double MinFreeRatio)
{
    public static DiskGuardOptions FromEnvironment()
    {
        var gib = double.TryParse(
            Environment.GetEnvironmentVariable("MIN_FREE_DISK_GIB"),
            System.Globalization.NumberStyles.Float,
            System.Globalization.CultureInfo.InvariantCulture,
            out var parsedGib) ? parsedGib : 20d;
        var ratio = double.TryParse(
            Environment.GetEnvironmentVariable("MIN_FREE_DISK_RATIO"),
            System.Globalization.NumberStyles.Float,
            System.Globalization.CultureInfo.InvariantCulture,
            out var parsedRatio) ? parsedRatio : 0.10d;
        return new DiskGuardOptions((long)(gib * 1024 * 1024 * 1024), ratio);
    }
}

public sealed record DiskSnapshot(long FreeBytes, long TotalBytes);

public sealed record DiskStatus(bool UnderPressure, long FreeBytes, double FreeRatio);

/// <summary>
/// New heavy local work stops when free space is below either the absolute or the relative
/// floor. It only reports; it never deletes anything.
/// </summary>
public sealed class DiskGuard
{
    private readonly DiskGuardOptions _options;
    private readonly Func<string, DiskSnapshot> _probe;

    public DiskGuard(DiskGuardOptions options, Func<string, DiskSnapshot>? probe = null)
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _probe = probe ?? Probe;
    }

    public DiskStatus Evaluate(string path)
    {
        var snapshot = _probe(path);
        var ratio = snapshot.TotalBytes <= 0 ? 0d : (double)snapshot.FreeBytes / snapshot.TotalBytes;
        return new DiskStatus(
            snapshot.FreeBytes < _options.MinFreeBytes || ratio < _options.MinFreeRatio,
            snapshot.FreeBytes,
            ratio);
    }

    private static DiskSnapshot Probe(string path)
    {
        var probePath = Directory.Exists(path) ? path : Path.GetDirectoryName(Path.GetFullPath(path)) ?? "/";
        var drive = new DriveInfo(Path.GetFullPath(probePath));
        return new DiskSnapshot(drive.AvailableFreeSpace, drive.TotalSize);
    }
}
