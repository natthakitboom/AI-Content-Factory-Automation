using System.Text.Json;
using ContentFactory.Contracts;

namespace ContentFactory.Core.Jobs;

/// <summary>Stable stage identities from the approved D1 pipeline.</summary>
public enum WorkflowStage
{
    ValidateProduct,
    PlanScript,
    PrepareVisuals,
    SynthesizeVoice,
    SubmitVideo,
    PollVideo,
    DownloadVideo,
    CompileTimeline,
    RenderVideo,
    TechnicalQa,
    PrepareReview,
    Approve,
    Export,
    RecordPublication
}

/// <summary>
/// One code-defined stage. <see cref="RequiresCloud"/> stages are skipped entirely in
/// template-only mode and are recorded as not required by later stages. Stages with
/// <see cref="Automatic"/> false are owner actions, not worker jobs.
/// </summary>
public sealed record WorkflowStageDefinition(
    WorkflowStage Stage,
    string JobType,
    JobLane Lane,
    bool RequiresCloud,
    bool Automatic);

/// <summary>
/// The single source of truth for worker stage order. An LLM may choose script content,
/// but it never chooses this graph, a lane or whether a cloud stage is permitted.
/// </summary>
public static class WorkflowDefinition
{
    public const string StageSucceededEvent = "stage_succeeded";

    public static IReadOnlyList<WorkflowStageDefinition> OrderedStages { get; } =
    [
        new(WorkflowStage.ValidateProduct, "validate_product", JobLane.Control, false, true),
        new(WorkflowStage.PlanScript, "plan_script", JobLane.HeavyLocal, false, true),
        new(WorkflowStage.PrepareVisuals, "prepare_visuals", JobLane.HeavyLocal, false, true),
        new(WorkflowStage.SynthesizeVoice, "synthesize_voice", JobLane.HeavyLocal, false, true),
        new(WorkflowStage.SubmitVideo, "submit_video", JobLane.CloudSubmit, true, true),
        new(WorkflowStage.PollVideo, "poll_video", JobLane.CloudPoll, true, true),
        new(WorkflowStage.DownloadVideo, "download_video", JobLane.Download, true, true),
        new(WorkflowStage.CompileTimeline, "compile_timeline", JobLane.HeavyLocal, false, true),
        new(WorkflowStage.RenderVideo, "render_video", JobLane.HeavyLocal, false, true),
        new(WorkflowStage.TechnicalQa, "technical_qa", JobLane.HeavyLocal, false, true),
        new(WorkflowStage.PrepareReview, "prepare_review", JobLane.Control, false, true),
        new(WorkflowStage.Approve, "approve", JobLane.Control, false, false),
        new(WorkflowStage.Export, "export", JobLane.Control, false, false),
        new(WorkflowStage.RecordPublication, "record_publication", JobLane.Control, false, false)
    ];

    private static readonly Dictionary<string, WorkflowStageDefinition> ByJobType =
        OrderedStages.ToDictionary(stage => stage.JobType, StringComparer.Ordinal);

    public static IReadOnlyList<WorkflowStageDefinition> StagesFor(VideoMode mode)
    {
        if (mode is not (VideoMode.TemplateOnly or VideoMode.HybridMotion))
        {
            throw new ArgumentOutOfRangeException(nameof(mode), mode, "Unknown video mode.");
        }

        return OrderedStages
            .Where(stage => !stage.RequiresCloud || mode == VideoMode.HybridMotion)
            .ToArray();
    }

    public static WorkflowStageDefinition? FindByJobType(string jobType)
        => ByJobType.TryGetValue(jobType, out var stage) ? stage : null;

    /// <summary>
    /// Cloud-only stages that are absent in this mode. They are persisted in the stage
    /// outbox payload as <c>not_required</c>; no fake provider success is written.
    /// </summary>
    public static IReadOnlyList<string> NotRequiredStagesFor(VideoMode mode)
        => OrderedStages
            .Where(stage => stage.RequiresCloud && mode == VideoMode.TemplateOnly)
            .Select(stage => stage.JobType)
            .ToArray();

    /// <summary>
    /// Returns the next automatic stage for this mode. Cloud stages are absent from the
    /// template-only sequence, so synthesize_voice moves straight to compile_timeline.
    /// A null result means the next action belongs to the owner or the run is complete.
    /// </summary>
    public static string? NextJobType(string currentJobType, VideoMode mode)
    {
        var stages = StagesFor(mode);
        var index = -1;
        for (var candidate = 0; candidate < stages.Count; candidate++)
        {
            if (string.Equals(stages[candidate].JobType, currentJobType, StringComparison.Ordinal))
            {
                index = candidate;
                break;
            }
        }

        if (index < 0)
        {
            return null;
        }

        for (var candidate = index + 1; candidate < stages.Count; candidate++)
        {
            if (stages[candidate].Automatic)
            {
                return stages[candidate].JobType;
            }
        }

        return null;
    }

    /// <summary>
    /// Reads the mode from persisted job payloads. T05 wraps ScriptRequest under
    /// <c>scriptRequest</c>; later stages may carry <c>mode</c> at the root. Unknown or
    /// malformed values return null so a caller never silently skips a cloud stage.
    /// </summary>
    public static VideoMode? TryReadMode(string? payloadJson)
    {
        if (string.IsNullOrWhiteSpace(payloadJson))
        {
            return null;
        }

        try
        {
            using var document = JsonDocument.Parse(payloadJson);
            if (document.RootElement.ValueKind != JsonValueKind.Object)
            {
                return null;
            }

            if (TryReadModeProperty(document.RootElement, out var mode))
            {
                return mode;
            }

            foreach (var wrapper in new[] { "scriptRequest", "request" })
            {
                if (document.RootElement.TryGetProperty(wrapper, out var nested)
                    && nested.ValueKind == JsonValueKind.Object
                    && TryReadModeProperty(nested, out mode))
                {
                    return mode;
                }
            }
        }
        catch (JsonException)
        {
            return null;
        }

        return null;
    }

    public static string ToDbMode(VideoMode mode) => mode switch
    {
        VideoMode.TemplateOnly => "template_only",
        VideoMode.HybridMotion => "hybrid_motion",
        _ => throw new ArgumentOutOfRangeException(nameof(mode), mode, "Unknown video mode.")
    };

    private static bool TryReadModeProperty(JsonElement element, out VideoMode mode)
    {
        mode = default;
        if (!element.TryGetProperty("mode", out var property)
            || property.ValueKind != JsonValueKind.String)
        {
            return false;
        }

        switch (property.GetString())
        {
            case "template_only":
            case "templateOnly":
                mode = VideoMode.TemplateOnly;
                return true;
            case "hybrid_motion":
            case "hybridMotion":
                mode = VideoMode.HybridMotion;
                return true;
            default:
                return false;
        }
    }
}
