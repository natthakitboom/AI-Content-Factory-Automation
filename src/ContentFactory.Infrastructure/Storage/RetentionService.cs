using ContentFactory.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace ContentFactory.Infrastructure.Storage;

public sealed record CleanupReport(
    IReadOnlyList<Guid> DeletedAssetIds, int DeletedTempFiles, bool TempSkippedBecauseJobsRunning);

/// <summary>
/// Deletes only what nothing can still need: an asset row that is old enough and referenced
/// by no table, no stored manifest or snapshot, and no unfinished job, and stale temp files
/// when no job is running. A referenced or recent file is never touched.
/// </summary>
public sealed class RetentionService
{
    public static readonly TimeSpan DefaultAssetRetention = TimeSpan.FromDays(14);
    public static readonly TimeSpan TempRetention = TimeSpan.FromHours(24);

    private readonly AppDbContext _db;
    private readonly StorageOptions _storage;
    private readonly TimeProvider _clock;
    private readonly TimeSpan _assetRetention;

    public RetentionService(
        AppDbContext db, StorageOptions storage, TimeProvider clock, TimeSpan? assetRetention = null)
    {
        _db = db ?? throw new ArgumentNullException(nameof(db));
        _storage = storage ?? throw new ArgumentNullException(nameof(storage));
        _clock = clock ?? throw new ArgumentNullException(nameof(clock));
        _assetRetention = assetRetention ?? DefaultAssetRetention;
    }

    public async Task<CleanupReport> CleanupAsync(CancellationToken ct)
    {
        var now = _clock.GetUtcNow();
        var deleted = new List<Guid>();
        foreach (var orphan in await FindUnreferencedAsync(now - _assetRetention, ct))
        {
            var removed = await _db.Assets.Where(x => x.Id == orphan.Id).ExecuteDeleteAsync(ct);
            if (removed == 0)
            {
                continue;
            }

            deleted.Add(orphan.Id);
            if (!await _db.Assets.AnyAsync(x => x.RelativePath == orphan.RelativePath, ct))
            {
                TryDeleteFile(orphan.RelativePath);
            }
        }

        var running = await _db.Jobs.AnyAsync(x => x.Status == "running", ct);
        var tempDeleted = running ? 0 : DeleteStaleTemp(now);
        return new CleanupReport(deleted, tempDeleted, running);
    }

    private async Task<List<Orphan>> FindUnreferencedAsync(DateTimeOffset cutoff, CancellationToken ct)
        => await _db.Database.SqlQuery<Orphan>($"""
            SELECT a.id AS "Id", a.relative_path AS "RelativePath"
            FROM app.assets a
            WHERE a.created_at < {cutoff}
              AND NOT EXISTS (SELECT 1 FROM app.product_assets x WHERE x.asset_id = a.id)
              AND NOT EXISTS (SELECT 1 FROM app.scene_assets x WHERE x.asset_id = a.id)
              AND NOT EXISTS (SELECT 1 FROM app.video_versions v
                              WHERE v.output_asset_id = a.id OR v.thumbnail_asset_id = a.id OR v.subtitles_asset_id = a.id)
              AND NOT EXISTS (SELECT 1 FROM app.provider_requests p WHERE p.output_asset_id = a.id)
              AND NOT EXISTS (SELECT 1 FROM app.export_packages e WHERE e.relative_path = a.relative_path)
              AND NOT EXISTS (SELECT 1 FROM app.content_revisions r
                              WHERE r.product_snapshot_json::text LIKE '%' || a.id::text || '%'
                                 OR r.script_json::text LIKE '%' || a.id::text || '%')
              AND NOT EXISTS (SELECT 1 FROM app.video_versions v
                              WHERE v.manifest_json::text LIKE '%' || a.id::text || '%')
              AND NOT EXISTS (SELECT 1 FROM app.jobs j
                              WHERE j.status NOT IN ('succeeded','cancelled')
                                AND j.payload_json::text LIKE '%' || a.id::text || '%')
            """).ToListAsync(ct);

    private void TryDeleteFile(string relativePath)
    {
        try
        {
            var path = AssetPathResolver.ResolveExisting(_storage.DataRoot, relativePath);
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException
            or ContentFactory.Core.Exceptions.ValidationException)
        {
            // An unsafe or locked path is left in place; the row is already gone, so the
            // file is only an unreferenced leftover that a later run can reconsider.
        }
    }

    private int DeleteStaleTemp(DateTimeOffset now)
    {
        if (!Directory.Exists(_storage.TempRoot))
        {
            return 0;
        }

        var deleted = 0;
        foreach (var file in Directory.EnumerateFiles(_storage.TempRoot, "*", SearchOption.AllDirectories))
        {
            var info = new FileInfo(file);
            if (info.LinkTarget is not null || now - info.LastWriteTimeUtc < TempRetention)
            {
                continue;
            }

            try
            {
                info.Delete();
                deleted++;
            }
            catch (IOException)
            {
            }
        }

        return deleted;
    }

    private sealed class Orphan
    {
        public Guid Id { get; set; }
        public string RelativePath { get; set; } = string.Empty;
    }
}
