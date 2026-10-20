using System.Text;
using ContentFactory.Contracts;
using ContentFactory.Core.Exceptions;
using ContentFactory.Infrastructure.Storage;

namespace ContentFactory.UnitTests;

/// <summary>
/// T03 upload-boundary tests. A user filename is only a label; every disk path is derived
/// from the content hash. These tests never touch PostgreSQL or the network.
/// </summary>
public sealed class AssetPathTests
{
    private static readonly byte[] JpegBytes =
    [
        0xFF, 0xD8, 0xFF, 0xE0, 0x00, 0x10, 0x4A, 0x46, 0x49, 0x46, 0x00, 0x01,
        0x01, 0x00, 0x00, 0x01, 0x00, 0x01, 0x00, 0x00, 0xFF, 0xD9
    ];

    private static readonly StorageOptions Limits = new() { DataRoot = Path.GetTempPath() };

    [Fact]
    public void TraversalAndSymlinkEscapeRejected()
    {
        Assert.False(TryUpload("../etc/passwd", AssetKind.Image, "image/jpeg", JpegBytes).Accepted);
        Assert.False(TryUpload("..\\windows\\system32", AssetKind.Image, "image/jpeg", JpegBytes).Accepted);
        Assert.False(TryUpload("/tmp/absolute.jpg", AssetKind.Image, "image/jpeg", JpegBytes).Accepted);
        Assert.False(TryUpload("folder/photo.jpg", AssetKind.Image, "image/jpeg", JpegBytes).Accepted);
        Assert.False(TryUpload("folder\\photo.jpg", AssetKind.Image, "image/jpeg", JpegBytes).Accepted);
        Assert.False(TryUpload("C:\\photo.jpg", AssetKind.Image, "image/jpeg", JpegBytes).Accepted);

        using var sandbox = new SandboxDirectory();
        var dataRoot = Path.Combine(sandbox.Path, "data");
        Directory.CreateDirectory(dataRoot);

        var outside = Path.Combine(sandbox.Path, "outside.txt");
        File.WriteAllText(outside, "secret");

        var fileLink = Path.Combine(dataRoot, "escape-file");
        File.CreateSymbolicLink(fileLink, outside);

        var dirLink = Path.Combine(dataRoot, "escape-dir");
        Directory.CreateSymbolicLink(dirLink, sandbox.Path);

        Assert.Throws<ValidationException>(
            () => AssetPathResolver.ResolveExisting(dataRoot, "escape-file"));
        Assert.Throws<ValidationException>(
            () => AssetPathResolver.ResolveExisting(dataRoot, "escape-dir/outside.txt"));
        Assert.Throws<ValidationException>(
            () => AssetPathResolver.ResolveExisting(dataRoot, "../outside.txt"));
    }

    [Fact]
    public void ThaiFilenameDoesNotBecomeDiskPath()
    {
        const string thaiName = "รูปสินค้า 01 \"ทดสอบ\";.jpg";

        var result = TryUpload(thaiName, AssetKind.Image, "image/jpeg", JpegBytes);

        Assert.True(result.Accepted, result.Code);
        Assert.True(AssetPathResolver.IsSafeUploadName(thaiName));

        var hash = new string('a', 64);
        var relativePath = AssetPathResolver.RelativePathFor(hash, AssetKind.Image, "image/jpeg");

        Assert.Equal($"assets/sha256/aa/{hash}.jpg", relativePath);
        Assert.DoesNotContain("รูป", relativePath, StringComparison.Ordinal);
        Assert.DoesNotContain("ทดสอบ", relativePath, StringComparison.Ordinal);
        Assert.DoesNotContain(";", relativePath, StringComparison.Ordinal);
        Assert.DoesNotContain("\"", relativePath, StringComparison.Ordinal);
    }

    [Fact]
    public void SvgDisguisedAsJpegRejected()
    {
        var svg = Encoding.UTF8.GetBytes(
            "<svg xmlns=\"http://www.w3.org/2000/svg\"><script>alert(1)</script></svg>");
        var html = Encoding.UTF8.GetBytes("<!doctype html><html><body>x</body></html>");
        var executable = new byte[] { 0x7F, 0x45, 0x4C, 0x46, 0x02, 0x01, 0x01, 0x00 };

        Assert.False(TryUpload("photo.jpg", AssetKind.Image, "image/jpeg", svg).Accepted);
        Assert.False(TryUpload("photo.jpg", AssetKind.Image, "image/jpeg", html).Accepted);
        Assert.False(TryUpload("photo.jpg", AssetKind.Image, "image/jpeg", executable).Accepted);
        Assert.False(TryUpload("photo.svg", AssetKind.Image, "image/svg+xml", svg).Accepted);
    }

    [Fact]
    public void OversizedUploadRejected()
    {
        var result = TryUpload(
            "huge.jpg",
            AssetKind.Image,
            "image/jpeg",
            JpegBytes,
            declaredBytes: StorageOptions.MaxImageBytes + 1);

        Assert.False(result.Accepted);
        Assert.Equal("ASSET_TOO_LARGE", result.Code);
    }

    [Fact]
    public void MimeMismatchRejected()
    {
        var png = new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 0x00, 0x00 };

        var result = TryUpload("photo.jpg", AssetKind.Image, "image/jpeg", png);

        Assert.False(result.Accepted);
        Assert.Equal("ASSET_MIME_MISMATCH", result.Code);
    }

    [Fact]
    public void UnsafeKindMismatchRejected()
    {
        var result = TryUpload("clip.bin", AssetKind.Video, "video/mp4", JpegBytes);

        Assert.False(result.Accepted);
        Assert.Equal("ASSET_KIND_MISMATCH", result.Code);
    }

    [Fact]
    public void WebVttAndSubRipKeepDistinctMimeTypes()
    {
        var webVtt = "WEBVTT\n\n00:00.000 --> 00:01.000\nสวัสดี"u8;
        var subRip = "1\n00:00:00,000 --> 00:00:01,000\nสวัสดี"u8;

        Assert.Equal("text/vtt", MimeSniffer.Sniff(webVtt)?.Mime);
        Assert.Equal("application/x-subrip", MimeSniffer.Sniff(subRip)?.Mime);
    }

    private static UploadOutcome TryUpload(
        string fileName,
        AssetKind kind,
        string mime,
        byte[] bytes,
        long? declaredBytes = null,
        StorageOptions? options = null)
    {
        try
        {
            AssetUploadPolicy.ValidateHeader(fileName, kind, mime, bytes);
            AssetUploadPolicy.EnsureWithinLimit(declaredBytes ?? bytes.LongLength, kind, options ?? Limits);
            return new UploadOutcome(true, null);
        }
        catch (ValidationException exception)
        {
            return new UploadOutcome(false, exception.Code);
        }
    }

    private sealed record UploadOutcome(bool Accepted, string? Code);

    private sealed class SandboxDirectory : IDisposable
    {
        public string Path { get; } = System.IO.Path.Combine(
            System.IO.Path.GetTempPath(),
            "cf-asset-path-" + Guid.NewGuid().ToString("N"));

        public SandboxDirectory()
        {
            Directory.CreateDirectory(Path);
        }

        public void Dispose()
        {
            try
            {
                Directory.Delete(Path, recursive: true);
            }
            catch (IOException)
            {
                // Best-effort cleanup for a test sandbox.
            }
        }
    }
}
