namespace ContentFactory.Core.Exceptions;

/// <summary>
/// A live approval envelope is missing, expired, out of scope, malformed or exhausted.
/// The paid path must fail closed and hold or release no money without evidence.
/// </summary>
public sealed class ApprovalStaleException : Exception
{
    public ApprovalStaleException(string code, string message)
        : base(message)
    {
        Code = code;
    }

    public string Code { get; }
}
