namespace ContentFactory.Core.Exceptions;

/// <summary>
/// A business or input validation failure with a stable machine-readable code.
/// API responses expose the code, never a stack trace or secret.
/// </summary>
public sealed class ValidationException : Exception
{
    public ValidationException(string code, string message)
        : base(message)
    {
        Code = code;
    }

    public string Code { get; }
}
