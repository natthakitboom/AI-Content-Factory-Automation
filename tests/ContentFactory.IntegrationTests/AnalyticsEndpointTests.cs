using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using ContentFactory.Infrastructure.Persistence;
using ContentFactory.Infrastructure.Persistence.Entities;
using ContentFactory.IntegrationTests.Api;
using ContentFactory.IntegrationTests.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace ContentFactory.IntegrationTests;

[Collection(ApiCollection.Name)]
public sealed class AnalyticsEndpointTests
{
    private readonly ApiTestFixture _fixture;

    public AnalyticsEndpointTests(ApiTestFixture fixture)
    {
        _fixture = fixture;
    }

    [Fact]
    public async Task ImportWithoutACsrfTokenIsRejected()
    {
        var client = await _fixture.CreateLoggedInClientAsync();
        client.DefaultRequestHeaders.Remove("X-CSRF-TOKEN");
        using var form = new MultipartFormDataContent
        {
            { new ByteArrayContent(Encoding.UTF8.GetBytes("post_url,views\nx,1\n")), "file", "m.csv" },
            { new StringContent("{}"), "mapping" }
        };

        var response = await client.PostAsync("/api/v1/metrics/imports", form);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains("CSRF_INVALID", await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task EmptyOverviewHasNoData()
    {
        var client = await _fixture.CreateLoggedInClientAsync();
        await using (var scope = _fixture.Factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            await db.Database.ExecuteSqlRawAsync("DELETE FROM app.metric_snapshots");
        }

        var body = await client.GetFromJsonAsync<JsonElement>("/api/v1/analytics/overview");

        Assert.False(body.GetProperty("hasData").GetBoolean());
        Assert.Equal(0, body.GetProperty("bySource").GetArrayLength());
        Assert.True(body.GetProperty("definitions").TryGetProperty("ctr", out _));
    }

    [Fact]
    public async Task ImportThroughTheApiReportsAndReturnsNullsForMissingData()
    {
        var client = await _fixture.CreateLoggedInClientAsync();
        string url = "https://www.tiktok.com/@api/video/" + Guid.NewGuid().ToString("N");
        await using (var scope = _fixture.Factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            await db.Database.ExecuteSqlRawAsync("DELETE FROM app.metric_snapshots");
            var seeded = await ReviewSeed.CreateAsync(db, "approved");
            var channelId = await db.Campaigns.Where(x => x.Id == seeded.CampaignId).Select(x => x.ChannelId).SingleAsync();
            db.Publications.Add(new Publication
            {
                Id = Guid.NewGuid(), VideoVersionId = seeded.VersionId, ChannelId = channelId, PostUrl = url,
                PublishedAt = DateTimeOffset.UtcNow, ProductLinkStatus = "unknown", CreatedAt = DateTimeOffset.UtcNow
            });
            await db.SaveChangesAsync();
        }

        using var form = new MultipartFormDataContent
        {
            { new ByteArrayContent(Encoding.UTF8.GetBytes($"post_url,views\n{url},321\n")), "file", "m.csv" },
            {
                new StringContent(JsonSerializer.Serialize(new
                {
                    source = "tiktok-export", asOf = "2026-10-20T00:00:00Z", publicationColumn = "post_url",
                    publicationKind = "post_url", viewsColumn = "views"
                })),
                "mapping"
            }
        };
        var response = await client.PostAsync("/api/v1/metrics/imports", form);
        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        var report = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(1, report.GetProperty("inserted").GetInt32());

        var overview = await client.GetFromJsonAsync<JsonElement>("/api/v1/analytics/overview");
        var source = overview.GetProperty("bySource")[0].GetProperty("overview");
        Assert.Equal(321, source.GetProperty("views").GetInt64());
        Assert.Equal(JsonValueKind.Null, source.GetProperty("ctr").ValueKind);
        Assert.Equal(JsonValueKind.Null, source.GetProperty("productClicks").ValueKind);
    }
}
