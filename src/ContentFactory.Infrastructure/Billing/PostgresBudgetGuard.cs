using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using ContentFactory.Contracts;
using ContentFactory.Core.Abstractions;
using ContentFactory.Core.Exceptions;
using ContentFactory.Infrastructure.Persistence;
using ContentFactory.Infrastructure.Persistence.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Npgsql;

namespace ContentFactory.Infrastructure.Billing;

/// <summary>
/// PostgreSQL-backed spending guard. It locks budget rows in the fixed order
/// monthly -> daily -> campaign -> video, checks every cap and the live approval envelope
/// before writing a reservation, and keeps an append-only cost entry with a price snapshot
/// reference. No network provider is called from this class.
/// </summary>
public sealed class PostgresBudgetGuard : IBudgetGuard
{
    private const string MonthlyScope = "monthly";
    private const string DailyScope = "daily";
    private const string CampaignScope = "campaign";
    private const string VideoScope = "video";
    private const string RequestScope = "request";
    private const int MaxOperationIdLength = 256;

    private static readonly DateTimeOffset CampaignPeriodStart =
        new(2000, 1, 1, 0, 0, 0, TimeSpan.Zero);

    private static readonly DateTimeOffset CampaignPeriodEnd =
        new(2100, 1, 1, 0, 0, 0, TimeSpan.Zero);

    private readonly AppDbContext _db;
    private readonly BudgetOptions _options;
    private readonly LiveApprovalGuard _approvals;

    public PostgresBudgetGuard(
        AppDbContext db,
        BudgetOptions options,
        LiveApprovalGuard approvals)
    {
        _db = db;
        _options = options;
        _approvals = approvals;
        _options.Validate();
    }

    public Task<BudgetReservation> ReserveAsync(
        string operationId,
        Guid campaignId,
        PriceQuote quote,
        CancellationToken ct)
        => ReserveCoreAsync(operationId, campaignId, campaignId, quote, ct);

    /// <summary>
    /// Reserves a paid operation with an explicit video/revision scope. The three-argument
    /// interface overload uses the campaign id as the video scope; callers that know the
    /// revision id should use this overload so a per-video cap is meaningful.
    /// </summary>
    public Task<BudgetReservation> ReserveAsync(
        string operationId,
        Guid campaignId,
        Guid videoScopeId,
        PriceQuote quote,
        CancellationToken ct)
        => ReserveCoreAsync(operationId, campaignId, videoScopeId, quote, ct);

    public async Task SettleAsync(
        Guid reservationId,
        decimal amountUsd,
        string certainty,
        CancellationToken ct)
    {
        ValidateAmount(amountUsd);
        ValidateCertainty(certainty);

        await using var transaction = await _db.Database.BeginTransactionAsync(ct);
        try
        {
            var row = await LockReservationAsync(reservationId, ct)
                ?? throw new NotFoundException(
                    "BUDGET_RESERVATION_NOT_FOUND",
                    "The budget reservation does not exist.");

            if (row.Status == "released")
            {
                throw new ConflictException(
                    "BUDGET_RESERVATION_RELEASED",
                    "A released reservation cannot be settled.");
            }

            var now = await BillingDatabaseTime.GetNowAsync(_db, ct);

            if (row.Status == "settled")
            {
                var previous = row.SettledUsd ?? 0m;
                var delta = amountUsd - previous;
                if (delta == 0m)
                {
                    await transaction.CommitAsync(ct);
                    return;
                }

                await ApplySettlementAdjustmentAsync(
                    row,
                    delta,
                    certainty,
                    now,
                    ct);
                await transaction.CommitAsync(ct);
                return;
            }

            var allocations = ParseAllocations(row.AllocationsJson);
            await LockAllocationsAsync(allocations, ct);
            foreach (var allocation in allocations)
            {
                var affected = await _db.Database.ExecuteSqlInterpolatedAsync(
                    $"""
                    UPDATE app.budget_periods
                    SET reserved_usd = reserved_usd - {allocation.AmountUsd},
                        spent_usd = spent_usd + {amountUsd}
                    WHERE id = {allocation.BudgetPeriodId}
                      AND reserved_usd >= {allocation.AmountUsd}
                    """,
                    ct);

                if (affected != 1)
                {
                    throw new ConflictException(
                        "BUDGET_INVARIANT_BROKEN",
                        "A budget reservation no longer matches its allocated periods.");
                }
            }

            AddCostEntry(
                row,
                "settle",
                amountUsd,
                certainty,
                now,
                $"settle:{row.Id:N}");

            await _db.Database.ExecuteSqlInterpolatedAsync(
                $"""
                UPDATE app.budget_reservations
                SET status = 'settled',
                    settled_usd = {amountUsd},
                    settled_at = {now}
                WHERE id = {row.Id}
                """,
                ct);

            await _db.SaveChangesAsync(ct);
            await transaction.CommitAsync(ct);
        }
        catch
        {
            await transaction.RollbackAsync(ct);
            throw;
        }
    }

    public async Task ReleaseAsync(
        Guid reservationId,
        string confirmedReason,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(confirmedReason) || confirmedReason.Length > 512)
        {
            throw new ValidationException(
                "BUDGET_RELEASE_REASON_REQUIRED",
                "A confirmed no-charge reason is required to release a reservation.");
        }

        await using var transaction = await _db.Database.BeginTransactionAsync(ct);
        try
        {
            var row = await LockReservationAsync(reservationId, ct)
                ?? throw new NotFoundException(
                    "BUDGET_RESERVATION_NOT_FOUND",
                    "The budget reservation does not exist.");

            if (row.Status == "released")
            {
                await transaction.CommitAsync(ct);
                return;
            }

            if (row.Status == "settled")
            {
                throw new ConflictException(
                    "BUDGET_RESERVATION_SETTLED",
                    "A settled reservation cannot be released.");
            }

            var allocations = ParseAllocations(row.AllocationsJson);
            await LockAllocationsAsync(allocations, ct);
            foreach (var allocation in allocations)
            {
                var affected = await _db.Database.ExecuteSqlInterpolatedAsync(
                    $"""
                    UPDATE app.budget_periods
                    SET reserved_usd = reserved_usd - {allocation.AmountUsd}
                    WHERE id = {allocation.BudgetPeriodId}
                      AND reserved_usd >= {allocation.AmountUsd}
                    """,
                    ct);

                if (affected != 1)
                {
                    throw new ConflictException(
                        "BUDGET_INVARIANT_BROKEN",
                        "A budget reservation no longer matches its allocated periods.");
                }
            }

            var now = await BillingDatabaseTime.GetNowAsync(_db, ct);
            AddCostEntry(
                row,
                "release",
                row.ReservedUsd,
                "confirmed",
                now,
                $"release:{row.Id:N}");

            await _db.Database.ExecuteSqlInterpolatedAsync(
                $"""
                UPDATE app.budget_reservations
                SET status = 'released',
                    released_at = {now},
                    release_reason = {confirmedReason}
                WHERE id = {row.Id}
                """,
                ct);

            await _db.SaveChangesAsync(ct);
            await transaction.CommitAsync(ct);
        }
        catch
        {
            await transaction.RollbackAsync(ct);
            throw;
        }
    }

    /// <summary>
    /// Holds a reservation after an uncertain provider submission. The reserved amounts
    /// stay on every budget period until an owner reconciles the request; this method never
    /// converts an unknown bill into zero.
    /// </summary>
    public async Task MarkUnknownAsync(
        Guid reservationId,
        string reason,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(reason) || reason.Length > 512)
        {
            throw new ValidationException(
                "BUDGET_UNKNOWN_REASON_REQUIRED",
                "A reason is required to hold a reservation for reconciliation.");
        }

        await using var transaction = await _db.Database.BeginTransactionAsync(ct);
        try
        {
            var row = await LockReservationAsync(reservationId, ct)
                ?? throw new NotFoundException(
                    "BUDGET_RESERVATION_NOT_FOUND",
                    "The budget reservation does not exist.");

            if (row.Status == "unknown")
            {
                await transaction.CommitAsync(ct);
                return;
            }

            if (row.Status is "settled" or "released")
            {
                throw new ConflictException(
                    "BUDGET_RESERVATION_FINALIZED",
                    "A finalized reservation cannot be marked unknown.");
            }

            await _db.Database.ExecuteSqlInterpolatedAsync(
                $"""
                UPDATE app.budget_reservations
                SET status = 'unknown'
                WHERE id = {row.Id}
                """,
                ct);

            await transaction.CommitAsync(ct);
        }
        catch
        {
            await transaction.RollbackAsync(ct);
            throw;
        }
    }

    private async Task<BudgetReservation> ReserveCoreAsync(
        string operationId,
        Guid campaignId,
        Guid videoScopeId,
        PriceQuote quote,
        CancellationToken ct)
    {
        ValidateOperationId(operationId);
        if (campaignId == Guid.Empty)
        {
            throw new ValidationException(
                "BUDGET_CAMPAIGN_REQUIRED",
                "A campaign id is required to reserve paid work.");
        }

        if (videoScopeId == Guid.Empty)
        {
            throw new ValidationException(
                "BUDGET_VIDEO_SCOPE_REQUIRED",
                "A video scope id is required to reserve paid work.");
        }

        _options.EnsurePaidApisEnabled();
        ValidateQuoteShape(quote);

        await using var transaction = await _db.Database.BeginTransactionAsync(ct);
        try
        {
            var now = await BillingDatabaseTime.GetNowAsync(_db, ct);

            var existing = await _db.BudgetReservations
                .AsNoTracking()
                .SingleOrDefaultAsync(x => x.OperationId == operationId, ct);
            if (existing is not null)
            {
                var result = ExistingReservation(existing, quote);
                await transaction.CommitAsync(ct);
                return result;
            }

            await ValidateQuoteAgainstSnapshotAsync(quote, now, ct);

            if (quote.EstimatedUsd > _options.MaxSingleRequestUsd)
            {
                throw new BudgetExceededException(
                    RequestScope,
                    $"The quoted request cost {quote.EstimatedUsd:0.######} USD exceeds " +
                    $"MAX_SINGLE_REQUEST_USD={_options.MaxSingleRequestUsd:0.######}.");
            }

            var campaignBudget = await _db.Campaigns
                .AsNoTracking()
                .Where(x => x.Id == campaignId)
                .Select(x => (decimal?)x.BudgetUsd)
                .SingleOrDefaultAsync(ct);
            if (campaignBudget is null)
            {
                throw new NotFoundException(
                    "BUDGET_CAMPAIGN_NOT_FOUND",
                    "The campaign for this reservation does not exist.");
            }

            var descriptors = BuildDescriptors(
                now,
                campaignId,
                videoScopeId,
                campaignBudget.Value);
            var locked = new List<LockedBudgetRow>(descriptors.Count);
            foreach (var descriptor in descriptors)
            {
                await EnsureBudgetRowAsync(descriptor, ct);
                locked.Add(await LockBudgetRowAsync(descriptor, ct));
            }

            var approval = await _approvals.ValidateAsync(
                quote.Provider,
                quote.Model,
                ct);
            await EnsureApprovalLimitsAsync(approval, quote.ReservationUsd, now, ct);

            foreach (var row in locked)
            {
                if (row.SpentUsd + row.ReservedUsd + quote.ReservationUsd > row.LimitUsd)
                {
                    throw new BudgetExceededException(
                        row.ScopeType,
                        $"The {row.ScopeType} budget would exceed its limit of " +
                        $"{row.LimitUsd:0.######} USD.");
                }
            }

            foreach (var row in locked)
            {
                var affected = await _db.Database.ExecuteSqlInterpolatedAsync(
                    $"""
                    UPDATE app.budget_periods
                    SET reserved_usd = reserved_usd + {quote.ReservationUsd}
                    WHERE id = {row.Id}
                    """,
                    ct);

                if (affected != 1)
                {
                    throw new ConflictException(
                        "BUDGET_INVARIANT_BROKEN",
                        "A budget period disappeared while reserving.");
                }
            }

            var reservationId = Guid.NewGuid();
            var allocations = locked
                .Select(row => new BudgetAllocation(
                    row.ScopeType,
                    row.Id,
                    quote.ReservationUsd))
                .ToList();

            _db.BudgetReservations.Add(new BudgetReservationRecord
            {
                Id = reservationId,
                OperationId = operationId,
                CampaignId = campaignId,
                VideoScopeId = videoScopeId,
                Provider = quote.Provider,
                Model = quote.Model,
                PriceSnapshotId = quote.PriceSnapshotId,
                ApprovalId = approval.ApprovalId,
                ReservedUsd = quote.ReservationUsd,
                Status = "reserved",
                AllocationsJson = JsonSerializer.Serialize(allocations, JsonDefaults.Options),
                CreatedAt = now
            });

            _db.CostEntries.Add(new CostEntry
            {
                Id = Guid.NewGuid(),
                OperationId = operationId,
                IdempotencyKey = $"reserve:{Hash(operationId)}",
                EntryType = "reserve",
                AmountUsd = quote.ReservationUsd,
                Certainty = "estimated",
                ReservationId = reservationId,
                PriceSnapshotId = quote.PriceSnapshotId,
                CreatedAt = now
            });

            await _db.SaveChangesAsync(ct);
            await transaction.CommitAsync(ct);

            return new BudgetReservation(reservationId, operationId, quote.ReservationUsd);
        }
        catch (DbUpdateException exception) when (IsUniqueViolation(exception))
        {
            await transaction.RollbackAsync(ct);
            _db.ChangeTracker.Clear();

            var existing = await _db.BudgetReservations
                .AsNoTracking()
                .SingleOrDefaultAsync(x => x.OperationId == operationId, ct);

            if (existing is null)
            {
                throw new ConflictException(
                    "BUDGET_OPERATION_CONFLICT",
                    "The paid operation was already reserved with a conflicting cost record.");
            }

            return ExistingReservation(existing, quote);
        }
        catch
        {
            await transaction.RollbackAsync(ct);
            throw;
        }
    }

    private async Task EnsureApprovalLimitsAsync(
        LiveApprovalEnvelope approval,
        decimal reservationUsd,
        DateTimeOffset now,
        CancellationToken ct)
    {
        if (approval.ExpiresAt <= now)
        {
            throw new ApprovalStaleException(
                "APPROVAL_EXPIRED",
                "The live approval has expired according to database time.");
        }

        var count = await _db.BudgetReservations
            .AsNoTracking()
            .CountAsync(x => x.ApprovalId == approval.ApprovalId && x.Status != "released", ct);

        if (count >= approval.MaxRequestCount)
        {
            throw new ApprovalStaleException(
                "APPROVAL_REQUEST_LIMIT",
                "The live approval has reached maxRequestCount.");
        }

        var committed = await _db.BudgetReservations
            .AsNoTracking()
            .Where(x => x.ApprovalId == approval.ApprovalId && x.Status != "released")
            .SumAsync(x => x.SettledUsd ?? x.ReservedUsd, ct);

        if (committed + reservationUsd > approval.MaxTotalUsd)
        {
            throw new ApprovalStaleException(
                "APPROVAL_BUDGET_LIMIT",
                "The live approval would exceed maxTotalUsd.");
        }
    }

    private async Task ApplySettlementAdjustmentAsync(
        ReservationRow row,
        decimal delta,
        string certainty,
        DateTimeOffset now,
        CancellationToken ct)
    {
        var entryType = delta > 0m ? "adjustment" : "refund";
        var idempotencyKey = $"adjust:{row.Id:N}:{Hash($"{delta:0.######}:{certainty}")}";

        var alreadyApplied = await _db.CostEntries
            .AsNoTracking()
            .AnyAsync(x => x.IdempotencyKey == idempotencyKey, ct);
        if (alreadyApplied)
        {
            return;
        }

        var allocations = ParseAllocations(row.AllocationsJson);
        await LockAllocationsAsync(allocations, ct);
        foreach (var allocation in allocations)
        {
            var affected = await _db.Database.ExecuteSqlInterpolatedAsync(
                $"""
                UPDATE app.budget_periods
                SET spent_usd = spent_usd + {delta}
                WHERE id = {allocation.BudgetPeriodId}
                  AND spent_usd + {delta} >= 0
                """,
                ct);

            if (affected != 1)
            {
                throw new ConflictException(
                    "BUDGET_INVARIANT_BROKEN",
                    "A spending adjustment would make a budget period negative.");
            }
        }

        AddCostEntry(
            row,
            entryType,
            delta,
            certainty,
            now,
            idempotencyKey);

        await _db.Database.ExecuteSqlInterpolatedAsync(
            $"""
            UPDATE app.budget_reservations
            SET settled_usd = {(row.SettledUsd ?? 0m) + delta},
                settled_at = {now}
            WHERE id = {row.Id}
            """,
            ct);

        await _db.SaveChangesAsync(ct);
    }

    private async Task<List<LockedBudgetRow>> LockAllocationsAsync(
        IReadOnlyList<BudgetAllocation> allocations,
        CancellationToken ct)
    {
        var rows = new List<LockedBudgetRow>(allocations.Count);
        foreach (var allocation in allocations
            .OrderBy(x => ScopeRank(x.ScopeType))
            .ThenBy(x => x.BudgetPeriodId))
        {
            var row = await LockBudgetRowAsync(allocation.BudgetPeriodId, ct);
            if (row is null)
            {
                throw new ConflictException(
                    "BUDGET_INVARIANT_BROKEN",
                    "A budget period referenced by the reservation is missing.");
            }

            rows.Add(row);
        }

        return rows;
    }

    private async Task EnsureBudgetRowAsync(
        BudgetPeriodDescriptor descriptor,
        CancellationToken ct)
    {
        await using (var insert = CreateCommand(
            """
            INSERT INTO app.budget_periods
                (id, scope_type, scope_id, period_start, period_end,
                 limit_usd, reserved_usd, spent_usd)
            VALUES
                (@id, @scope_type, @scope_id, @period_start, @period_end,
                 @limit_usd, 0, 0)
            ON CONFLICT (scope_type, scope_id, period_start, period_end)
            DO NOTHING
            """))
        {
            insert.Parameters.AddWithValue("id", Guid.NewGuid());
            insert.Parameters.AddWithValue("scope_type", descriptor.ScopeType);
            insert.Parameters.AddWithValue("scope_id", descriptor.ScopeId);
            insert.Parameters.AddWithValue("period_start", descriptor.PeriodStart);
            insert.Parameters.AddWithValue("period_end", descriptor.PeriodEnd);
            insert.Parameters.AddWithValue("limit_usd", descriptor.LimitUsd);
            await insert.ExecuteNonQueryAsync(ct);
        }
    }

    private async Task<LockedBudgetRow> LockBudgetRowAsync(
        BudgetPeriodDescriptor descriptor,
        CancellationToken ct)
    {
        await using var command = CreateCommand(
            """
            SELECT id
            FROM app.budget_periods
            WHERE scope_type = @scope_type
              AND scope_id = @scope_id
              AND period_start = @period_start
              AND period_end = @period_end
            """);
        command.Parameters.AddWithValue("scope_type", descriptor.ScopeType);
        command.Parameters.AddWithValue("scope_id", descriptor.ScopeId);
        command.Parameters.AddWithValue("period_start", descriptor.PeriodStart);
        command.Parameters.AddWithValue("period_end", descriptor.PeriodEnd);

        var idValue = await command.ExecuteScalarAsync(ct);
        if (idValue is not Guid id)
        {
            throw new ConflictException(
                "BUDGET_INVARIANT_BROKEN",
                "A budget period could not be found after creation.");
        }

        return await LockBudgetRowAsync(id, ct)
            ?? throw new ConflictException(
                "BUDGET_INVARIANT_BROKEN",
                "A budget period could not be locked.");
    }

    private async Task<LockedBudgetRow?> LockBudgetRowAsync(
        Guid id,
        CancellationToken ct)
    {
        await using var command = CreateCommand(
            """
            SELECT id, scope_type, limit_usd, reserved_usd, spent_usd
            FROM app.budget_periods
            WHERE id = @id
            FOR UPDATE
            """);
        command.Parameters.AddWithValue("id", id);

        await using var reader = await command.ExecuteReaderAsync(ct);
        if (!await reader.ReadAsync(ct))
        {
            return null;
        }

        return new LockedBudgetRow(
            reader.GetGuid(0),
            reader.GetString(1),
            reader.GetDecimal(2),
            reader.GetDecimal(3),
            reader.GetDecimal(4));
    }

    private async Task<ReservationRow?> LockReservationAsync(
        Guid id,
        CancellationToken ct)
    {
        await using var command = CreateCommand(
            """
            SELECT id, operation_id, status, reserved_usd, settled_usd,
                   allocations_json, price_snapshot_id, provider, model,
                   campaign_id, approval_id
            FROM app.budget_reservations
            WHERE id = @id
            FOR UPDATE
            """);
        command.Parameters.AddWithValue("id", id);

        await using var reader = await command.ExecuteReaderAsync(ct);
        if (!await reader.ReadAsync(ct))
        {
            return null;
        }

        return new ReservationRow(
            reader.GetGuid(0),
            reader.GetString(1),
            reader.GetString(2),
            reader.GetDecimal(3),
            reader.IsDBNull(4) ? null : reader.GetDecimal(4),
            reader.GetString(5),
            reader.GetGuid(6),
            reader.GetString(7),
            reader.GetString(8),
            reader.GetGuid(9),
            reader.GetString(10));
    }

    private async Task ValidateQuoteAgainstSnapshotAsync(
        PriceQuote quote,
        DateTimeOffset now,
        CancellationToken ct)
    {
        if (quote.ValidUntil <= now)
        {
            throw new PriceStaleException("The price quote has expired.");
        }

        var snapshot = await _db.PriceSnapshots
            .AsNoTracking()
            .SingleOrDefaultAsync(x => x.Id == quote.PriceSnapshotId, ct);

        if (snapshot is null ||
            snapshot.Provider != quote.Provider ||
            snapshot.Model != quote.Model ||
            snapshot.VerifiedAt > now ||
            snapshot.ValidUntil <= now)
        {
            throw new PriceStaleException(
                "The price snapshot is missing, unverified or expired.");
        }

        var expectedReservation = decimal.Round(
            snapshot.UnitPriceUsd * _options.ReservationMultiplier,
            6,
            MidpointRounding.AwayFromZero);

        if (snapshot.UnitPriceUsd != quote.EstimatedUsd ||
            expectedReservation != quote.ReservationUsd)
        {
            throw new PriceStaleException(
                "The price quote does not match the persisted price snapshot.");
        }

        if (quote.ReservationUsd <= 0m)
        {
            throw new ValidationException(
                "BUDGET_QUOTE_INVALID",
                "A paid reservation must be greater than zero.");
        }
    }

    private static void ValidateQuoteShape(PriceQuote quote)
    {
        if (string.IsNullOrWhiteSpace(quote.Provider) ||
            string.IsNullOrWhiteSpace(quote.Model) ||
            quote.PriceSnapshotId == Guid.Empty)
        {
            throw new ValidationException(
                "BUDGET_QUOTE_INVALID",
                "Provider, model and price snapshot id are required.");
        }

        if (quote.EstimatedUsd < 0m || quote.ReservationUsd <= 0m)
        {
            throw new ValidationException(
                "BUDGET_QUOTE_INVALID",
                "A paid reservation must be greater than zero.");
        }
    }

    private static void ValidateOperationId(string operationId)
    {
        if (string.IsNullOrWhiteSpace(operationId) ||
            operationId.Length > MaxOperationIdLength)
        {
            throw new ValidationException(
                "BUDGET_OPERATION_INVALID",
                "An operation id between 1 and 256 characters is required.");
        }
    }

    private static void ValidateAmount(decimal amountUsd)
    {
        if (amountUsd < 0m)
        {
            throw new ValidationException(
                "BUDGET_SETTLEMENT_INVALID",
                "A settlement amount cannot be negative.");
        }
    }

    private static void ValidateCertainty(string certainty)
    {
        if (certainty is not ("confirmed" or "estimated"))
        {
            throw new ValidationException(
                "BUDGET_CERTAINTY_INVALID",
                "Settlement certainty must be 'confirmed' or 'estimated'.");
        }
    }

    private static BudgetReservation ExistingReservation(
        BudgetReservationRecord existing,
        PriceQuote quote)
    {
        if (existing.Provider != quote.Provider ||
            existing.Model != quote.Model ||
            existing.ReservedUsd != quote.ReservationUsd)
        {
            throw new ConflictException(
                "BUDGET_OPERATION_CONFLICT",
                "The operation id is already reserved with a different provider, model or amount.");
        }

        if (existing.Status is not ("reserved" or "unknown"))
        {
            throw new ConflictException(
                "BUDGET_OPERATION_FINALIZED",
                "The operation id belongs to a finalized reservation.");
        }

        return new BudgetReservation(
            existing.Id,
            existing.OperationId,
            existing.ReservedUsd);
    }

    private static List<BudgetAllocation> ParseAllocations(string json)
    {
        List<BudgetAllocation>? allocations;
        try
        {
            allocations = JsonSerializer.Deserialize<List<BudgetAllocation>>(
                json,
                JsonDefaults.Options);
        }
        catch (JsonException exception)
        {
            throw new InvalidOperationException(
                "The budget reservation allocations are not valid JSON.",
                exception);
        }

        if (allocations is null || allocations.Count == 0)
        {
            throw new InvalidOperationException(
                "The budget reservation has no allocated budget periods.");
        }

        return allocations;
    }

    private static int ScopeRank(string scopeType)
        => scopeType switch
        {
            MonthlyScope => 0,
            DailyScope => 1,
            CampaignScope => 2,
            VideoScope => 3,
            _ => 99
        };

    private List<BudgetPeriodDescriptor> BuildDescriptors(
        DateTimeOffset now,
        Guid campaignId,
        Guid videoScopeId,
        decimal campaignBudget)
    {
        var utc = now.UtcDateTime;
        var dailyStart = new DateTimeOffset(
            utc.Year, utc.Month, utc.Day, 0, 0, 0, TimeSpan.Zero);
        var monthlyStart = new DateTimeOffset(
            utc.Year, utc.Month, 1, 0, 0, 0, TimeSpan.Zero);

        return
        [
            new(
                MonthlyScope,
                Guid.Empty,
                monthlyStart,
                monthlyStart.AddMonths(1),
                _options.MonthlyBudgetUsd),
            new(
                DailyScope,
                Guid.Empty,
                dailyStart,
                dailyStart.AddDays(1),
                _options.DailyBudgetUsd),
            new(
                CampaignScope,
                campaignId,
                CampaignPeriodStart,
                CampaignPeriodEnd,
                campaignBudget),
            new(
                VideoScope,
                videoScopeId,
                CampaignPeriodStart,
                CampaignPeriodEnd,
                _options.MaxVideoCostUsd)
        ];
    }

    private void AddCostEntry(
        ReservationRow row,
        string entryType,
        decimal amountUsd,
        string certainty,
        DateTimeOffset now,
        string idempotencyKey)
    {
        _db.CostEntries.Add(new CostEntry
        {
            Id = Guid.NewGuid(),
            OperationId = row.OperationId,
            IdempotencyKey = idempotencyKey,
            EntryType = entryType,
            AmountUsd = amountUsd,
            Certainty = certainty,
            ReservationId = row.Id,
            PriceSnapshotId = row.PriceSnapshotId,
            CreatedAt = now
        });
    }

    private NpgsqlCommand CreateCommand(string sql)
    {
        var connection = (NpgsqlConnection)_db.Database.GetDbConnection();
        var transaction = (NpgsqlTransaction?)_db.Database.CurrentTransaction?.GetDbTransaction();
        return new NpgsqlCommand(sql, connection, transaction);
    }

    private static bool IsUniqueViolation(DbUpdateException exception)
        => exception.InnerException is PostgresException
        {
            SqlState: PostgresErrorCodes.UniqueViolation
        };

    private static string Hash(string value)
        => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)))
            .ToLowerInvariant();

    private sealed record BudgetPeriodDescriptor(
        string ScopeType,
        Guid ScopeId,
        DateTimeOffset PeriodStart,
        DateTimeOffset PeriodEnd,
        decimal LimitUsd);

    private sealed record LockedBudgetRow(
        Guid Id,
        string ScopeType,
        decimal LimitUsd,
        decimal ReservedUsd,
        decimal SpentUsd);

    private sealed record ReservationRow(
        Guid Id,
        string OperationId,
        string Status,
        decimal ReservedUsd,
        decimal? SettledUsd,
        string AllocationsJson,
        Guid PriceSnapshotId,
        string Provider,
        string Model,
        Guid CampaignId,
        string ApprovalId);
}

public sealed record BudgetAllocation(
    string ScopeType,
    Guid BudgetPeriodId,
    decimal AmountUsd);
