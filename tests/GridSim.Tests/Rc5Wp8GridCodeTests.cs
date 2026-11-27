using System.Globalization;
using GridSim.Core.Compliance.GridCode;
using GridSim.Core.Dynamics.Models;
using GridSim.Core.Harmonics.Planning;
using GridSim.Core.Model;
using GridSim.Core.PowerFlow;
using GridSim.Core.Protection;
using Xunit;

namespace GridSim.Tests;

/// <summary>
/// RC5 WP8: the grid-code compliance suite (<see cref="GridCodeStudy"/>, <see cref="GridCodeChecks"/>). Sources: GB Grid
/// Code ECC Issue 6 Revision 33 (ECC.6.3.15.2-.8, ECC.6.3.2, ECC.6.3.7, ECC.6.3.13, ECC.6.1.2.1.2-.3) and Regulation (EU)
/// 2016/631 (Art. 13-16 and 18, 21; Tables 2, 3.1, 3.2, 4, 5, 7.1, 7.2, 8, 9). The FRT profile shape is read from Figure 3
/// of the Regulation, Figure ECC.6.3.2.3 and the FSM trajectory of Figure 6 are read from the figures. Every hand value
/// below was recomputed in Python (IEEE double) before it was pinned; <c>Assert.Equal(double, double)</c> without a
/// precision is bit-exact.
/// </summary>
public sealed class Rc5Wp8GridCodeTests
{
    private static FrtProfile Gb(ModuleType t, string tech, double kv) => GridCodeRequirements.GbProfile(t, tech, kv);

    // ---- 1. Profiles -------------------------------------------------------------------------------------------------

    /// <summary>Hand values: the line a + (b - a)(t - t0)/(t1 - t0) of each segment in IEEE double (Python).</summary>
    [Fact]
    public void T1_gb_profiles_evaluate_bit_for_bit()
    {
        FrtProfile d = Gb(ModuleType.D, GridCodeTechnology.Synchronous, 132);
        Assert.Equal(0.0, d.LowerLimitPu(0.1));
        Assert.Equal(0.0, d.LowerLimitPu(0.14));                     // held at tclear itself
        Assert.Equal(0.38636363636363635, d.LowerLimitPu(0.2));     // 0.25 + 0.25 (0.06 / 0.11)
        Assert.Equal(0.5, d.LowerLimitPu(0.3));
        Assert.Equal(0.7095238095238096, d.LowerLimitPu(1.0));      // 0.5 + 0.4 (0.55 / 1.05)
        Assert.Equal(0.9, d.LowerLimitPu(2.0));
        Assert.Equal(0.8047619047619048, Gb(ModuleType.B, GridCodeTechnology.Synchronous, 33).LowerLimitPu(1.0));
        Assert.Equal(0.4131067961165049, Gb(ModuleType.C, GridCodeTechnology.Ppm, 33).LowerLimitPu(1.0));
        Assert.Equal(0.3548543689320388, Gb(ModuleType.D, GridCodeTechnology.Ppm, 132).LowerLimitPu(1.0));
    }

    [Theory]
    [InlineData(ModuleType.B, "synchronous", 33.0, "ECC.6.3.15.2", 0.3, 0.7, 0.7, 0.9, 0.14, 0.14, 0.45, 1.5)]
    [InlineData(ModuleType.C, "synchronous", 33.0, "ECC.6.3.15.3", 0.1, 0.7, 0.7, 0.9, 0.14, 0.14, 0.45, 1.5)]
    [InlineData(ModuleType.D, "synchronous", 66.0, "ECC.6.3.15.3", 0.1, 0.7, 0.7, 0.9, 0.14, 0.14, 0.45, 1.5)]
    [InlineData(ModuleType.D, "synchronous", 132.0, "ECC.6.3.15.4", 0.0, 0.25, 0.5, 0.9, 0.14, 0.25, 0.45, 1.5)]
    [InlineData(ModuleType.B, "ppm", 33.0, "ECC.6.3.15.5", 0.10, 0.10, 0.10, 0.85, 0.14, 0.14, 0.14, 2.2)]
    [InlineData(ModuleType.D, "ppm-gfm", 66.0, "ECC.6.3.15.5", 0.10, 0.10, 0.10, 0.85, 0.14, 0.14, 0.14, 2.2)]
    [InlineData(ModuleType.D, "ppm", 132.0, "ECC.6.3.15.6", 0.0, 0.0, 0.0, 0.85, 0.14, 0.14, 0.14, 2.2)]
    [InlineData(ModuleType.C, "dc-ppm", 33.0, "ECC.6.3.15.6", 0.0, 0.0, 0.0, 0.85, 0.14, 0.14, 0.14, 2.2)]
    [InlineData(ModuleType.D, "hvdc", 400.0, "ECC.6.3.15.7", 0.0, 0.0, 0.0, 0.85, 0.14, 0.14, 0.14, 2.2)]
    public void T1_every_gb_table_reads_back_exactly(ModuleType t, string tech, double kv, string clause,
        double uret, double uclear, double urec1, double urec2, double tclear, double trec1, double trec2, double trec3)
    {
        FrtProfile p = Gb(t, tech, kv);
        Assert.Equal(clause, p.Clause);
        Assert.Equal(new[] { uret, uclear, urec1, urec2, tclear, trec1, trec2, trec3 },
            new[] { p.Uret, p.Uclear, p.Urec1, p.Urec2, p.Tclear, p.Trec1, p.Trec2, p.Trec3 });
    }

    [Fact]
    public void T1_a_type_b_or_c_module_at_110_kv_or_above_is_refused()
        => Assert.Contains("type D", Assert.Throws<InvalidDataException>(() => Gb(ModuleType.C, GridCodeTechnology.Synchronous, 132)).Message, StringComparison.Ordinal);

    // ---- 2. RfG ranges -----------------------------------------------------------------------------------------------

    private static RfgFrtTable Table(string id) => GridCodeRequirements.Load("eu-rfg-2016-631").RfgTables.Single(t => t.Table == id);

    [Fact]
    public void T2_rfg_choices_outside_the_tables_are_refused_by_parameter_range_and_article()
    {
        // Table 7.2 (PPM, type D at or above 110 kV): trec3 1.5-3.0.
        var v = RfgRanges.Validate(new FrtProfile(0, 0, 0, 0.85, 0.14, 0.14, 0.14, 3.5, "tso"), Table("7.2"));
        string m = Assert.Single(v);
        Assert.Contains("trec3 = 3.5", m, StringComparison.Ordinal);
        Assert.Contains("1,5-3,0", m, StringComparison.Ordinal);
        Assert.Contains("Art. 16(3)(a), Table 7.2", m, StringComparison.Ordinal);
        // Table 7.1 (synchronous D): Uret is 0.
        m = Assert.Single(RfgRanges.Validate(new FrtProfile(0.1, 0.25, 0.5, 0.85, 0.14, 0.25, 0.45, 1.5, "tso"), Table("7.1")));
        Assert.StartsWith("uret = 0.1 ", m, StringComparison.Ordinal);
        Assert.Contains("Table 7.1", m, StringComparison.Ordinal);
        // Table 3.1: tclear 0.14-0.15, or 0.14-0.25 only with the extended clearing time.
        var p = new FrtProfile(0.05, 0.7, 0.7, 0.85, 0.2, 0.2, 0.7, 1.5, "tso");
        m = Assert.Single(RfgRanges.Validate(p, Table("3.1")));
        Assert.StartsWith("tclear = 0.2 ", m, StringComparison.Ordinal);
        Assert.Empty(RfgRanges.Validate(p, Table("3.1"), extendedClearingTime: true));
        // Table 9, Great Britain: the Q/Pmax span is at most 0.66; 0.35 - (-0.35) = 0.7.
        ReactiveLimitTable t9 = GridCodeRequirements.Load("eu-rfg-2016-631").ReactiveLimits.Single(t => t.Clause.Contains("Table 9", StringComparison.Ordinal));
        var up = new UqProfile(new[] { new UqCorner(0.95, -0.35, 0.35), new UqCorner(1.05, -0.35, 0.35) }, "test");
        m = Assert.Single(RfgRanges.ValidateUqProfile(up, t9, "GB"));
        Assert.Contains("exceeds the maximum 0.66", m, StringComparison.Ordinal);
        Assert.Empty(RfgRanges.ValidateUqProfile(up with { Corners = new[] { new UqCorner(0.95, -0.33, 0.33) } }, t9, "GB"));
    }

    // ---- 3. UV settings ----------------------------------------------------------------------------------------------

    /// <summary>GB D synchronous: the line crosses 0.8 pu at t = 0.45 + 0.3 x 1.05 / 0.4 = 1.2375 s; below 0.2 pu only during
    /// [0, 0.14] (Uret = 0 held to tclear, then Uclear = 0.25).</summary>
    [Fact]
    public void T3_undervoltage_stages_against_the_line()
    {
        FrtProfile d = Gb(ModuleType.D, GridCodeTechnology.Synchronous, 132);
        Assert.Equal(1.2375, d.TimeBelow(0.8));
        var r = GridCodeChecks.FrtUvSettings(d, new[]
        {
            new GridCodeRelayStage("27-1", "27", 0.8, 1.0), new GridCodeRelayStage("27-2", "27", 0.8, 1.5), new GridCodeRelayStage("27-3", "27", 0.2, 0.15),
        });
        Assert.Equal(RuleOutcome.Fail, r[0].Outcome);
        Assert.Equal(-0.23750000000000004, r[0].Margin);
        Assert.Equal(RuleOutcome.Pass, r[1].Outcome);
        Assert.Equal(0.26249999999999996, r[1].Margin);
        Assert.Equal(0.14, r[2].Limit);
        Assert.Equal(RuleOutcome.Pass, r[2].Outcome);
        Assert.Equal(0.009999999999999981, r[2].Margin);
    }

    // ---- 4. Retained voltage -----------------------------------------------------------------------------------------

    [Fact]
    public void T4_retained_voltage_reactance()
    {
        Assert.Equal(0.08571428571428572, GridCodeChecks.RetainedVoltageReactance(0.3, 0.2));   // 0.3 x 0.2 / 0.7
        Assert.Equal(0.0, GridCodeChecks.RetainedVoltageReactance(0.0, 0.2));                   // Uret = 0: bolted
    }

    // ---- 5. FRT on RMS -----------------------------------------------------------------------------------------------

    /// <summary>
    /// Bus 1: GENCLS H = 1000, X'd = 0.1; line 1-2 X = 0.2; bus 2 (PCC): the unit, GENCLS X'd = 0.1, P = Q = 0; flat power
    /// flow, both EMFs 1 at angle 0, Sb = MBase = 100. The Thevenin reactance at bus 2 is (0.2 + 0.1) in parallel with 0.1
    /// = 0.03 / 0.4 = 0.075 pu. (The spec gave 0.2 x 0.1 / 0.3, which leaves out the bus-1 machine's own X'd; the RMS network
    /// carries both machines behind X'd, so the retained voltage is set by 0.075.)
    /// </summary>
    private static (GridModel Model, PowerFlowResult Pf, IReadOnlyList<SubtransientInit.MachineData> Data) FrtCase(bool converterUnit = false)
    {
        var buses = new[] { new Bus(1, BusType.Slack, BaseKv: 132), new Bus(2, BusType.Pv, BaseKv: 132) };
        Generator unit = converterUnit
            ? new Generator(2, 50.0, MBase: 100, Pmax: 50, Name: "unit", ConverterInterfaced: true)
            : new Generator(2, 0.0, MBase: 100, H: 5, Pmax: 100, SubtransientXdPu: 0.1, Name: "unit");
        var gens = new[] { new Generator(1, 0.0, MBase: 100, H: 1000, SubtransientXdPu: 0.1, Name: "grid"), unit };
        var model = new GridModel { BaseMva = 100, Buses = buses, Generators = gens, Branches = new[] { new Branch(1, 2, 0.0, 0.2) } };
        PowerFlowResult pf = new NewtonRaphsonPowerFlow().Solve(model);
        Assert.True(pf.Converged);
        return (model, pf, new[] { Gencls.Data(1000.0, 0.0, 0.1), Gencls.Data(5.0, 0.0, 0.1) });
    }

    private static GridCodeRequest Request((GridModel Model, PowerFlowResult Pf, IReadOnlyList<SubtransientInit.MachineData> Data) c,
        string tech, ModuleType mt, double kv, params GridCodeCheckRequest[] checks) => new()
        {
            Label = "wp8", Code = "gb-ecc", ModuleType = mt, Technology = tech, ConnectionKv = kv, Model = c.Model, Data = c.Data,
            UnitIndex = 1, PccBus = 2, PlantBuses = Array.Empty<int>(), Checks = checks,
        };

    /// <summary>The unit here runs at P = 0 with Pmax = 100 MW, so the run is on the line but the ECC.6.3.15.8 pre-fault
    /// condition (rated MW) is not met: NOT DEMONSTRATED. A unit at its rated MW that passes, and one that fails, are in
    /// <c>Rc5ReviewFix4GridCodeTests</c>.</summary>
    [Fact]
    public void T5_frt_on_rms_at_the_retained_voltage_is_on_the_line_but_below_rated_mw_is_not_demonstrated()
    {
        var c = FrtCase();
        double xth = GridCodeChecks.TheveninPu(c.Model, c.Pf, 2);
        Assert.True(Math.Abs(xth - 0.075) <= 1e-12, $"Xth {xth:R}");
        FrtProfile b = Gb(ModuleType.B, GridCodeTechnology.Synchronous, 33);
        double xf = GridCodeChecks.RetainedVoltageReactance(b.Uret, xth);
        Assert.True(Math.Abs(xf - 0.3 * 0.075 / 0.7) <= 1e-12);

        GridCodeResult res = GridCodeStudy.Run(Request(c, GridCodeTechnology.Synchronous, ModuleType.B, 33,
            new FrtEnvelopeRequest(new FrtFault(AtSeconds: 0.1), DurationSeconds: 3.0)));
        FrtRun run = Assert.Single(res.FrtRuns);
        Assert.Equal(xf, run.ReactancePu);
        Assert.True(run.Observed.Result.StayedInSynchronism);
        // With no power transfer V(2) = Xf / (Xth + Xf) = 0.3 during the fault and 1.0 after it.
        IReadOnlyList<double> t = run.Observed.Buses.TimeSeconds;
        double[] v = run.Observed.Buses.MagnitudePu[0];
        int during = 0;
        for (int k = 0; k < t.Count; k++)
        {
            if (t[k] >= 0.1 - 1e-9 && t[k] < 0.24 - 1e-9) { Assert.True(Math.Abs(v[k] - 0.3) <= 1e-12, $"V({t[k]:R}) = {v[k]:R}"); during++; }
            else Assert.True(Math.Abs(v[k] - 1.0) <= 1e-12, $"V({t[k]:R}) = {v[k]:R}");
        }
        Assert.Equal(14, during);
        GridCodeCheckResult r = Assert.Single(res.Checks);
        Assert.Equal("NOT DEMONSTRATED", r.Status);
        Assert.Equal(RuleOutcome.NotEvaluated, r.Outcome);
        Assert.Contains("P(0-) = 0 MW is below the rated MW 100 MW", r.Reason, StringComparison.Ordinal);
        Assert.True(Math.Abs(r.Margin!.Value) <= 1e-9, $"margin {r.Margin:R}");
        Assert.Equal(RuleSetEngine.Incomplete, res.Verdict);

        // 11. Bit-identity: the same study through StudyEventsObserved directly gives the same bits for V(PCC).
        RmsTransientStability.ObservedStudy direct = RmsTransientStability.StudyEventsObserved(c.Model, c.Pf, c.Data, run.Events, new[] { 2 },
            stepSeconds: 0.01, durationSeconds: 3.0);
        Assert.Equal(direct.Buses.TimeSeconds, run.Observed.Buses.TimeSeconds);
        Assert.Equal(direct.Buses.MagnitudePu[0], v);
        Assert.Equal(direct.Result.Machines[1].DeltaRad, run.Observed.Result.Machines[1].DeltaRad);
    }

    [Fact]
    public void T5_a_deeper_fault_than_the_line_is_not_demonstrated()
    {
        var c = FrtCase();
        double xf = GridCodeChecks.RetainedVoltageReactance(0.3, GridCodeChecks.TheveninPu(c.Model, c.Pf, 2));
        GridCodeResult res = GridCodeStudy.Run(Request(c, GridCodeTechnology.Synchronous, ModuleType.B, 33,
            new FrtEnvelopeRequest(new FrtFault(ReactancePu: 0.9 * xf), DurationSeconds: 3.0)));
        GridCodeCheckResult r = Assert.Single(res.Checks);
        Assert.Equal("NOT DEMONSTRATED", r.Status);
        Assert.Equal(RuleOutcome.NotEvaluated, r.Outcome);
        // V = 0.9 Xf / (Xth + 0.9 Xf) = 0.27 / 0.97 = 27/97 (Xf scales with Xth, so the ratio does not depend on it).
        Assert.True(Math.Abs(r.Value!.Value - 0.27835051546391754) <= 1e-12, $"V {r.Value:R}");
        Assert.True(Math.Abs(r.Margin!.Value - -0.021649484536082453) <= 1e-12, $"margin {r.Margin:R}");
        Assert.True(r.AtSeconds is >= 0.0 and < 0.14, $"time of the minimum {r.AtSeconds:R}");
        Assert.Equal(RuleSetEngine.Incomplete, res.Verdict);
    }

    /// <summary>
    /// A converter unit at its rated 50 MW (Sb 100) through a fault at the PCC: the recovery check reads its trace. Independent
    /// value: in the dip the model freezes the active current at its pre-fault value (its voltage-dependent limit sets the
    /// output to 0 below 0.4 pu), so once the voltage is back the output returns to P0 = 0.5 pu; its lags (Tg = Tr = 0.02 s)
    /// have decayed by e^-25 = 1.4e-11 at tv + 0.5 s, and the H = 1000 grid machine turns by under 1e-4 rad per step, so the
    /// power is P0 to well within 1e-6 from then on. The margin is P0 - 0.9 P0 = 0.05 pu, to 1e-6.
    /// </summary>
    [Fact]
    public void T5_frt_recovery_of_a_converter_unit_reads_its_trace()
    {
        var c = FrtCase(converterUnit: true);
        GridCodeResult res = GridCodeStudy.Run(Request(c, GridCodeTechnology.Ppm, ModuleType.B, 33,
            new FrtRecoveryRequest(new FrtFault(ReactancePu: 0.05), DurationSeconds: 3.0)));
        GridCodeCheckResult r = Assert.Single(res.Checks);
        Assert.Equal(RuleOutcome.Pass, r.Outcome);
        Assert.True(Math.Abs(r.Margin!.Value - 0.05) <= 1e-6, $"margin {r.Margin:R}");
        Assert.True(Math.Abs(r.Limit!.Value - 0.45) <= 1e-6, $"0.9 P0 {r.Limit:R}");    }

    /// <summary>The same converter at 25 MW of its 50 MW: the ECC.6.3.15.8 pre-fault condition (rated MW) is not met, so the
    /// recovery is NOT DEMONSTRATED.</summary>
    [Fact]
    public void T5_frt_recovery_below_rated_mw_is_not_demonstrated()
    {
        var c = FrtCase(converterUnit: true);
        GridModel m = c.Model.CopyWith(generators: c.Model.Generators.Select(g => g.Name == "unit" ? g with { Pg = 25.0 } : g).ToList(), generatorsAreWholeFleet: true);
        PowerFlowResult pf = new NewtonRaphsonPowerFlow().Solve(m);
        Assert.True(pf.Converged);
        GridCodeResult res = GridCodeStudy.Run(Request((m, pf, c.Data), GridCodeTechnology.Ppm, ModuleType.B, 33,
            new FrtRecoveryRequest(new FrtFault(ReactancePu: 0.05), DurationSeconds: 3.0)));
        GridCodeCheckResult r = Assert.Single(res.Checks);
        Assert.Equal("NOT DEMONSTRATED", r.Status);
        Assert.Contains("below the rated MW", r.Reason, StringComparison.Ordinal);
    }

    // ---- 6. Q capability ---------------------------------------------------------------------------------------------

    /// <summary>Two-bus: the unit at bus 1, the PCC at bus 2 through jX = 0.1, R = 0. Closed form: Vg^2 = (a + sqrt(a^2 -
    /// 4 X^2 (P^2 + Q^2))) / 2 with a = Vp^2 + 2 X Q, Q_pcc = Q - X (P^2 + Q^2) / Vg^2 (Python). Tolerance 1e-6 Mvar, the
    /// power-flow tolerance.</summary>
    private static GridModel QCase() => new()
    {
        BaseMva = 100,
        Buses = new[] { new Bus(1, BusType.Pv, BaseKv: 33), new Bus(2, BusType.Slack, BaseKv: 33) },
        Generators = new[] { new Generator(1, 100, Qmin: -50, Qmax: 50, Pmax: 100, MBase: 100, Name: "unit"), new Generator(2, 0, MBase: 100, Name: "grid") },
        Branches = new[] { new Branch(1, 2, 0.0, 0.1) },
    };

    private static GridCodeRequest QRequest(ModuleType mt, string tech = GridCodeTechnology.Synchronous, QCapabilityRequest? q = null) => new()
    {
        Label = "q", Code = "gb-ecc", ModuleType = mt, Technology = tech, ConnectionKv = 33, Model = QCase(), UnitIndex = 0, PccBus = 2,
        PlantBuses = new[] { 1 }, Checks = new GridCodeCheckRequest[] { q ?? new QCapabilityRequest() },
    };

    private static void Near(double want, double? got, double tol, string what) =>
        Assert.True(got is double g && Math.Abs(g - want) <= tol, $"{what}: want {want:R}, got {got:R}");

    [Fact]
    public void T6_q_capability_two_bus()
    {
        GridModel m = QCase();
        Near(38.516480713450406, GridCodeChecks.PccReactiveMvar(m, 0, 2, new[] { 1 }, 100, 50, 1.0), 1e-6, "Q(Qmax), 1.0");
        Near(-64.11010564593265, GridCodeChecks.PccReactiveMvar(m, 0, 2, new[] { 1 }, 100, -50, 1.0), 1e-6, "Q(Qmin), 1.0");

        GridCodeResult b = GridCodeStudy.Run(QRequest(ModuleType.B));
        Assert.Equal(2, b.Checks.Count);
        Near(32.86841051788632, b.Checks[0].Limit, 1e-12, "GB B lagging requirement");
        Near(5.648070195564081, b.Checks[0].Margin, 1e-6, "lagging margin");
        Near(31.241695128046345, b.Checks[1].Margin, 1e-6, "leading margin");
        Assert.All(b.Checks, x => Assert.Equal(RuleOutcome.Pass, x.Outcome));
        Assert.Equal(RuleSetEngine.Compliant, b.Verdict);

        GridCodeResult cd = GridCodeStudy.Run(QRequest(ModuleType.C));
        Assert.Equal(6, cd.Checks.Count);   // 0.95, 1.0 and 1.05 pu, lagging and leading
        Near(42.59982161362048, cd.Checks[2].Limit, 1e-12, "GB C/D lagging requirement");
        Near(-4.083340900170079, cd.Checks[2].Margin, 1e-6, "lagging margin at 1.0 pu");
        Assert.Equal(RuleOutcome.Fail, cd.Checks[2].Outcome);
        Near(37.37210602877967, cd.Checks[0].Value, 1e-6, "Q(Qmax), 0.95");
        Near(-65.89099530437854, cd.Checks[1].Value, 1e-6, "Q(Qmin), 0.95");
        Near(39.513541952277215, cd.Checks[4].Value, 1e-6, "Q(Qmax), 1.05");
        Near(-62.62789397122033, cd.Checks[5].Value, 1e-6, "Q(Qmin), 1.05");
        Assert.Equal(RuleSetEngine.NonCompliant, cd.Verdict);
    }

    // ---- 7. LFSM-O ---------------------------------------------------------------------------------------------------

    private static Tgov1Data Tgov(double t1 = 0.5) => new(0.05, t1, 1.0, 1.0, 0.0, 0.0, 1.0);

    /// <summary>TGOV1 R = 0.05, T1 = 0.5, T2 = T3 = 1 (the lead-lag is an exact pass-through), Pm0 = 0.8; a step to 50.9 Hz:
    /// dP(t) = -0.36 (1 - e^(-t/0.5)); GB dP_req = -(0.5 / 5) x 0.8 = -0.08 (threshold 50.4 Hz, droop 10 %, Pref the output).</summary>
    [Fact]
    public void T7_lfsm_o_by_frequency_injection()
    {
        GridCodeCheckResult r = GridCodeChecks.Lfsm(true, Tgov(), 0.8, 50.9, 50.0, 50.4, 0.10, 0.8, 0.01, 30.0, "ECC.6.3.7.1.1-.2");
        Assert.Equal(RuleOutcome.Pass, r.Outcome);
        Near(-0.08, r.Limit, 1e-15, "dP_req");
        Near(0.28, r.Margin, 1e-12, "margin");
        Assert.Equal(-0.04000000000000057, GridCodeChecks.RequiredChange(50.6, 50.4, 50.0, 0.1, 1.0, over: true));
        Assert.Equal(0.0, GridCodeChecks.RequiredChange(50.3, 50.4, 50.0, 0.1, 1.0, over: true));
    }

    // ---- 8. FSM step -------------------------------------------------------------------------------------------------

    /// <summary>The same TGOV1 (Vmax 1) and a step to 49.7 Hz: dw = -0.006, dPss = 0.12; t1 = 2, t2 = 10, dP1 = 0.10.
    /// dP(10) = 0.12 (1 - e^-20); with T1 = 10, dP(10) = 0.12 (1 - e^-1) and the shortfall at t2 is the minimum (the line
    /// rises at 0.0125 /s, faster than the response's 0.012 e^(-t/10) /s).</summary>
    [Fact]
    public void T8_fsm_step_by_frequency_injection()
    {
        GridCodeCheckResult r = GridCodeChecks.FsmStep(Tgov(), 0.8, 49.7, 50.0, 2.0, 10.0, 0.10, 0.01, 10.0, "ECC.6.3.7.3.3");
        Assert.Equal(RuleOutcome.Pass, r.Outcome);
        Near(0.11999999975266157, r.Value, 1e-12, "dP(10)");
        Near(0.019999999752661562, r.Margin, 1e-12, "margin");
        Near(10.0, r.AtSeconds, 1e-12, "time of the minimum");

        GridCodeCheckResult slow = GridCodeChecks.FsmStep(Tgov(10.0), 0.8, 49.7, 50.0, 2.0, 10.0, 0.10, 0.01, 10.0, "ECC.6.3.7.3.3");
        Assert.Equal(RuleOutcome.Fail, slow.Outcome);
        Near(0.07585446705942692, slow.Value, 1e-12, "dP(10), T1 = 10");
        Near(-0.02414553294057309, slow.Margin, 1e-12, "margin, T1 = 10");

        // Every sample against the exact zero-order-hold lag 0.12 (1 - e^(-t/T1)).
        FrequencyInjectionTrace tr = FrequencyInjection.Run(Tgov(), 0.8, _ => 49.7, 50.0, 0.01, 10.0);
        for (int k = 0; k < tr.TimeSeconds.Length; k++)
            Assert.True(Math.Abs(tr.DeltaPu(k) - 0.12 * (1.0 - Math.Exp(-tr.TimeSeconds[k] / 0.5))) <= 1e-12, $"sample {k}");
    }

    // ---- 9. RoCoF and frequency relays -------------------------------------------------------------------------------

    [Fact]
    public void T9_rocof_and_frequency_relay_settings()
    {
        var rocof = GridCodeChecks.RocofSettings(1.0, 0.5, "ECC.6.3.13.2",
            new[] { new GridCodeRelayStage("81R-1", "81R", 0.5, 0.5), new GridCodeRelayStage("81R-2", "81R", 1.2, 0.5) });
        Assert.Equal(RuleOutcome.Fail, rocof[0].Outcome);
        Assert.Equal(-0.5, rocof[0].Margin);
        Assert.Equal(RuleOutcome.Pass, rocof[1].Outcome);
        Assert.Equal(0.19999999999999996, rocof[1].Margin);

        GridCodeRequirementSet gb = GridCodeRequirements.Load("gb-ecc-issue6-rev33");
        var f = GridCodeChecks.FrequencyRelays(gb.FrequencyBands, gb.FrequencyBandsClause, gb.NoTripLowHz, gb.NoTripHighHz, gb.NoTripClause, new[]
        {
            new GridCodeRelayStage("81U-1", "81U", 48.0, 0.5), new GridCodeRelayStage("81U-2", "81U", 47.2, 20.0), new GridCodeRelayStage("81U-3", "81U", 47.2, 5.0),
        });
        Assert.Equal(RuleOutcome.Fail, f[0].Outcome);
        Assert.Equal("ECC.6.1.2.1.3", f[0].Clause);
        Assert.Equal(RuleOutcome.Pass, f[1].Outcome);
        Assert.Equal(0.0, f[1].Margin);
        Assert.Equal(RuleOutcome.Fail, f[2].Outcome);
        Assert.Equal(-15.0, f[2].Margin);
    }

    // ---- 10. Never a silent pass -------------------------------------------------------------------------------------

    [Fact]
    public void T10_what_cannot_be_evaluated_is_never_a_pass()
    {
        var c = FrtCase();
        GridCodeResult rec = GridCodeStudy.Run(Request(c, GridCodeTechnology.Synchronous, ModuleType.B, 33, new FrtRecoveryRequest(new FrtFault())));
        Assert.Equal(RuleOutcome.NotEvaluated, Assert.Single(rec.Checks).Outcome);
        Assert.Equal(RuleSetEngine.Incomplete, rec.Verdict);

        GridCodeResult q = GridCodeStudy.Run(QRequest(ModuleType.C, GridCodeTechnology.Ppm));
        GridCodeCheckResult r = Assert.Single(q.Checks);
        Assert.Equal(RuleOutcome.NotEvaluated, r.Outcome);
        Assert.Contains("figure not transcribed", r.Reason, StringComparison.Ordinal);
        Assert.Equal(RuleSetEngine.Incomplete, q.Verdict);
    }

    // ---- 12. Determinism ---------------------------------------------------------------------------------------------

    private static GridCodeRequest StaticRequest() => QRequest(ModuleType.D) with
    {
        ConnectionKv = 132,
        Checks = new GridCodeCheckRequest[]
        {
            new QCapabilityRequest(),
            new FrtUvSettingsRequest(new[] { new GridCodeRelayStage("27-1", "27", 0.8, 1.5) }),
            new FrequencyRelaysRequest(new[] { new GridCodeRelayStage("81U-1", "81U", 47.2, 20.0) }),
            new RocofSettingsRequest(new[] { new GridCodeRelayStage("81R-1", "81R", 1.2, 0.5, 0.2) }),
        },
    };

    [Fact]
    public void T12_the_document_is_byte_identical_on_two_runs_and_under_another_culture()
    {
        string a = GridCodeStudy.Write(GridCodeStudy.Run(StaticRequest()));
        string b = GridCodeStudy.Write(GridCodeStudy.Run(StaticRequest()));
        Assert.Equal(a, b);
        CultureInfo saved = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = new CultureInfo("de-DE");
            Assert.Equal(a, GridCodeStudy.Write(GridCodeStudy.Run(StaticRequest())));
        }
        finally { CultureInfo.CurrentCulture = saved; }
        Assert.Contains("\"format\": \"gridsim-gridcode/1\"", a, StringComparison.Ordinal);
        Assert.Contains("\"margin\": 0.26249999999999996", a, StringComparison.Ordinal);
        Assert.DoesNotContain("\r", a, StringComparison.Ordinal);
    }

    [Fact]
    public void T12_the_cli_verb_gives_the_same_bytes_twice_and_refuses_bad_input()
    {
        string dir = TestTemp.Under("gridsim-wp8-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            File.WriteAllText(Path.Combine(dir, "twobus.m"), string.Join("\n",
                "function mpc = twobus", "mpc.version = '2';", "mpc.baseMVA = 100;",
                "mpc.bus = [", "1 2 0 0 0 0 1 1 0 33 1 1.1 0.9;", "2 3 0 0 0 0 1 1 0 33 1 1.1 0.9;", "];",
                "mpc.gen = [", "1 100 0 50 -50 1 100 1 100 0;", "2 0 0 999 -999 1 100 1 0 0;", "];",
                "mpc.branch = [", "1 2 0 0.1 0 0 0 0 0 0 1 -360 360;", "];", ""));
            string study = """
                {
                  "schema": "gridsim-gridcode-study/1", "label": "two-bus", "case": "twobus.m", "unit": { "bus": 1 }, "pccBus": 2, "plantBuses": [1],
                  "code": "gb-ecc", "moduleType": "B", "technology": "synchronous", "connectionKv": 33,
                  "checks": [
                    { "id": "q-capability" },
                    { "id": "frt-uv-settings", "stages": [ { "device": "27-1", "function": "27", "pickup": 0.2, "delaySeconds": 0.5 } ] },
                    { "id": "frequency-relays", "stages": [ { "device": "81U-1", "function": "81U", "pickup": 47.0, "delaySeconds": 0.5 } ] },
                    { "id": "rocof-settings", "stages": [ { "device": "81R-1", "function": "81R", "pickup": 1.0, "delaySeconds": 0.5 } ] }
                  ]
                }
                """;
            string path = Path.Combine(dir, "study.json");
            File.WriteAllText(path, study);
            string oa = Path.Combine(dir, "a.json"), ob = Path.Combine(dir, "b.json");
            CliProcess.Result r1 = CliProcess.RunBuilt("grid-code", path, "--out", oa);
            CliProcess.Result r2 = CliProcess.RunBuilt("grid-code", path, "--out", ob);
            Assert.True(r1.ExitCode == 0, r1.ToString());
            Assert.Equal(r1.StdOut, r2.StdOut);
            Assert.Equal(File.ReadAllBytes(oa), File.ReadAllBytes(ob));
            Assert.Contains("\"verdict\": \"COMPLIANT\"", r1.StdOut, StringComparison.Ordinal);

            Assert.Equal(0, CliProcess.RunBuilt("grid-code", "--list").ExitCode);
            Assert.Equal(2, CliProcess.RunBuilt("grid-code").ExitCode);
            // grid-code is on the shared result writer since 2.1.091: --json exits 0 and writes a gridsim-cli-result/1 document.
            string cj = Path.Combine(dir, "c.json");
            Assert.Equal(0, CliProcess.RunBuilt("grid-code", path, "--json", cj).ExitCode);
            using (var result = System.Text.Json.JsonDocument.Parse(File.ReadAllText(cj)))
            {
                Assert.Equal("gridsim-cli-result/1", result.RootElement.GetProperty("schema").GetString());
                Assert.Equal("grid-code", result.RootElement.GetProperty("verb").GetString());
                Assert.Equal(0, result.RootElement.GetProperty("exitCode").GetInt32());
            }
            File.WriteAllText(path, study.Replace("\"moduleType\": \"B\"", "\"moduleType\": \"E\"", StringComparison.Ordinal));
            Assert.Equal(1, CliProcess.RunBuilt("grid-code", path).ExitCode);
        }
        finally { Directory.Delete(dir, recursive: true); }
    }
}
