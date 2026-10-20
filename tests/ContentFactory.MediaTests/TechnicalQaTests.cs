using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using ContentFactory.Contracts;
using ContentFactory.Core.Abstractions;
using ContentFactory.Infrastructure.Media;
using ContentFactory.Infrastructure.Storage;

namespace ContentFactory.MediaTests;

/// <summary>
/// Real-media technical QA. These tests are skipped when FFmpeg/FFprobe is unavailable;
/// an absent toolchain is reported as blocked, never converted into a synthetic pass.
/// </summary>
public sealed class TechnicalQaTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        "cf-technical-qa-tests",
        Guid.NewGuid().ToString("N"));
    private readonly SafeProcessRunner _runner = new();
    private readonly StorageOptions _storage;
    private readonly TempAssetStore _store;

    public TechnicalQaTests()
    {
        Directory.CreateDirectory(_root);
        _storage = StorageOptions.Create(_root);
        _store = new TempAssetStore(_root);
    }

    [MediaFact]
    public async Task CorruptMp4Rejected()
    {
        var corrupt = Path.Combine(_root, "corrupt.mp4");
        await File.WriteAllTextAsync(corrupt, "this is not an MP4");
        var result = CreateResult(corrupt, 16_000, DefaultSrt());

        var report = await CreateQa().CheckAsync(result, CancellationToken.None);

        Assert.False(report.Passed);
        Assert.Contains(report.Errors, issue => issue.Code == "QA_VIDEO_CORRUPT");
    }

    [MediaFact]
    public async Task MissingAudioRejectedWhenNarrationExpected()
    {
        var clip = await RenderClipAsync(1080, 1920, 16, audio: false);
        var result = CreateResult(clip, 16_000, DefaultSrt());

        var report = await CreateQa().CheckAsync(result, CancellationToken.None);

        Assert.False(report.Passed);
        Assert.Contains(report.Errors, issue => issue.Code == "QA_AUDIO_MISSING");
    }

    [MediaFact]
    public async Task WrongDimensionsRejected()
    {
        var clip = await RenderClipAsync(720, 1280, 16, audio: true);
        var result = CreateResult(clip, 16_000, DefaultSrt());

        var report = await CreateQa().CheckAsync(result, CancellationToken.None);

        Assert.False(report.Passed);
        Assert.Contains(report.Errors, issue => issue.Code == "QA_DIMENSIONS_INVALID");
    }

    [MediaFact]
    public async Task DurationOutsideBoundsRejected()
    {
        var clip = await RenderClipAsync(1080, 1920, 10, audio: true);
        var result = CreateResult(clip, 10_000, DefaultSrt(10));

        var report = await CreateQa().CheckAsync(result, CancellationToken.None);

        Assert.False(report.Passed);
        Assert.Contains(
            report.Errors,
            issue => issue.Code == "QA_DURATION_OUT_OF_BOUNDS");
    }

    [MediaFact]
    public async Task ValidNarratedClipPasses()
    {
        var clip = await RenderClipAsync(1080, 1920, 16, audio: true);
        var result = CreateResult(clip, 16_000, DefaultSrt());

        var report = await CreateQa().CheckAsync(result, CancellationToken.None);

        Assert.True(report.Passed, Describe(report));
        Assert.Empty(report.Errors);
    }

    [MediaFact]
    public async Task IntentionalEndCardDoesNotTriggerWholeClipFailure()
    {
        var clip = await RenderClipAsync(1080, 1920, 16, audio: true, blackStartSeconds: 14.5);
        var result = CreateResult(clip, 16_000, DefaultSrt());

        var report = await CreateQa().CheckAsync(result, CancellationToken.None);

        Assert.True(report.Passed, Describe(report));
        Assert.DoesNotContain(
            report.Errors,
            issue => issue.Code.StartsWith("QA_BLACK", StringComparison.Ordinal));
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_root))
            {
                Directory.Delete(_root, recursive: true);
            }
        }
        catch (IOException)
        {
            // Best-effort cleanup for a test sandbox.
        }
    }

    private TechnicalQa CreateQa()
        => new(
            _store,
            _storage,
            _runner,
            new TechnicalQaOptions
            {
                ProbeTimeout = TimeSpan.FromSeconds(60),
                FrameCheckTimeout = TimeSpan.FromMinutes(5)
            });

    private async Task<string> RenderClipAsync(
        int width,
        int height,
        int seconds,
        bool audio,
        double? blackStartSeconds = null)
    {
        var path = Path.Combine(_root, Guid.NewGuid().ToString("N") + ".mp4");
        var args = new List<string>
        {
            "-hide_banner",
            "-nostdin",
            "-y",
            "-f", "lavfi",
            "-i", $"testsrc2=size={width}x{height}:rate=30:duration={seconds}"
        };

        if (audio)
        {
            args.Add("-f");
            args.Add("lavfi");
            args.Add("-i");
            args.Add($"sine=frequency=440:sample_rate=48000:duration={seconds}");
            args.Add("-map");
            args.Add("0:v:0");
            args.Add("-map");
            args.Add("1:a:0");
        }

        if (blackStartSeconds is { } start)
        {
            args.Add("-vf");
            args.Add(
                $"drawbox=x=0:y=0:w=iw:h=ih:color=black:t=fill:enable='gte(t,{start.ToString(CultureInfo.InvariantCulture)})'");
        }

        args.Add("-c:v");
        args.Add("libx264");
        args.Add("-preset");
        args.Add("ultrafast");
        args.Add("-pix_fmt");
        args.Add("yuv420p");
        args.Add("-r");
        args.Add("30");

        if (audio)
        {
            args.Add("-c:a");
            args.Add("aac");
            args.Add("-b:a");
            args.Add("128k");
            args.Add("-ar");
            args.Add("48000");
            args.Add("-ac");
            args.Add("2");
        }

        args.Add(path);

        var result = await _runner.RunAsync(
            "ffmpeg",
            args,
            TimeSpan.FromMinutes(5),
            CancellationToken.None);
        Assert.False(result.TimedOut);
        Assert.Equal(0, result.ExitCode);
        return path;
    }

    private RenderResult CreateResult(string videoPath, long durationMs, string srt)
    {
        var video = _store.AddFile(videoPath, AssetKind.Video, "video/mp4", durationMs);
        var thumbnailPath = Path.Combine(_root, Guid.NewGuid().ToString("N") + ".jpg");
        File.WriteAllBytes(thumbnailPath, [0xFF, 0xD8, 0xFF, 0xE0, 0, 0]);
        var thumbnail = _store.AddFile(thumbnailPath, AssetKind.Thumbnail, "image/jpeg");
        var subtitlePath = Path.Combine(_root, Guid.NewGuid().ToString("N") + ".srt");
        File.WriteAllText(subtitlePath, srt, new UTF8Encoding(false));
        var subtitle = _store.AddFile(
            subtitlePath,
            AssetKind.Subtitle,
            "application/x-subrip");

        return new RenderResult(
            video,
            thumbnail,
            subtitle,
            new string('a', 64),
            durationMs);
    }

    private static string DefaultSrt(int durationSeconds = 16)
    {
        var last = durationSeconds - 1;
        return $"""
            1
            00:00:00,000 --> 00:00:05,000
            ข้อความหนึ่ง

            2
            00:00:05,000 --> 00:00:11,000
            ข้อความสอง

            3
            00:00:11,000 --> 00:00:{last:00},000
            ข้อความสาม
            """;
    }

    private static string Describe(ValidationReport report)
        => string.Join(
            "; ",
            report.Errors.Select(issue => $"{issue.Code}: {issue.Message}"));

    private sealed class TempAssetStore : IAssetStore
    {
        private readonly string _root;
        private readonly Dictionary<Guid, string> _paths = [];

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
    }
}
