using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using ContentFactory.Contracts;
using ContentFactory.Core.Abstractions;
using ContentFactory.Core.Campaigns;
using ContentFactory.Core.Exceptions;
using ContentFactory.Infrastructure.Jobs;
using ContentFactory.Infrastructure.Media;
using ContentFactory.Infrastructure.Persistence;
using ContentFactory.Infrastructure.Persistence.Entities;
using Microsoft.EntityFrameworkCore;

namespace ContentFactory.Infrastructure.Review;

/// <summary>
/// Owner review. An approval is bound to exactly one manifest hash and is only accepted for
/// a version that passed technical QA and is still waiting for review. Reviewer identity is
/// supplied by the authenticated session, never by the request body. There is no bulk or
/// "approve all" path. Editing a caption creates a new revision whose new manifest must be
/// reviewed again; the earlier approval stays attached to its own hash only.
/// </summary>
public sealed class ReviewService : IReviewService
{
    public static readonly IReadOnlyList<ReviewChecklistItem> RequiredChecklist =
    [
        new("product_unchanged", "Product shape, colour, logo and use are not changed misleadingly"),
        new("claims_match_evidence", "Every claim matches its evidence; no invented testimonial"),
        new("narration_and_captions_correct", "Narration and captions read correctly"),
        new("rights_confirmed", "Image, audio, music, font and cloud-use rights are confirmed"),
        new("disclosure_prepared", "Ad, affiliate and AI disclosure is prepared for this workflow"),
        new("offer_checked", "Offer, price and stock were checked within the last 24 hours"),
        new("version_matches_preview", "This exact MP4 version matches the preview and hash")
    ];

    private const string AiMotionWarningText =
        "This video contains AI-generated motion. Check that the product shape, colour and logo are unchanged.";

    private readonly AppDbContext _db;
    private readonly PostgresJobStore _jobs;
    private readonly TimeProvider _clock;

    public ReviewService(AppDbContext db, PostgresJobStore jobs, TimeProvider clock)
    {
        _db = db ?? throw new ArgumentNullException(nameof(db));
        _jobs = jobs ?? throw new ArgumentNullException(nameof(jobs));
        _clock = clock ?? throw new ArgumentNullException(nameof(clock));
    }

    public async Task RecordAsync(ReviewDecision decision, Guid reviewerId, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(decision);
        await RequireOwnerAsync(reviewerId, ct);

        if (decision.VideoVersionId == Guid.Empty || string.IsNullOrWhiteSpace(decision.ManifestSha256))
        {
            throw new ValidationException("REVIEW_REQUEST_INVALID", "A version id and manifest hash are required.");
        }

        var comment = decision.Comment?.Trim();
        if (!decision.Approved && string.IsNullOrEmpty(comment))
        {
            throw new ValidationException("REVIEW_COMMENT_REQUIRED", "A rejection needs a comment.");
        }

        if (comment is { Length: > 2000 })
        {
            throw new ValidationException("REVIEW_COMMENT_TOO_LONG", "The comment must be 2000 characters or fewer.");
        }

        await using var transaction = await _db.Database.BeginTransactionAsync(ct);

        // Serialise concurrent decisions on this version; the second one sees the result.
        await _db.Database.ExecuteSqlInterpolatedAsync(
            $"SELECT 1 FROM app.video_versions WHERE id = {decision.VideoVersionId} FOR UPDATE", ct);
        var version = await _db.VideoVersions
            .SingleOrDefaultAsync(x => x.Id == decision.VideoVersionId, ct)
            ?? throw new NotFoundException("VIDEO_VERSION_NOT_FOUND", "Video version not found.");

        if (version.Status is "approved" or "rejected")
        {
            throw new ConflictException("REVIEW_ALREADY_DECIDED", "This version already has a decision.");
        }

        if (version.Status != "awaiting_review")
        {
            throw new ConflictException("REVIEW_NOT_READY", "Only a version awaiting review can be decided.");
        }

        if (!QaPassed(version.QaJson))
        {
            throw new ConflictException("REVIEW_QA_NOT_PASSED", "Technical QA did not pass for this version.");
        }

        if (string.IsNullOrWhiteSpace(version.ManifestJson)
            || !string.Equals(version.ManifestSha256, decision.ManifestSha256, StringComparison.Ordinal)
            || !string.Equals(RecomputeHash(version.ManifestJson), decision.ManifestSha256, StringComparison.Ordinal))
        {
            throw new ApprovalStaleException(
                "APPROVAL_HASH_MISMATCH",
                "The manifest hash does not match this version; review the current version again.");
        }

        var checklist = decision.Checklist ?? new Dictionary<string, bool>();
        if (decision.Approved
            && RequiredChecklist.Any(item => !checklist.TryGetValue(item.Key, out var ticked) || !ticked))
        {
            throw new ValidationException(
                "REVIEW_CHECKLIST_INCOMPLETE", "Every checklist item must be confirmed to approve.");
        }

        var now = _clock.GetUtcNow();
        _db.Approvals.Add(new Approval
        {
            Id = Guid.NewGuid(),
            VideoVersionId = version.Id,
            ManifestSha256 = version.ManifestSha256!,
            ReviewerId = reviewerId,
            Decision = decision.Approved ? "approved" : "rejected",
            ChecklistJson = JsonSerializer.Serialize(
                new { checklist, comment }, JsonDefaults.Options),
            DecidedAt = now
        });
        version.Status = decision.Approved ? "approved" : "rejected";
        _db.AuditEvents.Add(new AuditEvent
        {
            Id = Guid.NewGuid(),
            Actor = reviewerId.ToString("D"),
            Action = decision.Approved ? "video.approved" : "video.rejected",
            TargetType = "video_version",
            TargetId = version.Id,
            DetailsJson = JsonSerializer.Serialize(
                new { manifestSha256 = version.ManifestSha256 }, JsonDefaults.Options),
            CreatedAt = now
        });
        await _db.SaveChangesAsync(ct);
        await transaction.CommitAsync(ct);
    }

    /// <summary>
    /// Creates a new revision with one edited caption and queues only the compile stage; the
    /// narration and images are reused, so no provider is called and nothing is billed.
    /// </summary>
    public async Task<CaptionEditResult> EditCaptionAsync(
        Guid videoVersionId, string sceneId, string caption, Guid reviewerId, CancellationToken ct)
    {
        await RequireOwnerAsync(reviewerId, ct);
        caption = (caption ?? string.Empty).Trim();
        if (caption.Length == 0)
        {
            throw new ValidationException("REVIEW_CAPTION_REQUIRED", "A caption is required.");
        }

        var version = await _db.VideoVersions.AsNoTracking()
            .SingleOrDefaultAsync(x => x.Id == videoVersionId, ct)
            ?? throw new NotFoundException("VIDEO_VERSION_NOT_FOUND", "Video version not found.");
        if (version.Status is not ("awaiting_review" or "approved" or "rejected" or "qa_failed"))
        {
            throw new ConflictException("REVIEW_NOT_EDITABLE", "This version cannot be edited now.");
        }

        var revision = await _db.ContentRevisions.AsNoTracking()
            .SingleAsync(x => x.Id == version.ContentRevisionId, ct);
        var campaign = await _db.Campaigns.AsNoTracking()
            .SingleAsync(x => x.Id == revision.CampaignId, ct);
        var script = JsonSerializer.Deserialize<ScriptDocument>(revision.ScriptJson!, JsonDefaults.Options)!;
        var snapshot = JsonSerializer.Deserialize<ProductSnapshot>(revision.ProductSnapshotJson, JsonDefaults.Options)!;
        if (script.Scenes.All(x => x.SceneId != sceneId))
        {
            throw new NotFoundException("REVIEW_SCENE_NOT_FOUND", "The scene does not exist in this version.");
        }

        var dedupeKey = "caption-edit:" + videoVersionId.ToString("N") + ":"
            + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(sceneId + "\n" + caption)))[..32].ToLowerInvariant();
        var existing = await _db.Jobs.AsNoTracking()
            .SingleOrDefaultAsync(x => x.DedupeKey == dedupeKey, ct);
        if (existing is not null)
        {
            return new CaptionEditResult(ReadRevisionId(existing.PayloadJson), existing.Id);
        }

        var scenes = script.Scenes
            .Select(x => x.SceneId == sceneId ? x with { Caption = caption } : x)
            .ToArray();
        var edited = script with
        {
            Scenes = scenes,
            CaptionText = string.Join(" ", scenes.Select(x => x.Caption))
        };
        var report = new ScriptValidator().Validate(edited, snapshot);
        if (!report.Passed)
        {
            var first = report.Errors[0];
            throw new ValidationException(first.Code, first.Message);
        }

        var manifest = JsonSerializer.Deserialize<RenderManifest>(version.ManifestJson!, JsonDefaults.Options)!;
        var visuals = manifest.Segments.ToDictionary(x => x.SceneId, x => x.Visual.Id, StringComparer.Ordinal);
        var voices = await _db.SceneAssets.AsNoTracking()
            .Where(x => x.ContentRevisionId == revision.Id && x.Role == "voice")
            .ToListAsync(ct);

        var now = _clock.GetUtcNow();
        var newRevisionId = Guid.NewGuid();
        var scriptJson = JsonSerializer.Serialize(edited, JsonDefaults.Options);
        var request = new ScriptRequest(
            newRevisionId, snapshot, campaign.Locale, campaign.Mode,
            campaign.TargetDurationMs, revision.PromptVersion);
        var payload = JsonSerializer.Serialize(
            new { scriptRequest = request, requestHash = dedupeKey, autoGenerateVoice = true, cloudVideoSceneLimit = 0 },
            JsonDefaults.Options);

        await using var transaction = await _db.Database.BeginTransactionAsync(ct);
        await _db.Database.ExecuteSqlInterpolatedAsync(
            $"SELECT 1 FROM app.campaigns WHERE id = {campaign.Id} FOR UPDATE", ct);
        var next = (await _db.ContentRevisions
            .Where(x => x.CampaignId == campaign.Id)
            .MaxAsync(x => (int?)x.Revision, ct) ?? 0) + 1;
        _db.ContentRevisions.Add(new ContentRevision
        {
            Id = newRevisionId,
            CampaignId = campaign.Id,
            Revision = next,
            ProductSnapshotJson = revision.ProductSnapshotJson,
            ScriptJson = scriptJson,
            ScriptHash = ManifestHasher.HashCanonicalJson(scriptJson),
            PromptVersion = revision.PromptVersion,
            ModelId = revision.ModelId,
            Status = "validated",
            Version = 1,
            CreatedAt = now
        });
        await _db.SaveChangesAsync(ct);

        foreach (var (scene, assetId) in visuals)
        {
            _db.SceneAssets.Add(new SceneAsset
            {
                Id = Guid.NewGuid(), ContentRevisionId = newRevisionId,
                SceneId = scene, Role = "visual", AssetId = assetId
            });
        }

        foreach (var voice in voices)
        {
            _db.SceneAssets.Add(new SceneAsset
            {
                Id = Guid.NewGuid(), ContentRevisionId = newRevisionId,
                SceneId = voice.SceneId, Role = "voice", AssetId = voice.AssetId
            });
        }

        _db.AuditEvents.Add(new AuditEvent
        {
            Id = Guid.NewGuid(),
            Actor = reviewerId.ToString("D"),
            Action = "video.caption_edited",
            TargetType = "video_version",
            TargetId = videoVersionId,
            DetailsJson = JsonSerializer.Serialize(new { sceneId, newRevisionId }, JsonDefaults.Options),
            CreatedAt = now
        });
        await _db.SaveChangesAsync(ct);
        var jobId = await _jobs.EnqueueAsync(
            new NewJob("compile_timeline", payload, JobLane.HeavyLocal), dedupeKey, ct);
        await transaction.CommitAsync(ct);
        return new CaptionEditResult(newRevisionId, jobId);
    }

    public async Task<VideoReviewDetail> GetDetailAsync(Guid videoVersionId, CancellationToken ct)
    {
        var version = await _db.VideoVersions.AsNoTracking()
            .SingleOrDefaultAsync(x => x.Id == videoVersionId, ct)
            ?? throw new NotFoundException("VIDEO_VERSION_NOT_FOUND", "Video version not found.");
        var revision = await _db.ContentRevisions.AsNoTracking()
            .SingleAsync(x => x.Id == version.ContentRevisionId, ct);
        var channelId = await _db.Campaigns.AsNoTracking()
            .Where(x => x.Id == revision.CampaignId).Select(x => x.ChannelId).SingleAsync(ct);
        var script = revision.ScriptJson is null
            ? null
            : JsonSerializer.Deserialize<ScriptDocument>(revision.ScriptJson, JsonDefaults.Options);
        var snapshot = JsonSerializer.Deserialize<ProductSnapshot>(revision.ProductSnapshotJson, JsonDefaults.Options);
        var manifest = version.ManifestJson is null
            ? null
            : JsonSerializer.Deserialize<RenderManifest>(version.ManifestJson, JsonDefaults.Options);

        var usedClaimIds = script?.Scenes.SelectMany(x => x.ClaimIds).ToHashSet() ?? [];
        var usesMotion = manifest?.Segments.Any(x => x.Visual.Kind == AssetKind.Video) ?? false;

        var prefixes = new[] { $"video:{revision.Id:N}:", $"voice:{revision.Id:N}:" };
        var reservations = await _db.BudgetReservations.AsNoTracking()
            .Where(x => prefixes.Any(prefix => x.OperationId.StartsWith(prefix)))
            .ToListAsync(ct);
        var reserved = reservations.Where(x => x.Status == "reserved").Sum(x => x.ReservedUsd);
        var settled = reservations.Where(x => x.Status == "settled").Sum(x => x.SettledUsd ?? 0m);

        var approval = await _db.Approvals.AsNoTracking()
            .Where(x => x.VideoVersionId == version.Id)
            .OrderByDescending(x => x.DecidedAt)
            .FirstOrDefaultAsync(ct);
        JsonElement? qa = null;
        if (!string.IsNullOrWhiteSpace(version.QaJson))
        {
            using var document = JsonDocument.Parse(version.QaJson);
            qa = document.RootElement.Clone();
        }

        return new VideoReviewDetail(
            version.Id,
            revision.Id,
            revision.CampaignId,
            channelId,
            version.VersionNo,
            version.Status,
            version.ManifestSha256,
            version.OutputAssetId is { } output ? $"/api/v1/assets/{output:D}/content" : null,
            script?.Title ?? snapshot?.Title ?? string.Empty,
            script?.Disclosure ?? string.Empty,
            script?.Scenes
                .Select(x => new ReviewSceneView(x.SceneId, x.Role, x.Narration, x.Caption, x.ClaimIds))
                .ToList() ?? [],
            snapshot?.Claims
                .Where(x => usedClaimIds.Contains(x.Id))
                .Select(x => new ReviewClaimView(x.Id, x.Text, x.SourceReference))
                .ToList() ?? [],
            snapshot?.Assets
                .Where(x => x.Kind == AssetKind.Image)
                .Select(x => new ReviewAssetView(x.Id, x.MimeType, $"/api/v1/assets/{x.Id:D}/content"))
                .ToList() ?? [],
            usesMotion,
            usesMotion ? AiMotionWarningText : null,
            new ReviewCostView(reserved, settled, "USD"),
            qa,
            RequiredChecklist,
            approval is null
                ? null
                : new ReviewDecisionView(approval.Decision, approval.ReviewerId, approval.DecidedAt, approval.ManifestSha256));
    }

    public async Task<IReadOnlyList<VideoQueueItem>> ListAsync(string? status, CancellationToken ct)
    {
        var query = _db.VideoVersions.AsNoTracking().AsQueryable();
        if (!string.IsNullOrWhiteSpace(status))
        {
            query = query.Where(x => x.Status == status);
        }

        return await (
                from version in query
                join revision in _db.ContentRevisions.AsNoTracking() on version.ContentRevisionId equals revision.Id
                join campaign in _db.Campaigns.AsNoTracking() on revision.CampaignId equals campaign.Id
                join product in _db.Products.AsNoTracking() on campaign.ProductId equals product.Id
                orderby version.CreatedAt descending
                select new VideoQueueItem(
                    version.Id, version.VersionNo, version.Status, product.Title, version.CreatedAt))
            .Take(100)
            .ToListAsync(ct);
    }

    private async Task RequireOwnerAsync(Guid reviewerId, CancellationToken ct)
    {
        if (reviewerId == Guid.Empty
            || !await _db.OwnerUsers.AsNoTracking().AnyAsync(x => x.Id == reviewerId && x.Active, ct))
        {
            throw new ValidationException("REVIEW_REVIEWER_INVALID", "The reviewer must be the signed-in owner.");
        }
    }

    private static bool QaPassed(string? qaJson)
    {
        if (string.IsNullOrWhiteSpace(qaJson))
        {
            return false;
        }

        using var document = JsonDocument.Parse(qaJson);
        return document.RootElement.TryGetProperty("passed", out var passed)
            && passed.ValueKind == JsonValueKind.True;
    }

    private static string RecomputeHash(string manifestJson)
    {
        var manifest = JsonSerializer.Deserialize<RenderManifest>(manifestJson, JsonDefaults.Options);
        return manifest is null ? string.Empty : ManifestHasher.Compute(manifest);
    }

    private static Guid ReadRevisionId(string payloadJson)
    {
        using var document = JsonDocument.Parse(payloadJson);
        return document.RootElement.GetProperty("scriptRequest").GetProperty("revisionId").GetGuid();
    }
}
