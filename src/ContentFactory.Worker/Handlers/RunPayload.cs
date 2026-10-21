using System.Text.Json;
using System.Text.Json.Nodes;
using ContentFactory.Contracts;

namespace ContentFactory.Worker.Handlers;

/// <summary>
/// The run payload created by the enqueuer travels through every stage. A stage that
/// produces data for the next one returns a copy with extra root properties; the original
/// <c>scriptRequest</c> is never rewritten, so the mode and revision stay stable.
/// </summary>
internal sealed class RunPayload
{
    private RunPayload(JsonObject root, ScriptRequest request)
    {
        Root = root;
        Request = request;
    }

    public JsonObject Root { get; }

    public ScriptRequest Request { get; }

    public Guid RevisionId => Request.RevisionId;

    public int CloudVideoSceneLimit
        => Root["cloudVideoSceneLimit"] is JsonValue value && value.TryGetValue<int>(out var limit)
            ? limit
            : 0;

    public bool AutoGenerateVoice
        => Root["autoGenerateVoice"] is JsonValue value
            && value.TryGetValue<bool>(out var flag)
            && flag;

    public Guid? VideoVersionId
        => Root["videoVersionId"] is JsonValue value
            && value.TryGetValue<Guid>(out var id)
            && id != Guid.Empty
                ? id
                : null;

    public VideoGenerationRequest? VideoRequest
        => Root["request"] is JsonObject node
            ? node.Deserialize<VideoGenerationRequest>(JsonDefaults.Options)
            : null;

    public static RunPayload? TryRead(string payloadJson)
    {
        try
        {
            if (JsonNode.Parse(payloadJson) is not JsonObject root
                || root["scriptRequest"] is not JsonObject requestNode)
            {
                return null;
            }

            var request = requestNode.Deserialize<ScriptRequest>(JsonDefaults.Options);
            return request is null || request.RevisionId == Guid.Empty
                ? null
                : new RunPayload(root, request);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>Returns the payload JSON with additional root properties set.</summary>
    public string With(params (string Name, object? Value)[] properties)
    {
        var copy = (JsonObject)JsonNode.Parse(Root.ToJsonString())!;
        foreach (var (name, value) in properties)
        {
            copy[name] = JsonSerializer.SerializeToNode(value, JsonDefaults.Options);
        }

        return copy.ToJsonString(JsonDefaults.Options);
    }
}
