using System;
using System.Collections.Generic;
using System.Linq;
using GridSim.Core.IO;
using GridSim.Core.Model;
using GridSim.Core.Protection.ShortCircuit;
using Xunit;

namespace GridSim.Tests;

/// <summary>
/// N134 (CF3, October 2026; Ruling R1): ETYS Appendix D (docs/reference S242 p.4) says NESO computes its X/R for the
/// peak-make current "using the equivalent frequency method (IEC 60909-0 (2001-07) Method C)". GridSim had Method B only
/// (kappa(R/X of Zk), times 1.15 when meshed, D-DOC-098). Method C is now an option of the three-phase duty
/// (<see cref="PeakFactorMethod.MethodC"/>, CLI <c>iec60909 --peak-method c</c>): R/X from the driving-point impedance
/// with every reactance at fc = 0.4 f, kappa with no safety factor. The default is Method B, bit for bit.
/// </summary>
public sealed partial class Cf3OpenCodeFindingsTests
{
    private static void SameBits(double expected, double actual) =>
        Assert.Equal(BitConverter.DoubleToInt64Bits(expected), BitConverter.DoubleToInt64Bits(actual));

    [Theory]
    [InlineData(1)]
    [InlineData(5)]
    [InlineData(9)]
    public void N134_method_b_stays_the_default_bit_for_bit(int bus)
    {
        GridModel m = IeeeCases.Case9();
        Iec60909.Duty d = Iec60909.ThreePhase(m, bus);
        Iec60909.Duty b = Iec60909.ThreePhase(m, bus, peakFactorMethod: PeakFactorMethod.MethodB);
        Assert.Equal(d, b);   // record equality: every field, exactly
        Assert.Equal(PeakFactorMethod.MethodB, d.PeakMethod);
        Assert.True(double.IsNaN(d.PeakRoverX));
    }

    [Theory]
    [InlineData(1)]
    [InlineData(5)]
    [InlineData(7)]
    public void N134_method_c_moves_the_peak_only(int bus)
    {
        GridModel m = IeeeCases.Case9();
        Iec60909.Duty b = Iec60909.ThreePhase(m, bus, f0Hz: m.FrequencyHz);
        Iec60909.Duty c = Iec60909.ThreePhase(m, bus, f0Hz: m.FrequencyHz, peakFactorMethod: PeakFactorMethod.MethodC);
        double rxc = Iec60909.MethodCRoverX(m, bus);
        Assert.Equal(PeakFactorMethod.MethodC, c.PeakMethod);
        SameBits(rxc, c.PeakRoverX);
        SameBits(Iec60909.PeakFactor(rxc), c.Kappa);
        SameBits(c.Kappa * Math.Sqrt(2.0) * c.IkppKa, c.PeakIpKa);   // case9 has no converter: no converter share
        Assert.False(c.MeshedPeakFactor);
        // Everything else is Method B's, bit for bit.
        SameBits(b.IkppKa, c.IkppKa);
        SameBits(b.BreakingIbKa, c.BreakingIbKa);
        SameBits(b.ThermalIthKa, c.ThermalIthKa);
        SameBits(b.ThermalM, c.ThermalM);
        // Moved 2026-10 (END-PASS CF4 row 8, end pass; N134 rest, IEC 60909-0 Method C (equivalent frequency method) applied
        // to the DC component as well as the peak, the method ETYS Appendix D (S242 p.4) names, Ruling R1 and Mark's go on
        // the end-pass moves 2026-10-11): Method C now gives idc from its own R/X at fc / f (MethodCDcRoverX, f x tb = 6
        // here, ratio 0.055 [VERIFY] against the paywalled standard) instead of Method B's R/X of Zk, so idc is no longer
        // Method B's. Before -> after: SameBits(b.DcComponentKa, c.DcComponentKa) -> c.DcComponentKa pinned to the bits
        // observed in the 1.95.273-275 runs (normal and AVX2-off identical): bus 1 4611661306417507015
        // (1.9945128315290945 kA, Method B) -> 4614453194012349616 (3.2288728190420315 kA); bus 5 4585872119475126236
        // (0.03963009392407432) -> 4589372673170672915 (0.06534012924480619); bus 7 4598185078622949875
        // (0.2505472887517321) -> 4601484151419645541 (0.4336826176920339). The assertions after this one (R/X of Zk and
        // the steady basis, still Method B's per the CF4 notes) were not reached in those runs: confirm at the validation run.
        long idcBits = bus switch
        {
            1 => 4614453194012349616L,
            5 => 4589372673170672915L,
            7 => 4601484151419645541L,
            _ => throw new ArgumentOutOfRangeException(nameof(bus)),
        };
        SameBits(BitConverter.Int64BitsToDouble(idcBits), c.DcComponentKa);
        SameBits(b.RoverX, c.RoverX);
        Assert.Equal(b.SteadyBasis, c.SteadyBasis);
        // Method C has no 1.15 safety factor and no cap: a valid IEC kappa.
        Assert.InRange(c.Kappa, 1.02, 2.0);
    }

    /// <summary>One machine (X''d 0.2 pu, R 0) behind one branch R + jX to the fault bus: a single series path.</summary>
    private static GridModel N134Radial(double r, double x)
    {
        return new GridModel
        {
            BaseMva = 100.0,
            Buses = new[] { new Bus(1, BusType.Slack, BaseKv: 132), new Bus(2, BusType.Pq, BaseKv: 132) },
            Generators = new[] { new Generator(1, Pg: 50, MBase: 100, SubtransientXdPu: 0.2, Name: "g") },
            Branches = new[] { new Branch(1, 2, r, x) },
        };
    }

    [Fact]
    public void N134_on_a_single_series_path_method_c_gives_the_networks_own_ratio()
    {
        // One path: Zc = R + j 0.4 (X + Xd''), so R/X_C = R / (0.4 (X + Xd'')) x 0.4 = R / (X + Xd''): the series R/X, to
        // the rounding of the two scalings (a few ulps), and kappa matches Method B's unmeshed kappa.
        GridModel m = N134Radial(0.02, 0.1);
        double rxc = Iec60909.MethodCRoverX(m, 2);
        Iec60909.Duty b = Iec60909.ThreePhase(m, 2);
        Assert.False(b.MeshedPeakFactor);
        double ulp = Math.BitIncrement(b.RoverX) - b.RoverX;
        Assert.True(Math.Abs(rxc - b.RoverX) <= 8 * ulp,
            $"R/X_C {rxc:R} vs series R/X {b.RoverX:R}");
        Assert.True(Math.Abs(Iec60909.ThreePhase(m, 2, peakFactorMethod: PeakFactorMethod.MethodC).Kappa - b.Kappa) < 1e-14);
    }

    [Fact]
    public void N134_an_unknown_method_is_refused()
        => Assert.Throws<ArgumentOutOfRangeException>(() => Iec60909.ThreePhase(IeeeCases.Case9(), 5, peakFactorMethod: (PeakFactorMethod)7));

    [Fact]
    public void N134_the_equivalent_frequency_ratio_is_20_hz_over_50()
    {
        Assert.Equal(0.4, Iec60909.MethodCFrequencyRatio);
        SameBits(20.0 / 50.0, Iec60909.MethodCFrequencyRatio);
        SameBits(24.0 / 60.0, Iec60909.MethodCFrequencyRatio);
    }

    // ------------------------------------------------------------------ the CLI flag (built CLI, real process)

    [Fact]
    public void N134_cli_peak_method_c_names_the_method_and_the_default_line_is_unchanged()
    {
        CliProcess.Result c = CliProcess.RunBuilt("iec60909", "case9", "--bus", "5", "--peak-method", "c");
        Assert.True(c.ExitCode == 0, c.ToString());
        Assert.Contains("Method C: kappa(R/X_C), R/X_C ", c.StdOut, StringComparison.Ordinal);
        Assert.Contains(" at fc = 0.4 f)", c.StdOut, StringComparison.Ordinal);
        CliProcess.Result b = CliProcess.RunBuilt("iec60909", "case9", "--bus", "5");
        Assert.True(b.ExitCode == 0, b.ToString());
        Assert.DoesNotContain("Method C", b.StdOut, StringComparison.Ordinal);
        CliProcess.Result explicitB = CliProcess.RunBuilt("iec60909", "case9", "--bus", "5", "--peak-method", "b");
        static string IpLine(string s) => s.Split('\n').Single(l => l.TrimStart().StartsWith("ip ", StringComparison.Ordinal)).TrimEnd('\r');
        Assert.Equal(IpLine(b.StdOut), IpLine(explicitB.StdOut));
    }

    // Moved 2026-10 (END-PASS CF5 rows 3 and 4, end pass; N353, the CLI reaches CF4's Method C for the unbalanced duties
    // (IEC 60909-0 Method C, N134 rest), and N116's rule that an earth-fault duty on a model with no earthing data exits 3,
    // Ruling R1 and Mark's go on the end-pass moves 2026-10-11): `--peak-method c` with `--type ll` / `slg` is no longer a
    // usage refusal. Before -> after: row `c --type ll` exit 2 -> 0 (observed: exit 0, "Method C: kappa(R/X_C), R/X_C
    // 0.10444341205098893 at fc = 0.4 f", 1.95.273-275 runs); row `c --type slg` exit 2 -> 3 (observed: exit 3, "the model
    // carries no transformer winding connections at all ... Exit 3."); row `d` unchanged (exit 2, still a refusal). The
    // expected exit code is now a row parameter; the name keeps "exit_2" so existing references resolve.
    [Theory]
    [InlineData(0, "--peak-method", "c", "--type", "ll")]
    [InlineData(3, "--peak-method", "c", "--type", "slg")]
    [InlineData(2, "--peak-method", "d")]
    public void N134_cli_peak_method_refusals_exit_2(int exitCode, params string[] flags)
    {
        CliProcess.Result r = CliProcess.RunBuilt(new[] { "iec60909", "case9", "--bus", "5" }.Concat(flags).ToArray());
        Assert.True(r.ExitCode == exitCode, r.ToString());
    }
}
