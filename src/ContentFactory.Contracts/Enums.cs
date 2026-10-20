namespace ContentFactory.Contracts;

public enum AssetKind
{
    Image,
    Audio,
    Video,
    Subtitle,
    Thumbnail,
    Manifest,
    Archive
}

public enum VideoMode
{
    TemplateOnly,
    HybridMotion
}

public enum JobLane
{
    Control,
    HeavyLocal,
    CloudSubmit,
    CloudPoll,
    Download
}

public enum ProviderRunStatus
{
    Submitted,
    InProgress,
    Succeeded,
    FailedConfirmed,
    SubmissionUnknown,
    CancellationRequested,
    CancelledConfirmed
}
