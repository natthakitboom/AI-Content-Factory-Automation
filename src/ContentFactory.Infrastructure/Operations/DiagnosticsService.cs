using ContentFactory.Infrastructure.Media;

namespace ContentFactory.Infrastructure.Operations;

/// <summary>
/// Readiness checks that are honest about what could not be measured. It reads only whether
/// a credential is configured, never the credential, and only talks to LM Studio when its
/// address is loopback.
/// </summary>
public sealed class DiagnosticsService
{
    private readonly HttpClient _http;
    private readonly IProcessRunner _runner;
    private readonly string _dataRoot;

    public DiagnosticsService(HttpClient http, IProcessRunner runner, string dataRoot)
    {
        _http = http ?? throw new ArgumentNullException(nameof(http));
        _runner = runner ?? throw new ArgumentNullException(nameof(runner));
        _dataRoot = dataRoot ?? throw new ArgumentNullException(nameof(dataRoot));
    }

    public async Task<object> RunAsync(CancellationToken ct)
    {
        var modelId = Environment.GetEnvironmentVariable("LM_STUDIO_MODEL_ID");
        return new
        {
            lmStudio = await LmStudioAsync(ct),
            selectedModelId = string.IsNullOrWhiteSpace(modelId) ? null : modelId,
            media = await MediaAsync(ct),
            disk = Disk(),
            thermal = new { status = "unavailable", detail = "No temperature sensor is read; none is invented." },
            configured = new
            {
                falKey = Present("FAL_KEY"),
                lmStudioToken = Present("LM_STUDIO_API_TOKEN"),
                captionFontFile = Present("CAPTION_FONT_FILE")
            }
        };
    }

    private static bool Present(string name) => !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(name));

    private async Task<object> LmStudioAsync(CancellationToken ct)
    {
        var baseUrl = Environment.GetEnvironmentVariable("LM_STUDIO_BASE_URL") ?? "http://127.0.0.1:1234/v1";
        if (!Uri.TryCreate(baseUrl, UriKind.Absolute, out var uri)
            || !(uri.IsLoopback || uri.Host == "localhost"))
        {
            return new { status = "not_checked", detail = "LM Studio is only checked on a loopback address." };
        }

        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromSeconds(2));
            using var response = await _http.GetAsync(new Uri(uri, uri.AbsolutePath.TrimEnd('/') + "/models"), timeout.Token);
            return new { status = response.IsSuccessStatusCode ? "reachable" : "error", httpStatus = (int)response.StatusCode };
        }
        catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException or OperationCanceledException)
        {
            return new { status = "unreachable", detail = "LM Studio did not answer on the configured loopback address." };
        }
    }

    private async Task<object> MediaAsync(CancellationToken ct)
    {
        try
        {
            var encoders = await _runner.RunAsync("ffmpeg", ["-hide_banner", "-encoders"], TimeSpan.FromSeconds(10), ct);
            var filters = await _runner.RunAsync("ffmpeg", ["-hide_banner", "-filters"], TimeSpan.FromSeconds(10), ct);
            var names = EncoderProbe.ParseEncoderNames(encoders.StandardOutput);
            return new
            {
                status = "checked",
                hardwareEncoder = names.Contains("h264_videotoolbox"),
                softwareEncoder = names.Contains("libx264"),
                subtitlesFilter = EncoderProbe.HasFilter(filters.StandardOutput, "subtitles")
            };
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            return new { status = "unavailable", detail = "FFmpeg could not be run." };
        }
    }

    private object Disk()
    {
        try
        {
            var path = Directory.Exists(_dataRoot) ? _dataRoot : Path.GetDirectoryName(Path.GetFullPath(_dataRoot)) ?? "/";
            var drive = new DriveInfo(Path.GetFullPath(path));
            return new { freeBytes = drive.AvailableFreeSpace, totalBytes = drive.TotalSize };
        }
        catch (Exception exception) when (exception is IOException or ArgumentException or UnauthorizedAccessException)
        {
            return new { freeBytes = (long?)null, totalBytes = (long?)null };
        }
    }
}
