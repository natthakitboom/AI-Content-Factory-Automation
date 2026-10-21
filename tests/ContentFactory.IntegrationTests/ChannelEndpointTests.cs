using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using ContentFactory.IntegrationTests.Api;

namespace ContentFactory.IntegrationTests;

[Collection(ApiCollection.Name)]
public sealed class ChannelEndpointTests
{
    private readonly ApiTestFixture _fixture;

    public ChannelEndpointTests(ApiTestFixture fixture)
    {
        _fixture = fixture;
    }

    [Fact]
    public async Task OwnerCanCreateAndListAChannel()
    {
        var client = await _fixture.CreateLoggedInClientAsync();

        var created = await client.PostAsJsonAsync("/api/v1/channels", new
        {
            platform = "tiktok", displayName = "My shop", market = "TH", locale = "th-TH", timezone = "Asia/Bangkok"
        });

        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var id = (await created.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid();
        var list = await client.GetFromJsonAsync<JsonElement>("/api/v1/channels");
        Assert.Contains(list.EnumerateArray(), x => x.GetProperty("id").GetGuid() == id
            && x.GetProperty("status").GetString() == "active");
    }

    [Theory]
    [InlineData("", "TH", "th-TH", "Asia/Bangkok")]
    [InlineData("Shop", "TH", "th-TH", "Mars/Olympus")]
    [InlineData("Shop", "", "th-TH", "Asia/Bangkok")]
    [InlineData("Shop", "TH", "", "Asia/Bangkok")]
    public async Task InvalidChannelsAreRejected(string name, string market, string locale, string timezone)
    {
        var client = await _fixture.CreateLoggedInClientAsync();

        var response = await client.PostAsJsonAsync("/api/v1/channels", new
        {
            platform = "tiktok", displayName = name, market, locale, timezone
        });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }
}
