using System.Text;
using ContentFactory.Contracts;
using ContentFactory.Core.Abstractions;
using ContentFactory.Core.Exceptions;
using ContentFactory.Infrastructure.Media;
using ContentFactory.Infrastructure.Storage;

namespace ContentFactory.UnitTests;

/// <summary>
/// Fast QA decision tests with a recorded FFprobe/FFmpeg boundary. They prove report
/// classification and warning handling without claiming that native media was decoded;
/// the real-media suite remains in ContentFactory.MediaTests.
/// </summary>
public sealed class TechnicalQaLogicTests
{
    private static readonly string Hash = new('a', 64);

    [Fact]
    public async Task ValidMetadataPasses()
    {
        var report = await RunAsync(ProbeJson());

        Assert.True(report.Passed);
        Assert.Empty(report.Errors);
    }

    [Fact]
    public async Task CorruptProbeResultIsRejected()
    {
        var report = await RunAsync(string.Empty, probeExitCode: 1);

        Assert.False(report.Passed);
        Assert.Contains(report.Errors, issue => issue.Code == "QA_VIDEO_CORRUPT");
    }

    [Fact]
    public async Task MissingAudioMetadataIsRejected()
    {
        var report = await RunAsync(ProbeJson(includeAudio: false));

        Assert.False(report.Passed);
        Assert.Contains(report.Errors, issue => issue.Code == "QA_AUDIO_MISSING");
    }

    [Fact]
    public async Task WrongDimensionMetadataIsRejected()
    {
        var report = await RunAsync(ProbeJson(width: 720, height: 1280));

        Assert.False(report.Passed);
        Assert.Contains(report.Errors, issue => issue.Code == "QA_DIMENSIONS_INVALID");
    }

    [Fact]
    public async Task DurationBoundsAreEnforced()
    {
        var report = await RunAsync(ProbeJson(durationSeconds: 10));

        Assert.False(report.Passed);
        Assert.Contains(
            report.Errors,
            issue => issue.Code == "QA_DURATION_OUT_OF_BOUNDS");
    }

    [Fact]
    public async Task SilentAudioIsRejected()
    {
        var report = await RunAsync(ProbeJson(), meanVolumeDb: -60);

        Assert.False(report.Passed);
        Assert.Contains(report.Errors, issue => issue.Code == "QA_AUDIO_SILENT");
    }

    [Fact]
    public async Task BlackEndCardIsWarningOnly()
    {
        var report = await RunAsync(
            ProbeJson(),
            blackDetect: "[blackdetect @ 0x1] black_start:14.5 black_end:16 black_duration:1.5");

        Assert.True(report.Passed);
        Assert.DoesNotContain(report.Errors, issue => issue.Code.StartsWith("QA_BLACK", StringComparison.Ordinal));
    }

    [Fact]
    public async Task SubtitleOverflowIsRejected()
    {
        var srt = """
            1
            00:00:00,000 --> 00:00:16,000
            บรรทัดหนึ่งที่ยาวมาก
            บรรทัดสองที่ยาวมาก
            บรรทัดสามที่ยาวมาก
            """;

        var report = await RunAsync(ProbeJson(), srt: srt);

        Assert.False(report.Passed);
        Assert.Contains(report.Errors, issue => issue.Code == "QA_SUBTITLE_OVERFLOW");
    }

    private static async Task<ValidationReport> RunAsync(
        string probeJson,
        int probeExitCode = 0,
        double meanVolumeDb = -12,
        string blackDetect = "",
        bool includeAudio = true,
        string? srt = null)
    {
        var root = Path.Combine(
            Path.GetTempPath(),
            "cf-qa-logic",
            Guid.NewGuid().ToString("N"));
        var storage = StorageOptions.Create(root);
        var store = new MemoryAssetStore();
        var video = store.Add(
            AssetKind.Video,
            "video/mp4",
            [0, 0, 0, 0x18, 0x66, 0x74, 0x79, 0x70, 0x6D, 0x70, 0x34, 0x32]);
        var thumbnail = store.Add(
            AssetKind.Thumbnail,
            "image/jpeg",
            [0xFF, 0xD8, 0xFF, 0xE0, 0, 0]);
        var subtitleBytes = Encoding.UTF8.GetBytes(srt ?? DefaultSrt());
        var subtitle = store.Add(AssetKind.Subtitle, "application/x-subrip", subtitleBytes);
        var runner = new FakeQaRunner(
            probeJson,
            probeExitCode,
            meanVolumeDb,
            blackDetect,
            includeAudio);
        var qa = new TechnicalQa(store, storage, runner, new TechnicalQaOptions());
        var durationMs = probeJson.Contains("\"duration\":\"16", StringComparison.Ordinal)
            ? 16_000
            : 10_000;

        var report = await qa.CheckAsync(
            new RenderResult(video, thumbnail, subtitle, Hash, durationMs),
            CancellationToken.None);

        return report;
    }

    private static string DefaultSrt()
        => """
            1
            00:00:00,000 --> 00:00:05,000
            ข้อความหนึ่ง

            2
            00:00:05,000 --> 00:00:11,000
            ข้อความสอง

            3
            00:00:11,000 --> 00:00:16,000
            ข้อความสาม
            """;

    private static string ProbeJson(
        int width = 1080,
        int height = 1920,
        double durationSeconds = 16,
        bool includeAudio = true)
    {
        var audio = includeAudio
            ? $$"""
              ,{"index":1,"codec_type":"audio","codec_name":"aac","sample_rate":"48000","channels":2,"duration":"{{durationSeconds:0.######}}"}
              """
            : string.Empty;
        var duration = durationSeconds.ToString("0.######", System.Globalization.CultureInfo.InvariantCulture);
        return $$"""
            {
              "streams": [
                {"index":0,"codec_type":"video","codec_name":"h264","width":{{width}},"height":{{height}},"pix_fmt":"yuv420p","r_frame_rate":"30/1","duration":"{{duration}}"}
                {{audio}}
              ],
              "format": {"duration":"{{duration}}","format_name":"mov,mp4,m4a,3gp,3g2,mj2"}
            }
            """;
    }

    private sealed class FakeQaRunner(
        string probeJson,
        int probeExitCode,
        double meanVolumeDb,
        string blackDetect,
        bool includeAudio) : IProcessRunner
    {
        public Task<ProcessResult> RunAsync(
            string executable,
            IReadOnlyList<string> args,
            TimeSpan timeout,
            CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            if (string.Equals(executable, "ffprobe", StringComparison.Ordinal))
            {
                return Task.FromResult(
                    new ProcessResult(probeExitCode, probeJson, string.Empty, false, false, TimeSpan.Zero));
            }

            if (args.Any(arg => arg.Contains("volumedetect", StringComparison.Ordinal)))
            {
                var stderr = includeAudio
                    ? $"mean_volume: {meanVolumeDb:0.##} dB"
                    : string.Empty;
                return Task.FromResult(
                    new ProcessResult(0, string.Empty, stderr, false, false, TimeSpan.Zero));
            }

            if (args.Any(arg => arg.Contains("blackdetect", StringComparison.Ordinal)))
            {
                return Task.FromResult(
                    new ProcessResult(0, string.Empty, blackDetect, false, false, TimeSpan.Zero));
            }

            return Task.FromResult(
                new ProcessResult(0, string.Empty, string.Empty, false, false, TimeSpan.Zero));
        }
    }

    private sealed class MemoryAssetStore : IAssetStore
    {
        private readonly Dictionary<Guid, (AssetRef Asset, byte[] Bytes)> _assets = [];

        public AssetRef Add(AssetKind kind, string mime, byte[] bytes)
        {
            var id = Guid.NewGuid();
            var hash = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(bytes))
                .ToLowerInvariant();
            var asset = new AssetRef(
                id,
                hash,
                $"assets/sha256/{hash[..2]}/{hash}",
                kind,
                mime,
                bytes.Length,
                kind == AssetKind.Audio ? 1_000 : null);
            _assets[id] = (asset, bytes);
            return asset;
        }

        public Task<AssetRef> PutAsync(Stream input, AssetKind kind, string mime, CancellationToken ct)
        {
            using var memory = new MemoryStream();
            input.CopyTo(memory);
            return Task.FromResult(Add(kind, mime, memory.ToArray()));
        }

        public Task<Stream> OpenReadAsync(Guid assetId, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            return Task.FromResult<Stream>(new MemoryStream(_assets[assetId].Bytes, writable: false));
        }
    }
}
