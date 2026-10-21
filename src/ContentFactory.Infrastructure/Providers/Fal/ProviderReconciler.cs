using System.Text.Json;
using ContentFactory.Contracts;
using ContentFactory.Core.Abstractions;
using ContentFactory.Core.Exceptions;
using ContentFactory.Infrastructure.Persistence;
using ContentFactory.Infrastructure.Persistence.Entities;
using Microsoft.EntityFrameworkCore;

namespace ContentFactory.Infrastructure.Providers.Fal;

/// <summary>
/// Owner actions for provider requests the worker cannot resolve alone. Nothing here
/// invents a provider request id: the owner attaches one that really exists on the fal
/// queue, or confirms no charge with a recorded reason. Every action is audited.
/// </summary>
public sealed class ProviderReconciler
{
    private readonly AppDbContext _db;
    private readonly IVideoProvider? _videoProvider;
    private readonly IBudgetGuard _budgetGuard;
    private readonly FalOptions _options;
    private readonly TimeProvider _clock;

    public ProviderReconciler(
        AppDbContext db,
        IVideoProvider? videoProvider,
        IBudgetGuard budgetGuard,
        FalOptions options,
        TimeProvider clock)
    {
        _db = db ?? throw new ArgumentNullException(nameof(db));
        _videoProvider = videoProvider;
        _budgetGuard = budgetGuard ?? throw new ArgumentNullException(nameof(budgetGuard));
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _clock = clock ?? throw new ArgumentNullException(nameof(clock));
    }

    /// <summary>
    /// Re-links an uncertain attempt to the request that exists on the provider so polling
    /// resumes instead of resubmitting.
    /// </summary>
    public async Task AttachExistingRequestAsync(
        Guid providerRequestRowId,
        string actor,
        string providerRequestId,
        Uri statusUrl,
        Uri responseUrl,
        Uri? cancelUrl,
        CancellationToken ct)
    {
        RequireActor(actor);
        var row = await LoadAsync(providerRequestRowId, ct);

        if (row.Status is not ("submission_unknown" or "prepared" or "submitting"))
        {
            throw new ConflictException(
                "RECONCILE_NOT_UNKNOWN",
                "Only an uncertain provider attempt can be re-linked to an existing request.");
        }

        if (string.IsNullOrWhiteSpace(providerRequestId)
            || !IsQueueUrl(statusUrl, providerRequestId, "/status")
            || !IsQueueUrl(responseUrl, providerRequestId, string.Empty)
            || (cancelUrl is not null && !IsQueueUrl(cancelUrl, providerRequestId, "/cancel")))
        {
            throw new ValidationException(
                "RECONCILE_REFERENCE_INVALID",
                "The references must be fal queue URLs for exactly this request id and model.");
        }

        var previous = row.Status;
        row.ProviderRequestId = providerRequestId;
        row.StatusUrl = statusUrl.ToString();
        row.ResponseUrl = responseUrl.ToString();
        row.CancelUrl = cancelUrl?.ToString();
        row.Status = "submitted";
        row.SubmittedAt ??= _clock.GetUtcNow();
        AddAudit(actor, "provider_request.reconcile_attach", row, new { previous, providerRequestId });
        await _db.SaveChangesAsync(ct);
    }

    /// <summary>
    /// Owner confirms from the provider's own records that no billable work exists, which
    /// is the only way to release the reservation of an uncertain or cancelled request.
    /// </summary>
    public async Task ConfirmNoChargeAsync(
        Guid providerRequestRowId,
        string actor,
        string reason,
        CancellationToken ct)
    {
        RequireActor(actor);
        if (string.IsNullOrWhiteSpace(reason))
        {
            throw new ValidationException(
                "RECONCILE_REASON_REQUIRED",
                "A reason for confirming no charge is required.");
        }

        var row = await LoadAsync(providerRequestRowId, ct);
        var next = row.Status switch
        {
            "submission_unknown" or "prepared" or "submitting" => "failed_confirmed",
            "cancellation_requested" => "cancelled_confirmed",
            _ => throw new ConflictException(
                "RECONCILE_NOT_UNKNOWN",
                "No-charge confirmation applies to uncertain or cancelled requests only.")
        };

        var reservation = await _db.BudgetReservations
            .AsNoTracking()
            .SingleOrDefaultAsync(x => x.OperationId == row.ClientOperationId, ct);

        var previous = row.Status;
        row.Status = next;
        AddAudit(actor, "provider_request.reconcile_no_charge", row, new { previous, reason });
        await _db.SaveChangesAsync(ct);

        if (reservation is { Status: "reserved" })
        {
            await _budgetGuard.ReleaseAsync(
                reservation.Id, $"Owner confirmed no charge: {reason.Trim()}", ct);
        }
    }

    /// <summary>
    /// Asks the provider to cancel. A cancellation request is not a refund: the
    /// reservation stays held until the owner confirms the outcome.
    /// </summary>
    public async Task RequestCancelAsync(
        Guid providerRequestRowId,
        string actor,
        CancellationToken ct)
    {
        RequireActor(actor);
        if (_videoProvider is null)
        {
            throw new InvalidOperationException("A video provider is required to cancel.");
        }

        var row = await LoadAsync(providerRequestRowId, ct);
        if (row.Status is not ("submitted" or "in_progress")
            || string.IsNullOrWhiteSpace(row.ProviderRequestId)
            || row.StatusUrl is null
            || row.ResponseUrl is null)
        {
            throw new ConflictException(
                "CANCEL_NOT_ALLOWED",
                "Only a submitted request with provider references can be cancelled.");
        }

        var previous = row.Status;
        row.Status = "cancellation_requested";
        AddAudit(actor, "provider_request.cancel_requested", row, new { previous });
        await _db.SaveChangesAsync(ct);

        await _videoProvider.RequestCancelAsync(
            new ProviderSubmission(
                row.Provider,
                row.Model,
                row.ProviderRequestId,
                new Uri(row.StatusUrl),
                new Uri(row.ResponseUrl),
                row.CancelUrl is null ? null : new Uri(row.CancelUrl)),
            ct);
    }

    private bool IsQueueUrl(Uri url, string requestId, string suffix)
        => url is not null
            && url.IsAbsoluteUri
            && url.Scheme == Uri.UriSchemeHttps
            && string.Equals(url.Host, _options.BaseUrl.Host, StringComparison.OrdinalIgnoreCase)
            && string.IsNullOrEmpty(url.Query)
            && url.AbsolutePath.EndsWith(
                $"/requests/{requestId}{suffix}", StringComparison.Ordinal)
            && (suffix.Length > 0 || !url.AbsolutePath.EndsWith("/status", StringComparison.Ordinal));

    private async Task<ProviderRequest> LoadAsync(Guid id, CancellationToken ct)
        => await _db.ProviderRequests.SingleOrDefaultAsync(x => x.Id == id, ct)
            ?? throw new NotFoundException("PROVIDER_REQUEST_NOT_FOUND", "Provider request not found.");

    private static void RequireActor(string actor)
    {
        if (string.IsNullOrWhiteSpace(actor))
        {
            throw new ValidationException("RECONCILE_ACTOR_REQUIRED", "An actor is required.");
        }
    }

    private void AddAudit(string actor, string action, ProviderRequest row, object details)
        => _db.AuditEvents.Add(new AuditEvent
        {
            Id = Guid.NewGuid(),
            Actor = actor.Trim(),
            Action = action,
            TargetType = "provider_request",
            TargetId = row.Id,
            DetailsJson = JsonSerializer.Serialize(details, JsonDefaults.Options),
            CreatedAt = _clock.GetUtcNow()
        });
}
