namespace ContentFactory.Core.Exceptions;

/// <summary>
/// The signed media URL was refused (401/403/404/410), which for provider output usually
/// means it expired. The caller refreshes the URL from the existing request; it never
/// resubmits paid work.
/// </summary>
public sealed class MediaUrlExpiredException : Exception
{
    public MediaUrlExpiredException(string message)
        : base(message)
    {
    }
}
