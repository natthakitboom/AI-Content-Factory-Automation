using System.Text.Json;
using ContentFactory.Contracts;
using ContentFactory.Infrastructure.Persistence.Entities;

namespace ContentFactory.Worker.Handlers;

/// <summary>
/// poll_video and download_video inherit the submit_video payload through the stage outbox.
/// They only need the client operation id, which maps to exactly one provider request row.
/// </summary>
internal static class ProviderJobPayload
{
    public static string? ReadOperationId(string payloadJson)
    {
        try
        {
            using var document = JsonDocument.Parse(payloadJson);
            if (document.RootElement.ValueKind == JsonValueKind.Object
                && document.RootElement.TryGetProperty("request", out var request)
                && request.ValueKind == JsonValueKind.Object
                && request.TryGetProperty("operationId", out var operationId)
                && operationId.ValueKind == JsonValueKind.String
                && !string.IsNullOrWhiteSpace(operationId.GetString()))
            {
                return operationId.GetString();
            }
        }
        catch (JsonException)
        {
            // Treated as an invalid payload by the caller.
        }

        return null;
    }

    public static bool NeedsReconciliation(ProviderRequest row)
        => row.Status is "prepared" or "submitting" or "submission_unknown"
            || string.IsNullOrWhiteSpace(row.ProviderRequestId)
            || string.IsNullOrWhiteSpace(row.StatusUrl)
            || string.IsNullOrWhiteSpace(row.ResponseUrl);

    public static ProviderSubmission ToSubmission(ProviderRequest row)
        => new(
            row.Provider,
            row.Model,
            row.ProviderRequestId!,
            new Uri(row.StatusUrl!),
            new Uri(row.ResponseUrl!),
            row.CancelUrl is null ? null : new Uri(row.CancelUrl));
}
