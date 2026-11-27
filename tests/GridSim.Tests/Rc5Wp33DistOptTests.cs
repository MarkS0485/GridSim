using System.Globalization;
using System.Numerics;
using System.Security.Cryptography;
using System.Text.Json;
using GridSim.Core.Analysis.DistributionOptimisation;
using GridSim.Core.Analysis.Opf;
using GridSim.Core.Analysis.Reconfiguration;
using GridSim.Core.Distribution.Meshed;
using GridSim.Core.Journal;
using GridSim.Core.Model;
using Xunit;

namespace GridSim.Tests;

/// <summary>
/// RC5 WP33: distribution optimisation, the <c>dist-opt</c> verb: capacitor placement (<see cref="CapacitorPlacement"/>),
/// volt/VAr optimisation and CVR (<see cref="VoltVarOptimisation"/>) and phase balancing (<see cref="PhaseBalancing"/>).
///
/// Sources of the expected values: closed forms of a two-bus feeder, re-derived outside the repo in Python with mpmath at
/// 50 digits (mp.findroot on the bus-2 power balance) and cross-checked with SciPy 1.18 fsolve (agreement 4e-16 MW on the
/// losses). The capacitor objective follows Grainger and Lee, IEEE Trans. PAS-100(3), 1981. The phase-balancing feeder's
/// neutral current and unbalance factor before balancing come from an independent SciPy fsolve of the same four-wire
/// circuit. Each figure below is quoted from those runs.
///
/// Tolerances are derived in each test from the Newton tolerance and the case's sensitivities, never fitted.
/// </summary>
public sealed class Rc5Wp33DistOptTests
{
    private static long Bits(double v) => BitConverter.DoubleToInt64Bits(v);

    // ---- cases -------------------------------------------------------------------------------------------------------

    /// <summary>Two buses on 100 MVA: source V = 1.0, line R = 0.01, X = 0.1 pu, load 50 MW + 30 Mvar at bus 2.</summary>
    private static GridModel CapCase() => new()
    {
        Name = "cap2", BaseMva = 100,
        Buses = new[] { new Bus(1, BusType.Slack), new Bus(2, BusType.Pq, Pd: 50, Qd: 30) },
        Generators = new[] { new Generator(1, 0) },
        Branches = new[] { new Branch(1, 2, 0.01, 0.1) },
    };

    private static readonly double[] CapSteps = { 0, 10, 20, 30, 40, 50, 60 };

    // Bus-2 losses (MW) with 0, 10, ..., 60 Mvar at bus 2 (mpmath, 50 digits; SciPy agrees).
    private static readonly double[] CapLossMw =
    {
        0.36705458450197805317, 0.30894367026122651145, 0.27063845064971464752, 0.2534017724218292289,
        0.25857620075780821391, 0.28758962403688293825, 0.34196130411698284317,
    };

    /// <summary>
    /// A source bus 1 feeding bus 2 through an on-load tap changer "T1" (ratio at the bus-1 end, so V2 = V1 / tau through
    /// the series impedance) of R + jX, load 100 MW at bus 2 (Q 0). <paramref name="zip"/> makes the load constant impedance.
    /// </summary>
    private static GridModel TapCase(double r, double x, double tap0, double minTap, double maxTap, bool constantImpedance) => new()
    {
        Name = "tap2", BaseMva = 100,
        Buses = new[]
        {
            new Bus(1, BusType.Slack),
            new Bus(2, BusType.Pq, Pd: 100, Qd: 0, Zip: constantImpedance ? new ZipLoad(Ap: 1, Bp: 0, Cp: 0, Aq: 1, Bq: 0, Cq: 0) : null),
        },
        Generators = new[] { new Generator(1, 0) },
        Branches = new[] { new Branch(1, 2, r, x, TapRatio: tap0, Name: "T1", IsTransformer: true) },
        Oltcs = new[] { new Oltc("T1", 2, TargetVpu: 1.0, MinTap: minTap, MaxTap: maxTap, StepPu: 0.00625) },
    };

    // ---- T1, T2: capacitor placement ---------------------------------------------------------------------------------

    [Fact]
    public void T1_capacitor_placement_reaches_the_closed_form_optimum_step_by_step()
    {
        var opt = new CapPlaceOptions { Candidates = new[] { new CapacitorCandidate(2, CapSteps) } };
        GridModel m = CapCase();
        CapPlaceResult r = CapacitorPlacement.Run(m, opt);

        // Bound: the losses are the sum of the solved injections, exact for the solved state, so their error is the state
        // error times dLoss/dV. The state error is at most |J^-1| x tol with |J^-1| below 0.1 pu/pu here (|y| = 9.95 pu on a
        // single PQ bus), and dLoss/dV is below 100 MW/pu (2 x 0.37 MW / 0.96 pu, x 100 for margin), so the error is below
        // 100 x 0.1 x 1e-10 = 1e-9 MW. The spec's 1e-8 MW is used.
        const double tolMw = 1e-8;
        Assert.Equal(CapLossMw[0], r.BaseLossMw, tolMw);

        // Every greedy step: 10, 20, 30 Mvar, each with its closed-form losses, then 40 Mvar (0.2586 MW) does not improve.
        Assert.Equal(3, r.Steps.Count);
        for (int s = 0; s < 3; s++)
        {
            Assert.Equal(s + 1, r.Steps[s].Round);
            Assert.Equal(2, r.Steps[s].BusId);
            Assert.Equal(CapSteps[s + 1], r.Steps[s].SizeMvar);
            Assert.Equal(CapLossMw[s + 1], r.Steps[s].LossMw, tolMw);
        }

        CapPlaceBank bank = Assert.Single(r.Banks);
        Assert.Equal(3, bank.Step);
        Assert.Equal(30.0, bank.SizeMvar);
        Assert.Equal(CapLossMw[3], r.LossMw, tolMw);
        // V2 = 0.99329702554467216701 (mpmath); the state bound above gives 1e-11 pu, asserted at 1e-10.
        Assert.Equal(0.99329702554467216701, r.Voltages.Single(v => v.BusId == 2).VmPu, 1e-10);
        Assert.Equal(0.0, r.CapitalCost);
        Assert.Equal(r.LossMw * 8760.0, r.Objective);
        // Confirmation over the one bank: steps 2 and 4, neither better.
        Assert.Equal(2, r.ConfirmationSolves);
        Assert.Equal(0, r.ConfirmationImprovements);
        Assert.Equal(DistOptStatus.LocalOptimum, r.Status);
        Assert.True(r.Feasible);
        Assert.Equal(0.0, m.Buses[1].Bs);   // the case given is not changed
    }

    [Fact]
    public void T2_a_capital_cost_above_the_last_saving_stops_one_step_earlier()
    {
        // With the energy value 1 per MWh over 1 hour the objective is losses (MW) + cost x Mvar. The step 2 -> 3 saving is
        // 0.017236678227885418622 MW for 10 Mvar, so a cost of 0.0018 per Mvar (0.018 per step) is just above it, and the
        // step 1 -> 2 saving (0.038305 MW) still pays.
        var opt = new CapPlaceOptions
        {
            Candidates = new[] { new CapacitorCandidate(2, CapSteps) },
            CostPerMvar = 0.0018, EnergyCostPerMwh = 1, HoursPerYear = 1,
        };
        CapPlaceResult r = CapacitorPlacement.Run(CapCase(), opt);
        Assert.Equal(2, Assert.Single(r.Banks).Step);
        Assert.Equal(2, r.Steps.Count);
        Assert.Equal(CapLossMw[1] + 0.018, r.Steps[0].Objective, 1e-8);
        Assert.Equal(CapLossMw[2] + 0.036, r.Steps[1].Objective, 1e-8);
        Assert.Equal(20.0 * 0.0018, r.CapitalCost, 1e-15);
        Assert.Equal(CapLossMw[2], r.LossMw, 1e-8);
        Assert.Equal(DistOptStatus.LocalOptimum, r.Status);

        // A fixed cost per bank above the first saving (0.0581 MW) leaves the bus without a bank.
        CapPlaceResult none = CapacitorPlacement.Run(CapCase(), opt with { CostPerMvar = 0, FixedCostPerBank = 1.0 });
        Assert.Equal(0, Assert.Single(none.Banks).Step);
        Assert.Empty(none.Steps);
        Assert.Equal(0, none.ConfirmationSolves);   // no bank, nothing to confirm
    }

    // ---- T3, T4: volt/VAr optimisation -------------------------------------------------------------------------------

    // Demand (MW) of the constant-impedance 100 MW load at tap positions n = 0..8 of tau = 1 + n x 0.00625 behind X = 0.01:
    // V2 = (1 / tau) / |1 + j0.01|, demand = 100 V2^2 (mpmath, 30 digits).
    private static readonly double[] CvrDemandMw =
    {
        99.990000999900009999, 98.751746676341200416, 97.536352141344317024, 96.343258142852205803,
        95.171922427031538369, 94.021819122036373039, 92.892438147678917694, 91.783284649770169457,
        90.693878457959192743,
    };

    [Fact]
    public void T3_cvr_lowers_the_tap_voltage_to_the_band_edge_step_by_step()
    {
        // Spec fix: the spec's OLTC has no impedance, which the admittance model cannot hold (Branch R = X = 0), so the
        // transformer has X = 0.01 pu and the closed form carries |1 + j0.01|. The largest tau with V2 >= 0.95 is still
        // n = 8 (V2 = 0.95233), n = 9 gives 0.94670.
        GridModel m = TapCase(0, 0.01, 1.0, 1.0, 1.1, constantImpedance: true);
        var opt = new VvoOptions { Objective = VvoObjective.Demand, Common = new DistOptCommon { TolerancePu = 1e-12 } };
        VvoResult r = VoltVarOptimisation.Run(m, opt);

        // Bound: |J^-1| is about 0.01 pu/pu (|y| = 100 pu), so the state error is below 1e-14 pu at a 1e-12 pu tolerance;
        // V2 is asserted at the spec's 1e-12 pu, and the demand (100 V^2 MW, d/dV = 200 MW/pu) at 1e-9 MW.
        Assert.Equal(CvrDemandMw[0], r.BaseObjectiveValue, 1e-9);
        Assert.Equal(8, r.Moves.Count);
        for (int n = 1; n <= 8; n++)
        {
            VvoMove mv = r.Moves[n - 1];
            Assert.Equal("tap", mv.Kind);
            Assert.Equal("T1", mv.Name);
            Assert.Equal(n, mv.Position);
            Assert.Equal(n, mv.Sweep);
            Assert.Equal(CvrDemandMw[n], mv.Objective, 1e-9);
            Assert.Equal(0.0, mv.ViolationPu);
        }
        VvoControlState tap = Assert.Single(r.Controls);
        Assert.Equal(8, tap.Position);
        Assert.Equal(1.05, tap.Value);
        Assert.Equal(0.95233333690446431175, r.Voltages.Single(v => v.BusId == 2).VmPu, 1e-12);
        Assert.Equal(CvrDemandMw[8], r.DemandMw, 1e-9);
        Assert.True(r.Feasible);
        Assert.Equal(DistOptStatus.LocalOptimum, r.Status);
        Assert.Equal(2, r.ConfirmationSolves);   // n = 7 (worse) and n = 9 (below 0.95)
        Assert.Empty(r.Notes);
    }

    [Fact]
    public void T3b_an_infeasible_start_first_moves_into_the_band_then_minimises()
    {
        // Start at tau = 1.1 (n = 16 on the grid from 1.0): V2 = 0.90904 < 0.95. Each step down lowers the violation, until
        // n = 8 is inside the band; from there a lower tap raises the demand, so it stops at n = 8 again.
        GridModel m = TapCase(0, 0.01, 1.1, 1.0, 1.1, constantImpedance: true);
        VvoResult r = VoltVarOptimisation.Run(m, new VvoOptions { Objective = VvoObjective.Demand, Common = new DistOptCommon { TolerancePu = 1e-12 } });
        Assert.True(r.BaseViolationPu > 0.04);
        Assert.Equal(8, r.Moves.Count);
        double last = r.BaseViolationPu;
        for (int i = 0; i < 8; i++)
        {
            Assert.Equal(15 - i, r.Moves[i].Position);
            if (i < 7) Assert.True(r.Moves[i].ViolationPu < last, $"move {i + 1} did not lower the violation");
            last = r.Moves[i].ViolationPu;
        }
        Assert.Equal(0.0, r.Moves[7].ViolationPu);
        Assert.Equal(CvrDemandMw[8], r.Moves[7].Objective, 1e-9);
        Assert.Equal(8, Assert.Single(r.Controls).Position);
    }

    // Losses (MW) of the constant-power 100 MW load behind R + jX = 0.01 + j0.05 at tau = 0.9 + k x 0.00625 (mpmath).
    private static readonly (int K, double Vm, double LossMw)[] VvoLoss =
    {
        (16, 0.988604934865515, 1.02318567675069), (15, 0.994983360465192, 1.01010928728076),
        (14, 1.00144104354096, 0.997124130789145), (13, 1.00797950818073, 0.984229989067047),
        (12, 1.01460031746311, 0.971426646819439), (11, 1.02130507471585, 0.958713891627763),
        (10, 1.02809542482341, 0.946091513913177), (9, 1.03497305558569, 0.933559306900356),
        (8, 1.04193969913048, 0.921117066581845), (7, 1.04899713338192, 0.908764591682939),
    };

    [Fact]
    public void T4_vvo_losses_mode_takes_the_highest_voltage_inside_vmax()
    {
        // Constant-power load: the current is P / V, so the losses fall as V rises. Grid 0.9 + k x 0.00625; the start
        // tau = 1.0 is k = 16. k = 7 gives V2 = 1.04900 <= 1.05; k = 6 gives 1.05615, above Vmax.
        GridModel m = TapCase(0.01, 0.05, 1.0, 0.9, 1.1, constantImpedance: false);
        VvoResult r = VoltVarOptimisation.Run(m, new VvoOptions { Objective = VvoObjective.Losses });
        // The mpmath figures are quoted to 15 significant digits, so they carry up to 5e-15; the solver bound is 1e-9 MW as
        // in T1. Asserted at 1e-8 MW (loss) and 1e-9 pu (voltage).
        Assert.Equal(VvoLoss[0].LossMw, r.BaseLossMw, 1e-8);
        Assert.Equal(9, r.Moves.Count);
        for (int i = 0; i < 9; i++)
        {
            Assert.Equal(VvoLoss[i + 1].K, r.Moves[i].Position);
            Assert.Equal(VvoLoss[i + 1].LossMw, r.Moves[i].Objective, 1e-8);
        }
        Assert.Equal(7, Assert.Single(r.Controls).Position);
        Assert.Equal(0.94375, r.Controls[0].Value);
        Assert.Equal(VvoLoss[9].Vm, r.Voltages.Single(v => v.BusId == 2).VmPu, 1e-9);
        Assert.True(r.VmaxPu <= 1.05);
        Assert.True(r.Feasible);
        Assert.Equal(DistOptStatus.LocalOptimum, r.Status);
    }

    [Fact]
    public void T4b_a_switched_capacitor_is_a_second_control_after_the_taps()
    {
        // The two-bus capacitor case with two 10 Mvar switched banks at bus 2 and no OLTC: both on (20 Mvar) is the loss
        // optimum of the reachable states 0, 10 and 20 Mvar (closed-form losses as T1). The descent switches C1 on, then C2.
        var opt = new VvoOptions
        {
            Taps = Array.Empty<string>(),
            Capacitors = new[] { new VvoCapacitor("C1", 2, 10), new VvoCapacitor("C2", 2, 10) },
        };
        VvoResult r = VoltVarOptimisation.Run(CapCase(), opt);
        Assert.Equal(new[] { "C1", "C2" }, r.Moves.Select(x => x.Name));
        Assert.Equal(CapLossMw[1], r.Moves[0].Objective, 1e-8);
        Assert.Equal(CapLossMw[2], r.Moves[1].Objective, 1e-8);
        Assert.Equal(new[] { 1, 1 }, r.Controls.Select(c => c.Position));
        Assert.Equal(new[] { 10.0, 10.0 }, r.Controls.Select(c => c.Value));
        Assert.Equal(3, r.ConfirmationSolves);   // 3^2 - 1 combinations, 5 out of range (on + 1)
    }

    // ---- T5: phase balancing -----------------------------------------------------------------------------------------

    private static readonly double[] PhaseLoadsKw = { 10, 20, 30, 40, 50, 30 };

    /// <summary>A source fixing a, b, c at 230 V (0, -120, 120 deg) and n at 0 V at node s; one four-wire branch "head" s-m of
    /// 0.01 + j0.01 ohm per conductor (no mutuals); six single-phase constant-power loads on phase a at m.</summary>
    private static PhaseFrameModel PhaseFeeder()
    {
        string[] wires = ["a", "b", "c", "n"];
        var z = new Complex[4, 4];
        for (int i = 0; i < 4; i++) z[i, i] = new Complex(0.01, 0.01);
        Complex V(double deg) => GridSim.Core.Numerics.DetMath.FromPolar(230.0, deg * Math.PI / 180.0);
        var loads = PhaseLoadsKw.Select((kw, i) => FrameLoad.Wye($"L{i + 1}", "m", [new Complex(kw * 1000.0, 0)], 230.0, phases: ["a"])).ToArray();
        return new PhaseFrameModel("phase6",
            [new FrameNode("s", wires), new FrameNode("m", wires)],
            [new FrameSource("s", wires, [V(0), V(-120), V(120), Complex.Zero])],
            [new FrameBranch("head", "s", "m", wires, z)],
            loads);
    }

    [Fact]
    public void T5_phase_balancing_picks_the_smallest_balanced_assignment_and_the_solve_confirms_it()
    {
        PhaseFrameModel feeder = PhaseFeeder();
        PhaseBalanceResult r = PhaseBalancing.Run(feeder);
        Assert.Equal("exhaustive", r.Method);
        Assert.Equal(729, r.ProxyEvaluations);   // 3^6
        // Several assignments give 60/60/60 kW; the lexicographically smallest (phase indices a = 0, b = 1, c = 2) is
        // (0, 1, 2, 1, 0, 2) (enumerated in Python over all 729).
        Assert.Equal(new[] { "a", "b", "c", "b", "a", "c" }, r.Moves.Select(m => m.ToPhase));
        Assert.All(r.Moves, m => Assert.Equal("a", m.FromPhase));
        Assert.Equal(new[] { 180000.0, 0.0, 0.0 }, r.PhasePowerBeforeW);
        Assert.Equal(new[] { 60000.0, 60000.0, 60000.0 }, r.PhasePowerAfterW);
        Assert.Equal(120000.0, r.ProxyBeforeW);
        Assert.Equal(0.0, r.ProxyAfterW);
        Assert.Equal("head", r.HeadBranch);
        Assert.Equal("m", r.HeadNode);
        Assert.Equal(MeshedStatus.Converged, r.StatusBefore);
        Assert.Equal(MeshedStatus.Converged, r.StatusAfter);

        // Before: SciPy fsolve of the same circuit gives 847.3464118931627 A in the neutral and VUF 0.01759737494182289.
        // Bound: KCL at m holds to 4 x 1e-6 A (the solver tolerance); the node voltage error is at most 1e-6 A over the
        // branch admittance (70.7 S), 1.4e-8 V, which moves each load current (S / V, |dI/dV| below 4 A/V) by under 6e-8 A.
        // So the neutral current is within 1e-5 A; asserted at 1e-4 A. The VUF moves by under 1e-9; asserted at 1e-8.
        Assert.Equal(847.3464118931627, r.NeutralBeforeA, 1e-4);
        Assert.Equal(0.01759737494182289, r.VufBefore, 1e-8);
        // After (observed check, as the spec asks): the neutral current falls below 5% of before. With equal conductor
        // impedances and no mutuals the balanced circuit carries no neutral current (SciPy: 1.2e-13 A).
        Assert.True(r.NeutralAfterA < 0.05 * r.NeutralBeforeA, $"neutral after {r.NeutralAfterA} A, before {r.NeutralBeforeA} A");
        Assert.Equal("b", r.Model.Loads[1].Legs[0].From);
        Assert.Equal("a", feeder.Loads[1].Legs[0].From);   // the case given is not changed
    }

    [Fact]
    public void T5b_allowed_phases_and_the_greedy_route()
    {
        // L5 (50 kW) may stay on a only: the best is then 60/60/60 with L5 on a, and the smallest such vector is
        // (a, b, c, b, a, c) as before (L5 there is a already).
        var opt = new PhaseBalanceOptions
        {
            Movable = PhaseLoadsKw.Select((_, i) => new MovableLoad($"L{i + 1}", i == 4 ? new[] { "a" } : null)).ToArray(),
        };
        PhaseBalanceResult r = PhaseBalancing.Run(PhaseFeeder(), opt);
        Assert.Equal("a", r.Moves[4].ToPhase);
        Assert.Equal(0.0, r.ProxyAfterW);
        Assert.Equal(243, r.ProxyEvaluations);   // 3^5 x 1

        // Thirteen loads take the greedy route: largest first to the lightest phase. 13 x 10 kW: 5/4/4 loads.
        var many = Enumerable.Range(1, 13).Select(i => FrameLoad.Wye($"G{i}", "m", [new Complex(10000, 0)], 230.0, phases: ["a"])).ToArray();
        PhaseFrameModel f = PhaseFeeder();
        var big = new PhaseFrameModel("phase13", f.Nodes, f.Sources, f.Branches, many);
        PhaseBalanceResult g = PhaseBalancing.Run(big);
        Assert.Equal("greedy", g.Method);
        Assert.Equal(new[] { 50000.0, 40000.0, 40000.0 }, g.PhasePowerAfterW);
        Assert.Equal(new[] { "a", "b", "c", "a", "b", "c", "a", "b", "c", "a", "b", "c", "a" }, g.Moves.Select(m => m.ToPhase));

        // Refusals by name.
        Assert.Contains("not a single-phase load", Assert.Throws<ArgumentException>(() => PhaseBalancing.Run(
            new PhaseFrameModel("x", f.Nodes, f.Sources, f.Branches, [FrameLoad.Wye("T", "m", [Complex.One, Complex.One, Complex.One], 230.0)]),
            new PhaseBalanceOptions { Movable = [new MovableLoad("T")] })).Message, StringComparison.Ordinal);
        Assert.Contains("no load 'Q'", Assert.Throws<ArgumentException>(() => PhaseBalancing.Run(f, new PhaseBalanceOptions { Movable = [new MovableLoad("Q")] })).Message, StringComparison.Ordinal);
        Assert.Contains("no branch 'feeder'", Assert.Throws<ArgumentException>(() => PhaseBalancing.Run(f, new PhaseBalanceOptions { HeadBranch = "feeder" })).Message, StringComparison.Ordinal);
    }

    // ---- T6: determinism and the confirmation cap --------------------------------------------------------------------

    /// <summary>A four-bus radial feeder (1 source, 2-3-4 in a chain) with loads on 2, 3 and 4.</summary>
    private static GridModel Feeder4() => new()
    {
        Name = "feeder4", BaseMva = 10,
        Buses = new[]
        {
            new Bus(1, BusType.Slack), new Bus(2, BusType.Pq, Pd: 1.0, Qd: 0.6), new Bus(3, BusType.Pq, Pd: 1.5, Qd: 0.9),
            new Bus(4, BusType.Pq, Pd: 0.8, Qd: 0.5),
        },
        Generators = new[] { new Generator(1, 0) },
        Branches = new[] { new Branch(1, 2, 0.02, 0.04), new Branch(2, 3, 0.03, 0.05), new Branch(3, 4, 0.03, 0.05) },
    };

    private static void WithCulture(string name, Action body)
    {
        CultureInfo old = CultureInfo.CurrentCulture, oldUi = CultureInfo.CurrentUICulture;
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo(name);
            CultureInfo.CurrentUICulture = CultureInfo.GetCultureInfo(name);
            body();
        }
        finally { CultureInfo.CurrentCulture = old; CultureInfo.CurrentUICulture = oldUi; }
    }

    [Fact]
    public void T6_two_runs_give_the_same_bits_and_the_confirmation_cap_is_refused()
    {
        var cap = new CapPlaceOptions { DefaultStepsMvar = new[] { 0.0, 0.3, 0.6, 0.9 } };
        var vvo = new VvoOptions { Taps = Array.Empty<string>(), Capacitors = new[] { new VvoCapacitor("C3", 3, 0.6), new VvoCapacitor("C4", 4, 0.3, InitiallyOn: true) } };
        string c1 = OptimisationReport.Csv(DistOptReport.Rows(CapacitorPlacement.Run(Feeder4(), cap)));
        string v1 = OptimisationReport.Json("dist-opt", DistOptReport.Rows(VoltVarOptimisation.Run(Feeder4(), vvo)));
        string p1 = OptimisationReport.Csv(DistOptReport.Rows(PhaseBalancing.Run(PhaseFeeder())));
        string c2 = "", v2 = "", p2 = "";
        WithCulture("de-DE", () =>
        {
            c2 = OptimisationReport.Csv(DistOptReport.Rows(CapacitorPlacement.Run(Feeder4(), cap)));
            v2 = OptimisationReport.Json("dist-opt", DistOptReport.Rows(VoltVarOptimisation.Run(Feeder4(), vvo)));
            p2 = OptimisationReport.Csv(DistOptReport.Rows(PhaseBalancing.Run(PhaseFeeder())));
        });
        Assert.Equal(c1, c2);
        Assert.Equal(v1, v2);
        Assert.Equal(p1, p2);
        CapPlaceResult a = CapacitorPlacement.Run(Feeder4(), cap), b = CapacitorPlacement.Run(Feeder4(), cap);
        Assert.Equal(Bits(a.Objective), Bits(b.Objective));
        Assert.Equal(a.Voltages.Select(x => Bits(x.VmPu)), b.Voltages.Select(x => Bits(x.VmPu)));
        Assert.Contains(a.Banks, x => x.Step > 0);   // the run placed something, so the comparison covers a search

        // Nine controls with the confirmation on: refused by name before any solve. Off: it runs.
        var nine = new VvoOptions
        {
            Taps = Array.Empty<string>(),
            Capacitors = Enumerable.Range(1, 9).Select(i => new VvoCapacitor($"C{i}", 2 + i % 3, 0.1)).ToArray(),
        };
        ArgumentException ex = Assert.Throws<ArgumentException>(() => VoltVarOptimisation.Run(Feeder4(), nine));
        Assert.Contains("over 9 controls is above the cap of 8", ex.Message, StringComparison.Ordinal);
        VvoResult off = VoltVarOptimisation.Run(Feeder4(), nine with { Common = new DistOptCommon { Confirm = false } });
        Assert.Equal(0, off.ConfirmationSolves);
        // Eight is the cap and runs.
        VvoResult eight = VoltVarOptimisation.Run(Feeder4(), nine with { Capacitors = nine.Capacitors.Take(8).ToArray() });
        Assert.True(eight.ConfirmationSolves > 0);
    }

    [Fact]
    public void T6b_refusals_are_named()
    {
        GridModel m = CapCase();
        Assert.Contains("not a bus of the case", Assert.Throws<ArgumentException>(() => CapacitorPlacement.Run(m, new CapPlaceOptions { Candidates = new[] { new CapacitorCandidate(9, CapSteps) } })).Message, StringComparison.Ordinal);
        Assert.Contains("first bank size must be 0", Assert.Throws<ArgumentException>(() => CapacitorPlacement.Run(m, new CapPlaceOptions { Candidates = new[] { new CapacitorCandidate(2, new[] { 10.0, 20.0 }) } })).Message, StringComparison.Ordinal);
        Assert.Contains("strictly ascending", Assert.Throws<ArgumentException>(() => CapacitorPlacement.Run(m, new CapPlaceOptions { Candidates = new[] { new CapacitorCandidate(2, new[] { 0.0, 20.0, 10.0 }) } })).Message, StringComparison.Ordinal);
        Assert.Contains("give the candidate buses", Assert.Throws<ArgumentException>(() => CapacitorPlacement.Run(m)).Message, StringComparison.Ordinal);
        Assert.Contains("no OLTC on transformer 'X'", Assert.Throws<ArgumentException>(() => VoltVarOptimisation.Run(m, new VvoOptions { Taps = new[] { "X" } })).Message, StringComparison.Ordinal);
        Assert.Contains("there is no control", Assert.Throws<ArgumentException>(() => VoltVarOptimisation.Run(m)).Message, StringComparison.Ordinal);
        Assert.Throws<ArgumentException>(() => VoltVarOptimisation.Run(m, new VvoOptions { Capacitors = new[] { new VvoCapacitor("C", 2, 10) }, Common = new DistOptCommon { VminPu = 1.1, VmaxPu = 1.0 } }));
    }

    [Fact]
    public void T6c_a_starting_tap_off_the_grid_is_moved_onto_it_and_noted()
    {
        GridModel m = TapCase(0.01, 0.05, 1.003, 0.9, 1.1, constantImpedance: false);
        VvoResult r = VoltVarOptimisation.Run(m, new VvoOptions());
        Assert.Contains("is not on the step grid", Assert.Single(r.Notes), StringComparison.Ordinal);
        Assert.Equal(16, r.Controls[0].StartPosition);   // round(0.103 / 0.00625) = 16
        Assert.Equal(7, r.Controls[0].Position);
    }

    // ---- T7: bit-identity --------------------------------------------------------------------------------------------

    [Fact]
    public void T7_reconfig_and_the_input_case_are_untouched_by_a_dist_opt_run()
    {
        // The reconfig code is not changed by WP33; its pins are Rc5Wp7 T12-T14. This checks that a dist-opt run in the same
        // process leaves no state behind: reconfig on case33bw gives the same bits before and after a capplace run on the
        // same model object, and the model's shunts are unchanged.
        GridModel m = GridSim.Core.IO.Matpower.MatpowerParser.ParseWithOpenBranches(S10TestSupport.MatpowerText("dist/case33bw.m"), "case33bw").Model;
        double[] bs0 = m.Buses.Select(b => b.Bs).ToArray();
        ReconfigResult r1 = FeederReconfiguration.Run(m);
        GridModel tree = m.CopyWith(branches: m.Branches.Where(b => b.InService).ToArray());
        CapPlaceResult cp = CapacitorPlacement.Run(tree, new CapPlaceOptions
        {
            Candidates = new[] { new CapacitorCandidate(18, new[] { 0.0, 0.3, 0.6 }), new CapacitorCandidate(33, new[] { 0.0, 0.3, 0.6 }) },
            Common = new DistOptCommon { VminPu = 0.9 },
        });
        Assert.True(cp.PowerFlows > 1);
        ReconfigResult r2 = FeederReconfiguration.Run(m);
        Assert.Equal(Bits(r1.BaseLossMw), Bits(r2.BaseLossMw));
        Assert.Equal(Bits(r1.LossMw), Bits(r2.LossMw));
        Assert.Equal(r1.OpenBranches, r2.OpenBranches);
        Assert.Equal(r1.Swaps.Select(s => (s.Closed, s.Opened, Bits(s.LossMw))), r2.Swaps.Select(s => (s.Closed, s.Opened, Bits(s.LossMw))));
        Assert.Equal(bs0.Select(Bits), m.Buses.Select(b => Bits(b.Bs)));
        Assert.Equal(bs0.Select(Bits), tree.Buses.Select(b => Bits(b.Bs)));
        // The published optimum, as Rc5Wp7 T13 pins it.
        Assert.Equal(new[] { 6, 8, 13, 31, 36 }, r2.OpenBranches);
    }

    // ---- T8: the verb ------------------------------------------------------------------------------------------------

    private const string PhaseCaseJson = """
        {
          "schema": "gridsim-dist3ph-mesh/1",
          "name": "phase6",
          "nodes": [ { "name": "s", "wires": ["a", "b", "c", "n"] }, { "name": "m", "wires": ["a", "b", "c", "n"] } ],
          "sources": [ { "node": "s", "wires": ["a", "b", "c", "n"], "magnitude_v": [230, 230, 230, 0], "angle_deg": [0, -120, 120, 0] } ],
          "branches": [ { "name": "head", "from": "s", "to": "m", "wires": ["a", "b", "c", "n"],
            "r_ohm": [[0.01, 0, 0, 0], [0, 0.01, 0, 0], [0, 0, 0.01, 0], [0, 0, 0, 0.01]],
            "x_ohm": [[0.01, 0, 0, 0], [0, 0.01, 0, 0], [0, 0, 0.01, 0], [0, 0, 0, 0.01]] } ],
          "loads": [
            { "name": "L1", "node": "m", "connection": "wye", "phases": ["a"], "p_w": [10000], "q_var": [0], "nominal_v": 230 },
            { "name": "L2", "node": "m", "connection": "wye", "phases": ["a"], "p_w": [20000], "q_var": [0], "nominal_v": 230 },
            { "name": "L3", "node": "m", "connection": "wye", "phases": ["a"], "p_w": [30000], "q_var": [0], "nominal_v": 230 },
            { "name": "L4", "node": "m", "connection": "wye", "phases": ["a"], "p_w": [40000], "q_var": [0], "nominal_v": 230 },
            { "name": "L5", "node": "m", "connection": "wye", "phases": ["a"], "p_w": [50000], "q_var": [0], "nominal_v": 230 },
            { "name": "L6", "node": "m", "connection": "wye", "phases": ["a"], "p_w": [30000], "q_var": [0], "nominal_v": 230 }
          ]
        }
        """;

    private const string CapParamsJson = """
        {
          "format": "gridsim-distopt/1",
          "common": { "vminPu": 0.9 },
          "capplace": { "candidates": [ { "bus": 18, "stepsMvar": [0, 0.3, 0.6] }, { "bus": 33, "stepsMvar": [0, 0.3, 0.6] } ],
                        "energyCostPerMwh": 50, "costPerMvar": 100 }
        }
        """;

    [Fact]
    public void T8_the_verb_writes_the_core_result_and_refuses_by_name()
    {
        string dir = JournalTestSupport.NewDir("wp33");
        try
        {
            string case33 = Path.Combine(S10TestSupport.DataDir, "matpower", "dist", "case33bw.m");
            File.WriteAllText(Path.Combine(dir, "cap.json"), CapParamsJson);
            CliProcess.Result r = JournalTestSupport.Run(dir, "dist-opt", case33, "--mode", "capplace", "--params", "cap.json", "--out", "cap.csv");
            Assert.True(r.ExitCode == 0, r.ToString());
            Assert.StartsWith("dist-opt capplace - ", r.StdOut, StringComparison.Ordinal);
            // The verb's table is the Core result's, for the case loaded the way the CLI loads it (default reader).
            GridModel m = S10TestSupport.LoadMatpower("dist/case33bw.m").Model;
            CapPlaceResult core = CapacitorPlacement.Run(m, new CapPlaceOptions
            {
                Candidates = new[] { new CapacitorCandidate(18, new[] { 0.0, 0.3, 0.6 }), new CapacitorCandidate(33, new[] { 0.0, 0.3, 0.6 }) },
                EnergyCostPerMwh = 50, CostPerMvar = 100, Common = new DistOptCommon { VminPu = 0.9 },
            });
            Assert.Equal(OptimisationReport.Csv(DistOptReport.Rows(core)), File.ReadAllText(Path.Combine(dir, "cap.csv")));

            File.WriteAllText(Path.Combine(dir, "phase.json"), PhaseCaseJson);
            CliProcess.Result p = JournalTestSupport.Run(dir, "dist-opt", "phase.json", "--mode", "phase-balance", "--out", "phase.json.csv");
            Assert.True(p.ExitCode == 0, p.ToString());
            Assert.Contains("load L2 (20000.0 W): a to b", p.StdOut, StringComparison.Ordinal);
            Assert.Equal(OptimisationReport.Csv(DistOptReport.Rows(PhaseBalancing.Run(PhaseFeeder()))), File.ReadAllText(Path.Combine(dir, "phase.json.csv")));

            // Usage (exit 2): no mode, an unknown mode, another verb's flag.
            Assert.Equal(2, JournalTestSupport.Run(dir, "dist-opt", case33).ExitCode);
            Assert.Equal(2, JournalTestSupport.Run(dir, "dist-opt", case33, "--mode", "global").ExitCode);
            Assert.Equal(2, JournalTestSupport.Run(dir, "dist-opt", case33, "--mode", "vvo", "--top", "3").ExitCode);
            // Refused input (exit 1), by name: a params section for another mode, an unknown member.
            File.WriteAllText(Path.Combine(dir, "other.json"), "{ \"format\": \"gridsim-distopt/1\", \"vvo\": { } }");
            CliProcess.Result o = JournalTestSupport.Run(dir, "dist-opt", case33, "--mode", "capplace", "--params", "other.json");
            Assert.True(o.ExitCode == 1, o.ToString());
            Assert.Contains("the file has a \"vvo\" section, but the mode is capplace", o.StdErr, StringComparison.Ordinal);
            File.WriteAllText(Path.Combine(dir, "unknown.json"), "{ \"format\": \"gridsim-distopt/1\", \"vvo\": { \"budget\": 3 } }");
            CliProcess.Result u = JournalTestSupport.Run(dir, "dist-opt", case33, "--mode", "vvo", "--params", "unknown.json");
            Assert.True(u.ExitCode == 1, u.ToString());
            Assert.Contains("unknown member \"budget\"", u.StdErr, StringComparison.Ordinal);
            // vvo on a case with no OLTC and no capacitor: refused by name.
            CliProcess.Result n = JournalTestSupport.Run(dir, "dist-opt", case33, "--mode", "vvo");
            Assert.True(n.ExitCode == 1, n.ToString());
            Assert.Contains("there is no control", n.StdErr, StringComparison.Ordinal);
            Assert.Contains("dist-opt", CliProcess.RunBuilt("dist-opt", "--help").StdOut, StringComparison.Ordinal);
        }
        finally { JournalTestSupport.Delete(dir); }
    }

    [Fact]
    public void T9_the_journal_hashes_the_params_file_and_validate_re_runs_the_job_identical()
    {
        Assert.Null(ReplayPolicy.Refusal("dist-opt", ["dist-opt", "phase.json", "--mode", "phase-balance", "--params", "p.json", "--out", "o.csv"], new HashSet<string>(StringComparer.Ordinal)));
        string dir = JournalTestSupport.NewDir("wp33j");
        try
        {
            File.WriteAllText(Path.Combine(dir, "phase.json"), PhaseCaseJson);
            File.WriteAllText(Path.Combine(dir, "p.json"), "{ \"format\": \"gridsim-distopt/1\", \"phaseBalance\": { \"headBranch\": \"head\" } }");
            string journal = Path.Combine(dir, "j", "journal.jsonl");
            CliProcess.Result run = JournalTestSupport.Run(dir, "--journal", journal, "dist-opt", "phase.json", "--mode", "phase-balance", "--params", "p.json", "--out", "o.csv");
            Assert.True(run.ExitCode == 0, run.ToString());
            JournalEntry e = JournalJson.Parse(File.ReadAllLines(journal)[0], out _)!;
            string caseSha = Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(Path.Combine(dir, "phase.json"))));
            string paramsSha = Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(Path.Combine(dir, "p.json"))));
            Assert.Contains(e.Tokens, t => t.Kind == "input" && t.Raw == "phase.json" && t.Sha256 == caseSha);
            Assert.Contains(e.Tokens, t => t.Kind == "input" && t.Raw == "p.json" && t.Sha256 == paramsSha);

            JournalTestSupport.FromAnotherBuild(journal);
            string report = Path.Combine(dir, "v.json");
            CliProcess.Result v = JournalTestSupport.Run(dir, "breaking-change", "validate", "--journal", journal, "--json", report);
            Assert.True(v.ExitCode == 0, v.ToString());
            using JsonDocument d = JsonDocument.Parse(File.ReadAllBytes(report));
            JsonElement entry = Assert.Single(d.RootElement.GetProperty("tables").GetProperty("entries").EnumerateArray());
            Assert.Equal(ReplayStatus.Identical, entry.GetProperty("status").GetString());
        }
        finally { JournalTestSupport.Delete(dir); }
    }
}
