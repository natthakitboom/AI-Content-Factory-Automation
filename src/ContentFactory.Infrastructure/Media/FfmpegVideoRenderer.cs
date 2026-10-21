using System.Globalization;
using System.Text;
using ContentFactory.Contracts;
using ContentFactory.Core.Abstractions;
using ContentFactory.Core.Exceptions;
using ContentFactory.Infrastructure.Storage;

namespace ContentFactory.Infrastructure.Media;

public sealed class FfmpegRenderOptions
{
    public string FfmpegExecutable { get; init; } = "ffmpeg";

    public TimeSpan Timeout { get; init; } = TimeSpan.FromMinutes(10);

    public string VideoBitrate { get; init; } = ProductBasicTemplate.VideoBitrate;

    public string AudioBitrate { get; init; } = ProductBasicTemplate.AudioBitrate;

    public int ThumbnailAtMs { get; init; } = ProductBasicTemplate.ThumbnailFrameMs;

    internal void Validate()
    {
        if (string.IsNullOrWhiteSpace(FfmpegExecutable))
        {
            throw new ValidationException(
                "RENDER_CONFIG_INVALID",
                "An FFmpeg executable name is required.");
        }

        if (Timeout <= TimeSpan.Zero || Timeout > TimeSpan.FromMinutes(10))
        {
            throw new ValidationException(
                "RENDER_CONFIG_INVALID",
                "The render timeout must be between zero and ten minutes.");
        }

        if (string.IsNullOrWhiteSpace(VideoBitrate) ||
            string.IsNullOrWhiteSpace(AudioBitrate))
        {
            throw new ValidationException(
                "RENDER_CONFIG_INVALID",
                "Video and audio bitrates are required.");
        }

        if (ThumbnailAtMs < 0)
        {
            throw new ValidationException(
                "RENDER_CONFIG_INVALID",
                "The thumbnail frame time cannot be negative.");
        }
    }
}

/// <summary>
/// Builds the FFmpeg filter graph and argument list for a product-basic render. The method
/// is pure and receives only generated temp paths, never model text or a user filename.
/// Audio is padded/trimmed to the same integer-frame timeline as video, and the final
/// video is faststart H.264/yuv420p with AAC 48 kHz stereo.
/// </summary>
public static class FfmpegRenderCommand
{
    public static List<string> Build(
        RenderManifest manifest,
        IReadOnlyList<string> visualPaths,
        IReadOnlyList<string?> audioPaths,
        string assPath,
        string outputPath,
        EncoderSelection selection,
        FfmpegRenderOptions options)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        ArgumentNullException.ThrowIfNull(visualPaths);
        ArgumentNullException.ThrowIfNull(audioPaths);
        ArgumentNullException.ThrowIfNull(selection);
        ArgumentNullException.ThrowIfNull(options);
        options.Validate();

        if (manifest.Segments.Count == 0 ||
            visualPaths.Count != manifest.Segments.Count ||
            audioPaths.Count != manifest.Segments.Count ||
            string.IsNullOrWhiteSpace(assPath) ||
            string.IsNullOrWhiteSpace(outputPath))
        {
            throw new ValidationException(
                "RENDER_INPUT_INVALID",
                "Every timeline segment requires one visual input, an audio slot, a caption file and an output path.");
        }

        for (var index = 0; index < visualPaths.Count; index++)
        {
            if (string.IsNullOrWhiteSpace(visualPaths[index]) ||
                (audioPaths[index] is { } audioPath && string.IsNullOrWhiteSpace(audioPath)))
            {
                throw new ValidationException(
                    "RENDER_INPUT_INVALID",
                    "Render input paths must be non-empty local paths.");
            }
        }

        var args = new List<string>
        {
            "-hide_banner",
            "-nostdin",
            "-y",
            "-loglevel", "error"
        };

        var inputIndex = 0;
        var audioIndices = new int?[manifest.Segments.Count];
        for (var index = 0; index < manifest.Segments.Count; index++)
        {
            var segment = manifest.Segments[index];
            var visualPath = visualPaths[index];
            var audioPath = audioPaths[index];
            var seconds = FfmpegFilterSafety.Invariant(segment.DurationMs / 1000d);

            if (segment.Visual.Kind == AssetKind.Image)
            {
                args.Add("-loop");
                args.Add("1");
                args.Add("-framerate");
                args.Add(manifest.Fps.ToString(CultureInfo.InvariantCulture));
                args.Add("-t");
                args.Add(seconds);
            }
            else if (segment.Visual.Kind == AssetKind.Video)
            {
                args.Add("-stream_loop");
                args.Add("-1");
                args.Add("-t");
                args.Add(seconds);
            }
            else
            {
                throw new ValidationException(
                    "RENDER_VISUAL_INVALID",
                    "A timeline visual must be an image or a video.");
            }

            args.Add("-i");
            args.Add(visualPath);

            if (audioPath is not null)
            {
                audioIndices[index] = inputIndex + 1;
                args.Add("-i");
                args.Add(audioPath);
                inputIndex += 2;
            }
            else
            {
                inputIndex++;
            }
        }

        var filters = new List<string>();
        var visualInputIndex = 0;
        for (var index = 0; index < manifest.Segments.Count; index++)
        {
            var segment = manifest.Segments[index];
            var seconds = FfmpegFilterSafety.Invariant(segment.DurationMs / 1000d);
            var frames = Math.Max(
                1,
                (long)Math.Round(
                    segment.DurationMs * manifest.Fps / 1000d,
                    MidpointRounding.AwayFromZero));

            var visualFilter =
                $"[{visualInputIndex}:v]" +
                $"trim=start=0:duration={seconds},setpts=PTS-STARTPTS," +
                $"fps={manifest.Fps}," +
                $"scale={manifest.Width}:{manifest.Height}:" +
                "force_original_aspect_ratio=decrease:force_divisible_by=2," +
                $"pad={manifest.Width}:{manifest.Height}:(ow-iw)/2:(oh-ih)/2:color=0x101014," +
                "setsar=1";

            if (segment.Transform.StartsWith("zoom", StringComparison.Ordinal))
            {
                visualFilter +=
                    $",zoompan=z='min(1+0.0005*on,1.05)':" +
                    "x='iw/2-(iw/zoom/2)':y='ih/2-(ih/zoom/2)':" +
                    $"d=1:s={manifest.Width}x{manifest.Height}:fps={manifest.Fps}";
            }
            else if (segment.Transform.StartsWith("cta", StringComparison.Ordinal))
            {
                visualFilter +=
                    $",zoompan=z='min(1+0.0006*on,1.06)':" +
                    "x='iw/2-(iw/zoom/2)':y='ih/2-(ih/zoom/2)':" +
                    $"d=1:s={manifest.Width}x{manifest.Height}:fps={manifest.Fps}";
            }
            else if (segment.Transform == "pan:right")
            {
                visualFilter +=
                    ",zoompan=z='1.05':" +
                    $"x='(iw-iw/zoom)*on/{frames}':y='ih/2-(ih/zoom/2)':" +
                    $"d=1:s={manifest.Width}x{manifest.Height}:fps={manifest.Fps}";
            }
            else if (segment.Transform == "pan:left")
            {
                visualFilter +=
                    ",zoompan=z='1.05':" +
                    $"x='(iw-iw/zoom)*(1-on/{frames})':y='ih/2-(ih/zoom/2)':" +
                    $"d=1:s={manifest.Width}x{manifest.Height}:fps={manifest.Fps}";
            }

            // A JPEG still decodes as full-range yuvj420p; convert to limited-range yuv420p
            // explicitly or the encoder keeps the full-range tag and QA rejects the file.
            visualFilter += $",scale=in_range=auto:out_range=tv,format=yuv420p[v{index}]";
            filters.Add(visualFilter);

            var audioIndex = audioIndices[index];
            if (audioIndex is null)
            {
                filters.Add(
                    $"anullsrc=r={ProductBasicTemplate.AudioSampleRate}:cl=stereo," +
                    $"atrim=duration={seconds},asetpts=PTS-STARTPTS[a{index}]");
            }
            else
            {
                filters.Add(
                    $"[{audioIndex}:a]aresample={ProductBasicTemplate.AudioSampleRate}," +
                    "aformat=sample_fmts=fltp:channel_layouts=stereo," +
                    $"atrim=duration={seconds},apad=whole_dur={seconds}," +
                    $"asetpts=PTS-STARTPTS[a{index}]");
            }

            visualInputIndex += audioIndex is null ? 1 : 2;
        }

        var videoInputs = string.Concat(
            manifest.Segments.Select((_, index) => $"[v{index}]"));
        var audioInputs = string.Concat(
            manifest.Segments.Select((_, index) => $"[a{index}]"));
        filters.Add(
            $"{videoInputs}concat=n={manifest.Segments.Count}:v=1:a=0[vcat]");
        filters.Add(
            $"{audioInputs}concat=n={manifest.Segments.Count}:v=0:a=1[acat]");
        filters.Add(
            $"[vcat]subtitles=filename={FfmpegFilterSafety.EscapePath(assPath)}[vout]");

        args.Add("-filter_complex");
        args.Add(string.Join(";", filters));
        args.Add("-map");
        args.Add("[vout]");
        args.Add("-map");
        args.Add("[acat]");
        args.Add("-c:v");
        args.Add(selection.VideoEncoder);
        if (selection.Threads > 0)
        {
            args.Add("-threads");
            args.Add(selection.Threads.ToString(CultureInfo.InvariantCulture));
            args.Add("-preset");
            args.Add("medium");
        }

        args.Add("-b:v");
        args.Add(options.VideoBitrate);
        args.Add("-pix_fmt");
        args.Add("yuv420p");
        args.Add("-r");
        args.Add(manifest.Fps.ToString(CultureInfo.InvariantCulture));
        args.Add("-c:a");
        args.Add("aac");
        args.Add("-b:a");
        args.Add(options.AudioBitrate);
        args.Add("-ar");
        args.Add(ProductBasicTemplate.AudioSampleRate.ToString(CultureInfo.InvariantCulture));
        args.Add("-ac");
        args.Add(ProductBasicTemplate.AudioChannels.ToString(CultureInfo.InvariantCulture));
        args.Add("-movflags");
        args.Add("+faststart");
        args.Add("-t");
        args.Add(FfmpegFilterSafety.Invariant(
            manifest.Segments.Sum(segment => segment.DurationMs) / 1000d));
        args.Add(outputPath);
        return args;
    }
}

/// <summary>
/// Native vertical-video renderer. It validates captions before starting a process, probes
/// the encoder/filter/font, copies only local stored assets into UUID-named temp files,
/// burns an escaped ASS file, verifies a real MP4 and JPEG, and only then registers
/// content-addressed assets. A failed render never returns a successful result.
/// </summary>
public sealed class FfmpegVideoRenderer : IVideoRenderer
{
    private readonly IAssetStore _assets;
    private readonly StorageOptions _storage;
    private readonly IProcessRunner _runner;
    private readonly EncoderProbe _encoderProbe;
    private readonly CaptionLayout _captions;
    private readonly FfmpegRenderOptions _options;

    public FfmpegVideoRenderer(
        IAssetStore assets,
        StorageOptions storage,
        IProcessRunner runner,
        EncoderProbe encoderProbe,
        CaptionLayout captions,
        FfmpegRenderOptions? options = null)
    {
        _assets = assets ?? throw new ArgumentNullException(nameof(assets));
        _storage = storage ?? throw new ArgumentNullException(nameof(storage));
        _runner = runner ?? throw new ArgumentNullException(nameof(runner));
        _encoderProbe = encoderProbe ?? throw new ArgumentNullException(nameof(encoderProbe));
        _captions = captions ?? throw new ArgumentNullException(nameof(captions));
        _options = options ?? new FfmpegRenderOptions();
        _options.Validate();
    }

    public async Task<RenderResult> RenderAsync(
        RenderManifest manifest,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        ValidateManifest(manifest);

        foreach (var segment in manifest.Segments)
        {
            var layout = _captions.Layout(segment.Caption);
            if (!layout.Passed)
            {
                var issue = layout.Issues.Count > 0
                    ? layout.Issues[0]
                    : new ValidationIssue(
                        "CAPTION_OVERFLOW",
                        "caption",
                        "Caption does not fit the template safe area.");
                throw new ValidationException(issue.Code, issue.Message);
            }
        }

        var selection = await _encoderProbe.ProbeAsync(ct);
        if (!string.Equals(
                selection.FontFingerprint,
                manifest.FontFingerprint,
                StringComparison.Ordinal))
        {
            throw new ValidationException(
                "RENDER_FONT_FINGERPRINT_MISMATCH",
                "The probed font fingerprint does not match the approved render manifest.");
        }

        var tempDirectory = Path.Combine(
            _storage.TempRoot,
            "render",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDirectory);
        var visualPaths = new string[manifest.Segments.Count];
        var audioPaths = new string?[manifest.Segments.Count];
        var assPath = Path.Combine(tempDirectory, "captions.ass");
        var outputPath = Path.Combine(tempDirectory, "video.mp4");
        var thumbnailPath = Path.Combine(tempDirectory, "thumbnail.jpg");

        try
        {
            for (var index = 0; index < manifest.Segments.Count; index++)
            {
                var segment = manifest.Segments[index];
                visualPaths[index] = Path.Combine(
                    tempDirectory,
                    $"visual-{index}.input");
                await CopyAssetAsync(segment.Visual, visualPaths[index], ct);

                if (segment.Audio is not null)
                {
                    audioPaths[index] = Path.Combine(
                        tempDirectory,
                        $"audio-{index}.input");
                    await CopyAssetAsync(segment.Audio, audioPaths[index]!, ct);
                }
            }

            var srt = _captions.BuildSrt(manifest);
            await File.WriteAllTextAsync(
                assPath,
                _captions.BuildAss(manifest),
                new UTF8Encoding(encoderShouldEmitUTF8Identifier: false),
                ct);

            var args = FfmpegRenderCommand.Build(
                manifest,
                visualPaths,
                audioPaths,
                assPath,
                outputPath,
                selection,
                _options);

            var render = await _runner.RunAsync(
                _options.FfmpegExecutable,
                args,
                _options.Timeout,
                ct);

            if (render.TimedOut)
            {
                throw new ValidationException(
                    "RENDER_TIMEOUT",
                    "FFmpeg did not finish the video within the configured timeout.");
            }

            if (render.ExitCode != 0)
            {
                throw new ValidationException(
                    "RENDER_FAILED",
                    "FFmpeg could not render the product video.");
            }

            EnsureMp4(outputPath);

            var totalMs = manifest.Segments.Sum(segment => segment.DurationMs);
            var thumbnailMs = ChooseThumbnailTime(totalMs);
            var thumbArgs = new List<string>
            {
                "-hide_banner",
                "-nostdin",
                "-y",
                "-loglevel", "error",
                "-ss", FfmpegFilterSafety.Invariant(thumbnailMs / 1000d),
                "-i", outputPath,
                "-frames:v", "1",
                "-q:v", "2",
                thumbnailPath
            };
            var thumb = await _runner.RunAsync(
                _options.FfmpegExecutable,
                thumbArgs,
                _options.Timeout,
                ct);

            if (thumb.TimedOut)
            {
                throw new ValidationException(
                    "RENDER_THUMBNAIL_TIMEOUT",
                    "FFmpeg did not finish the thumbnail within the configured timeout.");
            }

            if (thumb.ExitCode != 0)
            {
                throw new ValidationException(
                    "RENDER_THUMBNAIL_FAILED",
                    "FFmpeg could not extract the template thumbnail frame.");
            }

            EnsureJpeg(thumbnailPath);

            AssetRef video;
            await using (var stream = OpenRead(outputPath))
            {
                video = await _assets.PutAsync(stream, AssetKind.Video, "video/mp4", ct);
            }

            AssetRef thumbnail;
            await using (var stream = OpenRead(thumbnailPath))
            {
                thumbnail = await _assets.PutAsync(
                    stream,
                    AssetKind.Thumbnail,
                    "image/jpeg",
                    ct);
            }

            AssetRef subtitles;
            await using (var stream = new MemoryStream(
                Encoding.UTF8.GetBytes(srt),
                writable: false))
            {
                subtitles = await _assets.PutAsync(
                    stream,
                    AssetKind.Subtitle,
                    "application/x-subrip",
                    ct);
            }

            return new RenderResult(
                video,
                thumbnail,
                subtitles,
                ManifestHasher.Compute(manifest),
                totalMs);
        }
        finally
        {
            TryDeleteDirectory(tempDirectory);
        }
    }

    private async Task CopyAssetAsync(
        AssetRef asset,
        string destination,
        CancellationToken ct)
    {
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

        if (new FileInfo(destination).Length <= 0)
        {
            throw new ValidationException(
                "RENDER_INPUT_EMPTY",
                "A render input asset is empty.");
        }
    }

    private long ChooseThumbnailTime(long totalMs)
    {
        if (totalMs <= 0)
        {
            throw new ValidationException(
                "RENDER_DURATION_INVALID",
                "The render manifest has no positive duration.");
        }

        var preferred = _options.ThumbnailAtMs > 0
            ? _options.ThumbnailAtMs
            : (int)Math.Min(ProductBasicTemplate.ThumbnailFrameMs, totalMs / 2);
        return Math.Clamp(preferred, 0, Math.Max(0, totalMs - 1));
    }

    private static void ValidateManifest(RenderManifest manifest)
    {
        if (manifest.SchemaVersion != 1 ||
            manifest.Width != ProductBasicTemplate.Width ||
            manifest.Height != ProductBasicTemplate.Height ||
            manifest.Fps != ProductBasicTemplate.Fps ||
            string.IsNullOrWhiteSpace(manifest.FontFingerprint))
        {
            throw new ValidationException(
                "RENDER_MANIFEST_INVALID",
                "The render manifest does not match the product-basic 1080x1920 30 fps template.");
        }

        if (manifest.Segments is null || manifest.Segments.Count == 0)
        {
            throw new ValidationException(
                "RENDER_MANIFEST_INVALID",
                "The render manifest has no timeline segments.");
        }

        var expectedStartMs = 0L;
        foreach (var segment in manifest.Segments)
        {
            if (segment.StartMs != expectedStartMs || segment.DurationMs <= 0)
            {
                throw new ValidationException(
                    "RENDER_MANIFEST_INVALID",
                    "Timeline segments must be contiguous and have positive durations.");
            }

            expectedStartMs += segment.DurationMs;
        }

        var totalMs = manifest.Segments.Sum(segment => segment.DurationMs);
        if (totalMs < ProductBasicTemplate.MinDurationMs ||
            totalMs > ProductBasicTemplate.MaxDurationMs)
        {
            throw new ValidationException(
                "RENDER_DURATION_OUT_OF_BOUNDS",
                "The render manifest must be between 15 and 35 seconds.");
        }
    }

    private static void EnsureMp4(string path)
    {
        if (!File.Exists(path) || new FileInfo(path).Length <= 0)
        {
            throw new ValidationException(
                "RENDER_OUTPUT_INVALID",
                "FFmpeg reported success but did not produce a non-empty MP4.");
        }

        Span<byte> header = stackalloc byte[12];
        using var stream = File.OpenRead(path);
        var read = stream.Read(header);
        if (read < 12 ||
            !header.Slice(4, 4).SequenceEqual("ftyp"u8))
        {
            throw new ValidationException(
                "RENDER_OUTPUT_INVALID",
                "The render output is not a valid MP4 container.");
        }
    }

    private static void EnsureJpeg(string path)
    {
        if (!File.Exists(path) || new FileInfo(path).Length <= 0)
        {
            throw new ValidationException(
                "RENDER_THUMBNAIL_INVALID",
                "The thumbnail extraction did not produce a file.");
        }

        Span<byte> header = stackalloc byte[3];
        using var stream = File.OpenRead(path);
        var read = stream.Read(header);
        if (read < 3 ||
            header[0] != 0xFF ||
            header[1] != 0xD8 ||
            header[2] != 0xFF)
        {
            throw new ValidationException(
                "RENDER_THUMBNAIL_INVALID",
                "The thumbnail is not a JPEG image.");
        }
    }

    private static FileStream OpenRead(string path)
        => new(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            81920,
            FileOptions.Asynchronous | FileOptions.SequentialScan);

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
        catch (UnauthorizedAccessException)
        {
            // The temp cleaner can retry.
        }
    }
}
