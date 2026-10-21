using ContentFactory.Core.Exceptions;
using ContentFactory.Infrastructure.Jobs;
using ContentFactory.Infrastructure.Persistence;
using ContentFactory.Infrastructure.Persistence.Entities;
using Microsoft.EntityFrameworkCore;

namespace ContentFactory.Api.Endpoints;

public sealed record SaveScheduleRequest(
    Guid CampaignId,
    string Timezone,
    string LocalTime,
    int[] DaysOfWeek,
    int MaxJobsPerDay,
    bool Enabled,
    long? ExpectedVersion);

/// <summary>Schedules are created disabled by default; enabling is an explicit owner action.</summary>
public static class ScheduleEndpoints
{
    public static IEndpointRouteBuilder MapScheduleEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var group = endpoints.MapGroup("/api/v1/schedules").RequireAuthorization();
        group.MapGet("/", ListAsync);
        group.MapPost("/", CreateAsync);
        group.MapPut("/{id:guid}", UpdateAsync);
        return endpoints;
    }

    private static async Task<IResult> ListAsync(AppDbContext db, CancellationToken ct)
        => Results.Ok(await db.Schedules.AsNoTracking()
            .OrderBy(x => x.CampaignId)
            .Select(x => new
            {
                x.Id, x.CampaignId, x.Timezone, localTime = x.LocalTime.ToString("HH:mm", System.Globalization.CultureInfo.InvariantCulture),
                x.DaysOfWeek, x.Enabled, x.MaxJobsPerDay, x.LastSlot, x.Version
            })
            .ToListAsync(ct));

    private static async Task<IResult> CreateAsync(
        SaveScheduleRequest request, AppDbContext db, CancellationToken ct)
    {
        Validate(request);
        if (!await db.Campaigns.AnyAsync(x => x.Id == request.CampaignId, ct))
        {
            throw new NotFoundException("CAMPAIGN_NOT_FOUND", "Campaign not found.");
        }

        var schedule = new Schedule { Id = Guid.NewGuid(), CampaignId = request.CampaignId, Version = 1 };
        Apply(schedule, request, enabled: false);
        db.Schedules.Add(schedule);
        await db.SaveChangesAsync(ct);
        return Results.Created($"/api/v1/schedules/{schedule.Id}", new { schedule.Id, schedule.Enabled, schedule.Version });
    }

    private static async Task<IResult> UpdateAsync(
        Guid id, SaveScheduleRequest request, AppDbContext db, CancellationToken ct)
    {
        Validate(request);
        var schedule = await db.Schedules.SingleOrDefaultAsync(x => x.Id == id, ct)
            ?? throw new NotFoundException("SCHEDULE_NOT_FOUND", "Schedule not found.");
        if (request.ExpectedVersion is { } expected && expected != schedule.Version)
        {
            throw new ConflictException("SCHEDULE_VERSION_CONFLICT", "The schedule changed; reload it first.");
        }

        Apply(schedule, request, request.Enabled);
        schedule.Version++;
        await db.SaveChangesAsync(ct);
        return Results.Ok(new { schedule.Id, schedule.Enabled, schedule.Version });
    }

    private static void Apply(Schedule schedule, SaveScheduleRequest request, bool enabled)
    {
        schedule.Timezone = request.Timezone;
        schedule.LocalTime = TimeOnly.ParseExact(request.LocalTime, "HH:mm", System.Globalization.CultureInfo.InvariantCulture);
        schedule.DaysOfWeek = request.DaysOfWeek.Distinct().Order().ToArray();
        schedule.MaxJobsPerDay = request.MaxJobsPerDay;
        schedule.Enabled = enabled;
    }

    private static void Validate(SaveScheduleRequest request)
    {
        SchedulePlanner.ResolveZone(request.Timezone ?? string.Empty);
        if (!TimeOnly.TryParseExact(request.LocalTime, "HH:mm", System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.None, out _))
        {
            throw new ValidationException("SCHEDULE_TIME_INVALID", "localTime must be HH:mm.");
        }

        if (request.DaysOfWeek is not { Length: > 0 } || request.DaysOfWeek.Any(x => x is < 0 or > 6))
        {
            throw new ValidationException("SCHEDULE_DAYS_INVALID", "daysOfWeek must list days 0 (Sunday) to 6.");
        }

        if (request.MaxJobsPerDay is < 1 or > 5)
        {
            throw new ValidationException("SCHEDULE_LIMIT_INVALID", "maxJobsPerDay must be 1 to 5.");
        }
    }
}
