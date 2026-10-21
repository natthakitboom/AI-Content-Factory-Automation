using ContentFactory.Infrastructure.Persistence;
using ContentFactory.Infrastructure.Persistence.Entities;
using Microsoft.EntityFrameworkCore;

namespace ContentFactory.Infrastructure.Storage;

public sealed class SystemFlagStore
{
    public const string WriteQuiesce = "write_quiesce";
    public const string PaidLanesPaused = "paid_lanes_paused";
    public const string RestoredFromBackupAt = "restored_from_backup_at";

    private readonly AppDbContext _db;

    public SystemFlagStore(AppDbContext db)
    {
        _db = db ?? throw new ArgumentNullException(nameof(db));
    }

    public async Task<string?> GetAsync(string key)
        => await _db.SystemFlags.AsNoTracking()
            .Where(x => x.Key == key)
            .Select(x => x.Value)
            .SingleOrDefaultAsync();

    /// <summary>Sets a flag, or removes it when <paramref name="value"/> is null.</summary>
    public async Task SetAsync(string key, string? value)
    {
        if (value is null)
        {
            await _db.SystemFlags.Where(x => x.Key == key).ExecuteDeleteAsync();
            return;
        }

        var now = DateTimeOffset.UtcNow;
        var updated = await _db.SystemFlags.Where(x => x.Key == key)
            .ExecuteUpdateAsync(s => s.SetProperty(x => x.Value, value).SetProperty(x => x.UpdatedAt, now));
        if (updated == 0)
        {
            _db.SystemFlags.Add(new SystemFlag { Key = key, Value = value, UpdatedAt = now });
            await _db.SaveChangesAsync();
            _db.ChangeTracker.Clear();
        }
    }
}
