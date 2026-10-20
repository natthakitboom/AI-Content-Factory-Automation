using System.Globalization;
using System.Text.Json;
using ContentFactory.Contracts;
using ContentFactory.Core.Abstractions;
using ContentFactory.Core.Exceptions;
using ContentFactory.Infrastructure.Storage;

namespace ContentFactory.Infrastructure.Media;

public sealed class FfprobeOptions
{
    public string FfprobeExecutable { get; init; } = "ffprobe";

    public string FfmpegExecutable { get; init; } = "ffmpeg";

    public bool DetectSilence { get; init; } = true;

    public double SilenceMeanVolumeDb { get; init; } = -50d;

    public TimeSpan Timeout { get; init; } = TimeSpan.FromSeconds(30);

    public void Validate()
    {
        if (string.IsNullOrWhiteSpace(FfprobeExecutable) ||
            string.IsNullOrWhiteSpace(FfmpegExecutable))
        {
            throw new ValidationException(
                "VOICE_PROBE_CONFIG_INVALID",
                "FFprobe and FFmpeg executable names are required.");
        }

        if (Timeout <= TimeSpan.Zero || Timeout > TimeSpan.FromMinutes(10))
        {
            throw new ValidationException(
                "VOICE_PROBE_CONFIG_INVALID",
                "The audio probe timeout must be between zero and ten minutes.");
        }

        if (SilenceMeanVolumeDb is < -120d or > 0d)
        {
            throw new ValidationException(
                "VOICE_PROBE_CONFIG_INVALID",
                "The silence threshold must be between -120 and 0 dB.");
        }
    }
}

/// <summary>
/// Measures every narration segment with FFprobe and, when enabled, runs FFmpeg's
/// volumedetect to reject silent narration. A missing stream, unreadable file, zero
/// duration or silent file fails closed. No duration is invented from text length.
/// </summary>
public sealed class FfprobeAudioProbe : IAudioProbe
{
    private readonly IAssetStore _assets;
    private readonly StorageOptions _storage;
    private readonly SafeProcessRunner _runner;
    private readonly FfprobeOptions _options;

    public FfprobeAudioProbe(
        IAssetStore assets,
        StorageOptions storage,
        SafeProcessRunner? runner = null,
        FfprobeOptions? options = null)
    {
        _assets = assets ?? throw new ArgumentNullException(nameof(assets));
        _storage = storage ?? throw new ArgumentNullException(nameof(storage));
        _runner = runner ?? new SafeProcessRunner();
        _options = options ?? new FfprobeOptions();
        _options.Validate();
    }

    public async Task<AudioProbeResult> ProbeAsync(
        AssetRef audio,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(audio);

        if (audio.Kind != AssetKind.Audio)
        {
            throw new ValidationException(
                "VOICE_ASSET_INVALID",
                "Only audio assets can be measured for narration.");
        }

        var tempDirectory = Path.Combine(_storage.TempRoot, "voice-probe");
        Directory.CreateDirectory(tempDirectory);
        var tempPath = Path.Combine(
            tempDirectory,
            Guid.NewGuid().ToString("N") + ".audio");

        try
        {
            await using (var input = await _assets.OpenReadAsync(audio.Id, ct))
            await using (var output = new FileStream(
                tempPath,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None,
                81920,
                FileOptions.Asynchronous | FileOptions.SequentialScan))
            {
                await input.CopyToAsync(output, ct);
            }

            var probe = await _runner.RunAsync(
                _options.FfprobeExecutable,
                [
                    "-v", "error",
                    "-select_streams", "a:0",
                    "-show_entries",
                    "stream=codec_type,codec_name,duration:format=duration",
                    "-of", "json",
                    tempPath
                ],
                _options.Timeout,
                ct);

            if (probe.TimedOut)
            {
                throw new ValidationException(
                    "VOICE_PROBE_TIMEOUT",
                    "FFprobe did not finish within the configured timeout.");
            }

            if (probe.ExitCode != 0)
            {
                throw new ValidationException(
                    "VOICE_NARRATION_CORRUPT",
                    "FFprobe could not read the narration asset.");
            }

            var parsed = ParseProbeJson(probe.StandardOutput);
            if (!_options.DetectSilence)
            {
                return parsed;
            }

            var silence = await _runner.RunAsync(
                _options.FfmpegExecutable,
                [
                    "-hide_banner",
                    "-nostdin",
                    "-i", tempPath,
                    "-af", "volumedetect",
                    "-f", "null",
                    "-"
                ],
                _options.Timeout,
                ct);

            if (silence.TimedOut)
            {
                throw new ValidationException(
                    "VOICE_PROBE_TIMEOUT",
                    "FFmpeg volume detection did not finish within the configured timeout.");
            }

            if (silence.ExitCode != 0)
            {
                throw new ValidationException(
                    "VOICE_NARRATION_CORRUPT",
                    "FFmpeg could not inspect the narration volume.");
            }

            var mean = ParseMeanVolumeDb(silence.StandardError);
            if (mean is null)
            {
                throw new ValidationException(
                    "VOICE_NARRATION_CORRUPT",
                    "FFmpeg did not report a usable volume measurement.");
            }

            return parsed with
            {
                IsSilent = mean.Value <= _options.SilenceMeanVolumeDb
            };
        }
        finally
        {
            TryDelete(tempPath);
        }
    }

    public static AudioProbeResult ParseProbeJson(string json)
    {
        ArgumentNullException.ThrowIfNull(json);

        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(json);
        }
        catch (JsonException)
        {
            throw new ValidationException(
                "VOICE_PROBE_JSON_INVALID",
                "FFprobe returned invalid JSON.");
        }

        using (document)
        {
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                throw new ValidationException(
                    "VOICE_PROBE_JSON_INVALID",
                    "FFprobe JSON must be an object.");
            }

            if (!root.TryGetProperty("streams", out var streams) ||
                streams.ValueKind != JsonValueKind.Array)
            {
                return new AudioProbeResult(0, false, false, null);
            }

            string? codec = null;
            long durationMs = 0;
            foreach (var stream in streams.EnumerateArray())
            {
                if (stream.ValueKind != JsonValueKind.Object ||
                    !stream.TryGetProperty("codec_type", out var codecType) ||
                    codecType.ValueKind != JsonValueKind.String ||
                    !string.Equals(codecType.GetString(), "audio", StringComparison.Ordinal))
                {
                    continue;
                }

                if (stream.TryGetProperty("codec_name", out var codecName) &&
                    codecName.ValueKind == JsonValueKind.String)
                {
                    codec = codecName.GetString();
                }

                if (stream.TryGetProperty("duration", out var streamDuration))
                {
                    durationMs = ToDurationMs(streamDuration);
                }

                break;
            }

            if (durationMs <= 0 &&
                root.TryGetProperty("format", out var format) &&
                format.ValueKind == JsonValueKind.Object &&
                format.TryGetProperty("duration", out var formatDuration))
            {
                durationMs = ToDurationMs(formatDuration);
            }

            var hasAudio = codec is not null;
            return new AudioProbeResult(
                hasAudio ? durationMs : 0,
                hasAudio,
                IsSilent: false,
                codec);
        }
    }

    public static double? ParseMeanVolumeDb(string output)
    {
        ArgumentNullException.ThrowIfNull(output);

        const string marker = "mean_volume:";
        var index = output.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
        if (index < 0)
        {
            return null;
        }

        var start = index + marker.Length;
        var end = output.IndexOf("dB", start, StringComparison.OrdinalIgnoreCase);
        if (end <= start)
        {
            return null;
        }

        var value = output[start..end].Trim();
        return double.TryParse(
            value,
            NumberStyles.Float,
            CultureInfo.InvariantCulture,
            out var parsed)
            ? parsed
            : null;
    }

    private static long ToDurationMs(JsonElement element)
    {
        var text = element.ValueKind switch
        {
            JsonValueKind.String => element.GetString(),
            JsonValueKind.Number => element.GetRawText(),
            _ => null
        };

        if (text is null ||
            !double.TryParse(
                text,
                NumberStyles.Float,
                CultureInfo.InvariantCulture,
                out var seconds) ||
            seconds <= 0d ||
            double.IsNaN(seconds) ||
            double.IsInfinity(seconds))
        {
            return 0;
        }

        return checked((long)Math.Round(seconds * 1000d, MidpointRounding.AwayFromZero));
    }

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
