using System.Text.Json;
using System.Text.RegularExpressions;
using ContentFactory.Contracts;
using ContentFactory.Core.Exceptions;
using Npgsql;

namespace ContentFactory.Infrastructure.Storage;

public sealed record RestoreReport(
    string TargetDatabase,
    string TargetDataRoot,
    int AssetCount,
    bool AllAssetHashesMatch,
    DateTimeOffset BackupCreatedAt,
    IReadOnlyList<string> Warnings);

/// <summary>
/// Restores into a database and data root that do not exist yet, only after the operator
/// types the confirmation and every hash in the backup verifies. The restored system starts
/// with paid lanes paused and all unfinished jobs paused, because the backup may be older than
/// a request that was really sent to the provider; nothing is replayed automatically.
/// </summary>
public sealed partial class RestoreService
{
    private readonly IDatabaseTool _tool;
    private readonly string _adminConnectionString;
    private readonly TimeProvider _clock;

    public RestoreService(IDatabaseTool tool, string adminConnectionString, TimeProvider clock)
    {
        _tool = tool ?? throw new ArgumentNullException(nameof(tool));
        _adminConnectionString = adminConnectionString ?? throw new ArgumentNullException(nameof(adminConnectionString));
        _clock = clock ?? throw new ArgumentNullException(nameof(clock));
    }

    public async Task<BackupManifest> VerifyAsync(string backupDirectory, CancellationToken ct)
    {
        var directory = Path.GetFullPath(backupDirectory);
        var manifestPath = Path.Combine(directory, "manifest.json");
        if (!File.Exists(manifestPath))
        {
            throw new ValidationException("BACKUP_MANIFEST_MISSING", "The backup has no manifest.json.");
        }

        var manifest = JsonSerializer.Deserialize<BackupManifest>(
                await File.ReadAllTextAsync(manifestPath, ct), JsonDefaults.Options)
            ?? throw new ValidationException("BACKUP_MANIFEST_INVALID", "The backup manifest is unreadable.");
        if (manifest.SchemaVersion != BackupService.SchemaVersion)
        {
            throw new ValidationException("BACKUP_SCHEMA_UNSUPPORTED", "This backup was made by an unsupported version.");
        }

        var dumpPath = Path.Combine(directory, "database.dump");
        if (!File.Exists(dumpPath)
            || !string.Equals(await BackupService.HashFileAsync(dumpPath, ct), manifest.DatabaseDumpSha256, StringComparison.Ordinal))
        {
            throw new ValidationException("BACKUP_HASH_MISMATCH", "The database dump does not match its recorded hash.");
        }

        foreach (var asset in manifest.Assets)
        {
            var path = AssetPathResolver.ResolveExisting(Path.Combine(directory, "assets"), asset.RelativePath);
            if (!File.Exists(path))
            {
                throw new ValidationException("BACKUP_ASSET_MISSING", $"The backup is missing the file for asset {asset.Id}.");
            }

            if (!string.Equals(await BackupService.HashFileAsync(path, ct), asset.Sha256, StringComparison.Ordinal))
            {
                throw new ValidationException("BACKUP_HASH_MISMATCH", $"Asset {asset.Id} does not match its recorded hash.");
            }
        }

        return manifest;
    }

    public async Task<RestoreReport> RestoreAsync(
        string backupDirectory,
        string targetDatabase,
        string targetDataRoot,
        string confirmation,
        CancellationToken ct)
    {
        if (!TargetName().IsMatch(targetDatabase))
        {
            throw new ValidationException(
                "RESTORE_DATABASE_NAME_INVALID", "Use lowercase letters, digits and underscores for the new database.");
        }

        if (!string.Equals(confirmation, $"restore {targetDatabase}", StringComparison.Ordinal))
        {
            throw new ValidationException(
                "RESTORE_CONFIRMATION_REQUIRED", $"Type exactly 'restore {targetDatabase}' to confirm.");
        }

        var manifest = await VerifyAsync(backupDirectory, ct);

        if (await DatabaseExistsAsync(targetDatabase, ct))
        {
            throw new ConflictException(
                "RESTORE_DATABASE_EXISTS", "Restore needs a new database name; it never overwrites a database.");
        }

        var root = Path.GetFullPath(targetDataRoot);
        if (Directory.Exists(root) && Directory.EnumerateFileSystemEntries(root).Any())
        {
            throw new ConflictException(
                "RESTORE_DATA_ROOT_NOT_EMPTY", "Restore needs a new or empty data root; it never overwrites files.");
        }

        var createdRoot = !Directory.Exists(root);
        var createdDatabase = false;
        try
        {
            await ExecuteAdminAsync($"CREATE DATABASE \"{targetDatabase}\" TEMPLATE template0 ENCODING 'UTF8'", ct);
            createdDatabase = true;
            await _tool.RestoreAsync(targetDatabase, Path.Combine(Path.GetFullPath(backupDirectory), "database.dump"), ct);

            BackupService.CreateOwnerOnlyDirectory(root);
            foreach (var asset in manifest.Assets)
            {
                var source = AssetPathResolver.ResolveExisting(
                    Path.Combine(Path.GetFullPath(backupDirectory), "assets"), asset.RelativePath);
                var destination = Path.GetFullPath(Path.Combine(root, asset.RelativePath));
                BackupService.CreateOwnerOnlyDirectory(Path.GetDirectoryName(destination)!);
                File.Copy(source, destination);
                OwnerOnlyPermissions.File(destination);
                if (!string.Equals(await BackupService.HashFileAsync(destination, ct), asset.Sha256, StringComparison.Ordinal))
                {
                    throw new ValidationException(
                        "BACKUP_HASH_MISMATCH", $"Asset {asset.Id} changed while it was copied.");
                }
            }

            await StartPausedAsync(targetDatabase, ct);
            return new RestoreReport(
                targetDatabase, root, manifest.Assets.Count, true, manifest.CreatedAt,
                [
                    "Paid lanes are paused and unfinished jobs are paused. Compare the cost ledger with the provider history for any call made after " + manifest.CreatedAt.ToString("O", System.Globalization.CultureInfo.InvariantCulture) + " before resuming.",
                    BackupService.CloudNote
                ]);
        }
        catch
        {
            if (createdDatabase)
            {
                await TryDropAsync(targetDatabase);
            }

            if (createdRoot && Directory.Exists(root))
            {
                try
                {
                    Directory.Delete(root, recursive: true);
                }
                catch (IOException)
                {
                }
            }

            throw;
        }
    }

    private async Task StartPausedAsync(string database, CancellationToken ct)
    {
        var builder = new NpgsqlConnectionStringBuilder(_adminConnectionString) { Database = database, Pooling = false };
        await using var connection = new NpgsqlConnection(builder.ConnectionString);
        await connection.OpenAsync(ct);
        var now = _clock.GetUtcNow();
        await using var command = connection.CreateCommand();
        command.CommandText = """
            DELETE FROM app.system_flags WHERE key = 'write_quiesce';
            INSERT INTO app.system_flags (key, value, updated_at) VALUES ('paid_lanes_paused', 'true', @now)
              ON CONFLICT (key) DO UPDATE SET value = 'true', updated_at = @now;
            INSERT INTO app.system_flags (key, value, updated_at) VALUES ('restored_from_backup_at', @iso, @now)
              ON CONFLICT (key) DO UPDATE SET value = @iso, updated_at = @now;
            UPDATE app.jobs SET status = 'paused', lease_owner = NULL, lease_until = NULL, updated_at = @now
              WHERE status IN ('queued','retry_wait','running');
            """;
        command.Parameters.AddWithValue("now", now);
        command.Parameters.AddWithValue("iso", now.ToString("O", System.Globalization.CultureInfo.InvariantCulture));
        await command.ExecuteNonQueryAsync(ct);
    }

    private async Task<bool> DatabaseExistsAsync(string name, CancellationToken ct)
    {
        await using var connection = new NpgsqlConnection(_adminConnectionString);
        await connection.OpenAsync(ct);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT 1 FROM pg_database WHERE datname = @name";
        command.Parameters.AddWithValue("name", name);
        return await command.ExecuteScalarAsync(ct) is not null;
    }

    private async Task ExecuteAdminAsync(string sql, CancellationToken ct)
    {
        await using var connection = new NpgsqlConnection(_adminConnectionString);
        await connection.OpenAsync(ct);
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        await command.ExecuteNonQueryAsync(ct);
    }

    private async Task TryDropAsync(string database)
    {
        try
        {
            NpgsqlConnection.ClearAllPools();
            await ExecuteAdminAsync($"DROP DATABASE IF EXISTS \"{database}\" WITH (FORCE)", CancellationToken.None);
        }
        catch (Exception exception) when (exception is NpgsqlException or InvalidOperationException)
        {
            // The caller still gets the original failure; a leftover scratch database is visible.
        }
    }

    [GeneratedRegex("^[a-z0-9_]{1,63}$")]
    private static partial Regex TargetName();
}
