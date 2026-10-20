namespace ContentFactory.Core.Exceptions;

/// <summary>
/// A referenced record does not exist. The API maps this to 404 with a stable code and
/// never reveals internal paths or database details.
/// </summary>
public sealed class NotFoundException : Exception
{
    public NotFoundException(string code, string message)
        : base(message)
    {
        Code = code;
    }

    public string Code { get; }
}
