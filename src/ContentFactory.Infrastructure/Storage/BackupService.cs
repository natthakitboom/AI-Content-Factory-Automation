using System.Security.Cryptography;
using System.Text.Json;
using ContentFactory.Contracts;
using ContentFactory.Core.Exceptions;
using ContentFactory.Infrastructure.Jobs;
using ContentFactory.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace ContentFactory.Infrastructure.Storage;

public sealed record BackupAsset(Guid Id, string Sha256, string RelativePath, long Bytes);

public sealed record BackupCloudRequest(string? ProviderRequestId, string Provider, string Status);

public sealed record BackupManifest(
    int SchemaVersion,
    DateTimeOffset CreatedAt,
    string DatabaseName,
    string? LatestMigration,
    string DatabaseDumpSha256,
    IReadOnlyList<BackupAsset> Assets,
    IReadOnlyList<BackupCloudRequest> InFlightCloudRequests,
    string Note);

/// <summary>
/// Quiesces writes, dumps the database and copies every asset with its hash into a new
/// owner-only directory. Environment secrets are never read, so none can leak into the set.
/// Cloud work already submitted keeps running and billing; the manifest lists those requests
/// so a later restore can be reconciled against the provider's own history.
/// </summary>
public sealed class BackupService
{
    public const int SchemaVersion = 1;
    public const string CloudNote =
        "This backup does not stop cloud requests that were already submitted; they can still finish and bill. "
        + "After a restore, reconcile the ledger with the provider history for any call made after the backup time.";

    private static readonly TimeSpan DrainTimeout = TimeSpan.FromMinutes(5);

    private readonly AppDbContext _db;
    private readonly StorageOptions _storage;
    private readonly IDatabaseTool _tool;
    private readonly SystemFlagStore _flags;
    private readonly PostgresJobStore _jobs;
    private readonly TimeProvider _clock;

    public BackupService(
        AppDbContext db,
        StorageOptions storage,
        IDatabaseTool tool,
        SystemFlagStore flags,
        PostgresJobStore jobs,
        TimeProvider clock)
    {
        _db = db ?? throw new ArgumentNullException(nameof(db));
        _storage = storage ?? throw new ArgumentNullException(nameof(storage));
        _tool = tool ?? throw new ArgumentNullException(nameof(tool));
        _flags = flags ?? throw new ArgumentNullException(nameof(flags));
        _jobs = jobs ?? throw new ArgumentNullException(nameof(jobs));
        _clock = clock ?? throw new ArgumentNullException(nameof(clock));
    }

    public async Task<BackupManifest> CreateAsync(string outputDirectory, string databaseName, CancellationToken ct)
    {
        var target = Path.GetFullPath(outputDirectory);
        if (Directory.Exists(target) && Directory.EnumerateFileSystemEntries(target).Any())
        {
            throw new ConflictException("BACKUP_TARGET_NOT_EMPTY", "The backup directory must be new or empty.");
        }

        if (Path.GetFullPath(target).StartsWith(Path.GetFullPath(_storage.DataRoot) + Path.DirectorySeparatorChar,
                StringComparison.Ordinal))
        {
            throw new ValidationException("BACKUP_TARGET_INSIDE_DATA_ROOT", "Back up to a directory outside DATA_ROOT.");
        }

        CreateOwnerOnlyDirectory(target);

        var pausedBefore = await _db.Jobs.AnyAsync(x => x.Status == "paused", ct);
        await _flags.SetAsync(SystemFlagStore.WriteQuiesce, "true");
        try
        {
            if (!pausedBefore)
            {
                await _jobs.PauseAsync(ct);
            }

            await DrainAsync(ct);

            var dumpPath = Path.Combine(target, "database.dump");
            await _tool.DumpAsync(databaseName, dumpPath, ct);
            var dumpHash = await HashFileAsync(dumpPath, ct);

            var assets = new List<BackupAsset>();
            foreach (var asset in await _db.Assets.AsNoTracking().OrderBy(x => x.RelativePath).ToListAsync(ct))
            {
                assets.Add(await CopyAssetAsync(target, asset.Id, asset.Sha256, asset.RelativePath, ct));
            }

            var inFlight = await _db.ProviderRequests.AsNoTracking()
                .Where(x => x.Status != "succeeded" && x.Status != "failed_confirmed" && x.Status != "cancelled_confirmed")
                .Select(x => new BackupCloudRequest(x.ProviderRequestId, x.Provider, x.Status))
                .ToListAsync(ct);
            var migrations = (await _db.Database.GetAppliedMigrationsAsync(ct)).ToList();
            var manifest = new BackupManifest(
                SchemaVersion, _clock.GetUtcNow(), databaseName, migrations.LastOrDefault(),
                dumpHash, assets, inFlight, CloudNote);
            var manifestPath = Path.Combine(target, "manifest.json");
            await File.WriteAllTextAsync(
                manifestPath, JsonSerializer.Serialize(manifest, JsonDefaults.Options), ct);
            OwnerOnlyPermissions.File(manifestPath);
            return manifest;
        }
        finally
        {
            if (!pausedBefore)
            {
                await _jobs.ResumeAsync(CancellationToken.None);
            }

            await _flags.SetAsync(SystemFlagStore.WriteQuiesce, null);
        }
    }

    private async Task DrainAsync(CancellationToken ct)
    {
        var deadline = _clock.GetUtcNow() + DrainTimeout;
        while (await _db.Jobs.AsNoTracking().AnyAsync(
                   x => x.Status == "running" && x.Lane != JobLane.CloudPoll && x.Lane != JobLane.CloudSubmit, ct))
        {
            if (_clock.GetUtcNow() > deadline)
            {
                throw new ConflictException(
                    "BACKUP_DRAIN_TIMEOUT", "Local jobs did not finish in time; try the backup again later.");
            }

            await Task.Delay(TimeSpan.FromSeconds(1), ct);
        }
    }

    private async Task<BackupAsset> CopyAssetAsync(
        string target, Guid id, string sha256, string relativePath, CancellationToken ct)
    {
        var source = AssetPathResolver.ResolveExisting(_storage.DataRoot, relativePath);
        if (!File.Exists(source))
        {
            throw new ValidationException("BACKUP_ASSET_MISSING", $"The asset file for {id} is missing.");
        }

        var destination = Path.Combine(target, "assets", relativePath);
        CreateOwnerOnlyDirectory(Path.GetDirectoryName(destination)!);
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        long bytes = 0;
        await using (var input = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.Read))
        await using (var output = new FileStream(destination, FileMode.CreateNew, FileAccess.Write, FileShare.None))
        {
            var buffer = new byte[81920];
            int read;
            while ((read = await input.ReadAsync(buffer, ct)) > 0)
            {
                hash.AppendData(buffer, 0, read);
                await output.WriteAsync(buffer.AsMemory(0, read), ct);
                bytes += read;
            }
        }

        OwnerOnlyPermissions.File(destination);
        var actual = Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant();
        if (!string.Equals(actual, sha256, StringComparison.Ordinal))
        {
            throw new ValidationException(
                "BACKUP_HASH_MISMATCH", $"The stored file for asset {id} does not match its recorded hash.");
        }

        return new BackupAsset(id, sha256, relativePath, bytes);
    }

    internal static async Task<string> HashFileAsync(string path, CancellationToken ct)
    {
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        return Convert.ToHexString(await SHA256.HashDataAsync(stream, ct)).ToLowerInvariant();
    }

    internal static void CreateOwnerOnlyDirectory(string path)
    {
        var missing = new Stack<string>();
        for (var current = path; !Directory.Exists(current); current = Path.GetDirectoryName(current)!)
        {
            missing.Push(current);
        }

        while (missing.Count > 0)
        {
            var directory = missing.Pop();
            Directory.CreateDirectory(directory);
            OwnerOnlyPermissions.Directory(directory);
        }
    }
}
