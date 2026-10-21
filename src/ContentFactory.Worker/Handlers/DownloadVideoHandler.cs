using ContentFactory.Contracts;
using ContentFactory.Core.Abstractions;
using ContentFactory.Core.Exceptions;
using ContentFactory.Infrastructure.Persistence;
using ContentFactory.Infrastructure.Persistence.Entities;
using ContentFactory.Infrastructure.Security;
using Microsoft.EntityFrameworkCore;

namespace ContentFactory.Worker.Handlers;

/// <summary>
/// Fetches a completed generation exactly once. The signed output URL is re-read from the
/// existing request (never stored), so an expired URL refreshes without resubmitting. The
/// result row records the asset id; a replay after a crash finds the same content-addressed
/// asset and settles the reservation idempotently.
/// </summary>
public sealed class DownloadVideoHandler : IJobHandler
{
    public const string JobTypeName = "download_video";

    private static readonly TimeSpan RetryInterval = TimeSpan.FromSeconds(30);

    private readonly AppDbContext _db;
    private readonly IVideoProvider _videoProvider;
    private readonly SafeMediaDownloader _downloader;
    private readonly IAssetStore _store;
    private readonly IBudgetGuard _budgetGuard;
    private readonly TimeProvider _clock;

    public DownloadVideoHandler(
        AppDbContext db,
        IVideoProvider videoProvider,
        SafeMediaDownloader downloader,
        IAssetStore store,
        IBudgetGuard budgetGuard,
        TimeProvider clock)
    {
        _db = db ?? throw new ArgumentNullException(nameof(db));
        _videoProvider = videoProvider ?? throw new ArgumentNullException(nameof(videoProvider));
        _downloader = downloader ?? throw new ArgumentNullException(nameof(downloader));
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _budgetGuard = budgetGuard ?? throw new ArgumentNullException(nameof(budgetGuard));
        _clock = clock ?? throw new ArgumentNullException(nameof(clock));
    }

    public string JobType => JobTypeName;

    public async Task<JobOutcome> ExecuteAsync(LeasedJob job, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(job);
        ct.ThrowIfCancellationRequested();

        var operationId = ProviderJobPayload.ReadOperationId(job.PayloadJson);
        if (operationId is null)
        {
            return Blocked("DOWNLOAD_PAYLOAD_INVALID");
        }

        var row = await _db.ProviderRequests
            .SingleOrDefaultAsync(x => x.ClientOperationId == operationId, ct);
        if (row is null)
        {
            return Blocked("PROVIDER_REQUEST_NOT_FOUND");
        }

        if (row.OutputAssetId is { } existingAsset)
        {
            await SettleAsync(operationId, ct);
            return new JobOutcome("succeeded", null, null, existingAsset.ToString("D"));
        }

        if (row.Status is "failed_confirmed" or "cancelled_confirmed")
        {
            return new JobOutcome("failed", null, "FAL_GENERATION_FAILED", row.Id.ToString("D"));
        }

        if (ProviderJobPayload.NeedsReconciliation(row))
        {
            return Blocked("UNKNOWN_REQUIRES_RECONCILIATION");
        }

        var submission = ProviderJobPayload.ToSubmission(row);
        AssetRef? asset = null;

        for (var attempt = 0; attempt < 2 && asset is null; attempt++)
        {
            ProviderPollResult result;
            try
            {
                result = await _videoProvider.PollAsync(submission, ct);
            }
            catch (ProviderRejectedException exception)
            {
                return Blocked(exception.Code);
            }
            catch (ValidationException exception)
            {
                return Blocked(exception.Code);
            }

            row.LastCheckedAt = _clock.GetUtcNow();

            if (result.Status is ProviderRunStatus.InProgress or ProviderRunStatus.Submitted)
            {
                await _db.SaveChangesAsync(ct);
                return Retry(null, row);
            }

            if (result.Status == ProviderRunStatus.FailedConfirmed)
            {
                row.Status = "failed_confirmed";
                await _db.SaveChangesAsync(ct);
                return new JobOutcome(
                    "failed", null, result.ErrorCode ?? "FAL_GENERATION_FAILED", row.Id.ToString("D"));
            }

            if (result.Status != ProviderRunStatus.Succeeded)
            {
                await _db.SaveChangesAsync(ct);
                return Blocked(result.ErrorCode ?? "FAL_STATUS_UNRECOGNISED");
            }

            if (result.OutputUrl is null)
            {
                await _db.SaveChangesAsync(ct);
                return Retry("FAL_OUTPUT_MISSING", row);
            }

            try
            {
                asset = await _downloader.DownloadAsync(
                    result.OutputUrl, _store, AssetKind.Video, ct);
            }
            catch (MediaUrlExpiredException)
            {
                // Loop once more: the next poll reads a fresh URL from the same request.
            }
            catch (ValidationException exception)
            {
                await _db.SaveChangesAsync(ct);
                return Blocked(exception.Code);
            }
            catch (HttpRequestException)
            {
                await _db.SaveChangesAsync(ct);
                return Retry("MEDIA_DOWNLOAD_FAILED", row);
            }
        }

        if (asset is null)
        {
            await _db.SaveChangesAsync(ct);
            return Retry("MEDIA_URL_EXPIRED", row);
        }

        row.OutputAssetId = asset.Id;
        row.Status = "succeeded";
        await _db.SaveChangesAsync(ct);
        await SettleAsync(operationId, ct);
        return new JobOutcome("succeeded", null, null, asset.Id.ToString("D"));
    }

    /// <summary>
    /// A delivered output means the provider did the billed work. Settling at the price
    /// snapshot is idempotent, so a replay after a crash is safe.
    /// </summary>
    private async Task SettleAsync(string operationId, CancellationToken ct)
    {
        var reservation = await _db.BudgetReservations
            .AsNoTracking()
            .SingleOrDefaultAsync(x => x.OperationId == operationId, ct);
        if (reservation is null || reservation.Status != "reserved")
        {
            return;
        }

        var unitPrice = await _db.PriceSnapshots
            .AsNoTracking()
            .Where(x => x.Id == reservation.PriceSnapshotId)
            .Select(x => x.UnitPriceUsd)
            .SingleAsync(ct);
        await _budgetGuard.SettleAsync(reservation.Id, unitPrice, "estimated", ct);
    }

    private JobOutcome Retry(string? code, ProviderRequest row)
        => new("retry_wait", _clock.GetUtcNow() + RetryInterval, code, row.Id.ToString("D"));

    private static JobOutcome Blocked(string code) => new("blocked", null, code, null);
}
