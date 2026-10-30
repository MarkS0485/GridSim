using GridSim.Core.IO.Psse;
using GridSim.Core.Model;
using GridSim.Core.PowerFlow;
using Xunit;
using Xunit.Abstractions;

namespace GridSim.Tests;

/// <summary>
/// Tranche A (October 2026): PSS/E automatic voltage control by tap (COD 1), applied only on request
/// (<see cref="PsseReadOptions.ApplyTapControl"/>, gridsim <c>--tap-control</c>): each controlled winding becomes an
/// <see cref="Oltc"/> record that the steady-state tap loop steps, and <see cref="SteadyStateSolver.LoadRampStages"/> gives
/// a case that does not converge from the file's taps a ramp start. Without the option nothing changes.
/// <para>The two-bus cases are checked against the closed-form two-bus solution: with V1 = 1 at the slack, an ideal a:1
/// transformer at the from end (V1' = V1 / a) and a lossless reactance X to a constant-power load P + jQ (pu),
/// |V2|^2 = (b + sqrt(b^2 - 4 X^2 (P^2 + Q^2))) / 2 with b = V1'^2 - 2 Q X, and no solution when the root is negative.</para>
/// </summary>
public class PsseTapControlTests
{
    private readonly ITestOutputHelper _out;

    public PsseTapControlTests(ITestOutputHelper output) => _out = output;

    private static string Fixture(string name) => Path.Combine(CliProcess.Root(), "tests", "GridSim.Tests", "fixtures", "psse", name);

    private static readonly PsseReadOptions WithTaps = new() { FrequencyHz = 50, ApplyTapControl = true };

    private const string Tail = """
        0 / END OF TRANSFORMER DATA, BEGIN AREA DATA
        0 / END OF AREA DATA, BEGIN TWO-TERMINAL DC DATA
        0 / END OF TWO-TERMINAL DC DATA, BEGIN VSC DC DATA
        0 / END OF VSC DC DATA, BEGIN IMPEDANCE CORRECTION DATA
        0 / END OF IMPEDANCE CORRECTION DATA, BEGIN MULTI-TERMINAL DC DATA
        0 / END OF MULTI-TERMINAL DC DATA, BEGIN MULTI-SECTION LINE DATA
        0 / END OF MULTI-SECTION LINE DATA, BEGIN ZONE DATA
        0 / END OF ZONE DATA, BEGIN INTER-AREA TRANSFER DATA
        0 / END OF INTER-AREA TRANSFER DATA, BEGIN OWNER DATA
        0 / END OF OWNER DATA, BEGIN FACTS DEVICE DATA
        0 / END OF FACTS DEVICE DATA, BEGIN SWITCHED SHUNT DATA
        0 / END OF SWITCHED SHUNT DATA, BEGIN GNE DEVICE DATA
        0 / END OF GNE DEVICE DATA, BEGIN INDUCTION MACHINE DATA
        0 / END OF INDUCTION MACHINE DATA
        Q
        """;

    /// <summary>A revision 33 two-bus case: slack bus 1 at 1.0 pu, transformer T1 (R 0, X 0.1 pu on the system base, ratio
    /// 1) regulating bus 2 (COD 1, CONT 2, band 0.97..1.03) with ratio limits <paramref name="rmi"/>..<paramref name="rma"/>
    /// over <paramref name="ntp"/> positions, and a constant-power load of <paramref name="pMw"/> + j<paramref name="qMvar"/>
    /// at bus 2. <paramref name="cont"/> and <paramref name="cod"/> may be changed for the refusal checks.</summary>
    internal static string TwoBus(double pMw, double qMvar, double rma = 1.1, double rmi = 0.9, int ntp = 21, string cont = "2", int cod = 1) => $"""
        0, 100, 33, 0, 0, 50     / PSS/E-33 RAW, two-bus tap control check
        GridSim tap control check
        Two buses, one transformer regulating bus 2
        1, 'SRC', 132, 3, 1, 1, 1, 1, 0
        2, 'LOAD', 33, 1, 1, 1, 1, 1, 0
        0 / END OF BUS DATA, BEGIN LOAD DATA
        2, '1', 1, 1, 1, {I(pMw)}, {I(qMvar)}, 0, 0, 0, 0, 1, 1, 0
        0 / END OF LOAD DATA, BEGIN FIXED SHUNT DATA
        0 / END OF FIXED SHUNT DATA, BEGIN GENERATOR DATA
        1, '1', 0, 0, 999, -999, 1, 0, 100, 0, 0.01, 0, 0, 1, 1, 100, 999, -999, 1, 1
        0 / END OF GENERATOR DATA, BEGIN BRANCH DATA
        0 / END OF BRANCH DATA, BEGIN TRANSFORMER DATA
        1, 2, 0, '1', 1, 1, 1, 0, 0, 2, 'T1', 1, 1, 1
        0, 0.1, 100
        1, 0, 0, 200, 200, 200, {cod}, {cont}, {I(rma)}, {I(rmi)}, 1.03, 0.97, {ntp}, 0, 0, 0, 0
        1, 0
        """ + "\n" + Tail;

    private static string I(double v) => v.ToString("R", System.Globalization.CultureInfo.InvariantCulture);

    /// <summary>The closed-form |V2| of the two-bus case (see the class summary), or null with no solution.</summary>
    private static double? V2(double tap, double p, double q)
    {
        const double x = 0.1;
        double v1 = 1.0 / tap, b = v1 * v1 - 2 * q * x, d = b * b - 4 * x * x * (p * p + q * q);
        return d < 0 ? null : Math.Sqrt((b + Math.Sqrt(d)) / 2);
    }

    private static SteadyStateSolver Solver(int rampStages = 0) => new(new NewtonRaphsonPowerFlow()) { LoadRampStages = rampStages };

    private static void AssertSameBits(double expected, double actual, string what)
        => Assert.True(BitConverter.DoubleToInt64Bits(expected) == BitConverter.DoubleToInt64Bits(actual),
            $"{what}: expected {expected:R}, got {actual:R}");

    // ---- Reader --------------------------------------------------------------------------------------------------------

    [Fact]
    public void Without_tap_control_the_file_reads_as_before_and_with_it_only_the_tap_changers_are_added()
    {
        var plain = PsseRawParser.ParseFile(Fixture("ukgds-style-v33.raw"), new PsseReadOptions { FrequencyHz = 50 });
        var taps = PsseRawParser.ParseFile(Fixture("ukgds-style-v33.raw"), WithTaps);
        Assert.Empty(plain.Model.Oltcs);
        Assert.Contains(plain.Notes, n => n.StartsWith("2 transformer(s) have automatic tap or phase control (COD); it is not applied", StringComparison.Ordinal));
        // Everything but the tap changers is the same model (the controlled transformers' names, '1-2' and '2-3', are
        // already unique, so no branch is renamed).
        Assert.Equal(plain.Model.Buses, taps.Model.Buses);
        Assert.Equal(plain.Model.AllGenerators, taps.Model.AllGenerators);
        Assert.Equal(plain.Model.Branches, taps.Model.Branches);
        Assert.Equal(plain.BranchKeys, taps.BranchKeys);
        Assert.DoesNotContain(taps.Notes, n => n.Contains("it is not applied: the taps stay at the file's ratios", StringComparison.Ordinal));
        Assert.Contains("2 transformer winding(s) regulate a bus voltage by tap (COD 1): applied as on-load tap changers", string.Join("\n", taps.Notes), StringComparison.Ordinal);
    }

    [Fact]
    public void Each_cod_1_winding_becomes_a_tap_changer_with_the_files_band_limits_and_step()
    {
        GridModel m = PsseRawParser.ParseFile(Fixture("ukgds-style-v33.raw"), WithTaps).Model;
        // 1-2: CONT 2, RMA 1.05, RMI 0.85, VMA 1.03, VMI 0.97, NTP 41. 2-3: CONT 3, the same band and limits, NTP 21.
        // 2-4 has COD 0 and gets none. Both units have WINDV2 = 1, so the limits are the RMA / RMI ratios themselves.
        Assert.Equal(new[]
        {
            new Oltc("1-2", 2, TargetVpu: (1.03 + 0.97) / 2, MinTap: 0.85, MaxTap: 1.05, StepPu: (1.05 - 0.85) / 40, DeadbandPu: (1.03 - 0.97) / 2),
            new Oltc("2-3", 3, TargetVpu: (1.03 + 0.97) / 2, MinTap: 0.85, MaxTap: 1.05, StepPu: (1.05 - 0.85) / 20, DeadbandPu: (1.03 - 0.97) / 2),
        }, m.Oltcs);
    }

    [Fact]
    public void Winding_2_ratio_divides_the_limits_and_step_of_a_two_winding_unit()
    {
        // WINDV2 = 1.05: the branch tap is w1 / w2, so a winding 1 range of 0.9..1.1 is 0.9 / 1.05 .. 1.1 / 1.05 on the tap.
        string text = Raw(TwoBus(100, 50)).Replace("\n1, 0\n", "\n1.05, 0\n");
        var r = PsseRawParser.ParseWithDiagnostics(text, "w2", WithTaps);
        Oltc o = Assert.Single(r.Model.Oltcs);
        Assert.Equal(new Oltc("T1", 2, TargetVpu: (1.03 + 0.97) / 2, MinTap: 0.9 / 1.05, MaxTap: 1.1 / 1.05, StepPu: (1.1 - 0.9) / 20 / 1.05,
            DeadbandPu: (1.03 - 0.97) / 2), o);
        Assert.Equal(1.0 / 1.05, r.Model.Branches[0].TapRatio);
    }

    [Fact]
    public void Parallel_units_sharing_a_name_get_names_of_their_own_so_each_tap_changer_drives_its_own_transformer()
    {
        string unit2 = """
            1, 2, 0, '2', 1, 1, 1, 0, 0, 2, 'T1', 1, 1, 1
            0, 0.1, 100
            1, 0, 0, 200, 200, 200, 1, 2, 1.1, 0.9, 1.03, 0.97, 21, 0, 0, 0, 0
            1, 0
            """;
        string text = Raw(TwoBus(100, 50)).Replace("0 / END OF TRANSFORMER DATA", unit2.Replace("\r\n", "\n") + "\n0 / END OF TRANSFORMER DATA");
        var r = PsseRawParser.ParseWithDiagnostics(text, "parallel", WithTaps);
        Assert.Equal(new[] { "T1 (1-2 1)", "T1 (1-2 2)" }, r.Model.Branches.Select(b => b.Name));
        Assert.Equal(new[] { "T1 (1-2 1)", "T1 (1-2 2)" }, r.Model.Oltcs.Select(o => o.Transformer));
        Assert.Equal(new[] { 0, 1 }, SteadyStateSolver.MapOltcRecordsToBranches(r.Model));
        Assert.Contains(r.Notes, n => n.StartsWith("2 tap-changing transformer(s) with no name, or a name another branch shares, were given a name of their own", StringComparison.Ordinal));

        // Both units step together, one step each per pass, and end on the same tap.
        SteadyStateSolver s = Solver();
        PowerFlowResult pf = s.Solve(r.Model);
        Assert.True(pf.Converged && s.TapsSettled);
        Assert.Equal(s.RegulatedModel.Branches[0].TapRatio, s.RegulatedModel.Branches[1].TapRatio);
    }

    [Fact]
    public void A_three_winding_units_controlled_winding_becomes_a_tap_changer_on_its_winding_branch()
    {
        string text = """
            0, 100, 33, 0, 0, 50     / PSS/E-33 RAW
            GridSim three-winding tap control check
            Winding 1 regulates bus 2
            1, 'HV', 132, 3, 1, 1, 1, 1, 0
            2, 'MV', 33, 1, 1, 1, 1, 1, 0
            3, 'LV', 11, 1, 1, 1, 1, 1, 0
            0 / END OF BUS DATA, BEGIN LOAD DATA
            2, '1', 1, 1, 1, 30, 10, 0, 0, 0, 0, 1, 1, 0
            3, '1', 1, 1, 1, 5, 2, 0, 0, 0, 0, 1, 1, 0
            0 / END OF LOAD DATA, BEGIN FIXED SHUNT DATA
            0 / END OF FIXED SHUNT DATA, BEGIN GENERATOR DATA
            1, '1', 0, 0, 999, -999, 1, 0, 100, 0, 0.01, 0, 0, 1, 1, 100, 999, -999, 1, 1
            0 / END OF GENERATOR DATA, BEGIN BRANCH DATA
            0 / END OF BRANCH DATA, BEGIN TRANSFORMER DATA
            1, 2, 3, '1', 1, 1, 1, 0, 0, 2, 'T3', 1, 1, 1
            0.002, 0.1, 100, 0.003, 0.15, 100, 0.004, 0.2, 100, 1, 0
            1, 0, 0, 60, 60, 60, 1, 2, 1.1, 0.9, 1.04, 0.96, 17, 0, 0, 0, 0
            1, 0, 0, 60, 60, 60, 0, 0, 1.1, 0.9, 1.1, 0.9, 33, 0, 0, 0, 0
            1, 0, 0, 20, 20, 20, 0, 0, 1.1, 0.9, 1.1, 0.9, 33, 0, 0, 0, 0
            """ + "\n" + Tail;
        var r = PsseRawParser.ParseWithDiagnostics(Raw(text), "three", WithTaps);
        Assert.Equal(new[] { "T3 winding 1", "T3 winding 2", "T3 winding 3" }, r.Model.Branches.Select(b => b.Name));
        Oltc o = Assert.Single(r.Model.Oltcs);
        Assert.Equal(new Oltc("T3 winding 1", 2, TargetVpu: (1.04 + 0.96) / 2, MinTap: 0.9, MaxTap: 1.1, StepPu: (1.1 - 0.9) / 16,
            DeadbandPu: (1.04 - 0.96) / 2), o);
        Assert.Equal(0, SteadyStateSolver.MapOltcRecordsToBranches(r.Model).Single());   // bus 2 is on neither end: the far side
    }

    [Theory]
    [InlineData("-5", 1, "CONT1 names bus 5, which the file does not define")]
    [InlineData("-2", 1, "CONT1 is -2, a remote controlled bus taken as on the winding 1 side")]
    public void A_controlled_bus_the_tap_loop_cannot_regulate_is_refused_with_its_line(string cont, int cod, string message)
    {
        var ex = Assert.Throws<InvalidDataException>(() => PsseRawParser.ParseWithDiagnostics(Raw(TwoBus(100, 50, cont: cont, cod: cod)), "bad", WithTaps));
        Assert.Contains("line 15 (TRANSFORMER data)", ex.Message, StringComparison.Ordinal);
        Assert.Contains(message, ex.Message, StringComparison.Ordinal);
        // Without tap control the same file reads (the control data is not used).
        Assert.Empty(PsseRawParser.ParseWithDiagnostics(Raw(TwoBus(100, 50, cont: cont, cod: cod)), "bad", new PsseReadOptions { FrequencyHz = 50 }).Model.Oltcs);
    }

    [Theory]
    [InlineData(1.1, 0.9, 1, "NTP1 is 1; a tap changer needs at least 2 tap positions")]
    [InlineData(0.9, 0.9, 21, "a tap changer needs RMA above RMI, both above 0")]
    public void Control_data_that_cannot_make_a_tap_changer_is_refused_with_its_line(double rma, double rmi, int ntp, string message)
    {
        var ex = Assert.Throws<InvalidDataException>(() => PsseRawParser.ParseWithDiagnostics(Raw(TwoBus(100, 50, rma, rmi, ntp)), "bad", WithTaps));
        Assert.Contains("line 15 (TRANSFORMER data)", ex.Message, StringComparison.Ordinal);
        Assert.Contains(message, ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Cont_0_and_cod_4_are_noted_and_leave_the_tap_where_the_file_put_it()
    {
        var none = PsseRawParser.ParseWithDiagnostics(Raw(TwoBus(100, 50, cont: "0")), "c0", WithTaps);
        Assert.Empty(none.Model.Oltcs);
        Assert.Contains(none.Notes, n => n.StartsWith("1 winding(s) have voltage control (COD 1) but no controlled bus (CONT 0)", StringComparison.Ordinal));
        // 2026-10-25: COD 2 and 3 (reactive and active power flow control) are applied now (PsseFlowControlTests), so the
        // "noted, not applied" check moved from COD 2 to COD 4 (DC line control), and the note names COD 4 and 5.
        var q = PsseRawParser.ParseWithDiagnostics(Raw(TwoBus(100, 50, cod: 4)), "c4", WithTaps);
        Assert.Empty(q.Model.Oltcs);
        Assert.Empty(q.Model.TransformerFlowControls);
        Assert.Contains(q.Notes, n => n.StartsWith("1 transformer winding(s) have DC line or asymmetric control (COD 4 and 5); it is not applied", StringComparison.Ordinal));
    }

    // ---- Solving -------------------------------------------------------------------------------------------------------

    [Fact]
    public void Two_bus_tap_settles_on_the_first_step_that_brings_the_bus_into_band_as_the_closed_form_says()
    {
        // 100 MW + 50 MVAr (P 1, Q 0.5 pu). Closed form: |V2| = 0.94122 at tap 1, 0.95211 at 0.99, 0.96320 at 0.98 and 0.97450
        // at 0.97, the first in the band 0.97..1.03. The step is (1.1 - 0.9) / 20.
        GridModel m = PsseRawParser.ParseWithDiagnostics(Raw(TwoBus(100, 50)), "two", WithTaps).Model;
        double step = (1.1 - 0.9) / 20, expected = 1.0;
        while (V2(expected, 1.0, 0.5)!.Value < 0.97) expected -= step;
        Assert.Equal(3.0, Math.Round((1.0 - expected) / step));

        // Control run: the file's tap (no tap control) leaves the bus out of band at the closed-form voltage.
        PowerFlowResult plain = new NewtonRaphsonPowerFlow().Solve(PsseRawParser.ParseWithDiagnostics(Raw(TwoBus(100, 50)), "two", new PsseReadOptions { FrequencyHz = 50 }).Model);
        Assert.True(plain.Converged);
        Assert.Equal(V2(1.0, 1.0, 0.5)!.Value, plain.Vm[1], 1e-9);
        Assert.True(plain.Vm[1] < 0.97);

        SteadyStateSolver s = Solver();
        PowerFlowResult pf = s.Solve(m);
        Assert.True(pf.Converged && s.TapsSettled, $"converged {pf.Converged}, settled {s.TapsSettled}");
        Assert.Equal(3, s.OltcPasses);
        AssertSameBits(expected, s.RegulatedModel.Branches[0].TapRatio, "final tap");
        Assert.Equal(V2(expected, 1.0, 0.5)!.Value, pf.Vm[1], 1e-9);
        Assert.InRange(pf.Vm[1], 0.97, 1.03);
        Assert.Equal(0, s.LoadRampStagesRun);   // it converged from the file's tap: no ramp
    }

    [Fact]
    public void The_ramp_start_never_touches_a_case_that_converges_from_its_own_taps()
    {
        GridModel m = PsseRawParser.ParseWithDiagnostics(Raw(TwoBus(100, 50)), "two", WithTaps).Model;
        SteadyStateSolver off = Solver(), on = Solver(10);
        PowerFlowResult a = off.Solve(m), b = on.Solve(m);
        Assert.Equal((off.OltcPasses, off.TapsSettled), (on.OltcPasses, on.TapsSettled));
        Assert.Equal(0, on.LoadRampStagesRun);
        Assert.Null(on.LoadRampFailedAt);
        for (int i = 0; i < a.Vm.Length; i++)
        {
            AssertSameBits(a.Vm[i], b.Vm[i], $"Vm {i}");
            AssertSameBits(a.VaDeg[i], b.VaDeg[i], $"Va {i}");
        }
        Assert.Same(m.Buses, on.RegulatedModel.Buses);
    }

    [Fact]
    public void A_case_beyond_its_nose_point_at_the_files_tap_solves_from_the_ramp_start_to_the_closed_form_tap()
    {
        // 500 MW + 50 MVAr through X 0.1 pu: at tap 1 the closed form has no solution (b^2 < 4 X^2 (P^2 + Q^2)), so Newton
        // cannot converge. Ratio range 0.8..1.1 over 31 positions (step 0.01). The ramp solves at 10 %, 20 %, ... with the
        // taps settling at each stage; the closed form, stepped the same way, says where the tap ends.
        GridModel m = PsseRawParser.ParseWithDiagnostics(Raw(TwoBus(500, 50, 1.1, 0.8, 31)), "heavy", WithTaps).Model;
        Assert.Null(V2(1.0, 5.0, 0.5));

        // Control run: without the ramp start the solve from the file's tap does not converge.
        SteadyStateSolver plain = Solver();
        Assert.False(plain.Solve(m).Converged);
        Assert.False(plain.TapsSettled);

        double step = (1.1 - 0.8) / 30, tap = 1.0;
        for (int k = 1; k <= 10; k++)
        {
            double l = k == 10 ? 1.0 : (double)k / 10;
            Assert.NotNull(V2(tap, 5.0 * l, 0.5 * l));   // every stage starts from a tap that has a solution
            while (V2(tap, 5.0 * l, 0.5 * l)!.Value < 0.97) tap = Math.Max(0.8, tap - step);
        }
        _out.WriteLine($"closed-form final tap {tap:R}, V2 {V2(tap, 5.0, 0.5):R}");

        SteadyStateSolver s = Solver(10);
        PowerFlowResult pf = s.Solve(m);
        Assert.True(pf.Converged && s.TapsSettled, $"converged {pf.Converged}, settled {s.TapsSettled}");
        Assert.Equal(10, s.LoadRampStagesRun);
        Assert.Null(s.LoadRampFailedAt);
        AssertSameBits(tap, s.RegulatedModel.Branches[0].TapRatio, "final tap");
        Assert.Equal(V2(tap, 5.0, 0.5)!.Value, pf.Vm[1], 1e-9);
        Assert.InRange(pf.Vm[1], 0.97, 1.03);
        // The answer is the full case: the regulated model carries the case's own demand.
        Assert.Equal(500.0, s.RegulatedModel.Buses[1].Pd);

        // Deterministic: a second solve gives the same bits.
        PowerFlowResult again = Solver(10).Solve(m);
        for (int i = 0; i < pf.Vm.Length; i++) AssertSameBits(pf.Vm[i], again.Vm[i], $"Vm {i}");
    }

    [Fact]
    public void A_ramp_stage_that_does_not_converge_returns_the_cases_own_non_converged_solve()
    {
        // 900 MW + 90 MVAr has no solution even at the lowest tap (0.8): the ramp gets part way and stops.
        GridModel m = PsseRawParser.ParseWithDiagnostics(Raw(TwoBus(900, 90, 1.1, 0.8, 31)), "too-heavy", WithTaps).Model;
        Assert.Null(V2(0.8, 9.0, 0.9));
        PowerFlowResult plain = Solver().Solve(m);
        SteadyStateSolver s = Solver(10);
        PowerFlowResult pf = s.Solve(m);
        Assert.False(pf.Converged);
        Assert.False(s.TapsSettled);
        Assert.NotNull(s.LoadRampFailedAt);
        Assert.True(s.LoadRampFailedAt < 1.0, $"failed at {s.LoadRampFailedAt}");
        Assert.Same(m, s.RegulatedModel);
        for (int i = 0; i < pf.Vm.Length; i++) AssertSameBits(plain.Vm[i], pf.Vm[i], $"Vm {i}");
    }

    [Fact]
    public void The_ramp_stage_count_is_checked()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new SteadyStateSolver(new NewtonRaphsonPowerFlow()) { LoadRampStages = -1 });
        Assert.Throws<ArgumentOutOfRangeException>(() => new SteadyStateSolver(new NewtonRaphsonPowerFlow()) { LoadRampStages = SteadyStateSolver.MaxLoadRampStages + 1 });
        Assert.Equal(0, new SteadyStateSolver(new NewtonRaphsonPowerFlow()).LoadRampStages);
    }

    // ---- Command line (runs the built CLI) ------------------------------------------------------------------------------

    [Fact]
    public void Tap_control_on_the_command_line_solves_a_case_beyond_its_nose_point_and_says_how()
    {
        string dir = Path.Combine(Path.GetTempPath(), "gridsim-taps-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            string raw = Path.Combine(dir, "heavy.raw");
            File.WriteAllText(raw, Raw(TwoBus(500, 50, 1.1, 0.8, 31)));

            // Control run: without the flag the taps stay at the file's ratio and the solve fails (exit 3).
            CliProcess.Result plain = CliProcess.RunBuilt(raw, "--once");
            Assert.True(plain.ExitCode == 3, plain.ToString());
            Assert.Contains("DID NOT CONVERGE", plain.StdOut, StringComparison.Ordinal);
            Assert.Contains("note: heavy.raw: 1 transformer(s) have automatic tap or phase control (COD); it is not applied", plain.StdErr, StringComparison.Ordinal);
            Assert.DoesNotContain("Ramp start", plain.StdOut, StringComparison.Ordinal);

            CliProcess.Result taps = CliProcess.RunBuilt(raw, "--once", "--tap-control");
            Assert.True(taps.ExitCode == 0, taps.ToString());
            // A two-bus case is radial, so the CLI solves it with the backward/forward sweep ("Converged on the voltage
            // step"): the ramp start serves every engine the solver drives.
            Assert.Contains("Converged", taps.StdOut, StringComparison.Ordinal);
            Assert.DoesNotContain("DID NOT CONVERGE", taps.StdOut, StringComparison.Ordinal);
            Assert.Contains("Ramp start: the solve from the file's taps did not converge, so the case was solved at 10 %, 20 %, ... 90 % of its demand and generation first, the taps settling at each stage, then at full load from those taps (10 stages).",
                taps.StdOut, StringComparison.Ordinal);
            Assert.Contains("note: heavy.raw: 1 transformer winding(s) regulate a bus voltage by tap (COD 1)", taps.StdErr, StringComparison.Ordinal);
            Assert.DoesNotContain("had no effect", taps.StdErr, StringComparison.Ordinal);
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public void Tap_control_with_no_raw_file_says_it_had_no_effect_and_twice_is_refused()
    {
        CliProcess.Result r = CliProcess.RunBuilt("case14", "--once", "--tap-control");
        Assert.True(r.ExitCode == 0, r.ToString());
        Assert.Contains("warning: --tap-control had no effect: this run loaded no PSS/E .raw file.", r.StdErr, StringComparison.Ordinal);
        Assert.DoesNotContain("Ramp start", r.StdOut, StringComparison.Ordinal);

        CliProcess.Result twice = CliProcess.RunBuilt("case14", "--once", "--tap-control", "--tap-control");
        Assert.True(twice.ExitCode == 2, twice.ToString());
        Assert.Contains("error: --tap-control given more than once.", twice.StdErr, StringComparison.Ordinal);
    }

    // ---- UKGDS (opt-in: the files carry no licence, so they are not committed) -------------------------------------------

    [EnvironmentFact("reads the UKGDS RAW cases (github.com/sedg/ukgds raw folder), which carry no licence and are not committed",
        "GRIDSIM_UKGDS_DIR")]
    public void Every_ukgds_case_solves_with_its_tap_control_applied()
    {
        string dir = Environment.GetEnvironmentVariable("GRIDSIM_UKGDS_DIR")!;
        string[] raws = Directory.GetFiles(dir, "*.raw").OrderBy(f => f, StringComparer.Ordinal).ToArray();
        Assert.Equal(12, raws.Length);
        foreach (string path in raws)
        {
            PsseParseResult raw = PsseRawParser.ParseFile(path, new PsseReadOptions { Revision = 30, FrequencyHz = 50, ApplyTapControl = true });
            SteadyStateSolver s = Solver(10);
            PowerFlowResult pf = s.Solve(raw.Model);
            _out.WriteLine($"{Path.GetFileName(path)}: {raw.Model.Oltcs.Count} tap changers; converged {pf.Converged}, settled {s.TapsSettled}, " +
                $"{s.OltcPasses} tap passes, ramp stages {s.LoadRampStagesRun}, V {pf.Vm.Min():F4}..{pf.Vm.Max():F4} pu, at limit {s.TapsAtLimit.Count}");
            Assert.True(pf.Converged, $"{Path.GetFileName(path)} did not converge");
        }
    }

    /// <summary>A raw string literal's text with LF line ends (C# keeps the source file's line ends in a literal).</summary>
    private static string Raw(string text) => text.Replace("\r\n", "\n");
}
