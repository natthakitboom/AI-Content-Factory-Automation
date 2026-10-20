using System.Text;
using ContentFactory.Contracts;

namespace ContentFactory.Infrastructure.Storage;

/// <summary>
/// Magic-byte detection. File extensions are never trusted, and SVG/HTML/executables are
/// rejected even when a caller labels them as images.
/// </summary>
public static class MimeSniffer
{
    public static SniffResult? Sniff(ReadOnlySpan<byte> header)
    {
        if (header.Length >= 3 && header[0] == 0xFF && header[1] == 0xD8 && header[2] == 0xFF)
        {
            return new SniffResult("image/jpeg", AssetKind.Image);
        }

        if (header.Length >= 8 && header[..8].SequenceEqual(new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A }))
        {
            return new SniffResult("image/png", AssetKind.Image);
        }

        if (header.Length >= 6 && (header[..6].SequenceEqual("GIF87a"u8) || header[..6].SequenceEqual("GIF89a"u8)))
        {
            return new SniffResult("image/gif", AssetKind.Image);
        }

        if (header.Length >= 12 && header[..4].SequenceEqual("RIFF"u8) && header[8..12].SequenceEqual("WEBP"u8))
        {
            return new SniffResult("image/webp", AssetKind.Image);
        }

        if (header.Length >= 12 && header[..4].SequenceEqual("RIFF"u8) && header[8..12].SequenceEqual("WAVE"u8))
        {
            return new SniffResult("audio/wav", AssetKind.Audio);
        }

        if (header.Length >= 12 && header[4..8].SequenceEqual("ftyp"u8))
        {
            var brand = Encoding.ASCII.GetString(header[8..12]);
            if (brand.StartsWith("M4A", StringComparison.Ordinal) || brand.StartsWith("M4B", StringComparison.Ordinal))
            {
                return new SniffResult("audio/mp4", AssetKind.Audio);
            }

            if (brand.StartsWith("qt", StringComparison.Ordinal))
            {
                return new SniffResult("video/quicktime", AssetKind.Video);
            }

            return new SniffResult("video/mp4", AssetKind.Video);
        }

        if (header.Length >= 3 && header[..3].SequenceEqual("ID3"u8))
        {
            return new SniffResult("audio/mpeg", AssetKind.Audio);
        }

        if (header.Length >= 2 && header[0] == 0xFF && (header[1] == 0xFB || header[1] == 0xF3 || header[1] == 0xF2))
        {
            return new SniffResult("audio/mpeg", AssetKind.Audio);
        }

        if (LooksLikeWebVtt(header))
        {
            return new SniffResult("text/vtt", AssetKind.Subtitle);
        }

        if (LooksLikeSubRip(header))
        {
            return new SniffResult("application/x-subrip", AssetKind.Subtitle);
        }

        return null;
    }

    private static bool LooksLikeWebVtt(ReadOnlySpan<byte> header)
    {
        var text = Encoding.UTF8.GetString(header);
        if (text.StartsWith('\uFEFF'))
        {
            text = text[1..];
        }

        return text.TrimStart().StartsWith("WEBVTT", StringComparison.Ordinal);
    }

    private static bool LooksLikeSubRip(ReadOnlySpan<byte> header)
    {
        var text = Encoding.UTF8.GetString(header);
        if (text.StartsWith('\uFEFF'))
        {
            text = text[1..];
        }

        var trimmed = text.TrimStart();
        var arrow = trimmed.IndexOf("-->", StringComparison.Ordinal);
        if (arrow < 0 || arrow > 200)
        {
            return false;
        }

        var beforeArrow = trimmed[..arrow].Trim();
        if (beforeArrow.Length == 0)
        {
            return false;
        }

        var lastLine = beforeArrow.Split('\n').Last().Trim();
        var timestampOnly = lastLine.Length > 0
            && lastLine.All(c => char.IsDigit(c) || c is ':' or ',' or '.');
        var hasColon = lastLine.Contains(':');

        return timestampOnly && (hasColon || beforeArrow.All(char.IsDigit));
    }

    public sealed record SniffResult(string Mime, AssetKind Kind);
}
