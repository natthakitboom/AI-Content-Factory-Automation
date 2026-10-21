using System.Net.Http.Json;
using System.Text.Json;
using ContentFactory.IntegrationTests.Api;

namespace ContentFactory.IntegrationTests;

[Collection(ApiCollection.Name)]
public sealed class SystemModeTests
{
    private readonly ApiTestFixture _fixture;

    public SystemModeTests(ApiTestFixture fixture)
    {
        _fixture = fixture;
    }

    [Fact]
    public async Task ModeEndpointReportsMockBannerFromExplicitProviderNames()
    {
        var client = await _fixture.CreateLoggedInClientAsync();

        var response = await client.GetAsync("/api/v1/system/mode");

        response.EnsureSuccessStatusCode();
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("MOCK MODE", body.GetProperty("banner").GetString());
        Assert.True(body.GetProperty("mock").GetBoolean());
        Assert.Equal("mock", body.GetProperty("video").GetString());
    }

    [Fact]
    public async Task ModeEndpointRequiresLogin()
    {
        using var anonymous = _fixture.Factory.CreateClient();

        var response = await anonymous.GetAsync("/api/v1/system/mode");

        Assert.NotEqual(System.Net.HttpStatusCode.OK, response.StatusCode);
    }
}
