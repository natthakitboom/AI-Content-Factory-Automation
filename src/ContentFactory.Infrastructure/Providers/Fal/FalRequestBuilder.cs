using System.Buffers;
using System.Text;
using System.Text.Json;
using ContentFactory.Contracts;
using ContentFactory.Core.Abstractions;
using ContentFactory.Core.Exceptions;
using SkiaSharp;

namespace ContentFactory.Infrastructure.Providers.Fal;

/// <summary>
/// Builds the exact D6 request body for fal-ai/wan/v2.2-a14b/image-to-video/turbo. It
/// normalizes the source image to a JPEG/PNG data URI, strips metadata, caps the long
/// edge at 1280 px and the pre-base64 size at 3 MiB. The output is capped at 5 MiB.
/// </summary>
public sealed class FalRequestBuilder
{
    private readonly IAssetStore _assets;
    private readonly FalOptions _options;

    public FalRequestBuilder(IAssetStore assets, FalOptions options)
    {
        _assets = assets ?? throw new ArgumentNullException(nameof(assets));
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _options.Validate();
    }

    public async Task<string> BuildJsonAsync(
        VideoGenerationRequest request,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (request.SourceImage.Kind != AssetKind.Image)
        {
            throw new ValidationException(
                "FAL_SOURCE_NOT_IMAGE",
                "The source asset for fal image-to-video must be an image.");
        }

        await using var input = await _assets.OpenReadAsync(request.SourceImage.Id, ct);
        var imageBytes = await ReadAllAsync(input, _options.ImageMaxBytesBeforeBase64 * 2, ct);

        var normalized = NormalizeImage(imageBytes);
        if (normalized.Length > _options.ImageMaxBytesBeforeBase64)
        {
            throw new ValidationException(
                "FAL_IMAGE_TOO_LARGE",
                "The normalized source image exceeds the 3 MiB pre-base64 cap.");
        }

        var dataUri = $"data:image/jpeg;base64,{Convert.ToBase64String(normalized)}";
        var body = new
        {
            image_url = dataUri,
            prompt = request.Prompt,
            resolution = request.Resolution,
            aspect_ratio = request.AspectRatio,
            enable_safety_checker = true,
            enable_output_safety_checker = true,
            enable_prompt_expansion = false,
            acceleration = "regular",
            video_quality = "high",
            video_write_mode = "balanced"
        };

        var json = JsonSerializer.Serialize(body, JsonDefaults.Options);
        if (Encoding.UTF8.GetByteCount(json) > _options.RequestBodyCapBytes)
        {
            throw new ValidationException(
                "FAL_BODY_TOO_LARGE",
                "The fal request body exceeds the 5 MiB cap.");
        }

        return json;
    }

    private byte[] NormalizeImage(ReadOnlyMemory<byte> source)
    {
        using var bitmap = SKBitmap.Decode(source.ToArray());
        if (bitmap is null)
        {
            throw new ValidationException(
                "FAL_IMAGE_DECODE_FAILED",
                "The source image could not be decoded.");
        }

        var longEdge = Math.Max(bitmap.Width, bitmap.Height);
        if (longEdge > _options.ImageMaxLongEdge)
        {
            var scale = (double)_options.ImageMaxLongEdge / longEdge;
            var newWidth = (int)Math.Round(bitmap.Width * scale);
            var newHeight = (int)Math.Round(bitmap.Height * scale);
            using var resized = bitmap.Resize(
                new SKImageInfo(newWidth, newHeight),
                SKSamplingOptions.Default);
            if (resized is null)
            {
                throw new ValidationException(
                    "FAL_IMAGE_RESIZE_FAILED",
                    "The source image could not be resized.");
            }

            return EncodeJpeg(resized);
        }

        return EncodeJpeg(bitmap);
    }

    private byte[] EncodeJpeg(SKBitmap bitmap)
    {
        using var stream = new MemoryStream();
        if (!bitmap.Encode(stream, SKEncodedImageFormat.Jpeg, _options.JpegQuality))
        {
            throw new ValidationException(
                "FAL_IMAGE_ENCODE_FAILED",
                "The source image could not be encoded as JPEG.");
        }

        return stream.ToArray();
    }

    private static async Task<byte[]> ReadAllAsync(
        Stream input,
        long maxBytes,
        CancellationToken ct)
    {
        using var memory = new MemoryStream();
        var buffer = ArrayPool<byte>.Shared.Rent(81920);
        try
        {
            while (true)
            {
                var read = await input.ReadAsync(buffer.AsMemory(0, buffer.Length), ct);
                if (read == 0)
                {
                    break;
                }

                if (memory.Length + read > maxBytes)
                {
                    throw new ValidationException(
                        "FAL_IMAGE_TOO_LARGE",
                        "The source image is too large to send to fal.");
                }

                memory.Write(buffer, 0, read);
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }

        return memory.ToArray();
    }
}
