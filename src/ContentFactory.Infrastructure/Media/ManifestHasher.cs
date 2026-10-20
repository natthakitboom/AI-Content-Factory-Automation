using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using ContentFactory.Contracts;
using ContentFactory.Core.Exceptions;

namespace ContentFactory.Infrastructure.Media;

/// <summary>
/// Produces a reproducible identity for a render input. Object keys are sorted ordinally,
/// array order is preserved, and the hash is SHA-256 over UTF-8 canonical JSON. No current
/// time or machine-specific value is added.
/// </summary>
public static class ManifestHasher
{
    public static string Compute(RenderManifest manifest)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        return Hash(Canonicalize(manifest));
    }

    public static string Canonicalize(RenderManifest manifest)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        var element = JsonSerializer.SerializeToElement(manifest, JsonDefaults.Options);
        return CanonicalizeElement(element);
    }

    public static string CanonicalizeJson(string json)
    {
        ArgumentNullException.ThrowIfNull(json);

        try
        {
            using var document = JsonDocument.Parse(json);
            return CanonicalizeElement(document.RootElement);
        }
        catch (JsonException)
        {
            throw new ValidationException(
                "MANIFEST_JSON_INVALID",
                "Manifest JSON is invalid.");
        }
    }

    public static string HashCanonicalJson(string json)
        => Hash(CanonicalizeJson(json));

    private static string CanonicalizeElement(JsonElement element)
    {
        using var buffer = new MemoryStream();
        using (var writer = new Utf8JsonWriter(buffer, new JsonWriterOptions
        {
            Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
            Indented = false
        }))
        {
            WriteCanonical(writer, element);
        }

        return Encoding.UTF8.GetString(buffer.ToArray());
    }

    private static void WriteCanonical(Utf8JsonWriter writer, JsonElement element)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                writer.WriteStartObject();
                foreach (var property in element
                    .EnumerateObject()
                    .OrderBy(item => item.Name, StringComparer.Ordinal))
                {
                    writer.WritePropertyName(property.Name);
                    WriteCanonical(writer, property.Value);
                }

                writer.WriteEndObject();
                break;

            case JsonValueKind.Array:
                writer.WriteStartArray();
                foreach (var item in element.EnumerateArray())
                {
                    WriteCanonical(writer, item);
                }

                writer.WriteEndArray();
                break;

            case JsonValueKind.String:
                writer.WriteStringValue(element.GetString());
                break;

            case JsonValueKind.Number:
                writer.WriteRawValue(element.GetRawText(), skipInputValidation: true);
                break;

            case JsonValueKind.True:
                writer.WriteBooleanValue(true);
                break;

            case JsonValueKind.False:
                writer.WriteBooleanValue(false);
                break;

            case JsonValueKind.Null:
                writer.WriteNullValue();
                break;

            default:
                throw new ValidationException(
                    "MANIFEST_JSON_INVALID",
                    "Manifest JSON contains an unsupported value.");
        }
    }

    private static string Hash(string canonical)
        => Convert.ToHexString(
                SHA256.HashData(Encoding.UTF8.GetBytes(canonical)))
            .ToLowerInvariant();
}
