#pragma warning disable CA1416 // These tests assert Unix owner-only modes and run on macOS/Linux only.
using System.Security.Cryptography;
using System.Text.Json;
using ContentFactory.Contracts;
using ContentFactory.Core.Abstractions;
using ContentFactory.Core.Exceptions;
using ContentFactory.Infrastructure.Jobs;
using ContentFactory.Infrastructure.Persistence;
using ContentFactory.Infrastructure.Persistence.Entities;
using ContentFactory.Infrastructure.Storage;
using ContentFactory.IntegrationTests.Infrastructure;
using ContentFactory.Worker.Handlers;
using ContentFactory.Worker.Health;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace ContentFactory.IntegrationTests;

[CollectionDefinition(Name)]
public sealed class BackupCollection : ICollectionFixture<PostgresSchemaFixture>
{
    public const string Name = "postgres-backup";
}

/// <summary>
/// T20: cleanup never touches anything referenced or in use, a backup is verified by hash,
/// and a restore goes to a new database and new data root with every paid lane paused.
/// </summary>
[Collection(BackupCollection.Name)]
public sealed class BackupRestoreTests : IAsyncLifetime
{
    private readonly PostgresSchemaFixture _fixture;
    private readonly string _root = Path.Combine(
        Path.GetTempPath(), "contentfactory-backup-tests", Guid.NewGuid().ToString("N"));
    private readonly List<string> _restoredDatabases = [];

    public BackupRestoreTests(PostgresSchemaFixture fixture)
    {
        _fixture = fixture;
    }

    public Task InitializeAsync() => Task.CompletedTask;

    public async Task DisposeAsync()
    {
        NpgsqlConnection.ClearAllPools();
        var admin = Environment.GetEnvironmentVariable("CF_TEST_POSTGRES");
        foreach (var name in _restoredDatabases)
        {
            try
            {
                await using var connection = new NpgsqlConnection(admin);
                await connection.OpenAsync();
                await using var command = connection.CreateCommand();
                command.CommandText = $"DROP DATABASE IF EXISTS \"{name}\" WITH (FORCE)";
                await command.ExecuteNonQueryAsync();
            }
            catch (Exception)
            {
                // Best-effort cleanup of a scratch database.
            }
        }

        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    [Fact]
    public async Task DiskPressureBlocksNewRender()
    {
        await using var db = _fixture.CreateContext();
        var renderer = new CountingRenderer();
        var guard = new DiskGuard(
            new DiskGuardOptions(MinFreeBytes: 20L * 1024 * 1024 * 1024, MinFreeRatio: 0.10),
            _ => new DiskSnapshot(FreeBytes: 5L * 1024 * 1024 * 1024, TotalBytes: 500L * 1024 * 1024 * 1024));
        var handler = new RenderVideoHandler(db, renderer, guard, _root);

        var outcome = await handler.ExecuteAsync(RenderJob(), default);

        Assert.Equal("blocked", outcome.Status);
        Assert.Equal("DISK_PRESSURE", outcome.ErrorCode);
        Assert.Equal(0, renderer.Calls);

        var healthy = new DiskGuard(
            new DiskGuardOptions(1024, 0.10),
            _ => new DiskSnapshot(400L * 1024 * 1024 * 1024, 500L * 1024 * 1024 * 1024));
        Assert.False(healthy.Evaluate(_root).UnderPressure);
        var ratio = new DiskGuard(
            new DiskGuardOptions(1024, 0.10),
            _ => new DiskSnapshot(40L * 1024 * 1024 * 1024, 500L * 1024 * 1024 * 1024));
        Assert.True(ratio.Evaluate(_root).UnderPressure);
    }

    [Fact]
    public async Task ReferencedAssetNeverDeleted()
    {
        await using var db = _fixture.CreateContext();
        var (service, storage) = Retention(db, "ref");
        var old = DateTimeOffset.UtcNow.AddDays(-90);

        var orphan = await AddAssetFileAsync(db, storage, old);
        var viaScene = await AddAssetFileAsync(db, storage, old);
        var viaSnapshot = await AddAssetFileAsync(db, storage, old);
        var viaManifest = await AddAssetFileAsync(db, storage, old);
        var viaQueuedJob = await AddAssetFileAsync(db, storage, old);
        var recentOrphan = await AddAssetFileAsync(db, storage, DateTimeOffset.UtcNow);

        var seeded = await ReviewSeed.CreateAsync(db);
        db.SceneAssets.Add(new SceneAsset
        {
            Id = Guid.NewGuid(), ContentRevisionId = seeded.RevisionId, SceneId = "extra",
            Role = "visual", AssetId = viaScene.Id
        });
        await db.Database.ExecuteSqlInterpolatedAsync(
            $"UPDATE app.content_revisions SET product_snapshot_json = jsonb_set(product_snapshot_json, ARRAY['note'], to_jsonb({viaSnapshot.Id.ToString()}::text)) WHERE id = {seeded.RevisionId}");
        await db.Database.ExecuteSqlInterpolatedAsync(
            $"UPDATE app.video_versions SET manifest_json = jsonb_set(manifest_json, ARRAY['note'], to_jsonb({viaManifest.Id.ToString()}::text)) WHERE id = {seeded.VersionId}");
        db.Jobs.Add(new Job
        {
            Id = Guid.NewGuid(), JobType = "submit_video", PayloadJson = $"{{\"asset\":\"{viaQueuedJob.Id}\"}}",
            DedupeKey = "retention-" + Guid.NewGuid().ToString("N"), Lane = JobLane.CloudSubmit,
            Status = "queued", RunAfter = DateTimeOffset.UtcNow, CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow
        });
        await db.SaveChangesAsync();

        var report = await service.CleanupAsync(default);

        Assert.Contains(orphan.Id, report.DeletedAssetIds);
        Assert.DoesNotContain(viaScene.Id, report.DeletedAssetIds);
        Assert.DoesNotContain(viaSnapshot.Id, report.DeletedAssetIds);
        Assert.DoesNotContain(viaManifest.Id, report.DeletedAssetIds);
        Assert.DoesNotContain(viaQueuedJob.Id, report.DeletedAssetIds);
        Assert.DoesNotContain(recentOrphan.Id, report.DeletedAssetIds);
        Assert.False(File.Exists(Path.Combine(storage.DataRoot, orphan.RelativePath)));
        foreach (var kept in new[] { viaScene, viaSnapshot, viaManifest, viaQueuedJob, recentOrphan })
        {
            Assert.True(File.Exists(Path.Combine(storage.DataRoot, kept.RelativePath)), kept.RelativePath);
            Assert.True(await db.Assets.AnyAsync(x => x.Id == kept.Id));
        }
    }

    [Fact]
    public async Task ActiveAttemptTempNeverDeleted()
    {
        await using var db = _fixture.CreateContext();
        var (service, storage) = Retention(db, "temp");
        var ingest = Path.Combine(storage.TempRoot, "ingest");
        Directory.CreateDirectory(ingest);
        var stale = Path.Combine(ingest, "stale.part");
        var fresh = Path.Combine(ingest, "fresh.part");
        File.WriteAllText(stale, "x");
        File.WriteAllText(fresh, "x");
        File.SetLastWriteTimeUtc(stale, DateTime.UtcNow.AddDays(-5));

        // A running job means a local attempt may be using temp files: nothing is deleted.
        var running = new Job
        {
            Id = Guid.NewGuid(), JobType = "render_video", PayloadJson = "{}",
            DedupeKey = "running-" + Guid.NewGuid().ToString("N"), Lane = JobLane.HeavyLocal,
            Status = "running", RunAfter = DateTimeOffset.UtcNow, CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow
        };
        db.Jobs.Add(running);
        await db.SaveChangesAsync();

        var blocked = await service.CleanupAsync(default);
        Assert.Equal(0, blocked.DeletedTempFiles);
        Assert.True(File.Exists(stale));

        await db.Jobs.Where(x => x.Id == running.Id)
            .ExecuteUpdateAsync(s => s.SetProperty(x => x.Status, "succeeded"));
        var done = await service.CleanupAsync(default);
        Assert.Equal(1, done.DeletedTempFiles);
        Assert.False(File.Exists(stale));
        Assert.True(File.Exists(fresh));
    }

    [DockerFact]
    public async Task RestoreVerifiesAllHashes()
    {
        await using var iso = await IsolatedAsync();
        var db = iso.Db;
        var (backups, restores, storage) = await BackupToolsAsync(db, "hash");
        await AddAssetFileAsync(db, storage, DateTimeOffset.UtcNow);
        await AddAssetFileAsync(db, storage, DateTimeOffset.UtcNow);
        var backupDir = Path.Combine(_root, "backup-hash");

        var manifest = await backups.CreateAsync(backupDir, iso.Name, default);

        Assert.True(manifest.Assets.Count >= 2);
        var victim = Directory.EnumerateFiles(Path.Combine(backupDir, "assets"), "*", SearchOption.AllDirectories).First();
        File.SetUnixFileMode(victim, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        File.AppendAllText(victim, "tamper");

        var target = "cf_test_restore_" + Guid.NewGuid().ToString("N")[..10];
        var targetRoot = Path.Combine(_root, "restored-hash");
        var error = await Assert.ThrowsAsync<ValidationException>(() =>
            restores.RestoreAsync(backupDir, target, targetRoot, $"restore {target}", default));

        Assert.Equal("BACKUP_HASH_MISMATCH", error.Code);
        Assert.False(await DatabaseExistsAsync(target));
        Assert.False(Directory.Exists(targetRoot));
    }

    [DockerFact]
    public async Task RestoreStartsWithPaidLanesPaused()
    {
        await using var iso = await IsolatedAsync();
        var db = iso.Db;
        var (backups, restores, storage) = await BackupToolsAsync(db, "paused");
        var asset = await AddAssetFileAsync(db, storage, DateTimeOffset.UtcNow);
        db.Jobs.Add(new Job
        {
            Id = Guid.NewGuid(), JobType = "submit_video", PayloadJson = "{}",
            DedupeKey = "restore-queued-" + Guid.NewGuid().ToString("N"), Lane = JobLane.CloudSubmit,
            Status = "queued", RunAfter = DateTimeOffset.UtcNow, CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow
        });
        await db.SaveChangesAsync();
        var backupDir = Path.Combine(_root, "backup-paused");
        await backups.CreateAsync(backupDir, iso.Name, default);

        var target = "cf_test_restore_" + Guid.NewGuid().ToString("N")[..10];
        _restoredDatabases.Add(target);
        var targetRoot = Path.Combine(_root, "restored-paused");
        var report = await restores.RestoreAsync(backupDir, target, targetRoot, $"restore {target}", default);

        Assert.True(report.AllAssetHashesMatch);
        var restoredFile = Path.Combine(targetRoot, asset.RelativePath);
        Assert.Equal(asset.Sha256, Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(restoredFile))).ToLowerInvariant());

        await using var restored = new AppDbContext(
            new DbContextOptionsBuilder<AppDbContext>().UseNpgsql(RestoredConnection(target)).Options,
            TimeProvider.System);
        Assert.Equal("true", await new SystemFlagStore(restored).GetAsync(SystemFlagStore.PaidLanesPaused));
        Assert.True(await restored.Jobs.AnyAsync(x => x.JobType == "submit_video"));
        Assert.DoesNotContain(
            await restored.Jobs.Where(x => x.Lane == JobLane.CloudSubmit || x.Lane == JobLane.CloudPoll).Select(x => x.Status).ToListAsync(),
            status => status is "queued" or "retry_wait" or "running");
        Assert.NotNull(await new SystemFlagStore(restored).GetAsync(SystemFlagStore.RestoredFromBackupAt));
    }

    [DockerFact]
    public async Task RestoreRefusesUnsafeTargets()
    {
        await using var iso = await IsolatedAsync();
        var db = iso.Db;
        var (backups, restores, storage) = await BackupToolsAsync(db, "unsafe");
        await AddAssetFileAsync(db, storage, DateTimeOffset.UtcNow);
        var backupDir = Path.Combine(_root, "backup-unsafe");
        await backups.CreateAsync(backupDir, iso.Name, default);
        var fresh = "cf_test_restore_" + Guid.NewGuid().ToString("N")[..10];

        var wrongConfirmation = await Assert.ThrowsAsync<ValidationException>(() =>
            restores.RestoreAsync(backupDir, fresh, Path.Combine(_root, "r1"), "yes", default));
        var existingDatabase = await Assert.ThrowsAsync<ConflictException>(() =>
            restores.RestoreAsync(
                backupDir, iso.Name, Path.Combine(_root, "r2"), $"restore {iso.Name}", default));
        Directory.CreateDirectory(Path.Combine(_root, "r3"));
        File.WriteAllText(Path.Combine(_root, "r3", "keep.txt"), "x");
        var usedRoot = await Assert.ThrowsAsync<ConflictException>(() =>
            restores.RestoreAsync(backupDir, fresh, Path.Combine(_root, "r3"), $"restore {fresh}", default));

        Assert.Equal("RESTORE_CONFIRMATION_REQUIRED", wrongConfirmation.Code);
        Assert.Equal("RESTORE_DATABASE_EXISTS", existingDatabase.Code);
        Assert.Equal("RESTORE_DATA_ROOT_NOT_EMPTY", usedRoot.Code);
        Assert.True(File.Exists(Path.Combine(_root, "r3", "keep.txt")));
    }

    [DockerFact]
    public async Task BackupQuiescesRestoresStateAndKeepsSecretsOut()
    {
        await using var iso = await IsolatedAsync();
        var db = iso.Db;
        var (backups, _, storage) = await BackupToolsAsync(db, "quiesce");
        await AddAssetFileAsync(db, storage, DateTimeOffset.UtcNow);
        var queued = new Job
        {
            Id = Guid.NewGuid(), JobType = "render_video", PayloadJson = "{}",
            DedupeKey = "quiesce-" + Guid.NewGuid().ToString("N"), Lane = JobLane.HeavyLocal,
            Status = "queued", RunAfter = DateTimeOffset.UtcNow, CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow
        };
        db.Jobs.Add(queued);
        await db.SaveChangesAsync();
        var backupDir = Path.Combine(_root, "backup-quiesce");

        var manifest = await backups.CreateAsync(backupDir, iso.Name, default);

        Assert.Null(await new SystemFlagStore(db).GetAsync(SystemFlagStore.WriteQuiesce));
        db.ChangeTracker.Clear();
        Assert.Equal("queued", await db.Jobs.Where(x => x.Id == queued.Id).Select(x => x.Status).SingleAsync());
        Assert.False(string.IsNullOrWhiteSpace(manifest.Note));
        var text = await File.ReadAllTextAsync(Path.Combine(backupDir, "manifest.json"));
        Assert.DoesNotContain("password", text, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("FAL_KEY", text, StringComparison.Ordinal);
        Assert.Equal(
            UnixFileMode.UserRead | UnixFileMode.UserWrite,
            File.GetUnixFileMode(Path.Combine(backupDir, "manifest.json")));
        Assert.Equal(
            UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute,
            File.GetUnixFileMode(backupDir));
    }

    [Fact]
    public async Task PausedPaidLanesBlockSubmitUntilTheOwnerClearsTheFlag()
    {
        await using var db = _fixture.CreateContext();
        var flags = new SystemFlagStore(db);
        await flags.SetAsync(SystemFlagStore.PaidLanesPaused, "true");
        var provider = new CountingBillableProvider();
        var budget = new ContentFactory.Infrastructure.Billing.BudgetOptions { AllowPaidApis = true };
        var handler = new SubmitVideoHandler(
            db,
            provider,
            new ContentFactory.Infrastructure.Billing.PostgresBudgetGuard(
                db, budget, new ContentFactory.Infrastructure.Billing.LiveApprovalGuard(budget, TimeProvider.System)),
            new ContentFactory.Infrastructure.Billing.PriceSnapshotService(db, budget),
            new ContentFactory.Infrastructure.Providers.Fal.FalOptions(),
            TimeProvider.System,
            budget);
        var image = new AssetRef(Guid.NewGuid(), new string('a', 64), "assets/x", AssetKind.Image, "image/jpeg", 1, null);
        var payload = JsonSerializer.Serialize(
            new
            {
                request = new VideoGenerationRequest("op-paused", Guid.NewGuid(), "demo", image, "p", "720p", "9:16", Guid.NewGuid()),
                campaignId = Guid.NewGuid(), videoScopeId = Guid.NewGuid(), configHash = "h"
            },
            JsonDefaults.Options);

        try
        {
            var outcome = await handler.ExecuteAsync(
                new LeasedJob(Guid.NewGuid(), "submit_video", payload, 1, "w", 1, DateTimeOffset.UtcNow.AddMinutes(1)),
                default);

            Assert.Equal("blocked", outcome.Status);
            Assert.Equal("PAID_LANES_PAUSED", outcome.ErrorCode);
            Assert.Equal(0, provider.Submits);
        }
        finally
        {
            await flags.SetAsync(SystemFlagStore.PaidLanesPaused, null);
        }
    }

    private sealed class Isolated(PostgresTestDatabase database, AppDbContext db) : IAsyncDisposable
    {
        public AppDbContext Db { get; } = db;
        public string Name => database.DatabaseName;

        public async ValueTask DisposeAsync()
        {
            await Db.DisposeAsync();
            await database.DisposeAsync();
        }
    }

    private static async Task<Isolated> IsolatedAsync()
    {
        var database = await PostgresTestDatabase.CreateAsync();
        var db = new AppDbContext(
            new DbContextOptionsBuilder<AppDbContext>().UseNpgsql(database.ConnectionString).Options,
            TimeProvider.System);
        await db.Database.MigrateAsync();
        return new Isolated(database, db);
    }

    private async Task<(BackupService Backups, RestoreService Restores, StorageOptions Storage)> BackupToolsAsync(
        AppDbContext db, string name)
    {
        var storage = new StorageOptions { DataRoot = Path.Combine(_root, "data-" + name) };
        Directory.CreateDirectory(storage.DataRoot);
        var tool = new DockerPostgresTool(DockerFactAttribute.Container, "content_factory");
        var admin = Environment.GetEnvironmentVariable("CF_TEST_POSTGRES")!;
        await Task.CompletedTask;
        return (
            new BackupService(db, storage, tool, new SystemFlagStore(db), new PostgresJobStore(db, TimeProvider.System), TimeProvider.System),
            new RestoreService(tool, admin, TimeProvider.System),
            storage);
    }

    private (RetentionService Service, StorageOptions Storage) Retention(AppDbContext db, string name)
    {
        var storage = new StorageOptions { DataRoot = Path.Combine(_root, "data-" + name) };
        Directory.CreateDirectory(storage.DataRoot);
        return (new RetentionService(db, storage, TimeProvider.System, TimeSpan.FromDays(14)), storage);
    }

    private static async Task<Asset> AddAssetFileAsync(AppDbContext db, StorageOptions storage, DateTimeOffset createdAt)
    {
        var bytes = RandomNumberGenerator.GetBytes(64);
        var hash = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
        var relative = $"assets/sha256/{hash[..2]}/{hash}.jpg";
        var path = Path.Combine(storage.DataRoot, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        await File.WriteAllBytesAsync(path, bytes);
        var asset = new Asset
        {
            Id = Guid.NewGuid(), Sha256 = hash, RelativePath = relative, Kind = AssetKind.Image,
            Mime = "image/jpeg", Bytes = bytes.Length, RightsStatus = "owner_attested",
            SourceReference = "fixture", CloudUseAllowed = false, CreatedAt = createdAt
        };
        db.Assets.Add(asset);
        await db.SaveChangesAsync();
        return asset;
    }

    private static async Task<bool> DatabaseExistsAsync(string name)
    {
        await using var connection = new NpgsqlConnection(Environment.GetEnvironmentVariable("CF_TEST_POSTGRES"));
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT 1 FROM pg_database WHERE datname = @n";
        command.Parameters.AddWithValue("n", name);
        return await command.ExecuteScalarAsync() is not null;
    }

    private static string RestoredConnection(string database)
        => new NpgsqlConnectionStringBuilder(Environment.GetEnvironmentVariable("CF_TEST_POSTGRES")!)
        {
            Database = database, Pooling = false
        }.ConnectionString;

    private static LeasedJob RenderJob()
        => new(
            Guid.NewGuid(), "render_video",
            JsonSerializer.Serialize(
                new
                {
                    scriptRequest = new ScriptRequest(
                        Guid.NewGuid(),
                        new ProductSnapshot(Guid.NewGuid(), 1, "t", "TH", "th-TH", [], [], DateTimeOffset.UtcNow),
                        "th-TH", VideoMode.TemplateOnly, 25_000, "v"),
                    videoVersionId = Guid.NewGuid()
                },
                JsonDefaults.Options),
            1, "w", 1, DateTimeOffset.UtcNow.AddMinutes(1));

    private sealed class CountingRenderer : IVideoRenderer
    {
        public int Calls { get; private set; }

        public Task<RenderResult> RenderAsync(RenderManifest manifest, CancellationToken ct)
        {
            Calls++;
            throw new InvalidOperationException("The renderer must not run under disk pressure.");
        }
    }

    private sealed class CountingBillableProvider : IVideoProvider
    {
        public int Submits { get; private set; }
        public string Name => "fal";
        public Task<ProviderSubmission> SubmitAsync(VideoGenerationRequest request, CancellationToken ct)
        {
            Submits++;
            throw new InvalidOperationException("A paused paid lane must not submit.");
        }

        public Task<ProviderPollResult> PollAsync(ProviderSubmission submission, CancellationToken ct)
            => throw new NotSupportedException();

        public Task RequestCancelAsync(ProviderSubmission submission, CancellationToken ct)
            => throw new NotSupportedException();
    }
}
