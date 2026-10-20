using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using ContentFactory.Contracts;
using ContentFactory.Infrastructure.Persistence;
using ContentFactory.Infrastructure.Persistence.Entities;
using ContentFactory.Infrastructure.Storage;
using ContentFactory.IntegrationTests.Api;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace ContentFactory.IntegrationTests;

/// <summary>
/// T03 end-to-end ingest tests against real PostgreSQL and a temporary DATA_ROOT.
/// No asset is ever exposed through a public URL; every read is authenticated.
/// </summary>
[Collection(ApiCollection.Name)]
public sealed class AssetIngestTests
{
    private const string Attested = "owner_attested";
    private readonly ApiTestFixture _fixture;

    public AssetIngestTests(ApiTestFixture fixture)
    {
        _fixture = fixture;
    }

    [Fact]
    public async Task RightsRequiredForProduction()
    {
        var productId = await CreateProductAsync();
        var client = await _fixture.CreateLoggedInClientAsync();
        var bytes = CreateJpegBytes(512, seed: 1);

        using (var noRights = CreateUploadContent(bytes, "photo.jpg", rights: "unknown"))
        {
            var response = await client.PostAsync($"/api/v1/products/{productId}/assets", noRights);
            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
            Assert.Equal("ASSET_RIGHTS_REQUIRED", await ReadCodeAsync(response));
        }

        using (var noSource = CreateUploadContent(bytes, "photo.jpg", rights: Attested, source: "  "))
        {
            var response = await client.PostAsync($"/api/v1/products/{productId}/assets", noSource);
            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
            Assert.Equal("ASSET_RIGHTS_REQUIRED", await ReadCodeAsync(response));
        }

        Assert.Equal(0, await CountAssetsAsync(bytes));

        using (var attested = CreateUploadContent(bytes, "photo.jpg", rights: Attested, source: "owner-photo-2026"))
        {
            var response = await client.PostAsync($"/api/v1/products/{productId}/assets", attested);
            Assert.Equal(HttpStatusCode.Created, response.StatusCode);
            var asset = await ReadAssetAsync(response);

            await using var scope = _fixture.Factory.Services.CreateAsyncScope();
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var row = await db.Assets.AsNoTracking().SingleAsync(x => x.Id == asset.Id);
            Assert.Equal(Attested, row.RightsStatus);
            Assert.Equal("owner-photo-2026", row.SourceReference);
            Assert.False(row.CloudUseAllowed);
        }
    }

    [Fact]
    public async Task ThaiFilenameWithSpacesQuotesAndSemicolonIsAccepted()
    {
        var productId = await CreateProductAsync();
        var client = await _fixture.CreateLoggedInClientAsync();
        var bytes = CreateJpegBytes(1024, seed: 2);

        using var content = CreateUploadContent(
            bytes,
            "รูปสินค้า 01 'ทดสอบ';.jpg",
            rights: Attested,
            source: "owner-photo-2026");

        var response = await client.PostAsync($"/api/v1/products/{productId}/assets", content);
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);

        var asset = await ReadAssetAsync(response);
        Assert.Equal("image/jpeg", asset.MimeType);
        Assert.Matches("^[0-9a-f]{64}$", asset.Sha256);
        Assert.Equal($"assets/sha256/{asset.Sha256[..2]}/{asset.Sha256}.jpg", asset.RelativePath);
        Assert.DoesNotContain("รูป", asset.RelativePath, StringComparison.Ordinal);
        Assert.DoesNotContain("\"", asset.RelativePath, StringComparison.Ordinal);
        Assert.DoesNotContain(";", asset.RelativePath, StringComparison.Ordinal);

        var fullPath = AssetPathResolver.ResolveExisting(_fixture.DataRoot, asset.RelativePath);
        Assert.True(File.Exists(fullPath));
        Assert.Equal(bytes, await File.ReadAllBytesAsync(fullPath));

        var download = await client.GetAsync($"/api/v1/assets/{asset.Id}/content");
        Assert.Equal(HttpStatusCode.OK, download.StatusCode);
        Assert.Equal(bytes, await download.Content.ReadAsByteArrayAsync());
    }

    [Fact]
    public async Task DuplicateUploadReturnsSameAssetAndOneRow()
    {
        var productId = await CreateProductAsync();
        var client = await _fixture.CreateLoggedInClientAsync();
        var bytes = CreateJpegBytes(700, seed: 3);

        using var firstContent = CreateUploadContent(bytes, "one.jpg", source: "owner-photo-2026");
        var firstResponse = await client.PostAsync($"/api/v1/products/{productId}/assets", firstContent);
        Assert.Equal(HttpStatusCode.Created, firstResponse.StatusCode);
        var first = await ReadAssetAsync(firstResponse);

        using var secondContent = CreateUploadContent(bytes, "two.jpg", source: "owner-photo-2026");
        var secondResponse = await client.PostAsync($"/api/v1/products/{productId}/assets", secondContent);
        Assert.Equal(HttpStatusCode.Created, secondResponse.StatusCode);
        var second = await ReadAssetAsync(secondResponse);

        Assert.Equal(first.Id, second.Id);
        Assert.Equal(first.Sha256, second.Sha256);

        await using var scope = _fixture.Factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        Assert.Equal(1, await db.Assets.CountAsync(x => x.Sha256 == first.Sha256 && x.Kind == AssetKind.Image));
        Assert.Equal(1, await db.ProductAssets.CountAsync(
            x => x.ProductId == productId && x.AssetId == first.Id));
    }

    [Fact]
    public async Task SvgDisguisedAsJpegRejectedByApi()
    {
        var productId = await CreateProductAsync();
        var client = await _fixture.CreateLoggedInClientAsync();
        var svg = Encoding.UTF8.GetBytes(
            "<svg xmlns=\"http://www.w3.org/2000/svg\"><script>alert(1)</script></svg>");

        using var content = CreateUploadContent(svg, "photo.jpg", source: "owner-photo-2026");
        var response = await client.PostAsync($"/api/v1/products/{productId}/assets", content);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("ASSET_MIME_UNSUPPORTED", await ReadCodeAsync(response));
    }

    [Fact]
    public async Task RangeRequestReturnsPartialContent()
    {
        var productId = await CreateProductAsync();
        var client = await _fixture.CreateLoggedInClientAsync();
        var bytes = CreateJpegBytes(4096, seed: 4);

        using var content = CreateUploadContent(bytes, "range.jpg", source: "owner-photo-2026");
        var upload = await client.PostAsync($"/api/v1/products/{productId}/assets", content);
        Assert.Equal(HttpStatusCode.Created, upload.StatusCode);
        var asset = await ReadAssetAsync(upload);

        using var rangeRequest = new HttpRequestMessage(
            HttpMethod.Get,
            $"/api/v1/assets/{asset.Id}/content");
        rangeRequest.Headers.Range = new RangeHeaderValue(100, 199);

        var partial = await client.SendAsync(rangeRequest);
        Assert.Equal(HttpStatusCode.PartialContent, partial.StatusCode);
        Assert.Equal("bytes", partial.Headers.AcceptRanges.Single());
        Assert.Equal($"bytes 100-199/{bytes.Length}", partial.Content.Headers.ContentRange!.ToString());
        Assert.Equal(bytes[100..200], await partial.Content.ReadAsByteArrayAsync());

        using var invalidRequest = new HttpRequestMessage(
            HttpMethod.Get,
            $"/api/v1/assets/{asset.Id}/content");
        invalidRequest.Headers.Range = new RangeHeaderValue(bytes.Length + 10, null);

        var invalid = await client.SendAsync(invalidRequest);
        Assert.Equal(HttpStatusCode.RequestedRangeNotSatisfiable, invalid.StatusCode);
        Assert.Equal($"bytes */{bytes.Length}", invalid.Content.Headers.ContentRange!.ToString());
    }

    [Fact]
    public async Task CleanupNeverDeletesReferencedAssets()
    {
        var productId = await CreateProductAsync();
        var client = await _fixture.CreateLoggedInClientAsync();
        var bytes = CreateJpegBytes(900, seed: 5);

        using var content = CreateUploadContent(bytes, "keep.jpg", source: "owner-photo-2026");
        var upload = await client.PostAsync($"/api/v1/products/{productId}/assets", content);
        Assert.Equal(HttpStatusCode.Created, upload.StatusCode);
        var asset = await ReadAssetAsync(upload);

        var orphanDirectory = Path.Combine(_fixture.DataRoot, "temp", "ingest");
        Directory.CreateDirectory(orphanDirectory);
        var orphan = Path.Combine(orphanDirectory, "orphan.part");
        await File.WriteAllBytesAsync(orphan, [1, 2, 3]);

        await using var scope = _fixture.Factory.Services.CreateAsyncScope();
        var store = scope.ServiceProvider.GetRequiredService<FileSystemAssetStore>();
        var removed = await store.CleanupTempAsync(TimeSpan.Zero, CancellationToken.None);

        Assert.True(removed >= 1);
        Assert.False(File.Exists(orphan));

        var assetPath = AssetPathResolver.ResolveExisting(_fixture.DataRoot, asset.RelativePath);
        Assert.True(File.Exists(assetPath));

        await using var stream = await store.OpenReadAsync(asset.Id, CancellationToken.None);
        using var buffer = new MemoryStream();
        await stream.CopyToAsync(buffer);
        Assert.Equal(bytes, buffer.ToArray());
    }

    [Fact]
    public async Task AssetEndpointsRequireAuthentication()
    {
        var productId = await CreateProductAsync();
        var client = _fixture.CreateClient();
        var bytes = CreateJpegBytes(128, seed: 6);

        using (var content = CreateUploadContent(bytes, "anon.jpg", source: "owner-photo-2026"))
        {
            var upload = await client.PostAsync($"/api/v1/products/{productId}/assets", content);
            Assert.Equal(HttpStatusCode.Unauthorized, upload.StatusCode);
        }

        var download = await client.GetAsync($"/api/v1/assets/{Guid.NewGuid()}/content");
        Assert.Equal(HttpStatusCode.Unauthorized, download.StatusCode);
    }

    [Fact]
    public async Task UnknownAssetReturnsNotFound()
    {
        var client = await _fixture.CreateLoggedInClientAsync();

        var response = await client.GetAsync($"/api/v1/assets/{Guid.NewGuid()}/content");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Equal("ASSET_NOT_FOUND", await ReadCodeAsync(response));
    }

    private async Task<Guid> CreateProductAsync()
    {
        await using var scope = _fixture.Factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var now = DateTimeOffset.UtcNow;
        var product = new Product
        {
            Id = Guid.NewGuid(),
            Title = "สินค้าทดสอบ " + Guid.NewGuid().ToString("N")[..8],
            Market = "TH",
            Locale = "th-TH",
            CreatedAt = now,
            UpdatedAt = now
        };
        db.Products.Add(product);
        await db.SaveChangesAsync();
        return product.Id;
    }

    private async Task<int> CountAssetsAsync(byte[] bytes)
    {
        var sha = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(bytes))
            .ToLowerInvariant();

        await using var scope = _fixture.Factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        return await db.Assets.CountAsync(x => x.Sha256 == sha);
    }

    private static MultipartFormDataContent CreateUploadContent(
        byte[] bytes,
        string fileName,
        string kind = "image",
        string rights = Attested,
        string source = "owner-photo-2026",
        bool cloudUseAllowed = false)
    {
        var content = new MultipartFormDataContent();
        var file = new ByteArrayContent(bytes);
        file.Headers.ContentType = new MediaTypeHeaderValue("image/jpeg");
        content.Add(file, "file", fileName);
        content.Add(new StringContent(kind), "kind");
        content.Add(new StringContent(rights), "rightsStatus");
        content.Add(new StringContent(source), "sourceReference");
        content.Add(new StringContent(cloudUseAllowed ? "true" : "false"), "cloudUseAllowed");
        return content;
    }

    private static async Task<AssetRef> ReadAssetAsync(HttpResponseMessage response)
    {
        var json = await response.Content.ReadAsStringAsync();
        var asset = JsonSerializer.Deserialize<AssetRef>(json, JsonDefaults.Options);
        Assert.NotNull(asset);
        return asset!;
    }

    private static async Task<string?> ReadCodeAsync(HttpResponseMessage response)
    {
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return document.RootElement.TryGetProperty("code", out var code)
            ? code.GetString()
            : null;
    }

    private static byte[] CreateJpegBytes(int length, byte seed)
    {
        var bytes = new byte[length];
        bytes[0] = 0xFF;
        bytes[1] = 0xD8;
        bytes[2] = 0xFF;
        bytes[3] = 0xE0;
        for (var index = 4; index < length; index++)
        {
            bytes[index] = (byte)((seed + index) % 251);
        }

        return bytes;
    }
}
