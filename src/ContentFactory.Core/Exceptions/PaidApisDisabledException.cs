namespace ContentFactory.Core.Exceptions;

/// <summary>
/// Paid APIs are disabled by configuration. This guard wins over every budget and
/// approval value, including an otherwise valid owner envelope.
/// </summary>
public sealed class PaidApisDisabledException : Exception
{
    public PaidApisDisabledException()
        : base("Paid APIs are disabled. Set ALLOW_PAID_APIS=true only inside an owner-approved live envelope.")
    {
    }

    public string Code => "PAID_APIS_DISABLED";
}
