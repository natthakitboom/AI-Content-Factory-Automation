using ContentFactory.Infrastructure.Security;

namespace ContentFactory.UnitTests;

public sealed class SecretRedactorTests
{
    [Fact]
    public void RemovesKnownSecretValuesAndCredentialPatterns()
    {
        const string knownSecret = "super-secret-value-123";
        var redactor = new SecretRedactor([knownSecret, null, ""]);

        var result = redactor.Redact(
            $"Authorization: Bearer {knownSecret} access_token=other-token password=hunter2");

        Assert.DoesNotContain(knownSecret, result, StringComparison.Ordinal);
        Assert.DoesNotContain("other-token", result, StringComparison.Ordinal);
        Assert.DoesNotContain("hunter2", result, StringComparison.Ordinal);
        Assert.Contains(SecretRedactor.Replacement, result, StringComparison.Ordinal);
    }

    [Fact]
    public void LeavesOrdinaryTextReadable()
    {
        var redactor = new SecretRedactor(["abcdef"]);

        Assert.Equal("render scene 2 of 4", redactor.Redact("render scene 2 of 4"));
        Assert.Equal(string.Empty, redactor.Redact(null));
    }
}
