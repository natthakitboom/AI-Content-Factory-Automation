using ContentFactory.Core.Exceptions;

namespace ContentFactory.Core.Assets;

/// <summary>
/// Rights are metadata, not part of the content hash. A file being found online (or copied
/// from a marketplace) never proves advertising use is allowed. Production and cloud use
/// require an explicit owner attestation with a source reference.
/// </summary>
public static class AssetRightsPolicy
{
    public const string Unknown = "unknown";
    public const string OwnerAttested = "owner_attested";
    public const string Rejected = "rejected";

    public static string Normalize(string? rightsStatus)
    {
        if (string.IsNullOrWhiteSpace(rightsStatus))
        {
            return Unknown;
        }

        var normalized = rightsStatus.Trim().ToLowerInvariant();
        return normalized switch
        {
            Unknown => Unknown,
            OwnerAttested => OwnerAttested,
            Rejected => Rejected,
            _ => throw new ValidationException(
                "ASSET_RIGHTS_INVALID",
                "Rights status must be unknown, owner_attested or rejected.")
        };
    }

    public static void EnsureUsableInProduction(string? rightsStatus, string? sourceReference)
    {
        if (!string.Equals(Normalize(rightsStatus), OwnerAttested, StringComparison.Ordinal)
            || string.IsNullOrWhiteSpace(sourceReference))
        {
            throw new ValidationException(
                "ASSET_RIGHTS_REQUIRED",
                "Production use requires owner-attested rights and a source reference.");
        }
    }

    public static void EnsureCloudUseAllowed(string? rightsStatus, bool cloudUseAllowed)
    {
        if (cloudUseAllowed
            && !string.Equals(Normalize(rightsStatus), OwnerAttested, StringComparison.Ordinal))
        {
            throw new ValidationException(
                "ASSET_CLOUD_CONSENT_REQUIRED",
                "Cloud use requires owner-attested rights.");
        }
    }
}
