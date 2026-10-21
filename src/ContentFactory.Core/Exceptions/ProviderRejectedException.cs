namespace ContentFactory.Core.Exceptions;

/// <summary>
/// Thrown when the cloud provider responded with a definitive client error. A rejected
/// submission is not retried blindly; the budget reservation can be released because the
/// provider confirmed it will not charge.
/// </summary>
public sealed class ProviderRejectedException : Exception
{
    public ProviderRejectedException(string code, string message)
        : base(message)
    {
        Code = code;
    }

    public string Code { get; }
}
