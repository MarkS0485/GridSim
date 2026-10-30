using System.Reflection;

namespace GridSim.Core;

/// <summary>
/// The version of this GridSim build, read once from the assembly attributes the .NET SDK writes at build time.
///
/// There is exactly one version number in the repository: the <c>&lt;Version&gt;</c> in Directory.Build.props
/// (F02). Every project (Core, Gda, Gpu, Cli, Web, Wpf) is built with it, so reading it from this Core assembly
/// gives the same answer as reading it from the app that is running. The SDK stamps it as the assembly's
/// InformationalVersion in the form <c>1.95.111+&lt;40-character git commit sha&gt;</c>; the "+sha" part is present
/// only when the build ran inside a git checkout (or was handed <c>-p:SourceRevisionId=...</c>).
///
/// Used by <c>gridsim --version</c>, the web footer, the web OpenAPI document and the desktop About box, so all of
/// them always name the same release and commit.
/// </summary>
public static class BuildInfo
{
    /// <summary>
    /// The full informational version, for example <c>1.95.111+43e779ed96a88bcdeb4a9711f4950a4aa0d07f68</c>, or
    /// <c>1.95.111</c> when the build had no commit to record. Never empty: if the attribute is somehow missing
    /// (it is always written by the SDK), the numeric assembly version is used instead.
    /// </summary>
    public static string InformationalVersion { get; } = ReadInformationalVersion(typeof(BuildInfo).Assembly);

    /// <summary>The release number alone, for example <c>1.95.111</c> (everything before the first '+').</summary>
    public static string Version { get; } = SplitVersion(InformationalVersion);

    /// <summary>
    /// The major version of this build: the first component of <see cref="Version"/> (the "1" of 1.95.148). LICENSE.md
    /// section 1 defines "Major Version" exactly so, and a licence key is bound to it (F27): a key issued for 1.x works
    /// on every 1.x build and on no other.
    /// </summary>
    public static int MajorVersion { get; } = MajorOf(Version);

    /// <summary>
    /// The major version (first dot-separated component) of a release number such as <c>1.95.148</c> or <c>2.0.0</c>.
    /// Fails loud (FormatException) on anything that does not start with a non-negative integer: a build whose version
    /// cannot be read must never quietly accept or refuse licence keys for the wrong major version.
    /// </summary>
    public static int MajorOf(string version)
    {
        ArgumentNullException.ThrowIfNull(version);
        int dot = version.IndexOf('.', StringComparison.Ordinal);
        string head = dot < 0 ? version : version[..dot];
        if (!int.TryParse(head, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out int major))
            throw new FormatException($"'{version}' does not start with a major version number.");
        return major;
    }

    /// <summary>
    /// The git commit the build was made from, shortened to 8 characters (enough to identify it uniquely in this
    /// repository and the same length the CLI's run headers use), or <c>unknown</c> when the build recorded none.
    /// </summary>
    public static string Commit { get; } = SplitCommit(InformationalVersion);

    /// <summary>
    /// One line suitable for an About box or a footer: <c>GridSim 1.95.111 (commit 43e779ed)</c>.
    /// </summary>
    public static string DisplayLine => $"GridSim {Version} (commit {Commit})";

    /// <summary>
    /// Read the informational version from any assembly. Public so a test (or an app) can compare the stamp of
    /// its own assembly with Core's and prove they came from the same build.
    /// </summary>
    public static string ReadInformationalVersion(Assembly assembly)
    {
        ArgumentNullException.ThrowIfNull(assembly);
        string? info = assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
        if (!string.IsNullOrWhiteSpace(info)) return info.Trim();
        // Fallback only: the SDK always writes the attribute, but never return an empty version.
        return assembly.GetName().Version?.ToString(3) ?? "0.0.0";
    }

    /// <summary>The part of an informational version before the first '+' (the release number).</summary>
    public static string SplitVersion(string informationalVersion)
    {
        ArgumentNullException.ThrowIfNull(informationalVersion);
        int plus = informationalVersion.IndexOf('+', StringComparison.Ordinal);
        return plus < 0 ? informationalVersion : informationalVersion[..plus];
    }

    /// <summary>
    /// The commit part of an informational version (after the first '+'), shortened to 8 characters; <c>unknown</c>
    /// when there is no '+' part or it is empty.
    /// </summary>
    public static string SplitCommit(string informationalVersion)
    {
        ArgumentNullException.ThrowIfNull(informationalVersion);
        int plus = informationalVersion.IndexOf('+', StringComparison.Ordinal);
        if (plus < 0 || plus == informationalVersion.Length - 1) return "unknown";
        string sha = informationalVersion[(plus + 1)..];
        return sha.Length > 8 ? sha[..8] : sha;
    }
}
