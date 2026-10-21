using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using ContentFactory.Contracts;
using ContentFactory.Core.Abstractions;
using ContentFactory.Infrastructure.Media;
using ContentFactory.Infrastructure.Storage;
using Xunit;

namespace ContentFactory.MediaTests;

/// <summary>
/// Native media tests are executable only when FFmpeg/FFprobe, libass and a Thai-capable
/// font are present. They are marked skipped otherwise; an unavailable media toolchain is
/// never reported as a pass.
/// </summary>
public sealed class MediaFactAttribute : FactAttribute
{
    public MediaFactAttribute()
    {
        if (!MediaPrerequisites.Ready)
        {
            Skip = MediaPrerequisites.Reason;
        }
    }
}

internal static class MediaPrerequisites
{
    public static readonly string TestFontFingerprint = new('a', 64);

    public static bool Ready { get; }

    public static string Reason { get; } = string.Empty;

    static MediaPrerequisites()
    {
        try
        {
            var runner = new SafeProcessRunner();
            var ffmpeg = runner.RunAsync(
                "ffmpeg",
                ["-version"],
                TimeSpan.FromSeconds(10),
                CancellationToken.None).GetAwaiter().GetResult();
            var ffprobe = runner.RunAsync(
                "ffprobe",
                ["-version"],
                TimeSpan.FromSeconds(10),
                CancellationToken.None).GetAwaiter().GetResult();
            if (ffmpeg.ExitCode != 0 || ffprobe.ExitCode != 0)
            {
                Reason = "FFmpeg/FFprobe are unavailable; native media tests are blocked.";
                return;
            }

            var filters = runner.RunAsync(
                "ffmpeg",
                ["-hide_banner", "-nostdin", "-filters"],
                TimeSpan.FromSeconds(20),
                CancellationToken.None).GetAwaiter().GetResult();
            if (filters.ExitCode != 0 ||
                !EncoderProbe.HasFilter(filters.StandardOutput, "subtitles"))
            {
                Reason = "FFmpeg lacks the libass subtitles filter; media tests are blocked.";
                return;
            }

            var probeDirectory = Path.Combine(
                Path.GetTempPath(),
                "cf-media-prereq",
                Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(probeDirectory);
            var assPath = Path.Combine(probeDirectory, "probe.ass");
            try
            {
                var layout = new CaptionLayout(
                    new ConservativeTextMeasurer(),
                    new CaptionLayoutOptions());
                var manifest = new RenderManifest(
                    1,
                    Guid.NewGuid(),
                    new string('0', 64),
                    ProductBasicTemplate.Version,
                    ProductBasicTemplate.RendererVersion,
                    MediaPrerequisites.TestFontFingerprint,
                    ProductBasicTemplate.Width,
                    ProductBasicTemplate.Height,
                    ProductBasicTemplate.Fps,
                    [
                        new TimelineSegment(
                            "probe",
                            0,
                            200,
                            new AssetRef(
                                Guid.Empty,
                                new string('0', 64),
                                "assets/sha256/00/" + new string('0', 64),
                                AssetKind.Image,
                                "image/png",
                                1,
                                null),
                            null,
                            "ทดสอบ",
                            "still")
                    ]);
                File.WriteAllText(assPath, layout.BuildAss(manifest));
                var font = runner.RunAsync(
                    "ffmpeg",
                    [
                        "-hide_banner", "-nostdin", "-y", "-loglevel", "error",
                        "-f", "lavfi", "-i", "color=c=black:s=64x64:d=0.2",
                        "-vf", "subtitles=filename=" + EscapeFilterPath(assPath),
                        "-frames:v", "1", "-f", "null", "-"
                    ],
                    TimeSpan.FromSeconds(30),
                    CancellationToken.None).GetAwaiter().GetResult();
                if (font.ExitCode != 0)
                {
                    Reason = "No Thai-capable font/libass combination is available; media tests are blocked.";
                    return;
                }
            }
            finally
            {
                TryDeleteDirectory(probeDirectory);
            }

            Ready = true;
        }
        catch (Exception)
        {
            Reason = "FFmpeg/FFprobe or a Thai-capable font is unavailable; media tests are blocked.";
        }
    }

    private static string EscapeFilterPath(string path)
        => path
            .Replace("\\", "\\\\", StringComparison.Ordinal)
            .Replace(":", "\\:", StringComparison.Ordinal)
            .Replace("'", "\\'", StringComparison.Ordinal)
            .Replace(",", "\\,", StringComparison.Ordinal)
            .Replace(" ", "\\ ", StringComparison.Ordinal);

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
            // Best-effort cleanup for the prerequisite probe.
        }
    }
}

public sealed class RenderGoldenTests
{
    [MediaFact]
    public Task ThaiFixtureRendersVerticalMp4WithBurnedCaptions()
        => RenderFixtureAsync(
            "thai",
            "th-TH",
            "#2E86C1",
            [
                ("hook", "พบกับผลิตภัณฑ์ใหม่", "ผิวชุ่มชื้นตลอดวัน"),
                ("benefit", "ช่วยให้ผิวชุ่มชื้น", "ผิวชุ่มชื้นตลอดวัน"),
                ("demo", "ใช้งานได้ทุกวัน", "ใช้งานได้ทุกวัน"),
                ("cta", "สั่งซื้อวันนี้", "สั่งซื้อวันนี้")
            ]);

    [MediaFact]
    public Task EnglishFixtureRendersVerticalMp4WithBurnedCaptions()
        => RenderFixtureAsync(
            "english",
            "en-US",
            "#239B56",
            [
                ("hook", "Meet the new product", "Meet the new product"),
                ("benefit", "Hydration every day", "Hydration every day"),
                ("demo", "Easy to use at home", "Easy to use at home"),
                ("cta", "Order today", "Order today")
            ]);

    [MediaFact]
    public Task LongCaptionFixtureStaysWithinTwoSafeLines()
        => RenderFixtureAsync(
            "long",
            "th-TH",
            "#B03A2E",
            [
                ("hook", "ผิวชุ่มชื้นและดูเรียบเนียน", "ผิวชุ่มชื้นและดูเรียบเนียน"),
                ("benefit", "ผิวชุ่มชื้นและดูเรียบเนียน", "ผิวชุ่มชื้นและดูเรียบเนียน"),
                ("demo", "ผิวชุ่มชื้นและดูเรียบเนียน", "ผิวชุ่มชื้นและดูเรียบเนียน"),
                ("cta", "ผิวชุ่มชื้นและดูเรียบเนียน", "ผิวชุ่มชื้นและดูเรียบเนียน")
            ]);

    private static async Task RenderFixtureAsync(
        string name,
        string locale,
        string color,
        IReadOnlyList<(string Role, string Narration, string Caption)> scenes)
    {
        var root = Path.Combine(
            Path.GetTempPath(),
            "cf-media-tests",
            name + "-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var storage = StorageOptions.Create(root);
        var runner = new SafeProcessRunner();
        var assetStore = new TempAssetStore(root);

        try
        {
            var imagePath = Path.Combine(root, "scene.png");
            await RunMediaAsync(
                runner,
                [
                    "-hide_banner", "-nostdin", "-y", "-loglevel", "error",
                    "-f", "lavfi", "-i", $"color=c={color}:s=1080x1920:d=1",
                    "-frames:v", "1", imagePath
                ]);

            var visual = assetStore.AddFile(imagePath, AssetKind.Image, "image/png");
            var visuals = scenes
                .Select((_, index) => $"scene-{index + 1}")
                .ToDictionary(sceneId => sceneId, _ => visual, StringComparer.Ordinal);
            var voices = new List<VoiceSegment>();
            for (var index = 0; index < scenes.Count; index++)
            {
                var audioPath = Path.Combine(root, $"voice-{index}.wav");
                await RunMediaAsync(
                    runner,
                    [
                        "-hide_banner", "-nostdin", "-y", "-loglevel", "error",
                        "-f", "lavfi",
                        "-i", "sine=frequency=440:sample_rate=48000:duration=4",
                        "-af", "volume=0.15",
                        "-c:a", "pcm_s16le", audioPath
                    ]);
                var audio = assetStore.AddFile(
                    audioPath,
                    AssetKind.Audio,
                    "audio/wav",
                    durationMs: 4_000);
                voices.Add(new VoiceSegment($"scene-{index + 1}", audio, 4_000));
            }

            var script = new ScriptDocument(
                1,
                Guid.NewGuid(),
                locale,
                "Product fixture",
                "Disclosure",
                "Caption",
                scenes.Select((scene, index) => new ScriptScene(
                    $"scene-{index + 1}",
                    scene.Role,
                    scene.Narration,
                    scene.Caption,
                    [],
                    null,
                    null)).ToArray());

            var compiler = new TimelineCompiler(
                new TimelineOptions { FontFingerprint = MediaPrerequisites.TestFontFingerprint });
            var manifest = compiler.Compile(
                Guid.NewGuid(),
                script,
                visuals,
                voices,
                ProductBasicTemplate.Version);

            Assert.Equal(1080, manifest.Width);
            Assert.Equal(1920, manifest.Height);
            Assert.Equal(30, manifest.Fps);

            var captions = new CaptionLayout(
                new ConservativeTextMeasurer(),
                new CaptionLayoutOptions());
            foreach (var segment in manifest.Segments)
            {
                var report = captions.Layout(segment.Caption);
                Assert.True(report.Passed);
                Assert.InRange(report.Lines.Count, 1, 2);
            }

            var probe = new EncoderProbe(
                runner,
                storage,
                new EncoderProbeOptions
                {
                    FontFingerprint = MediaPrerequisites.TestFontFingerprint
                });
            var renderer = new FfmpegVideoRenderer(
                assetStore,
                storage,
                runner,
                probe,
                captions,
                new FfmpegRenderOptions());

            var result = await renderer.RenderAsync(manifest, CancellationToken.None);
            var videoPath = assetStore.PathFor(result.Video.Id);
            var thumbnailPath = assetStore.PathFor(result.Thumbnail.Id);
            var subtitlePath = assetStore.PathFor(result.Subtitles.Id);

            Assert.Matches("^[0-9a-f]{64}$", result.ManifestSha256);
            Assert.InRange(result.DurationMs, 15_000, 35_000);

            var probeJson = await RunFfprobeAsync(
                runner,
                [
                    "-v", "error",
                    "-show_entries",
                    "stream=codec_type,codec_name,width,height,pix_fmt,r_frame_rate,sample_rate,channels",
                    "-show_entries", "format=duration",
                    "-of", "json",
                    videoPath
                ]);
            using var document = JsonDocument.Parse(probeJson.StandardOutput);
            var rootElement = document.RootElement;
            var video = rootElement.GetProperty("streams")
                .EnumerateArray()
                .First(stream => stream.GetProperty("codec_type").GetString() == "video");
            var audioStream = rootElement.GetProperty("streams")
                .EnumerateArray()
                .First(stream => stream.GetProperty("codec_type").GetString() == "audio");

            Assert.Equal("h264", video.GetProperty("codec_name").GetString());
            Assert.Equal(1080, video.GetProperty("width").GetInt32());
            Assert.Equal(1920, video.GetProperty("height").GetInt32());
            Assert.Equal("yuv420p", video.GetProperty("pix_fmt").GetString());
            Assert.Equal("30/1", video.GetProperty("r_frame_rate").GetString());
            Assert.Equal("aac", audioStream.GetProperty("codec_name").GetString());
            Assert.Equal("48000", audioStream.GetProperty("sample_rate").GetString());
            Assert.Equal(2, audioStream.GetProperty("channels").GetInt32());

            var durationSeconds = double.Parse(
                rootElement.GetProperty("format").GetProperty("duration").GetString()!,
                CultureInfo.InvariantCulture);
            Assert.InRange(durationSeconds, 15d, 35d);
            AssertFastStart(videoPath);
            AssertJpeg(thumbnailPath);

            var srt = await File.ReadAllTextAsync(subtitlePath);
            Assert.Contains("-->", srt, StringComparison.Ordinal);
            // A long caption is wrapped onto two lines; the wrap must not change the text.
            Assert.Contains(
                scenes[0].Caption,
                srt.Replace("\r", string.Empty).Replace("\n", string.Empty),
                StringComparison.Ordinal);

            var framePath = Path.Combine(root, "sample.ppm");
            await RunMediaAsync(
                runner,
                [
                    "-hide_banner", "-nostdin", "-y", "-loglevel", "error",
                    "-ss", "1", "-i", videoPath,
                    "-frames:v", "1", "-f", "image2", "-vcodec", "ppm", framePath
                ]);
            var frame = ReadPpm(framePath);
            var background = ParseRgb(color);
            var backgroundSample = AverageRegion(frame, 20, 20, 80, 80);
            AssertColorNear(background, backgroundSample, tolerance: 35);

            var changedPixels = CountDifferentPixels(
                frame,
                140,
                1280,
                800,
                200,
                background,
                tolerance: 35);
            Assert.True(
                changedPixels > 50,
                $"Expected burned caption pixels in the safe area, found {changedPixels}.");
        }
        finally
        {
            TryDeleteDirectory(root);
        }
    }

    private static async Task<ProcessResult> RunMediaAsync(
        SafeProcessRunner runner,
        IReadOnlyList<string> args)
    {
        var result = await runner.RunAsync(
            "ffmpeg",
            args,
            TimeSpan.FromMinutes(10),
            CancellationToken.None);
        if (result.ExitCode != 0)
        {
            throw new Xunit.Sdk.XunitException(
                $"FFmpeg failed with code {result.ExitCode}: {result.StandardError}");
        }

        return result;
    }

    private static async Task<ProcessResult> RunFfprobeAsync(
        SafeProcessRunner runner,
        IReadOnlyList<string> args)
    {
        var result = await runner.RunAsync(
            "ffprobe",
            args,
            TimeSpan.FromSeconds(30),
            CancellationToken.None);
        if (result.ExitCode != 0)
        {
            throw new Xunit.Sdk.XunitException(
                $"FFprobe failed with code {result.ExitCode}: {result.StandardError}");
        }

        return result;
    }

    private static void AssertFastStart(string path)
    {
        using var stream = File.OpenRead(path);
        var buffer = new byte[4 * 1024 * 1024];
        var read = stream.Read(buffer);
        var header = Encoding.ASCII.GetString(buffer, 0, read);
        var moov = header.IndexOf("moov", StringComparison.Ordinal);
        var mdat = header.IndexOf("mdat", StringComparison.Ordinal);
        Assert.True(
            moov >= 0 && (mdat < 0 || moov < mdat),
            "The MP4 must place the moov atom before mdat for faststart playback.");
    }

    private static void AssertJpeg(string path)
    {
        var header = File.ReadAllBytes(path);
        Assert.True(header.Length > 3);
        Assert.Equal(0xFF, (int)header[0]);
        Assert.Equal(0xD8, (int)header[1]);
        Assert.Equal(0xFF, (int)header[2]);
    }

    private static PpmImage ReadPpm(string path)
    {
        var bytes = File.ReadAllBytes(path);
        var position = 0;
        var magic = NextToken(bytes, ref position);
        if (magic != "P6")
        {
            throw new Xunit.Sdk.XunitException("Expected a binary PPM frame.");
        }

        var width = int.Parse(NextToken(bytes, ref position), CultureInfo.InvariantCulture);
        var height = int.Parse(NextToken(bytes, ref position), CultureInfo.InvariantCulture);
        var max = int.Parse(NextToken(bytes, ref position), CultureInfo.InvariantCulture);
        if (max != 255)
        {
            throw new Xunit.Sdk.XunitException("Expected an 8-bit PPM frame.");
        }

        while (position < bytes.Length && char.IsWhiteSpace((char)bytes[position]))
        {
            position++;
        }

        return new PpmImage(width, height, bytes[position..]);
    }

    private static string NextToken(byte[] bytes, ref int position)
    {
        while (position < bytes.Length)
        {
            while (position < bytes.Length && char.IsWhiteSpace((char)bytes[position]))
            {
                position++;
            }

            if (position < bytes.Length && bytes[position] == '#')
            {
                while (position < bytes.Length && bytes[position] != '\n')
                {
                    position++;
                }

                continue;
            }

            break;
        }

        var start = position;
        while (position < bytes.Length && !char.IsWhiteSpace((char)bytes[position]))
        {
            position++;
        }

        return Encoding.ASCII.GetString(bytes, start, position - start);
    }

    private static (int R, int G, int B) AverageRegion(
        PpmImage image,
        int x,
        int y,
        int width,
        int height)
    {
        long red = 0;
        long green = 0;
        long blue = 0;
        var count = 0;
        for (var row = y; row < y + height; row++)
        {
            for (var column = x; column < x + width; column++)
            {
                var offset = (row * image.Width + column) * 3;
                red += image.Pixels[offset];
                green += image.Pixels[offset + 1];
                blue += image.Pixels[offset + 2];
                count++;
            }
        }

        return (
            (int)(red / count),
            (int)(green / count),
            (int)(blue / count));
    }

    private static int CountDifferentPixels(
        PpmImage image,
        int x,
        int y,
        int width,
        int height,
        (int R, int G, int B) background,
        int tolerance)
    {
        var count = 0;
        for (var row = y; row < y + height; row++)
        {
            for (var column = x; column < x + width; column++)
            {
                var offset = (row * image.Width + column) * 3;
                if (Math.Abs(image.Pixels[offset] - background.R) > tolerance ||
                    Math.Abs(image.Pixels[offset + 1] - background.G) > tolerance ||
                    Math.Abs(image.Pixels[offset + 2] - background.B) > tolerance)
                {
                    count++;
                }
            }
        }

        return count;
    }

    private static void AssertColorNear(
        (int R, int G, int B) expected,
        (int R, int G, int B) actual,
        int tolerance)
    {
        Assert.InRange(Math.Abs(expected.R - actual.R), 0, tolerance);
        Assert.InRange(Math.Abs(expected.G - actual.G), 0, tolerance);
        Assert.InRange(Math.Abs(expected.B - actual.B), 0, tolerance);
    }

    private static (int R, int G, int B) ParseRgb(string hex)
    {
        var value = Convert.ToInt32(hex.TrimStart('#'), 16);
        return ((value >> 16) & 0xFF, (value >> 8) & 0xFF, value & 0xFF);
    }

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
            // Best-effort cleanup for a test fixture.
        }
    }

    private sealed record PpmImage(int Width, int Height, byte[] Pixels);

    private sealed class TempAssetStore : IAssetStore
    {
        private readonly string _root;
        private readonly Dictionary<Guid, string> _paths = [];
        private readonly Dictionary<Guid, AssetRef> _assets = [];

        public TempAssetStore(string root)
        {
            _root = root;
        }

        public AssetRef AddFile(
            string path,
            AssetKind kind,
            string mime,
            long? durationMs = null)
        {
            var destination = Path.Combine(
                _root,
                Guid.NewGuid().ToString("N") + ".asset");
            File.Copy(path, destination, overwrite: true);
            var bytes = File.ReadAllBytes(destination);
            var hash = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
            var id = Guid.NewGuid();
            var asset = new AssetRef(
                id,
                hash,
                $"assets/sha256/{hash[..2]}/{hash}",
                kind,
                mime,
                bytes.Length,
                durationMs);
            _paths[id] = destination;
            _assets[id] = asset;
            return asset;
        }

        public async Task<AssetRef> PutAsync(
            Stream input,
            AssetKind kind,
            string mime,
            CancellationToken ct)
        {
            var destination = Path.Combine(
                _root,
                Guid.NewGuid().ToString("N") + ".asset");
            await using (var output = new FileStream(
                destination,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None,
                81920,
                FileOptions.Asynchronous))
            {
                await input.CopyToAsync(output, ct);
            }

            return AddFile(destination, kind, mime);
        }

        public Task<Stream> OpenReadAsync(Guid assetId, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            return Task.FromResult<Stream>(File.OpenRead(_paths[assetId]));
        }

        public string PathFor(Guid assetId) => _paths[assetId];
    }
}
