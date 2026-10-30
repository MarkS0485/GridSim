using System.Text.RegularExpressions;
using GridSim.Core.IO;
using Xunit;

namespace GridSim.Tests;

/// <summary>
/// R069, R070, R150, R151, R152 (October 2026 review): CLI flags that were accepted and ignored, a Modbus framing
/// bound looser than the protocol, and unnamed literals duplicated across Program.cs. Source-level anti-drift checks
/// (the test project does not reference the CLI), plus a value check on the one table that moved into Core.
/// </summary>
public class CliLiteralsAndFlagsContractTests
{
    private static string Cli(string file)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "GridSim.slnx"))) dir = dir.Parent;
        Assert.NotNull(dir);
        return File.ReadAllText(Path.Combine(dir!.FullName, "src", "GridSim.Cli", file)).Replace("\r\n", "\n");
    }

    [Fact]
    public void Modbus_framing_bounds_the_mbap_length_at_254()
    {
        // R150: unit id (1) + the 253-byte maximum PDU = 254; the bound used to be 260 on the length field.
        string src = Cli("InterposeTransport.cs");
        Assert.Contains("private const int MaxMbapLength = 254;", src);
        Assert.Contains("length is < MinMbapLength or > MaxMbapLength", src);
        Assert.DoesNotContain("> 260)", src);
    }

    [Fact]
    public void Unimplemented_serial_flag_is_no_longer_accepted()
    {
        // R069: --serial passed the unknown-flag gate and was ignored (there is no serial transport).
        string src = Cli("Program.cs");
        int start = src.IndexOf("var knownFlags = new HashSet<string>", StringComparison.Ordinal);
        int end = src.IndexOf("};", start, StringComparison.Ordinal);
        Assert.DoesNotContain("\"--serial\",", src[start..end]);
    }

    [Fact]
    public void Synth_sources_and_tool_are_honoured_not_ignored()
    {
        string src = Cli("Program.cs");
        Assert.Contains("--spectra and --synth-sources are mutually exclusive", src);
        Assert.Contains("FailFlag<string>(\"--tool\", tool, \"pandapower (the only cross-validation sidecar)\")", src);
    }

    [Fact]
    public void Impedance_stability_refuses_flags_the_selected_model_ignores()
    {
        // R070: --pll-damping (GFM / asset-class GFL) and --current-bw (GFM) were parsed and silently dropped.
        string src = Cli("Program.cs");
        Assert.Contains("FailFlag<double>(\"--pll-damping\"", src);
        Assert.Contains("FailFlag<double>(\"--current-bw\"", src);
        Assert.Contains("FailFlag<double>(\"--pll-bw\"", src);
    }

    [Fact]
    public void Wscc9_transient_reactances_are_defined_once_with_the_anderson_fouad_values()
    {
        // R151: the map was repeated five times in Program.cs; it now lives next to IeeeCases.Case9.
        Assert.Equal(0.0608, IeeeCases.Wscc9TransientReactancePu[1]);
        Assert.Equal(0.1198, IeeeCases.Wscc9TransientReactancePu[2]);
        Assert.Equal(0.1813, IeeeCases.Wscc9TransientReactancePu[3]);
        Assert.Equal(3, IeeeCases.Wscc9TransientReactancePu.Count);
        string src = Cli("Program.cs");
        Assert.DoesNotContain("0.0608", src);
        Assert.Equal(5, Regex.Count(src, @"IeeeCases\.Wscc9TransientReactancePu"));
    }

    [Theory]
    [InlineData(@"\b0\.30\b")]                       // synthetic X'd
    [InlineData(@"Tdop: 8\.0")]                      // GENROU time constants
    [InlineData(@"onsetSeconds: 0\.1\b")]            // fault onset
    [InlineData(@"p\.YReduced, 50\.0")]              // f0 literal in the classical study
    [InlineData(@"f0 = 50\.0")]                      // f0 literal in the classical export
    [InlineData(@">= 0\.94 && r\.Vm\.Max\(\) <= 1\.06")] // voltage band
    [InlineData(@"tick % 100000")]
    [InlineData(@"fundamentalCurrentPu: 0\.5\)")]
    [InlineData(@"Roff: 1e9")]
    [InlineData(@"now-169h|169 h horizon|>169 h old")]
    public void Program_no_longer_carries_the_hoisted_literal(string pattern)
    {
        Assert.DoesNotMatch(new Regex(pattern), Cli("Program.cs"));
    }

    [Fact]
    public void Storage_defaults_are_named_and_reused_by_gda_replay()
    {
        // R152: 0.5 h step, 0.5 SoC, 1,000 V pack, 0.98 efficiency, 0.9 / 0.7 peak shave, 40 / 90 GBP/MWh.
        string storage = Cli("StorageCli.cs");
        foreach (string name in new[] { "DefaultStepHours = 0.5", "DefaultInitialSoc = 0.5", "DefaultPackDcVoltage = 1000.0",
                     "DefaultConverterEfficiency = 0.98", "PeakShaveHighFraction = 0.9, PeakShaveLowFraction = 0.7",
                     "DefaultBuyBelowGbpPerMwh = 40, DefaultSellAboveGbpPerMwh = 90" })
            Assert.Contains(name, storage);
        Assert.DoesNotContain("PackModel.Sized(chem, mwh * 1e6, 1000,", storage);
        Assert.Contains("StorageCli.DefaultStepHours", Cli("Program.cs"));
    }
}
