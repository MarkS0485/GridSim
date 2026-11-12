using System.Text.RegularExpressions;
using Xunit;

namespace GridSim.Tests;

/// <summary>
/// Bug #208: the CLI flags --device-inertia (GVA.s) and --device-ffr (GW) used to accept negative values (and were only
/// protected against NaN and Infinity by the generic number reader). They must now fail loud, exit 2 through FailFlag,
/// on anything that is not finite and 0 or more. The test project does not reference the CLI, so, like the F-064 tests,
/// these are source-level anti-drift checks over src/GridSim.Cli/Program.cs.
/// </summary>
public class CliBug208DeviceInertiaFfrFlagTests
{
    private static string Program()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "GridSim.slnx"))) dir = dir.Parent;
        Assert.True(dir is not null, $"GridSim.slnx not found above {AppContext.BaseDirectory}");
        string path = Path.Combine(dir!.FullName, "src", "GridSim.Cli", "Program.cs");
        Assert.True(File.Exists(path), $"source not found: {path}");
        return File.ReadAllText(path).Replace("\r\n", "\n");
    }

    /// <summary>From a signature to the next blank line (the helpers are separated by blank lines).</summary>
    private static string Body(string src, string signature)
    {
        int i = src.IndexOf(signature, StringComparison.Ordinal);
        Assert.True(i >= 0, $"helper not found: {signature}");
        int end = src.IndexOf("\n\n", i, StringComparison.Ordinal);
        return src[i..(end < 0 ? src.Length : end)];
    }

    [Theory]
    [InlineData("--device-inertia")]
    [InlineData("--device-ffr")]
    public void Bug208_every_read_of_the_flag_uses_the_refusing_reader_in_scenario_and_evidence(string flag)
    {
        string src = Program();
        // Two reads (scenario, evidence), both through the non-negative reader; the plain FlagDouble must not read it.
        Assert.Equal(2, Regex.Count(src, @"FlagDoubleNonNegative\(a, """ + Regex.Escape(flag) + @""", "));
        Assert.DoesNotContain("FlagDouble(a, \"" + flag + "\"", src);
        // No other reader of any kind may take the flag.
        Assert.DoesNotContain("FlagInt(a, \"" + flag + "\"", src);
    }

    [Fact]
    public void Bug208_defaults_are_unchanged()
    {
        string src = Program();
        Assert.Equal(2, Regex.Count(src, @"""--device-inertia"", GridSim\.Cli\.CliDefaults\.DefaultDeviceInertiaGvaS\)"));
        Assert.Equal(2, Regex.Count(src, @"""--device-ffr"", GridSim\.Cli\.CliDefaults\.DefaultDeviceFfrGw\)"));
    }

    [Fact]
    public void Bug208_reader_refuses_non_finite_and_negative_and_names_the_flag_and_requirement()
    {
        string body = Body(Program(), "static double FlagDoubleNonNegative(");
        Assert.Contains("double.IsFinite(v)", body);     // NaN, Infinity, overflow such as 1e999
        Assert.Contains("v >= 0.0", body);               // negatives refused, zero allowed
        Assert.DoesNotContain("v > 0.0", body);          // zero must stay legal (no device)
        // Both the unparseable value and the missing value go through FailFlag under the real flag name.
        Assert.Equal(2, Regex.Count(body, @"FailFlag<double>\(name, .*""a number that is finite and 0 or more""\)"));
        Assert.Contains("FlagValueMissing(a, i)", body);
        Assert.Contains("FlagIndex(a, name)", body);     // a repeated flag is refused by FlagIndex
    }

    [Theory]
    [InlineData("--device-inertia")]
    [InlineData("--device-ffr")]
    public void Bug208_flag_is_still_listed_for_the_verbs_that_take_it(string flag)
    {
        string src = Program();
        // scenario and evidence RequireVerbFlags lists, plus the shared known-flag table.
        Assert.True(Regex.Count(src, "\"" + Regex.Escape(flag) + "\"") >= 3);
    }
}
