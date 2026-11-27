using System.Numerics;
using GridSim.Core.Analysis.DistributionOptimisation;
using GridSim.Core.Analysis.Opf;
using GridSim.Core.Distribution.Meshed;
using GridSim.Core.Model;
using Xunit;

namespace GridSim.Tests;

/// <summary>
/// RC5 WP33 review fixes (review R3): the bounded capacitor-placement confirmation, the refusal of devices the trial power
/// flows do not regulate, the OLTC impedance correction table in VVO, the phase-balancing cap on assignments and the
/// generating-load rule, the CVR demand at a derived tolerance, and the B31 guard on the dist-opt CSV.
///
/// Sources of the expected values, all computed outside the repo in Python: an independent polar Newton power flow in NumPy
/// (MATPOWER branch model, tap at the from end; it reproduces the WP33 T1 closed-form losses to 2e-15 MW) with a replica of
/// the greedy search and the coordinate descent; exhaustive enumeration and a replica of the greedy rule for phase
/// balancing; mpmath (30 digits) for the CVR closed form V2 = (1/tau)/|1 + j0.01|. Each figure is quoted from those runs.
/// </summary>
public sealed class Rc5Wp33ReviewFixTests
{
    // ---- cases -------------------------------------------------------------------------------------------------------

    /// <summary>A radial feeder on 10 MVA: source bus 1, then buses 2..11 in a chain, each 0.3 MW + 0.2 Mvar, every section
    /// R + jX = 0.01 + j0.02 pu.</summary>
    private static GridModel Feeder11() => new()
    {
        Name = "feeder11", BaseMva = 10,
        Buses = new[] { new Bus(1, BusType.Slack) }.Concat(Enumerable.Range(2, 10).Select(i => new Bus(i, BusType.Pq, Pd: 0.3, Qd: 0.2))).ToArray(),
        Generators = new[] { new Generator(1, 0) },
        Branches = Enumerable.Range(1, 10).Select(i => new Branch(i, i + 1, 0.01, 0.02)).ToArray(),
    };

    /// <summary>Two buses on 100 MVA: source V = 1.0, line R = 0.01, X = 0.1 pu, load 50 MW + 30 Mvar at bus 2.</summary>
    private static GridModel CapCase() => new()
    {
        Name = "cap2", BaseMva = 100,
        Buses = new[] { new Bus(1, BusType.Slack), new Bus(2, BusType.Pq, Pd: 50, Qd: 30) },
        Generators = new[] { new Generator(1, 0) },
        Branches = new[] { new Branch(1, 2, 0.01, 0.1) },
    };

    /// <summary>Bus 1 feeds bus 2 through OLTC "T1" (ratio at the bus-1 end) of R + jX, load 100 MW at bus 2, steps of 0.00625.</summary>
    private static GridModel TapCase(double r, double x, double tap0, double minTap, double maxTap, bool constantImpedance, TapImpedanceCorrection? table = null) => new()
    {
        Name = "tap2", BaseMva = 100,
        Buses = new[]
        {
            new Bus(1, BusType.Slack),
            new Bus(2, BusType.Pq, Pd: 100, Qd: 0, Zip: constantImpedance ? new ZipLoad(Ap: 1, Bp: 0, Cp: 0, Aq: 1, Bq: 0, Cq: 0) : null),
        },
        Generators = new[] { new Generator(1, 0) },
        Branches = new[] { new Branch(1, 2, r, x, TapRatio: tap0, Name: "T1", IsTransformer: true) },
        Oltcs = new[] { new Oltc("T1", 2, TargetVpu: 1.0, MinTap: minTap, MaxTap: maxTap, StepPu: 0.00625) { ImpedanceCorrection = table } },
    };

    private static readonly string[] Wires = ["a", "b", "c", "n"];

    /// <summary>The WP33 phase feeder (source s at 230 V, branch "head" s-m of 0.01 + j0.01 ohm per conductor) with the
    /// given loads at m.</summary>
    private static PhaseFrameModel PhaseFeeder(IReadOnlyList<FrameLoad> loads)
    {
        var z = new Complex[4, 4];
        for (int i = 0; i < 4; i++) z[i, i] = new Complex(0.01, 0.01);
        Complex V(double deg) => GridSim.Core.Numerics.DetMath.FromPolar(230.0, deg * Math.PI / 180.0);
        return new PhaseFrameModel("phase",
            [new FrameNode("s", Wires), new FrameNode("m", Wires)],
            [new FrameSource("s", Wires, [V(0), V(-120), V(120), Complex.Zero])],
            [new FrameBranch("head", "s", "m", Wires, z)],
            loads);
    }

    private static FrameLoad OnA(string name, double w) => FrameLoad.Wye(name, "m", [new Complex(w, 0)], 230.0, phases: ["a"]);

    // ---- item 1: capplace above eight banks --------------------------------------------------------------------------

    // Greedy steps on Feeder11 with every PQ bus a candidate of 0 or 0.1 Mvar and no cost (Python replica): the bank goes
    // to the far end first. Losses (MW) after each step. The best trial beats the second best by 3.1e-5 MW or more in every
    // round, far above the objective tie (2 x 11 x 1e-10 x 10 = 2.2e-8 MW).
    private static readonly (int Bus, double LossMw)[] Feeder11Steps =
    {
        (11, 0.051212879399221596), (10, 0.04918711106481944), (9, 0.047365120108881636), (8, 0.045751848888670246),
        (7, 0.044352105218261606), (6, 0.043170757909561444), (5, 0.042212935430405926), (4, 0.04148422908935245),
        (3, 0.04099090213179498), (2, 0.040740106143294756),
    };

    [Fact]
    public void F1_capplace_with_more_than_eight_banks_runs_and_confirms_the_eight_placed_last()
    {
        var opt = new CapPlaceOptions { DefaultStepsMvar = new[] { 0.0, 0.1 } };
        CapPlaceResult r = CapacitorPlacement.Run(Feeder11(), opt);

        // Bound on each loss figure: the solver noise 2 x N x tol x base = 2.2e-8 MW (DistOpt.SolverNoiseMw); asserted at
        // 1e-7 MW.
        const double tolMw = 1e-7;
        Assert.Equal(0.053437154370027766, r.BaseLossMw, tolMw);
        Assert.Equal(10, r.Steps.Count);
        for (int s = 0; s < 10; s++)
        {
            Assert.Equal(s + 1, r.Steps[s].Round);
            Assert.Equal(Feeder11Steps[s].Bus, r.Steps[s].BusId);
            Assert.Equal(0.1, r.Steps[s].SizeMvar);
            Assert.Equal(Feeder11Steps[s].LossMw, r.Steps[s].LossMw, tolMw);
        }
        Assert.All(r.Banks, b => Assert.Equal(1, b.Step));

        // Ten banks: the confirmation covers the eight placed last (buses 2 to 9). Each sits at its top size, so only the
        // step down is in range: 2^8 - 1 = 255 power flows, none better (Python: every subset removal raises the losses).
        Assert.Equal(new[] { 10, 11 }, r.UnconfirmedBanks);
        Assert.Equal(255, r.ConfirmationSolves);
        Assert.Equal(0, r.ConfirmationImprovements);
        Assert.Equal(1, r.Rounds);
        Assert.Equal(1 + 55 + 255, r.PowerFlows);   // the start, 10 + 9 + ... + 1 greedy trials, the confirmation
        Assert.Equal(DistOptStatus.LocalOptimum, r.Status);
        Assert.Contains("not the banks at bus(es) 10, 11", r.Message, StringComparison.Ordinal);
        Assert.Equal(Feeder11Steps[9].LossMw, r.LossMw, tolMw);
        Assert.True(r.Feasible);

        string csv = OptimisationReport.Csv(DistOptReport.Rows(r));
        Assert.Contains("summary,,unconfirmed_banks,2\n", csv, StringComparison.Ordinal);
        Assert.Contains("unconfirmed,10,bus,10\n", csv, StringComparison.Ordinal);
        Assert.Contains("unconfirmed,11,bus,11\n", csv, StringComparison.Ordinal);
    }

    [Fact]
    public void F1b_eight_banks_are_all_confirmed_and_none_is_reported_unconfirmed()
    {
        // Candidates 4 to 11 only: eight banks, the cap itself (Python: the same greedy order 11..4, 255 confirmation
        // trials, none better).
        var opt = new CapPlaceOptions { Candidates = Enumerable.Range(4, 8).Select(b => new CapacitorCandidate(b, new[] { 0.0, 0.1 })).ToArray() };
        CapPlaceResult r = CapacitorPlacement.Run(Feeder11(), opt);
        Assert.Equal(8, r.Banks.Count(b => b.Step == 1));
        Assert.Empty(r.UnconfirmedBanks);
        Assert.Equal(255, r.ConfirmationSolves);
        Assert.DoesNotContain("not the banks", r.Message, StringComparison.Ordinal);
        Assert.Equal(Feeder11Steps[7].LossMw, r.LossMw, 1e-7);
    }

    // ---- item 4: devices the trial power flows do not regulate -------------------------------------------------------

    [Fact]
    public void F4_cases_with_unregulated_oltcs_or_flow_controls_are_refused_by_name()
    {
        GridModel tap = TapCase(0.01, 0.05, 1.0, 0.9, 1.1, constantImpedance: false);
        ArgumentException cap = Assert.Throws<ArgumentException>(() => CapacitorPlacement.Run(tap, new CapPlaceOptions { Candidates = new[] { new CapacitorCandidate(2, new[] { 0.0, 10.0 }) } }));
        Assert.Contains("OLTC 'T1'", cap.Message, StringComparison.Ordinal);
        Assert.Contains("plain Newton power flow", cap.Message, StringComparison.Ordinal);

        // VVO with the OLTC left out of the controls.
        ArgumentException vvo = Assert.Throws<ArgumentException>(() => VoltVarOptimisation.Run(tap, new VvoOptions
        {
            Taps = Array.Empty<string>(),
            Capacitors = new[] { new VvoCapacitor("C1", 2, 10) },
        }));
        Assert.Contains("OLTC 'T1'", vvo.Message, StringComparison.Ordinal);

        // A transformer flow control, in either mode.
        GridModel flow = CapCase();
        flow = new GridModel
        {
            Name = flow.Name, BaseMva = flow.BaseMva, Buses = flow.Buses, Generators = flow.Generators, Branches = flow.Branches,
            TransformerFlowControls = new[] { new TransformerFlowControl("X1", 1, TransformerFlowKind.ReactivePower, -10, 10, 0.9, 1.1, 0.0125) },
        };
        Assert.Contains("transformer flow control 'X1'", Assert.Throws<ArgumentException>(() => CapacitorPlacement.Run(flow, new CapPlaceOptions
        {
            Candidates = new[] { new CapacitorCandidate(2, new[] { 0.0, 10.0 }) },
        })).Message, StringComparison.Ordinal);
        Assert.Contains("transformer flow control 'X1'", Assert.Throws<ArgumentException>(() => VoltVarOptimisation.Run(flow, new VvoOptions
        {
            Capacitors = new[] { new VvoCapacitor("C1", 2, 10) },
        })).Message, StringComparison.Ordinal);

        // Every OLTC among the controls: it runs.
        Assert.Equal(DistOptStatus.LocalOptimum, VoltVarOptimisation.Run(tap, new VvoOptions()).Status);
    }

    // ---- item 3: the OLTC impedance correction table in VVO ----------------------------------------------------------

    // The table: factor 1.5 at ratio 0.9 falling linearly to 0.5 at 1.1 (purely real), on R + jX = 0.01 + j0.05; the case's
    // own ratio 1.0 keeps R + jX as given. Constant-power 100 MW load, losses objective, band 0.95 to 1.05. With the table the
    // impedance falls as the ratio rises, so the losses fall upwards: the descent climbs from k = 16 to k = 22 (V2 = 0.95440;
    // k = 23 gives 0.94897, below the band). Without it the same case descends to k = 7 (WP33 T4). Losses (MW) and V2 from the
    // Python replica with R, X = (0.01 F, 0.05 F) at each trial ratio.
    private static readonly (int K, double LossMw)[] TableMoves =
    {
        (17, 1.0031432149985031), (18, 0.982378477111645), (19, 0.9608861429914239), (20, 0.9386609803569534),
        (21, 0.9156978438920937), (22, 0.8919916742091916),
    };

    [Fact]
    public void F3_vvo_takes_r_and_x_from_the_oltc_impedance_correction_table()
    {
        var table = new TapImpedanceCorrection(0.01, 0.05, 1.0, new[] { new ImpedanceCorrectionPoint(0.9, 1.5, 0.0), new ImpedanceCorrectionPoint(1.1, 0.5, 0.0) });
        VvoResult r = VoltVarOptimisation.Run(TapCase(0.01, 0.05, 1.0, 0.9, 1.1, constantImpedance: false, table), new VvoOptions());

        // Bound as WP33 T4: the solver error on the losses is below 1e-9 MW; asserted at 1e-8 MW, and V2 at 1e-9 pu.
        Assert.Equal(1.023185676750693, r.BaseLossMw, 1e-8);   // at the case's ratio: the case's R and X
        Assert.Equal(TableMoves.Length, r.Moves.Count);
        for (int i = 0; i < TableMoves.Length; i++)
        {
            Assert.Equal(TableMoves[i].K, r.Moves[i].Position);
            Assert.Equal(TableMoves[i].LossMw, r.Moves[i].Objective, 1e-8);
            Assert.Equal(0.0, r.Moves[i].ViolationPu);
        }
        Assert.Equal(22, Assert.Single(r.Controls).Position);
        Assert.Equal(0.9544018763095398, r.Voltages.Single(v => v.BusId == 2).VmPu, 1e-9);
        Assert.Equal(2, r.ConfirmationSolves);   // k = 21 (worse) and k = 23 (below the band)
        Assert.Contains("impedance correction table", Assert.Single(r.Notes), StringComparison.Ordinal);
        Assert.True(r.Feasible);
    }

    // ---- item 5: the phase-balancing cap counts assignments; generating loads ---------------------------------------

    [Fact]
    public void F5_the_exhaustive_search_is_capped_on_assignments_not_loads()
    {
        // Thirteen 10 kW loads with L1 held to phase a: 1 x 3^12 = 531 441 assignments, so exhaustive (the old cap of
        // twelve loads sent this to the greedy route). Enumerated in Python: the smallest best vector is a x5, b x4, c x4.
        var thirteen = Enumerable.Range(1, 13).Select(i => OnA($"L{i}", 10000)).ToArray();
        var opt = new PhaseBalanceOptions { Movable = thirteen.Select((l, i) => new MovableLoad(l.Name, i == 0 ? new[] { "a" } : null)).ToArray() };
        PhaseBalanceResult r = PhaseBalancing.Run(PhaseFeeder(thirteen), opt);
        Assert.Equal("exhaustive", r.Method);
        Assert.Equal(PhaseBalancing.MaxExhaustiveAssignments, r.ProxyEvaluations);
        Assert.Equal("aaaaabbbbcccc", string.Concat(r.Moves.Select(m => m.ToPhase)));
        Assert.Equal(new[] { 50000.0, 40000.0, 40000.0 }, r.PhasePowerAfterW);

        // Two balancing phases: 19 loads give 2^19 = 524 288 assignments (exhaustive), 20 give 2^20 (greedy). Python: the
        // smallest best vector puts the first ten on a and the last nine on b (100 / 90 kW).
        var two = new PhaseBalanceOptions { Phases = ["a", "b"] };
        var nineteen = Enumerable.Range(1, 19).Select(i => OnA($"L{i}", 10000)).ToArray();
        PhaseBalanceResult e = PhaseBalancing.Run(PhaseFeeder(nineteen), two);
        Assert.Equal("exhaustive", e.Method);
        Assert.Equal(524288, e.ProxyEvaluations);
        Assert.Equal(new string('a', 10) + new string('b', 9), string.Concat(e.Moves.Select(m => m.ToPhase)));
        Assert.Equal(new[] { 100000.0, 90000.0 }, e.PhasePowerAfterW);
        PhaseBalanceResult g = PhaseBalancing.Run(PhaseFeeder(Enumerable.Range(1, 20).Select(i => OnA($"L{i}", 10000)).ToArray()), two);
        Assert.Equal("greedy", g.Method);
        Assert.Equal(20, g.ProxyEvaluations);
    }

    [Fact]
    public void F5b_the_greedy_route_puts_a_generating_load_on_the_heaviest_phase()
    {
        // A fixed 50 kW load F on b; thirteen movable loads of -10 kW (generating) on a, so 3^13 assignments: greedy. Each
        // goes to the phase of most power so far (ties to the first): b five times (50 to 0 kW), then a, b, c in turn. Python
        // replica: b b b b b a b c a b c a b, phase powers -30 / -30 / -20 kW. The old rule (least power) put all thirteen on
        // a: -130 / 50 / 0 kW.
        var loads = new List<FrameLoad> { FrameLoad.Wye("F", "m", [new Complex(50000, 0)], 230.0, phases: ["b"]) };
        loads.AddRange(Enumerable.Range(1, 13).Select(i => OnA($"N{i}", -10000)));
        var opt = new PhaseBalanceOptions { Movable = Enumerable.Range(1, 13).Select(i => new MovableLoad($"N{i}")).ToArray() };
        PhaseBalanceResult r = PhaseBalancing.Run(PhaseFeeder(loads), opt);
        Assert.Equal("greedy", r.Method);
        Assert.Equal("bbbbbabcabcab", string.Concat(r.Moves.Select(m => m.ToPhase)));
        // Whole watts below 2^53 add exactly.
        Assert.Equal(new[] { -30000.0, -30000.0, -20000.0 }, r.PhasePowerAfterW);
        // The mean -80000 / 3 carries one rounding (under 4e-12 W).
        Assert.Equal(20000.0 / 3.0, r.ProxyAfterW, 1e-9);
    }

    // ---- item 6: T3's demand at a derived tolerance ------------------------------------------------------------------

    // Demand (MW) of the constant-impedance 100 MW load at tau = 1 + n x 0.00625 behind X = 0.01: 100 V2^2 with
    // V2 = (1 / tau) / |1 + j0.01| (mpmath, 30 digits; exact for this circuit, since the load conductance is 1 pu).
    private static readonly double[] CvrDemandMw =
    {
        99.990000999900009999, 98.751746676341200416, 97.536352141344317024, 96.343258142852205803,
        95.171922427031538369, 94.021819122036373039, 92.892438147678917694, 91.783284649770169457,
        90.693878457959192743,
    };

    [Fact]
    public void F6_cvr_demand_on_every_step_at_the_derived_tolerance()
    {
        GridModel m = TapCase(0, 0.01, 1.0, 1.0, 1.1, constantImpedance: true);
        VvoResult r = VoltVarOptimisation.Run(m, new VvoOptions { Objective = VvoObjective.Demand, Common = new DistOptCommon { TolerancePu = 1e-12 } });

        // Bound: |J^-1| is about 0.01 pu/pu (|y| = 100 pu), so at a 1e-12 pu tolerance the state error is below 1e-14 pu;
        // the demand 100 V^2 MW moves by 200 MW/pu x 1e-14 = 2e-12 MW. The binary inputs (0.01, 0.00625) shift it by under
        // 5e-14 MW and the sums round by a few ulp of 100 MW (1e-13). Under 3e-12 MW in all; asserted at 1e-10 MW, the
        // spec's 1e-12 relative on the 100 MVA base.
        const double tolMw = 1e-10;
        Assert.Equal(CvrDemandMw[0], r.BaseObjectiveValue, tolMw);
        Assert.Equal(8, r.Moves.Count);
        for (int n = 1; n <= 8; n++)
        {
            Assert.Equal(n, r.Moves[n - 1].Position);
            Assert.Equal(CvrDemandMw[n], r.Moves[n - 1].Objective, tolMw);
        }
        Assert.Equal(CvrDemandMw[8], r.DemandMw, tolMw);
        Assert.Equal(CvrDemandMw[8], r.ObjectiveValue, tolMw);
    }

    // ---- item 7: B31 on the dist-opt CSV -----------------------------------------------------------------------------

    [Fact]
    public void F7_a_formula_like_load_name_is_neutralised_in_the_dist_opt_csv()
    {
        FrameLoad[] loads = [OnA("=SUM(A1)", 10000), OnA("L2", 20000), OnA("@cmd", 30000)];
        string csv = OptimisationReport.Csv(DistOptReport.Rows(PhaseBalancing.Run(PhaseFeeder(loads))));
        Assert.Contains("load,'=SUM(A1),from_phase,a\n", csv, StringComparison.Ordinal);
        Assert.Contains("load,'@cmd,to_phase,", csv, StringComparison.Ordinal);
        // No cell anywhere starts with a formula trigger.
        foreach (string line in csv.Split('\n', StringSplitOptions.RemoveEmptyEntries))
            foreach (string cell in line.Split(','))
                Assert.False(cell.Length > 0 && GridSim.Core.IO.CsvFormulaGuard.TriggerCharacters.Contains(cell[0], StringComparison.Ordinal), $"cell '{cell}' in '{line}'");
    }
}
