using System.Text.Json;
using ContentFactory.Contracts;

namespace ContentFactory.UnitTests;

public sealed class ContractSerializationTests
{
    [Fact]
    public void EnumValuesSerializeAsCamelCaseStrings()
    {
        var json = JsonSerializer.Serialize(
            new { mode = VideoMode.TemplateOnly, lane = JobLane.HeavyLocal },
            JsonDefaults.Options);

        Assert.Contains("\"mode\":\"templateOnly\"", json, StringComparison.Ordinal);
        Assert.Contains("\"lane\":\"heavyLocal\"", json, StringComparison.Ordinal);
    }

    [Fact]
    public void ProductSnapshotUsesCamelCaseAndRoundTrips()
    {
        var snapshot = new ProductSnapshot(
            Guid.NewGuid(),
            3,
            "สินค้าทดสอบ",
            "TH",
            "th-TH",
            [new ApprovedClaim(Guid.NewGuid(), "ข้อความที่อนุมัติ", "source-1")],
            [new AssetRef(Guid.NewGuid(), new string('a', 64), "assets/sha256/aa/file.jpg",
                AssetKind.Image, "image/jpeg", 1234, null)],
            new DateTimeOffset(2026, 10, 20, 21, 0, 0, TimeSpan.FromHours(7)));

        var json = JsonSerializer.Serialize(snapshot, JsonDefaults.Options);

        Assert.Contains("\"productId\":", json, StringComparison.Ordinal);
        Assert.Contains("\"capturedAt\":", json, StringComparison.Ordinal);
        Assert.Contains("\"kind\":\"image\"", json, StringComparison.Ordinal);
        Assert.Contains("สินค้าทดสอบ", json, StringComparison.Ordinal);

        var reloaded = JsonSerializer.Deserialize<ProductSnapshot>(json, JsonDefaults.Options);
        Assert.NotNull(reloaded);
        Assert.Equal(snapshot.Version, reloaded!.Version);
        Assert.Equal(snapshot.Claims.Single().Text, reloaded.Claims.Single().Text);
        Assert.Equal(snapshot.Assets.Single().Kind, reloaded.Assets.Single().Kind);
    }
}
