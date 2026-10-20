using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using ContentFactory.Contracts;
using ContentFactory.Core.Abstractions;
using ContentFactory.Core.Exceptions;
using ContentFactory.Infrastructure.Storage;

namespace ContentFactory.Infrastructure.Media;

public sealed class TechnicalQaOptions
{
    public string FfprobeExecutable { get; init; } = "ffprobe";

    public string FfmpegExecutable { get; init; } = "ffmpeg";

    public TimeSpan ProbeTimeout { get; init; } = TimeSpan.FromSeconds(60);

    public TimeSpan FrameCheckTimeout { get; init; } = TimeSpan.FromMinutes(5);

    public int ExpectedWidth { get; init; } = ProductBasicTemplate.Width;

    public int ExpectedHeight { get; init; } = ProductBasicTemplate.Height;

    public double ExpectedFps { get; init; } = ProductBasicTemplate.Fps;

    public long MinDurationMs { get; init; } = ProductBasicTemplate.MinDurationMs;

    public long MaxDurationMs { get; init; } = ProductBasicTemplate.MaxDurationMs;

    public long DurationToleranceMs { get; init; } = 200;

    public string VideoCodec { get; init; } = "h264";

    public string PixelFormat { get; init; } = "yuv420p";

    public string AudioCodec { get; init; } = "aac";

    public int AudioSampleRate { get; init; } = ProductBasicTemplate.AudioSampleRate;

    public int AudioChannels { get; init; } = ProductBasicTemplate.AudioChannels;

    public bool RequireAudio { get; init; } = true;

    public double SilenceMeanVolumeDb { get; init; } = -50d;

    public double BlackPixelThreshold { get; init; } = 0.10d;

    public double BlackMinDurationSeconds { get; init; } = 0.5d;

    public int MaxSubtitleBytes { get; init; } = 1 * 1024 * 1024;

    internal void Validate()
    {
        if (string.IsNullOrWhiteSpace(FfprobeExecutable) ||
            string.IsNullOrWhiteSpace(FfmpegExecutable))
        {
            throw new ValidationException(
                "QA_CONFIG_INVALID",
                "FFprobe and FFmpeg executable names are required.");
        }

        if (ProbeTimeout <= TimeSpan.Zero || ProbeTimeout > TimeSpan.FromMinutes(10) ||
            FrameCheckTimeout <= TimeSpan.Zero || FrameCheckTimeout > TimeSpan.FromMinutes(30))
        {
            throw new ValidationException(
                "QA_CONFIG_INVALID",
                "Technical QA timeouts must be positive and bounded.");
        }

        if (ExpectedWidth <= 0 || ExpectedHeight <= 0 || ExpectedFps <= 0)
        {
            throw new ValidationException(
                "QA_CONFIG_INVALID",
                "Expected dimensions and frame rate must be positive.");
        }

        if (MinDurationMs <= 0 || MaxDurationMs < MinDurationMs ||
            DurationToleranceMs < 0)
        {
            throw new ValidationException(
                "QA_CONFIG_INVALID",
                "Technical QA duration bounds are invalid.");
        }

        if (string.IsNullOrWhiteSpace(VideoCodec) ||
            string.IsNullOrWhiteSpace(PixelFormat) ||
            string.IsNullOrWhiteSpace(AudioCodec) ||
            AudioSampleRate <= 0 ||
            AudioChannels <= 0)
        {
            throw new ValidationException(
                "QA_CONFIG_INVALID",
                "Technical QA codec and audio expectations are invalid.");
        }

        if (SilenceMeanVolumeDb is < -120d or > 0d ||
            BlackPixelThreshold is < 0d or > 1d ||
            BlackMinDurationSeconds <= 0d ||
            MaxSubtitleBytes <= 0)
        {
            throw new ValidationException(
                "QA_CONFIG_INVALID",
                "Technical QA threshold or size limits are invalid.");
        }
    }
}

/// <summary>
/// Checks the actual rendered media with FFprobe and FFmpeg. Corrupt files, missing
/// narration, wrong dimensions/codecs, out-of-bounds durations, failed decoding and
/// subtitle overflow are errors. Black-frame detection is a warning because an intentional
/// title/end card is allowed; it never turns an otherwise valid clip into a fake pass or a
/// whole-clip failure by itself.
/// </summary>
public sealed class TechnicalQa : ITechnicalQa
{
    private static readonly Regex Sha256Regex = new(
        "^[0-9a-f]{64}$",
        RegexOptions.CultureInvariant,
        TimeSpan.FromMilliseconds(100));

    private static readonly Regex BlackStartRegex = new(
        "black_start:(?<start>[0-9]+(?:\\.[0-9]+)?)",
        RegexOptions.CultureInvariant,
        TimeSpan.FromMilliseconds(100));

    private static readonly Regex BlackEndRegex = new(
        "black_end:(?<end>[0-9]+(?:\\.[0-9]+)?)",
        RegexOptions.CultureInvariant,
        TimeSpan.FromMilliseconds(100));

    private readonly IAssetStore _assets;
    private readonly StorageOptions _storage;
    private readonly IProcessRunner _runner;
    private readonly TechnicalQaOptions _options;

    public TechnicalQa(
        IAssetStore assets,
        StorageOptions storage,
        IProcessRunner runner,
        TechnicalQaOptions? options = null)
    {
        _assets = assets ?? throw new ArgumentNullException(nameof(assets));
        _storage = storage ?? throw new ArgumentNullException(nameof(storage));
        _runner = runner ?? throw new ArgumentNullException(nameof(runner));
        _options = options ?? new TechnicalQaOptions();
        _options.Validate();
    }

    public async Task<ValidationReport> CheckAsync(
        RenderResult result,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(result);
        ct.ThrowIfCancellationRequested();

        var errors = new List<ValidationIssue>();
        var warnings = new List<ValidationIssue>();

        if (result.Video is null || result.Video.Kind != AssetKind.Video)
        {
            Add(errors, "QA_VIDEO_INVALID", "video", "A video asset is required.");
        }

        if (result.Thumbnail is null || result.Thumbnail.Kind != AssetKind.Thumbnail)
        {
            Add(errors, "QA_THUMBNAIL_INVALID", "thumbnail", "A thumbnail asset is required.");
        }

        if (result.Subtitles is null || result.Subtitles.Kind != AssetKind.Subtitle)
        {
            Add(errors, "QA_SUBTITLES_INVALID", "subtitles", "A subtitle asset is required.");
        }

        if (string.IsNullOrWhiteSpace(result.ManifestSha256) ||
            !Sha256Regex.IsMatch(result.ManifestSha256))
        {
            Add(errors, "QA_MANIFEST_HASH_INVALID", "manifest", "The manifest hash is invalid.");
        }

        if (result.Video is null || result.Video.Kind != AssetKind.Video)
        {
            return Report(errors, warnings);
        }

        var tempDirectory = Path.Combine(
            _storage.TempRoot,
            "qa",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDirectory);
        var videoPath = Path.Combine(tempDirectory, "video.bin");

        try
        {
            await CopyAssetAsync(result.Video, videoPath, ct);

            if (result.Thumbnail is { Kind: AssetKind.Thumbnail })
            {
                await CheckThumbnailAsync(result.Thumbnail, errors, ct);
            }

            if (result.Subtitles is { Kind: AssetKind.Subtitle })
            {
                await CheckSubtitlesAsync(result.Subtitles, result.DurationMs, errors, ct);
            }

            var probe = await RunToolAsync(
                _options.FfprobeExecutable,
                [
                    "-v", "error",
                    "-show_entries",
                    "stream=index,codec_type,codec_name,width,height,pix_fmt,r_frame_rate,avg_frame_rate,sample_rate,channels,duration:format=duration,format_name",
                    "-of", "json",
                    videoPath
                ],
                _options.ProbeTimeout,
                ct);

            if (probe.TimedOut)
            {
                throw new ValidationException(
                    "QA_TOOL_TIMEOUT",
                    "FFprobe did not finish within the technical QA timeout.");
            }

            if (probe.ExitCode != 0)
            {
                Add(errors, "QA_VIDEO_CORRUPT", "video", "FFprobe could not read the rendered video.");
                return Report(errors, warnings);
            }

            var metadata = ParseProbe(probe.StandardOutput, errors);
            if (metadata.Video is null)
            {
                Add(errors, "QA_VIDEO_CORRUPT", "video", "The rendered file has no video stream.");
                return Report(errors, warnings);
            }

            var durationMs = metadata.DurationMs;
            if (durationMs <= 0)
            {
                Add(errors, "QA_VIDEO_CORRUPT", "video", "The rendered video duration is unavailable.");
                return Report(errors, warnings);
            }

            CheckVideo(metadata.Video, errors);
            CheckAudio(metadata.Audio, errors);
            CheckDurations(metadata, result.DurationMs, errors);

            if (!await DecodeAsync(videoPath, metadata.Audio is not null, errors, ct))
            {
                return Report(errors, warnings);
            }

            if (metadata.Audio is not null)
            {
                await CheckSilenceAsync(videoPath, errors, ct);
            }

            await CheckBlackFramesAsync(videoPath, warnings, ct);
            return Report(errors, warnings);
        }
        finally
        {
            TryDeleteDirectory(tempDirectory);
        }
    }

    private void CheckVideo(StreamInfo video, ICollection<ValidationIssue> errors)
    {
        if (!string.Equals(video.Codec, _options.VideoCodec, StringComparison.OrdinalIgnoreCase))
        {
            Add(
                errors,
                "QA_VIDEO_CODEC_INVALID",
                "video.codec",
                $"Expected {_options.VideoCodec}, got {video.Codec ?? "unknown"}.");
        }

        if (video.Width != _options.ExpectedWidth || video.Height != _options.ExpectedHeight)
        {
            Add(
                errors,
                "QA_DIMENSIONS_INVALID",
                "video.dimensions",
                $"Expected {_options.ExpectedWidth}x{_options.ExpectedHeight}, got {video.Width}x{video.Height}.");
        }

        if (!string.Equals(video.PixelFormat, _options.PixelFormat, StringComparison.OrdinalIgnoreCase))
        {
            Add(
                errors,
                "QA_PIXEL_FORMAT_INVALID",
                "video.pix_fmt",
                $"Expected {_options.PixelFormat}, got {video.PixelFormat ?? "unknown"}.");
        }

        if (video.Fps is not { } fps ||
            Math.Abs(fps - _options.ExpectedFps) > 0.01d)
        {
            Add(
                errors,
                "QA_FPS_INVALID",
                "video.fps",
                $"Expected {_options.ExpectedFps} fps, got {video.Fps?.ToString(CultureInfo.InvariantCulture) ?? "unknown"}.");
        }
    }

    private void CheckAudio(StreamInfo? audio, ICollection<ValidationIssue> errors)
    {
        if (audio is null)
        {
            if (_options.RequireAudio)
            {
                Add(errors, "QA_AUDIO_MISSING", "audio", "The rendered video has no audio stream.");
            }

            return;
        }

        if (!string.Equals(audio.Codec, _options.AudioCodec, StringComparison.OrdinalIgnoreCase))
        {
            Add(
                errors,
                "QA_AUDIO_CODEC_INVALID",
                "audio.codec",
                $"Expected {_options.AudioCodec}, got {audio.Codec ?? "unknown"}.");
        }

        if (audio.SampleRate != _options.AudioSampleRate)
        {
            Add(
                errors,
                "QA_AUDIO_SAMPLE_RATE_INVALID",
                "audio.sample_rate",
                $"Expected {_options.AudioSampleRate} Hz, got {audio.SampleRate?.ToString(CultureInfo.InvariantCulture) ?? "unknown"}.");
        }

        if (audio.Channels != _options.AudioChannels)
        {
            Add(
                errors,
                "QA_AUDIO_CHANNELS_INVALID",
                "audio.channels",
                $"Expected {_options.AudioChannels} channels, got {audio.Channels?.ToString(CultureInfo.InvariantCulture) ?? "unknown"}.");
        }
    }

    private void CheckDurations(
        ProbeMetadata metadata,
        long resultDurationMs,
        ICollection<ValidationIssue> errors)
    {
        if (metadata.DurationMs < _options.MinDurationMs ||
            metadata.DurationMs > _options.MaxDurationMs)
        {
            Add(
                errors,
                "QA_DURATION_OUT_OF_BOUNDS",
                "video.duration",
                $"The rendered duration {metadata.DurationMs} ms is outside the allowed range.");
        }

        if (resultDurationMs <= 0 ||
            Math.Abs(metadata.DurationMs - resultDurationMs) > _options.DurationToleranceMs)
        {
            Add(
                errors,
                "QA_RESULT_DURATION_MISMATCH",
                "video.duration",
                "The render result duration does not match the probed video duration.");
        }

        if (metadata.Audio?.DurationMs is { } audioDuration &&
            metadata.Video?.DurationMs is { } videoDuration &&
            Math.Abs(audioDuration - videoDuration) > _options.DurationToleranceMs)
        {
            Add(
                errors,
                "QA_AV_DURATION_MISMATCH",
                "audio.duration",
                "Audio and video durations differ by more than 200 ms.");
        }
    }

    private async Task<bool> DecodeAsync(
        string videoPath,
        bool hasAudio,
        ICollection<ValidationIssue> errors,
        CancellationToken ct)
    {
        var args = new List<string>
        {
            "-hide_banner",
            "-nostdin",
            "-v", "error",
            "-i", videoPath,
            "-map", "0:v:0"
        };
        if (hasAudio)
        {
            args.Add("-map");
            args.Add("0:a:0");
        }

        args.Add("-f");
        args.Add("null");
        args.Add("-");
        var decode = await RunToolAsync(
            _options.FfmpegExecutable,
            args,
            _options.FrameCheckTimeout,
            ct);

        if (decode.TimedOut)
        {
            throw new ValidationException(
                "QA_TOOL_TIMEOUT",
                "FFmpeg did not finish the frame check within the technical QA timeout.");
        }

        if (decode.ExitCode != 0)
        {
            Add(errors, "QA_DECODE_FAILED", "video", "FFmpeg could not decode the rendered media.");
            return false;
        }

        return true;
    }

    private async Task CheckSilenceAsync(
        string videoPath,
        ICollection<ValidationIssue> errors,
        CancellationToken ct)
    {
        var silence = await RunToolAsync(
            _options.FfmpegExecutable,
            [
                "-hide_banner",
                "-nostdin",
                "-v", "info",
                "-i", videoPath,
                "-map", "0:a:0",
                "-af", "volumedetect",
                "-f", "null",
                "-"
            ],
            _options.FrameCheckTimeout,
            ct);

        if (silence.TimedOut)
        {
            throw new ValidationException(
                "QA_TOOL_TIMEOUT",
                "FFmpeg did not finish the audio check within the technical QA timeout.");
        }

        if (silence.ExitCode != 0)
        {
            Add(errors, "QA_AUDIO_UNMEASURABLE", "audio", "FFmpeg could not measure the audio level.");
            return;
        }

        var mean = ParseMeanVolumeDb(silence.StandardError);
        if (mean is null)
        {
            Add(errors, "QA_AUDIO_UNMEASURABLE", "audio", "FFmpeg did not report a usable audio level.");
            return;
        }

        if (mean.Value <= _options.SilenceMeanVolumeDb)
        {
            Add(
                errors,
                "QA_AUDIO_SILENT",
                "audio",
                "The rendered narration is silent.");
        }
    }

    private async Task CheckBlackFramesAsync(
        string videoPath,
        ICollection<ValidationIssue> warnings,
        CancellationToken ct)
    {
        var black = await RunToolAsync(
            _options.FfmpegExecutable,
            [
                "-hide_banner",
                "-nostdin",
                "-v", "info",
                "-i", videoPath,
                "-vf",
                $"blackdetect=d={_options.BlackMinDurationSeconds.ToString(CultureInfo.InvariantCulture)}:pix_th={_options.BlackPixelThreshold.ToString(CultureInfo.InvariantCulture)}",
                "-an",
                "-f", "null",
                "-"
            ],
            _options.FrameCheckTimeout,
            ct);

        if (black.TimedOut)
        {
            throw new ValidationException(
                "QA_TOOL_TIMEOUT",
                "FFmpeg did not finish black-frame detection within the technical QA timeout.");
        }

        if (black.ExitCode != 0)
        {
            throw new ValidationException(
                "QA_TOOL_UNAVAILABLE",
                "FFmpeg could not run the black-frame detector.");
        }

        var starts = BlackStartRegex.Matches(black.StandardError)
            .Select(match => match.Groups["start"].Value)
            .ToArray();
        var ends = BlackEndRegex.Matches(black.StandardError)
            .Select(match => match.Groups["end"].Value)
            .ToArray();
        if (starts.Length > 0)
        {
            var ranges = starts.Select((start, index) =>
                index < ends.Length ? $"{start}s-{ends[index]}s" : $"{start}s");
            warnings.Add(new ValidationIssue(
                "QA_BLACK_FRAMES",
                "video",
                $"Black frames detected at {string.Join(", ", ranges)}; title/end cards may be intentional."));
        }
    }

    private async Task CheckSubtitlesAsync(
        AssetRef subtitles,
        long durationMs,
        ICollection<ValidationIssue> errors,
        CancellationToken ct)
    {
        string text;
        try
        {
            text = await ReadSmallTextAsync(subtitles, _options.MaxSubtitleBytes, ct);
        }
        catch (ValidationException)
        {
            Add(errors, "QA_SUBTITLES_EMPTY", "subtitles", "The subtitle asset is missing or empty.");
            return;
        }

        var cues = ParseSrt(text);
        if (cues.Count == 0)
        {
            Add(errors, "QA_SUBTITLES_EMPTY", "subtitles", "The subtitle asset has no cues.");
            return;
        }

        foreach (var cue in cues)
        {
            if (cue.EndMs <= cue.StartMs)
            {
                Add(errors, "QA_SUBTITLE_TIMING_INVALID", "subtitles", "A subtitle cue has an invalid duration.");
                continue;
            }

            if (durationMs > 0 && cue.EndMs > durationMs + _options.DurationToleranceMs)
            {
                Add(errors, "QA_SUBTITLE_TIMING_INVALID", "subtitles", "A subtitle cue ends after the video.");
            }

            if (cue.Lines.Count > ProductBasicTemplate.CaptionMaxLines)
            {
                Add(
                    errors,
                    "QA_SUBTITLE_OVERFLOW",
                    "subtitles",
                    $"A subtitle cue has {cue.Lines.Count} lines; the template allows {ProductBasicTemplate.CaptionMaxLines}.");
            }

            if (cue.Lines.Count == 0 || cue.Lines.All(string.IsNullOrWhiteSpace))
            {
                Add(errors, "QA_SUBTITLES_EMPTY", "subtitles", "A subtitle cue has no readable text.");
            }
        }
    }

    private async Task CheckThumbnailAsync(
        AssetRef thumbnail,
        ICollection<ValidationIssue> errors,
        CancellationToken ct)
    {
        var header = new byte[3];
        await using var stream = await _assets.OpenReadAsync(thumbnail.Id, ct);
        var read = await stream.ReadAsync(header.AsMemory(0, header.Length), ct);
        if (read < 3 ||
            header[0] != 0xFF ||
            header[1] != 0xD8 ||
            header[2] != 0xFF)
        {
            Add(errors, "QA_THUMBNAIL_INVALID", "thumbnail", "The thumbnail is not a JPEG image.");
        }
    }

    private async Task CopyAssetAsync(AssetRef asset, string destination, CancellationToken ct)
    {
        if (asset.Bytes <= 0 || asset.Bytes > _storage.VideoBytesLimit)
        {
            throw new ValidationException(
                "QA_VIDEO_INVALID",
                "The rendered video asset is empty or exceeds the video size limit.");
        }

        await using var input = await _assets.OpenReadAsync(asset.Id, ct);
        await using var output = new FileStream(
            destination,
            FileMode.CreateNew,
            FileAccess.Write,
            FileShare.None,
            81920,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        await input.CopyToAsync(output, ct);
        await output.FlushAsync(ct);
        output.Flush(flushToDisk: true);

        if (new FileInfo(destination).Length == 0)
        {
            throw new ValidationException(
                "QA_VIDEO_INVALID",
                "The rendered video asset is empty.");
        }
    }

    private async Task<string> ReadSmallTextAsync(
        AssetRef asset,
        int maxBytes,
        CancellationToken ct)
    {
        await using var stream = await _assets.OpenReadAsync(asset.Id, ct);
        using var memory = new MemoryStream();
        var buffer = new byte[8192];
        while (true)
        {
            var read = await stream.ReadAsync(buffer, ct);
            if (read == 0)
            {
                break;
            }

            if (memory.Length + read > maxBytes)
            {
                throw new ValidationException(
                    "QA_SUBTITLES_INVALID",
                    "The subtitle asset exceeds the technical QA size limit.");
            }

            memory.Write(buffer, 0, read);
        }

        if (memory.Length == 0)
        {
            throw new ValidationException(
                "QA_SUBTITLES_EMPTY",
                "The subtitle asset is empty.");
        }

        return Encoding.UTF8.GetString(memory.ToArray());
    }

    private async Task<ProcessResult> RunToolAsync(
        string executable,
        IReadOnlyList<string> args,
        TimeSpan timeout,
        CancellationToken ct)
    {
        try
        {
            return await _runner.RunAsync(executable, args, timeout, ct);
        }
        catch (ValidationException exception) when (exception.Code == "PROCESS_START_FAILED")
        {
            throw new ValidationException(
                "QA_TOOL_UNAVAILABLE",
                "The configured media tool is not installed or cannot be started.");
        }
    }

    private static ProbeMetadata ParseProbe(
        string json,
        ICollection<ValidationIssue> errors)
    {
        try
        {
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object ||
                !root.TryGetProperty("streams", out var streams) ||
                streams.ValueKind != JsonValueKind.Array)
            {
                Add(errors, "QA_VIDEO_CORRUPT", "video", "FFprobe returned no stream metadata.");
                return ProbeMetadata.Empty;
            }

            StreamInfo? video = null;
            StreamInfo? audio = null;
            foreach (var stream in streams.EnumerateArray())
            {
                var type = GetString(stream, "codec_type");
                if (type == "video" && video is null)
                {
                    video = ToStreamInfo(stream);
                }
                else if (type == "audio" && audio is null)
                {
                    audio = ToStreamInfo(stream);
                }
            }

            var durationMs = ReadDurationMs(root.TryGetProperty("format", out var format)
                ? format
                : default);
            if (durationMs <= 0 && video?.DurationMs is { } videoDuration)
            {
                durationMs = videoDuration;
            }

            return new ProbeMetadata(
                video,
                audio,
                durationMs);
        }
        catch (JsonException)
        {
            Add(errors, "QA_VIDEO_CORRUPT", "video", "FFprobe returned invalid JSON.");
            return ProbeMetadata.Empty;
        }
    }

    private static StreamInfo ToStreamInfo(JsonElement stream)
    {
        var frameRate = GetString(stream, "r_frame_rate");
        return new StreamInfo(
            GetString(stream, "codec_name"),
            GetInt(stream, "width"),
            GetInt(stream, "height"),
            GetString(stream, "pix_fmt"),
            ParseRational(frameRate),
            GetInt(stream, "sample_rate"),
            GetInt(stream, "channels"),
            ReadDurationMs(stream));
    }

    private static long ReadDurationMs(JsonElement element)
    {
        if (element.ValueKind != JsonValueKind.Object ||
            !element.TryGetProperty("duration", out var duration))
        {
            return 0;
        }

        var text = duration.ValueKind switch
        {
            JsonValueKind.String => duration.GetString(),
            JsonValueKind.Number => duration.GetRawText(),
            _ => null
        };
        return double.TryParse(
            text,
            NumberStyles.Float,
            CultureInfo.InvariantCulture,
            out var seconds) &&
            seconds > 0 &&
            !double.IsNaN(seconds) &&
            !double.IsInfinity(seconds)
            ? checked((long)Math.Round(seconds * 1000d, MidpointRounding.AwayFromZero))
            : 0;
    }

    private static string? GetString(JsonElement element, string name)
        => element.ValueKind == JsonValueKind.Object &&
            element.TryGetProperty(name, out var value) &&
            value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static int? GetInt(JsonElement element, string name)
    {
        if (element.ValueKind != JsonValueKind.Object ||
            !element.TryGetProperty(name, out var value))
        {
            return null;
        }

        if (value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var number))
        {
            return number;
        }

        return value.ValueKind == JsonValueKind.String &&
            int.TryParse(value.GetString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed)
            ? parsed
            : null;
    }

    private static double? ParseRational(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        var parts = value.Split('/');
        if (parts.Length == 1 &&
            double.TryParse(parts[0], NumberStyles.Float, CultureInfo.InvariantCulture, out var single))
        {
            return single;
        }

        if (parts.Length == 2 &&
            double.TryParse(parts[0], NumberStyles.Float, CultureInfo.InvariantCulture, out var numerator) &&
            double.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out var denominator) &&
            denominator != 0)
        {
            return numerator / denominator;
        }

        return null;
    }

    private static double? ParseMeanVolumeDb(string output)
    {
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

        return double.TryParse(
            output[start..end].Trim(),
            NumberStyles.Float,
            CultureInfo.InvariantCulture,
            out var value)
            ? value
            : null;
    }

    private static IReadOnlyList<SubtitleCue> ParseSrt(string text)
    {
        var normalized = text
            .Replace("\r\n", "\n", StringComparison.Ordinal)
            .Replace('\r', '\n');
        var blocks = normalized.Split(
            "\n\n",
            StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var cues = new List<SubtitleCue>();
        foreach (var block in blocks)
        {
            var lines = block.Split('\n');
            if (lines.Length < 3)
            {
                continue;
            }

            var timelineIndex = Array.FindIndex(lines, line => line.Contains("-->", StringComparison.Ordinal));
            if (timelineIndex < 0)
            {
                continue;
            }

            var parts = lines[timelineIndex].Split("-->", StringSplitOptions.TrimEntries);
            if (parts.Length != 2 ||
                !TryParseSrtTime(parts[0], out var startMs) ||
                !TryParseSrtTime(parts[1], out var endMs))
            {
                continue;
            }

            var cueLines = lines
                .Skip(timelineIndex + 1)
                .Where(line => !string.IsNullOrWhiteSpace(line))
                .ToArray();
            cues.Add(new SubtitleCue(startMs, endMs, cueLines));
        }

        return cues;
    }

    private static bool TryParseSrtTime(string value, out long milliseconds)
    {
        milliseconds = 0;
        var normalized = value.Trim().Replace(',', '.');
        if (!TimeSpan.TryParse(
                normalized,
                CultureInfo.InvariantCulture,
                out var time))
        {
            return false;
        }

        milliseconds = (long)Math.Round(time.TotalMilliseconds, MidpointRounding.AwayFromZero);
        return true;
    }

    private static void Add(
        ICollection<ValidationIssue> issues,
        string code,
        string path,
        string message)
        => issues.Add(new ValidationIssue(code, path, message));

    private static ValidationReport Report(
        IReadOnlyList<ValidationIssue> errors,
        IReadOnlyList<ValidationIssue> warnings)
        => new(errors.Count == 0, errors, warnings);

    private static void TryDeleteDirectory(string path)
    {
        try
        {
            if (Directory.Exists(path))
            {
                Directory.Delete(path, recursive: true);
            }
        }
        catch (IOException)
        {
            // The temp cleaner can retry.
        }
    }

    private sealed record StreamInfo(
        string? Codec,
        int? Width,
        int? Height,
        string? PixelFormat,
        double? Fps,
        int? SampleRate,
        int? Channels,
        long DurationMs);

    private sealed record ProbeMetadata(
        StreamInfo? Video,
        StreamInfo? Audio,
        long DurationMs)
    {
        public static ProbeMetadata Empty { get; } = new(null, null, 0);
    }

    private sealed record SubtitleCue(
        long StartMs,
        long EndMs,
        IReadOnlyList<string> Lines);
}
