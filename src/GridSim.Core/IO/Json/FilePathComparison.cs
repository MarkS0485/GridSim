namespace GridSim.Core.IO.Json;

/// <summary>
/// Bug hunt 2026-10-29, findings 37 and 42: how the v2 case loader and validator compare two file paths. Windows file
/// systems are case-insensitive, so "Net.json" and "net.json" are one file there and are compared ignoring case; on
/// Linux and the other systems they are two different files and are compared exactly. This is the rule GR-655 already
/// applied to the "inside the case folder" test (V2CaseLoader.IsInside, CaseSession.IsInside); the duplicate-file, the
/// pulled-network and the library-file lookups compared ignoring case on every system, so on Linux a second file whose
/// path differed only in case was skipped (never read or never validated) or matched against the first file's content.
/// On Windows every comparison is the same as before.
/// </summary>
internal static class FilePathComparison
{
    /// <summary>The comparer for file paths on this operating system.</summary>
    public static StringComparer Comparer => OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;

    /// <summary>The comparison for file paths on this operating system.</summary>
    public static StringComparison Comparison => OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
}
