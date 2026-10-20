using ContentFactory.Contracts;

namespace ContentFactory.Core.Abstractions;

public interface IScriptPlanner
{
    Task<ScriptDocument> GenerateAsync(ScriptRequest request, CancellationToken ct);
}
