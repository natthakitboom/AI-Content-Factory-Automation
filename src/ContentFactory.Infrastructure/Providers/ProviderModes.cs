using ContentFactory.Core.Exceptions;

namespace ContentFactory.Infrastructure.Providers;

/// <summary>
/// The provider selection for this process, resolved only from explicit TEXT_PROVIDER,
/// VIDEO_PROVIDER and VOICE_PROVIDER names. The mere presence of a credential such as
/// FAL_KEY never switches anything to live, and unknown names fail instead of falling
/// back to something paid.
/// </summary>
public sealed record ProviderModes(string Text, string Video, string Voice)
{
    public const string MockBanner = "MOCK MODE";
    public const string LiveBanner = "LIVE MODE";

    private static readonly string[] TextNames = ["mock", "lm_studio"];
    private static readonly string[] VideoNames = ["mock", "fal"];
    private static readonly string[] VoiceNames = ["uploaded", "mock", "azure"];

    public bool IsMock => Text == "mock" || Video == "mock" || Voice == "mock";

    public string Banner => IsMock ? MockBanner : LiveBanner;

    public static ProviderModes Resolve(Func<string, string?> environment)
    {
        ArgumentNullException.ThrowIfNull(environment);
        return new ProviderModes(
            Pick(environment, "TEXT_PROVIDER", "mock", TextNames),
            Pick(environment, "VIDEO_PROVIDER", "mock", VideoNames),
            Pick(environment, "VOICE_PROVIDER", "uploaded", VoiceNames));
    }

    public static ProviderModes FromEnvironment()
        => Resolve(Environment.GetEnvironmentVariable);

    private static string Pick(
        Func<string, string?> environment,
        string name,
        string fallback,
        string[] allowed)
    {
        var value = environment(name)?.Trim().ToLowerInvariant();
        if (string.IsNullOrEmpty(value))
        {
            return fallback;
        }

        if (!allowed.Contains(value, StringComparer.Ordinal))
        {
            throw new ValidationException(
                "PROVIDER_MODE_INVALID",
                $"{name} must be one of: {string.Join(", ", allowed)}.");
        }

        return value;
    }
}
