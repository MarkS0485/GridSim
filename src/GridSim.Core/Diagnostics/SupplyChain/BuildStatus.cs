namespace GridSim.Core.Diagnostics.SupplyChain;

/// <summary>
/// The build status a running GridSim reports beside its results: <see cref="Release"/> or <see cref="Unsupported"/>.
/// It is read from the <see cref="ComplianceAttestation"/> of the running build. A build is <see cref="Release"/> when
/// the binary identifies the exact release checkout it was built from:
/// <list type="bullet">
/// <item>it is a CI release build (<see cref="ComplianceAttestation.IsReleaseBuild"/>: the release workflow injected the
/// 64-hex SBOM hash with <c>-p:GridSimSbomSha256</c>, and the source tree was clean when it was built), and</item>
/// <item>the stamped commit is a full git object name (40 or 64 hexadecimal digits), so it names exactly one checkout.</item>
/// </list>
/// Every other build, a local or development build, or a build of changed source, is <see cref="Unsupported"/>.
///
/// <para>Compliance is a separate statement: <c>gridsim compliance verify</c> reports whether every in-scope control of
/// the compliance manifest is Achieved. A release build reads <see cref="Release"/> whatever that verdict is.</para>
///
/// <para>Like the build hash, the status is a statement the binary makes about itself from its build metadata, not an
/// integrity proof: whether a downloaded binary is the published one is checked outside it, against the SHA256SUMS
/// list, the list of published release commits and the signed build-provenance attestation of the Release.</para>
/// </summary>
public static class BuildStatus
{
    /// <summary>The status of a CI release build that names its exact release checkout.</summary>
    public const string Release = "RELEASE";

    /// <summary>The status of every other build.</summary>
    public const string Unsupported = "UNSUPPORTED BUILD";

    /// <summary>The status of the running build.</summary>
    public static string Current { get; } = Of(ComplianceAttestation.Current);

    /// <summary>The status a given attestation earns: <see cref="Release"/> when it is a release build whose stamped
    /// commit is a full git object name, else <see cref="Unsupported"/>. The compliance verdict does not enter it.</summary>
    public static string Of(ComplianceAttestation attestation)
    {
        ArgumentNullException.ThrowIfNull(attestation);
        return attestation.IsReleaseBuild && IsFullCommit(attestation.Commit) ? Release : Unsupported;
    }

    /// <summary>True when <paramref name="commit"/> is a full git object name: 40 hexadecimal digits (SHA-1) or 64
    /// (SHA-256), either case. A short or symbolic name does not identify one checkout.</summary>
    public static bool IsFullCommit(string? commit)
    {
        if (commit is null || (commit.Length != 40 && commit.Length != 64)) return false;
        foreach (char c in commit)
            if (!char.IsAsciiHexDigit(c)) return false;
        return true;
    }
}
