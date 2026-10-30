namespace GridSim.Core.Licensing;

/// <summary>
/// A decoded and cryptographically verified licence. Instances only ever come from a valid,
/// signed key (via <see cref="LicenceKey.TryParse(string, out Entitlement, out string)"/>) or
/// from <see cref="FreeDefault"/> - never constructed from untrusted input directly at a gate.
/// </summary>
/// <param name="Email">The licensed individual's email (the "EmailAddress" the key encodes).</param>
/// <param name="Org">The licensed organisation (the "ORG" the key encodes); empty for a personal key.</param>
/// <param name="Tier">The commercial tier (the "TYPE" the key encodes).</param>
/// <param name="BusLimit">Max buses per network model. <c>0</c> means unlimited.</param>
/// <param name="Issued">Issue date (UTC).</param>
/// <param name="Expires">Expiry date (UTC), inclusive (the "EXPIRES" the key encodes).</param>
/// <param name="LicenceId">Short opaque id, printed in status output for support/provenance.</param>
public sealed record Entitlement(
    string Email,
    string Org,
    LicenceTier Tier,
    int BusLimit,
    DateOnly Issued,
    DateOnly Expires,
    string LicenceId)
{
    /// <summary>The free Personal-tier bus cap (LICENSE.md §3).</summary>
    public const int FreeBusLimit = 1500;

    /// <summary>
    /// F27 (Ruling R1, 2026-10-04): the GridSim major version this licence was issued for. LICENSE.md section 1 sells a
    /// licence per major version, so a key issued for 1 works on every 1.x build and is refused on 2.x (see
    /// <see cref="LicenceGate"/>). A <c>v2</c> key carries it signed in its payload; a <c>v1</c> key (issued before the
    /// binding existed) reads as <see cref="LicenceKey.LegacyMajorVersion"/>, 1, the line it was issued on.
    /// When an entitlement is built in code without naming it (the key forge, tests), it defaults to the major version
    /// of the GridSim.Core build doing the building, so a forge issues keys for its own line unless told otherwise.
    /// </summary>
    public int MajorVersion { get; init; } = BuildInfo.MajorVersion;

    /// <summary>True when this tier carries no bus cap (commercial and operator tiers encode <c>0</c>).</summary>
    public bool IsUnlimited => BusLimit <= 0;

    /// <summary>The cap to compare a model's bus count against; <see cref="int.MaxValue"/> when unlimited.</summary>
    public int EffectiveBusLimit => IsUnlimited ? int.MaxValue : BusLimit;

    /// <summary>True once today's UTC date is past <see cref="Expires"/>.</summary>
    public bool IsExpired => DateOnly.FromDateTime(DateTime.UtcNow) > Expires;

    /// <summary>
    /// D-DOC-086 (October 2026): true while today's UTC date is before <see cref="Issued"/>, the day the licence starts.
    /// A key's issue date is signed into it but was never checked, so a future-dated key applied at once. Such a key is
    /// now treated like an expired one until its day comes: the free tier applies (<see cref="LicenceGate.Check"/>) and
    /// the status line says so (<see cref="LicenceGate.Describe"/>). The issue day itself counts (inclusive, as
    /// <see cref="Expires"/> is). The free default's issue date is the earliest date, so it is never "not yet valid".
    /// </summary>
    public bool IsNotYetValid => DateOnly.FromDateTime(DateTime.UtcNow) < Issued;

    /// <summary>
    /// The entitlement used when no key is installed: free Personal tier, 1,500-bus cap, never expires.
    /// This is the honest default - GridSim runs unlicensed at the Personal tier, it does not lock out.
    /// </summary>
    public static Entitlement FreeDefault { get; } = new(
        Email: string.Empty,
        Org: string.Empty,
        Tier: LicenceTier.Personal,
        BusLimit: FreeBusLimit,
        Issued: default,
        Expires: DateOnly.MaxValue,
        LicenceId: "free");
}
