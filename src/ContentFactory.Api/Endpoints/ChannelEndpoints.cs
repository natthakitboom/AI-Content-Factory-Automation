using ContentFactory.Core.Exceptions;
using ContentFactory.Infrastructure.Jobs;
using ContentFactory.Infrastructure.Persistence;
using ContentFactory.Infrastructure.Persistence.Entities;
using Microsoft.EntityFrameworkCore;

namespace ContentFactory.Api.Endpoints;

public sealed record CreateChannelRequest(
    string? Platform, string? DisplayName, string? Market, string? Locale, string? Timezone);

/// <summary>
/// The owner's own publishing destination. A channel records where a human will post by hand;
/// creating one grants no platform access and stores no platform credential.
/// </summary>
public static class ChannelEndpoints
{
    public static IEndpointRouteBuilder MapChannelEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var group = endpoints.MapGroup("/api/v1/channels").RequireAuthorization();
        group.MapGet("/", ListAsync);
        group.MapPost("/", CreateAsync);
        return endpoints;
    }

    private static async Task<IResult> ListAsync(AppDbContext db, CancellationToken ct)
        => Results.Ok(await db.Channels.AsNoTracking()
            .OrderBy(x => x.DisplayName)
            .Select(x => new { x.Id, x.Platform, x.DisplayName, x.Market, x.Locale, x.Timezone, x.Status })
            .ToListAsync(ct));

    private static async Task<IResult> CreateAsync(
        CreateChannelRequest request, AppDbContext db, TimeProvider clock, CancellationToken ct)
    {
        var platform = (request.Platform ?? "tiktok").Trim().ToLowerInvariant();
        var name = request.DisplayName?.Trim() ?? string.Empty;
        var market = request.Market?.Trim() ?? string.Empty;
        var locale = request.Locale?.Trim() ?? string.Empty;
        if (platform.Length is 0 or > 32 || name.Length is 0 or > 128 || market.Length is 0 or > 16 || locale.Length is 0 or > 32)
        {
            throw new ValidationException(
                "CHANNEL_INVALID", "Platform, display name, market and locale are required and must be short.");
        }

        var timezone = request.Timezone?.Trim() ?? string.Empty;
        SchedulePlanner.ResolveZone(timezone);

        var channel = new Channel
        {
            Id = Guid.NewGuid(),
            Platform = platform,
            DisplayName = name,
            Market = market,
            Locale = locale,
            Timezone = timezone,
            Status = "active",
            CapabilitiesJson = "{}",
            CreatedAt = clock.GetUtcNow()
        };
        db.Channels.Add(channel);
        await db.SaveChangesAsync(ct);
        return Results.Created($"/api/v1/channels/{channel.Id}", new { channel.Id, channel.Status });
    }
}
