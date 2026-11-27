using GridSim.Core.Assets;
using GridSim.Core.Compliance.GridCode;
using GridSim.Core.Diagnostics;
using GridSim.Core.Dynamics.Models;
using GridSim.Core.Harmonics.Planning;
using GridSim.Core.Model;
using GridSim.Core.PowerFlow;
using GridSim.Core.Protection;
using Xunit;

namespace GridSim.Tests;

/// <summary>
/// REVIEW-FIX-4 (2.1.088): the grid-code checks report only what the study demonstrates. Sources: GB Grid Code ECC Issue 6
/// Revision 33 (ECC.6.3.15.4, .8, ECC.6.3.7.1-.3, ECC.6.3.13.2) and Regulation (EU) 2016/631 Figure 6. Every expected value
/// is derived here by hand from the circuit or the governor's closed form (each was evaluated in Python, IEEE double), not
/// read back from the trace under test. Each failing case fails when its check is broken.
/// </summary>
public sealed class Rc5ReviewFix4GridCodeTests
{
    private static readonly AssetClassProvenance Test = new(ProvenanceTag.Synthetic, "test setting");

    private static UnitProtectionSet Relays(params (string Key, double Value)[] settings) =>
        new(Array.Empty<UnitProtectionClass>(), new[]
        {
            new UnitProtectionAssignment("unit", null, new UnitProtectionLayer(settings.ToDictionary(x => x.Key, x => x.Value, StringComparer.Ordinal),
                settings.ToDictionary(x => x.Key, _ => Test, StringComparer.Ordinal))),
        });

    private static GridCodeRequest Request(GridModel model, IReadOnlyList<SubtransientInit.MachineData> data, string tech, ModuleType mt, double kv,
        params GridCodeCheckRequest[] checks) => new()
        {
            Label = "fix4", Code = "gb-ecc", ModuleType = mt, Technology = tech, ConnectionKv = kv, Model = model, Data = data,
            UnitIndex = 1, PccBus = 2, PlantBuses = Array.Empty<int>(), Checks = checks,
        };

    private static (GridModel Model, PowerFlowResult Pf) Solve(GridModel m)
    {
        PowerFlowResult pf = new NewtonRaphsonPowerFlow().Solve(m);
        Assert.True(pf.Converged);
        return (m, pf);
    }

    // ---- FRT: a single machine against an infinite bus, the equal-area criterion ---------------------------------------

    /// <summary>
    /// Bus 1: GENCLS H = 1e6 (an infinite bus), X'd = 0.1; line 1-2, X = 0.2; bus 2 (PCC): the unit, GENCLS X'd = 0.1, at
    /// P = Pmax = 50 MW with both buses at 1.0 pu (the ECC.6.3.15.8 pre-fault point), Sb = MBase = 100. GB type D synchronous
    /// at 132 kV (ECC.6.3.15.4): Uret = 0, so the fault is bolted and the unit's electrical power is zero while it is in.
    /// Hand values: theta2 = asin(0.5 x 0.2); E' = V + j0.1 I at each end gives |E1'| = |E2'| = 1.0037523819847554 and
    /// delta0 = 0.19983483292693294 rad; after clearing Pe = Pmax sin delta with Pmax = |E1'||E2'| / 0.4 = 2.5187971108501754.
    /// Over the fault delta = delta0 + w0 Pm t^2 / (4H) exactly; the critical clearing angle is
    /// cos(delta_cr) = (Pm (delta_max - delta0) + Pmax cos delta_max) / Pmax with delta_max = pi - delta0, so
    /// delta_cr = 2.021731289691602 rad and the critical clearing time sqrt(4H (delta_cr - delta0) / (w0 Pm)) is 0.681 s for
    /// H = 10 and 0.096 s for H = 0.2, against tclear = 0.14 s. With H = 0.2 the angle at clearing, 4.05 rad, is past delta_max
    /// with the rotor still speeding up, so it slips a pole. With H = 10 the first swing peaks at 0.394 rad (fourth-order
    /// integration of the same swing), where |V2| = |0.25 E1' + 0.75 E2'| stays at least 0.089 pu above the line.
    /// </summary>
    private static (GridModel Model, PowerFlowResult Pf, IReadOnlyList<SubtransientInit.MachineData> Data) Smib(double unitH, UnitProtectionSet? relays = null)
    {
        var buses = new[] { new Bus(1, BusType.Slack, BaseKv: 132), new Bus(2, BusType.Pv, BaseKv: 132) };
        var gens = new[]
        {
            new Generator(1, 0.0, MBase: 100, H: 1e6, SubtransientXdPu: 0.1, Name: "grid"),
            new Generator(2, 50.0, MBase: 100, H: unitH, Pmax: 50, SubtransientXdPu: 0.1, Name: "unit"),
        };
        var model = new GridModel { BaseMva = 100, Buses = buses, Generators = gens, Branches = new[] { new Branch(1, 2, 0.0, 0.2) } };
        if (relays is not null) model = model.WithUnitProtection(relays);
        (GridModel m, PowerFlowResult pf) = Solve(model);
        return (m, pf, new[] { Gencls.Data(1e6, 0.0, 0.1), Gencls.Data(unitH, 0.0, 0.1) });
    }

    private static GridCodeResult SmibFrt(double unitH, UnitProtectionSet? relays = null)
    {
        var c = Smib(unitH, relays);
        return GridCodeStudy.Run(Request(c.Model, c.Data, GridCodeTechnology.Synchronous, ModuleType.D, 132,
            new FrtEnvelopeRequest(new FrtFault(AtSeconds: 0.1))));
    }

    [Fact]
    public void A_unit_inside_its_critical_clearing_time_at_the_code_pre_fault_point_passes()
    {
        GridCodeResult res = SmibFrt(10.0);
        FrtRun run = Assert.Single(res.FrtRuns);
        Assert.Equal(0.0, run.ReactancePu);   // Uret = 0: the bolted fault
        GridCodeCheckResult r = Assert.Single(res.Checks);
        Assert.True(r.Outcome == RuleOutcome.Pass, r.Reason);
        Assert.Contains(GridCodeChecks.RecoveryNote, r.Notes);
        Assert.Contains(r.Notes, n => n.Contains("maximum leading power factor", StringComparison.Ordinal));
        Assert.Equal(RuleSetEngine.Compliant, res.Verdict);
    }

    [Fact]
    public void A_unit_that_loses_synchronism_on_the_line_fails()
    {
        GridCodeResult res = SmibFrt(0.2);
        Assert.False(Assert.Single(res.FrtRuns).Observed.Result.StayedInSynchronism);
        GridCodeCheckResult r = Assert.Single(res.Checks);
        Assert.Equal(RuleOutcome.Fail, r.Outcome);
        Assert.Contains("synchronism was lost", r.Reason, StringComparison.Ordinal);
        Assert.Equal(RuleSetEngine.NonCompliant, res.Verdict);
    }

    /// <summary>The stable unit (H = 10) with an undervoltage stage at 0.5 pu, 0.05 s: the bolted fault holds its terminal near
    /// 0 for 0.14 s, longer than the delay, so the stage must trip it.</summary>
    [Fact]
    public void A_unit_relay_that_trips_on_the_line_fault_fails()
    {
        GridCodeResult res = SmibFrt(10.0, Relays(("uv1Pu", 0.5), ("uv1DelayS", 0.05)));
        GridCodeCheckResult r = Assert.Single(res.Checks);
        Assert.Equal(RuleOutcome.Fail, r.Outcome);
        Assert.Contains("27-1 stage tripped it", r.Reason, StringComparison.Ordinal);
        Assert.Equal(RuleSetEngine.NonCompliant, res.Verdict);
    }

    /// <summary>A converter unit's own relays are not integrated by the RMS relay study, so a converter with an undervoltage
    /// stage cannot be shown to stay connected (it was a PASS before this fix).</summary>
    [Fact]
    public void A_converter_whose_relays_the_run_does_not_evaluate_is_not_evaluated()
    {
        var buses = new[] { new Bus(1, BusType.Slack, BaseKv: 132), new Bus(2, BusType.Pv, BaseKv: 132) };
        var gens = new[]
        {
            new Generator(1, 0.0, MBase: 100, H: 1e6, SubtransientXdPu: 0.1, Name: "grid"),
            new Generator(2, 50.0, MBase: 100, Pmax: 50, Name: "unit", ConverterInterfaced: true),
        };
        var model = new GridModel { BaseMva = 100, Buses = buses, Generators = gens, Branches = new[] { new Branch(1, 2, 0.0, 0.2) } }
            .WithUnitProtection(Relays(("uv1Pu", 0.5), ("uv1DelayS", 0.05)));
        Solve(model);
        GridCodeResult res = GridCodeStudy.Run(Request(model, new[] { Gencls.Data(1e6, 0.0, 0.1), Gencls.Data(5.0, 0.0, 0.1) },
            GridCodeTechnology.Ppm, ModuleType.D, 132, new FrtEnvelopeRequest(new FrtFault(AtSeconds: 0.1))));
        GridCodeCheckResult r = Assert.Single(res.Checks);
        Assert.Equal(RuleOutcome.NotEvaluated, r.Outcome);
        Assert.Equal("NOT EVALUATED", r.Status);
        Assert.Contains("27-1", r.Reason, StringComparison.Ordinal);
        Assert.Equal(RuleSetEngine.Incomplete, res.Verdict);
    }

    // ---- FRT: depth and the unbalanced path -------------------------------------------------------------------------

    /// <summary>The flat two-machine case of WP8 T5 (both EMFs 1, no transfer): positive and negative sequence Thevenin
    /// reactance at bus 2 are both z = 0.3 || 0.1 = 0.075 pu (GENCLS X'd = X''d = X2 = 0.1). A line-to-line fault at the PCC
    /// through jxf with xf = z: V1 = (z + xf) / (2z + xf) = 2/3, V2 = z / (2z + xf) = 1/3, and the faulted phases have
    /// |a^2 V1 + a V2| = sqrt(V1^2 + V2^2 - V1 V2) = 1/sqrt(3) = 0.5773502691896258. The check must judge the smallest phase
    /// voltage (1/sqrt 3), not the positive sequence (2/3); both lie far above Uret = 0.3, so the test is milder than the
    /// line and is NOT DEMONSTRATED.</summary>
    [Fact]
    public void An_unbalanced_fault_is_judged_on_the_smallest_phase_voltage_and_a_mild_fault_is_not_demonstrated()
    {
        var buses = new[] { new Bus(1, BusType.Slack, BaseKv: 132), new Bus(2, BusType.Pv, BaseKv: 132) };
        var gens = new[]
        {
            new Generator(1, 0.0, MBase: 100, H: 1000, SubtransientXdPu: 0.1, Name: "grid"),
            new Generator(2, 0.0, MBase: 100, H: 5, Pmax: 100, SubtransientXdPu: 0.1, Name: "unit"),
        };
        (GridModel m, _) = Solve(new GridModel { BaseMva = 100, Buses = buses, Generators = gens, Branches = new[] { new Branch(1, 2, 0.0, 0.2) } });
        GridCodeResult res = GridCodeStudy.Run(Request(m, new[] { Gencls.Data(1000.0, 0.0, 0.1), Gencls.Data(5.0, 0.0, 0.1) },
            GridCodeTechnology.Synchronous, ModuleType.B, 33,
            new FrtEnvelopeRequest(new FrtFault(FaultType.LineToLine, 0.0, 0.075, AtSeconds: 0.1), DurationSeconds: 3.0)));
        GridCodeCheckResult r = Assert.Single(res.Checks);
        Assert.Equal("NOT DEMONSTRATED", r.Status);
        Assert.Equal(RuleOutcome.NotEvaluated, r.Outcome);
        Assert.True(Math.Abs(r.Value!.Value - 0.5773502691896258) <= 1e-9, $"min phase {r.Value:R}");
        Assert.Equal(0.3, r.Limit);
        Assert.Contains("milder than the line", r.Reason, StringComparison.Ordinal);
        Assert.Equal(RuleSetEngine.Incomplete, res.Verdict);
    }

    // ---- LFSM and FSM by frequency injection ------------------------------------------------------------------------

    private static Tgov1Data Tgov(double r = 0.05, double t1 = 0.5) => new(r, t1, 1.0, 1.0, 0.0, 0.0, 1.0);

    /// <summary>The two-machine case with a governor on the unit (bus 2, Pmax 100 MW, MBase 100).</summary>
    private static (GridModel Model, IReadOnlyList<SubtransientInit.MachineData> Data) GovernedCase(Tgov1Data gov)
    {
        var buses = new[] { new Bus(1, BusType.Slack, BaseKv: 132), new Bus(2, BusType.Pv, BaseKv: 132) };
        var gens = new[]
        {
            new Generator(1, 0.0, MBase: 100, H: 1000, SubtransientXdPu: 0.1, Name: "grid"),
            new Generator(2, 0.0, MBase: 100, H: 5, Pmax: 100, SubtransientXdPu: 0.1, Name: "unit"),
        };
        var model = new GridModel { BaseMva = 100, Buses = buses, Generators = gens, Branches = new[] { new Branch(1, 2, 0.0, 0.2) } };
        return (model, new[] { Gencls.Data(1000.0, 0.0, 0.1), Gencls.Data(5.0, 0.0, 0.1, gov) });
    }

    /// <summary>GB LFSM-U (type C): TGOV1 R = 0.05, T1 = 0.5 (T2 = T3, the lead-lag passes through), Pm0 = 0.5, a step to
    /// 49.1 Hz: dP(t) = 0.36 (1 - e^(-t/0.5)); dP_req = (49.5 - 49.1) / (50 x 0.1) x 0.5 = 0.04 (threshold 49.5 Hz, droop
    /// 10 %, Pref the output). Read at 10 s: margin 0.36 (1 - e^-20) - 0.04 = 0.3199999992579848.</summary>
    [Fact]
    public void Gb_lfsm_u_by_frequency_injection()
    {
        var c = GovernedCase(Tgov());
        GridCodeResult res = GridCodeStudy.Run(Request(c.Model, c.Data, GridCodeTechnology.Synchronous, ModuleType.C, 33,
            new LfsmRequest(false, 49.1, Pm0Pu: 0.5)));
        GridCodeCheckResult r = Assert.Single(res.Checks);
        Assert.Equal("lfsm-u", r.Id);
        Assert.Equal(RuleOutcome.Pass, r.Outcome);
        Assert.True(Math.Abs(r.Limit!.Value - 0.04) <= 1e-15, $"dP_req {r.Limit:R}");
        Assert.True(Math.Abs(r.Margin!.Value - 0.3199999992579848) <= 1e-12, $"margin {r.Margin:R}");
        Assert.Contains(r.Notes, n => n.Contains("no response time for LFSM-U", StringComparison.Ordinal));
    }

    /// <summary>GB LFSM-O with a slow governor (T1 = 50 s), Pm0 = 0.8, a step to 50.9 Hz: dP(10) = -0.36 (1 - e^-0.2) =
    /// -0.06525692889192654 against dP_req = -0.08, so it FAILs by 0.014743071108073463 at the code's 10 s. At 30 s it would
    /// pass (margin 0.0824), so a later read time must be refused under gb-ecc (ECC.6.3.7.1.2(iii): within 10 s).</summary>
    [Fact]
    public void Gb_lfsm_o_is_read_at_ten_seconds_so_a_slow_governor_fails_and_a_later_read_is_refused()
    {
        var c = GovernedCase(Tgov(t1: 50.0));
        GridCodeResult res = GridCodeStudy.Run(Request(c.Model, c.Data, GridCodeTechnology.Synchronous, ModuleType.C, 33,
            new LfsmRequest(true, 50.9, Pm0Pu: 0.8)));
        GridCodeCheckResult r = Assert.Single(res.Checks);
        Assert.Equal(RuleOutcome.Fail, r.Outcome);
        Assert.Equal(10.0, r.AtSeconds);
        Assert.True(Math.Abs(r.Value!.Value - -0.06525692889192654) <= 1e-12, $"dP(10) {r.Value:R}");
        Assert.True(Math.Abs(r.Margin!.Value - -0.014743071108073463) <= 1e-12, $"margin {r.Margin:R}");
        Assert.Equal(RuleSetEngine.NonCompliant, res.Verdict);

        var ex = Assert.Throws<InvalidDataException>(() => GridCodeStudy.Run(Request(c.Model, c.Data, GridCodeTechnology.Synchronous, ModuleType.C, 33,
            new LfsmRequest(true, 50.9, TEndSeconds: 30.0, Pm0Pu: 0.8))));
        Assert.Contains("tEndSeconds must be 10", ex.Message, StringComparison.Ordinal);
    }

    /// <summary>GB FSM: dP1 = 10 % of Pmax, droop at most 5 %, so a governor needs |df| of at least 0.1 x 50 x 0.05 = 0.25 Hz to
    /// reach dP1. A 0.2 Hz step is refused. With the unit's droop given as 3 % the step needs 0.15 Hz, and TGOV1 at R = 0.03
    /// gives dP(t) = (0.2 / 50 / 0.03)(1 - e^(-t/0.5)); dP(10) = 0.13333333305851286, margin at t2 0.033333333058512854 (the
    /// response settles before the line rises, so the minimum is at t2).</summary>
    [Fact]
    public void Gb_fsm_refuses_a_step_too_small_to_reach_dp1_and_sizes_it_by_the_given_droop()
    {
        var c = GovernedCase(Tgov(r: 0.03));
        var ex = Assert.Throws<InvalidDataException>(() => GridCodeStudy.Run(Request(c.Model, c.Data, GridCodeTechnology.Synchronous, ModuleType.C, 33,
            new FsmStepRequest(49.8, Pm0Pu: 0.5))));
        Assert.Contains("at least dP1 fn s = 0.25", ex.Message, StringComparison.Ordinal);

        GridCodeResult res = GridCodeStudy.Run(Request(c.Model, c.Data, GridCodeTechnology.Synchronous, ModuleType.C, 33,
            new FsmStepRequest(49.8, Pm0Pu: 0.5, Droop: 0.03)));
        GridCodeCheckResult r = Assert.Single(res.Checks);
        Assert.Equal(RuleOutcome.Pass, r.Outcome);
        Assert.True(Math.Abs(r.Value!.Value - 0.13333333305851286) <= 1e-12, $"dP(t2) {r.Value:R}");
        Assert.True(Math.Abs(r.Margin!.Value - 0.033333333058512854) <= 1e-12, $"margin {r.Margin:R}");
    }

    // ---- RMS frequency events ---------------------------------------------------------------------------------------

    /// <summary>
    /// Two machines with no transfer (both EMFs 1 at angle 0): the grid at bus 1, H1 = 5, behind 0.1 + line 0.2; the unit at
    /// bus 2 (the PCC), H2 = 15, behind 0.1. A load step G at bus 2 (dP at 1.0 pu, constant admittance) draws its current
    /// from the two sources in the ratio of their admittances, 3 : 1 to the unit, and H2 = 3 H1, so both rotors decelerate at
    /// the same rate and stay together: the system turns as one, at the RoCoF f0 P / (2 (H1 + H2)) with
    /// P = G |V2|^2 = G b^2 / (G^2 + b^2), b = 1/0.1 + 1/0.3. The unit's own rotor sees the same rate.
    /// dP = 40 MW: 0.49955040463582784 Hz/s; dP = 100 MW: 1.2430080795525171 Hz/s (GB limit 1 Hz/s over 500 ms).
    /// </summary>
    private static GridCodeResult RocofEvent(double loadMw, double pickupHzPerS)
    {
        var buses = new[] { new Bus(1, BusType.Slack, BaseKv: 33), new Bus(2, BusType.Pv, BaseKv: 33) };
        var gens = new[]
        {
            new Generator(1, 0.0, MBase: 100, H: 5, SubtransientXdPu: 0.1, Name: "grid"),
            new Generator(2, 0.0, MBase: 100, H: 15, Pmax: 100, SubtransientXdPu: 0.1, Name: "unit"),
        };
        var model = new GridModel { BaseMva = 100, Buses = buses, Generators = gens, Branches = new[] { new Branch(1, 2, 0.0, 0.2) } }
            .WithUnitProtection(Relays(("rocofHzPerS", pickupHzPerS), ("rocofDelayS", 0.1)));
        Solve(model);
        return GridCodeStudy.Run(Request(model, new[] { Gencls.Data(5.0, 0.0, 0.1), Gencls.Data(15.0, 0.0, 0.1) },
            GridCodeTechnology.Synchronous, ModuleType.B, 33,
            new RocofEventRequest(new RmsEvent[] { new RmsLoadStep(0.5, 2, loadMw, 0.0) }, 3.0)));
    }

    [Fact]
    public void Rocof_event_within_the_limit_fails_when_the_unit_81r_trips_and_passes_when_it_does_not()
    {
        GridCodeCheckResult trip = Assert.Single(RocofEvent(40.0, 0.4).Checks);
        Assert.True(Math.Abs(trip.Value!.Value - 0.49955040463582784) <= 1e-6, $"worst RoCoF {trip.Value:R}");
        Assert.Equal(1.0, trip.Limit);
        Assert.Equal(RuleOutcome.Fail, trip.Outcome);
        Assert.Contains("81R stage tripped it", trip.Reason, StringComparison.Ordinal);

        GridCodeCheckResult ride = Assert.Single(RocofEvent(40.0, 0.6).Checks);
        Assert.Equal(RuleOutcome.Pass, ride.Outcome);
        Assert.Equal(trip.Value, ride.Value);
    }

    [Fact]
    public void Rocof_event_above_the_limit_is_not_demonstrated()
    {
        GridCodeResult res = RocofEvent(100.0, 0.6);
        GridCodeCheckResult r = Assert.Single(res.Checks);
        Assert.True(Math.Abs(r.Value!.Value - 1.2430080795525171) <= 1e-6, $"worst RoCoF {r.Value:R}");
        Assert.Equal("NOT DEMONSTRATED", r.Status);
        Assert.Equal(RuleSetEngine.Incomplete, res.Verdict);
    }

    /// <summary>
    /// The network variant of GB LFSM-O. The grid machine at bus 1 (H = 50, no governor) behind 0.1 + line 0.2; at bus 2 a
    /// 50 MW converter unit with no frequency response and a 50 MW load, so nothing flows before the event. A 20 MW load
    /// reduction at 1 s leaves the grid machine absorbing about 0.2 pu, so the frequency rises at about 0.2 x 50 / (2 x 50) =
    /// 0.1 Hz/s and is near 51.0 Hz at 11 s, the code's 10 s after the event. Solving the post-event network (the load at
    /// G = 0.3 and the converter at 0.5 pu in phase with V2) gives |V2| = 0.99817 and a grid power of -0.20110 pu, so
    /// f(11) = 51.0055 Hz and dP_req = -(51.0055 - 50.4) / (50 x 0.1) x 0.5 = -0.0605. The converter holds 0.5 pu, so it
    /// FAILs. A run that does not end 10 s after the event is refused.
    /// </summary>
    [Fact]
    public void Gb_lfsm_o_network_variant_fails_a_converter_without_frequency_response()
    {
        var buses = new[] { new Bus(1, BusType.Slack, BaseKv: 132), new Bus(2, BusType.Pv, Pd: 50, BaseKv: 132) };
        var gens = new[]
        {
            new Generator(1, 0.0, MBase: 100, H: 50, SubtransientXdPu: 0.1, Name: "grid"),
            new Generator(2, 50.0, MBase: 100, Pmax: 50, Name: "unit", ConverterInterfaced: true),
        };
        (GridModel m, _) = Solve(new GridModel { BaseMva = 100, Buses = buses, Generators = gens, Branches = new[] { new Branch(1, 2, 0.0, 0.2) } });
        var data = new[] { Gencls.Data(50.0, 0.0, 0.1), Gencls.Data(5.0, 0.0, 0.1) };
        RmsEvent[] ev = { new RmsLoadStep(1.0, 2, -20.0, 0.0) };
        GridCodeResult res = GridCodeStudy.Run(Request(m, data, GridCodeTechnology.Ppm, ModuleType.B, 33,
            new LfsmRequest(true, 0.0, TEndSeconds: 11.0, Events: ev)));
        GridCodeCheckResult r = Assert.Single(res.Checks);
        Assert.Equal(RuleOutcome.Fail, r.Outcome);
        Assert.True(Math.Abs(r.Value!.Value) <= 1e-6, $"converter dP {r.Value:R}");
        Assert.True(Math.Abs(r.Limit!.Value - -0.0605) <= 2e-3, $"dP_req {r.Limit:R}");
        Assert.Equal(RuleSetEngine.NonCompliant, res.Verdict);

        var ex = Assert.Throws<InvalidDataException>(() => GridCodeStudy.Run(Request(m, data, GridCodeTechnology.Ppm, ModuleType.B, 33,
            new LfsmRequest(true, 0.0, TEndSeconds: 10.0, Events: ev))));
        Assert.Contains("tEndSeconds must be 11", ex.Message, StringComparison.Ordinal);
    }
}
