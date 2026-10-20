using ContentFactory.Contracts;
using ContentFactory.Core.Exceptions;
using ContentFactory.Infrastructure.Media;

namespace ContentFactory.UnitTests;

/// <summary>
/// T04 manifest identity tests. The hash covers canonical JSON: object keys sorted
/// ordinally, array order preserved, enums and numbers in invariant form.
/// </summary>
public sealed class ManifestHashTests
{
    [Fact]
    public void SameManifestHasSameHash()
    {
        var manifest = CreateManifest();

        var first = ManifestHasher.Compute(manifest);
        var second = ManifestHasher.Compute(manifest);

        Assert.Equal(first, second);
        Assert.Matches("^[0-9a-f]{64}$", first);
    }

    [Fact]
    public void ChangedCaptionChangesHash()
    {
        var original = CreateManifest("คำบรรยายเดิม");
        var changed = CreateManifest("คำบรรยายใหม่");

        Assert.NotEqual(
            ManifestHasher.Compute(original),
            ManifestHasher.Compute(changed));
    }

    [Fact]
    public void KeyOrderDoesNotChangeHash()
    {
        const string original =
            """{"b":1,"a":{"y":true,"x":null},"list":[{"d":4,"c":3}]}""";
        const string reordered =
            """{"list":[{"c":3,"d":4}],"a":{"x":null,"y":true},"b":1}""";

        Assert.Equal(
            ManifestHasher.HashCanonicalJson(original),
            ManifestHasher.HashCanonicalJson(reordered));
    }

    [Fact]
    public void SegmentOrderChangesHash()
    {
        var first = CreateManifest();
        var second = CreateManifest();
        var reversed = second with
        {
            Segments = second.Segments.Reverse().ToArray()
        };

        Assert.NotEqual(
            ManifestHasher.Compute(first),
            ManifestHasher.Compute(reversed));
    }

    [Fact]
    public void CanonicalJsonPreservesThaiText()
    {
        var manifest = CreateManifest("สวัสดี ภาษาไทย");

        var canonical = ManifestHasher.Canonicalize(manifest);

        Assert.Contains("สวัสดี ภาษาไทย", canonical, StringComparison.Ordinal);
        Assert.DoesNotContain("\\u0e", canonical, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void InvalidJsonIsRejectedWithoutEchoingContent()
    {
        const string invalid = """{"secret":"do-not-echo-this",""";
        var exception = Assert.Throws<ValidationException>(
            () => ManifestHasher.HashCanonicalJson(invalid));

        Assert.Equal("MANIFEST_JSON_INVALID", exception.Code);
        Assert.DoesNotContain("do-not-echo-this", exception.Message, StringComparison.Ordinal);
    }

    private static RenderManifest CreateManifest(string caption = "สวัสดี")
    {
        var visual = new AssetRef(
            Guid.Parse("11111111-1111-1111-1111-111111111111"),
            new string('b', 64),
            $"assets/sha256/bb/{new string('b', 64)}.jpg",
            AssetKind.Image,
            "image/jpeg",
            1234,
            null);

        var first = new TimelineSegment(
            "scene-1",
            0,
            3000,
            visual,
            null,
            caption,
            "pan:left");

        var second = new TimelineSegment(
            "scene-2",
            3000,
            2500,
            visual,
            null,
            "ปิดท้าย",
            "zoom:in");

        return new RenderManifest(
            SchemaVersion: 1,
            VideoVersionId: Guid.Parse("22222222-2222-2222-2222-222222222222"),
            ScriptSha256: new string('a', 64),
            TemplateVersion: "template-1",
            RendererVersion: "renderer-1",
            FontFingerprint: "font-1",
            Width: 1080,
            Height: 1920,
            Fps: 30,
            Segments: [first, second]);
    }
}
