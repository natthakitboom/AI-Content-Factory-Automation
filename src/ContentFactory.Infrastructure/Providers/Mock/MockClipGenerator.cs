using ContentFactory.Infrastructure.Media;

namespace ContentFactory.Infrastructure.Providers.Mock;

/// <summary>
/// Produces the mock "AI motion" clip locally with FFmpeg's synthetic test source. The
/// clip contains no real product imagery, so nothing needs to be committed to the repo.
/// </summary>
public static class MockClipGenerator
{
    public static async Task<byte[]> GenerateAsync(IProcessRunner runner, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(runner);

        var directory = Path.Combine(
            Path.GetTempPath(), "cf-mock-clip", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var output = Path.Combine(directory, "clip.mp4");
            var result = await runner.RunAsync(
                "ffmpeg",
                [
                    "-hide_banner", "-nostdin", "-y", "-loglevel", "error",
                    "-f", "lavfi", "-i", "testsrc2=size=720x1280:rate=30:duration=3",
                    "-c:v", "libx264", "-pix_fmt", "yuv420p", "-movflags", "+faststart",
                    output
                ],
                TimeSpan.FromMinutes(2),
                ct);
            if (result.ExitCode != 0)
            {
                throw new InvalidOperationException(
                    "FFmpeg could not generate the mock clip: " + result.StandardError);
            }

            return await File.ReadAllBytesAsync(output, ct);
        }
        finally
        {
            try
            {
                Directory.Delete(directory, recursive: true);
            }
            catch (IOException)
            {
                // Best-effort cleanup of a temp directory.
            }
        }
    }
}
