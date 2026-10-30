using Xunit;

namespace GridSim.Tests;

/// <summary>
/// LF5 (October 2026 review, the remaining code findings; Ruling R1: Mark delegated the EE / product calls 2026-10-04).
/// One partial file per finding: N314 (the F stream's leftovers: trip's inertia, AssetEnvelope's model copy, the frequency
/// fitter's nominal, classical stability --dyn-data, two internal marks), N266 (stability --screen / --clear, the PLL
/// flags, TwoAxisMachine's reactances), N299 (the v2 case writer) and N229 (bundled status by content). Pure parts are
/// pinned directly; verbs and flags run as real processes (CliProcess) for their output, refusals and exit codes. Where a
/// change claims bit-identity on existing inputs, a test runs both forms and compares them exactly.
/// </summary>
public sealed partial class Lf5RemainingTests
{
    /// <summary>A fresh temporary folder for one test's input files.</summary>
    private static string TempDir()
    {
        string dir = Path.Combine(Path.GetTempPath(), "gridsim-lf5-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        return dir;
    }

    /// <summary>Delete a temporary folder, ignoring a file still held by a just-exited process.</summary>
    private static void Clean(string dir)
    {
        try { Directory.Delete(dir, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    /// <summary>A number in the round-trip form the CLI prints its exact values in.</summary>
    private static string R(double v) => v.ToString("R", System.Globalization.CultureInfo.InvariantCulture);

    /// <summary>Assert an exit code, showing the whole run when it differs.</summary>
    private static void AssertExit(int expected, CliProcess.Result r) => Assert.True(r.ExitCode == expected, r.ToString());

    /// <summary>Copy a folder (files only, one level, as the JSON case folders are) into a new temporary folder.</summary>
    private static string CopyCaseFolder(string source)
    {
        string dir = TempDir();
        foreach (string f in Directory.GetFiles(source)) File.Copy(f, Path.Combine(dir, Path.GetFileName(f)));
        return dir;
    }

    private static string RepoPath(params string[] parts) => Path.Combine(new[] { CliProcess.Root() }.Concat(parts).ToArray());
}
