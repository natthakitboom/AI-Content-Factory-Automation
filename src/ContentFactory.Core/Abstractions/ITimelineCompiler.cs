using ContentFactory.Contracts;

namespace ContentFactory.Core.Abstractions;

public interface ITimelineCompiler
{
    RenderManifest Compile(Guid videoVersionId, ScriptDocument script,
        IReadOnlyDictionary<string, AssetRef> visuals,
        IReadOnlyList<VoiceSegment> voices, string templateVersion);
}
