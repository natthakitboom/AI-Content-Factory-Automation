using System.Text.Json;
using ContentFactory.Contracts;
using ContentFactory.Core.Campaigns;
using ContentFactory.Infrastructure.Providers.Mock;

namespace ContentFactory.UnitTests;

/// <summary>
/// T08 strict script checks. The validator is independent of any provider and rejects
/// schema/business problems before TTS or video budget can be reserved.
/// </summary>
public sealed class ScriptValidationTests
{
    private readonly ScriptValidator _validator = new();

    [Fact]
    public void ValidScriptPasses()
    {
        var product = CreateProduct();
        var script = CreateScript(product);

        var report = _validator.Validate(script, product);

        Assert.True(report.Passed, Describe(report));
        Assert.Empty(report.Errors);
    }

    [Fact]
    public void UnknownClaimIdRejected()
    {
        var product = CreateProduct();
        var scenes = CreateScenes(product);
        scenes[1] = scenes[1] with { ClaimIds = [Guid.NewGuid()] };
        var script = CreateScript(product, scenes);

        var report = _validator.Validate(script, product);

        Assert.False(report.Passed);
        Assert.Contains(
            report.Errors,
            issue => issue.Code == "UNKNOWN_CLAIM_ID" && issue.Path == "scenes[1].claimIds[0]");
    }

    [Fact]
    public void DuplicateSceneIdsRejected()
    {
        var product = CreateProduct();
        var scenes = CreateScenes(product);
        scenes[1] = scenes[1] with { SceneId = scenes[0].SceneId };
        var script = CreateScript(product, scenes);

        var report = _validator.Validate(script, product);

        Assert.False(report.Passed);
        Assert.Contains(
            report.Errors,
            issue => issue.Code == "DUPLICATE_SCENE_ID" && issue.Path == "scenes[1].sceneId");
    }

    [Fact]
    public void BenefitSceneWithoutClaimRejected()
    {
        var product = CreateProduct();
        var scenes = CreateScenes(product);
        scenes[1] = scenes[1] with { ClaimIds = [] };
        var script = CreateScript(product, scenes);

        var report = _validator.Validate(script, product);

        Assert.False(report.Passed);
        Assert.Contains(
            report.Errors,
            issue => issue.Code == "SCENE_CLAIMS_REQUIRED" && issue.Path == "scenes[1].claimIds");
    }

    [Fact]
    public void SceneCountOutsideRangeRejected()
    {
        var product = CreateProduct();
        var script = CreateScript(product, CreateScenes(product).Take(2).ToArray());

        var report = _validator.Validate(script, product);

        Assert.False(report.Passed);
        Assert.Contains(report.Errors, issue => issue.Code == "SCENE_COUNT_INVALID");
    }

    [Fact]
    public void MultipleMotionScenesRejected()
    {
        var product = CreateProduct();
        var scenes = CreateScenes(product);
        scenes[0] = scenes[0] with { MotionPrompt = "camera push in" };
        scenes[1] = scenes[1] with { MotionPrompt = "camera orbit" };
        var script = CreateScript(product, scenes);

        var report = _validator.Validate(script, product);

        Assert.False(report.Passed);
        Assert.Contains(report.Errors, issue => issue.Code == "MOTION_SCENE_LIMIT");
    }

    [Fact]
    public void MarkupInCaptionRejected()
    {
        var product = CreateProduct();
        var scenes = CreateScenes(product);
        scenes[1] = scenes[1] with { Caption = "<b>ลดราคา</b>" };
        var script = CreateScript(product, scenes);

        var report = _validator.Validate(script, product);

        Assert.False(report.Passed);
        Assert.Contains(
            report.Errors,
            issue => issue.Code == "PLAIN_TEXT_REQUIRED" && issue.Path == "scenes[1].caption");
    }

    [Fact]
    public void UnknownAssetRejected()
    {
        var product = CreateProduct();
        var scenes = CreateScenes(product);
        scenes[1] = scenes[1] with { PreferredAssetId = Guid.NewGuid() };
        var script = CreateScript(product, scenes);

        var report = _validator.Validate(script, product);

        Assert.False(report.Passed);
        Assert.Contains(report.Errors, issue => issue.Code == "UNKNOWN_ASSET_ID");
    }

    [Fact]
    public async Task MockPlannerProducesValidatorApprovedScript()
    {
        var product = CreateProduct();
        var planner = new MockScriptPlanner();
        var request = new ScriptRequest(
            Guid.NewGuid(),
            product,
            product.Locale,
            VideoMode.HybridMotion,
            25000,
            "script-planner.v1");

        var script = await planner.GenerateAsync(request, CancellationToken.None);
        var report = _validator.Validate(script, product);

        Assert.True(report.Passed, Describe(report));
        Assert.Equal(request.RevisionId, script.RevisionId);
        Assert.InRange(script.Scenes.Count, 3, 6);
        Assert.Single(script.Scenes, scene => !string.IsNullOrWhiteSpace(scene.MotionPrompt));
    }

    [Fact]
    public void PromptAndSchemaFilesMatchD2()
    {
        var root = RepositoryRoot();
        var prompt = File.ReadAllText(Path.Combine(
            root,
            "media",
            "prompts",
            "script-planner.v1.txt"));
        Assert.Contains("untrusted", prompt, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("3", prompt, StringComparison.Ordinal);
        Assert.Contains("6", prompt, StringComparison.Ordinal);
        Assert.Contains("claimIds", prompt, StringComparison.Ordinal);
        Assert.Contains("JSON", prompt, StringComparison.Ordinal);

        var schemaPath = Path.Combine(root, "media", "schemas", "script-document.v1.schema.json");
        using var schema = JsonDocument.Parse(File.ReadAllText(schemaPath));
        var rootElement = schema.RootElement;

        Assert.Equal(1, rootElement.GetProperty("properties")
            .GetProperty("schemaVersion").GetProperty("const").GetInt32());
        Assert.Equal(3, rootElement.GetProperty("properties")
            .GetProperty("scenes").GetProperty("minItems").GetInt32());
        Assert.Equal(6, rootElement.GetProperty("properties")
            .GetProperty("scenes").GetProperty("maxItems").GetInt32());

        var roles = rootElement.GetProperty("$defs")
            .GetProperty("scene")
            .GetProperty("properties")
            .GetProperty("role")
            .GetProperty("enum")
            .EnumerateArray()
            .Select(value => value.GetString() ?? string.Empty)
            .ToArray();
        Assert.Equal(new[] { "hook", "benefit", "demo", "cta" }, roles);
    }

    private static ProductSnapshot CreateProduct()
    {
        var claimA = new ApprovedClaim(
            Guid.NewGuid(),
            "ช่วยให้ผิวชุ่มชื้นตลอดวัน",
            "owner-sheet:claim-a");
        var claimB = new ApprovedClaim(
            Guid.NewGuid(),
            "เนื้อบางเบาและไม่เหนียวเหนอะหนะ",
            "owner-sheet:claim-b");
        var asset = new AssetRef(
            Guid.NewGuid(),
            new string('a', 64),
            "assets/sha256/aa/" + new string('a', 64) + ".jpg",
            AssetKind.Image,
            "image/jpeg",
            1024,
            null);

        return new ProductSnapshot(
            Guid.NewGuid(),
            1,
            "ครีมบำรุงผิวทดสอบ",
            "TH",
            "th-TH",
            [claimA, claimB],
            [asset],
            DateTimeOffset.UtcNow);
    }

    private static ScriptScene[] CreateScenes(ProductSnapshot product)
        =>
        [
            new("hook", "hook", "อยากมีผิวชุ่มชื้นตลอดวันไหม?", "อยากมีผิวชุ่มชื้นตลอดวันไหม?", [], null, null),
            new(
                "benefit",
                "benefit",
                "ช่วยให้ผิวชุ่มชื้นตลอดวัน",
                "ช่วยให้ผิวชุ่มชื้นตลอดวัน",
                [product.Claims[0].Id],
                product.Assets[0].Id,
                null),
            new("cta", "cta", "ดูรายละเอียดสินค้าได้เลย", "ดูรายละเอียดสินค้าได้เลย", [], null, null)
        ];

    private static ScriptDocument CreateScript(
        ProductSnapshot product,
        IReadOnlyList<ScriptScene>? scenes = null)
        => new(
            1,
            Guid.NewGuid(),
            product.Locale,
            "ครีมบำรุงผิวทดสอบ",
            "โฆษณา",
            "ผิวชุ่มชื้นตลอดวัน",
            scenes ?? CreateScenes(product));

    private static string Describe(ValidationReport report)
        => string.Join(
            "; ",
            report.Errors.Select(issue => $"{issue.Code}@{issue.Path}: {issue.Message}"));

    private static string RepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "ContentFactory.slnx")))
            {
                return directory.FullName;
            }

            directory = directory.Parent;
        }

        throw new InvalidOperationException("Could not locate repository root.");
    }
}
