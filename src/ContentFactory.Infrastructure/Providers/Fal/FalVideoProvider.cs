using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using ContentFactory.Contracts;
using ContentFactory.Core.Abstractions;
using ContentFactory.Core.Exceptions;

namespace ContentFactory.Infrastructure.Providers.Fal;

/// <summary>
/// fal.ai queue adapter for image-to-video generation. It posts the exact D6 request,
/// records the provider's request id and queue URLs, and treats any ambiguous outcome as
/// <see cref="SubmissionUnknownException"/> so the budget guard holds the reservation.
/// </summary>
public sealed class FalVideoProvider : IVideoProvider
{
    private readonly HttpClient _http;
    private readonly FalOptions _options;
    private readonly FalRequestBuilder _requestBuilder;

    public FalVideoProvider(
        HttpClient http,
        FalOptions options,
        FalRequestBuilder requestBuilder)
    {
        _http = http ?? throw new ArgumentNullException(nameof(http));
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _requestBuilder = requestBuilder ?? throw new ArgumentNullException(nameof(requestBuilder));
        _options.Validate();
    }

    public string Name => "fal";

    public async Task<ProviderSubmission> SubmitAsync(
        VideoGenerationRequest request,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (string.IsNullOrWhiteSpace(_options.ApiKey))
        {
            throw new ValidationException(
                "FAL_KEY_MISSING",
                "A fal API key is required to submit video generation requests.");
        }

        var json = await _requestBuilder.BuildJsonAsync(request, ct);
        var url = new Uri(
            _options.BaseUrl.AbsoluteUri.TrimEnd('/') + "/" + _options.ModelId.TrimStart('/'));

        using var content = new StringContent(json, Encoding.UTF8, "application/json");
        using var requestMessage = new HttpRequestMessage(HttpMethod.Post, url)
        {
            Content = content
        };
        requestMessage.Headers.Authorization = new AuthenticationHeaderValue(
            "Key",
            _options.ApiKey);

        HttpResponseMessage response;
        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(_options.SubmitTimeout);
            response = await _http.SendAsync(
                requestMessage,
                HttpCompletionOption.ResponseHeadersRead,
                cts.Token);
        }
        catch (OperationCanceledException exception) when (!ct.IsCancellationRequested)
        {
            throw new SubmissionUnknownException(
                "The fal submission timed out before a response was received.",
                exception);
        }
        catch (HttpRequestException exception)
        {
            throw new SubmissionUnknownException(
                "The fal submission could not be confirmed due to a network failure.",
                exception);
        }

        await using var responseStream = await response.Content.ReadAsStreamAsync(ct);
        var document = await JsonDocument.ParseAsync(responseStream, cancellationToken: ct);

        if (response.StatusCode == HttpStatusCode.Unauthorized ||
            response.StatusCode == HttpStatusCode.Forbidden ||
            (int)response.StatusCode >= 400 && (int)response.StatusCode < 500)
        {
            throw new ProviderRejectedException(
                "FAL_PROVIDER_REJECTED",
                $"The fal provider rejected the request with status {(int)response.StatusCode}.");
        }

        if (!response.IsSuccessStatusCode)
        {
            throw new SubmissionUnknownException(
                $"The fal provider returned an ambiguous status {(int)response.StatusCode}.");
        }

        var requestId = GetString(document.RootElement, "request_id");
        var statusUrl = GetUri(document.RootElement, "status_url");
        var responseUrl = GetUri(document.RootElement, "response_url");
        var cancelUrl = GetUri(document.RootElement, "cancel_url");

        if (string.IsNullOrWhiteSpace(requestId) || statusUrl is null || responseUrl is null)
        {
            throw new SubmissionUnknownException(
                "The fal provider response did not contain the required queue references.");
        }

        return new ProviderSubmission(
            Name,
            _options.ModelId,
            requestId,
            statusUrl,
            responseUrl,
            cancelUrl);
    }

    public async Task<ProviderPollResult> PollAsync(
        ProviderSubmission submission,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(submission);
        EnsureQueueUrl(submission.StatusUrl);
        EnsureQueueUrl(submission.ResponseUrl);

        using var status = await SendGetWithRetriesAsync(submission.StatusUrl, ct);
        if (status is null)
        {
            // Poll failures never become a new paid submit; the caller reschedules.
            return new ProviderPollResult(ProviderRunStatus.InProgress, null, null, null);
        }

        var statusText = GetString(status.RootElement, "status")?.ToUpperInvariant();
        switch (statusText)
        {
            case "IN_QUEUE" or "IN_PROGRESS":
                return new ProviderPollResult(ProviderRunStatus.InProgress, null, null, null);
            case "FAILED":
                return new ProviderPollResult(
                    ProviderRunStatus.FailedConfirmed, null, "FAL_GENERATION_FAILED", null);
            case "COMPLETED":
                break;
            default:
                return new ProviderPollResult(
                    ProviderRunStatus.SubmissionUnknown, null, "FAL_STATUS_UNRECOGNISED", null);
        }

        // The output URL is short-lived and signed, so it is fetched from the existing
        // request's response_url on every call instead of being stored.
        using var result = await SendGetWithRetriesAsync(submission.ResponseUrl, ct);
        if (result is null)
        {
            return new ProviderPollResult(ProviderRunStatus.InProgress, null, null, null);
        }

        Uri? output = null;
        if (result.RootElement.ValueKind == JsonValueKind.Object
            && result.RootElement.TryGetProperty("video", out var video))
        {
            output = GetUri(video, "url");
        }

        int? seed = result.RootElement.ValueKind == JsonValueKind.Object
            && result.RootElement.TryGetProperty("seed", out var seedElement)
            && seedElement.ValueKind == JsonValueKind.Number
            && seedElement.TryGetInt32(out var seedValue)
                ? seedValue
                : null;

        return new ProviderPollResult(
            ProviderRunStatus.Succeeded,
            output,
            output is null ? "FAL_OUTPUT_MISSING" : null,
            seed);
    }

    public async Task RequestCancelAsync(
        ProviderSubmission submission,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(submission);

        if (submission.CancelUrl is null)
        {
            return;
        }

        EnsureQueueUrl(submission.CancelUrl);

        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Put, submission.CancelUrl);
            request.Headers.Authorization = new AuthenticationHeaderValue("Key", _options.ApiKey);
            using var response = await _http.SendAsync(request, ct);
        }
        catch (HttpRequestException)
        {
            // Cancellation is best-effort; the next poll/reconcile will determine state.
        }
    }

    /// <summary>
    /// GET with bounded retries for 429, 5xx and network failures, honoring Retry-After and
    /// adding jitter. Returns null when retries are exhausted. Other 4xx responses are
    /// definitive and surface as <see cref="ProviderRejectedException"/>.
    /// </summary>
    private async Task<JsonDocument?> SendGetWithRetriesAsync(Uri url, CancellationToken ct)
    {
        for (var attempt = 0; ; attempt++)
        {
            TimeSpan? wait = null;
            try
            {
                using var request = new HttpRequestMessage(HttpMethod.Get, url);
                request.Headers.Authorization = new AuthenticationHeaderValue(
                    "Key", _options.ApiKey);
                using var response = await _http.SendAsync(request, ct);
                var code = (int)response.StatusCode;

                if (response.IsSuccessStatusCode)
                {
                    var text = await response.Content.ReadAsStringAsync(ct);
                    try
                    {
                        return JsonDocument.Parse(text);
                    }
                    catch (JsonException)
                    {
                        wait = BackoffFor(attempt, null);
                    }
                }
                else if (code == 429 || code >= 500)
                {
                    wait = BackoffFor(attempt, RetryAfterOf(response));
                }
                else
                {
                    throw new ProviderRejectedException(
                        "FAL_POLL_REJECTED",
                        $"The fal provider rejected a status request with status {code}.");
                }
            }
            catch (HttpRequestException)
            {
                wait = BackoffFor(attempt, null);
            }

            if (attempt >= _options.GetRetries)
            {
                return null;
            }

            await _options.Delay(wait!.Value, ct);
        }
    }

    private TimeSpan BackoffFor(int attempt, TimeSpan? retryAfter)
    {
        var backoff = TimeSpan.FromTicks(
            _options.GetRetryBaseDelay.Ticks * (1L << Math.Min(attempt, 6)));
        var jitter = TimeSpan.FromTicks(
            (long)(_options.GetRetryBaseDelay.Ticks * _options.Jitter()));
        var wait = backoff + jitter;
        if (retryAfter is { } hinted && hinted > wait)
        {
            wait = hinted;
        }

        return wait > _options.MaxRetryAfter ? _options.MaxRetryAfter : wait;
    }

    private static TimeSpan? RetryAfterOf(HttpResponseMessage response)
    {
        var header = response.Headers.RetryAfter;
        if (header?.Delta is { } delta)
        {
            return delta;
        }

        if (header?.Date is { } date)
        {
            var remaining = date - DateTimeOffset.UtcNow;
            return remaining > TimeSpan.Zero ? remaining : TimeSpan.Zero;
        }

        return null;
    }

    /// <summary>
    /// The fal key is only ever sent to the configured queue host over HTTPS.
    /// </summary>
    private void EnsureQueueUrl(Uri url)
    {
        if (url is null
            || !url.IsAbsoluteUri
            || url.Scheme != Uri.UriSchemeHttps
            || !string.Equals(url.Host, _options.BaseUrl.Host, StringComparison.OrdinalIgnoreCase))
        {
            throw new ValidationException(
                "FAL_QUEUE_URL_REJECTED",
                "The fal queue URL must be HTTPS on the configured queue host.");
        }
    }

    private static string? GetString(JsonElement element, string name)
    {
        if (element.ValueKind != JsonValueKind.Object ||
            !element.TryGetProperty(name, out var value) ||
            value.ValueKind != JsonValueKind.String)
        {
            return null;
        }

        return value.GetString();
    }

    private static Uri? GetUri(JsonElement element, string name)
    {
        var text = GetString(element, name);
        return Uri.TryCreate(text, UriKind.Absolute, out var uri) ? uri : null;
    }
}
