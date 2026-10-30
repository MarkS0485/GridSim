using System.Globalization;

namespace GridSim.Core.Diagnostics.SupplyChain;

/// <summary>
/// What a running GridSim binary states about its own build and control set (NCSC CAF A4.b). It binds the build
/// identity (<see cref="BuildProvenance"/>) to the compliance manifest (<see cref="ComplianceManifest"/>): the version,
/// the commit, the build hash (an identity label, not an integrity proof: see <see cref="BuildProvenance"/>), the SBOM
/// hash and the manifest hash, plus how many in-scope controls the manifest records as Achieved. A reviewer reads it
/// from a running binary (<c>gridsim compliance verify</c> or the web endpoint). Whether a downloaded binary is the
/// released one is checked outside it: the signed build-provenance attestation and SHA256SUMS of the Release.
///
/// <para><b>The verdict.</b> <see cref="IsCompliant"/> needs BOTH (1) a release build (<see cref="IsReleaseBuild"/>:
/// the release workflow injected the SBOM hash, which it does only after the full test matrix passed on every OS and
/// SIMD mode, so a release binary implies its evidencing tests were green) and (2) every in-scope control recorded as
/// Achieved. The manifest's statuses are maintained by hand in source, so (2) is an inventory, not a measurement: a
/// control whose product wiring is missing is recorded PartiallyAchieved, and the verdict then reads NOT COMPLIANT.
/// A local or development build never attests compliance, whatever the manifest says.</para>
/// </summary>
/// <param name="Version">The release version, for example <c>1.95.295</c>.</param>
/// <param name="Commit">The full git commit the build was made from, or <c>unknown</c>.</param>
/// <param name="BuildHash">The deterministic build hash from <see cref="BuildProvenance"/>.</param>
/// <param name="SbomSha256">The SHA-256 of the SBOM this build was produced against, or <c>unset</c> for a non-release build.</param>
/// <param name="ManifestHash">The hash of the compliance manifest this build embeds.</param>
/// <param name="InScopeControlCount">How many controls GridSim owns (are in scope).</param>
/// <param name="AchievedControlCount">How many of those are Achieved.</param>
/// <param name="IsReleaseBuild">True when an SBOM hash was injected (a release build).</param>
public sealed record ComplianceAttestation(
    string Version,
    string Commit,
    string BuildHash,
    string SbomSha256,
    string ManifestHash,
    int InScopeControlCount,
    int AchievedControlCount,
    bool IsReleaseBuild)
{
    /// <summary>The attestation of the currently running build.</summary>
    public static ComplianceAttestation Current { get; } = FromCurrent();

    /// <summary>True only for a release build whose every in-scope control is recorded Achieved (see the class
    /// summary). A local or development build is never compliant.</summary>
    public bool IsCompliant => IsReleaseBuild && InScopeControlCount == AchievedControlCount && InScopeControlCount > 0;

    private static ComplianceAttestation FromCurrent()
    {
        BuildProvenance p = BuildProvenance.Current;
        int inScope = ComplianceManifest.InScope.Count();
        int achieved = ComplianceManifest.InScope.Count(c => c.Status == ComplianceStatus.Achieved);
        return new ComplianceAttestation(
            p.Version, p.Commit, p.BuildHash, p.SbomSha256, ComplianceManifest.Hash,
            inScope, achieved, p.IsReleaseBuild);
    }

    /// <summary>A human-readable report of the attestation, one field per line. Used by the CLI verb and the web
    /// endpoint so both show the same thing.</summary>
    public string Report()
    {
        var ci = CultureInfo.InvariantCulture;
        string verdict = IsCompliant ? "COMPLIANT"
                       : !IsReleaseBuild ? "NOT ATTESTED (not a release build: only a release build can attest compliance)"
                       : "NOT COMPLIANT (in-scope controls not all Achieved)";
        string release = IsReleaseBuild ? "yes" : "no (local/dev build; SBOM hash not injected)";
        return string.Join('\n',
            $"GridSim compliance attestation",
            $"  version        {Version}",
            $"  commit         {Commit}",
            $"  build hash     {BuildHash}",
            $"  sbom sha256    {SbomSha256}",
            $"  manifest hash  {ManifestHash}",
            $"  release build  {release}",
            string.Create(ci, $"  controls       {AchievedControlCount}/{InScopeControlCount} in-scope controls Achieved"),
            $"  verdict        {verdict}")
            + NotAchievedLines();
    }

    // One line per in-scope control the manifest does not record as Achieved, with its status, so the report says
    // exactly what stands between this build and a compliant verdict.
    private static string NotAchievedLines()
    {
        var sb = new System.Text.StringBuilder();
        foreach (ComplianceControl c in ComplianceManifest.InScope)
            if (c.Status != ComplianceStatus.Achieved)
                sb.Append('\n').Append("  not achieved   ").Append(c.Id).Append(" (").Append(c.Principle).Append("): ")
                  .Append(c.Status).Append(" - ").Append(c.Description);
        return sb.ToString();
    }
}
