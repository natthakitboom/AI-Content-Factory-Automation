using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using ContentFactory.IntegrationTests.Api;

namespace ContentFactory.IntegrationTests;

[Collection(ApiCollection.Name)]
public sealed class AuthenticationTests
{
    private readonly ApiTestFixture _fixture;

    public AuthenticationTests(ApiTestFixture fixture)
    {
        _fixture = fixture;
    }

    [Fact]
    public async Task AnonymousMutationReturns401()
    {
        var client = _fixture.CreateClient();

        var response = await client.PostAsync("/api/v1/auth/logout", content: null);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task MissingCsrfRejected()
    {
        var client = await _fixture.CreateLoggedInClientAsync();
        client.DefaultRequestHeaders.Remove("X-CSRF-TOKEN");

        var response = await client.PostAsync("/api/v1/auth/logout", content: null);

        Assert.True(
            response.StatusCode is HttpStatusCode.BadRequest or HttpStatusCode.Forbidden,
            $"Expected CSRF rejection, got {(int)response.StatusCode}");
    }

    [Fact]
    public async Task ForeignOriginRejected()
    {
        var client = _fixture.CreateClient();
        var token = await ApiTestFixture.GetCsrfTokenAsync(client);

        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/v1/auth/login");
        request.Headers.Add("X-CSRF-TOKEN", token);
        request.Headers.Add("Origin", "https://evil.example");
        request.Content = JsonContent.Create(new
        {
            username = ApiTestFixture.Username,
            password = ApiTestFixture.Password
        });

        var response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task InvalidHostRejected()
    {
        var client = _fixture.CreateClient();
        using var request = new HttpRequestMessage(HttpMethod.Get, "/health/live");
        request.Headers.TryAddWithoutValidation("Host", "evil.example");

        var response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task SecretsNeverAppearInLogs()
    {
        var client = _fixture.CreateClient();

        var response = await client.GetAsync(
            $"/api/v1/auth/csrf?access_token={ApiTestFixture.TestSecret}");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.NotEmpty(_fixture.Logs.Messages);
        Assert.DoesNotContain(
            ApiTestFixture.TestSecret,
            string.Join('\n', _fixture.Logs.Messages),
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task LoginCookieIsHttpOnlyAndSameSiteStrict()
    {
        var client = _fixture.CreateClient();
        var token = await ApiTestFixture.GetCsrfTokenAsync(client);

        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/v1/auth/login");
        request.Headers.Add("X-CSRF-TOKEN", token);
        request.Content = JsonContent.Create(new
        {
            username = ApiTestFixture.Username,
            password = ApiTestFixture.Password
        });

        var response = await client.SendAsync(request);
        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);

        var authCookie = response.Headers
            .GetValues("Set-Cookie")
            .Single(value => value.StartsWith("cf_auth=", StringComparison.Ordinal))
            .ToLowerInvariant();

        Assert.Contains("httponly", authCookie, StringComparison.Ordinal);
        Assert.Contains("samesite=strict", authCookie, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RepeatedWrongPasswordsAreThrottled()
    {
        var client = _fixture.CreateClient();
        var token = await ApiTestFixture.GetCsrfTokenAsync(client);
        client.DefaultRequestHeaders.Add("X-CSRF-TOKEN", token);

        for (var attempt = 0; attempt < 5; attempt++)
        {
            var failed = await client.PostAsJsonAsync("/api/v1/auth/login", new
            {
                username = "throttled-owner",
                password = "wrong-password"
            });
            Assert.Equal(HttpStatusCode.Unauthorized, failed.StatusCode);
        }

        var blocked = await client.PostAsJsonAsync("/api/v1/auth/login", new
        {
            username = "throttled-owner",
            password = "wrong-password"
        });

        Assert.Equal(HttpStatusCode.TooManyRequests, blocked.StatusCode);
    }

    [Fact]
    public async Task LogoutInvalidatesPreviousCookie()
    {
        var client = await _fixture.CreateLoggedInClientAsync();

        var logout = await client.PostAsync("/api/v1/auth/logout", content: null);
        Assert.Equal(HttpStatusCode.NoContent, logout.StatusCode);

        var afterLogout = await client.PostAsync("/api/v1/auth/logout", content: null);
        Assert.Equal(HttpStatusCode.Unauthorized, afterLogout.StatusCode);
    }
}
