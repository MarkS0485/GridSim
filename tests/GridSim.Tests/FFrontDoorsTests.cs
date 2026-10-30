using Xunit;

namespace GridSim.Tests;

/// <summary>
/// F front doors (October 2026 review, R079 and the R095 remainder; Ruling R1): engines that existed in Core with tests
/// but no production entry point now have one. Each partial file covers one front door: the pure parsing / model edits
/// (src/GridSim.Cli/FrontDoorsCli.cs, linked into this project) are pinned directly, and the verb or flag itself is run
/// as a real process (CliProcess) for its output, its refusals and its exit codes. With the new flag or verb absent no
/// existing output changes; where that is the claim, a test runs both forms and compares them.
/// </summary>
public sealed partial class FFrontDoorsTests
{
    /// <summary>A fresh temporary folder for one test's input files.</summary>
    private static string TempDir()
    {
        string dir = Path.Combine(Path.GetTempPath(), "gridsim-f-front-doors-" + Guid.NewGuid().ToString("N"));
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
}
