using System.Globalization;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;

namespace GridSim.Core.Diagnostics.SupplyChain;

/// <summary>
/// What a running GridSim binary can attest about how it was built (NCSC CAF A3 asset management, A4 supply
/// chain, and A4.b "secure software development and support", which at Achieved requires a supplier to attest to
/// the authenticity and integrity of its software). It correlates an emitted result or audit entry to the exact
/// build that produced it, and is the input the <c>ComplianceAttestation</c> binds the compliance manifest to.
///
/// <para>Everything here is deterministic: the version and commit come from the assembly's informational version
/// (<see cref="BuildInfo"/>), the toolchain from the .NET SDK version stamped into the assembly at build time
/// (assembly metadata <c>GridSimBuildSdk</c>, not the runtime the binary happens to run on), and the SBOM hash from a
/// build-time assembly metadata attribute the release workflow injects. There is no wall-clock read, so two builds of
/// the same commit with the same SDK and SBOM produce the same <see cref="BuildHash"/>, on any machine.</para>
///
/// <para><b>What the build hash is, and is not.</b> It is an IDENTITY LABEL: a short, deterministic name for "this
/// version, this commit, this SDK, this SBOM", to correlate a result or an audit entry with the release it came from.
/// It is NOT an integrity proof: it is computed from metadata strings, not from the bytes of the shipped files, so a
/// modified binary that keeps the same metadata reports the same hash. Integrity of a download comes from outside the
/// binary: the signed build-provenance attestation over every Release file (<c>gh attestation verify</c>), the
/// SHA256SUMS list, and the Authenticode signature of the Windows binary.</para>
/// </summary>
public sealed record BuildProvenance(
    string Version,
    string Commit,
    string Toolchain,
    string SbomSha256,
    string BuildHash)
{
    /// <summary>The assembly metadata key the release build injects the SBOM's SHA-256 under
    /// (see Directory.Build.props; <c>-p:GridSimSbomSha256=&lt;hex&gt;</c>).</summary>
    public const string SbomMetadataKey = "GridSimSbomSha256";

    /// <summary>The value <see cref="SbomSha256"/> takes when the build injected no SBOM hash (a local or dev
    /// build, as opposed to a release build). The compliance gate treats this as "not a release build".</summary>
    public const string SbomUnset = "unset";

    /// <summary>The assembly metadata key the build stamps the .NET SDK version under (Directory.Build.props).</summary>
    public const string SdkMetadataKey = "GridSimBuildSdk";

    /// <summary>The toolchain value when no SDK version was stamped (a build outside the repository's props).</summary>
    public const string SdkUnknown = "unknown-sdk";

    /// <summary>The provenance of the currently running build, read once from this (Core) assembly.</summary>
    public static BuildProvenance Current { get; } = ForAssembly(typeof(BuildProvenance).Assembly);

    /// <summary>True when this build carries an injected SBOM hash, that is, it was produced by the release
    /// workflow rather than a local build.</summary>
    public bool IsReleaseBuild => !string.Equals(SbomSha256, SbomUnset, StringComparison.Ordinal);

    /// <summary>
    /// Build the provenance for a given assembly. The commit is the full sha from the informational version (not
    /// the 8-character short form), so the attestation names the exact commit.
    /// </summary>
    public static BuildProvenance ForAssembly(Assembly assembly)
    {
        ArgumentNullException.ThrowIfNull(assembly);
        string info = BuildInfo.ReadInformationalVersion(assembly);
        string version = BuildInfo.SplitVersion(info);
        string commit = FullCommit(info);
        // The SDK that built the assembly, stamped at build time: the identity must not change with the .NET patch
        // runtime a framework-dependent build happens to run on (it did, when this was the running runtime).
        string toolchain = ReadMetadata(assembly, SdkMetadataKey) is string sdk ? ".NET SDK " + sdk : SdkUnknown;
        string sbom = ReadSbomSha256(assembly);
        string buildHash = ComputeBuildHash(version, commit, toolchain, sbom);
        return new BuildProvenance(version, commit, toolchain, sbom, buildHash);
    }

    /// <summary>One line for a footer or an attestation display:
    /// <c>GridSim 1.95.295 commit &lt;sha&gt; build &lt;hash12&gt;</c>.</summary>
    public string DisplayLine => string.Create(CultureInfo.InvariantCulture,
        $"GridSim {Version} commit {Commit} build {BuildHash[..Math.Min(12, BuildHash.Length)]}");

    // The full git sha from an informational version "1.95.295+<sha>", or "unknown" when the build recorded none.
    private static string FullCommit(string informationalVersion)
    {
        int plus = informationalVersion.IndexOf('+', StringComparison.Ordinal);
        if (plus < 0 || plus == informationalVersion.Length - 1)
            return "unknown";
        return informationalVersion[(plus + 1)..];
    }

    private static string ReadSbomSha256(Assembly assembly) => ReadMetadata(assembly, SbomMetadataKey) ?? SbomUnset;

    // The trimmed value of one assembly metadata attribute, or null when it is absent or blank.
    private static string? ReadMetadata(Assembly assembly, string key)
    {
        foreach (AssemblyMetadataAttribute meta in assembly.GetCustomAttributes<AssemblyMetadataAttribute>())
            if (string.Equals(meta.Key, key, StringComparison.Ordinal))
                return string.IsNullOrWhiteSpace(meta.Value) ? null : meta.Value!.Trim();
        return null;
    }

    /// <summary>The deterministic build hash: SHA-256 over the version, commit, toolchain and SBOM hash, in that
    /// order. Public so a test and the release workflow can recompute and compare it.</summary>
    public static string ComputeBuildHash(string version, string commit, string toolchain, string sbomSha256)
    {
        ArgumentNullException.ThrowIfNull(version);
        ArgumentNullException.ThrowIfNull(commit);
        ArgumentNullException.ThrowIfNull(toolchain);
        ArgumentNullException.ThrowIfNull(sbomSha256);
        string payload = string.Join('|', version, commit, toolchain, sbomSha256);
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(payload)));
    }
}
