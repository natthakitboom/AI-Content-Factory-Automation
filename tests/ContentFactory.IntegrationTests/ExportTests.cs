using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using ContentFactory.Contracts;
using ContentFactory.Core.Exceptions;
using ContentFactory.Infrastructure.Export;
using ContentFactory.Infrastructure.Persistence;
using ContentFactory.Infrastructure.Persistence.Entities;
using ContentFactory.Infrastructure.Storage;
using ContentFactory.IntegrationTests.Infrastructure;
using Microsoft.EntityFrameworkCore;

namespace ContentFactory.IntegrationTests;

[CollectionDefinition(Name)]
public sealed class ExportCollection : ICollectionFixture<PostgresSchemaFixture>
{
    public const string Name = "postgres-export";
}

/// <summary>
/// T17: a ZIP is built only from an approved version whose approval matches the manifest
/// hash, with current rights and a recently checked offer. Exporting never records a
/// publication; the owner records one afterwards from evidence of a real post.
/// </summary>
[Collection(ExportCollection.Name)]
public sealed class ExportTests : IAsyncLifetime
{
    private static readonly string[] RequiredEntries =
    [
        "video.mp4", "thumbnail.jpg", "captions.srt", "post-caption.txt",
        "product-reference.json", "disclosure-checklist.md", "manifest.json", "sha256sums.txt"
    ];

    private readonly PostgresSchemaFixture _fixture;
    private FileSystemAssetStore? _lastStore;
    private readonly string _root = Path.Combine(
        Path.GetTempPath(), "contentfactory-export-tests", Guid.NewGuid().ToString("N"));

    public ExportTests(PostgresSchemaFixture fixture)
    {
        _fixture = fixture;
    }

    public Task InitializeAsync() => Task.CompletedTask;

    public Task DisposeAsync()
    {
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }

        return Task.CompletedTask;
    }

    [Fact]
    public async Task UnapprovedExportRejected()
    {
        await using var db = _fixture.CreateContext();
        var (service, seeded, _, channelId) = await ArrangeAsync(db, approve: false);

        var error = await Assert.ThrowsAsync<ConflictException>(() =>
            service.CreateAsync(seeded.VersionId, channelId, default));

        Assert.Equal("EXPORT_NOT_APPROVED", error.Code);
        Assert.Equal(0, await db.ExportPackages.CountAsync(x => x.VideoVersionId == seeded.VersionId));
    }

    [Fact]
    public async Task ApprovalForDifferentHashRejected()
    {
        await using var db = _fixture.CreateContext();
        var (service, seeded, _, channelId) = await ArrangeAsync(db, approve: true);
        await db.Approvals.Where(x => x.VideoVersionId == seeded.VersionId)
            .ExecuteUpdateAsync(s => s.SetProperty(x => x.ManifestSha256, new string('1', 64)));

        var error = await Assert.ThrowsAsync<ApprovalStaleException>(() =>
            service.CreateAsync(seeded.VersionId, channelId, default));

        Assert.Equal("EXPORT_APPROVAL_STALE", error.Code);
    }

    [Fact]
    public async Task TamperedManifestRejected()
    {
        await using var db = _fixture.CreateContext();
        var (service, seeded, _, channelId) = await ArrangeAsync(db, approve: true);
        await db.Database.ExecuteSqlInterpolatedAsync(
            $"UPDATE app.video_versions SET manifest_json = jsonb_set(manifest_json, ARRAY['width'], '999'::jsonb) WHERE id = {seeded.VersionId}");

        var error = await Assert.ThrowsAsync<ApprovalStaleException>(() =>
            service.CreateAsync(seeded.VersionId, channelId, default));

        Assert.Equal("EXPORT_APPROVAL_STALE", error.Code);
    }

    [Fact]
    public async Task PackageContainsRequiredFilesAndChecksums()
    {
        await using var db = _fixture.CreateContext();
        var (service, seeded, bytes, channelId) = await ArrangeAsync(db, approve: true);

        var package = await service.CreateAsync(seeded.VersionId, channelId, default);

        Assert.Equal(AssetKind.Archive, package.Kind);
        using var archive = new ZipArchive(await OpenAsync(db, package.Id), ZipArchiveMode.Read);
        Assert.Equal(
            RequiredEntries.Order(StringComparer.Ordinal),
            archive.Entries.Select(x => x.FullName).Order(StringComparer.Ordinal));
        Assert.All(archive.Entries, entry => Assert.DoesNotContain("..", entry.FullName));

        Assert.Equal(bytes.Video, Read(archive, "video.mp4"));
        Assert.Equal(bytes.Thumbnail, Read(archive, "thumbnail.jpg"));
        Assert.Equal(bytes.Subtitles, Read(archive, "captions.srt"));

        var sums = Encoding.UTF8.GetString(Read(archive, "sha256sums.txt"))
            .Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Select(line => line.Split("  ", 2))
            .ToDictionary(parts => parts[1], parts => parts[0]);
        foreach (var entry in archive.Entries.Where(x => x.FullName != "sha256sums.txt"))
        {
            Assert.Equal(
                Convert.ToHexString(SHA256.HashData(Read(archive, entry.FullName))).ToLowerInvariant(),
                sums[entry.FullName]);
        }

        using var manifest = JsonDocument.Parse(Read(archive, "manifest.json"));
        Assert.Equal(seeded.ManifestSha256, manifest.RootElement.GetProperty("manifestSha256").GetString());
        var disclosure = Encoding.UTF8.GetString(Read(archive, "disclosure-checklist.md"));
        Assert.Contains("does not turn on", disclosure, StringComparison.OrdinalIgnoreCase);
        using var reference = JsonDocument.Parse(Read(archive, "product-reference.json"));
        Assert.Equal("TH", reference.RootElement.GetProperty("market").GetString());
        Assert.False(reference.RootElement.TryGetProperty("customer", out _));
    }

    [Fact]
    public async Task ExportDoesNotMarkPublishedAndReplayIsIdempotent()
    {
        await using var db = _fixture.CreateContext();
        var (service, seeded, _, channelId) = await ArrangeAsync(db, approve: true);

        var first = await service.CreateAsync(seeded.VersionId, channelId, default);
        var second = await service.CreateAsync(seeded.VersionId, channelId, default);

        Assert.Equal(first.Id, second.Id);
        Assert.Equal(first.Sha256, second.Sha256);
        Assert.Equal(1, await db.ExportPackages.CountAsync(x => x.VideoVersionId == seeded.VersionId));
        Assert.Equal(0, await db.Publications.CountAsync(x => x.VideoVersionId == seeded.VersionId));
    }

    [Fact]
    public async Task StaleOfferRequiresVerification()
    {
        await using var db = _fixture.CreateContext();
        var (service, seeded, _, channelId) = await ArrangeAsync(db, approve: true, offerAge: TimeSpan.FromHours(48));

        var stale = await Assert.ThrowsAsync<ConflictException>(() =>
            service.CreateAsync(seeded.VersionId, channelId, default));
        Assert.Equal("EXPORT_OFFER_STALE", stale.Code);

        await db.ProductOffers.ExecuteUpdateAsync(s => s.SetProperty(x => x.CheckedAt, DateTimeOffset.UtcNow));
        var package = await service.CreateAsync(seeded.VersionId, channelId, default);
        Assert.Equal(AssetKind.Archive, package.Kind);

        var (noOfferService, noOffer, _, noOfferChannel) = await ArrangeAsync(db, approve: true, offerAge: null, offer: false);
        var missing = await Assert.ThrowsAsync<ConflictException>(() =>
            noOfferService.CreateAsync(noOffer.VersionId, noOfferChannel, default));
        Assert.Equal("EXPORT_OFFER_STALE", missing.Code);
    }

    [Fact]
    public async Task RevokedRightsBlockExport()
    {
        await using var db = _fixture.CreateContext();
        var (service, seeded, _, channelId) = await ArrangeAsync(db, approve: true);
        var revision = await db.ContentRevisions.AsNoTracking().SingleAsync(x => x.Id == seeded.RevisionId);
        var snapshot = JsonSerializer.Deserialize<ProductSnapshot>(revision.ProductSnapshotJson, JsonDefaults.Options)!;
        var imageId = snapshot.Assets[0].Id;
        await db.Assets.Where(x => x.Id == imageId)
            .ExecuteUpdateAsync(s => s.SetProperty(x => x.RightsStatus, "rejected"));

        var error = await Assert.ThrowsAsync<ConflictException>(() =>
            service.CreateAsync(seeded.VersionId, channelId, default));

        Assert.Equal("EXPORT_RIGHTS_INVALID", error.Code);
    }

    [Fact]
    public async Task ChannelMustMatchTheProductMarket()
    {
        await using var db = _fixture.CreateContext();
        var (service, seeded, _, _) = await ArrangeAsync(db, approve: true);
        var other = Guid.NewGuid();
        db.Channels.Add(new Channel
        {
            Id = other, Platform = "tiktok", DisplayName = "VN", Market = "VN", Locale = "vi-VN",
            Timezone = "Asia/Ho_Chi_Minh", Status = "active", CapabilitiesJson = "{}"
        });
        await db.SaveChangesAsync();

        var error = await Assert.ThrowsAsync<ValidationException>(() =>
            service.CreateAsync(seeded.VersionId, other, default));

        Assert.Equal("EXPORT_CHANNEL_MISMATCH", error.Code);
    }

    [Fact]
    public async Task PublicationNeedsAnExportAndStaysUnknownUntilAttested()
    {
        await using var db = _fixture.CreateContext();
        var (service, seeded, _, channelId) = await ArrangeAsync(db, approve: true);
        var recorder = new PublicationRecorder(db, TimeProvider.System);

        var before = await Assert.ThrowsAsync<ConflictException>(() =>
            recorder.RecordAsync(seeded.VersionId, channelId, "https://www.tiktok.com/@x/video/1", null, null, null, default));
        Assert.Equal("PUBLICATION_NOT_EXPORTED", before.Code);

        await service.CreateAsync(seeded.VersionId, channelId, default);
        var recorded = await recorder.RecordAsync(
            seeded.VersionId, channelId, "https://www.tiktok.com/@x/video/1", "1", null, null, default);
        var replay = await recorder.RecordAsync(
            seeded.VersionId, channelId, "https://www.tiktok.com/@x/video/1", "1", null, null, default);

        Assert.Equal("unknown", recorded.ProductLinkStatus);
        Assert.Equal(recorded.Id, replay.Id);
        Assert.Equal(1, await db.Publications.CountAsync(x => x.VideoVersionId == seeded.VersionId));

        var noEvidence = await Assert.ThrowsAsync<ValidationException>(() =>
            recorder.RecordAsync(
                seeded.VersionId, channelId, "https://www.tiktok.com/@x/video/2", null, "verified", null, default));
        Assert.Equal("PUBLICATION_EVIDENCE_REQUIRED", noEvidence.Code);

        var verified = await recorder.RecordAsync(
            seeded.VersionId, channelId, "https://www.tiktok.com/@x/video/3", null, "verified", "screenshot-2026-10-21", default);
        Assert.Equal("verified", verified.ProductLinkStatus);

        var badUrl = await Assert.ThrowsAsync<ValidationException>(() =>
            recorder.RecordAsync(
                seeded.VersionId, channelId, "javascript:alert(1)", null, null, null, default));
        Assert.Equal("PUBLICATION_URL_INVALID", badUrl.Code);
    }

    private async Task<(ExportService Service, SeededVersion Seeded, (byte[] Video, byte[] Thumbnail, byte[] Subtitles) Bytes, Guid ChannelId)>
        ArrangeAsync(AppDbContext db, bool approve, TimeSpan? offerAge = null, bool offer = true)
    {
        var storage = new StorageOptions { DataRoot = Path.Combine(_root, Guid.NewGuid().ToString("N")) };
        var store = new FileSystemAssetStore(db, storage, TimeProvider.System);
        _lastStore = store;
        var seeded = await ReviewSeed.CreateAsync(db);
        var bytes = await ReviewSeed.AttachRealOutputsAsync(db, store, seeded);
        var channelId = await db.Campaigns.AsNoTracking()
            .Where(x => x.Id == seeded.CampaignId).Select(x => x.ChannelId).SingleAsync();

        if (offerAge != null || offer)
        {
            db.ProductOffers.Add(new ProductOffer
            {
                Id = Guid.NewGuid(),
                ProductId = seeded.ProductId,
                Market = "TH",
                ProductUrl = "https://shop.example.test/p/1",
                Price = 199m,
                Currency = "THB",
                CommissionRate = 0.1m,
                Availability = "in_stock",
                CheckedAt = DateTimeOffset.UtcNow - (offerAge ?? TimeSpan.FromMinutes(5)),
                CreatedAt = DateTimeOffset.UtcNow
            });
            await db.SaveChangesAsync();
        }

        if (approve)
        {
            var owner = new OwnerUser
            {
                Id = Guid.NewGuid(), Username = "o" + Guid.NewGuid().ToString("N")[..8],
                PasswordHash = "x", Active = true, SessionVersion = 1, CreatedAt = DateTimeOffset.UtcNow
            };
            db.OwnerUsers.Add(owner);
            db.Approvals.Add(new Approval
            {
                Id = Guid.NewGuid(), VideoVersionId = seeded.VersionId, ManifestSha256 = seeded.ManifestSha256,
                ReviewerId = owner.Id, Decision = "approved", ChecklistJson = "{}", DecidedAt = DateTimeOffset.UtcNow
            });
            await db.SaveChangesAsync();
            await db.VideoVersions.Where(x => x.Id == seeded.VersionId)
                .ExecuteUpdateAsync(s => s.SetProperty(x => x.Status, "approved"));
        }

        db.ChangeTracker.Clear();
        return (new ExportService(db, store, TimeProvider.System), seeded, bytes, channelId);
    }

    private async Task<Stream> OpenAsync(AppDbContext db, Guid assetId)
    {
        var source = await _lastStore!.OpenReadAsync(assetId, default);
        var memory = new MemoryStream();
        await source.CopyToAsync(memory);
        await source.DisposeAsync();
        memory.Position = 0;
        return memory;
    }

    private static byte[] Read(ZipArchive archive, string name)
    {
        using var stream = archive.GetEntry(name)!.Open();
        using var memory = new MemoryStream();
        stream.CopyTo(memory);
        return memory.ToArray();
    }
}
