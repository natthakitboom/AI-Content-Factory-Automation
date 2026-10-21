using System.Diagnostics;

namespace ContentFactory.IntegrationTests.Infrastructure;

/// <summary>
/// Skips (never passes) a native-media pipeline test when FFmpeg, FFprobe or the libass
/// subtitles filter is unavailable, so a missing prerequisite is reported as skipped.
/// </summary>
public sealed class PipelineFactAttribute : FactAttribute
{
    private static readonly string? MissingReason = Probe();

    public PipelineFactAttribute()
    {
        if (MissingReason is not null)
        {
            Skip = MissingReason;
        }
    }

    private static string? Probe()
    {
        try
        {
            if (Run("ffprobe", "-version") != 0)
            {
                return "FFprobe is unavailable; the native pipeline test is blocked.";
            }

            return Run("ffmpeg", "-hide_banner -filters", "subtitles") != 0
                ? "FFmpeg lacks the libass subtitles filter; the native pipeline test is blocked."
                : null;
        }
        catch (Exception)
        {
            return "FFmpeg/FFprobe are unavailable; the native pipeline test is blocked.";
        }
    }

    private static int Run(string file, string arguments, string? mustContain = null)
    {
        using var process = Process.Start(new ProcessStartInfo(file, arguments)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false
        })!;
        var output = process.StandardOutput.ReadToEnd();
        process.StandardError.ReadToEnd();
        process.WaitForExit(20_000);
        if (process.ExitCode != 0)
        {
            return process.ExitCode;
        }

        return mustContain is null || output.Contains(mustContain, StringComparison.Ordinal) ? 0 : 1;
    }
}
