namespace ContentFactory.Core.Exceptions;

/// <summary>
/// The request conflicts with the current persisted state, for example when an
/// idempotency key is reused with a different request body or when product facts are not
/// approved. The API maps this to 409 with a stable code.
/// </summary>
public sealed class ConflictException : Exception
{
    public ConflictException(string code, string message)
        : base(message)
    {
        Code = code;
    }

    public string Code { get; }
}
