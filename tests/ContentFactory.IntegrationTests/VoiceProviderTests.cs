using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using ContentFactory.Contracts;
using ContentFactory.Core.Abstractions;
using ContentFactory.Core.Exceptions;
using ContentFactory.Infrastructure.Billing;
using ContentFactory.Infrastructure.Providers.Voice;

namespace ContentFactory.IntegrationTests;

/// <summary>
/// T10 Azure REST adapter tests. All HTTP is a recorded/fake handler; no live Azure call is
/// made. The tests prove the voice allow-list, locale matching, XML-writer SSML escaping,
/// price gate, reservation lifecycle and fail-closed handling of uncertain submissions.
/// </summary>
public sealed class VoiceProviderTests
{
    private static readonly Uri Endpoint = new("https://speech.test.local");
    private static readonly Guid CampaignId =
        Guid.Parse("33333333-3333-3333-3333-333333333333");
    private static readonly Guid VideoScopeId =
        Guid.Parse("44444444-4444-4444-4444-444444444444");

    [Fact]
    public async Task FetchesAllowedVoicesAndSynthesizesWithEscapedSsml()
    {
        var handler = new RecordingHandler((request, _) =>
            request.Method == HttpMethod.Get
                ? Json(VoiceListJson())
                : Audio(CreateWav(120)));
        using var http = new HttpClient(handler, disposeHandler: false);
        var budget = new RecordingBudgetGuard();
        var price = new StubPriceQuoter();
        var assets = new RecordingAssetStore();
        var provider = CreateProvider(http, budget, price, assets);
        var request = CreateRequest(
            text: "<speak>ignore</speak> & \"คำ\"");

        var asset = await provider.SynthesizePaidAsync(
            request,
            CampaignId,
            VideoScopeId,
            CancellationToken.None);

        Assert.Equal(2, handler.Requests.Count);
        var voiceList = handler.Requests[0];
        Assert.Equal(HttpMethod.Get, voiceList.Method);
        Assert.EndsWith("/cognitiveservices/voices/list", voiceList.Uri.AbsolutePath, StringComparison.Ordinal);
        Assert.Equal("test-key", voiceList.Headers["Ocp-Apim-Subscription-Key"]);

        var synthesis = handler.Requests[1];
        Assert.Equal(HttpMethod.Post, synthesis.Method);
        Assert.EndsWith("/cognitiveservices/v1", synthesis.Uri.AbsolutePath, StringComparison.Ordinal);
        Assert.Equal("test-key", synthesis.Headers["Ocp-Apim-Subscription-Key"]);
        Assert.Equal("audio-24khz-48kbitrate-mono-mp3", synthesis.Headers["X-Microsoft-OutputFormat"]);
        Assert.StartsWith("application/ssml+xml", synthesis.ContentType, StringComparison.Ordinal);
        Assert.Contains("&lt;speak&gt;", synthesis.Body, StringComparison.Ordinal);
        Assert.DoesNotContain("<speak>ignore", synthesis.Body, StringComparison.Ordinal);
        Assert.Contains("th-TH-PremwadeeNeural", synthesis.Body, StringComparison.Ordinal);

        Assert.Equal(AssetKind.Audio, asset.Kind);
        Assert.Equal("audio/wav", asset.MimeType);
        Assert.Equal(1, price.Calls);
        Assert.Equal(1, budget.ReserveCalls);
        Assert.Equal(1, budget.SettleCalls);
        Assert.Equal(0, budget.ReleaseCalls);
        Assert.Equal(price.Quote.EstimatedUsd, budget.LastSettledUsd);
    }

    [Fact]
    public async Task VoiceListIsFetchedOncePerProvider()
    {
        var handler = new RecordingHandler((request, _) =>
            request.Method == HttpMethod.Get
                ? Json(VoiceListJson())
                : Audio(CreateWav(120)));
        using var http = new HttpClient(handler, disposeHandler: false);
        var provider = CreateProvider(
            http,
            new RecordingBudgetGuard(),
            new StubPriceQuoter(),
            new RecordingAssetStore());

        await provider.SynthesizePaidAsync(
            CreateRequest("ประโยคแรก"),
            CampaignId,
            VideoScopeId,
            CancellationToken.None);
        await provider.SynthesizePaidAsync(
            CreateRequest("ประโยคที่สอง"),
            CampaignId,
            VideoScopeId,
            CancellationToken.None);

        Assert.Equal(3, handler.Requests.Count);
        Assert.Single(handler.Requests, request => request.Method == HttpMethod.Get);
        Assert.Equal(2, handler.Requests.Count(request => request.Method == HttpMethod.Post));
    }

    [Fact]
    public async Task VoiceLocaleMismatchRejectedBeforeSynthesis()
    {
        var handler = new RecordingHandler((request, _) =>
            request.Method == HttpMethod.Get
                ? Json(VoiceListJson())
                : Audio(CreateWav(120)));
        using var http = new HttpClient(handler, disposeHandler: false);
        var provider = CreateProvider(
            http,
            new RecordingBudgetGuard(),
            new StubPriceQuoter(),
            new RecordingAssetStore());

        var exception = await Assert.ThrowsAsync<ValidationException>(
            () => provider.SynthesizePaidAsync(
                CreateRequest(locale: "en-US"),
                CampaignId,
                VideoScopeId,
                CancellationToken.None));

        Assert.Equal("VOICE_LOCALE_MISMATCH", exception.Code);
        Assert.Single(handler.Requests);
        Assert.Equal(HttpMethod.Get, handler.Requests[0].Method);
    }

    [Fact]
    public async Task UnknownVoiceRejectedBeforeSynthesis()
    {
        var handler = new RecordingHandler((request, _) =>
            request.Method == HttpMethod.Get
                ? Json(VoiceListJson())
                : Audio(CreateWav(120)));
        using var http = new HttpClient(handler, disposeHandler: false);
        var provider = CreateProvider(
            http,
            new RecordingBudgetGuard(),
            new StubPriceQuoter(),
            new RecordingAssetStore());

        var exception = await Assert.ThrowsAsync<ValidationException>(
            () => provider.SynthesizePaidAsync(
                CreateRequest(voiceId: "th-TH-NotInCatalog"),
                CampaignId,
                VideoScopeId,
                CancellationToken.None));

        Assert.Equal("VOICE_NOT_ALLOWED", exception.Code);
        Assert.Single(handler.Requests);
    }

    [Fact]
    public async Task MissingSpeechPriceBlocksBeforeAnyHttp()
    {
        var handler = new RecordingHandler((_, _) =>
            throw new InvalidOperationException("No HTTP is allowed."));
        using var http = new HttpClient(handler, disposeHandler: false);
        var budget = new RecordingBudgetGuard();
        var price = new StubPriceQuoter { ThrowStale = true };
        var provider = CreateProvider(http, budget, price, new RecordingAssetStore());

        await Assert.ThrowsAsync<PriceStaleException>(
            () => provider.SynthesizePaidAsync(
                CreateRequest(),
                CampaignId,
                VideoScopeId,
                CancellationToken.None));

        Assert.Empty(handler.Requests);
        Assert.Equal(0, budget.ReserveCalls);
        Assert.Equal(0, budget.SettleCalls);
        Assert.Equal(0, budget.ReleaseCalls);
    }

    [Fact]
    public async Task PaidApisSwitchBlocksBeforeAnyHttp()
    {
        var handler = new RecordingHandler((_, _) =>
            throw new InvalidOperationException("No HTTP is allowed."));
        using var http = new HttpClient(handler, disposeHandler: false);
        var provider = new AzureSpeechVoiceProvider(
            http,
            new AzureSpeechOptions
            {
                Endpoint = Endpoint,
                ApiKey = "test-key",
                AllowPaidApis = false,
                EnableSynthesis = true
            },
            new RecordingAssetStore(),
            new RecordingBudgetGuard(),
            new StubPriceQuoter());

        await Assert.ThrowsAsync<PaidApisDisabledException>(
            () => provider.SynthesizePaidAsync(
                CreateRequest(),
                CampaignId,
                VideoScopeId,
                CancellationToken.None));

        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task PaidApisDisabledBlocksBeforeSynthesis()
    {
        var handler = new RecordingHandler((request, _) =>
            request.Method == HttpMethod.Get
                ? Json(VoiceListJson())
                : throw new InvalidOperationException("Synthesis must not be called."));
        using var http = new HttpClient(handler, disposeHandler: false);
        var budget = new RecordingBudgetGuard { ThrowDisabled = true };
        var provider = CreateProvider(http, budget, new StubPriceQuoter(), new RecordingAssetStore());

        await Assert.ThrowsAsync<PaidApisDisabledException>(
            () => provider.SynthesizePaidAsync(
                CreateRequest(),
                CampaignId,
                VideoScopeId,
                CancellationToken.None));

        Assert.Single(handler.Requests);
        Assert.Equal(HttpMethod.Get, handler.Requests[0].Method);
        Assert.Equal(0, budget.SettleCalls);
        Assert.Equal(0, budget.ReleaseCalls);
    }

    [Fact]
    public async Task DefinitiveRejectionReleasesReservation()
    {
        var handler = new RecordingHandler((request, _) =>
            request.Method == HttpMethod.Get
                ? Json(VoiceListJson())
                : new HttpResponseMessage(HttpStatusCode.BadRequest)
                {
                    Content = new StringContent("{\"error\":\"bad request\"}")
                });
        using var http = new HttpClient(handler, disposeHandler: false);
        var budget = new RecordingBudgetGuard();
        var provider = CreateProvider(
            http,
            budget,
            new StubPriceQuoter(),
            new RecordingAssetStore());

        var exception = await Assert.ThrowsAsync<VoiceSynthesisException>(
            () => provider.SynthesizePaidAsync(
                CreateRequest(),
                CampaignId,
                VideoScopeId,
                CancellationToken.None));

        Assert.Equal("VOICE_PROVIDER_REJECTED", exception.Code);
        Assert.False(exception.SubmissionUncertain);
        Assert.Equal(1, budget.ReserveCalls);
        Assert.Equal(0, budget.SettleCalls);
        Assert.Equal(1, budget.ReleaseCalls);
    }

    [Fact]
    public async Task UncertainFailureKeepsReservation()
    {
        var handler = new RecordingHandler((request, _) =>
            request.Method == HttpMethod.Get
                ? Json(VoiceListJson())
                : new HttpResponseMessage(HttpStatusCode.ServiceUnavailable));
        using var http = new HttpClient(handler, disposeHandler: false);
        var budget = new RecordingBudgetGuard();
        var provider = CreateProvider(
            http,
            budget,
            new StubPriceQuoter(),
            new RecordingAssetStore());

        var exception = await Assert.ThrowsAsync<VoiceSynthesisException>(
            () => provider.SynthesizePaidAsync(
                CreateRequest(),
                CampaignId,
                VideoScopeId,
                CancellationToken.None));

        Assert.Equal("VOICE_SUBMISSION_UNKNOWN", exception.Code);
        Assert.True(exception.SubmissionUncertain);
        Assert.Equal(1, budget.ReserveCalls);
        Assert.Equal(0, budget.SettleCalls);
        Assert.Equal(0, budget.ReleaseCalls);
    }

    [Fact]
    public async Task EmptyProviderResponseKeepsReservation()
    {
        var handler = new RecordingHandler((request, _) =>
            request.Method == HttpMethod.Get
                ? Json(VoiceListJson())
                : Audio([]));
        using var http = new HttpClient(handler, disposeHandler: false);
        var budget = new RecordingBudgetGuard();
        var provider = CreateProvider(
            http,
            budget,
            new StubPriceQuoter(),
            new RecordingAssetStore());

        var exception = await Assert.ThrowsAsync<VoiceSynthesisException>(
            () => provider.SynthesizePaidAsync(
                CreateRequest(),
                CampaignId,
                VideoScopeId,
                CancellationToken.None));

        Assert.Equal("VOICE_RESPONSE_INVALID", exception.Code);
        Assert.True(exception.SubmissionUncertain);
        Assert.Equal(1, budget.ReserveCalls);
        Assert.Equal(0, budget.SettleCalls);
        Assert.Equal(0, budget.ReleaseCalls);
    }

    [Fact]
    public async Task UnscopedSynthesisIsRejectedWithoutHttp()
    {
        var handler = new RecordingHandler((_, _) =>
            throw new InvalidOperationException("No HTTP is allowed."));
        using var http = new HttpClient(handler, disposeHandler: false);
        var provider = CreateProvider(
            http,
            new RecordingBudgetGuard(),
            new StubPriceQuoter(),
            new RecordingAssetStore());

        var exception = await Assert.ThrowsAsync<ValidationException>(
            () => provider.SynthesizeAsync(CreateRequest(), CancellationToken.None));

        Assert.Equal("VOICE_BILLING_SCOPE_REQUIRED", exception.Code);
        Assert.Empty(handler.Requests);
    }

    private static AzureSpeechVoiceProvider CreateProvider(
        HttpClient http,
        RecordingBudgetGuard budget,
        StubPriceQuoter price,
        RecordingAssetStore assets)
        => new(
            http,
            new AzureSpeechOptions
            {
                Endpoint = Endpoint,
                ApiKey = "test-key",
                AllowPaidApis = true,
                EnableSynthesis = true
            },
            assets,
            budget,
            price);

    private static VoiceRequest CreateRequest(
        string text = "สวัสดีค่ะ",
        string locale = "th-TH",
        string voiceId = "th-TH-PremwadeeNeural")
        => new(
            "voice:" + Guid.NewGuid().ToString("N"),
            locale,
            voiceId,
            text,
            "config-v1");

    private static string VoiceListJson()
        => File.ReadAllText(Path.Combine(
            AppContext.BaseDirectory,
            "Fixtures",
            "Voice",
            "voices-th-TH.json"));

    private static HttpResponseMessage Json(string json)
        => new(HttpStatusCode.OK)
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json")
        };

    private static HttpResponseMessage Audio(byte[] bytes)
    {
        var content = new ByteArrayContent(bytes);
        content.Headers.ContentType = new MediaTypeHeaderValue("audio/wav");
        return new HttpResponseMessage(HttpStatusCode.OK) { Content = content };
    }

    private static byte[] CreateWav(int milliseconds)
    {
        const int sampleRate = 8000;
        const short channels = 1;
        const short bitsPerSample = 16;
        var samples = Math.Max(1, sampleRate * milliseconds / 1000);
        var dataSize = samples * channels * bitsPerSample / 8;

        using var buffer = new MemoryStream();
        using var writer = new BinaryWriter(buffer);
        writer.Write("RIFF"u8);
        writer.Write(36 + dataSize);
        writer.Write("WAVE"u8);
        writer.Write("fmt "u8);
        writer.Write(16);
        writer.Write((short)1);
        writer.Write(channels);
        writer.Write(sampleRate);
        writer.Write(sampleRate * channels * bitsPerSample / 8);
        writer.Write((short)(channels * bitsPerSample / 8));
        writer.Write(bitsPerSample);
        writer.Write("data"u8);
        writer.Write(dataSize);

        for (var index = 0; index < samples; index++)
        {
            var sample = (short)(Math.Sin(2 * Math.PI * 440 * index / sampleRate) * 12000);
            writer.Write(sample);
        }

        writer.Flush();
        return buffer.ToArray();
    }

    private sealed record RecordedRequest(
        HttpMethod Method,
        Uri Uri,
        string Body,
        string ContentType,
        IReadOnlyDictionary<string, string> Headers);

    private sealed class RecordingHandler(
        Func<HttpRequestMessage, CancellationToken, HttpResponseMessage> responder)
        : HttpMessageHandler
    {
        public List<RecordedRequest> Requests { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            var body = request.Content is null
                ? string.Empty
                : await request.Content.ReadAsStringAsync(cancellationToken);
            var headers = request.Headers
                .ToDictionary(
                    header => header.Key,
                    header => string.Join(",", header.Value),
                    StringComparer.OrdinalIgnoreCase);

            Requests.Add(new RecordedRequest(
                request.Method,
                request.RequestUri!,
                body,
                request.Content?.Headers.ContentType?.ToString() ?? string.Empty,
                headers));

            return responder(request, cancellationToken);
        }
    }

    private sealed class RecordingAssetStore : IAssetStore
    {
        private readonly Dictionary<Guid, byte[]> _content = [];

        public Task<AssetRef> PutAsync(
            Stream input,
            AssetKind kind,
            string mime,
            CancellationToken ct)
        {
            using var buffer = new MemoryStream();
            input.CopyTo(buffer);
            var bytes = buffer.ToArray();
            if (bytes.Length == 0)
            {
                throw new ValidationException("ASSET_EMPTY", "The audio response was empty.");
            }

            var sha = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
            var id = Guid.NewGuid();
            _content[id] = bytes;

            return Task.FromResult(new AssetRef(
                id,
                sha,
                $"assets/sha256/{sha[..2]}/{sha}.wav",
                kind,
                mime,
                bytes.Length,
                null));
        }

        public Task<Stream> OpenReadAsync(Guid assetId, CancellationToken ct)
            => Task.FromResult<Stream>(new MemoryStream(_content[assetId], writable: false));
    }

    private sealed class RecordingBudgetGuard : IBudgetGuard
    {
        public int ReserveCalls { get; private set; }

        public int SettleCalls { get; private set; }

        public int ReleaseCalls { get; private set; }

        public decimal LastSettledUsd { get; private set; }

        public bool ThrowDisabled { get; init; }

        public Task<BudgetReservation> ReserveAsync(
            string operationId,
            Guid campaignId,
            PriceQuote quote,
            CancellationToken ct)
            => ReserveAsync(operationId, campaignId, campaignId, quote, ct);

        public Task<BudgetReservation> ReserveAsync(
            string operationId,
            Guid campaignId,
            Guid videoScopeId,
            PriceQuote quote,
            CancellationToken ct)
        {
            if (ThrowDisabled)
            {
                throw new PaidApisDisabledException();
            }

            Assert.Equal(CampaignId, campaignId);
            Assert.Equal(VideoScopeId, videoScopeId);
            ReserveCalls++;
            return Task.FromResult(new BudgetReservation(
                Guid.NewGuid(),
                operationId,
                quote.ReservationUsd));
        }

        public Task SettleAsync(
            Guid reservationId,
            decimal amountUsd,
            string certainty,
            CancellationToken ct)
        {
            SettleCalls++;
            LastSettledUsd = amountUsd;
            Assert.Equal("estimated", certainty);
            return Task.CompletedTask;
        }

        public Task ReleaseAsync(
            Guid reservationId,
            string confirmedReason,
            CancellationToken ct)
        {
            ReleaseCalls++;
            Assert.False(string.IsNullOrWhiteSpace(confirmedReason));
            return Task.CompletedTask;
        }
    }

    private sealed class StubPriceQuoter : IVoicePriceQuoter
    {
        public bool ThrowStale { get; init; }

        public int Calls { get; private set; }

        public PriceQuote Quote { get; } = new(
            Guid.Parse("55555555-5555-5555-5555-555555555555"),
            "azure-speech",
            "th-TH-PremwadeeNeural",
            0.004m,
            0.0048m,
            DateTimeOffset.UtcNow.AddHours(1));

        public Task<PriceQuote> GetQuoteAsync(
            string provider,
            string model,
            string configHash,
            CancellationToken ct)
        {
            Calls++;
            if (ThrowStale)
            {
                throw new PriceStaleException(
                    "No verified Azure Speech price snapshot is available.");
            }

            Assert.Equal("azure-speech", provider);
            Assert.False(string.IsNullOrWhiteSpace(model));
            Assert.Equal("config-v1", configHash);
            return Task.FromResult(Quote);
        }
    }
}
