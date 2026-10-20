using System.Text.RegularExpressions;

namespace ContentFactory.Infrastructure.Security;

/// <summary>
/// Removes known secret values and common credential patterns before text is logged.
/// Redaction is best-effort defence in depth; secrets must never be placed in log
/// messages in the first place.
/// </summary>
public sealed class SecretRedactor
{
    public const string Replacement = "[REDACTED]";

    private static readonly Regex CredentialPattern = new(
        @"(?ix)(\b(?:access_token|api[_-]?key|apikey|authorization|bearer|password|secret|token)\b\s*[=:]\s*)([^&\s,;""']+)",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private readonly string[] _secrets;

    public SecretRedactor(IEnumerable<string?> secrets)
    {
        _secrets = secrets
            .Where(value => !string.IsNullOrWhiteSpace(value) && value.Length >= 4)
            .Select(value => value!)
            .Distinct(StringComparer.Ordinal)
            .OrderByDescending(value => value.Length)
            .ToArray();
    }

    public string Redact(string? text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return string.Empty;
        }

        var redacted = text;
        foreach (var secret in _secrets)
        {
            redacted = redacted.Replace(secret, Replacement, StringComparison.Ordinal);
        }

        return CredentialPattern.Replace(redacted, match => match.Groups[1].Value + Replacement);
    }
}
