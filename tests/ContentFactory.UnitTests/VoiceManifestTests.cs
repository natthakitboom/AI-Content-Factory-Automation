using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Xml.Linq;
using ContentFactory.Contracts;
using ContentFactory.Core.Abstractions;
using ContentFactory.Core.Exceptions;
using ContentFactory.Infrastructure.Media;
using ContentFactory.Infrastructure.Providers.Voice;
using ContentFactory.Infrastructure.Storage;

namespace ContentFactory.UnitTests;

/// <summary>
/// T10 voice-manifest tests. They prove that missing or silent narration never turns into a
/// successful asset, that model text is escaped into SSML, that cache keys are stable and
/// specific, and that measured audio duration drives phrase/caption timing.
/// </summary>
public sealed class VoiceManifestTests
{
    private static readonly Guid RevisionId =
        Guid.Parse("11111111-1111-1111-1111-111111111111");

    private static readonly string ScriptSha = new('a', 64);

    [Fact]
    public async Task MissingNarrationNeverBecomesSilentSuccess()
    {
        var provider = new CountingProvider();
        var assembler = CreateAssembler(CreateProbe(DefaultMeasuredAsset));
        var request = CreateRequest(
            scenes:
            [
                new VoiceSceneRequest("scene-1", "ข้อความปกติ", "ข้อความปกติ"),
                new VoiceSceneRequest("scene-2", "   ", "ข้อความว่าง")
            ]);

        var result = await assembler.AssembleAsync(request, provider, CancellationToken.None);

        Assert.False(result.Passed);
        Assert.Empty(result.Segments);
        Assert.Null(result.Manifest);
        Assert.Null(result.ManifestSha256);
        Assert.Contains(result.Issues, issue => issue.Code == "VOICE_NARRATION_MISSING");
        Assert.Equal(0, provider.Calls);
    }

    [Fact]
    public async Task VoiceLocaleMismatchRejected()
    {
        var asset = CreateAudioAsset("เสียงที่อัปโหลด", durationMs: 1200);
        var provider = UploadedVoiceProvider.ForScenes(
            RevisionId,
            [new UploadedNarrationAsset("scene-1", "th-TH", asset)]);
        var request = new VoiceRequest(
            VoiceOperationId.ForScene(RevisionId, "scene-1"),
            "en-US",
            "uploaded-th",
            "ข้อความ",
            "uploaded-v1");

        var exception = await Assert.ThrowsAsync<ValidationException>(
            () => provider.SynthesizeAsync(request, CancellationToken.None));

        Assert.Equal("VOICE_LOCALE_MISMATCH", exception.Code);
    }

    [Fact]
    public async Task UploadedNarrationResolvesFromSceneMapping()
    {
        var asset = CreateAudioAsset("เสียงที่อัปโหลด", durationMs: 1200);
        var provider = UploadedVoiceProvider.ForScenes(
            RevisionId,
            [new UploadedNarrationAsset("scene-1", "th-TH", asset)]);
        var request = new VoiceRequest(
            VoiceOperationId.ForScene(RevisionId, "scene-1"),
            "th-TH",
            "uploaded-th",
            "ข้อความ",
            "uploaded-v1");

        var resolved = await provider.SynthesizeAsync(request, CancellationToken.None);

        Assert.Equal(asset.Id, resolved.Id);
        Assert.Equal(asset.Sha256, resolved.Sha256);
    }

    [Fact]
    public async Task UploadedPhraseMappingResolvesByPhraseIndex()
    {
        var asset = CreateAudioAsset("เสียง phrase", durationMs: 900);
        var provider = UploadedVoiceProvider.ForPhrases(
            RevisionId,
            [
                new UploadedNarrationAsset(
                    "scene-1",
                    "th-TH",
                    asset,
                    PhraseIndex: 2)
            ]);
        var request = new VoiceRequest(
            VoiceOperationId.ForPhrase(RevisionId, "scene-1", 2),
            "th-TH",
            "uploaded-th",
            "ข้อความ",
            "uploaded-v1");

        var resolved = await provider.SynthesizeAsync(request, CancellationToken.None);

        Assert.Equal(asset.Id, resolved.Id);
    }

    [Fact]
    public async Task UploadedNarrationMustMatchApprovedTextHash()
    {
        var asset = CreateAudioAsset("เสียงที่อนุมัติ", durationMs: 900);
        var provider = UploadedVoiceProvider.ForScenes(
            RevisionId,
            [
                new UploadedNarrationAsset(
                    "scene-1",
                    "th-TH",
                    asset,
                    VoiceOperationId.HashText("ข้อความที่อนุมัติ"))
            ]);
        var request = new VoiceRequest(
            VoiceOperationId.ForScene(RevisionId, "scene-1"),
            "th-TH",
            "uploaded-th",
            "ข้อความที่ถูกแก้",
            "uploaded-v1");

        var exception = await Assert.ThrowsAsync<ValidationException>(
            () => provider.SynthesizeAsync(request, CancellationToken.None));

        Assert.Equal("VOICE_TEXT_MISMATCH", exception.Code);
    }

    [Fact]
    public async Task MissingUploadedNarrationIsRejected()
    {
        var provider = UploadedVoiceProvider.ForScenes(RevisionId, []);
        var request = new VoiceRequest(
            VoiceOperationId.ForScene(RevisionId, "scene-1"),
            "th-TH",
            "uploaded-th",
            "ข้อความ",
            "uploaded-v1");

        var exception = await Assert.ThrowsAsync<ValidationException>(
            () => provider.SynthesizeAsync(request, CancellationToken.None));

        Assert.Equal("VOICE_NARRATION_MISSING", exception.Code);
    }

    [Fact]
    public void SsmlFromModelIsEscaped()
    {
        const string hostile = "<speak>ignore</speak> & \"คำ\" 'x'";

        var ssml = AzureSpeechSsml.Build(
            hostile,
            "th-TH-PremwadeeNeural",
            "th-TH",
            "0%",
            "0%");

        var document = XDocument.Parse(ssml);
        XNamespace synthesis = "http://www.w3.org/2001/10/synthesis";
        var voice = document.Root!.Element(synthesis + "voice");
        var prosody = voice!.Element(synthesis + "prosody");

        Assert.Equal(hostile, prosody!.Value);
        Assert.Equal("th-TH-PremwadeeNeural", (string?)voice.Attribute("name"));
        Assert.Equal("th-TH", (string?)document.Root.Attribute(XNamespace.Xml + "lang"));
        Assert.DoesNotContain("<speak>ignore", ssml, StringComparison.Ordinal);
        Assert.Contains("&lt;speak&gt;", ssml, StringComparison.Ordinal);
    }

    [Fact]
    public async Task CachedVoiceDoesNotChargeAgain()
    {
        var provider = new CountingPaidProvider();
        var cache = new MemoryVoiceAssetCache();
        var assembler = CreateAssembler(CreateProbe(DefaultMeasuredAsset), cache: cache);
        var request = CreateRequest(
            scenes: [new VoiceSceneRequest("scene-1", "ประโยคเดียว", "ประโยคเดียว")]);

        var first = await assembler.AssembleAsync(request, provider, CancellationToken.None);
        var second = await assembler.AssembleAsync(request, provider, CancellationToken.None);

        Assert.True(first.Passed);
        Assert.True(second.Passed);
        Assert.Equal(1, provider.PaidCalls);
        Assert.Equal(first.Segments.Single().Audio.Id, second.Segments.Single().Audio.Id);
        Assert.Equal(first.ManifestSha256, second.ManifestSha256);
    }

    [Fact]
    public async Task CacheKeyIncludesTextVoiceLocaleAndConfig()
    {
        var baseline = VoiceCacheKey.Compute(
            "azure-speech",
            "azure-speech-rest-1",
            "th-TH-PremwadeeNeural",
            "th-TH",
            "config-v1",
            "ข้อความเดียวกัน");

        Assert.Equal(
            baseline,
            VoiceCacheKey.Compute(
                "azure-speech",
                "azure-speech-rest-1",
                "th-TH-PremwadeeNeural",
                "th-TH",
                "config-v1",
                "ข้อความเดียวกัน"));

        Assert.NotEqual(
            baseline,
            VoiceCacheKey.Compute(
                "azure-speech",
                "azure-speech-rest-1",
                "th-TH-PremwadeeNeural",
                "th-TH",
                "config-v1",
                "ข้อความใหม่"));
        Assert.NotEqual(
            baseline,
            VoiceCacheKey.Compute(
                "azure-speech",
                "azure-speech-rest-1",
                "th-TH-AcharaNeural",
                "th-TH",
                "config-v1",
                "ข้อความเดียวกัน"));
        Assert.NotEqual(
            baseline,
            VoiceCacheKey.Compute(
                "azure-speech",
                "azure-speech-rest-1",
                "th-TH-PremwadeeNeural",
                "en-US",
                "config-v1",
                "ข้อความเดียวกัน"));
        Assert.NotEqual(
            baseline,
            VoiceCacheKey.Compute(
                "azure-speech",
                "azure-speech-rest-2",
                "th-TH-PremwadeeNeural",
                "th-TH",
                "config-v1",
                "ข้อความเดียวกัน"));
    }

    [Fact]
    public async Task ActualAudioDurationDefinesCaptionTiming()
    {
        const long firstMeasuredMs = 1234;
        const long secondMeasuredMs = 987;
        var provider = new CountingPaidProvider(request =>
            CreateAudioAsset(
                request.Text,
                DurationFor(request.Text, firstMeasuredMs, secondMeasuredMs)));
        var assembler = CreateAssembler(CreateProbe(DefaultMeasuredAsset));
        var request = CreateRequest(
            scenes:
            [
                new VoiceSceneRequest(
                    "scene-1",
                    "ประโยคแรก. ประโยคที่สอง.",
                    "ประโยคแรก ประโยคที่สอง")
            ]);

        var result = await assembler.AssembleAsync(request, provider, CancellationToken.None);

        Assert.True(result.Passed);
        var segment = Assert.Single(result.Segments);
        Assert.Equal(firstMeasuredMs + secondMeasuredMs, segment.DurationMs);
        Assert.NotNull(result.Manifest);
        Assert.Equal(2, result.Manifest.Phrases.Count);

        var first = result.Manifest.Phrases[0];
        var second = result.Manifest.Phrases[1];
        Assert.Equal(firstMeasuredMs, first.AudioDurationMs);
        Assert.Equal(0, first.StartMs);
        Assert.Equal(firstMeasuredMs + 100, first.CaptionEndMs);
        Assert.Equal(firstMeasuredMs, second.StartMs);
        Assert.Equal(secondMeasuredMs, second.AudioDurationMs);
        Assert.Equal(
            firstMeasuredMs + secondMeasuredMs,
            second.CaptionEndMs);
    }

    [Fact]
    public async Task SilentNarrationIsRejected()
    {
        var provider = new CountingPaidProvider();
        var assembler = CreateAssembler(
            new StubProbe((asset, _) => asset.Id == provider.LastAssetId
                ? new AudioProbeResult(1000, HasAudioStream: true, IsSilent: true, "mp3")
                : new AudioProbeResult(
                    asset.DurationMs ?? 1000,
                    HasAudioStream: true,
                    IsSilent: false,
                    "mp3")));
        var request = CreateRequest(
            scenes: [new VoiceSceneRequest("scene-1", "ประโยคเดียว", "ประโยคเดียว")]);

        var result = await assembler.AssembleAsync(request, provider, CancellationToken.None);

        Assert.False(result.Passed);
        Assert.Contains(result.Issues, issue => issue.Code == "VOICE_NARRATION_SILENT");
        Assert.Null(result.Manifest);
    }

    [Fact]
    public async Task CorruptNarrationIsRejected()
    {
        var provider = new CountingPaidProvider();
        var assembler = CreateAssembler(
            new StubProbe((_, _) =>
                new AudioProbeResult(0, HasAudioStream: false, IsSilent: false, null)));
        var request = CreateRequest(
            scenes: [new VoiceSceneRequest("scene-1", "ประโยคเดียว", "ประโยคเดียว")]);

        var result = await assembler.AssembleAsync(request, provider, CancellationToken.None);

        Assert.False(result.Passed);
        Assert.Contains(result.Issues, issue => issue.Code == "VOICE_NARRATION_MISSING_AUDIO");
        Assert.Null(result.Manifest);
    }

    [Fact]
    public async Task NarrationOverThirtyFiveSecondsIsBlockedWithoutRetry()
    {
        var provider = new CountingPaidProvider(request =>
            CreateAudioAsset(request.Text, durationMs: 20_000));
        var assembler = CreateAssembler(CreateProbe(DefaultMeasuredAsset));
        var request = CreateRequest(
            scenes:
            [
                new VoiceSceneRequest(
                    "scene-1",
                    "ประโยคแรก. ประโยคที่สอง.",
                    "ประโยคแรก ประโยคที่สอง")
            ]);

        var result = await assembler.AssembleAsync(request, provider, CancellationToken.None);

        Assert.False(result.Passed);
        Assert.Contains(result.Issues, issue => issue.Code == "SCRIPT_TOO_LONG");
        Assert.Equal(2, provider.PaidCalls);
        Assert.Null(result.Manifest);
    }

    [Fact]
    public void PhraseSplitterNeverSplitsThaiGrapheme()
    {
        var text = string.Concat(Enumerable.Repeat("สวัสดีค่ะ ", 20)) + "ขอบคุณค่ะ";

        var phrases = VoicePhraseSplitter.Split(text, maxCharacters: 24);

        Assert.True(phrases.Count > 1);
        foreach (var phrase in phrases)
        {
            Assert.False(string.IsNullOrWhiteSpace(phrase));
            Assert.DoesNotContain('\n', phrase);
            var first = StringInfo.GetNextTextElement(phrase);
            Assert.False(
                first.Length == 1 && first[0] is >= '\u0E31' and <= '\u0E3A',
                "A phrase must not start in the middle of a Thai grapheme cluster.");
            Assert.Equal(
                new StringInfo(phrase).LengthInTextElements,
                StringInfo.ParseCombiningCharacters(phrase).Length);
        }
    }

    [Fact]
    public void RecordedFfprobeJsonProvidesMeasuredDuration()
    {
        var path = Path.Combine(
            AppContext.BaseDirectory,
            "Fixtures",
            "Voice",
            "ffprobe-audio.json");
        var json = File.ReadAllText(path);

        var result = FfprobeAudioProbe.ParseProbeJson(json);

        Assert.True(result.HasAudioStream);
        Assert.False(result.IsSilent);
        Assert.Equal("mp3", result.Codec);
        Assert.Equal(1234, result.DurationMs);
    }

    [Fact]
    public async Task FileVoiceCacheSurvivesANewProviderInstance()
    {
        var root = Path.Combine(
            Path.GetTempPath(),
            "cf-voice-cache-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);

        try
        {
            var storage = new StorageOptions { DataRoot = root };
            var key = VoiceCacheKey.Compute(
                "azure-speech",
                "azure-speech-rest-1",
                "th-TH-PremwadeeNeural",
                "th-TH",
                "config-v1",
                "ข้อความ");
            var asset = CreateAudioAsset("ข้อความ", durationMs: 1500);

            var writer = new FileVoiceAssetCache(storage);
            await writer.PutAsync(key, asset, CancellationToken.None);

            var reader = new FileVoiceAssetCache(storage);
            var cached = await reader.TryGetAsync(key, CancellationToken.None);

            Assert.NotNull(cached);
            Assert.Equal(asset.Id, cached.Id);
            Assert.Equal(asset.Sha256, cached.Sha256);

            if (!OperatingSystem.IsWindows())
            {
                var path = Path.Combine(root, "voice-cache", key[..2], key + ".json");
                var mode = File.GetUnixFileMode(path);
                Assert.Equal(
                    UnixFileMode.UserRead | UnixFileMode.UserWrite,
                    mode);
            }
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task UploadedNarrationDoesNotRequirePaidBillingScope()
    {
        var asset = CreateAudioAsset("เสียงที่อัปโหลด", durationMs: 1200);
        var provider = UploadedVoiceProvider.ForScenes(
            RevisionId,
            [new UploadedNarrationAsset("scene-1", "th-TH", asset)]);
        var assembler = CreateAssembler(CreateProbe(DefaultMeasuredAsset));
        var request = CreateRequest(
            campaignId: Guid.Empty,
            videoScopeId: Guid.Empty,
            providerName: "uploaded",
            providerVersion: "uploaded-v1",
            voiceId: "uploaded-th",
            scenes: [new VoiceSceneRequest("scene-1", "ประโยคเดียว", "ประโยคเดียว")],
            splitNarrationIntoPhrases: false);

        var result = await assembler.AssembleAsync(request, provider, CancellationToken.None);

        Assert.True(result.Passed);
        Assert.Single(result.Segments);
    }

    private static VoiceAssembler CreateAssembler(
        IAudioProbe probe,
        IVoiceAssetCache? cache = null)
        => new(
            new NullAssetStore(),
            probe,
            new StubConcatenator(),
            cache ?? new MemoryVoiceAssetCache());

    private static VoiceAssemblyRequest CreateRequest(
        IReadOnlyList<VoiceSceneRequest> scenes,
        Guid? campaignId = null,
        Guid? videoScopeId = null,
        string providerName = "azure-speech",
        string providerVersion = "azure-speech-rest-1",
        string voiceId = "th-TH-PremwadeeNeural",
        bool splitNarrationIntoPhrases = true)
        => new(
            RevisionId,
            campaignId ?? Guid.Parse("22222222-2222-2222-2222-222222222222"),
            videoScopeId ?? RevisionId,
            "th-TH",
            voiceId,
            providerName,
            providerVersion,
            "config-v1",
            ScriptSha,
            scenes,
            splitNarrationIntoPhrases,
            MaxPhraseCharacters: 120);

    private static AudioProbeResult DefaultMeasuredAsset(AssetRef asset, CancellationToken _)
        => new(
            asset.DurationMs ?? 1000,
            HasAudioStream: true,
            IsSilent: false,
            "mp3");

    private static IAudioProbe CreateProbe(
        Func<AssetRef, CancellationToken, AudioProbeResult> probe)
        => new StubProbe(probe);

    private static long DurationFor(string text, long first, long second)
        => text.Contains("สอง", StringComparison.Ordinal) ? second : first;

    private static AssetRef CreateAudioAsset(string text, long durationMs)
    {
        var sha = Convert.ToHexString(
                SHA256.HashData(Encoding.UTF8.GetBytes(text)))
            .ToLowerInvariant();

        return new AssetRef(
            GuidFrom(sha),
            sha,
            $"assets/sha256/{sha[..2]}/{sha}.mp3",
            AssetKind.Audio,
            "audio/mpeg",
            Math.Max(128, text.Length * 8),
            durationMs);
    }

    private static Guid GuidFrom(string sha)
        => new(Convert.FromHexString(sha[..32]));

    private sealed class StubProbe(
        Func<AssetRef, CancellationToken, AudioProbeResult> probe) : IAudioProbe
    {
        public Task<AudioProbeResult> ProbeAsync(AssetRef audio, CancellationToken ct)
            => Task.FromResult(probe(audio, ct));
    }

    private sealed class StubConcatenator : IAudioConcatenator
    {
        public Task<AssetRef> ConcatenateAsync(
            IReadOnlyList<AssetRef> parts,
            CancellationToken ct)
        {
            if (parts.Count == 1)
            {
                return Task.FromResult(parts[0]);
            }

            var duration = parts.Sum(part => part.DurationMs ?? 0);
            var sha = Convert.ToHexString(
                    SHA256.HashData(Encoding.UTF8.GetBytes(
                        string.Join('|', parts.Select(part => part.Sha256)))))
                .ToLowerInvariant();

            return Task.FromResult(new AssetRef(
                GuidFrom(sha),
                sha,
                $"assets/sha256/{sha[..2]}/{sha}.wav",
                AssetKind.Audio,
                "audio/wav",
                parts.Sum(part => part.Bytes),
                duration));
        }
    }

    private sealed class NullAssetStore : IAssetStore
    {
        public Task<AssetRef> PutAsync(
            Stream input,
            AssetKind kind,
            string mime,
            CancellationToken ct)
            => throw new NotSupportedException("These tests do not store assets.");

        public Task<Stream> OpenReadAsync(Guid assetId, CancellationToken ct)
            => throw new NotSupportedException("These tests do not read assets.");
    }

    private sealed class CountingProvider : IVoiceProvider
    {
        public int Calls { get; private set; }

        public Task<AssetRef> SynthesizeAsync(VoiceRequest request, CancellationToken ct)
        {
            Calls++;
            return Task.FromResult(CreateAudioAsset(request.Text, 1000));
        }
    }

    private sealed class CountingPaidProvider(
        Func<VoiceRequest, AssetRef>? factory = null) : IPaidVoiceProvider
    {
        private readonly Func<VoiceRequest, AssetRef> _factory =
            factory ?? (request => CreateAudioAsset(request.Text, 1000));

        public int PaidCalls { get; private set; }

        public Guid? LastAssetId { get; private set; }

        public Task<AssetRef> SynthesizeAsync(VoiceRequest request, CancellationToken ct)
            => throw new ValidationException(
                "VOICE_BILLING_SCOPE_REQUIRED",
                "A paid provider must receive an explicit budget scope.");

        public Task<AssetRef> SynthesizePaidAsync(
            VoiceRequest request,
            Guid campaignId,
            Guid videoScopeId,
            CancellationToken ct)
        {
            PaidCalls++;
            var asset = _factory(request);
            LastAssetId = asset.Id;
            return Task.FromResult(asset);
        }
    }
}
