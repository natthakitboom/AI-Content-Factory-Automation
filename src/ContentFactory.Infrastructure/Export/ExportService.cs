using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using ContentFactory.Contracts;
using ContentFactory.Core.Abstractions;
using ContentFactory.Core.Exceptions;
using ContentFactory.Infrastructure.Media;
using ContentFactory.Infrastructure.Persistence;
using ContentFactory.Infrastructure.Persistence.Entities;
using Microsoft.EntityFrameworkCore;

namespace ContentFactory.Infrastructure.Export;

/// <summary>
/// Builds the manual-publishing ZIP (plan E3) from an approved version. It checks, at the
/// moment of export, that the approval still matches the stored manifest, that attested
/// rights still hold and that the offer was verified within 24 hours. It calls no platform
/// API and records no publication; the package is byte-for-byte reproducible.
/// </summary>
public sealed class ExportService : IExportService
{
    public static readonly TimeSpan OfferMaxAge = TimeSpan.FromHours(24);

    private static readonly DateTimeOffset FixedEntryTime = new(2000, 1, 1, 0, 0, 0, TimeSpan.Zero);

    private readonly AppDbContext _db;
    private readonly IAssetStore _store;
    private readonly TimeProvider _clock;

    public ExportService(AppDbContext db, IAssetStore store, TimeProvider clock)
    {
        _db = db ?? throw new ArgumentNullException(nameof(db));
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _clock = clock ?? throw new ArgumentNullException(nameof(clock));
    }

    public async Task<AssetRef> CreateAsync(Guid videoVersionId, Guid channelId, CancellationToken ct)
    {
        var version = await _db.VideoVersions.AsNoTracking()
            .SingleOrDefaultAsync(x => x.Id == videoVersionId, ct)
            ?? throw new NotFoundException("VIDEO_VERSION_NOT_FOUND", "Video version not found.");
        if (version.Status != "approved")
        {
            throw new ConflictException("EXPORT_NOT_APPROVED", "Only an approved version can be exported.");
        }

        var approval = await _db.Approvals.AsNoTracking()
            .Where(x => x.VideoVersionId == version.Id && x.Decision == "approved")
            .OrderByDescending(x => x.DecidedAt)
            .FirstOrDefaultAsync(ct);
        var manifest = version.ManifestJson is null
            ? null
            : JsonSerializer.Deserialize<RenderManifest>(version.ManifestJson, JsonDefaults.Options);
        if (approval is null
            || manifest is null
            || !string.Equals(approval.ManifestSha256, version.ManifestSha256, StringComparison.Ordinal)
            || !string.Equals(ManifestHasher.Compute(manifest), approval.ManifestSha256, StringComparison.Ordinal))
        {
            throw new ApprovalStaleException(
                "EXPORT_APPROVAL_STALE",
                "The approval does not match this version's manifest; review it again.");
        }

        if (version.OutputAssetId is null || version.ThumbnailAssetId is null || version.SubtitlesAssetId is null)
        {
            throw new ConflictException("EXPORT_OUTPUT_MISSING", "The rendered files for this version are missing.");
        }

        var revision = await _db.ContentRevisions.AsNoTracking()
            .SingleAsync(x => x.Id == version.ContentRevisionId, ct);
        var campaign = await _db.Campaigns.AsNoTracking().SingleAsync(x => x.Id == revision.CampaignId, ct);
        var product = await _db.Products.AsNoTracking().SingleAsync(x => x.Id == campaign.ProductId, ct);
        var channel = await _db.Channels.AsNoTracking()
            .SingleOrDefaultAsync(x => x.Id == channelId, ct)
            ?? throw new NotFoundException("CHANNEL_NOT_FOUND", "Channel not found.");
        if (channel.Status != "active"
            || !string.Equals(channel.Market, product.Market, StringComparison.Ordinal)
            || !string.Equals(channel.Locale, campaign.Locale, StringComparison.Ordinal))
        {
            throw new ValidationException(
                "EXPORT_CHANNEL_MISMATCH", "The channel must be active and match the product market and locale.");
        }

        var snapshot = JsonSerializer.Deserialize<ProductSnapshot>(revision.ProductSnapshotJson, JsonDefaults.Options)!;
        var script = JsonSerializer.Deserialize<ScriptDocument>(revision.ScriptJson!, JsonDefaults.Options)!;
        await EnsureRightsAsync(snapshot, ct);
        var offer = await EnsureFreshOfferAsync(product, ct);
        var usesMotion = manifest.Segments.Any(x => x.Visual.Kind == AssetKind.Video);

        var video = await ReadAsync(version.OutputAssetId.Value, ct);
        var thumbnail = await ReadAsync(version.ThumbnailAssetId.Value, ct);
        var subtitles = await ReadAsync(version.SubtitlesAssetId.Value, ct);

        var files = new List<(string Name, byte[] Content, bool Compress)>
        {
            ("video.mp4", video, false),
            ("thumbnail.jpg", thumbnail, false),
            ("captions.srt", subtitles, true),
            ("post-caption.txt", Utf8(PostCaption(script)), true),
            ("product-reference.json", Utf8(ProductReference(product, snapshot, offer)), true),
            ("disclosure-checklist.md", Utf8(DisclosureChecklist(usesMotion)), true)
        };
        files.Add(("manifest.json", Utf8(ManifestJson(version, approval, channel, files)), true));
        files.Add(("sha256sums.txt", Utf8(Checksums(files)), true));

        var tempPath = Path.Combine(Path.GetTempPath(), "cf-export-" + Guid.NewGuid().ToString("N") + ".zip");
        try
        {
            await using (var zipFile = new FileStream(tempPath, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None))
            {
                using var archive = new ZipArchive(zipFile, ZipArchiveMode.Create, leaveOpen: true);
                foreach (var (name, content, compress) in files)
                {
                    var entry = archive.CreateEntry(
                        name, compress ? CompressionLevel.Optimal : CompressionLevel.NoCompression);
                    entry.LastWriteTime = FixedEntryTime;
                    await using var entryStream = entry.Open();
                    await entryStream.WriteAsync(content, ct);
                }
            }

            await using var input = new FileStream(tempPath, FileMode.Open, FileAccess.Read, FileShare.Read);
            var package = await _store.PutAsync(input, AssetKind.Archive, "application/zip", ct);

            var exists = await _db.ExportPackages.AnyAsync(
                x => x.VideoVersionId == version.Id && x.ChannelId == channel.Id && x.PackageHash == package.Sha256, ct);
            if (!exists)
            {
                _db.ExportPackages.Add(new ExportPackage
                {
                    Id = Guid.NewGuid(),
                    VideoVersionId = version.Id,
                    ChannelId = channel.Id,
                    PackageHash = package.Sha256,
                    RelativePath = package.RelativePath,
                    CreatedAt = _clock.GetUtcNow()
                });
                await _db.SaveChangesAsync(ct);
            }

            return package;
        }
        finally
        {
            try
            {
                File.Delete(tempPath);
            }
            catch (IOException)
            {
            }
        }
    }

    private async Task EnsureRightsAsync(ProductSnapshot snapshot, CancellationToken ct)
    {
        var ids = snapshot.Assets.Select(x => x.Id).ToArray();
        var rows = await _db.Assets.AsNoTracking().Where(x => ids.Contains(x.Id)).ToListAsync(ct);
        if (rows.Count != ids.Length
            || rows.Any(x => x.RightsStatus != "owner_attested" || string.IsNullOrWhiteSpace(x.SourceReference)))
        {
            throw new ConflictException(
                "EXPORT_RIGHTS_INVALID", "Every source asset needs a current owner attestation with a source.");
        }
    }

    private async Task<ProductOffer> EnsureFreshOfferAsync(Product product, CancellationToken ct)
    {
        var offer = await _db.ProductOffers.AsNoTracking()
            .Where(x => x.ProductId == product.Id && x.Market == product.Market)
            .OrderByDescending(x => x.CheckedAt)
            .FirstOrDefaultAsync(ct);
        var now = _clock.GetUtcNow();
        if (offer is null || now - offer.CheckedAt > OfferMaxAge || offer.CheckedAt > now.AddMinutes(5))
        {
            throw new ConflictException(
                "EXPORT_OFFER_STALE",
                "The product offer, price and stock must be verified within the last 24 hours before export.");
        }

        return offer;
    }

    private async Task<byte[]> ReadAsync(Guid assetId, CancellationToken ct)
    {
        await using var stream = await _store.OpenReadAsync(assetId, ct);
        using var memory = new MemoryStream();
        await stream.CopyToAsync(memory, ct);
        return memory.ToArray();
    }

    private static byte[] Utf8(string text) => new UTF8Encoding(false).GetBytes(text);

    private static string PostCaption(ScriptDocument script)
        => $"{script.Title}\n\n{script.CaptionText}\n\n{script.Disclosure}\n";

    private static string ProductReference(Product product, ProductSnapshot snapshot, ProductOffer offer)
        => JsonSerializer.Serialize(
            new
            {
                productId = product.Id,
                title = product.Title,
                market = product.Market,
                sourceUrl = offer.ProductUrl,
                checkedAt = offer.CheckedAt,
                price = offer.Price,
                currency = offer.Currency,
                availability = offer.Availability,
                claims = snapshot.Claims.Select(x => new { x.Id, x.Text, x.SourceReference })
            },
            JsonDefaults.Options);

    private static string DisclosureChecklist(bool usesMotion)
    {
        var builder = new StringBuilder();
        builder.AppendLine("# Posting checklist");
        builder.AppendLine();
        builder.AppendLine("Do these on the platform yourself. Having a disclosure line in a file does not turn on");
        builder.AppendLine("the platform's own label for you, and this package has not been posted anywhere.");
        builder.AppendLine();
        builder.AppendLine("- [ ] Turn on the platform's advertising / paid-promotion label where it applies.");
        builder.AppendLine("- [ ] Turn on the affiliate or commercial-content disclosure where it applies.");
        builder.AppendLine("- [ ] Add the product link yourself and open it to confirm it works.");
        builder.AppendLine("- [ ] Confirm the offer, price and stock on the product page before posting.");
        if (usesMotion)
        {
            builder.AppendLine("- [ ] This video includes AI-generated motion: use the platform's AI-content label.");
        }

        builder.AppendLine("- [ ] After posting, record the post URL in the dashboard so the publication is tracked.");
        return builder.ToString();
    }

    private static string ManifestJson(
        VideoVersion version,
        Approval approval,
        Channel channel,
        IReadOnlyList<(string Name, byte[] Content, bool Compress)> files)
        => JsonSerializer.Serialize(
            new
            {
                videoVersionId = version.Id,
                manifestSha256 = version.ManifestSha256,
                channelId = channel.Id,
                approval = new { approval.Id, approval.ReviewerId, approval.DecidedAt },
                files = files.Select(x => new { name = x.Name, bytes = x.Content.Length })
            },
            JsonDefaults.Options);

    private static string Checksums(IReadOnlyList<(string Name, byte[] Content, bool Compress)> files)
    {
        var builder = new StringBuilder();
        foreach (var (name, content, _) in files)
        {
            builder.Append(Convert.ToHexString(SHA256.HashData(content)).ToLowerInvariant())
                .Append("  ").Append(name).Append('\n');
        }

        return builder.ToString();
    }
}
