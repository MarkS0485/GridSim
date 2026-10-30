using System.Globalization;
using GridSim.Core.IO.Psse;
using GridSim.Core.Model;
using GridSim.Core.PowerFlow;
using Xunit;

namespace GridSim.Tests;

/// <summary>
/// PSS/E transformer flow control (COD 2 reactive power by tap, COD 3 active power by phase shift), read with tap control
/// as <see cref="TransformerFlowControl"/> records the steady-state tap loop steps; and switched shunts with unequal
/// blocks or a remote regulated bus (MODSW 1, read with shunt control). Each regulated answer is checked against the
/// plain solves of the same network at fixed positions: the loop must stop at the first position, stepping from the
/// file's value, whose solve puts the regulated quantity in its band, and give exactly that solve's bits.
/// </summary>
public class PsseFlowControlTests
{
    private static string I(double v) => v.ToString("R", CultureInfo.InvariantCulture);

    private static readonly PsseReadOptions WithTaps = new() { FrequencyHz = 50, ApplyTapControl = true };
    private static readonly PsseReadOptions WithShunts = new() { FrequencyHz = 50, ApplyShuntControl = true };

    /// <summary>A revision 33 case: slack bus 1 (132 kV) and bus 2 (132 kV) joined by a line (X 0.1 pu) and, in parallel,
    /// transformer T1 (X 0.1 pu, ratio 1, angle 0) whose winding 1 line is <paramref name="winding1"/>; a load of 200 MW +
    /// 60 Mvar at bus 2. The transformer is the fourth branch-like record (line 13 starts it).</summary>
    internal static string Parallel(string winding1) => ($"""
        0, 100, 33, 0, 0, 50     / PSS/E-33 RAW, flow control check
        GridSim flow control check
        A line and a transformer in parallel
        1, 'SRC', 132, 3, 1, 1, 1, 1, 0
        2, 'LOAD', 132, 1, 1, 1, 1, 1, 0
        0 / END OF BUS DATA, BEGIN LOAD DATA
        2, '1', 1, 1, 1, 200, 60, 0, 0, 0, 0, 1, 1, 0
        0 / END OF LOAD DATA, BEGIN FIXED SHUNT DATA
        0 / END OF FIXED SHUNT DATA, BEGIN GENERATOR DATA
        1, '1', 0, 0, 999, -999, 1, 0, 100, 0, 0.01, 0, 0, 1, 1, 100, 999, -999, 1, 1
        0 / END OF GENERATOR DATA, BEGIN BRANCH DATA
        1, 2, '1', 0, 0.1, 0, 0, 0, 0, 0, 0, 0, 0, 1, 1, 0, 1, 1
        0 / END OF BRANCH DATA, BEGIN TRANSFORMER DATA
        1, 2, 0, '1', 1, 1, 1, 0, 0, 2, 'T1', 1, 1, 1
        0, 0.1, 100
        {winding1}
        1, 0
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
        """).Replace("\r\n", "\n");

    // WINDV1, NOMV1, ANG1, RATA1, RATB1, RATC1, COD1, CONT1, RMA1, RMI1, VMA1, VMI1, NTP1, TAB1, CR1, CX1, CNXA1
    private const string PhaseShifter = "1, 0, 0, 200, 200, 200, 3, 0, 30, -30, 150, 140, 61, 0, 0, 0, 0";
    private const string ReactiveTap = "1, 0, 0, 200, 200, 200, 2, 0, 1.1, 0.9, 50, 45, 21, 0, 0, 0, 0";

    private static void AssertSameBits(double[] want, double[] got, string what)
        => Assert.Equal(want.Select(BitConverter.DoubleToInt64Bits), got.Select(BitConverter.DoubleToInt64Bits));

    private static GridModel With(GridModel m, Func<Branch, Branch> f) =>
        m.CopyWith(branches: m.Branches.Select(b => b.IsTransformer ? f(b) : b).ToArray(), transformerFlowControls: Array.Empty<TransformerFlowControl>());

    [Fact]
    public void A_phase_shifter_steps_its_angle_until_the_active_power_into_it_is_in_its_band()
    {
        PsseParseResult r = PsseRawParser.ParseWithDiagnostics(Parallel(PhaseShifter), "ps", WithTaps);
        TransformerFlowControl fc = Assert.Single(r.Model.TransformerFlowControls);
        Assert.Equal(new TransformerFlowControl("T1", 1, TransformerFlowKind.ActivePower, 140, 150, -30, 30, 1.0), fc);
        Assert.Contains(r.Notes, n => n.StartsWith("1 transformer winding(s) regulate the power flowing through them (0 reactive power by tap, COD 2; 1 active power by phase shift, COD 3)", StringComparison.Ordinal));

        // By hand: the plain solve at each fixed angle, stepping from 0 one degree at a time in the direction that moves
        // the flow into the band (searched both ways, so the direction is not assumed).
        PowerFlowResult At(double deg) => new NewtonRaphsonPowerFlow().Solve(With(r.Model, b => b with { PhaseShiftDeg = deg }));
        double Flow(PowerFlowResult p) => p.LineFlows[1].PFromMw;
        Assert.InRange(Flow(At(0)), 90.0, 110.0);   // control: about half the load at angle 0, outside the band
        double expected = double.NaN;
        for (int k = 1; k <= 30 && double.IsNaN(expected); k++)
            foreach (double deg in new[] { -k * 1.0, k * 1.0 })
            {
                double f = Flow(At(deg));
                if (f >= 140 && f <= 150) { expected = deg; break; }
            }
        Assert.False(double.IsNaN(expected));

        var solver = new SteadyStateSolver(new NewtonRaphsonPowerFlow());
        PowerFlowResult pf = solver.Solve(r.Model);
        Assert.True(pf.Converged && solver.TapsSettled, $"converged {pf.Converged}, settled {solver.TapsSettled}");
        Assert.Equal(expected, solver.RegulatedModel.Branches[1].PhaseShiftDeg);
        AssertSameBits(At(expected).Vm, pf.Vm, "Vm");
        AssertSameBits(At(expected).VaDeg, pf.VaDeg, "Va");
        Assert.InRange(pf.LineFlows[1].PFromMw, 140.0, 150.0);
    }

    [Fact]
    public void A_reactive_power_tap_steps_its_ratio_until_the_reactive_power_into_it_is_in_its_band()
    {
        PsseParseResult r = PsseRawParser.ParseWithDiagnostics(Parallel(ReactiveTap), "q", WithTaps);
        TransformerFlowControl fc = Assert.Single(r.Model.TransformerFlowControls);
        Assert.Equal(TransformerFlowKind.ReactivePower, fc.Kind);
        Assert.Equal((45.0, 50.0, 0.9, 1.1), (fc.FlowMin, fc.FlowMax, fc.Min, fc.Max));
        double step = (1.1 - 0.9) / 20;
        Assert.Equal(step, fc.Step);

        PowerFlowResult At(double tap) => new NewtonRaphsonPowerFlow().Solve(With(r.Model, b => b with { TapRatio = tap }));
        double Q(PowerFlowResult p) => p.LineFlows[1].QFromMvar;
        double q0 = Q(At(1.0));
        Assert.True(q0 < 45, $"control: {q0:R} Mvar at tap 1, below the band");
        // Stepping the tap the same way the loop does (both directions tried, from the file's ratio).
        double expected = double.NaN;
        for (int k = 1; k <= 10 && double.IsNaN(expected); k++)
            foreach (double tap in new[] { Math.Max(0.9, 1.0 - k * step), Math.Min(1.1, 1.0 + k * step) })
            {
                double q = Q(At(tap));
                if (q >= 45 && q <= 50) { expected = tap; break; }
            }
        Assert.False(double.IsNaN(expected));
        var solver = new SteadyStateSolver(new NewtonRaphsonPowerFlow());
        PowerFlowResult pf = solver.Solve(r.Model);
        Assert.True(pf.Converged && solver.TapsSettled, $"converged {pf.Converged}, settled {solver.TapsSettled}");
        Assert.Equal(expected, solver.RegulatedModel.Branches[1].TapRatio, 15);
        Assert.InRange(Q(pf), 45.0, 50.0);
    }

    [Fact]
    public void A_flow_band_out_of_reach_leaves_the_control_at_its_limit_named_and_not_settled()
    {
        // A band of 400..410 MW into the transformer cannot be reached within +/- 30 degrees with a 200 MW load.
        string w = "1, 0, 0, 200, 200, 200, 3, 0, 30, -30, 410, 400, 61, 0, 0, 0, 0";
        GridModel m = PsseRawParser.ParseWithDiagnostics(Parallel(w), "ps", WithTaps).Model;
        var solver = new SteadyStateSolver(new NewtonRaphsonPowerFlow());
        PowerFlowResult pf = solver.Solve(m, maxOltcPasses: 100);
        Assert.True(pf.Converged);
        Assert.False(solver.TapsSettled);
        Assert.Equal(new[] { "T1" }, solver.TapsAtLimit);
        Assert.Equal(30.0, Math.Abs(solver.RegulatedModel.Branches[1].PhaseShiftDeg));
    }

    [Theory]
    [InlineData("1, 0, 0, 200, 200, 200, 3, 0, -30, 30, 150, 140, 61, 0, 0, 0, 0", "flow control needs RMA above RMI")]
    [InlineData("1, 0, 0, 200, 200, 200, 3, 0, 30, -30, 140, 150, 61, 0, 0, 0, 0", "flow control needs VMA above VMI")]
    [InlineData("1, 0, 0, 200, 200, 200, 2, 0, 1.1, 0.9, 40, 35, 1, 0, 0, 0, 0", "NTP1 is 1; a tap changer needs at least 2 positions")]
    public void Flow_control_data_that_cannot_regulate_is_refused_with_its_line(string w, string expected)
    {
        var ex = Assert.Throws<InvalidDataException>(() => PsseRawParser.ParseWithDiagnostics(Parallel(w), "bad", WithTaps));
        Assert.Contains("(TRANSFORMER data)", ex.Message, StringComparison.Ordinal);
        Assert.Contains(expected, ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Without_tap_control_cod_2_and_3_leave_the_file_values_and_writers_refuse_flow_controls()
    {
        GridModel plain = PsseRawParser.ParseWithDiagnostics(Parallel(PhaseShifter), "ps", new PsseReadOptions { FrequencyHz = 50 }).Model;
        Assert.Empty(plain.TransformerFlowControls);
        GridModel with = PsseRawParser.ParseWithDiagnostics(Parallel(PhaseShifter), "ps", WithTaps).Model;
        Assert.Equal(plain.Branches, with.Branches);   // same network; only the control record differs
        // Test changed 2026-10-25 (export pack): the PSS/E RAW writer now writes this device (it used to refuse it), so the
        // refusal check became a check that the written file reads back to the same device records (strengthened).
        Assert.Equal(with.TransformerFlowControls, PsseRawParser.ParseWithDiagnostics(PsseRawWriter.Write(with).Text, "ps", WithTaps).Model.TransformerFlowControls);
    }

    // ---- Switched shunts: unequal blocks and a remote regulated bus --------------------------------------------------

    [Fact]
    public void Unequal_blocks_switch_one_at_a_time_in_the_order_listed_giving_exactly_the_fixed_shunt_solve()
    {
        // Blocks 1 x 5 then 2 x 20 Mvar: levels 0, 5, 25, 45 from BINIT 0.
        PsseParseResult r = PsseRawParser.ParseWithDiagnostics(PsseShuntControlTests.TwoBus("1, 0, 1, 1.03, 0.97, 0, 100, '', 0, 1, 5, 2, 20"), "mixed", WithShunts);
        SwitchedShunt bank = Assert.Single(r.SwitchedShunts);
        Assert.Equal(new[] { 0.0, 5.0, 25.0, 45.0 }, bank.StepLevelsMvar);
        Assert.Equal((0, 3, 0), (bank.MinSteps, bank.MaxSteps, bank.InitialSteps));
        GridModel m = r.Model;
        PowerFlowResult Fixed(double b) => new NewtonRaphsonPowerFlow().Solve(m.CopyWith(buses: m.Buses.Select(x => x.Id == 2 ? x with { Bs = x.Bs + b } : x).ToArray()));
        double[] levels = { 0, 5, 25, 45 };
        int expected = Array.FindIndex(levels, b => Fixed(b).Vm[1] >= 0.97);
        Assert.InRange(expected, 1, 3);
        var loop = new SwitchedShuntPowerFlow(new NewtonRaphsonPowerFlow(), r.SwitchedShunts);
        PowerFlowResult pf = new SteadyStateSolver(loop).Solve(m);
        Assert.True(pf.Converged && loop.ShuntsSettled, loop.LastNote);
        Assert.Equal(expected, Assert.Single(loop.LastSteps));
        AssertSameBits(Fixed(levels[expected]).Vm, pf.Vm, "Vm");
    }

    [Fact]
    public void A_mixed_bank_starting_part_way_counts_its_steps_from_binit()
    {
        // Reactor 1 x -10, capacitors 2 x 5 then 1 x 20: levels -10, 0, 5, 10, 30; BINIT 5 is step 0.
        PsseParseResult r = PsseRawParser.ParseWithDiagnostics(PsseShuntControlTests.TwoBus("1, 0, 1, 1.03, 0.97, 0, 100, '', 5, 1, -10, 2, 5, 1, 20"), "mixed", WithShunts);
        SwitchedShunt bank = Assert.Single(r.SwitchedShunts);
        Assert.Equal((-2, 2, 0), (bank.MinSteps, bank.MaxSteps, bank.InitialSteps));
        Assert.Equal(new[] { -15.0, -5.0, 0.0, 5.0, 25.0 }, bank.StepLevelsMvar);
    }

    /// <summary>Three buses: slack 1, transformer to bus 2 (33 kV), a line to bus 3 with the 100 MW + 50 Mvar load; the
    /// switched shunt at bus 2 written as <paramref name="shunt"/>.</summary>
    private static string ThreeBus(string shunt) => PsseShuntControlTests.TwoBus(shunt)
        .Replace("2, 'LOAD', 33, 1, 1, 1, 1, 1, 0\n", "2, 'MID', 33, 1, 1, 1, 1, 1, 0\n3, 'LOAD', 33, 1, 1, 1, 1, 1, 0\n", StringComparison.Ordinal)
        .Replace("2, '1', 1, 1, 1, 100, 50, 0, 0, 0, 0, 1, 1, 0", "3, '1', 1, 1, 1, 100, 50, 0, 0, 0, 0, 1, 1, 0", StringComparison.Ordinal)
        .Replace("0 / END OF BRANCH DATA", "2, 3, '1', 0.01, 0.05, 0, 0, 0, 0, 0, 0, 0, 0, 1, 1, 0, 1, 1\n0 / END OF BRANCH DATA", StringComparison.Ordinal);

    [Fact]
    public void A_bank_regulating_a_remote_bus_switches_until_that_bus_is_in_band()
    {
        PsseParseResult r = PsseRawParser.ParseWithDiagnostics(ThreeBus("1, 0, 1, 1.03, 0.97, 3, 100, '', 0, 8, 10"), "remote", WithShunts);
        SwitchedShunt bank = Assert.Single(r.SwitchedShunts);
        Assert.Equal(3, bank.ControlsBus);
        GridModel m = r.Model;
        PowerFlowResult Fixed(int k) => new NewtonRaphsonPowerFlow().Solve(m.CopyWith(buses: m.Buses.Select(x => x.Id == 2 ? x with { Bs = x.Bs + k * 10.0 } : x).ToArray()));
        int i3 = Fixed(0).BusIds.ToList().IndexOf(3);
        int expected = 0;
        while (Fixed(expected).Vm[i3] < 0.97) expected++;
        Assert.InRange(expected, 1, 8);
        var loop = new SwitchedShuntPowerFlow(new NewtonRaphsonPowerFlow(), r.SwitchedShunts);
        PowerFlowResult pf = new SteadyStateSolver(loop).Solve(m);
        Assert.True(pf.Converged && loop.ShuntsSettled, loop.LastNote);
        Assert.Equal(expected, Assert.Single(loop.LastSteps));
        AssertSameBits(Fixed(expected).Vm, pf.Vm, "Vm");
    }

    [Fact]
    public void A_remote_bus_a_generator_holds_or_the_file_does_not_define_is_noted_or_refused()
    {
        PsseParseResult held = PsseRawParser.ParseWithDiagnostics(ThreeBus("1, 0, 1, 1.03, 0.97, 1, 100, '', 0, 8, 10"), "held", WithShunts);
        Assert.Empty(held.SwitchedShunts);
        Assert.Contains(held.Notes, n => n == "1 switched shunt(s) regulate a remote bus (SWREM) that a generator holds, so switching cannot move it: they stay fixed at BINIT.");
        var ex = Assert.Throws<InvalidDataException>(() => PsseRawParser.ParseWithDiagnostics(ThreeBus("1, 0, 1, 1.03, 0.97, 9, 100, '', 0, 8, 10"), "bad", WithShunts));
        Assert.Contains("SWREM names bus 9, which the file does not define", ex.Message, StringComparison.Ordinal);
    }
}
