using System.Text.Json;
using ContentFactory.Contracts;
using ContentFactory.Core.Exceptions;
using ContentFactory.Infrastructure.Persistence;
using ContentFactory.Infrastructure.Persistence.Entities;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace ContentFactory.Infrastructure.Jobs;

public sealed record ScheduleTickResult(int Enqueued, int Missed, int Skipped, int Failed);

/// <summary>
/// Turns due schedule slots into campaign runs. State lives in PostgreSQL (slot key, last
/// slot and the daily count), so a restart or a second tick can neither repeat a slot nor
/// exceed the daily limit. A scheduled run asks for no cloud scenes and no paid approval, so
/// the budget, approval and thermal guards still decide everything that costs money.
/// </summary>
public sealed class ScheduleTicker
{
    private readonly AppDbContext _db;
    private readonly RunEnqueuer _runs;

    public ScheduleTicker(AppDbContext db, RunEnqueuer runs)
    {
        _db = db ?? throw new ArgumentNullException(nameof(db));
        _runs = runs ?? throw new ArgumentNullException(nameof(runs));
    }

    public async Task<ScheduleTickResult> TickAsync(DateTimeOffset now, CancellationToken ct)
    {
        int enqueued = 0, missed = 0, skipped = 0, failed = 0;
        var ids = await _db.Schedules.AsNoTracking()
            .Where(x => x.Enabled)
            .Select(x => x.Id)
            .ToListAsync(ct);

        foreach (var id in ids)
        {
            _db.ChangeTracker.Clear();
            var schedule = await _db.Schedules.SingleOrDefaultAsync(x => x.Id == id && x.Enabled, ct);
            if (schedule is null)
            {
                continue;
            }

            var evaluation = SchedulePlanner.Evaluate(
                schedule.Timezone, schedule.LocalTime, schedule.DaysOfWeek, now, schedule.LastSlot);

            foreach (var slot in evaluation.Missed)
            {
                Audit(schedule, "schedule.missed", slot, new { reason = "older than 60 minutes" }, now);
                missed++;
            }

            foreach (var slot in evaluation.Skipped)
            {
                Audit(schedule, "schedule.dst_skipped", slot, new { reason = "local time does not exist" }, now);
                skipped++;
            }

            DateTimeOffset? newest = null;
            foreach (var slot in evaluation.Due)
            {
                if (await TryRunAsync(schedule, slot, now, ct))
                {
                    enqueued++;
                }
                else
                {
                    failed++;
                }
            }

            var handled = evaluation.Missed.Concat(evaluation.Skipped).Concat(evaluation.Due)
                .Select(x => x.Instant)
                .DefaultIfEmpty()
                .Max();
            if (handled != default)
            {
                newest = handled;
            }

            if (newest is { } latest && (schedule.LastSlot is null || latest > schedule.LastSlot))
            {
                schedule.LastSlot = latest;
            }

            await _db.SaveChangesAsync(ct);
        }

        return new ScheduleTickResult(enqueued, missed, skipped, failed);
    }

    private async Task<bool> TryRunAsync(
        Schedule schedule, ScheduleSlot slot, DateTimeOffset now, CancellationToken ct)
    {
        if (await _db.ScheduleOccurrences.AnyAsync(
                x => x.ScheduleId == schedule.Id && x.SlotKey == slot.Key, ct))
        {
            return true;
        }

        var dayPrefix = slot.Key[..10];
        var today = await _db.ScheduleOccurrences.CountAsync(
            x => x.ScheduleId == schedule.Id && x.SlotKey.StartsWith(dayPrefix), ct);
        if (today >= schedule.MaxJobsPerDay)
        {
            Audit(schedule, "schedule.limit_reached", slot, new { limit = schedule.MaxJobsPerDay }, now);
            return false;
        }

        var campaign = await _db.Campaigns.AsNoTracking()
            .SingleOrDefaultAsync(x => x.Id == schedule.CampaignId, ct);
        if (campaign is null)
        {
            Audit(schedule, "schedule.skipped", slot, new { code = "CAMPAIGN_NOT_FOUND" }, now);
            return false;
        }

        try
        {
            var run = await _runs.EnqueueAsync(
                new RunEnqueueCommand(
                    campaign.Id,
                    IdempotencyKey(schedule, slot),
                    campaign.Mode,
                    campaign.TargetDurationMs,
                    AutoGenerateVoice: true,
                    CloudVideoSceneLimit: 0),
                ct);
            _db.ScheduleOccurrences.Add(new ScheduleOccurrence
            {
                Id = Guid.NewGuid(),
                ScheduleId = schedule.Id,
                SlotKey = slot.Key,
                JobId = run.JobId,
                CreatedAt = now
            });
            Audit(schedule, "schedule.enqueued", slot, new { run.JobId }, now);
            await _db.SaveChangesAsync(ct);
            return true;
        }
        catch (Exception exception) when (exception is ValidationException or ConflictException or NotFoundException)
        {
            var code = exception switch
            {
                ValidationException v => v.Code,
                ConflictException c => c.Code,
                NotFoundException n => n.Code,
                _ => "SCHEDULE_FAILED"
            };
            _db.ChangeTracker.Clear();
            _db.Attach(schedule);
            Audit(schedule, "schedule.skipped", slot, new { code }, now);
            return false;
        }
        catch (DbUpdateException exception) when (exception.InnerException is PostgresException { SqlState: "23505" })
        {
            // Another tick already recorded this slot.
            _db.ChangeTracker.Clear();
            return true;
        }
    }

    private static string IdempotencyKey(Schedule schedule, ScheduleSlot slot)
    {
        var key = $"schedule-{schedule.Id:N}-{slot.Key}";
        return key.Length <= 128 ? key : key[..128];
    }

    private void Audit(Schedule schedule, string action, ScheduleSlot slot, object details, DateTimeOffset now)
        => _db.AuditEvents.Add(new AuditEvent
        {
            Id = Guid.NewGuid(),
            Actor = "scheduler",
            Action = action,
            TargetType = "schedule",
            TargetId = schedule.Id,
            DetailsJson = JsonSerializer.Serialize(
                new { slotKey = slot.Key, slotInstant = slot.Instant, detail = details },
                JsonDefaults.Options),
            CreatedAt = now
        });
}
