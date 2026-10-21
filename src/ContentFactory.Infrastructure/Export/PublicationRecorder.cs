using ContentFactory.Core.Exceptions;
using ContentFactory.Infrastructure.Persistence;
using ContentFactory.Infrastructure.Persistence.Entities;
using Microsoft.EntityFrameworkCore;

namespace ContentFactory.Infrastructure.Export;

/// <summary>
/// Records a post the owner really made, from the owner's own evidence. The system never
/// publishes and never claims a product link works: the link status stays "unknown" until
/// the owner attests with a reference to the evidence.
/// </summary>
public sealed class PublicationRecorder
{
    private static readonly string[] LinkStatuses = ["unknown", "verified", "broken", "not_applicable"];

    private readonly AppDbContext _db;
    private readonly TimeProvider _clock;

    public PublicationRecorder(AppDbContext db, TimeProvider clock)
    {
        _db = db ?? throw new ArgumentNullException(nameof(db));
        _clock = clock ?? throw new ArgumentNullException(nameof(clock));
    }

    public async Task<Publication> RecordAsync(
        Guid videoVersionId,
        Guid channelId,
        string postUrl,
        string? externalPostId,
        string? productLinkStatus,
        string? evidenceReference,
        CancellationToken ct)
    {
        if (!Uri.TryCreate(postUrl?.Trim(), UriKind.Absolute, out var uri)
            || uri.Scheme != Uri.UriSchemeHttps
            || !string.IsNullOrEmpty(uri.UserInfo)
            || postUrl!.Trim().Length > 2048)
        {
            throw new ValidationException("PUBLICATION_URL_INVALID", "The post URL must be an https URL.");
        }

        var status = string.IsNullOrWhiteSpace(productLinkStatus) ? "unknown" : productLinkStatus.Trim();
        if (!LinkStatuses.Contains(status, StringComparer.Ordinal))
        {
            throw new ValidationException("PUBLICATION_LINK_STATUS_INVALID", "The product link status is not recognised.");
        }

        var evidence = evidenceReference?.Trim();
        if (status != "unknown" && string.IsNullOrEmpty(evidence))
        {
            throw new ValidationException(
                "PUBLICATION_EVIDENCE_REQUIRED", "A reference to your evidence is required to set the link status.");
        }

        if (evidence is { Length: > 1024 } || externalPostId is { Length: > 256 })
        {
            throw new ValidationException("PUBLICATION_FIELD_TOO_LONG", "A field exceeds its length limit.");
        }

        var version = await _db.VideoVersions.AsNoTracking()
            .SingleOrDefaultAsync(x => x.Id == videoVersionId, ct)
            ?? throw new NotFoundException("VIDEO_VERSION_NOT_FOUND", "Video version not found.");
        if (version.Status != "approved"
            || !await _db.ExportPackages.AnyAsync(x => x.VideoVersionId == videoVersionId && x.ChannelId == channelId, ct))
        {
            throw new ConflictException(
                "PUBLICATION_NOT_EXPORTED", "A publication can only be recorded for an approved, exported version.");
        }

        var url = uri.ToString();
        var existing = await _db.Publications.FirstOrDefaultAsync(
            x => x.VideoVersionId == videoVersionId && x.ChannelId == channelId && x.PostUrl == url, ct);
        if (existing is not null)
        {
            return existing;
        }

        var now = _clock.GetUtcNow();
        var publication = new Publication
        {
            Id = Guid.NewGuid(),
            VideoVersionId = videoVersionId,
            ChannelId = channelId,
            PostUrl = url,
            ExternalPostId = string.IsNullOrWhiteSpace(externalPostId) ? null : externalPostId.Trim(),
            PublishedAt = now,
            ProductLinkStatus = status,
            EvidenceReference = string.IsNullOrEmpty(evidence) ? null : evidence,
            CreatedAt = now
        };
        _db.Publications.Add(publication);
        _db.AuditEvents.Add(new AuditEvent
        {
            Id = Guid.NewGuid(),
            Actor = "owner",
            Action = "publication.recorded",
            TargetType = "video_version",
            TargetId = videoVersionId,
            DetailsJson = "{}",
            CreatedAt = now
        });
        await _db.SaveChangesAsync(ct);
        return publication;
    }
}
