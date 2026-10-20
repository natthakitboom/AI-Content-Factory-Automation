namespace ContentFactory.Core.Exceptions;

/// <summary>
/// No verified and currently valid price snapshot exists for the requested provider,
/// model and configuration. Paid work must not start from a guessed or expired price.
/// </summary>
public sealed class PriceStaleException : Exception
{
    public PriceStaleException(string message)
        : base(message)
    {
    }

    public string Code => "PRICE_STALE";
}
