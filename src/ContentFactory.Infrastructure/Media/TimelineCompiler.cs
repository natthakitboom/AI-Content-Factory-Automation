using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using ContentFactory.Contracts;
using ContentFactory.Core.Abstractions;
using ContentFactory.Core.Exceptions;

namespace ContentFactory.Infrastructure.Media;

/// <summary>
/// Converts scene durations to an integer 30 fps frame grid before cumulative times are
/// calculated. Every segment therefore starts and ends on a frame boundary even when the
/// measured narration duration is not an exact multiple of a frame.
/// </summary>
public static class FrameGrid
{
    public static long RoundUpFrames(long durationMs, int fps)
    {
        if (durationMs <= 0)
        {
            throw new ValidationException(
                "TIMELINE_DURATION_INVALID",
                "A scene duration must be positive.");
        }

        if (fps <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(fps));
        }

        var frames = (durationMs * fps + 999) / 1000;
        return frames < 1 ? 1 : frames;
    }

    public static long StartMs(long frame, int fps)
    {
        if (frame < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(frame));
        }

        if (fps <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(fps));
        }

        return (long)Math.Round(
            frame * 1000d / fps,
            MidpointRounding.AwayFromZero);
    }

    public static long DurationMs(long startFrame, long frameCount, int fps)
        => StartMs(startFrame + frameCount, fps) - StartMs(startFrame, fps);
}

public sealed class TimelineOptions
{
    public int Width { get; init; } = ProductBasicTemplate.Width;

    public int Height { get; init; } = ProductBasicTemplate.Height;

    public int Fps { get; init; } = ProductBasicTemplate.Fps;

    public long CaptionPaddingMs { get; init; } = ProductBasicTemplate.CaptionPaddingMs;

    public long MinDurationMs { get; init; } = ProductBasicTemplate.MinDurationMs;

    public long MaxDurationMs { get; init; } = ProductBasicTemplate.MaxDurationMs;

    public string RendererVersion { get; init; } = ProductBasicTemplate.RendererVersion;

    public string FontFingerprint { get; init; } = string.Empty;

    internal void Validate()
    {
        if (Width <= 0 || Height <= 0 || Fps <= 0)
        {
            throw new ValidationException(
                "TIMELINE_CONFIG_INVALID",
                "Timeline width, height and fps must be positive.");
        }

        if (CaptionPaddingMs < 0)
        {
            throw new ValidationException(
                "TIMELINE_CONFIG_INVALID",
                "Caption padding cannot be negative.");
        }

        if (MinDurationMs <= 0 || MaxDurationMs < MinDurationMs)
        {
            throw new ValidationException(
                "TIMELINE_CONFIG_INVALID",
                "Timeline duration bounds are invalid.");
        }

        if (string.IsNullOrWhiteSpace(RendererVersion) ||
            !FontFingerprintIdentity.IsSha256(FontFingerprint))
        {
            throw new ValidationException(
                "TIMELINE_CONFIG_INVALID",
                "A renderer version and a SHA-256 font fingerprint are required for approval identity.");
        }
    }
}

/// <summary>
/// Builds the immutable render input from approved script text, content-addressed visuals
/// and measured voice segments. The script is hashed canonically; the compiler never reads
/// files or trusts text as a path. Missing narration or visuals fail closed.
/// </summary>
public sealed class TimelineCompiler : ITimelineCompiler
{
    private static readonly HashSet<string> AllowedRoles =
        new(StringComparer.Ordinal) { "hook", "benefit", "demo", "cta" };

    private readonly TimelineOptions _options;

    public TimelineCompiler(TimelineOptions? options = null)
    {
        _options = options ?? new TimelineOptions();
        _options.Validate();
    }

    public RenderManifest Compile(
        Guid videoVersionId,
        ScriptDocument script,
        IReadOnlyDictionary<string, AssetRef> visuals,
        IReadOnlyList<VoiceSegment> voices,
        string templateVersion)
    {
        ArgumentNullException.ThrowIfNull(script);
        ArgumentNullException.ThrowIfNull(visuals);
        ArgumentNullException.ThrowIfNull(voices);

        if (videoVersionId == Guid.Empty)
        {
            throw new ValidationException(
                "TIMELINE_VIDEO_VERSION_REQUIRED",
                "A video version id is required before a render manifest can be compiled.");
        }

        if (string.IsNullOrWhiteSpace(templateVersion))
        {
            throw new ValidationException(
                "TIMELINE_TEMPLATE_REQUIRED",
                "A template version is required.");
        }

        if (script.Scenes is null || script.Scenes.Count == 0)
        {
            throw new ValidationException(
                "TIMELINE_SCENES_REQUIRED",
                "A script with at least one scene is required.");
        }

        var voiceByScene = BuildVoiceMap(script, voices);
        var ctaCount = 0;
        var startFrame = 0L;
        var seenSceneIds = new HashSet<string>(StringComparer.Ordinal);
        var segments = new List<TimelineSegment>(script.Scenes.Count);

        for (var index = 0; index < script.Scenes.Count; index++)
        {
            var scene = script.Scenes[index];
            if (scene is null)
            {
                throw new ValidationException(
                    "TIMELINE_SCENE_REQUIRED",
                    "Every timeline scene must be present.");
            }

            if (string.IsNullOrWhiteSpace(scene.SceneId) ||
                !seenSceneIds.Add(scene.SceneId))
            {
                throw new ValidationException(
                    "TIMELINE_SCENE_ID_INVALID",
                    "Every timeline scene needs a unique non-empty scene id.");
            }

            if (!AllowedRoles.Contains(scene.Role))
            {
                throw new ValidationException(
                    "TIMELINE_ROLE_INVALID",
                    "Scene role must be hook, benefit, demo or cta.");
            }

            if (string.IsNullOrWhiteSpace(scene.Narration))
            {
                throw new ValidationException(
                    "TIMELINE_NARRATION_REQUIRED",
                    "Every scene requires narration before rendering.");
            }

            if (string.IsNullOrWhiteSpace(scene.Caption))
            {
                throw new ValidationException(
                    "TIMELINE_CAPTION_REQUIRED",
                    "Every scene requires a plain-text caption.");
            }

            if (scene.Role == "cta")
            {
                ctaCount++;
            }

            var visual = ResolveVisual(scene, visuals);
            if (visual.Kind is not (AssetKind.Image or AssetKind.Video))
            {
                throw new ValidationException(
                    "TIMELINE_VISUAL_INVALID",
                    "Timeline visuals must be images or videos.");
            }

            if (!voiceByScene.TryGetValue(scene.SceneId, out var voice))
            {
                throw new ValidationException(
                    "TIMELINE_VOICE_REQUIRED",
                    "Every scene requires a measured narration segment.");
            }

            if (voice.Audio is null ||
                voice.Audio.Kind != AssetKind.Audio ||
                voice.DurationMs <= 0)
            {
                throw new ValidationException(
                    "TIMELINE_VOICE_INVALID",
                    "Narration must be a positive-duration audio asset.");
            }

            var requestedDuration = voice.DurationMs + _options.CaptionPaddingMs;
            var frameCount = FrameGrid.RoundUpFrames(requestedDuration, _options.Fps);
            var startMs = FrameGrid.StartMs(startFrame, _options.Fps);
            var endFrame = startFrame + frameCount;
            var endMs = FrameGrid.StartMs(endFrame, _options.Fps);

            segments.Add(new TimelineSegment(
                scene.SceneId,
                startMs,
                endMs - startMs,
                visual,
                voice.Audio,
                scene.Caption.Trim(),
                ResolveTransform(scene, visual, index)));

            startFrame = endFrame;
        }

        if (ctaCount == 0)
        {
            throw new ValidationException(
                "TIMELINE_CTA_REQUIRED",
                "The product-basic template requires one call-to-action scene.");
        }

        var totalMs = FrameGrid.StartMs(startFrame, _options.Fps);
        if (totalMs < _options.MinDurationMs || totalMs > _options.MaxDurationMs)
        {
            throw new ValidationException(
                "TIMELINE_DURATION_OUT_OF_BOUNDS",
                "The rendered timeline must be between 15 and 35 seconds.");
        }

        return new RenderManifest(
            SchemaVersion: 1,
            videoVersionId,
            ComputeScriptHash(script),
            templateVersion,
            _options.RendererVersion,
            _options.FontFingerprint,
            _options.Width,
            _options.Height,
            _options.Fps,
            segments);
    }

    private static Dictionary<string, VoiceSegment> BuildVoiceMap(
        ScriptDocument script,
        IReadOnlyList<VoiceSegment> voices)
    {
        var sceneIds = script.Scenes
            .Where(scene => scene is not null)
            .Select(scene => scene.SceneId)
            .ToHashSet(StringComparer.Ordinal);
        var result = new Dictionary<string, VoiceSegment>(StringComparer.Ordinal);

        foreach (var voice in voices)
        {
            if (voice is null)
            {
                throw new ValidationException(
                    "TIMELINE_VOICE_INVALID",
                    "Narration segments cannot be null.");
            }

            if (!sceneIds.Contains(voice.SceneId))
            {
                throw new ValidationException(
                    "TIMELINE_VOICE_UNKNOWN",
                    "A narration segment does not belong to this script.");
            }

            if (!result.TryAdd(voice.SceneId, voice))
            {
                throw new ValidationException(
                    "TIMELINE_VOICE_AMBIGUOUS",
                    "A scene has more than one narration segment.");
            }
        }

        return result;
    }

    private static AssetRef ResolveVisual(
        ScriptScene scene,
        IReadOnlyDictionary<string, AssetRef> visuals)
    {
        if (visuals.TryGetValue(scene.SceneId, out var byScene) && byScene is not null)
        {
            return byScene;
        }

        if (scene.PreferredAssetId is { } preferred)
        {
            var key = preferred.ToString("D");
            if (visuals.TryGetValue(key, out var byAsset) && byAsset is not null)
            {
                return byAsset;
            }
        }

        throw new ValidationException(
            "TIMELINE_VISUAL_REQUIRED",
            "Every scene requires an approved local visual.");
    }

    private static string ResolveTransform(
        ScriptScene scene,
        AssetRef visual,
        int index)
    {
        if (visual.Kind == AssetKind.Video)
        {
            return "motion:fit";
        }

        return scene.Role switch
        {
            "hook" => index % 2 == 0 ? "pan:right" : "pan:left",
            "benefit" => "zoom:in",
            "demo" => "still",
            "cta" => "cta:zoom",
            _ => "still"
        };
    }

    private static string ComputeScriptHash(ScriptDocument script)
    {
        var json = JsonSerializer.Serialize(script, JsonDefaults.Options);
        var canonical = ManifestHasher.CanonicalizeJson(json);
        return Convert.ToHexString(
                SHA256.HashData(Encoding.UTF8.GetBytes(canonical)))
            .ToLowerInvariant();
    }
}
