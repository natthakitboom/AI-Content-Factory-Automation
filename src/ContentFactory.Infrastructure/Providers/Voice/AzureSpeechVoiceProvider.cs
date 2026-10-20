using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Xml;
using ContentFactory.Contracts;
using ContentFactory.Core.Abstractions;
using ContentFactory.Core.Exceptions;
using ContentFactory.Infrastructure.Billing;

namespace ContentFactory.Infrastructure.Providers.Voice;

/// <summary>
/// Configuration for the Azure Speech REST adapter. Live synthesis is opt-in; an endpoint,
/// key and an explicit <see cref="EnableSynthesis"/> flag are all required. The key is never
/// included in an exception message or log line.
/// </summary>
public sealed class AzureSpeechOptions
{
    public const string DefaultProvider = "azure-speech";
    public const string DefaultProviderVersion = "azure-speech-rest-1";
    public const string DefaultOutputFormat = "audio-24khz-48kbitrate-mono-mp3";

    public Uri? Endpoint { get; init; }

    public string ApiKey { get; init; } = string.Empty;

    /// <summary>
    /// Mirrors ALLOW_PAID_APIS. Even an otherwise enabled adapter must not make a live
    /// Azure request while this is false.
    /// </summary>
    public bool AllowPaidApis { get; init; }

    public bool EnableSynthesis { get; init; }

    public string Provider { get; init; } = DefaultProvider;

    public string ProviderVersion { get; init; } = DefaultProviderVersion;

    public string OutputFormat { get; init; } = DefaultOutputFormat;

    public string VoiceListPath { get; init; } = "cognitiveservices/voices/list";

    public string SynthesisPath { get; init; } = "cognitiveservices/v1";

    public string Rate { get; init; } = "0%";

    public string Pitch { get; init; } = "0%";

    public TimeSpan RequestTimeout { get; init; } = TimeSpan.FromSeconds(60);

    public TimeSpan VoiceListLifetime { get; init; } = TimeSpan.FromHours(1);

    public static AzureSpeechOptions FromEnvironment()
    {
        var endpoint = Environment.GetEnvironmentVariable("AZURE_SPEECH_ENDPOINT");
        var apiKey = Environment.GetEnvironmentVariable("AZURE_SPEECH_KEY");
        var provider = Environment.GetEnvironmentVariable("VOICE_PROVIDER");
        var allowPaid = bool.TryParse(
            Environment.GetEnvironmentVariable("ALLOW_PAID_APIS"),
            out var parsed) && parsed;

        return new AzureSpeechOptions
        {
            Endpoint = Uri.TryCreate(endpoint, UriKind.Absolute, out var uri) ? uri : null,
            ApiKey = apiKey ?? string.Empty,
            AllowPaidApis = allowPaid,
            EnableSynthesis = allowPaid &&
                string.Equals(
                    string.IsNullOrWhiteSpace(provider) ? "uploaded" : provider.Trim(),
                    "azure",
                    StringComparison.OrdinalIgnoreCase)
        };
    }

    public void Validate()
    {
        if (Endpoint is not null &&
            (!Endpoint.IsAbsoluteUri ||
             (Endpoint.Scheme != Uri.UriSchemeHttps &&
              !(Endpoint.Scheme == Uri.UriSchemeHttp && Endpoint.IsLoopback))))
        {
            throw new ValidationException(
                "VOICE_CONFIG_INVALID",
                "AZURE_SPEECH_ENDPOINT must be an absolute HTTPS endpoint, or loopback HTTP for a fake/local test.");
        }

        if (EnableSynthesis && Endpoint is null)
        {
            throw new ValidationException(
                "VOICE_CONFIG_INVALID",
                "AZURE_SPEECH_ENDPOINT is required when live Azure Speech synthesis is enabled.");
        }

        if (EnableSynthesis && string.IsNullOrWhiteSpace(ApiKey))
        {
            throw new ValidationException(
                "VOICE_CONFIG_INVALID",
                "AZURE_SPEECH_KEY is required when live Azure Speech synthesis is enabled.");
        }

        if (string.IsNullOrWhiteSpace(Provider) || Provider.Length > 32)
        {
            throw new ValidationException(
                "VOICE_CONFIG_INVALID",
                "The speech provider name is invalid.");
        }

        if (string.IsNullOrWhiteSpace(ProviderVersion) || ProviderVersion.Length > 64)
        {
            throw new ValidationException(
                "VOICE_CONFIG_INVALID",
                "The speech provider version is invalid.");
        }

        if (string.IsNullOrWhiteSpace(OutputFormat) || OutputFormat.Length > 128)
        {
            throw new ValidationException(
                "VOICE_CONFIG_INVALID",
                "The speech output format is invalid.");
        }

        EnsureRelativePath(VoiceListPath, "voice-list path");
        EnsureRelativePath(SynthesisPath, "synthesis path");
        EnsurePercent(Rate, "rate");
        EnsurePercent(Pitch, "pitch");

        if (RequestTimeout <= TimeSpan.Zero ||
            RequestTimeout > TimeSpan.FromMinutes(10))
        {
            throw new ValidationException(
                "VOICE_CONFIG_INVALID",
                "The speech request timeout must be between zero and ten minutes.");
        }

        if (VoiceListLifetime <= TimeSpan.Zero ||
            VoiceListLifetime > TimeSpan.FromDays(7))
        {
            throw new ValidationException(
                "VOICE_CONFIG_INVALID",
                "The voice-list lifetime must be between zero and seven days.");
        }
    }

    public void EnsureEnabled()
    {
        if (!AllowPaidApis)
        {
            throw new PaidApisDisabledException();
        }

        if (!EnableSynthesis)
        {
            throw new ValidationException(
                "VOICE_LIVE_DISABLED",
                "Live Azure Speech synthesis is disabled. Use uploaded narration or enable it only after an owner approval.");
        }
    }

    private static void EnsureRelativePath(string path, string label)
    {
        if (string.IsNullOrWhiteSpace(path) ||
            path.Length > 256 ||
            path.Contains("://", StringComparison.Ordinal) ||
            path.Contains("..", StringComparison.Ordinal) ||
            path.Contains('\0') ||
            path.Any(char.IsControl))
        {
            throw new ValidationException(
                "VOICE_CONFIG_INVALID",
                $"The speech {label} is invalid.");
        }
    }

    private static void EnsurePercent(string value, string label)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > 8)
        {
            throw new ValidationException(
                "VOICE_CONFIG_INVALID",
                $"The speech {label} must be a percentage such as 0% or -10%.");
        }

        var trimmed = value.Trim();
        var start = trimmed[0] is '+' or '-' ? 1 : 0;
        if (start >= trimmed.Length || trimmed[^1] != '%')
        {
            throw new ValidationException(
                "VOICE_CONFIG_INVALID",
                $"The speech {label} must be a percentage such as 0% or -10%.");
        }

        var digits = trimmed[start..^1];
        if (digits.Length is < 1 or > 3 || !digits.All(char.IsAsciiDigit))
        {
            throw new ValidationException(
                "VOICE_CONFIG_INVALID",
                $"The speech {label} must be a percentage such as 0% or -10%.");
        }
    }
}

/// <summary>
/// A provider-side speech failure. <see cref="SubmissionUncertain"/> is true whenever the
/// request may have reached Azure; the caller must not release the reservation or retry
/// blindly in that case.
/// </summary>
public sealed class VoiceSynthesisException : Exception
{
    public VoiceSynthesisException(
        string code,
        string message,
        bool submissionUncertain)
        : base(message)
    {
        Code = code;
        SubmissionUncertain = submissionUncertain;
    }

    public string Code { get; }

    public bool SubmissionUncertain { get; }
}

/// <summary>
/// SSML construction through <see cref="XmlWriter"/>. Model/scene text is written as text
/// content, so markup is escaped rather than interpreted. Voice IDs and locales are also
/// escaped in attributes even though they must already be in the fetched allow-list.
/// </summary>
public static class AzureSpeechSsml
{
    private const string SynthesisNamespace = "http://www.w3.org/2001/10/synthesis";
    private const int MaxTextLength = 4000;
    private const int MaxVoiceIdLength = 256;
    private const int MaxLocaleLength = 35;

    public static string Build(
        string text,
        string voiceId,
        string locale,
        string rate,
        string pitch)
    {
        ArgumentNullException.ThrowIfNull(text);

        if (string.IsNullOrWhiteSpace(text) || text.Length > MaxTextLength)
        {
            throw new ValidationException(
                "VOICE_TEXT_INVALID",
                "Narration text must be non-empty and no longer than 4000 characters.");
        }

        if (string.IsNullOrWhiteSpace(voiceId) || voiceId.Length > MaxVoiceIdLength)
        {
            throw new ValidationException(
                "VOICE_VOICE_ID_INVALID",
                "The selected voice id is invalid.");
        }

        if (string.IsNullOrWhiteSpace(locale) || locale.Length > MaxLocaleLength)
        {
            throw new ValidationException(
                "VOICE_LOCALE_INVALID",
                "The requested narration locale is invalid.");
        }

        try
        {
            XmlConvert.VerifyXmlChars(text);
            XmlConvert.VerifyXmlChars(voiceId);
            XmlConvert.VerifyXmlChars(locale);
        }
        catch (XmlException)
        {
            throw new ValidationException(
                "VOICE_TEXT_INVALID",
                "Narration text contains characters that are not valid XML.");
        }

        var settings = new XmlWriterSettings
        {
            OmitXmlDeclaration = true,
            Indent = false,
            ConformanceLevel = ConformanceLevel.Document
        };

        using var writer = new StringWriter(CultureInfo.InvariantCulture);
        using (var xml = XmlWriter.Create(writer, settings))
        {
            xml.WriteStartElement("speak", SynthesisNamespace);
            xml.WriteAttributeString("version", "1.0");
            xml.WriteAttributeString("xml", "lang", null, locale);

            xml.WriteStartElement("voice", SynthesisNamespace);
            xml.WriteAttributeString("name", voiceId);

            xml.WriteStartElement("prosody", SynthesisNamespace);
            xml.WriteAttributeString("rate", rate);
            xml.WriteAttributeString("pitch", pitch);
            xml.WriteString(text);
            xml.WriteEndElement();

            xml.WriteEndElement();
            xml.WriteEndElement();
        }

        return writer.ToString();
    }
}

/// <summary>
/// Azure Speech REST adapter. It fetches the allowed voice list from the configured
/// endpoint, rejects voices that are not exactly in that list, reserves through the same
/// PostgreSQL budget guard as video work, and only settles after a valid audio asset has
/// been stored. A definite rejection releases the reservation; a 5xx, timeout or malformed
/// response is uncertain and keeps it.
/// </summary>
public sealed class AzureSpeechVoiceProvider : IPaidVoiceProvider
{
    private const int MaxVoiceListBytes = 1024 * 1024;
    private const int MaxOperationIdLength = 256;
    private const int MaxConfigHashLength = 64;

    private readonly HttpClient _http;
    private readonly AzureSpeechOptions _options;
    private readonly IAssetStore _assets;
    private readonly IBudgetGuard _budget;
    private readonly IVoicePriceQuoter _priceQuoter;
    private readonly TimeProvider _clock;
    private readonly SemaphoreSlim _voiceListLock = new(1, 1);

    private IReadOnlyList<AzureVoice>? _voices;
    private DateTimeOffset _voicesValidUntil;

    public AzureSpeechVoiceProvider(
        HttpClient http,
        AzureSpeechOptions options,
        IAssetStore assets,
        IBudgetGuard budget,
        IVoicePriceQuoter priceQuoter,
        TimeProvider? clock = null)
    {
        _http = http ?? throw new ArgumentNullException(nameof(http));
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _assets = assets ?? throw new ArgumentNullException(nameof(assets));
        _budget = budget ?? throw new ArgumentNullException(nameof(budget));
        _priceQuoter = priceQuoter ?? throw new ArgumentNullException(nameof(priceQuoter));
        _clock = clock ?? TimeProvider.System;
        _options.Validate();
    }

    /// <summary>
    /// A paid provider cannot synthesize without an explicit campaign/video scope. The
    /// assembler calls <see cref="SynthesizePaidAsync"/> instead.
    /// </summary>
    public Task<AssetRef> SynthesizeAsync(
        VoiceRequest request,
        CancellationToken ct)
        => throw new ValidationException(
            "VOICE_BILLING_SCOPE_REQUIRED",
            "Paid speech synthesis requires an explicit campaign and video budget scope.");

    public async Task<AssetRef> SynthesizePaidAsync(
        VoiceRequest request,
        Guid campaignId,
        Guid videoScopeId,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);
        ValidateRequest(request);
        _options.EnsureEnabled();

        if (campaignId == Guid.Empty || videoScopeId == Guid.Empty)
        {
            throw new ValidationException(
                "VOICE_BILLING_SCOPE_REQUIRED",
                "A campaign id and video/revision scope are required for paid speech.");
        }

        var ssml = AzureSpeechSsml.Build(
            request.Text,
            request.VoiceId,
            request.Locale,
            _options.Rate,
            _options.Pitch);

        // A verified, unexpired speech price is required before any live HTTP request.
        var quote = await _priceQuoter.GetQuoteAsync(
            _options.Provider,
            request.VoiceId,
            request.VoiceConfigHash,
            ct);

        await EnsureVoiceAllowedAsync(request, ct);

        var reservation = await _budget.ReserveAsync(
            request.OperationId,
            campaignId,
            videoScopeId,
            quote,
            ct);

        var sent = false;
        try
        {
            using var content = new StringContent(
                ssml,
                Encoding.UTF8,
                "application/ssml+xml");
            using var message = new HttpRequestMessage(
                HttpMethod.Post,
                BuildUri(_options.SynthesisPath))
            {
                Content = content
            };
            message.Headers.TryAddWithoutValidation(
                "Ocp-Apim-Subscription-Key",
                _options.ApiKey);
            message.Headers.TryAddWithoutValidation(
                "X-Microsoft-OutputFormat",
                _options.OutputFormat);
            message.Headers.TryAddWithoutValidation(
                "User-Agent",
                "ContentFactory/1.0");

            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(_options.RequestTimeout);

            sent = true;
            using var response = await _http.SendAsync(
                message,
                HttpCompletionOption.ResponseHeadersRead,
                timeout.Token);

            if (!response.IsSuccessStatusCode)
            {
                if (IsDefinitiveRejection(response.StatusCode))
                {
                    await _budget.ReleaseAsync(
                        reservation.Id,
                        $"Azure Speech rejected the request with HTTP {(int)response.StatusCode}.",
                        ct);
                    throw new VoiceSynthesisException(
                        "VOICE_PROVIDER_REJECTED",
                        "Azure Speech rejected the synthesis request.",
                        submissionUncertain: false);
                }

                throw new VoiceSynthesisException(
                    "VOICE_SUBMISSION_UNKNOWN",
                    "Azure Speech did not confirm whether the synthesis request was accepted.",
                    submissionUncertain: true);
            }

            var mime = response.Content.Headers.ContentType?.MediaType;
            if (string.IsNullOrWhiteSpace(mime) ||
                !mime.StartsWith("audio/", StringComparison.OrdinalIgnoreCase))
            {
                throw new VoiceSynthesisException(
                    "VOICE_RESPONSE_INVALID",
                    "Azure Speech returned a non-audio response.",
                    submissionUncertain: true);
            }

            AssetRef asset;
            try
            {
                await using var stream = await response.Content.ReadAsStreamAsync(timeout.Token);
                asset = await _assets.PutAsync(stream, AssetKind.Audio, mime, timeout.Token);
            }
            catch (ValidationException)
            {
                throw new VoiceSynthesisException(
                    "VOICE_RESPONSE_INVALID",
                    "Azure Speech returned an empty or invalid audio response.",
                    submissionUncertain: true);
            }

            await _budget.SettleAsync(
                reservation.Id,
                quote.EstimatedUsd,
                "estimated",
                ct);

            return asset;
        }
        catch (VoiceSynthesisException)
        {
            throw;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            if (!sent)
            {
                await _budget.ReleaseAsync(
                    reservation.Id,
                    "The paid speech request was cancelled before submission.",
                    CancellationToken.None);
            }

            throw;
        }
        catch (OperationCanceledException)
        {
            throw new VoiceSynthesisException(
                "VOICE_SUBMISSION_UNKNOWN",
                "The Azure Speech request timed out and may have been accepted.",
                submissionUncertain: true);
        }
        catch (HttpRequestException)
        {
            throw new VoiceSynthesisException(
                "VOICE_SUBMISSION_UNKNOWN",
                "The Azure Speech request failed and may have been accepted.",
                submissionUncertain: true);
        }
    }

    private async Task EnsureVoiceAllowedAsync(
        VoiceRequest request,
        CancellationToken ct)
    {
        var voices = await GetVoicesAsync(ct);
        var voice = voices.FirstOrDefault(item =>
            string.Equals(item.ShortName, request.VoiceId, StringComparison.Ordinal));

        if (voice is null)
        {
            throw new ValidationException(
                "VOICE_NOT_ALLOWED",
                "The requested voice is not in the configured Azure Speech voice list.");
        }

        if (!string.Equals(voice.Locale, request.Locale, StringComparison.OrdinalIgnoreCase))
        {
            throw new ValidationException(
                "VOICE_LOCALE_MISMATCH",
                "The requested voice does not match the narration locale.");
        }
    }

    private async Task<IReadOnlyList<AzureVoice>> GetVoicesAsync(CancellationToken ct)
    {
        var now = _clock.GetUtcNow();
        if (_voices is { } cached && now < _voicesValidUntil)
        {
            return cached;
        }

        await _voiceListLock.WaitAsync(ct);
        try
        {
            now = _clock.GetUtcNow();
            if (_voices is { } rechecked && now < _voicesValidUntil)
            {
                return rechecked;
            }

            using var message = new HttpRequestMessage(
                HttpMethod.Get,
                BuildUri(_options.VoiceListPath));
            message.Headers.TryAddWithoutValidation(
                "Ocp-Apim-Subscription-Key",
                _options.ApiKey);

            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(_options.RequestTimeout);
            using var response = await _http.SendAsync(
                message,
                HttpCompletionOption.ResponseHeadersRead,
                timeout.Token);

            if (!response.IsSuccessStatusCode)
            {
                throw new ValidationException(
                    "VOICE_CATALOG_UNAVAILABLE",
                    "The Azure Speech voice list could not be fetched.");
            }

            if (response.Content.Headers.ContentLength is > MaxVoiceListBytes)
            {
                throw new ValidationException(
                    "VOICE_CATALOG_UNAVAILABLE",
                    "The Azure Speech voice list is larger than the configured limit.");
            }

            var json = await response.Content.ReadAsStringAsync(timeout.Token);
            if (json.Length > MaxVoiceListBytes)
            {
                throw new ValidationException(
                    "VOICE_CATALOG_UNAVAILABLE",
                    "The Azure Speech voice list is larger than the configured limit.");
            }

            var parsed = ParseVoiceList(json);
            _voices = parsed;
            _voicesValidUntil = _clock.GetUtcNow() + _options.VoiceListLifetime;
            return parsed;
        }
        finally
        {
            _voiceListLock.Release();
        }
    }

    private static IReadOnlyList<AzureVoice> ParseVoiceList(string json)
    {
        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(json);
        }
        catch (JsonException)
        {
            throw new ValidationException(
                "VOICE_CATALOG_UNAVAILABLE",
                "The Azure Speech voice list is not valid JSON.");
        }

        using (document)
        {
            if (document.RootElement.ValueKind != JsonValueKind.Array)
            {
                throw new ValidationException(
                    "VOICE_CATALOG_UNAVAILABLE",
                    "The Azure Speech voice list must be a JSON array.");
            }

            var voices = new List<AzureVoice>();
            foreach (var item in document.RootElement.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.Object ||
                    !item.TryGetProperty("ShortName", out var shortName) ||
                    !item.TryGetProperty("Locale", out var locale) ||
                    shortName.ValueKind != JsonValueKind.String ||
                    locale.ValueKind != JsonValueKind.String)
                {
                    continue;
                }

                var name = shortName.GetString();
                var localeValue = locale.GetString();
                if (string.IsNullOrWhiteSpace(name) ||
                    string.IsNullOrWhiteSpace(localeValue))
                {
                    continue;
                }

                voices.Add(new AzureVoice(name, localeValue));
            }

            if (voices.Count == 0)
            {
                throw new ValidationException(
                    "VOICE_CATALOG_INVALID",
                    "The Azure Speech voice list contains no usable voices.");
            }

            return voices;
        }
    }

    private Uri BuildUri(string relativePath)
    {
        var endpoint = _options.Endpoint
            ?? throw new ValidationException(
                "VOICE_CONFIG_INVALID",
                "AZURE_SPEECH_ENDPOINT is not configured.");
        return new Uri(
            endpoint.AbsoluteUri.TrimEnd('/') + "/" + relativePath.TrimStart('/'),
            UriKind.Absolute);
    }

    private static bool IsDefinitiveRejection(HttpStatusCode status)
        => status is HttpStatusCode.BadRequest
            or HttpStatusCode.Unauthorized
            or HttpStatusCode.Forbidden
            or HttpStatusCode.NotFound
            or HttpStatusCode.UnprocessableEntity;

    private static void ValidateRequest(VoiceRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.OperationId) ||
            request.OperationId.Length > MaxOperationIdLength)
        {
            throw new ValidationException(
                "VOICE_OPERATION_REQUIRED",
                "A speech operation id between 1 and 256 characters is required.");
        }

        if (string.IsNullOrWhiteSpace(request.Locale) || request.Locale.Length > 35)
        {
            throw new ValidationException(
                "VOICE_LOCALE_INVALID",
                "The narration locale is invalid.");
        }

        if (string.IsNullOrWhiteSpace(request.VoiceId) || request.VoiceId.Length > 256)
        {
            throw new ValidationException(
                "VOICE_VOICE_ID_INVALID",
                "The selected voice id is invalid.");
        }

        if (string.IsNullOrWhiteSpace(request.Text) || request.Text.Length > 4000)
        {
            throw new ValidationException(
                "VOICE_TEXT_INVALID",
                "Narration text must be non-empty and no longer than 4000 characters.");
        }

        if (string.IsNullOrWhiteSpace(request.VoiceConfigHash) ||
            request.VoiceConfigHash.Length > MaxConfigHashLength)
        {
            throw new ValidationException(
                "VOICE_CONFIG_INVALID",
                "A speech synthesis config hash between 1 and 64 characters is required.");
        }
    }

    private sealed record AzureVoice(string ShortName, string Locale);
}
