using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using ContentFactory.Contracts;
using ContentFactory.Infrastructure.Media;
using ContentFactory.Infrastructure.Persistence;
using ContentFactory.Infrastructure.Persistence.Entities;
using ContentFactory.IntegrationTests.Api;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace ContentFactory.IntegrationTests;

/// <summary>
/// T05: a run is created from database-approved product facts only. Repeating the same
/// idempotency key must never create a second revision or job, and later product edits
/// must not rewrite a revision that was already frozen.
/// </summary>
[Collection(ApiCollection.Name)]
public sealed class CampaignRunTests
{
    private readonly ApiTestFixture _fixture;

    public CampaignRunTests(ApiTestFixture fixture)
    {
        _fixture = fixture;
    }

    [Fact]
    public async Task DuplicateRequestReturnsSameJob()
    {
        var client = await _fixture.CreateLoggedInClientAsync();
        var productId = await CreateProductAsync(client, "สินค้าทดสอบ A");
        await CreateClaimAsync(client, productId, "ผ่านการอนุมัติแล้ว", "approved", "owner-sheet-1");
        var campaignId = await CreateCampaignAsync(client, productId);

        var body = RunBody(targetDurationMs: 25000);
        var first = await PostRunAsync(client, campaignId, "duplicate-run-key-0001", body);
        Assert.Equal(HttpStatusCode.Accepted, first.StatusCode);
        var firstRun = await ReadRunAsync(first);

        var second = await PostRunAsync(client, campaignId, "duplicate-run-key-0001", body);
        Assert.Equal(HttpStatusCode.Accepted, second.StatusCode);
        var secondRun = await ReadRunAsync(second);

        Assert.Equal(firstRun.JobId, secondRun.JobId);
        Assert.Equal(firstRun.RevisionId, secondRun.RevisionId);

        await using var scope = _fixture.Factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        Assert.Equal(1, await db.ContentRevisions.CountAsync(x => x.CampaignId == campaignId));
        Assert.Equal(1, await db.Jobs.CountAsync(
            x => x.JobType == "plan_script"
                && x.DedupeKey.StartsWith($"campaign-run:{campaignId:N}:")));

        var revision = await db.ContentRevisions.AsNoTracking().SingleAsync(x => x.Id == firstRun.RevisionId);
        var snapshot = JsonSerializer.Deserialize<ProductSnapshot>(
            revision.ProductSnapshotJson,
            JsonDefaults.Options);
        Assert.NotNull(snapshot);
        Assert.Equal(productId, snapshot!.ProductId);
        Assert.Equal("สินค้าทดสอบ A", snapshot.Title);
        Assert.Single(snapshot.Claims);
        Assert.Equal("ผ่านการอนุมัติแล้ว", snapshot.Claims[0].Text);
        Assert.Equal("owner-sheet-1", snapshot.Claims[0].SourceReference);
    }

    [Fact]
    public async Task SameKeyDifferentBodyReturns409()
    {
        var client = await _fixture.CreateLoggedInClientAsync();
        var productId = await CreateProductAsync(client, "สินค้าทดสอบ B");
        await CreateClaimAsync(client, productId, "ข้อความที่อนุมัติ", "approved", "owner-sheet-2");
        var campaignId = await CreateCampaignAsync(client, productId);

        var first = await PostRunAsync(
            client,
            campaignId,
            "conflicting-run-key-01",
            RunBody(targetDurationMs: 25000));
        Assert.Equal(HttpStatusCode.Accepted, first.StatusCode);

        var changed = await PostRunAsync(
            client,
            campaignId,
            "conflicting-run-key-01",
            RunBody(targetDurationMs: 26000));
        Assert.Equal(HttpStatusCode.Conflict, changed.StatusCode);
        Assert.Equal("IDEMPOTENCY_CONFLICT", await ReadCodeAsync(changed));

        await using var scope = _fixture.Factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        Assert.Equal(1, await db.ContentRevisions.CountAsync(x => x.CampaignId == campaignId));
        Assert.Equal(1, await db.Jobs.CountAsync(
            x => x.JobType == "plan_script"
                && x.DedupeKey.StartsWith($"campaign-run:{campaignId:N}:")));
    }

    [Fact]
    public async Task UnapprovedClaimsBlockRun()
    {
        var client = await _fixture.CreateLoggedInClientAsync();
        var productId = await CreateProductAsync(client, "สินค้าทดสอบ C");
        await CreateClaimAsync(client, productId, "ยังไม่อนุมัติ", "pending", "owner-sheet-3");
        var campaignId = await CreateCampaignAsync(client, productId);

        var blocked = await PostRunAsync(
            client,
            campaignId,
            "unapproved-run-key-001",
            RunBody(targetDurationMs: 25000));

        Assert.Equal(HttpStatusCode.Conflict, blocked.StatusCode);
        Assert.Equal("RUN_FACTS_UNAPPROVED", await ReadCodeAsync(blocked));

        await using (var scope = _fixture.Factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            Assert.Equal(0, await db.ContentRevisions.CountAsync(x => x.CampaignId == campaignId));
            Assert.Equal(0, await db.Jobs.CountAsync(x => x.DedupeKey.Contains(campaignId.ToString("N"))));
        }

        await CreateClaimAsync(client, productId, "อนุมัติภายหลัง", "approved", "owner-sheet-4");

        var allowed = await PostRunAsync(
            client,
            campaignId,
            "unapproved-run-key-002",
            RunBody(targetDurationMs: 25000));
        Assert.Equal(HttpStatusCode.Accepted, allowed.StatusCode);
        var run = await ReadRunAsync(allowed);

        await using (var scope = _fixture.Factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var revision = await db.ContentRevisions.AsNoTracking().SingleAsync(x => x.Id == run.RevisionId);
            var snapshot = JsonSerializer.Deserialize<ProductSnapshot>(
                revision.ProductSnapshotJson,
                JsonDefaults.Options);
            Assert.NotNull(snapshot);
            Assert.Single(snapshot!.Claims);
            Assert.Equal("อนุมัติภายหลัง", snapshot.Claims[0].Text);
        }
    }

    [Fact]
    public async Task ProductEditDoesNotMutateOldRevision()
    {
        var client = await _fixture.CreateLoggedInClientAsync();
        var productId = await CreateProductAsync(client, "ชื่อเดิม");
        await CreateClaimAsync(client, productId, "ข้อความเดิม", "approved", "owner-sheet-5");
        var campaignId = await CreateCampaignAsync(client, productId);

        var first = await PostRunAsync(
            client,
            campaignId,
            "immutable-run-key-001",
            RunBody(targetDurationMs: 25000));
        Assert.Equal(HttpStatusCode.Accepted, first.StatusCode);
        var firstRun = await ReadRunAsync(first);

        string originalSnapshotJson;
        string originalSnapshotHash;

        await using (var scope = _fixture.Factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var revision = await db.ContentRevisions.AsNoTracking().SingleAsync(x => x.Id == firstRun.RevisionId);
            originalSnapshotJson = revision.ProductSnapshotJson;
            originalSnapshotHash = ManifestHasher.HashCanonicalJson(originalSnapshotJson);
        }

        await using (var scope = _fixture.Factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var product = await db.Products.SingleAsync(x => x.Id == productId);
            product.Title = "ชื่อที่แก้ไข";
            product.Version += 1;
            product.UpdatedAt = DateTimeOffset.UtcNow;

            var claim = await db.ProductClaims.SingleAsync(x => x.ProductId == productId);
            claim.Text = "ข้อความที่แก้ไข";
            await db.SaveChangesAsync();
        }

        var second = await PostRunAsync(
            client,
            campaignId,
            "immutable-run-key-002",
            RunBody(targetDurationMs: 25000));
        Assert.Equal(HttpStatusCode.Accepted, second.StatusCode);
        var secondRun = await ReadRunAsync(second);
        Assert.NotEqual(firstRun.RevisionId, secondRun.RevisionId);

        await using (var scope = _fixture.Factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var oldRevision = await db.ContentRevisions.AsNoTracking().SingleAsync(x => x.Id == firstRun.RevisionId);
            var newRevision = await db.ContentRevisions.AsNoTracking().SingleAsync(x => x.Id == secondRun.RevisionId);

            Assert.Equal(originalSnapshotJson, oldRevision.ProductSnapshotJson);
            Assert.Equal(originalSnapshotHash, ManifestHasher.HashCanonicalJson(oldRevision.ProductSnapshotJson));

            var oldSnapshot = JsonSerializer.Deserialize<ProductSnapshot>(
                oldRevision.ProductSnapshotJson,
                JsonDefaults.Options);
            Assert.NotNull(oldSnapshot);
            Assert.Equal("ชื่อเดิม", oldSnapshot!.Title);
            Assert.Equal("ข้อความเดิม", oldSnapshot.Claims.Single().Text);

            var newSnapshot = JsonSerializer.Deserialize<ProductSnapshot>(
                newRevision.ProductSnapshotJson,
                JsonDefaults.Options);
            Assert.NotNull(newSnapshot);
            Assert.Equal("ชื่อที่แก้ไข", newSnapshot!.Title);
            Assert.Equal("ข้อความที่แก้ไข", newSnapshot.Claims.Single().Text);
            Assert.Equal(2, newSnapshot.Version);
        }
    }

    [Fact]
    public async Task UnattestedAssetBlocksRun()
    {
        var client = await _fixture.CreateLoggedInClientAsync();
        var productId = await CreateProductAsync(client, "สินค้าทดสอบ D");
        await CreateClaimAsync(client, productId, "ข้อความที่อนุมัติ", "approved", "owner-sheet-6");
        var campaignId = await CreateCampaignAsync(client, productId);

        await using (var scope = _fixture.Factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var asset = new Asset
            {
                Id = Guid.NewGuid(),
                Sha256 = new string('d', 64),
                RelativePath = $"assets/sha256/dd/{new string('d', 64)}.jpg",
                Kind = AssetKind.Image,
                Mime = "image/jpeg",
                Bytes = 16,
                RightsStatus = "unknown",
                SourceReference = string.Empty,
                CloudUseAllowed = false,
                CreatedAt = DateTimeOffset.UtcNow
            };
            db.Assets.Add(asset);
            db.ProductAssets.Add(new ProductAsset
            {
                ProductId = productId,
                AssetId = asset.Id,
                DisplayOrder = 0
            });
            await db.SaveChangesAsync();
        }

        var blocked = await PostRunAsync(
            client,
            campaignId,
            "unattested-run-key-01",
            RunBody(targetDurationMs: 25000));

        Assert.Equal(HttpStatusCode.Conflict, blocked.StatusCode);
        Assert.Equal("RUN_ASSET_RIGHTS_UNATTESTED", await ReadCodeAsync(blocked));

        await using (var scope = _fixture.Factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            Assert.Equal(0, await db.ContentRevisions.CountAsync(x => x.CampaignId == campaignId));
        }
    }

    private async Task<Guid> CreateProductAsync(HttpClient client, string title)
    {
        var response = await client.PostAsJsonAsync("/api/v1/products", new
        {
            title,
            market = "TH",
            locale = "th-TH",
            category = "test"
        });

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return document.RootElement.GetProperty("id").GetGuid();
    }

    private async Task<Guid> CreateClaimAsync(
        HttpClient client,
        Guid productId,
        string text,
        string status,
        string sourceReference)
    {
        var response = await client.PostAsJsonAsync($"/api/v1/products/{productId}/claims", new
        {
            text,
            sourceReference,
            status
        });

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return document.RootElement.GetProperty("id").GetGuid();
    }

    private async Task<Guid> CreateCampaignAsync(HttpClient client, Guid productId)
    {
        var channelId = await CreateChannelAsync();

        var response = await client.PostAsJsonAsync("/api/v1/campaigns", new
        {
            productId,
            channelId,
            mode = "template_only",
            locale = "th-TH",
            targetDurationMs = 25000,
            budgetUsd = 5m
        });

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return document.RootElement.GetProperty("id").GetGuid();
    }

    private async Task<Guid> CreateChannelAsync()
    {
        await using var scope = _fixture.Factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var channel = new Channel
        {
            Id = Guid.NewGuid(),
            Platform = "tiktok",
            DisplayName = "ทดสอบ " + Guid.NewGuid().ToString("N")[..8],
            Market = "TH",
            Locale = "th-TH",
            Timezone = "Asia/Bangkok",
            Status = "active",
            CreatedAt = DateTimeOffset.UtcNow
        };
        db.Channels.Add(channel);
        await db.SaveChangesAsync();
        return channel.Id;
    }

    private static object RunBody(int targetDurationMs)
        => new
        {
            mode = "template_only",
            targetDurationMs,
            autoGenerateVoice = false,
            cloudVideoSceneLimit = 0,
            productSnapshot = new
            {
                title = "client supplied title",
                claims = new[] { new { text = "client supplied claim" } }
            },
            price = 0.01m
        };

    private static async Task<HttpResponseMessage> PostRunAsync(
        HttpClient client,
        Guid campaignId,
        string idempotencyKey,
        object body)
    {
        using var request = new HttpRequestMessage(
            HttpMethod.Post,
            $"/api/v1/campaigns/{campaignId}/runs")
        {
            Content = JsonContent.Create(body)
        };
        request.Headers.Add("Idempotency-Key", idempotencyKey);
        return await client.SendAsync(request);
    }

    private static async Task<RunIds> ReadRunAsync(HttpResponseMessage response)
    {
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return new RunIds(
            document.RootElement.GetProperty("jobId").GetGuid(),
            document.RootElement.GetProperty("revisionId").GetGuid());
    }

    private static async Task<string?> ReadCodeAsync(HttpResponseMessage response)
    {
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return document.RootElement.TryGetProperty("code", out var code)
            ? code.GetString()
            : null;
    }

    private sealed record RunIds(Guid JobId, Guid RevisionId);
}
