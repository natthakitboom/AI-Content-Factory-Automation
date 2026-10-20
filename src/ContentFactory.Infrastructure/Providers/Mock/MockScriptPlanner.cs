using ContentFactory.Contracts;
using ContentFactory.Core.Abstractions;

namespace ContentFactory.Infrastructure.Providers.Mock;

/// <summary>
/// Deterministic, offline planner used by development and tests. It never contacts a
/// provider or reads a paid credential.
/// </summary>
public sealed class MockScriptPlanner : IScriptPlanner
{
    public Task<ScriptDocument> GenerateAsync(ScriptRequest request, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);
        ct.ThrowIfCancellationRequested();

        var product = request.Product;
        var firstClaim = product.Claims.FirstOrDefault();
        var secondClaim = product.Claims.Skip(1).FirstOrDefault() ?? firstClaim;
        var assetId = product.Assets.FirstOrDefault()?.Id;
        var scenes = new List<ScriptScene>
        {
            new(
                "hook",
                "hook",
                "อยากมีผิวชุ่มชื้นตลอดวันไหม?",
                "อยากมีผิวชุ่มชื้นตลอดวันไหม?",
                Array.Empty<Guid>(),
                null,
                null),
            new(
                "benefit",
                "benefit",
                firstClaim?.Text ?? "สินค้าที่คัดสรรเพื่อการดูแลผิว",
                firstClaim?.Text ?? "สินค้าที่คัดสรรเพื่อการดูแลผิว",
                firstClaim is null ? Array.Empty<Guid>() : new[] { firstClaim.Id },
                assetId,
                null)
        };

        if (request.Mode == VideoMode.HybridMotion)
        {
            scenes.Add(new ScriptScene(
                "demo",
                "demo",
                secondClaim?.Text ?? "เนื้อบางเบาและใช้ง่าย",
                secondClaim?.Text ?? "เนื้อบางเบาและใช้ง่าย",
                secondClaim is null ? Array.Empty<Guid>() : new[] { secondClaim.Id },
                assetId,
                "slow camera push toward the product, soft studio light"));
        }

        scenes.Add(new ScriptScene(
            "cta",
            "cta",
            "ดูรายละเอียดสินค้าได้เลย",
            "ดูรายละเอียดสินค้าได้เลย",
            Array.Empty<Guid>(),
            null,
            null));

        var document = new ScriptDocument(
            1,
            request.RevisionId,
            request.Locale,
            product.Title,
            "โฆษณา",
            string.Join(" ", scenes.Select(scene => scene.Caption)),
            scenes);

        return Task.FromResult(document);
    }
}
