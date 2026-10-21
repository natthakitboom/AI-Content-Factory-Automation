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

        if (submission.StatusUrl is null)
        {
            throw new ValidationException(
                "FAL_STATUS_URL_MISSING",
                "The fal submission has no status URL to poll.");
        }

        HttpResponseMessage response;
        try
        {
            response = await _http.GetAsync(submission.StatusUrl, ct);
        }
        catch (HttpRequestException exception)
        {
            throw new SubmissionUnknownException(
                "The fal status poll could not be completed.",
                exception);
        }

        if (!response.IsSuccessStatusCode)
        {
            return new ProviderPollResult(
                ProviderRunStatus.InProgress,
                null,
                null,
                null);
        }

        var json = await response.Content.ReadAsStringAsync(ct);
        using var document = JsonDocument.Parse(json);
        var status = GetString(document.RootElement, "status")?.ToUpperInvariant();
        var statusEnum = status switch
        {
            "IN_QUEUE" or "IN_PROGRESS" => ProviderRunStatus.InProgress,
            "COMPLETED" => ProviderRunStatus.Succeeded,
            "FAILED" => ProviderRunStatus.FailedConfirmed,
            _ => ProviderRunStatus.SubmissionUnknown
        };

        return new ProviderPollResult(statusEnum, null, null, null);
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

        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, submission.CancelUrl);
            request.Headers.Authorization = new AuthenticationHeaderValue(
                "Key",
                _options.ApiKey);
            await _http.SendAsync(request, ct);
        }
        catch (HttpRequestException)
        {
            // Cancellation is best-effort; the next poll/reconcile will determine state.
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
