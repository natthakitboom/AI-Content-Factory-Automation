using System.Globalization;
using System.Text.Json;
using ContentFactory.Core.Exceptions;

namespace ContentFactory.Infrastructure.Billing;

/// <summary>
/// Reads the owner-supplied live approval envelope on every paid request. The file is
/// local-only, bounded and fail-closed; ALLOW_PAID_APIS=false always wins. Request count
/// and total spend are enforced by PostgresBudgetGuard inside the same transaction as the
/// reservation, because the envelope itself is immutable owner input.
/// </summary>
public sealed class LiveApprovalGuard
{
    private const int MaxApprovalBytes = 64 * 1024;
    private const int MaxApprovalIdLength = 128;
    private const int MaxProviderLength = 32;
    private const int MaxModelLength = 256;
    private const int MaxApprovedByLength = 128;

    private readonly BudgetOptions _options;
    private readonly TimeProvider _clock;

    public LiveApprovalGuard(BudgetOptions options, TimeProvider clock)
    {
        _options = options;
        _clock = clock;
        _options.Validate();
    }

    public async Task<LiveApprovalEnvelope> ValidateAsync(
        string provider,
        string model,
        CancellationToken ct)
    {
        _options.EnsurePaidApisEnabled();

        if (string.IsNullOrWhiteSpace(provider) || provider.Length > MaxProviderLength)
        {
            throw Invalid("The approval request has an invalid provider.");
        }

        if (string.IsNullOrWhiteSpace(model) || model.Length > MaxModelLength)
        {
            throw Invalid("The approval request has an invalid model.");
        }

        var path = _options.LiveApprovalFile;
        if (string.IsNullOrWhiteSpace(path))
        {
            throw new ApprovalStaleException(
                "APPROVAL_MISSING",
                "LIVE_APPROVAL_FILE is not configured.");
        }

        FileInfo file;
        try
        {
            file = new FileInfo(path);
            if (!file.Exists)
            {
                throw new ApprovalStaleException(
                    "APPROVAL_MISSING",
                    "The configured live approval file does not exist.");
            }

            if (file.Length is <= 0 or > MaxApprovalBytes)
            {
                throw Invalid("The live approval file must be between 1 byte and 64 KiB.");
            }
        }
        catch (ApprovalStaleException)
        {
            throw;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            throw Invalid("The live approval file could not be inspected.");
        }

        string json;
        try
        {
            json = await File.ReadAllTextAsync(path, ct);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            throw Invalid("The live approval file could not be read.");
        }

        if (json.Length > MaxApprovalBytes)
        {
            throw Invalid("The live approval file must be at most 64 KiB.");
        }

        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(json);
        }
        catch (JsonException)
        {
            throw Invalid("The live approval file is not valid JSON.");
        }

        using (document)
        {
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                throw Invalid("The live approval file must contain a JSON object.");
            }

            var approvalId = RequiredString(root, "approvalId", MaxApprovalIdLength);
            var envelopeProvider = RequiredString(root, "provider", MaxProviderLength);
            var envelopeModel = RequiredString(root, "model", MaxModelLength);
            var approvedBy = RequiredString(root, "approvedBy", MaxApprovedByLength);

            if (IsPlaceholder(approvalId) || IsPlaceholder(approvedBy))
            {
                throw Invalid("The live approval still contains placeholder values.");
            }

            if (!string.Equals(envelopeProvider, provider, StringComparison.OrdinalIgnoreCase) ||
                !string.Equals(envelopeModel, model, StringComparison.Ordinal))
            {
                throw new ApprovalStaleException(
                    "APPROVAL_SCOPE_MISMATCH",
                    "The live approval does not cover this provider and model.");
            }

            if (!root.TryGetProperty("allowFallbackProvider", out var fallback))
            {
                throw Invalid(
                    "The live approval must explicitly set allowFallbackProvider to false.");
            }

            if (fallback.ValueKind != JsonValueKind.False)
            {
                throw new ApprovalStaleException(
                    "APPROVAL_FALLBACK_FORBIDDEN",
                    "Provider fallback is not allowed by a live approval envelope.");
            }

            var maxRequestCount = RequiredInt(root, "maxRequestCount");
            if (maxRequestCount is <= 0 or > 10_000)
            {
                throw Invalid("maxRequestCount must be between 1 and 10000.");
            }

            var maxTotalUsd = RequiredDecimal(root, "maxTotalUsd");
            if (maxTotalUsd <= 0m || maxTotalUsd > 1_000_000m)
            {
                throw Invalid("maxTotalUsd must be greater than zero and at most 1000000.");
            }

            var expiresAt = RequiredDateTimeOffset(root, "expiresAt");
            if (expiresAt <= _clock.GetUtcNow())
            {
                throw new ApprovalStaleException(
                    "APPROVAL_EXPIRED",
                    "The live approval has expired.");
            }

            return new LiveApprovalEnvelope(
                approvalId,
                envelopeProvider,
                envelopeModel,
                maxRequestCount,
                maxTotalUsd,
                expiresAt,
                approvedBy);
        }
    }

    private static string RequiredString(JsonElement root, string property, int maxLength)
    {
        if (!root.TryGetProperty(property, out var element) ||
            element.ValueKind != JsonValueKind.String)
        {
            throw Invalid($"The live approval field '{property}' must be a string.");
        }

        var value = element.GetString();
        if (string.IsNullOrWhiteSpace(value) || value.Length > maxLength)
        {
            throw Invalid($"The live approval field '{property}' has an invalid length.");
        }

        return value;
    }

    private static int RequiredInt(JsonElement root, string property)
    {
        if (!root.TryGetProperty(property, out var element) ||
            element.ValueKind != JsonValueKind.Number ||
            !element.TryGetInt32(out var value))
        {
            throw Invalid($"The live approval field '{property}' must be an integer.");
        }

        return value;
    }

    private static decimal RequiredDecimal(JsonElement root, string property)
    {
        if (!root.TryGetProperty(property, out var element) ||
            element.ValueKind != JsonValueKind.Number ||
            !element.TryGetDecimal(out var value))
        {
            throw Invalid($"The live approval field '{property}' must be a decimal number.");
        }

        return value;
    }

    private static DateTimeOffset RequiredDateTimeOffset(JsonElement root, string property)
    {
        if (!root.TryGetProperty(property, out var element) ||
            element.ValueKind != JsonValueKind.String ||
            !DateTimeOffset.TryParse(
                element.GetString()!,
                CultureInfo.InvariantCulture,
                DateTimeStyles.RoundtripKind,
                out var value))
        {
            throw Invalid($"The live approval field '{property}' must be an ISO-8601 timestamp.");
        }

        return value;
    }

    private static bool IsPlaceholder(string value)
    {
        if (value.Contains('<') || value.Contains('>'))
        {
            return true;
        }

        var normalized = new string(
            value.Where(character => char.IsLetterOrDigit(character))
                .Select(char.ToLowerInvariant)
                .ToArray());

        return normalized is
            "ownerdefinedid" or
            "replaceme" or
            "changeme" or
            "todo" or
            "tbd" or
            "example";
    }

    private static ApprovalStaleException Invalid(string message)
        => new("APPROVAL_INVALID", message);
}
