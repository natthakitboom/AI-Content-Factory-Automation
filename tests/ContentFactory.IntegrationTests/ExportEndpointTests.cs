using System.IO.Compression;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using ContentFactory.Core.Abstractions;
using ContentFactory.Infrastructure.Persistence;
using ContentFactory.Infrastructure.Persistence.Entities;
using ContentFactory.IntegrationTests.Api;
using ContentFactory.IntegrationTests.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace ContentFactory.IntegrationTests;

[Collection(ApiCollection.Name)]
public sealed class ExportEndpointTests
{
    private readonly ApiTestFixture _fixture;

    public ExportEndpointTests(ApiTestFixture fixture)
    {
        _fixture = fixture;
    }

    [Fact]
    public async Task ApprovedVersionDownloadsAsZipAndPublicationIsOwnerRecorded()
    {
        var client = await _fixture.CreateLoggedInClientAsync();
        Guid versionId;
        Guid channelId;
        await using (var scope = _fixture.Factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var store = scope.ServiceProvider.GetRequiredService<IAssetStore>();
            var seeded = await ReviewSeed.CreateAsync(db);
            await ReviewSeed.AttachRealOutputsAsync(db, store, seeded);
            channelId = await db.Campaigns.Where(x => x.Id == seeded.CampaignId)
                .Select(x => x.ChannelId).SingleAsync();
            var owner = await db.OwnerUsers.Select(x => x.Id).FirstAsync();
            db.ProductOffers.Add(new ProductOffer
            {
                Id = Guid.NewGuid(), ProductId = seeded.ProductId, Market = "TH",
                ProductUrl = "https://shop.example.test/p/2", Price = 1m, Currency = "THB",
                CommissionRate = 0m, Availability = "in_stock",
                CheckedAt = DateTimeOffset.UtcNow, CreatedAt = DateTimeOffset.UtcNow
            });
            db.Approvals.Add(new Approval
            {
                Id = Guid.NewGuid(), VideoVersionId = seeded.VersionId, ManifestSha256 = seeded.ManifestSha256,
                ReviewerId = owner, Decision = "approved", ChecklistJson = "{}", DecidedAt = DateTimeOffset.UtcNow
            });
            await db.SaveChangesAsync();
            await db.VideoVersions.Where(x => x.Id == seeded.VersionId)
                .ExecuteUpdateAsync(s => s.SetProperty(x => x.Status, "approved"));
            versionId = seeded.VersionId;
        }

        var created = await client.PostAsJsonAsync($"/api/v1/videos/{versionId}/exports", new { channelId });
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var body = await created.Content.ReadFromJsonAsync<JsonElement>();
        var url = body.GetProperty("downloadUrl").GetString()!;

        var download = await client.GetAsync(url);
        Assert.Equal(HttpStatusCode.OK, download.StatusCode);
        Assert.Equal("application/zip", download.Content.Headers.ContentType!.MediaType);
        Assert.Equal("attachment", download.Content.Headers.ContentDisposition!.DispositionType);
        using var archive = new ZipArchive(await download.Content.ReadAsStreamAsync(), ZipArchiveMode.Read);
        Assert.NotNull(archive.GetEntry("video.mp4"));
        Assert.NotNull(archive.GetEntry("sha256sums.txt"));

        var before = await client.GetFromJsonAsync<JsonElement>($"/api/v1/videos/{versionId}/publications");
        Assert.Equal(0, before.GetArrayLength());

        var recorded = await client.PostAsJsonAsync(
            $"/api/v1/videos/{versionId}/publications",
            new { channelId, postUrl = "https://www.tiktok.com/@owner/video/123" });
        Assert.Equal(HttpStatusCode.OK, recorded.StatusCode);
        var publication = await recorded.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("unknown", publication.GetProperty("productLinkStatus").GetString());
    }

    [Fact]
    public async Task UnapprovedVersionCannotBeExportedThroughTheApi()
    {
        var client = await _fixture.CreateLoggedInClientAsync();
        Guid versionId;
        Guid channelId;
        await using (var scope = _fixture.Factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var seeded = await ReviewSeed.CreateAsync(db);
            versionId = seeded.VersionId;
            channelId = await db.Campaigns.Where(x => x.Id == seeded.CampaignId)
                .Select(x => x.ChannelId).SingleAsync();
        }

        var response = await client.PostAsJsonAsync($"/api/v1/videos/{versionId}/exports", new { channelId });

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
    }
}
