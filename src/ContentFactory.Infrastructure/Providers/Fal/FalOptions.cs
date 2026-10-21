using ContentFactory.Core.Exceptions;

namespace ContentFactory.Infrastructure.Providers.Fal;

/// <summary>
/// Configuration for the fal.ai queue adapter. The model ID is always supplied by
/// configuration; it is never guessed from a model name or inferred from a file path.
/// </summary>
public sealed class FalOptions
{
    public const string DefaultEndpointId =
        "fal-ai/wan/v2.2-a14b/image-to-video/turbo";

    public string ApiKey { get; init; } = string.Empty;

    public string ModelId { get; init; } = DefaultEndpointId;

    public Uri BaseUrl { get; init; } = new("https://queue.fal.run/");

    public TimeSpan SubmitTimeout { get; init; } = TimeSpan.FromSeconds(60);

    public int ImageMaxLongEdge { get; init; } = 1280;

    public long ImageMaxBytesBeforeBase64 { get; init; } = 3L * 1024 * 1024;

    public long RequestBodyCapBytes { get; init; } = 5L * 1024 * 1024;

    public int JpegQuality { get; init; } = 90;

    /// <summary>Extra attempts for an idempotent GET after a 429, 5xx or network error.</summary>
    public int GetRetries { get; init; } = 2;

    public TimeSpan GetRetryBaseDelay { get; init; } = TimeSpan.FromSeconds(1);

    public TimeSpan MaxRetryAfter { get; init; } = TimeSpan.FromSeconds(60);

    /// <summary>Injected so tests never sleep; production waits for real.</summary>
    public Func<TimeSpan, CancellationToken, Task> Delay { get; init; } =
        static (delay, ct) => Task.Delay(delay, ct);

    /// <summary>Returns a value in [0, 1) used to jitter retry delays.</summary>
    public Func<double> Jitter { get; init; } = static () => Random.Shared.NextDouble();

    public static FalOptions FromEnvironment()
    {
        var apiKey = Environment.GetEnvironmentVariable("FAL_KEY") ?? string.Empty;
        var modelId = Environment.GetEnvironmentVariable("FAL_MODEL_ID");

        return new FalOptions
        {
            ApiKey = apiKey,
            ModelId = string.IsNullOrWhiteSpace(modelId)
                ? DefaultEndpointId
                : modelId.Trim()
        };
    }

    public void Validate()
    {
        if (string.IsNullOrWhiteSpace(ModelId))
        {
            throw new ValidationException(
                "FAL_MODEL_ID_INVALID",
                "The fal model id is required.");
        }

        if (BaseUrl is null || !BaseUrl.IsAbsoluteUri)
        {
            throw new ValidationException(
                "FAL_BASE_URL_INVALID",
                "The fal base URL must be an absolute URI.");
        }

        if (ImageMaxLongEdge <= 0 ||
            ImageMaxBytesBeforeBase64 <= 0 ||
            RequestBodyCapBytes <= 0)
        {
            throw new ValidationException(
                "FAL_LIMITS_INVALID",
                "The fal adapter size limits must be positive.");
        }

        if (RequestBodyCapBytes <= ImageMaxBytesBeforeBase64)
        {
            throw new ValidationException(
                "FAL_LIMITS_INVALID",
                "The request body cap must be larger than the image cap.");
        }
    }
}
