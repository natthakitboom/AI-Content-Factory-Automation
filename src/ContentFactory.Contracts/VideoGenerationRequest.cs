namespace ContentFactory.Contracts;

public sealed record VideoGenerationRequest(
    string OperationId, Guid RevisionId, string SceneId,
    AssetRef SourceImage, string Prompt, string Resolution,
    string AspectRatio, Guid PriceSnapshotId);
