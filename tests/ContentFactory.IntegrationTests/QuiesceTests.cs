using System.Net;
using System.Net.Http.Json;
using ContentFactory.Infrastructure.Persistence;
using ContentFactory.Infrastructure.Storage;
using ContentFactory.IntegrationTests.Api;
using Microsoft.Extensions.DependencyInjection;

namespace ContentFactory.IntegrationTests;

[Collection(ApiCollection.Name)]
public sealed class QuiesceTests
{
    private readonly ApiTestFixture _fixture;

    public QuiesceTests(ApiTestFixture fixture)
    {
        _fixture = fixture;
    }

    [Fact]
    public async Task MutationsAreRefusedWhileABackupHoldsTheFlag()
    {
        var client = await _fixture.CreateLoggedInClientAsync();
        await using var scope = _fixture.Factory.Services.CreateAsyncScope();
        var flags = new SystemFlagStore(scope.ServiceProvider.GetRequiredService<AppDbContext>());
        await flags.SetAsync(SystemFlagStore.WriteQuiesce, "true");
        try
        {
            var blocked = await client.PostAsJsonAsync(
                "/api/v1/products", new { title = "x", market = "TH", locale = "th-TH" });
            var read = await client.GetAsync("/api/v1/analytics/overview");
            var health = await client.GetAsync("/health/live");

            Assert.Equal(HttpStatusCode.ServiceUnavailable, blocked.StatusCode);
            Assert.True(blocked.Headers.Contains("Retry-After"));
            Assert.Equal(HttpStatusCode.OK, read.StatusCode);
            Assert.Equal(HttpStatusCode.OK, health.StatusCode);
        }
        finally
        {
            await flags.SetAsync(SystemFlagStore.WriteQuiesce, null);
        }

        var allowed = await client.PostAsJsonAsync(
            "/api/v1/products", new { title = "after backup", market = "TH", locale = "th-TH" });
        Assert.Equal(HttpStatusCode.Created, allowed.StatusCode);
    }
}
