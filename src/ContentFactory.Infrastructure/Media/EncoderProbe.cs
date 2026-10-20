using System.Globalization;
using System.Text;
using ContentFactory.Contracts;
using ContentFactory.Core.Exceptions;
using ContentFactory.Infrastructure.Storage;

namespace ContentFactory.Infrastructure.Media;

public sealed class EncoderProbeOptions
{
    public string FfmpegExecutable { get; init; } = "ffmpeg";

    public TimeSpan Timeout { get; init; } = TimeSpan.FromSeconds(30);

    public string PreferredHardwareEncoder { get; init; } = "h264_videotoolbox";

    public string FallbackEncoder { get; init; } = "libx264";

    public int FallbackThreads { get; init; } = 4;

    public IReadOnlyList<string> RequiredFilters { get; init; } =
        ["subtitles", "zoompan", "concat"];

    public string FontName { get; init; } = ProductBasicTemplate.FontName;

    public int FontSize { get; init; } = ProductBasicTemplate.CaptionFontSize;

    public string FontFingerprint { get; init; } = string.Empty;

    public void Validate()
    {
        if (string.IsNullOrWhiteSpace(FfmpegExecutable))
        {
            throw new ValidationException(
                "RENDER_PROBE_CONFIG_INVALID",
                "An FFmpeg executable name is required.");
        }

        if (Timeout <= TimeSpan.Zero || Timeout > TimeSpan.FromMinutes(10))
        {
            throw new ValidationException(
                "RENDER_PROBE_CONFIG_INVALID",
                "The encoder probe timeout must be between zero and ten minutes.");
        }

        if (string.IsNullOrWhiteSpace(PreferredHardwareEncoder) ||
            string.IsNullOrWhiteSpace(FallbackEncoder))
        {
            throw new ValidationException(
                "RENDER_PROBE_CONFIG_INVALID",
                "Hardware and fallback encoder names are required.");
        }

        if (FallbackThreads is < 1 or > 32)
        {
            throw new ValidationException(
                "RENDER_PROBE_CONFIG_INVALID",
                "The CPU fallback thread limit must be between 1 and 32.");
        }

        if (RequiredFilters is null || RequiredFilters.Count == 0)
        {
            throw new ValidationException(
                "RENDER_PROBE_CONFIG_INVALID",
                "At least one required media filter must be declared.");
        }

        if (string.IsNullOrWhiteSpace(FontName) ||
            FontSize <= 0 ||
            !FontFingerprintIdentity.IsSha256(FontFingerprint))
        {
            throw new ValidationException(
                "RENDER_PROBE_CONFIG_INVALID",
                "A caption font name, size and SHA-256 font fingerprint are required.");
        }
    }
}

public sealed record EncoderSelection(
    string VideoEncoder,
    int Threads,
    bool HardwareAccelerated,
    string Reason,
    string FontFingerprint);

/// <summary>
/// Selects an encoder only after a real probe encode. A listed hardware encoder is not
/// trusted by name: if its probe fails, the renderer falls back to a listed CPU libx264
/// with a bounded thread count. Missing libass/font support blocks rendering.
/// </summary>
public sealed class EncoderProbe
{
    private readonly IProcessRunner _runner;
    private readonly StorageOptions _storage;
    private readonly EncoderProbeOptions _options;

    public EncoderProbe(
        IProcessRunner runner,
        StorageOptions storage,
        EncoderProbeOptions? options = null)
    {
        _runner = runner ?? throw new ArgumentNullException(nameof(runner));
        _storage = storage ?? throw new ArgumentNullException(nameof(storage));
        _options = options ?? new EncoderProbeOptions();
        _options.Validate();
    }

    public async Task<EncoderSelection> ProbeAsync(CancellationToken ct)
    {
        var encoders = await RunRequiredAsync(
            ["-hide_banner", "-nostdin", "-encoders"],
            "RENDER_PROBE_FAILED",
            ct);

        var available = ParseEncoderNames(encoders.StandardOutput);
        var hardwareAvailable = available.Contains(_options.PreferredHardwareEncoder);
        var fallbackAvailable = available.Contains(_options.FallbackEncoder);

        if (hardwareAvailable)
        {
            var hardware = await _runner.RunAsync(
                _options.FfmpegExecutable,
                [
                    "-hide_banner",
                    "-nostdin",
                    "-y",
                    "-loglevel", "error",
                    "-f", "lavfi",
                    "-i", "color=c=black:s=64x64:d=0.2",
                    "-frames:v", "1",
                    "-c:v", _options.PreferredHardwareEncoder,
                    "-f", "null",
                    "-"
                ],
                _options.Timeout,
                ct);

            if (!hardware.TimedOut && hardware.ExitCode == 0)
            {
                await ProbeFiltersAndFontAsync(ct);
                return new EncoderSelection(
                    _options.PreferredHardwareEncoder,
                    0,
                    HardwareAccelerated: true,
                    "Hardware encoder probe succeeded.",
                    _options.FontFingerprint);
            }
        }

        if (!fallbackAvailable)
        {
            throw new ValidationException(
                "RENDER_ENCODER_UNAVAILABLE",
                "No probed H.264 encoder is available; install a build with libx264 or VideoToolbox support.");
        }

        await ProbeFiltersAndFontAsync(ct);
        return new EncoderSelection(
            _options.FallbackEncoder,
            _options.FallbackThreads,
            HardwareAccelerated: false,
            "CPU fallback: the hardware encoder was unavailable or its probe failed.",
            _options.FontFingerprint);
    }

    public static IReadOnlySet<string> ParseEncoderNames(string output)
    {
        ArgumentNullException.ThrowIfNull(output);

        var result = new HashSet<string>(StringComparer.Ordinal);
        foreach (var line in output.Split('\n'))
        {
            var parts = line.Split(
                ' ',
                StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            if (parts.Length >= 2 &&
                parts[0].StartsWith('V') &&
                IsIdentifier(parts[1]))
            {
                result.Add(parts[1]);
            }
        }

        return result;
    }

    public static bool HasFilter(string output, string name)
    {
        ArgumentNullException.ThrowIfNull(output);
        ArgumentNullException.ThrowIfNull(name);

        foreach (var line in output.Split('\n'))
        {
            var parts = line.Split(
                ' ',
                StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            if (parts.Length >= 2 &&
                parts[1] == name)
            {
                return true;
            }
        }

        return false;
    }

    private async Task ProbeFiltersAndFontAsync(CancellationToken ct)
    {
        var filters = await RunRequiredAsync(
            ["-hide_banner", "-nostdin", "-filters"],
            "RENDER_PROBE_FAILED",
            ct);

        if (!HasFilter(filters.StandardOutput, "subtitles"))
        {
            throw new ValidationException(
                "RENDER_SUBTITLES_UNAVAILABLE",
                "The FFmpeg build lacks the subtitles/libass filter; install an FFmpeg build with libass.");
        }

        foreach (var filter in _options.RequiredFilters)
        {
            if (filter != "subtitles" &&
                !HasFilter(filters.StandardOutput, filter))
            {
                throw new ValidationException(
                    "RENDER_FILTER_UNAVAILABLE",
                    $"The FFmpeg build lacks the required {filter} filter.");
            }
        }

        var probeDirectory = Path.Combine(_storage.TempRoot, "render-probe");
        Directory.CreateDirectory(probeDirectory);
        var assPath = Path.Combine(
            probeDirectory,
            Guid.NewGuid().ToString("N") + ".ass");

        try
        {
            await File.WriteAllTextAsync(
                assPath,
                BuildProbeAss(),
                new UTF8Encoding(encoderShouldEmitUTF8Identifier: false),
                ct);

            var font = await _runner.RunAsync(
                _options.FfmpegExecutable,
                [
                    "-hide_banner",
                    "-nostdin",
                    "-y",
                    "-loglevel", "error",
                    "-f", "lavfi",
                    "-i", "color=c=black:s=64x64:d=0.2",
                    "-vf", "subtitles=filename=" + FfmpegFilterSafety.EscapePath(assPath),
                    "-frames:v", "1",
                    "-f", "null",
                    "-"
                ],
                _options.Timeout,
                ct);

            if (font.TimedOut || font.ExitCode != 0)
            {
                throw new ValidationException(
                    "RENDER_FONT_UNAVAILABLE",
                    "The subtitle renderer could not render Thai text; install a Thai-capable font and a libass-enabled FFmpeg build.");
            }
        }
        finally
        {
            TryDelete(assPath);
        }
    }

    private async Task<ProcessResult> RunRequiredAsync(
        IReadOnlyList<string> args,
        string errorCode,
        CancellationToken ct)
    {
        var result = await _runner.RunAsync(
            _options.FfmpegExecutable,
            args,
            _options.Timeout,
            ct);

        if (result.TimedOut || result.ExitCode != 0)
        {
            throw new ValidationException(
                errorCode,
                "FFmpeg could not complete a required probe.");
        }

        return result;
    }

    private string BuildProbeAss()
    {
        var layout = new CaptionLayout(
            new ConservativeTextMeasurer(_options.FontSize),
            new CaptionLayoutOptions(
                MaxWidthPx: ProductBasicTemplate.CaptionMaxWidth,
                MaxLines: ProductBasicTemplate.CaptionMaxLines,
                FontSize: _options.FontSize,
                FontName: _options.FontName));
        var visual = new AssetRef(
            Guid.Empty,
            new string('0', 64),
            "assets/sha256/00/" + new string('0', 64),
            AssetKind.Image,
            "image/png",
            1,
            null);
        var manifest = new RenderManifest(
            1,
            Guid.Empty,
            new string('0', 64),
            ProductBasicTemplate.Version,
            ProductBasicTemplate.RendererVersion,
            _options.FontFingerprint,
            ProductBasicTemplate.Width,
            ProductBasicTemplate.Height,
            ProductBasicTemplate.Fps,
            [
                new TimelineSegment(
                    "probe",
                    0,
                    200,
                    visual,
                    null,
                    "ทดสอบ",
                    "still")
            ]);

        return layout.BuildAss(manifest);
    }

    private static bool IsIdentifier(string value)
        => value.Length > 0 &&
            value.All(character =>
                character is >= 'a' and <= 'z' or
                >= 'A' and <= 'Z' or
                >= '0' and <= '9' or
                '_' or '-');

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (IOException)
        {
            // The temp cleaner can retry.
        }
        catch (UnauthorizedAccessException)
        {
            // The temp cleaner can retry.
        }
    }
}

internal static class FfmpegFilterSafety
{
    public static string EscapePath(string path)
    {
        ArgumentNullException.ThrowIfNull(path);

        var builder = new StringBuilder(path.Length + 8);
        foreach (var character in path)
        {
            switch (character)
            {
                case '\\':
                case ':':
                case '\'':
                case ',':
                case '[':
                case ']':
                case ';':
                case ' ':
                    builder.Append('\\');
                    builder.Append(character);
                    break;
                default:
                    builder.Append(character);
                    break;
            }
        }

        return builder.ToString();
    }

    public static string Invariant(double value)
        => value.ToString("0.###", CultureInfo.InvariantCulture);
}
