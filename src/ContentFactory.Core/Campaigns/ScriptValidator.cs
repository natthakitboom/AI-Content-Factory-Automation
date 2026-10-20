using System.Text.RegularExpressions;
using ContentFactory.Contracts;
using ContentFactory.Core.Abstractions;

namespace ContentFactory.Core.Campaigns;

/// <summary>
/// Strict, provider-independent validation for the D2 script contract. The planner may
/// suggest text, but only this validator decides whether the script may move on to voice
/// or video work.
/// </summary>
public sealed class ScriptValidator : IScriptValidator
{
    private const int MinScenes = 3;
    private const int MaxScenes = 6;
    private const int MaxTitleLength = 512;
    private const int MaxDisclosureLength = 1024;
    private const int MaxCaptionTextLength = 4096;
    private const int MaxSceneIdLength = 64;
    private const int MaxNarrationLength = 4000;
    private const int MaxCaptionLength = 1000;
    private const int MaxMotionPromptLength = 1000;

    private static readonly Regex MarkupRegex = new(
        "<\\s*/?\\s*[A-Za-z][^>]*>",
        RegexOptions.CultureInvariant | RegexOptions.Compiled,
        TimeSpan.FromMilliseconds(100));

    private static readonly Regex AssTagRegex = new(
        "\\{\\\\[^}]+\\}",
        RegexOptions.CultureInvariant | RegexOptions.Compiled,
        TimeSpan.FromMilliseconds(100));

    private static readonly HashSet<string> AllowedRoles =
        new(StringComparer.Ordinal) { "hook", "benefit", "demo", "cta" };

    public ValidationReport Validate(ScriptDocument script, ProductSnapshot product)
    {
        ArgumentNullException.ThrowIfNull(script);
        ArgumentNullException.ThrowIfNull(product);

        var errors = new List<ValidationIssue>();

        if (script.SchemaVersion != 1)
        {
            Add(errors, "SCHEMA_VERSION_INVALID", "schemaVersion", "schemaVersion must be 1.");
        }

        if (script.RevisionId == Guid.Empty)
        {
            Add(errors, "REVISION_ID_REQUIRED", "revisionId", "revisionId must be a non-empty UUID.");
        }

        if (!string.Equals(script.Locale, product.Locale, StringComparison.OrdinalIgnoreCase))
        {
            Add(errors, "LOCALE_MISMATCH", "locale", "The script locale must match the product locale.");
        }

        CheckPlainText(errors, script.Title, "title", MaxTitleLength, required: true);
        CheckPlainText(errors, script.Disclosure, "disclosure", MaxDisclosureLength, required: true);
        CheckPlainText(errors, script.CaptionText, "captionText", MaxCaptionTextLength, required: true);

        var scenes = script.Scenes;
        if (scenes is null)
        {
            Add(errors, "SCENES_REQUIRED", "scenes", "scenes must be an array.");
            return Report(errors);
        }

        if (scenes.Count is < MinScenes or > MaxScenes)
        {
            Add(
                errors,
                "SCENE_COUNT_INVALID",
                "scenes",
                $"scenes must contain between {MinScenes} and {MaxScenes} items.");
        }

        var knownClaims = product.Claims.Select(claim => claim.Id).ToHashSet();
        var knownAssets = product.Assets.Select(asset => asset.Id).ToHashSet();
        var seenSceneIds = new HashSet<string>(StringComparer.Ordinal);
        var motionSceneCount = 0;

        for (var index = 0; index < scenes.Count; index++)
        {
            var scene = scenes[index];
            if (scene is null)
            {
                Add(errors, "SCENE_REQUIRED", $"scenes[{index}]", "Scene must be an object.");
                continue;
            }

            var scenePath = $"scenes[{index}]";
            CheckSceneId(errors, scene.SceneId, scenePath, seenSceneIds);

            if (!AllowedRoles.Contains(scene.Role))
            {
                Add(
                    errors,
                    "ROLE_INVALID",
                    $"{scenePath}.role",
                    "role must be one of hook, benefit, demo or cta.");
            }

            CheckPlainText(
                errors,
                scene.Narration,
                $"{scenePath}.narration",
                MaxNarrationLength,
                required: true);
            CheckPlainText(
                errors,
                scene.Caption,
                $"{scenePath}.caption",
                MaxCaptionLength,
                required: true);

            var claimIds = scene.ClaimIds;
            if (claimIds is null)
            {
                Add(errors, "CLAIM_IDS_REQUIRED", $"{scenePath}.claimIds", "claimIds must be an array.");
            }
            else
            {
                for (var claimIndex = 0; claimIndex < claimIds.Count; claimIndex++)
                {
                    if (!knownClaims.Contains(claimIds[claimIndex]))
                    {
                        Add(
                            errors,
                            "UNKNOWN_CLAIM_ID",
                            $"{scenePath}.claimIds[{claimIndex}]",
                            "Every claimId must reference an approved product claim.");
                    }
                }

                if (scene.Role is "benefit" or "demo" && claimIds.Count == 0)
                {
                    Add(
                        errors,
                        "SCENE_CLAIMS_REQUIRED",
                        $"{scenePath}.claimIds",
                        "benefit and demo scenes must cite at least one approved product claim.");
                }
            }

            if (scene.PreferredAssetId is { } assetId && !knownAssets.Contains(assetId))
            {
                Add(
                    errors,
                    "UNKNOWN_ASSET_ID",
                    $"{scenePath}.preferredAssetId",
                    "preferredAssetId must reference an asset attached to the product.");
            }

            if (!string.IsNullOrWhiteSpace(scene.MotionPrompt))
            {
                motionSceneCount++;
                CheckPlainText(
                    errors,
                    scene.MotionPrompt,
                    $"{scenePath}.motionPrompt",
                    MaxMotionPromptLength,
                    required: false);

                if (motionSceneCount > 1)
                {
                    Add(
                        errors,
                        "MOTION_SCENE_LIMIT",
                        $"{scenePath}.motionPrompt",
                        "At most one scene may use motionPrompt.");
                }
            }
        }

        return Report(errors);
    }

    private static void CheckSceneId(
        ICollection<ValidationIssue> errors,
        string sceneId,
        string scenePath,
        ISet<string> seenSceneIds)
    {
        if (string.IsNullOrWhiteSpace(sceneId))
        {
            Add(errors, "SCENE_ID_REQUIRED", $"{scenePath}.sceneId", "sceneId is required.");
            return;
        }

        if (sceneId.Length > MaxSceneIdLength)
        {
            Add(
                errors,
                "SCENE_ID_TOO_LONG",
                $"{scenePath}.sceneId",
                $"sceneId must be at most {MaxSceneIdLength} characters.");
        }

        if (!seenSceneIds.Add(sceneId))
        {
            Add(errors, "DUPLICATE_SCENE_ID", $"{scenePath}.sceneId", "sceneId must be unique.");
        }
    }

    private static void CheckPlainText(
        ICollection<ValidationIssue> errors,
        string? value,
        string path,
        int maxLength,
        bool required)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            if (required)
            {
                Add(errors, "TEXT_REQUIRED", path, "Text is required.");
            }

            return;
        }

        if (value.Length > maxLength)
        {
            Add(errors, "TEXT_TOO_LONG", path, $"Text must be at most {maxLength} characters.");
        }

        if (ContainsMarkup(value) || ContainsControlCharacters(value))
        {
            Add(
                errors,
                "PLAIN_TEXT_REQUIRED",
                path,
                "Text must be plain text without markup, SSML, ASS tags or control characters.");
        }
    }

    private static bool ContainsMarkup(string value)
        => MarkupRegex.IsMatch(value) || AssTagRegex.IsMatch(value);

    private static bool ContainsControlCharacters(string value)
    {
        foreach (var character in value)
        {
            if (char.IsControl(character) &&
                character is not '\n' and not '\r' and not '\t')
            {
                return true;
            }
        }

        return false;
    }

    private static void Add(
        ICollection<ValidationIssue> errors,
        string code,
        string path,
        string message)
        => errors.Add(new ValidationIssue(code, path, message));

    private static ValidationReport Report(IReadOnlyList<ValidationIssue> errors)
        => new(errors.Count == 0, errors, Array.Empty<ValidationIssue>());
}
