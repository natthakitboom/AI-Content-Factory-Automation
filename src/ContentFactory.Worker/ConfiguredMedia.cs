using ContentFactory.Contracts;
using ContentFactory.Core.Abstractions;
using ContentFactory.Core.Exceptions;
using ContentFactory.Infrastructure.Media;
using ContentFactory.Infrastructure.Storage;

namespace ContentFactory.Worker;

/// <summary>
/// Builds the caption-dependent media services on first use. The font identity comes from
/// <c>CAPTION_FONT_FILE</c>, hashed from the licensed file the host will really render.
/// Without it the render stages fail with a stable, blocked code instead of the whole
/// worker failing to start, and no fingerprint is ever invented.
/// </summary>
internal sealed class ConfiguredMedia
{
    private readonly IAssetStore _assets;
    private readonly StorageOptions _storage;
    private readonly IProcessRunner _runner;
    private readonly Func<string?> _fontFile;

    public ConfiguredMedia(
        IAssetStore assets,
        StorageOptions storage,
        IProcessRunner runner,
        Func<string?> fontFile)
    {
        _assets = assets;
        _storage = storage;
        _runner = runner;
        _fontFile = fontFile;
    }

    public ITimelineCompiler Compiler()
        => new TimelineCompiler(new TimelineOptions { FontFingerprint = Fingerprint() });

    public IVideoRenderer Renderer()
        => new FfmpegVideoRenderer(
            _assets,
            _storage,
            _runner,
            new EncoderProbe(
                _runner,
                _storage,
                new EncoderProbeOptions { FontFingerprint = Fingerprint() }),
            new CaptionLayout(new ConservativeTextMeasurer(), new CaptionLayoutOptions()),
            new FfmpegRenderOptions());

    private string Fingerprint()
    {
        var path = _fontFile();
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
        {
            throw new ValidationException(
                "CAPTION_FONT_NOT_CONFIGURED",
                "Set CAPTION_FONT_FILE to the licensed caption font file before rendering.");
        }

        return FontFingerprintIdentity.ComputeFromFile(path);
    }
}

internal sealed class ConfiguredTimelineCompiler(ConfiguredMedia media) : ITimelineCompiler
{
    public RenderManifest Compile(
        Guid videoVersionId,
        ScriptDocument script,
        IReadOnlyDictionary<string, AssetRef> visuals,
        IReadOnlyList<VoiceSegment> voices,
        string templateVersion)
        => media.Compiler().Compile(videoVersionId, script, visuals, voices, templateVersion);
}

internal sealed class ConfiguredVideoRenderer(ConfiguredMedia media) : IVideoRenderer
{
    public Task<RenderResult> RenderAsync(RenderManifest manifest, CancellationToken ct)
        => media.Renderer().RenderAsync(manifest, ct);
}

/// <summary>Stands in when no narration provider is configured; it never fabricates audio.</summary>
internal sealed class UnconfiguredVoiceProvider(string name) : IVoiceProvider
{
    public Task<AssetRef> SynthesizeAsync(VoiceRequest request, CancellationToken ct)
        => throw new ValidationException(
            "VOICE_PROVIDER_NOT_CONFIGURED",
            $"The '{name}' narration provider is not wired for automatic generation.");
}
