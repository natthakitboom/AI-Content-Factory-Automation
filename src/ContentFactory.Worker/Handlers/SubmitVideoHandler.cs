using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using ContentFactory.Contracts;
using ContentFactory.Core.Abstractions;
using ContentFactory.Core.Exceptions;
using ContentFactory.Infrastructure.Billing;
using ContentFactory.Infrastructure.Persistence;
using ContentFactory.Infrastructure.Persistence.Entities;
using ContentFactory.Infrastructure.Providers.Fal;
using Microsoft.EntityFrameworkCore;

namespace ContentFactory.Worker.Handlers;

/// <summary>
/// Worker stage that submits a single image-to-video scene request to the configured
/// <see cref="IVideoProvider"/>. It persists a provider attempt, reserves the budget,
/// and only then performs the billable POST. If the provider accepts the request but the
/// connection drops before the response arrives, the provider request is marked
/// <c>submission_unknown</c>, the budget reservation is held, and the job fails without
/// blindly retrying the POST. Replaying the job is idempotent until the owner reconciles.
/// </summary>
public sealed class SubmitVideoHandler : IJobHandler
{
    public const string JobTypeName = "submit_video";

    private readonly AppDbContext _db;
    private readonly IVideoProvider _videoProvider;
    private readonly IBudgetGuard _budgetGuard;
    private readonly PriceSnapshotService _priceService;
    private readonly FalOptions _falOptions;
    private readonly TimeProvider _clock;
    private readonly BudgetOptions? _budgetOptions;

    public SubmitVideoHandler(
        AppDbContext db,
        IVideoProvider videoProvider,
        IBudgetGuard budgetGuard,
        PriceSnapshotService priceService,
        FalOptions falOptions,
        TimeProvider clock,
        BudgetOptions? budgetOptions = null)
    {
        _budgetOptions = budgetOptions;
        _db = db ?? throw new ArgumentNullException(nameof(db));
        _videoProvider = videoProvider ?? throw new ArgumentNullException(nameof(videoProvider));
        _budgetGuard = budgetGuard ?? throw new ArgumentNullException(nameof(budgetGuard));
        _priceService = priceService ?? throw new ArgumentNullException(nameof(priceService));
        _falOptions = falOptions ?? throw new ArgumentNullException(nameof(falOptions));
        _clock = clock ?? throw new ArgumentNullException(nameof(clock));
    }

    public string JobType => JobTypeName;

    public async Task<JobOutcome> ExecuteAsync(LeasedJob job, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(job);
        ct.ThrowIfCancellationRequested();

        SubmitVideoPayload? payload;
        try
        {
            payload = JsonSerializer.Deserialize<SubmitVideoPayload>(
                job.PayloadJson,
                JsonDefaults.Options);
        }
        catch (JsonException)
        {
            return Blocked("SUBMIT_PAYLOAD_INVALID");
        }

        if (payload is null ||
            payload.Request is null ||
            payload.CampaignId == Guid.Empty ||
            payload.VideoScopeId == Guid.Empty ||
            string.IsNullOrWhiteSpace(payload.ConfigHash))
        {
            return Blocked("SUBMIT_PAYLOAD_INVALID");
        }

        // After a restore the ledger may be older than what was really sent to the provider,
        // so no paid submission runs until the owner has reconciled and cleared the flag.
        if (_videoProvider.IsBillable
            && await _db.SystemFlags.AsNoTracking().AnyAsync(
                x => x.Key == "paid_lanes_paused" && x.Value == "true", ct))
        {
            return Blocked("PAID_LANES_PAUSED");
        }

        var existing = await _db.ProviderRequests
            .AsNoTracking()
            .SingleOrDefaultAsync(
                x => x.ClientOperationId == payload.Request.OperationId,
                ct);
        if (existing is not null)
        {
            return ExistingOutcome(existing);
        }

        var sourceAsset = await _db.Assets
            .AsNoTracking()
            .SingleOrDefaultAsync(x => x.Id == payload.Request.SourceImage.Id, ct);
        if (sourceAsset is null)
        {
            return Blocked("SUBMIT_SOURCE_ASSET_MISSING");
        }

        if (!sourceAsset.CloudUseAllowed)
        {
            return Blocked("CLOUD_USE_NOT_ALLOWED");
        }

        // The paid flag wins before any quote, reservation or HTTP request exists.
        if (_videoProvider.IsBillable && _budgetOptions is { AllowPaidApis: false })
        {
            return Blocked("PAID_APIS_DISABLED");
        }

        BudgetReservation? reservation = null;
        if (_videoProvider.IsBillable)
        {
            try
            {
                var quote = await _priceService.GetQuoteAsync(
                    _videoProvider.Name,
                    _falOptions.ModelId,
                    payload.ConfigHash,
                    ct);

                reservation = await _budgetGuard.ReserveAsync(
                    payload.Request.OperationId,
                    payload.CampaignId,
                    payload.VideoScopeId,
                    quote,
                    ct);
            }
            catch (PaidApisDisabledException)
            {
                return Blocked("PAID_APIS_DISABLED");
            }
        }

        var requestHash = HashPayload(payload.Request);
        var providerRequest = new ProviderRequest
        {
            Id = Guid.NewGuid(),
            JobId = job.Id,
            Provider = _videoProvider.Name,
            Model = _videoProvider.IsBillable ? _falOptions.ModelId : _videoProvider.Name,
            ClientOperationId = payload.Request.OperationId,
            RequestHash = requestHash,
            Status = "prepared",
            CreatedAt = _clock.GetUtcNow()
        };
        _db.ProviderRequests.Add(providerRequest);
        await _db.SaveChangesAsync(ct);

        providerRequest.Status = "submitting";
        await _db.SaveChangesAsync(ct);

        ProviderSubmission submission;
        try
        {
            submission = await _videoProvider.SubmitAsync(payload.Request, ct);
        }
        catch (SubmissionUnknownException)
        {
            providerRequest.Status = "submission_unknown";
            await _db.SaveChangesAsync(ct);
            return new JobOutcome(
                "failed",
                null,
                "SUBMISSION_UNKNOWN",
                providerRequest.Id.ToString("D"));
        }
        catch (ProviderRejectedException exception)
        {
            providerRequest.Status = "failed_confirmed";
            await _db.SaveChangesAsync(ct);
            if (reservation is not null)
            {
                await _budgetGuard.ReleaseAsync(
                    reservation.Id,
                    $"Provider rejected: {exception.Code}",
                    ct);
            }

            return new JobOutcome(
                "failed",
                null,
                exception.Code,
                providerRequest.Id.ToString("D"));
        }

        providerRequest.ProviderRequestId = submission.RequestId;
        providerRequest.StatusUrl = submission.StatusUrl.ToString();
        providerRequest.ResponseUrl = submission.ResponseUrl.ToString();
        providerRequest.CancelUrl = submission.CancelUrl?.ToString();
        providerRequest.Status = "submitted";
        providerRequest.SubmittedAt = _clock.GetUtcNow();
        await _db.SaveChangesAsync(ct);

        return new JobOutcome(
            "succeeded",
            null,
            null,
            providerRequest.Id.ToString("D"));
    }

    private static JobOutcome ExistingOutcome(ProviderRequest existing)
        => existing.Status switch
        {
            "submitted" or "in_progress" or "succeeded" => new JobOutcome(
                "succeeded",
                null,
                null,
                existing.Id.ToString("D")),
            "submission_unknown" => new JobOutcome(
                "failed",
                null,
                "SUBMISSION_UNKNOWN",
                existing.Id.ToString("D")),
            "failed_confirmed" => new JobOutcome(
                "failed",
                null,
                "FAL_PROVIDER_REJECTED",
                existing.Id.ToString("D")),
            "prepared" or "submitting" =>
                // A previous worker died before finishing; let the replay continue.
                Blocked("SUBMIT_INCOMPLETE_ATTEMPT"),
            _ => Blocked("SUBMIT_UNEXPECTED_STATE")
        };

    private static string HashPayload(VideoGenerationRequest request)
    {
        var json = JsonSerializer.Serialize(request, JsonDefaults.Options);
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(json)))
            .ToLowerInvariant();
    }

    private static JobOutcome Blocked(string code)
        => new("blocked", null, code, null);

    private sealed record SubmitVideoPayload(
        VideoGenerationRequest Request,
        Guid CampaignId,
        Guid VideoScopeId,
        string ConfigHash);
}
