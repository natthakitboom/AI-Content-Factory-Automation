using ContentFactory.Cli.Commands;
using ContentFactory.Infrastructure.Persistence.Entities;
using ContentFactory.IntegrationTests.Infrastructure;
using Microsoft.AspNetCore.Identity;

namespace ContentFactory.IntegrationTests;

[Collection(PostgresSchemaCollection.Name)]
public sealed class InitOwnerCommandTests
{
    private readonly PostgresSchemaFixture _fixture;

    public InitOwnerCommandTests(PostgresSchemaFixture fixture)
    {
        _fixture = fixture;
    }

    [Fact]
    public async Task CreatesOwnerWithHashedPasswordAndRejectsShortPassword()
    {
        var hasher = new PasswordHasher<OwnerUser>();
        await using var db = _fixture.CreateContext();
        var command = new InitOwnerCommand(db, hasher);
        var username = "owner-" + Guid.NewGuid().ToString("N")[..8];
        const string password = "correct horse battery staple";

        var exitCode = await command.ExecuteAsync(username, password);

        Assert.Equal(0, exitCode);
        var owner = db.OwnerUsers.Single(x => x.Username == username);
        Assert.True(owner.Active);
        Assert.NotEqual(password, owner.PasswordHash);
        Assert.Equal(
            PasswordVerificationResult.Success,
            hasher.VerifyHashedPassword(owner, owner.PasswordHash, password));

        var rejected = await command.ExecuteAsync(username + "-x", "short");
        Assert.Equal(2, rejected);
        Assert.DoesNotContain(db.OwnerUsers, x => x.Username == username + "-x");
    }
}
