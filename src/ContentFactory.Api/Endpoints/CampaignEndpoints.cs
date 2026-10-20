using ContentFactory.Api.Middleware;
using ContentFactory.Contracts;
using ContentFactory.Core.Exceptions;
using ContentFactory.Infrastructure.Jobs;
using ContentFactory.Infrastructure.Persistence;
using ContentFactory.Infrastructure.Persistence.Entities;
using Microsoft.EntityFrameworkCore;

namespace ContentFactory.Api.Endpoints;

public sealed record CreateCampaignRequest(
    Guid ProductId,
    Guid ChannelId,
    string? Mode,
    string? Locale,
    int TargetDurationMs,
    decimal BudgetUsd);

public sealed record CreateRunRequest(
    string? Mode,
    int TargetDurationMs,
    bool AutoGenerateVoice,
    int CloudVideoSceneLimit);

public sealed record CampaignResponse(
    Guid Id,
    Guid ProductId,
    Guid ChannelId,
    VideoMode Mode,
    string Locale,
    int TargetDurationMs,
    decimal BudgetUsd,
    string Status,
    long Version,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt);

public static class CampaignEndpoints
{
    public static IEndpointRouteBuilder MapCampaignEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var group = endpoints.MapGroup("/api/v1/campaigns").RequireAuthorization();

        group.MapPost("/", CreateCampaignAsync);
        group.MapPost("/{campaignId:guid}/runs", CreateRunAsync);

        return endpoints;
    }

    private static async Task<IResult> CreateCampaignAsync(
        CreateCampaignRequest request,
        AppDbContext db,
        TimeProvider clock,
        CancellationToken ct)
    {
        var product = await db.Products
            .AsNoTracking()
            .SingleOrDefaultAsync(x => x.Id == request.ProductId, ct)
            ?? throw new NotFoundException("PRODUCT_NOT_FOUND", "Product not found.");

        if (product.ArchivedAt is not null)
        {
            throw new ConflictException("PRODUCT_ARCHIVED", "An archived product cannot start a campaign.");
        }

        var channel = await db.Channels
            .AsNoTracking()
            .SingleOrDefaultAsync(x => x.Id == request.ChannelId, ct)
            ?? throw new NotFoundException("CHANNEL_NOT_FOUND", "Channel not found.");

        if (!string.Equals(channel.Status, "active", StringComparison.Ordinal))
        {
            throw new ConflictException(
                "CHANNEL_NOT_ACTIVE",
                "Only an active channel can start a campaign.");
        }

        var locale = string.IsNullOrWhiteSpace(request.Locale)
            ? product.Locale
            : request.Locale.Trim();

        if (locale.Length > 32 || !string.Equals(locale, product.Locale, StringComparison.Ordinal))
        {
            throw Invalid(
                "CAMPAIGN_LOCALE_MISMATCH",
                "The campaign locale must match the product locale.");
        }

        if (!string.Equals(channel.Locale, locale, StringComparison.Ordinal)
            || !string.Equals(channel.Market, product.Market, StringComparison.Ordinal))
        {
            throw Invalid(
                "CAMPAIGN_CHANNEL_MISMATCH",
                "The channel market and locale must match the product.");
        }

        if (request.TargetDurationMs is < 15000 or > 35000)
        {
            throw Invalid(
                "CAMPAIGN_DURATION_INVALID",
                "The target duration must be between 15000 and 35000 milliseconds.");
        }

        if (request.BudgetUsd < 0)
        {
            throw Invalid("CAMPAIGN_BUDGET_INVALID", "The budget cannot be negative.");
        }

        var now = clock.GetUtcNow();
        var campaign = new Campaign
        {
            Id = Guid.NewGuid(),
            ProductId = product.Id,
            ChannelId = channel.Id,
            Mode = ParseMode(request.Mode),
            Locale = locale,
            TargetDurationMs = request.TargetDurationMs,
            BudgetUsd = request.BudgetUsd,
            Status = "draft",
            Version = 1,
            CreatedAt = now,
            UpdatedAt = now
        };

        db.Campaigns.Add(campaign);
        await db.SaveChangesAsync(ct);

        return Results.Created(
            $"/api/v1/campaigns/{campaign.Id}",
            ToResponse(campaign));
    }

    private static async Task<IResult> CreateRunAsync(
        Guid campaignId,
        CreateRunRequest request,
        HttpRequest http,
        RunEnqueuer enqueuer,
        CancellationToken ct)
    {
        var idempotencyKey = http.Headers["Idempotency-Key"].ToString();

        var command = new RunEnqueueCommand(
            campaignId,
            idempotencyKey,
            ParseMode(request.Mode),
            request.TargetDurationMs,
            request.AutoGenerateVoice,
            request.CloudVideoSceneLimit,
            AllowPaidCloud: false);

        var enqueued = await enqueuer.EnqueueAsync(command, ct);

        return Results.Accepted(
            $"/api/v1/jobs/{enqueued.JobId}",
            new { jobId = enqueued.JobId, revisionId = enqueued.RevisionId });
    }

    private static CampaignResponse ToResponse(Campaign campaign)
        => new(
            campaign.Id,
            campaign.ProductId,
            campaign.ChannelId,
            campaign.Mode,
            campaign.Locale,
            campaign.TargetDurationMs,
            campaign.BudgetUsd,
            campaign.Status,
            campaign.Version,
            campaign.CreatedAt,
            campaign.UpdatedAt);

    private static VideoMode ParseMode(string? value)
    {
        var normalized = value?.Trim();
        return normalized switch
        {
            "template_only" or "templateOnly" => VideoMode.TemplateOnly,
            "hybrid_motion" or "hybridMotion" => VideoMode.HybridMotion,
            _ => throw Invalid(
                "CAMPAIGN_MODE_INVALID",
                "Mode must be template_only or hybrid_motion.")
        };
    }

    private static ValidationException Invalid(string code, string message) => new(code, message);
}
