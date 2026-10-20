using System.Text.Json;
using ContentFactory.Contracts;
using ContentFactory.Core.Exceptions;
using ContentFactory.Core.Products;
using ContentFactory.Infrastructure.Media;
using ContentFactory.Infrastructure.Persistence;
using ContentFactory.Infrastructure.Persistence.Entities;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace ContentFactory.Infrastructure.Jobs;

/// <summary>
/// The trusted input for one campaign run. Mode and duration are validated against the
/// campaign, cloud scene limits require a live paid-work approval, and the product facts
/// are read from PostgreSQL rather than accepted from the client.
/// </summary>
public sealed record RunEnqueueCommand(
    Guid CampaignId,
    string IdempotencyKey,
    VideoMode Mode,
    int TargetDurationMs,
    bool AutoGenerateVoice,
    int CloudVideoSceneLimit,
    bool AllowPaidCloud = false);

public sealed record RunEnqueued(Guid JobId, Guid RevisionId);

/// <summary>
/// Creates one immutable content revision and its first job in a single transaction.
/// A repeated idempotency key with the same canonical body returns the original pair; the
/// same key with a different body is a conflict. No provider is called here.
/// </summary>
public sealed class RunEnqueuer
{
    public const string JobType = "plan_script";
    public const string PromptVersion = "script-planner.v1";

    private readonly AppDbContext _db;
    private readonly TimeProvider _clock;

    public RunEnqueuer(AppDbContext db, TimeProvider clock)
    {
        _db = db;
        _clock = clock;
    }

    public async Task<RunEnqueued> EnqueueAsync(
        RunEnqueueCommand command,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(command);

        if (command.CampaignId == Guid.Empty)
        {
            throw Invalid("A campaign id is required.");
        }

        ValidateIdempotencyKey(command.IdempotencyKey);

        if (command.TargetDurationMs is < 15000 or > 35000)
        {
            throw Invalid("The target duration must be between 15000 and 35000 milliseconds.");
        }

        if (command.CloudVideoSceneLimit < 0)
        {
            throw Invalid("The cloud video scene limit cannot be negative.");
        }

        var requestHash = ComputeRequestHash(command);
        var dedupeKey = BuildDedupeKey(command.CampaignId, command.IdempotencyKey);

        var existing = await _db.Jobs
            .AsNoTracking()
            .SingleOrDefaultAsync(x => x.DedupeKey == dedupeKey, ct);

        if (existing is not null)
        {
            return ResolveExisting(existing, requestHash);
        }

        var campaign = await _db.Campaigns
            .AsNoTracking()
            .SingleOrDefaultAsync(x => x.Id == command.CampaignId, ct)
            ?? throw new NotFoundException("CAMPAIGN_NOT_FOUND", "Campaign not found.");

        if (campaign.Status is not ("draft" or "active"))
        {
            throw new ConflictException(
                "CAMPAIGN_NOT_RUNNABLE",
                "Only draft or active campaigns can create a run.");
        }

        var product = await _db.Products
            .AsNoTracking()
            .SingleOrDefaultAsync(x => x.Id == campaign.ProductId, ct)
            ?? throw new NotFoundException("PRODUCT_NOT_FOUND", "Product not found.");

        if (product.ArchivedAt is not null)
        {
            throw new ConflictException(
                "PRODUCT_ARCHIVED",
                "An archived product cannot create a new run.");
        }

        if (campaign.Mode != command.Mode)
        {
            throw Invalid("The run mode does not match the campaign mode.");
        }

        if (campaign.TargetDurationMs != command.TargetDurationMs)
        {
            throw Invalid("The run target duration does not match the campaign.");
        }

        if (!string.Equals(campaign.Locale, product.Locale, StringComparison.Ordinal))
        {
            throw Invalid("The campaign locale does not match the product locale.");
        }

        if (command.CloudVideoSceneLimit > 0 && !command.AllowPaidCloud)
        {
            throw new ConflictException(
                "CLOUD_NOT_APPROVED",
                "Cloud video scenes require a live paid-work approval.");
        }

        var now = _clock.GetUtcNow();

        var claimRows = await _db.ProductClaims
            .AsNoTracking()
            .Where(x => x.ProductId == product.Id
                && x.Status == "approved"
                && (x.ExpiresAt == null || x.ExpiresAt > now))
            .OrderBy(x => x.Id)
            .ToListAsync(ct);

        if (claimRows.Count == 0)
        {
            throw new ConflictException(
                "RUN_FACTS_UNAPPROVED",
                "A run requires at least one approved and unexpired product claim.");
        }

        var assetRows = await (
                from link in _db.ProductAssets.AsNoTracking()
                join asset in _db.Assets.AsNoTracking() on link.AssetId equals asset.Id
                where link.ProductId == product.Id
                orderby link.DisplayOrder, asset.Id
                select asset)
            .ToListAsync(ct);

        if (assetRows.Any(asset =>
                !string.Equals(asset.RightsStatus, "owner_attested", StringComparison.Ordinal)
                || string.IsNullOrWhiteSpace(asset.SourceReference)))
        {
            throw new ConflictException(
                "RUN_ASSET_RIGHTS_UNATTESTED",
                "Every attached asset needs an owner attestation and source reference.");
        }

        var snapshot = ProductSnapshotBuilder.Build(
            product.Id,
            product.Version,
            product.Title,
            product.Market,
            product.Locale,
            claimRows.Select(x => new ApprovedClaim(x.Id, x.Text, x.SourceReference)).ToArray(),
            assetRows.Select(x => new AssetRef(
                x.Id,
                x.Sha256,
                x.RelativePath,
                x.Kind,
                x.Mime,
                x.Bytes,
                x.DurationMs)).ToArray(),
            now);

        var revisionId = Guid.NewGuid();
        var jobId = Guid.NewGuid();
        var scriptRequest = new ScriptRequest(
            revisionId,
            snapshot,
            campaign.Locale,
            campaign.Mode,
            campaign.TargetDurationMs,
            PromptVersion);

        var payloadJson = JsonSerializer.Serialize(
            new RunJobPayload(scriptRequest, requestHash, command.AutoGenerateVoice,
                command.CloudVideoSceneLimit),
            JsonDefaults.Options);

        await using var transaction = await _db.Database.BeginTransactionAsync(ct);

        // Serialise revision numbering for this campaign. A concurrent run waits for the
        // campaign row instead of racing the unique (campaign_id, revision) index.
        await _db.Database.ExecuteSqlInterpolatedAsync(
            $"SELECT 1 FROM app.campaigns WHERE id = {campaign.Id} FOR UPDATE",
            ct);

        var lastRevision = await _db.ContentRevisions
            .Where(x => x.CampaignId == campaign.Id)
            .MaxAsync(x => (int?)x.Revision, ct);

        var revision = new ContentRevision
        {
            Id = revisionId,
            CampaignId = campaign.Id,
            Revision = (lastRevision ?? 0) + 1,
            ProductSnapshotJson = JsonSerializer.Serialize(snapshot, JsonDefaults.Options),
            PromptVersion = PromptVersion,
            Status = "draft",
            CreatedAt = now
        };

        var job = new Job
        {
            Id = jobId,
            JobType = JobType,
            PayloadJson = payloadJson,
            PayloadVersion = 1,
            DedupeKey = dedupeKey,
            Lane = JobLane.HeavyLocal,
            Status = "queued",
            Priority = 100,
            RunAfter = now,
            Attempts = 0,
            MaxAttempts = 5,
            Fence = 0,
            CreatedAt = now,
            UpdatedAt = now
        };

        _db.ContentRevisions.Add(revision);
        _db.Jobs.Add(job);

        try
        {
            await _db.SaveChangesAsync(ct);
            await transaction.CommitAsync(ct);
        }
        catch (DbUpdateException exception) when (IsUniqueViolation(exception))
        {
            await transaction.RollbackAsync(ct);
            _db.ChangeTracker.Clear();

            var winner = await _db.Jobs
                .AsNoTracking()
                .SingleOrDefaultAsync(x => x.DedupeKey == dedupeKey, ct);

            if (winner is not null)
            {
                return ResolveExisting(winner, requestHash);
            }

            throw;
        }

        return new RunEnqueued(jobId, revisionId);
    }

    private static RunEnqueued ResolveExisting(Job job, string requestHash)
    {
        string? storedHash = null;
        Guid? revisionId = null;

        try
        {
            using var document = JsonDocument.Parse(job.PayloadJson);
            var root = document.RootElement;
            if (root.TryGetProperty("requestHash", out var hashElement))
            {
                storedHash = hashElement.GetString();
            }

            if (root.TryGetProperty("scriptRequest", out var requestElement)
                && requestElement.TryGetProperty("revisionId", out var revisionElement)
                && Guid.TryParse(revisionElement.GetString(), out var parsedRevision))
            {
                revisionId = parsedRevision;
            }
        }
        catch (JsonException)
        {
            throw new ConflictException(
                "IDEMPOTENCY_STATE_INVALID",
                "The stored idempotent request is unreadable.");
        }

        if (!string.Equals(storedHash, requestHash, StringComparison.Ordinal)
            || revisionId is null)
        {
            throw new ConflictException(
                "IDEMPOTENCY_CONFLICT",
                "This idempotency key was already used with a different request body.");
        }

        return new RunEnqueued(job.Id, revisionId.Value);
    }

    private static string ComputeRequestHash(RunEnqueueCommand command)
    {
        var fingerprint = new RunRequestFingerprint(
            ToContractMode(command.Mode),
            command.TargetDurationMs,
            command.AutoGenerateVoice,
            command.CloudVideoSceneLimit);

        return ManifestHasher.HashCanonicalJson(
            JsonSerializer.Serialize(fingerprint, JsonDefaults.Options));
    }

    private static string ToContractMode(VideoMode mode) => mode switch
    {
        VideoMode.TemplateOnly => "template_only",
        VideoMode.HybridMotion => "hybrid_motion",
        _ => throw Invalid("The video mode is invalid.")
    };

    private static string BuildDedupeKey(Guid campaignId, string idempotencyKey)
        => $"campaign-run:{campaignId:N}:{idempotencyKey}";

    private static void ValidateIdempotencyKey(string? idempotencyKey)
    {
        if (string.IsNullOrEmpty(idempotencyKey)
            || idempotencyKey.Length is < 16 or > 128
            || idempotencyKey.Any(character => character is < ' ' or > '~'))
        {
            throw Invalid("The Idempotency-Key must be 16-128 printable ASCII characters.");
        }
    }

    private static bool IsUniqueViolation(DbUpdateException exception)
        => exception.InnerException is PostgresException { SqlState: "23505" };

    private static ValidationException Invalid(string message)
        => new("RUN_REQUEST_INVALID", message);

    private sealed record RunRequestFingerprint(
        string Mode,
        int TargetDurationMs,
        bool AutoGenerateVoice,
        int CloudVideoSceneLimit);

    private sealed record RunJobPayload(
        ScriptRequest ScriptRequest,
        string RequestHash,
        bool AutoGenerateVoice,
        int CloudVideoSceneLimit);
}
