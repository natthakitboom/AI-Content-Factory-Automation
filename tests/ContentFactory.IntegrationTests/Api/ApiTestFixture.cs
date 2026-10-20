using ContentFactory.Infrastructure.Persistence;
using ContentFactory.Infrastructure.Persistence.Entities;
using ContentFactory.IntegrationTests.Infrastructure;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using System.Net.Http.Json;

namespace ContentFactory.IntegrationTests.Api;

public sealed class ApiTestFixture : IAsyncLifetime
{
    public const string Username = "owner";
    public const string Password = "correct horse battery staple";
    public const string TestSecret = "super-secret-value-123";

    private PostgresTestDatabase? _database;
    private string? _dataRoot;

    public ApiFactory Factory { get; private set; } = null!;
    public CapturingLoggerProvider Logs { get; } = new();
    public string DataRoot => _dataRoot
        ?? throw new InvalidOperationException("ApiTestFixture is not initialized.");

    public async Task InitializeAsync()
    {
        _dataRoot = Path.Combine(
            Path.GetTempPath(),
            "contentfactory-tests",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dataRoot);

        _database = await PostgresTestDatabase.CreateAsync();
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseNpgsql(_database.ConnectionString)
            .Options;

        await using (var db = new AppDbContext(options))
        {
            await db.Database.MigrateAsync();
        }

        Factory = new ApiFactory(_database.ConnectionString, _dataRoot, Logs);

        using var scope = Factory.Services.CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var owner = new OwnerUser
        {
            Id = Guid.NewGuid(),
            Username = Username,
            Active = true,
            SessionVersion = 0,
            CreatedAt = DateTimeOffset.UtcNow
        };
        owner.PasswordHash = new PasswordHasher<OwnerUser>().HashPassword(owner, Password);
        dbContext.OwnerUsers.Add(owner);
        await dbContext.SaveChangesAsync();
    }

    public HttpClient CreateClient(bool handleCookies = true)
        => Factory.CreateClient(new WebApplicationFactoryClientOptions
        {
            BaseAddress = new Uri("http://127.0.0.1"),
            HandleCookies = handleCookies,
            AllowAutoRedirect = false
        });

    public async Task<HttpClient> CreateLoggedInClientAsync()
    {
        var client = CreateClient();
        var token = await GetCsrfTokenAsync(client);
        client.DefaultRequestHeaders.Add("X-CSRF-TOKEN", token);

        var response = await client.PostAsJsonAsync("/api/v1/auth/login", new
        {
            username = Username,
            password = Password
        });
        Assert.Equal(System.Net.HttpStatusCode.NoContent, response.StatusCode);
        return client;
    }

    public static async Task<string> GetCsrfTokenAsync(HttpClient client)
    {
        var response = await client.GetAsync("/api/v1/auth/csrf");
        Assert.Equal(System.Net.HttpStatusCode.OK, response.StatusCode);
        using var document = System.Text.Json.JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return document.RootElement.GetProperty("token").GetString()
            ?? throw new InvalidOperationException("CSRF token missing");
    }

    public async Task DisposeAsync()
    {
        if (Factory is not null)
        {
            Factory.Dispose();
        }

        if (_database is not null)
        {
            await _database.DisposeAsync();
        }

        if (_dataRoot is not null)
        {
            try
            {
                Directory.Delete(_dataRoot, recursive: true);
            }
            catch (IOException)
            {
                // Best-effort cleanup of a test sandbox.
            }
        }
    }
}

[CollectionDefinition(Name)]
public sealed class ApiCollection : ICollectionFixture<ApiTestFixture>
{
    public const string Name = "api";
}
