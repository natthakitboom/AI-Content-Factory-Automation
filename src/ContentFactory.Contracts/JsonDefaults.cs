using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Unicode;

namespace ContentFactory.Contracts;

/// <summary>
/// Shared JSON settings: camelCase properties, string enum values and readable Thai text
/// (non-ASCII is preserved instead of being escaped to \uXXXX).
/// </summary>
public static class JsonDefaults
{
    public static JsonSerializerOptions Options { get; } = Create();

    public static JsonSerializerOptions Create()
    {
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web)
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            DictionaryKeyPolicy = JsonNamingPolicy.CamelCase,
            WriteIndented = false,
            Encoder = JavaScriptEncoder.Create(UnicodeRanges.All)
        };
        options.Converters.Add(new JsonStringEnumConverter(JsonNamingPolicy.CamelCase));
        return options;
    }
}
