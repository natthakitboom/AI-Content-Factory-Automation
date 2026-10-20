using System.Security.Cryptography;

namespace ContentFactory.Infrastructure.Media;

/// <summary>
/// Identifies the exact font file used for caption burn-in. The manifest and approval bind
/// this value, so changing the font file invalidates prior approvals. There is deliberately
/// no built-in fingerprint: a host must hash the licensed font it will actually render.
/// </summary>
public static class FontFingerprintIdentity
{
    public static string ComputeFromFile(string fontPath)
    {
        if (string.IsNullOrWhiteSpace(fontPath))
        {
            throw new ArgumentException("A font file path is required.", nameof(fontPath));
        }

        using var stream = File.OpenRead(fontPath);
        return Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
    }

    public static bool IsSha256(string? value)
        => value is { Length: 64 } &&
            value.All(character =>
                character is >= '0' and <= '9' or
                >= 'a' and <= 'f');
}
