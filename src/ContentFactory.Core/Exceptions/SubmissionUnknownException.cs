namespace ContentFactory.Core.Exceptions;

/// <summary>
/// Thrown when a cloud provider accepted or may have accepted a request but the local
/// adapter cannot prove the final outcome. The caller must hold the budget reservation
/// and reconcile with the provider instead of blindly retrying the billable POST.
/// </summary>
public sealed class SubmissionUnknownException : Exception
{
    public SubmissionUnknownException(string message)
        : base(message)
    {
        Code = "SUBMISSION_UNKNOWN";
    }

    public SubmissionUnknownException(string message, Exception innerException)
        : base(message, innerException)
    {
        Code = "SUBMISSION_UNKNOWN";
    }

    public string Code { get; }
}
