using ContentFactory.Contracts;
using ContentFactory.Core.Abstractions;
using ContentFactory.Core.Exceptions;
using ContentFactory.Infrastructure.Storage;

namespace ContentFactory.Infrastructure.Media;

/// <summary>
/// Concatenates phrase audio into one scene-level WAV using FFmpeg's filter graph. Inputs
/// are copied to random temp paths, passed through ArgumentList, resampled to 48 kHz stereo
/// and stored as an immutable audio asset. A single phrase is returned unchanged.
/// </summary>
public sealed class FfmpegAudioConcatenator : IAudioConcatenator
{
    private readonly IAssetStore _assets;
    private readonly StorageOptions _storage;
    private readonly SafeProcessRunner _runner;
    private readonly TimeSpan _timeout;

    public FfmpegAudioConcatenator(
        IAssetStore assets,
        StorageOptions storage,
        SafeProcessRunner? runner = null,
        TimeSpan? timeout = null)
    {
        _assets = assets ?? throw new ArgumentNullException(nameof(assets));
        _storage = storage ?? throw new ArgumentNullException(nameof(storage));
        _runner = runner ?? new SafeProcessRunner();
        _timeout = timeout ?? TimeSpan.FromMinutes(5);

        if (_timeout <= TimeSpan.Zero || _timeout > TimeSpan.FromMinutes(30))
        {
            throw new ValidationException(
                "VOICE_CONCAT_CONFIG_INVALID",
                "The audio concatenation timeout must be between zero and thirty minutes.");
        }
    }

    public async Task<AssetRef> ConcatenateAsync(
        IReadOnlyList<AssetRef> parts,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(parts);

        if (parts.Count == 0)
        {
            throw new ValidationException(
                "VOICE_CONCAT_INPUT_REQUIRED",
                "At least one audio phrase is required.");
        }

        if (parts.Any(part => part.Kind != AssetKind.Audio))
        {
            throw new ValidationException(
                "VOICE_CONCAT_INPUT_INVALID",
                "Only audio assets can be concatenated.");
        }

        if (parts.Count == 1)
        {
            return parts[0];
        }

        var tempDirectory = Path.Combine(_storage.TempRoot, "voice-concat");
        Directory.CreateDirectory(tempDirectory);
        var token = Guid.NewGuid().ToString("N");
        var inputPaths = new List<string>(parts.Count);
        var outputPath = Path.Combine(tempDirectory, token + ".wav");

        try
        {
            for (var index = 0; index < parts.Count; index++)
            {
                var inputPath = Path.Combine(tempDirectory, token + "-" + index + ".audio");
                inputPaths.Add(inputPath);
                await using var input = await _assets.OpenReadAsync(parts[index].Id, ct);
                await using var output = new FileStream(
                    inputPath,
                    FileMode.CreateNew,
                    FileAccess.Write,
                    FileShare.None,
                    81920,
                    FileOptions.Asynchronous | FileOptions.SequentialScan);
                await input.CopyToAsync(output, ct);
            }

            var filters = string.Join(
                ";",
                inputPaths.Select((_, index) =>
                    $"[{index}:a]aresample=48000,aformat=sample_fmts=s16:channel_layouts=stereo[a{index}]"));
            var concatInputs = string.Concat(
                inputPaths.Select((_, index) => $"[a{index}]"));
            var filterGraph =
                $"{filters};{concatInputs}concat=n={inputPaths.Count}:v=0:a=1[out]";

            var args = new List<string>
            {
                "-hide_banner",
                "-nostdin",
                "-y",
                "-loglevel", "error"
            };
            foreach (var inputPath in inputPaths)
            {
                args.Add("-i");
                args.Add(inputPath);
            }

            args.Add("-filter_complex");
            args.Add(filterGraph);
            args.Add("-map");
            args.Add("[out]");
            args.Add("-c:a");
            args.Add("pcm_s16le");
            args.Add("-ar");
            args.Add("48000");
            args.Add("-ac");
            args.Add("2");
            args.Add(outputPath);

            var result = await _runner.RunAsync("ffmpeg", args, _timeout, ct);
            if (result.TimedOut)
            {
                throw new ValidationException(
                    "VOICE_CONCAT_TIMEOUT",
                    "FFmpeg did not finish concatenating narration within the timeout.");
            }

            if (result.ExitCode != 0 || !File.Exists(outputPath))
            {
                throw new ValidationException(
                    "VOICE_CONCAT_FAILED",
                    "FFmpeg could not concatenate the narration phrases.");
            }

            await using var stream = new FileStream(
                outputPath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                81920,
                FileOptions.Asynchronous | FileOptions.SequentialScan);
            return await _assets.PutAsync(stream, AssetKind.Audio, "audio/wav", ct);
        }
        finally
        {
            foreach (var inputPath in inputPaths)
            {
                TryDelete(inputPath);
            }

            TryDelete(outputPath);
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (IOException)
        {
            // The temp cleaner can retry.
        }
        catch (UnauthorizedAccessException)
        {
            // The temp cleaner can retry.
        }
    }
}
