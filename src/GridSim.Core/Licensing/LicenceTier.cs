namespace GridSim.Core.Licensing;

/// <summary>
/// The software-licence tier a key grants, per <c>LICENSE.md</c>. This is entitlement,
/// deliberately distinct from the DNO electricity-distribution "licence area" concept
/// (EMIDS/WMIDS/SWEST/...) used by the GDA model builder - that is grid data, this is a
/// commercial grant. Keep the two apart: this type never uses the spelling "Licence".
/// </summary>
public enum LicenceTier
{
    /// <summary>§3 Personal, free, non-commercial, capped at 1,500 buses.</summary>
    Personal,

    /// <summary>§4 Academic research &amp; teaching, free, 1,500 buses (larger on request).</summary>
    Academic,

    /// <summary>§5 Commercial Code Licence, no bus limit.</summary>
    CommercialCode,

    /// <summary>§6 Commercial Deployment Licence, signed agreement, no bus limit.</summary>
    CommercialDeployment,

    /// <summary>§7 Network Operator (TSO/ESO/DNO), nominal site-wide fee, no bus limit.</summary>
    NetworkOperator,
}

/// <summary>Wire-code and display helpers for <see cref="LicenceTier"/>.</summary>
public static class LicenceTierCodes
{
    /// <summary>The compact token embedded in a licence key's payload.</summary>
    public static string ToWire(this LicenceTier tier) => tier switch
    {
        LicenceTier.Personal => "personal",
        LicenceTier.Academic => "academic",
        LicenceTier.CommercialCode => "code",
        LicenceTier.CommercialDeployment => "deployment",
        LicenceTier.NetworkOperator => "operator",
        _ => throw new ArgumentOutOfRangeException(nameof(tier), tier, "Unknown licence tier."),
    };

    /// <summary>Parse the compact wire token back to a tier. Returns false for anything unrecognised.</summary>
    public static bool TryFromWire(string wire, out LicenceTier tier)
    {
        switch (wire)
        {
            case "personal": tier = LicenceTier.Personal; return true;
            case "academic": tier = LicenceTier.Academic; return true;
            case "code": tier = LicenceTier.CommercialCode; return true;
            case "deployment": tier = LicenceTier.CommercialDeployment; return true;
            case "operator": tier = LicenceTier.NetworkOperator; return true;
            default: tier = LicenceTier.Personal; return false;
        }
    }

    /// <summary>Human-readable tier name for CLI/UI status lines.</summary>
    public static string DisplayName(this LicenceTier tier) => tier switch
    {
        LicenceTier.Personal => "Personal",
        LicenceTier.Academic => "Academic",
        LicenceTier.CommercialCode => "Commercial Code",
        LicenceTier.CommercialDeployment => "Commercial Deployment",
        LicenceTier.NetworkOperator => "Network Operator",
        _ => tier.ToString(),
    };
}
