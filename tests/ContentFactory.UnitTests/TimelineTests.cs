using System.Globalization;
using System.Text;
using ContentFactory.Contracts;
using ContentFactory.Core.Abstractions;
using ContentFactory.Core.Exceptions;
using ContentFactory.Infrastructure.Media;
using ContentFactory.Infrastructure.Storage;

namespace ContentFactory.UnitTests;

/// <summary>
/// T11 timeline and caption tests. Thai text is split by grapheme cluster, never by bytes or
/// a byte count; ASS control text is escaped before it reaches libass; captions that cannot
/// fit two safe lines block rendering instead of being silently shrunk or dropped.
/// </summary>
public sealed class TimelineTests
{
    private static readonly Guid RevisionId =
        Guid.Parse("22222222-2222-2222-2222-222222222222");

    private static readonly Guid VideoVersionId =
        Guid.Parse("33333333-3333-3333-3333-333333333333");

    [Fact]
    public void TimelineDurationUsesFrameGrid()
    {
        var script = CreateScript(
            ("hook", "ตะขอ", "ตะขอ"),
            ("benefit", "ประโยชน์", "ประโยชน์"),
            ("cta", "สั่งซื้อ", "สั่งซื้อ"));
        var voices = new[]
        {
            CreateVoice("scene-1", 5_000),
            CreateVoice("scene-2", 5_000),
            CreateVoice("scene-3", 5_000)
        };
        var visuals = CreateVisuals(script);

        var manifest = new TimelineCompiler().Compile(
            VideoVersionId, script, visuals, voices, ProductBasicTemplate.Version);

        Assert.Equal(1080, manifest.Width);
        Assert.Equal(1920, manifest.Height);
        Assert.Equal(30, manifest.Fps);
        Assert.Equal(0, manifest.Segments[0].StartMs);

        var startFrame = 0L;
        foreach (var segment in manifest.Segments)
        {
            var expectedStart = FrameGrid.StartMs(startFrame, manifest.Fps);
            startFrame += FrameGrid.RoundUpFrames(segment.DurationMs, manifest.Fps);
            var expectedEnd = FrameGrid.StartMs(startFrame, manifest.Fps);

            Assert.Equal(expectedStart, segment.StartMs);
            Assert.Equal(expectedEnd - expectedStart, segment.DurationMs);
        }

        var totalMs = manifest.Segments.Sum(segment => segment.DurationMs);
        Assert.InRange(totalMs, 15_000, 35_000);
        Assert.Equal(
            FrameGrid.StartMs(
                manifest.Segments.Sum(segment => FrameGrid.RoundUpFrames(segment.DurationMs, manifest.Fps)),
                manifest.Fps),
            totalMs);
    }

    [Fact]
    public void CompileMapsScenesVoicesCaptionsAndCta()
    {
        var script = CreateScript(
            ("hook", "เปิดตัวสินค้า", "เปิดตัวสินค้า"),
            ("benefit", "ใช้งานง่ายทุกวัน", "ใช้งานง่ายทุกวัน"),
            ("cta", "สั่งซื้อวันนี้", "สั่งซื้อวันนี้"));
        var voices = new[]
        {
            CreateVoice("scene-1", 5_000),
            CreateVoice("scene-2", 5_000),
            CreateVoice("scene-3", 5_000)
        };

        var manifest = new TimelineCompiler().Compile(
            VideoVersionId, script, CreateVisuals(script), voices, ProductBasicTemplate.Version);

        Assert.Equal(ProductBasicTemplate.Version, manifest.TemplateVersion);
        Assert.Equal(ProductBasicTemplate.RendererVersion, manifest.RendererVersion);
        Assert.Equal(ProductBasicTemplate.FontFingerprint, manifest.FontFingerprint);
        Assert.Matches("^[0-9a-f]{64}$", manifest.ScriptSha256);
        Assert.Equal(3, manifest.Segments.Count);
        Assert.Equal(script.Scenes.Select(scene => scene.SceneId),
            manifest.Segments.Select(segment => segment.SceneId));
        Assert.All(manifest.Segments, segment => Assert.NotNull(segment.Audio));
        Assert.Equal("cta:zoom", manifest.Segments[^1].Transform);
        Assert.Equal("เปิดตัวสินค้า", manifest.Segments[0].Caption);
    }

    [Fact]
    public void MissingVisualBlocksCompile()
    {
        var script = CreateScript(
            ("hook", "ตะขอ", "ตะขอ"),
            ("benefit", "ประโยชน์", "ประโยชน์"),
            ("cta", "สั่งซื้อ", "สั่งซื้อ"));
        var visuals = CreateVisuals(script);
        visuals.Remove("scene-2");

        var exception = Assert.Throws<ValidationException>(() =>
            new TimelineCompiler().Compile(
                VideoVersionId,
                script,
                visuals,
                new[]
                {
                    CreateVoice("scene-1", 5_000),
                    CreateVoice("scene-2", 5_000),
                    CreateVoice("scene-3", 5_000)
                },
                ProductBasicTemplate.Version));

        Assert.Equal("TIMELINE_VISUAL_REQUIRED", exception.Code);
    }

    [Fact]
    public void MissingVoiceBlocksCompile()
    {
        var script = CreateScript(
            ("hook", "ตะขอ", "ตะขอ"),
            ("benefit", "ประโยชน์", "ประโยชน์"),
            ("cta", "สั่งซื้อ", "สั่งซื้อ"));

        var exception = Assert.Throws<ValidationException>(() =>
            new TimelineCompiler().Compile(
                VideoVersionId,
                script,
                CreateVisuals(script),
                new[]
                {
                    CreateVoice("scene-1", 5_000),
                    CreateVoice("scene-3", 5_000)
                },
                ProductBasicTemplate.Version));

        Assert.Equal("TIMELINE_VOICE_REQUIRED", exception.Code);
    }

    [Fact]
    public void ThaiMarksStayWithBaseCharacter()
    {
        var layout = new CaptionLayout(
            new FixedWidthMeasurer(1d),
            new CaptionLayoutOptions(MaxWidthPx: 2, MaxLines: 8));

        var result = layout.Layout("ก\u0E49ำ\u0E49น\u0E49");

        Assert.True(result.Passed);
        Assert.NotEmpty(result.Lines);
        Assert.All(
            result.Lines,
            line => Assert.False(
                IsCombiningMark(StringInfo.GetNextTextElement(line)),
                $"A line began with a combining mark: {line}"));
        Assert.Equal(
            "ก\u0E49ำ\u0E49น\u0E49",
            string.Concat(result.Lines).Replace("\\N", string.Empty, StringComparison.Ordinal));
    }

    [Fact]
    public void AssOverrideTextRenderedLiterally()
    {
        const string text = "{\\an8}ราคา 199 บาท";

        var escaped = CaptionLayout.EscapeAssText(text);

        Assert.DoesNotContain("{\\an8}", escaped, StringComparison.Ordinal);
        Assert.Contains("\\{", escaped, StringComparison.Ordinal);
        Assert.Contains("\\}", escaped, StringComparison.Ordinal);
        Assert.Contains("199", escaped, StringComparison.Ordinal);

        var layout = new CaptionLayout(
            new FixedWidthMeasurer(1d),
            new CaptionLayoutOptions(MaxWidthPx: 800, MaxLines: 2));
        var result = layout.Layout(text);
        Assert.True(result.Passed);
        Assert.DoesNotContain("{\\an8}", result.AssText, StringComparison.Ordinal);
        Assert.Contains("\\{", result.AssText, StringComparison.Ordinal);
    }

    [Fact]
    public async Task CaptionOverflowBlocksRender()
    {
        var manifest = CreateManifest(
            "ข้อความยาวมากไม่ควรถูกย่อจนอ่านไม่ออกและไม่ควรถูกปล่อยผ่านไปยังวิดีโอ");
        var layout = new CaptionLayout(
            new FixedWidthMeasurer(1d),
            new CaptionLayoutOptions(MaxWidthPx: 4, MaxLines: 2, FontSize: 56));
        var report = layout.Layout(manifest.Segments[0].Caption);

        Assert.False(report.Passed);
        Assert.Contains(report.Issues, issue => issue.Code == "CAPTION_OVERFLOW");

        var runner = new FakeProcessRunner();
        var renderer = CreateRenderer(layout, runner);
        var exception = await Assert.ThrowsAsync<ValidationException>(
            () => renderer.RenderAsync(manifest, CancellationToken.None));

        Assert.Equal("CAPTION_OVERFLOW", exception.Code);
        Assert.Empty(runner.Calls);
    }

    [Fact]
    public void NarrationNeverCutMidWord()
    {
        const string narration =
            "ผลิตภัณฑ์นี้ช่วยให้ผิวชุ่มชื้นตลอดวันโดยไม่ทำให้รู้สึกเหนียวเหนอะหนะ";
        var phrases = VoicePhraseSplitter.Split(narration, 20);

        Assert.NotEmpty(phrases);
        Assert.Equal(
            narration.Replace(" ", string.Empty, StringComparison.Ordinal),
            string.Concat(phrases).Replace(" ", string.Empty, StringComparison.Ordinal));
        Assert.All(
            phrases,
            phrase => Assert.False(
                IsCombiningMark(StringInfo.GetNextTextElement(phrase)),
                $"A phrase began with a combining mark: {phrase}"));

        var script = CreateScript(
            ("hook", narration, narration),
            ("benefit", "ประโยชน์", "ประโยชน์"),
            ("cta", "สั่งซื้อ", "สั่งซื้อ"));
        var firstVoice = CreateVoice("scene-1", 15_000);
        var manifest = new TimelineCompiler().Compile(
            VideoVersionId,
            script,
            CreateVisuals(script),
            new[]
            {
                firstVoice,
                CreateVoice("scene-2", 5_000),
                CreateVoice("scene-3", 5_000)
            },
            ProductBasicTemplate.Version);

        Assert.Equal(narration, script.Scenes[0].Narration);
        Assert.True(manifest.Segments[0].DurationMs >= 15_100);
        Assert.Equal(firstVoice.Audio.Id, manifest.Segments[0].Audio!.Id);
    }

    [Fact]
    public async Task MissingHardwareEncoderFallsBackExplicitly()
    {
        var runner = new FakeProcessRunner
        {
            Handler = (executable, args) =>
            {
                if (args.Contains("-encoders"))
                {
                    return Success(
                        """
                         V..... h264_videotoolbox    VideoToolbox H.264 Encoder
                         V..... libx264              libx264 H.264 encoder
                        """);
                }

                if (args.Contains("-filters"))
                {
                    return Success(
                        """
                         T.. subtitles    V->V       Render text subtitles onto input video
                         T.. zoompan      V->V       Apply Zoom & Pan effect
                         T.. concat       N->N       Concatenate audio and video streams
                        """);
                }

                if (args.Contains("-c:v") && args.Contains("h264_videotoolbox"))
                {
                    return Failure("hardware encode failed");
                }

                if (args.Contains("-f") && args.Contains("null"))
                {
                    return Success(string.Empty);
                }

                return Failure("unexpected command");
            }
        };
        var probe = CreateProbe(runner);

        var selection = await probe.ProbeAsync(CancellationToken.None);

        Assert.Equal("libx264", selection.VideoEncoder);
        Assert.Equal(4, selection.Threads);
        Assert.False(selection.HardwareAccelerated);
        Assert.Contains("fallback", selection.Reason, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task MissingEncoderFailsClosed()
    {
        var runner = new FakeProcessRunner
        {
            Handler = (_, args) => args.Contains("-encoders")
                ? Success(" V..... h264_videotoolbox VideoToolbox H.264 Encoder")
                : Failure("unexpected command")
        };
        var probe = CreateProbe(runner);

        var exception = await Assert.ThrowsAsync<ValidationException>(
            () => probe.ProbeAsync(CancellationToken.None));

        Assert.Equal("RENDER_ENCODER_UNAVAILABLE", exception.Code);
    }

    [Fact]
    public async Task MissingSubtitlesFilterFailsClosed()
    {
        var runner = new FakeProcessRunner
        {
            Handler = (_, args) =>
            {
                if (args.Contains("-encoders"))
                {
                    return Success(" V..... libx264 libx264 H.264 encoder");
                }

                if (args.Contains("-filters"))
                {
                    return Success(" T.. zoompan V->V Apply Zoom & Pan effect");
                }

                return Success(string.Empty);
            }
        };
        var probe = CreateProbe(runner);

        var exception = await Assert.ThrowsAsync<ValidationException>(
            () => probe.ProbeAsync(CancellationToken.None));

        Assert.Equal("RENDER_SUBTITLES_UNAVAILABLE", exception.Code);
    }

    [Fact]
    public async Task MissingThaiFontFailsClosed()
    {
        var runner = new FakeProcessRunner
        {
            Handler = (_, args) =>
            {
                if (args.Contains("-encoders"))
                {
                    return Success(" V..... libx264 libx264 H.264 encoder");
                }

                if (args.Contains("-filters"))
                {
                    return Success(
                        """
                         T.. subtitles V->V Render text subtitles
                         T.. zoompan V->V Apply Zoom & Pan effect
                         T.. concat N->N Concatenate streams
                        """);
                }

                if (args.Contains("-vf"))
                {
                    return Failure("font not found");
                }

                return Failure("unexpected command");
            }
        };
        var probe = CreateProbe(runner);

        var exception = await Assert.ThrowsAsync<ValidationException>(
            () => probe.ProbeAsync(CancellationToken.None));

        Assert.Equal("RENDER_FONT_UNAVAILABLE", exception.Code);
    }

    [Fact]
    public void FfmpegCommandUsesNativeVerticalContract()
    {
        var manifest = CreateManifest("ข้อความทดสอบ");
        var args = FfmpegRenderCommand.Build(
            manifest,
            ["/tmp/visual-0.png"],
            [null],
            "/tmp/captions.ass",
            "/tmp/output.mp4",
            new EncoderSelection(
                "libx264",
                4,
                HardwareAccelerated: false,
                "CPU fallback",
                ProductBasicTemplate.FontFingerprint),
            new FfmpegRenderOptions());

        Assert.Contains("-movflags", args);
        Assert.Contains("+faststart", args);
        Assert.Contains("-pix_fmt", args);
        Assert.Contains("yuv420p", args);
        Assert.Contains("-c:v", args);
        Assert.Contains("libx264", args);
        Assert.Contains("-threads", args);
        Assert.Contains("4", args);
        Assert.Contains("-c:a", args);
        Assert.Contains("aac", args);
        Assert.Contains("-ar", args);
        Assert.Contains("48000", args);
        Assert.Contains("-ac", args);
        Assert.Contains("2", args);
        Assert.Contains("-r", args);
        Assert.Contains("30", args);

        var filter = args[args.IndexOf("-filter_complex") + 1];
        Assert.Contains("scale=1080:1920", filter, StringComparison.Ordinal);
        Assert.Contains("pad=1080:1920", filter, StringComparison.Ordinal);
        Assert.Contains("concat=n=1:v=1:a=0", filter, StringComparison.Ordinal);
        Assert.Contains("subtitles=filename=", filter, StringComparison.Ordinal);
        Assert.DoesNotContain("ข้อความทดสอบ", filter, StringComparison.Ordinal);
    }

    private static EncoderProbe CreateProbe(FakeProcessRunner runner)
        => new(
            runner,
            StorageOptions.Create(Path.Combine(
                Path.GetTempPath(),
                "cf-timeline-tests",
                Guid.NewGuid().ToString("N"))),
            new EncoderProbeOptions());

    private static FfmpegVideoRenderer CreateRenderer(
        CaptionLayout layout,
        FakeProcessRunner runner)
    {
        var root = Path.Combine(
            Path.GetTempPath(),
            "cf-timeline-tests",
            Guid.NewGuid().ToString("N"));
        var storage = StorageOptions.Create(root);
        return new FfmpegVideoRenderer(
            new EmptyAssetStore(),
            storage,
            runner,
            CreateProbe(runner),
            layout,
            new FfmpegRenderOptions());
    }

    private static RenderManifest CreateManifest(
        string caption,
        long durationMs = 15_000)
    {
        var visual = CreateAsset("visual", AssetKind.Image, "image/png", 0);
        var audio = CreateAsset("audio", AssetKind.Audio, "audio/wav", 1_000);
        return new RenderManifest(
            1,
            VideoVersionId,
            new string('a', 64),
            ProductBasicTemplate.Version,
            ProductBasicTemplate.RendererVersion,
            ProductBasicTemplate.FontFingerprint,
            ProductBasicTemplate.Width,
            ProductBasicTemplate.Height,
            ProductBasicTemplate.Fps,
            [
                new TimelineSegment(
                    "scene-1",
                    0,
                    durationMs,
                    visual,
                    audio,
                    caption,
                    "still")
            ]);
    }

    private static ScriptDocument CreateScript(
        params (string Role, string Narration, string Caption)[] scenes)
    {
        var list = scenes
            .Select((scene, index) => new ScriptScene(
                $"scene-{index + 1}",
                scene.Role,
                scene.Narration,
                scene.Caption,
                [],
                null,
                null))
            .ToArray();
        return new ScriptDocument(
            1,
            RevisionId,
            "th-TH",
            "สินค้า",
            "โฆษณา",
            "คำบรรยาย",
            list);
    }

    private static Dictionary<string, AssetRef> CreateVisuals(ScriptDocument script)
        => script.Scenes.ToDictionary(
            scene => scene.SceneId,
            scene => CreateAsset(scene.SceneId, AssetKind.Image, "image/png", 0),
            StringComparer.Ordinal);

    private static VoiceSegment CreateVoice(string sceneId, long durationMs)
        => new(
            sceneId,
            CreateAsset("voice-" + sceneId, AssetKind.Audio, "audio/wav", durationMs),
            durationMs);

    private static AssetRef CreateAsset(
        string label,
        AssetKind kind,
        string mime,
        long durationMs)
    {
        var hash = Convert.ToHexString(
                System.Security.Cryptography.SHA256.HashData(
                    Encoding.UTF8.GetBytes(label)))
            .ToLowerInvariant();
        return new AssetRef(
            Guid.NewGuid(),
            hash,
            $"assets/sha256/{hash[..2]}/{hash}",
            kind,
            mime,
            1,
            durationMs == 0 ? null : durationMs);
    }

    private static bool IsCombiningMark(string element)
    {
        if (element.Length == 0)
        {
            return false;
        }

        var rune = Rune.GetRuneAt(element, 0);
        return Rune.GetUnicodeCategory(rune) is
                UnicodeCategory.NonSpacingMark or
                UnicodeCategory.SpacingCombiningMark or
                UnicodeCategory.EnclosingMark ||
            rune.Value is >= 0x0E31 and <= 0x0E3A or
            >= 0x0E47 and <= 0x0E4E;
    }

    private static ProcessResult Success(string output)
        => new(0, output, string.Empty, false, false, TimeSpan.Zero);

    private static ProcessResult Failure(string error)
        => new(1, string.Empty, error, false, false, TimeSpan.Zero);

    private sealed class FixedWidthMeasurer : ITextWidthMeasurer
    {
        private readonly double _width;

        public FixedWidthMeasurer(double width)
        {
            _width = width;
        }

        public double Measure(string text)
            => string.IsNullOrEmpty(text) ? 0 : _width * StringInfo.ParseCombiningCharacters(text).Length;
    }

    private sealed class FakeProcessRunner : IProcessRunner
    {
        public Func<string, IReadOnlyList<string>, ProcessResult> Handler { get; set; } =
            (_, _) => Failure("no handler");

        public List<(string Executable, IReadOnlyList<string> Args)> Calls { get; } = [];

        public Task<ProcessResult> RunAsync(
            string executable,
            IReadOnlyList<string> args,
            TimeSpan timeout,
            CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            Calls.Add((executable, args));
            return Task.FromResult(Handler(executable, args));
        }
    }

    private sealed class EmptyAssetStore : IAssetStore
    {
        public Task<AssetRef> PutAsync(
            Stream input,
            AssetKind kind,
            string mime,
            CancellationToken ct)
            => throw new InvalidOperationException("No asset store writes expected.");

        public Task<Stream> OpenReadAsync(Guid assetId, CancellationToken ct)
            => throw new InvalidOperationException("No asset reads expected.");
    }
}
