using System.Security.Cryptography;
using ContentFactory.Contracts;
using ContentFactory.Core.Abstractions;
using ContentFactory.Core.Exceptions;
using ContentFactory.Infrastructure.Persistence;
using ContentFactory.Infrastructure.Persistence.Entities;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace ContentFactory.Infrastructure.Storage;

/// <summary>
/// Streams an upload to a temp file while hashing it, then atomically renames the immutable
/// blob into DATA_ROOT/assets/sha256/&lt;first-2&gt;/&lt;hash&gt;.&lt;ext&gt;. The user filename is
/// never used as a path. Duplicate content maps to one row per (sha256, kind).
/// </summary>
public sealed class FileSystemAssetStore : IAssetStore
{
    private const int HeaderBytes = 512;
    private const int BufferBytes = 81920;

    private readonly AppDbContext _db;
    private readonly StorageOptions _options;
    private readonly TimeProvider _clock;

    public FileSystemAssetStore(AppDbContext db, StorageOptions options, TimeProvider clock)
    {
        _db = db;
        _options = options;
        _clock = clock;
    }

    public async Task<AssetRef> PutAsync(
        Stream input,
        AssetKind kind,
        string mime,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(input);

        AssetPathResolver.EnsureNoSymlinks(_options.DataRoot, "temp");
        var tempDirectory = Path.Combine(_options.TempRoot, "ingest");
        Directory.CreateDirectory(tempDirectory);
        AssetPathResolver.EnsureNoSymlinks(_options.DataRoot, "temp/ingest");

        var tempPath = Path.Combine(tempDirectory, Guid.NewGuid().ToString("N") + ".part");
        var header = new byte[HeaderBytes];
        var limit = _options.LimitFor(kind);
        long total = 0;
        string sha256;
        string sniffedMime;

        try
        {
            var headerCount = await ReadHeaderAsync(input, header, ct);
            var sniffed = AssetUploadPolicy.ValidateHeader(null, kind, mime, header.AsSpan(0, headerCount));
            sniffedMime = sniffed.Mime;

            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            await using (var file = new FileStream(
                tempPath,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None,
                BufferBytes,
                FileOptions.Asynchronous | FileOptions.SequentialScan))
            {
                SetOwnerOnly(tempPath);

                total += headerCount;
                AssetUploadPolicy.EnsureWithinLimit(total, kind, _options);
                hash.AppendData(header, 0, headerCount);
                await file.WriteAsync(header.AsMemory(0, headerCount), ct);

                var buffer = new byte[BufferBytes];
                while (true)
                {
                    var read = await input.ReadAsync(buffer, ct);
                    if (read == 0)
                    {
                        break;
                    }

                    total += read;
                    if (total > limit)
                    {
                        throw new ValidationException(
                            "ASSET_TOO_LARGE",
                            $"The uploaded {kind} is larger than the {limit} byte limit.");
                    }

                    hash.AppendData(buffer, 0, read);
                    await file.WriteAsync(buffer.AsMemory(0, read), ct);
                }

                await file.FlushAsync(ct);
                file.Flush(flushToDisk: true);
            }

            sha256 = Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant();
            MoveIntoPlace(tempPath, sha256, kind, sniffedMime, ct);
        }
        catch
        {
            TryDelete(tempPath);
            throw;
        }

        var existing = await _db.Assets
            .AsNoTracking()
            .SingleOrDefaultAsync(x => x.Sha256 == sha256 && x.Kind == kind, ct);

        if (existing is not null)
        {
            return ToRef(existing);
        }

        var asset = new Asset
        {
            Id = Guid.NewGuid(),
            Sha256 = sha256,
            RelativePath = AssetPathResolver.RelativePathFor(sha256, kind, sniffedMime),
            Kind = kind,
            Mime = sniffedMime,
            Bytes = total,
            RightsStatus = "unknown",
            SourceReference = string.Empty,
            CloudUseAllowed = false,
            CreatedAt = _clock.GetUtcNow()
        };

        _db.Assets.Add(asset);

        var transaction = _db.Database.CurrentTransaction;
        if (transaction is not null)
        {
            await transaction.CreateSavepointAsync("asset_insert", ct);
        }

        try
        {
            await _db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException exception) when (IsUniqueViolation(exception))
        {
            if (transaction is not null)
            {
                await transaction.RollbackToSavepointAsync("asset_insert", ct);
            }

            _db.Entry(asset).State = EntityState.Detached;
            var raced = await _db.Assets
                .AsNoTracking()
                .SingleOrDefaultAsync(x => x.Sha256 == sha256 && x.Kind == kind, ct);

            if (raced is null)
            {
                throw;
            }

            return ToRef(raced);
        }

        return ToRef(asset);
    }

    public async Task<Stream> OpenReadAsync(Guid assetId, CancellationToken ct)
    {
        var asset = await _db.Assets
            .AsNoTracking()
            .SingleOrDefaultAsync(x => x.Id == assetId, ct)
            ?? throw new NotFoundException("ASSET_NOT_FOUND", "Asset not found.");

        var fullPath = AssetPathResolver.ResolveExisting(_options.DataRoot, asset.RelativePath);
        if (!File.Exists(fullPath))
        {
            throw new NotFoundException("ASSET_CONTENT_MISSING", "Asset content is missing from storage.");
        }

        return new FileStream(
            fullPath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            BufferBytes,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
    }

    /// <summary>
    /// Deletes only stale files under DATA_ROOT/temp. Assets under DATA_ROOT/assets are
    /// never enumerated, so a referenced asset cannot be removed by cleanup.
    /// </summary>
    public Task<int> CleanupTempAsync(TimeSpan olderThan, CancellationToken ct)
    {
        if (olderThan < TimeSpan.Zero)
        {
            throw new ValidationException("ASSET_CLEANUP_INVALID", "Cleanup age cannot be negative.");
        }

        var dataRoot = Path.GetFullPath(_options.DataRoot);
        var tempRoot = Path.GetFullPath(_options.TempRoot);
        var prefix = dataRoot.EndsWith(Path.DirectorySeparatorChar)
            ? dataRoot
            : dataRoot + Path.DirectorySeparatorChar;

        if (!tempRoot.StartsWith(prefix, StringComparison.Ordinal))
        {
            throw new ValidationException("ASSET_PATH_UNSAFE", "Temp root escapes DATA_ROOT.");
        }

        if (!Directory.Exists(tempRoot))
        {
            return Task.FromResult(0);
        }

        AssetPathResolver.EnsureNoSymlinks(dataRoot, "temp");

        var cutoff = _clock.GetUtcNow() - olderThan;
        var removed = 0;
        var enumeration = new EnumerationOptions
        {
            RecurseSubdirectories = true,
            IgnoreInaccessible = true,
            AttributesToSkip = FileAttributes.ReparsePoint | FileAttributes.System
        };

        foreach (var file in Directory.EnumerateFiles(tempRoot, "*", enumeration))
        {
            ct.ThrowIfCancellationRequested();

            try
            {
                if (File.GetLastWriteTimeUtc(file) <= cutoff.UtcDateTime)
                {
                    File.Delete(file);
                    removed++;
                }
            }
            catch (IOException)
            {
                // A worker may still hold the temp file; the next cleanup can retry.
            }
            catch (UnauthorizedAccessException)
            {
                // Leave files we cannot inspect rather than deleting blindly.
            }
        }

        foreach (var directory in Directory
            .EnumerateDirectories(tempRoot, "*", enumeration)
            .OrderByDescending(path => path.Length))
        {
            try
            {
                if (!Directory.EnumerateFileSystemEntries(directory).Any())
                {
                    Directory.Delete(directory);
                }
            }
            catch (IOException)
            {
                // Non-empty or in use; safe to leave behind.
            }
            catch (UnauthorizedAccessException)
            {
                // Safe to leave behind.
            }
        }

        return Task.FromResult(removed);
    }

    private void MoveIntoPlace(
        string tempPath,
        string sha256,
        AssetKind kind,
        string mime,
        CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();

        var relativePath = AssetPathResolver.RelativePathFor(sha256, kind, mime);
        var finalPath = AssetPathResolver.ResolveExisting(_options.DataRoot, relativePath);
        var relativeDirectory = Path.GetDirectoryName(relativePath)?.Replace('\\', '/')
            ?? throw new ValidationException("ASSET_PATH_UNSAFE", "Asset path has no directory.");

        Directory.CreateDirectory(Path.GetDirectoryName(finalPath)!);
        AssetPathResolver.EnsureNoSymlinks(_options.DataRoot, relativeDirectory);

        if (File.Exists(finalPath))
        {
            // Content-addressed: identical bytes already exist, so keep the immutable copy.
            SetOwnerOnly(finalPath);
            TryDelete(tempPath);
            return;
        }

        try
        {
            File.Move(tempPath, finalPath, overwrite: false);
            SetOwnerOnly(finalPath);
        }
        catch (IOException) when (File.Exists(finalPath))
        {
            TryDelete(tempPath);
        }
    }

    private static async Task<int> ReadHeaderAsync(Stream input, byte[] header, CancellationToken ct)
    {
        var total = 0;
        while (total < header.Length)
        {
            var read = await input.ReadAsync(header.AsMemory(total, header.Length - total), ct);
            if (read == 0)
            {
                break;
            }

            total += read;
        }

        return total;
    }

    private static void SetOwnerOnly(string path)
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
    }

    private static bool IsUniqueViolation(DbUpdateException exception)
        => exception.InnerException is PostgresException
        {
            SqlState: PostgresErrorCodes.UniqueViolation
        };

    private static AssetRef ToRef(Asset asset)
        => new(
            asset.Id,
            asset.Sha256,
            asset.RelativePath,
            asset.Kind,
            asset.Mime,
            asset.Bytes,
            asset.DurationMs);

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (IOException)
        {
            // The temp cleaner will retry later.
        }
        catch (UnauthorizedAccessException)
        {
            // The temp cleaner will retry later.
        }
    }
}
