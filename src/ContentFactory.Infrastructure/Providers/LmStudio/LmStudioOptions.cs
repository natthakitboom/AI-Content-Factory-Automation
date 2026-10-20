namespace ContentFactory.Infrastructure.Providers.LmStudio;

/// <summary>
/// Configuration for the local LM Studio adapter. The model ID is always supplied by
/// configuration; it is never guessed from a model name or downloaded automatically.
/// </summary>
public sealed class LmStudioOptions
{
    public const string DefaultPromptVersion = "script-planner.v1";

    public Uri BaseUrl { get; init; } = new("http://127.0.0.1:1234/v1");

    public string? ApiToken { get; init; }

    public string ModelId { get; init; } = string.Empty;

    public int MaxOutputTokens { get; init; } = 2048;

    public TimeSpan Timeout { get; init; } = TimeSpan.FromSeconds(60);

    public double Temperature { get; init; } = 0.4;

    public string PromptVersion { get; init; } = DefaultPromptVersion;

    public int MaxResponseBytes { get; init; } = 1024 * 1024;

    public static LmStudioOptions FromEnvironment()
    {
        var baseUrl = Environment.GetEnvironmentVariable("LM_STUDIO_BASE_URL");
        var modelId = Environment.GetEnvironmentVariable("LM_STUDIO_MODEL_ID");
        var apiToken = Environment.GetEnvironmentVariable("LM_STUDIO_API_TOKEN");
        var maxTokens = Environment.GetEnvironmentVariable("TEXT_MAX_OUTPUT_TOKENS");

        return new LmStudioOptions
        {
            BaseUrl = Uri.TryCreate(baseUrl, UriKind.Absolute, out var parsed)
                ? parsed
                : new Uri("http://127.0.0.1:1234/v1"),
            ModelId = modelId ?? string.Empty,
            ApiToken = string.IsNullOrWhiteSpace(apiToken) ? null : apiToken,
            MaxOutputTokens = int.TryParse(maxTokens, out var parsedTokens) && parsedTokens > 0
                ? parsedTokens
                : 2048
        };
    }
}
