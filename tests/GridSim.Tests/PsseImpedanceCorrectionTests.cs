using GridSim.Core.IO.Psse;
using GridSim.Core.Model;
using GridSim.Core.PowerFlow;
using Xunit;

namespace GridSim.Tests;

/// <summary>
/// PSS/E transformer impedance correction tables (TAB): the winding's impedance multiplied by the table's factor at its
/// ratio (or phase shift angle for a phase shifter), interpolated linearly with the end factor outside the table; with tap
/// control, the factor recomputed at every tap the tap loop steps. Checked by hand calculation on the two-bus case of
/// <see cref="PsseTapControlTests"/>, on PSS/E 30's own sample, and on malformed tables.
/// </summary>
public class PsseImpedanceCorrectionTests
{
    private static string Fixture(string name) => Path.Combine(CliProcess.Root(), "tests", "GridSim.Tests", "fixtures", "psse", name);

    private static void AssertSameBits(double expected, double actual, string what)
        => Assert.True(BitConverter.DoubleToInt64Bits(expected) == BitConverter.DoubleToInt64Bits(actual),
            $"{what}: expected {expected:R}, got {actual:R}");

    /// <summary>The two-bus case (X 0.1 pu, R 0) with winding 1 at ratio <paramref name="windv1"/>, using impedance
    /// correction table 1 = <paramref name="table"/> (a revision 33 record body: T1, F1, T2, F2, ...).</summary>
    private static string TwoBusWithTable(double pMw, double qMvar, string windv1, string table, int cod = 1)
    {
        string text = PsseTapControlTests.TwoBus(pMw, qMvar, cod: cod).Replace("\r\n", "\n");
        text = text.Replace("\n1, 0, 0, 200, 200, 200, ", $"\n{windv1}, 0, 0, 200, 200, 200, ");
        text = text.Replace(", 1.03, 0.97, 21, 0, 0, 0, 0\n", ", 1.03, 0.97, 21, 1, 0, 0, 0\n");
        return text.Replace("0 / END OF VSC DC DATA, BEGIN IMPEDANCE CORRECTION DATA\n",
            $"0 / END OF VSC DC DATA, BEGIN IMPEDANCE CORRECTION DATA\n1, {table}\n");
    }

    private const string Table = "0.9, 1.2, 1.0, 1.0, 1.1, 0.8";

    [Fact]
    public void A_two_winding_impedance_is_scaled_by_the_factor_interpolated_at_its_ratio()
    {
        // Ratio 0.95 lies between T 0.9 (F 1.2) and T 1.0 (F 1.0): F = 1.2 + (0.95 - 0.9) / (1.0 - 0.9) x (1.0 - 1.2).
        var r = PsseRawParser.ParseWithDiagnostics(TwoBusWithTable(100, 50, "0.95", Table), "tab", new PsseReadOptions { FrequencyHz = 50 });
        Branch t = r.Model.Branches[0];
        double f = 1.2 + (0.95 - 0.9) / (1.0 - 0.9) * (1.0 - 1.2);
        AssertSameBits(0.1 * f, t.X, "X");
        AssertSameBits(0.0, t.R, "R");
        Assert.Equal(0.95, t.TapRatio);
        Assert.Contains(r.Notes, n => n.StartsWith("1 transformer winding(s) use an impedance correction table (TAB)", StringComparison.Ordinal));
        // On a break point the factor is that point's: ratio 1.1 gives F 0.8 exactly.
        Branch t11 = PsseRawParser.ParseWithDiagnostics(TwoBusWithTable(100, 50, "1.1", Table), "tab", new PsseReadOptions { FrequencyHz = 50 }).Model.Branches[0];
        AssertSameBits(0.1 * 0.8, t11.X, "X at 1.1");
    }

    [Fact]
    public void Outside_the_table_the_end_factor_is_used_and_noted()
    {
        var r = PsseRawParser.ParseWithDiagnostics(TwoBusWithTable(100, 50, "1.15", Table), "tab", new PsseReadOptions { FrequencyHz = 50 });
        AssertSameBits(0.1 * 0.8, r.Model.Branches[0].X, "X beyond the last point");
        Assert.Contains(r.Notes, n => n.StartsWith("1 winding(s) have a ratio or angle outside their impedance correction table; the table's end factor is used", StringComparison.Ordinal));
    }

    [Fact]
    public void With_tap_control_the_impedance_follows_every_tap_the_loop_steps()
    {
        // 100 MW + j 50 Mvar at bus 2 from ratio 1: the loop lowers the tap to raise bus 2 into 0.97..1.03. At the tap it
        // settles on, X must be 0.1 x F(tap), not the X of the starting ratio.
        var opts = new PsseReadOptions { FrequencyHz = 50, ApplyTapControl = true };
        GridModel m = PsseRawParser.ParseWithDiagnostics(TwoBusWithTable(100, 50, "1", Table), "tab", opts).Model;
        Oltc o = Assert.Single(m.Oltcs);
        TapImpedanceCorrection ic = Assert.IsType<TapImpedanceCorrection>(o.ImpedanceCorrection);
        Assert.Equal((0.0, 0.1, 1.0), (ic.BaseR, ic.BaseX, ic.RatioScale));
        var solver = new SteadyStateSolver(new NewtonRaphsonPowerFlow());
        PowerFlowResult pf = solver.Solve(m);
        Assert.True(pf.Converged);
        Assert.True(solver.TapsSettled);
        Branch final = solver.RegulatedModel!.Branches[0];
        Assert.True(final.TapRatio < 1.0, $"the tap should have been lowered, got {final.TapRatio:R}");
        (double rExp, double xExp) = ic.At(final.TapRatio);
        AssertSameBits(xExp, final.X, "X at the settled tap");
        AssertSameBits(rExp, final.R, "R at the settled tap");
        Assert.NotEqual(0.1, final.X);
        // Without the table, the same case settles with X still 0.1.
        GridModel plain = PsseRawParser.ParseWithDiagnostics(PsseTapControlTests.TwoBus(100, 50), "plain", opts).Model;
        var s2 = new SteadyStateSolver(new NewtonRaphsonPowerFlow());
        s2.Solve(plain);
        Assert.Equal(0.1, s2.RegulatedModel!.Branches[0].X);
    }

    [Fact]
    public void The_factor_function_interpolates_and_holds_the_ends()
    {
        var pts = new[] { new ImpedanceCorrectionPoint(-30, 1.1, 0.0), new ImpedanceCorrectionPoint(0, 1.0, 0.02), new ImpedanceCorrectionPoint(30, 1.1, 0.0) };
        Assert.Equal((1.1, 0.0), TapImpedanceCorrection.Factor(pts, -45));
        Assert.Equal((1.0, 0.02), TapImpedanceCorrection.Factor(pts, 0));
        Assert.Equal((1.0 + 0.5 * 0.1, 0.02 + 0.5 * -0.02), TapImpedanceCorrection.Factor(pts, 15));
        Assert.Equal((1.1, 0.0), TapImpedanceCorrection.Factor(pts, 30));
        // A complex factor rotates the impedance: (R + jX)(Fr + jFi).
        var ic = new TapImpedanceCorrection(0.01, 0.1, 1.0, pts);
        Assert.Equal((0.01 * 1.0 - 0.1 * 0.02, 0.01 * 0.02 + 0.1 * 1.0), ic.At(0));
    }

    [Fact]
    public void The_pssE_30_sample_transformers_get_their_tables_factors()
    {
        // t_psse_case3.raw: T4 'WDUM DC' (152-3021, Z1-2 = 0.00009 + j0.0042, ratio 1.1, COD 4) uses table 2, whose point
        // (1.10, 0.98) gives F 0.98 exactly.
        var r = PsseRawParser.ParseFile(Fixture("t_psse_case3.raw"), new PsseReadOptions { AllowUnmodelled = true });
        Branch t4 = r.Model.Branches.Single(b => b.Name == "WDUM DC");
        AssertSameBits(0.00009 * 0.98, t4.R, "T4 R");
        AssertSameBits(0.00420 * 0.98, t4.X, "T4 X");
        Assert.Contains(r.Notes, n => n.StartsWith("3 transformer winding(s) use an impedance correction table (TAB)", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("1.0, 1.0", "has 1 point(s); it needs at least 2")]
    [InlineData("1.0, 1.0, 0.9, 1.1", "point 2 has T 0.9, not above the previous point's 1; T must increase")]
    [InlineData("0.9, 1.0, 1.1, 0.0", "point 2 (T 1.1) has a zero factor")]
    public void A_malformed_table_is_refused_naming_the_line(string table, string expected)
    {
        var ex = Assert.Throws<InvalidDataException>(() => PsseRawParser.ParseWithDiagnostics(TwoBusWithTable(100, 50, "1", table), "bad"));
        Assert.Contains(expected, ex.Message, StringComparison.Ordinal);
        Assert.Contains("line ", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_table_defined_twice_is_refused()
    {
        string text = TwoBusWithTable(100, 50, "1", Table).Replace($"1, {Table}\n", $"1, {Table}\n1, {Table}\n");
        var ex = Assert.Throws<InvalidDataException>(() => PsseRawParser.ParseWithDiagnostics(text, "dup"));
        Assert.Contains("impedance correction table 1 is defined twice", ex.Message, StringComparison.Ordinal);
    }
}
