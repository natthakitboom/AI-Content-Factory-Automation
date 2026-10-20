using ContentFactory.Contracts;

namespace ContentFactory.Core.Campaigns;

/// <summary>
/// Optional trace returned by a planner that can report the exact prompt version, model
/// and token usage observed in the provider response. Nothing is invented if LM Studio
/// omits usage.
/// </summary>
public sealed record ScriptPlanTrace(
    string PromptVersion,
    string ModelId,
    int? PromptTokens,
    int? CompletionTokens,
    int RepairRequests);

public sealed record ScriptPlanResult(ScriptDocument Script, ScriptPlanTrace Trace);
