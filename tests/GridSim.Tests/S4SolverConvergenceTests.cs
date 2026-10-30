using GridSim.Core.Analysis;
using GridSim.Core.Diagnostics;
using GridSim.Core.IO;
using GridSim.Core.IO.Json;
using GridSim.Core.Model;
using GridSim.Core.Numerics;
using GridSim.Core.PowerFlow;
using Xunit;

namespace GridSim.Tests;

/// <summary>
/// S4 solver and convergence batch (October 2026, agent "S4 solver and convergence"). One region per finding id
/// (N082, N083, ...). Diagnosis tests pin the EVIDENCE of a diagnosis (a non-convergence that is a genuine physical
/// limit stays a non-convergence, and the test says why); fix tests pin the corrected behaviour against a hand
/// calculation. Every bit-identity claim is an exact comparison of IEEE-754 bit patterns, never a tolerance.
/// </summary>
public class S4SolverConvergenceTests
{
    private static GridModel Uk() => JsonGridLoader.LoadDirectory(Path.Combine(AppContext.BaseDirectory, "data", "uk"));

    private static void SameBits(double a, double b, string what) =>
        Assert.True(BitConverter.DoubleToInt64Bits(a) == BitConverter.DoubleToInt64Bits(b),
            $"{what}: {a:R} vs {b:R} differ in their bits");

    // =============================================================================================================
    // N082: case14 outage of branch 1-2 with reactive limits on is a genuine voltage collapse, not a loop failure
    // =============================================================================================================
    //
    // The diagnosis (N082): with branch 1-2 out, the four PV units (buses 2, 3,
    // 6, 8) all reach their Qmax, and with all four pinned the post-outage network can carry at most 98.04 % of case14's
    // load. The PV -> PQ loop does its job: it pins the right units (each one's voltage then sits BELOW its setpoint, so
    // no revert applies), and the remaining Newton solve has no solution to find. Load scaling shows the fold: the study
    // solve converges at lambda 0.98041 and fails from 0.9805, and the smallest singular value of the Jacobian falls from
    // 1.06e-1 at lambda 0.90 to 9.42e-4 at 0.98041 in the square-root profile of a saddle-node bifurcation. Reporting the
    // outage as not solved (contingency label "islanded / no solution", exit 3) is the honest answer.

    /// <summary>case14 with its first branch (1-2) out of service.</summary>
    private static GridModel Case14Without12()
    {
        GridModel m = IeeeCases.Case14();
        Assert.Equal((1, 2), (m.Branches[0].From, m.Branches[0].To));
        return m.CopyWith(branches: m.Branches.Skip(1).ToList());
    }

    [Fact]
    public void N082_case14_outage_1_2_solves_with_q_limits_off_and_not_with_them_on_in_both_engines()
    {
        GridModel post = Case14Without12();
        PowerFlowOptions study = ContingencyAnalysis.StudyOptions;

        // Q limits off: an ordinary 5-iteration solve (the old sweep's "0 islanded" on case14).
        PowerFlowResult off = new SparseNewtonRaphsonPowerFlow().Solve(post, study with { EnforceQLimits = false });
        Assert.True(off.Converged);
        Assert.Equal(5, off.Iterations);

        // Q limits on (the D04 study set): pass 0 converges in 5 and pins buses 2 and 6, pass 1 converges in 3 and pins
        // 3 and 8, pass 2 (every PV unit at Qmax) runs its 100 iterations without converging: 5 + 3 + 100 = 108.
        PowerFlowResult sparse = new SparseNewtonRaphsonPowerFlow().Solve(post, study);
        PowerFlowResult dense = new NewtonRaphsonPowerFlow().Solve(post, study);
        foreach (PowerFlowResult r in new[] { sparse, dense })
        {
            Assert.False(r.Converged);
            Assert.Equal(PowerFlowStatus.MaxIterations, r.Status);
            Assert.Equal(108, r.Iterations);
        }
        // The dense and sparse engines fail identically, to the bit (the last iterate is the same state).
        for (int i = 0; i < post.BusCount; i++)
        {
            SameBits(sparse.Vm[i], dense.Vm[i], $"Vm bus {post.Buses[i].Id}");
            SameBits(sparse.VaDeg[i], dense.VaDeg[i], $"Va bus {post.Buses[i].Id}");
        }
        SameBits(sparse.MaxMismatch, dense.MaxMismatch, "max mismatch");
    }

    [Fact]
    public void N082_the_q_limit_loop_pins_every_pv_unit_at_qmax_with_its_voltage_below_setpoint()
    {
        GridModel post = Case14Without12();
        var trace = new SolveTrace();
        new SparseNewtonRaphsonPowerFlow().Solve(post, ContingencyAnalysis.StudyOptions with { Trace = trace });

        // Two converged passes switch the four PV units, each at its over-excited limit; nothing reverts.
        var events = trace.QLimitEvents.Select(e => (e.BusId, e.PinnedAt, e.Pass)).ToArray();
        Assert.Equal(new[] { (2, "Max", 0), (6, "Max", 0), (3, "Max", 1), (8, "Max", 1) }, events);
        Assert.Equal(3, trace.Passes.Count);

        // The same configuration solved directly (the four units as PQ buses fixed at Qmax, limits off) has a
        // solution just below the base load: each unit's voltage is then below its setpoint, so the pinned state is
        // the physically right one (a unit at Qmax that cannot hold its voltage), and no revert rule would release it.
        GridModel pinned = PinnedAtQmax(post);
        PowerFlowResult r = new SparseNewtonRaphsonPowerFlow().Solve(
            ContinuationPowerFlow.ScaleLoads(pinned, 0.98), ContingencyAnalysis.StudyOptions with { EnforceQLimits = false });
        Assert.True(r.Converged);
        foreach (Generator g in post.Generators.Where(g => g.Bus != 1))
        {
            int i = post.Buses.ToList().FindIndex(b => b.Id == g.Bus);
            Assert.True(r.Vm[i] < g.Vg, $"bus {g.Bus}: Vm {r.Vm[i]:R} should sit below the setpoint {g.Vg:R}");
        }
    }

    [Fact]
    public void N082_the_post_outage_network_collapses_at_98_point_04_percent_load()
    {
        // The fold, bracketed: the study solve (Q limits on, cold start from the case's own voltages) converges at
        // lambda 0.98041 with every PV unit pinned and fails at 0.9805 and at the base load (lambda 1). The smallest
        // singular value of the pinned configuration's Jacobian falls towards zero on the way (measured: 1.060e-1 at
        // 0.90, 7.159e-3 at 0.98, 9.420e-4 at 0.98041), so 0.9805 fails because there is no solution, not for want of
        // iterations: case14 without line 1-2 cannot carry its base load inside its generators' reactive limits.
        GridModel post = Case14Without12();
        PowerFlowOptions study = ContingencyAnalysis.StudyOptions;

        var trace = new SolveTrace();
        PowerFlowResult below = new SparseNewtonRaphsonPowerFlow().Solve(
            ContinuationPowerFlow.ScaleLoads(post, 0.98041), study with { Trace = trace });
        Assert.True(below.Converged);
        Assert.Equal(4, trace.QLimitEvents.Count);
        Assert.True(below.Vm.Min() < 0.84, $"min Vm {below.Vm.Min():R}: the nose is a deep voltage depression");

        Assert.False(new SparseNewtonRaphsonPowerFlow().Solve(ContinuationPowerFlow.ScaleLoads(post, 0.9805), study).Converged);
        Assert.False(new SparseNewtonRaphsonPowerFlow().Solve(post, study).Converged);

        // The Jacobian's smallest singular value shrinks towards the fold (pinned configuration, limits off, dense
        // engine so the Jacobian is captured).
        GridModel pinned = PinnedAtQmax(post);
        double SigmaMin(double lambda)
        {
            var t = new SolveTrace { CaptureJacobian = true };
            PowerFlowResult r = new NewtonRaphsonPowerFlow().Solve(
                ContinuationPowerFlow.ScaleLoads(pinned, lambda), study with { EnforceQLimits = false, Trace = t });
            Assert.True(r.Converged, $"pinned configuration at lambda {lambda}");
            double[][] j = t.Passes[^1].Iterations[^1].Jacobian!;
            int n = j.Length;
            var jtj = new double[n, n];
            for (int a = 0; a < n; a++)
                for (int b = 0; b < n; b++)
                {
                    double acc = 0;
                    for (int k = 0; k < n; k++) acc += j[k][a] * j[k][b];
                    jtj[a, b] = acc;
                }
            return Math.Sqrt(Math.Max(0.0, SymmetricEigen.Eigenvalues(jtj).Min()));
        }
        double s90 = SigmaMin(0.90), s98 = SigmaMin(0.98), sNose = SigmaMin(0.98041);
        Assert.True(s90 > 0.1 && s98 < 0.01 && sNose < 0.001, $"sigma_min {s90:E3}, {s98:E3}, {sNose:E3}");
    }

    /// <summary>The post-outage case14 with its four PV units (buses 2, 3, 6, 8) turned into PQ buses whose units
    /// inject exactly their Qmax: the final bus types of the failing Q-limited solve.</summary>
    private static GridModel PinnedAtQmax(GridModel post)
    {
        var pv = new HashSet<int> { 2, 3, 6, 8 };
        return post.CopyWith(
            buses: post.Buses.Select(b => pv.Contains(b.Id) ? b with { Type = BusType.Pq } : b).ToList(),
            generators: post.Generators.Select(g => pv.Contains(g.Bus) ? g with { Qg = g.Qmax } : g).ToList());
    }

    // =============================================================================================================
    // N083: the corrective redispatch measures the reference unit's room from its DC-balanced output
    // =============================================================================================================
    //
    // A triangle of equal reactances (0.1 pu): bus 1 slack (G1), bus 2 (G2), bus 3 load 100 MW. Moving D MW from G1 to G2
    // changes the 1-3 flow by -D/3 and the 2-3 flow by +D/3.

    private static GridModel Triangle(double g1Pg, double g1Pmax, double g2Pg, double rate13)
    {
        var buses = new List<Bus> { new(1, BusType.Slack), new(2, BusType.Pv), new(3, BusType.Pq, Pd: 100) };
        var gens = new List<Generator> { new(1, Pg: g1Pg, Pmax: g1Pmax), new(2, Pg: g2Pg, Pmax: 200) };
        var branches = new List<Branch>
        {
            new(1, 2, 0.0, 0.1) { Name = "L12" },
            new(2, 3, 0.0, 0.1) { Name = "L23" },
            new(1, 3, 0.0, 0.1) { Name = "L13", RateAMva = rate13 },
        };
        return new GridModel { BaseMva = 100, Buses = buses, Generators = gens, Branches = branches };
    }

    [Fact]
    public void N083_a_slack_stated_at_zero_still_has_the_room_it_really_has()
    {
        // G1 (slack) is stated at 0 MW but supplies the whole 100 MW load (G2 stated 0): the reference absorbs it. L13
        // (rate 50) carries 66.67 MW; relieving it needs 50 MW moved from G1 to G2. Before N083 the LP gave G1 no room
        // to fall (stated 0 - Pmin 0 = 0), so the redispatch was Infeasible and the overload was left in place. Now
        // G1's room is measured from its DC-balanced 100 MW: Applied, G2 rises to 50, G1 falls by 50 (written as its
        // stated value plus the change, -50; the AC slack produces whatever the network then needs, 50 MW in DC).
        CorrectiveRedispatch plan = ContingencyAnalysis.PlanCorrectiveRedispatch(Triangle(g1Pg: 0, g1Pmax: 200, g2Pg: 0, rate13: 50));
        Assert.Equal(RedispatchOutcome.Applied, plan.Outcome);
        Assert.Equal(new[] { "branch L13" }, plan.OverloadedBefore);
        Assert.Equal(50.0, plan.Model.Generators[1].Pg, 9);
        Assert.Equal(-50.0, plan.Model.Generators[0].Pg, 9);
        Assert.Equal(100.0, plan.MovedMw, 9);
    }

    [Fact]
    public void N083_a_slack_stated_above_its_real_output_is_not_offered_room_it_does_not_have()
    {
        // G1 stated 100 MW and G2 stated 100 MW against 100 MW of load: the reference absorbs the 100 MW surplus, so G1
        // really produces 0 MW, at its Pmin. In DC, G2's 100 MW reaches bus 3 one third via 2-1-3, so L13 carries
        // 33.33 MW; with L13 rated 20 the only relief is moving D = 40 MW from G1 to G2, which would take G1 to -40 MW.
        // Before N083 the LP measured G1's room from its stated 100 MW, found 100 MW of footroom and "applied" a
        // redispatch that drove the real slack output below its floor. Now it is Infeasible and the dispatch is
        // unchanged: no fabricated operating point, and the AC screen reports the overload.
        GridModel m = Triangle(g1Pg: 100, g1Pmax: 200, g2Pg: 100, rate13: 20);
        CorrectiveRedispatch plan = ContingencyAnalysis.PlanCorrectiveRedispatch(m);
        Assert.Equal(new[] { "branch L13" }, plan.OverloadedBefore);
        Assert.Equal(RedispatchOutcome.Infeasible, plan.Outcome);
        Assert.Same(m, plan.Model);
        Assert.Equal(0.0, plan.MovedMw);
    }

    // =============================================================================================================
    // N084: `chain --bess` solves with the contingency family's option set and the OLTC loop, as plain `chain` does
    // =============================================================================================================

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "GridSim.slnx"))) dir = dir.Parent;
        Assert.True(dir is not null, $"GridSim.slnx not found above {AppContext.BaseDirectory}");
        return dir!.FullName;
    }

    [Fact]
    public void N084_chain_bess_passes_the_study_set_and_the_oltc_loop_to_the_storage_time_series()
    {
        string program = File.ReadAllText(Path.Combine(RepoRoot(), "src", "GridSim.Cli", "Program.cs")).Replace("\r\n", "\n");
        string storage = File.ReadAllText(Path.Combine(RepoRoot(), "src", "GridSim.Cli", "StorageCli.cs")).Replace("\r\n", "\n");
        Assert.Contains("IPowerFlowSolver chainSolver = ContingencyAnalysis.WithOltcLoop(backend.NewtonSolver(chainModel));", program);
        Assert.Contains("FlagString(rest, \"--out\"), chainSolver, ContingencyAnalysis.StudyOptions);", program);
        Assert.Contains("StorageTimeSeries.Run(baseModel, sites, spec2, solver, options);", storage);
    }

    /// <summary>A one-site storage day on <paramref name="m"/> (100 MW / 200 MWh LFP at <paramref name="bus"/>, peak
    /// shaving at 90 % / 70 % of the peak demand, 8 steps of 3 h), the set-up of <c>chain --bess lfp:bus:100:200</c>.</summary>
    private static GridSim.Core.Storage.StorageTimeSeriesResult StorageDay(GridModel m, int bus,
        GridSim.Core.Abstractions.IPowerFlowSolver solver, PowerFlowOptions? options)
    {
        const int steps = 8;
        const double stepHours = 24.0 / steps;
        double[] f = Enumerable.Range(0, steps).Select(s => GridSim.Core.Simulation.LoadProfile.DailyFactor(s * stepHours % 24.0)).ToArray();
        double peak = m.Buses.Sum(b => b.Pd) * f.Max();
        var pack = GridSim.Core.Storage.PackModel.Sized(GridSim.Core.Storage.BatteryChemistryLibrary.ByName("lfp"), 200e6, 1000.0, 0.5, ratedPowerW: 100e6);
        var site = new GridSim.Core.Storage.StorageSite("lfp", bus, 100, pack, new GridSim.Core.Storage.PeakShavePolicy(0.9 * peak, 0.7 * peak));
        return GridSim.Core.Storage.StorageTimeSeries.Run(m, new[] { site },
            new GridSim.Core.Storage.StorageTimeSeriesSpec(steps, stepHours, s => f[s]), solver, options);
    }

    [Fact]
    public void N084_without_oltcs_the_storage_chain_is_unchanged_to_the_bit()
    {
        // case118 (the chain default) has no OLTC records, so the study set plus the OLTC loop gives the old storage
        // chain (raw engine, its own default of 100 iterations with Q limits on) to the bit, step by step.
        GridModel m = IeeeCases.Case118();
        var before = StorageDay(m, 59, SolverSelector.CreateNewton(m), null);
        var after = StorageDay(m, 59, ContingencyAnalysis.WithOltcLoop(SolverSelector.CreateNewton(m)), ContingencyAnalysis.StudyOptions);
        for (int s = 0; s < before.Steps.Count; s++)
        {
            Assert.Equal(before.Steps[s].Converged, after.Steps[s].Converged);
            SameBits(before.Steps[s].VminPu, after.Steps[s].VminPu, $"step {s} Vmin");
            SameBits(before.Steps[s].VmaxPu, after.Steps[s].VmaxPu, $"step {s} Vmax");
            SameBits(before.Steps[s].SlackMw, after.Steps[s].SlackMw, $"step {s} slack");
            SameBits(before.Steps[s].LossMw, after.Steps[s].LossMw, $"step {s} loss");
        }
    }

    [Fact]
    public void N084_on_gb_reduced_the_storage_chain_now_solves_to_the_tap_regulated_state()
    {
        // GB reduced has OLTCs. Measured step 0 before -> after: Vmin 1.0313969238603367 -> 0.9977947297199731 pu,
        // slack 3410.920657001034 -> 3411.9305068986973 MW, loss 26.220656968923777 -> 27.230506898039494 MW: the same
        // shift plain `chain` made under D04 (Vmin 1.027 -> 0.998 on its fixed run), now in the storage chain too.
        GridModel m = Uk();
        var r = StorageDay(m, 6, ContingencyAnalysis.WithOltcLoop(SolverSelector.CreateNewton(m)), ContingencyAnalysis.StudyOptions);
        Assert.All(r.Steps, s => Assert.True(s.Converged));
        SameBits(0.9977947297199731, r.Steps[0].VminPu, "step 0 Vmin");
        SameBits(27.230506898039494, r.Steps[0].LossMw, "step 0 loss");
    }

    [Fact]
    public void N083_gb_reduced_redispatches_are_unchanged_to_the_bit()
    {
        // On the reduced GB model the reference unit's bounds never bind, so the two first outages that need a
        // redispatch move exactly what they moved before N083 (measured before and after on every first outage of
        // case9, case14, case30, case57, case118, case300, case24_ieee_rts, GB reduced and Ireland: every Pg identical).
        GridModel m = Uk();
        (int Branch, double Moved, (int Gen, double Pg)[] Changes)[] expected =
        {
            (15, 250, new[] { (0, 525.0), (5, 2285.0) }),    // T2 GSP S Scotland: relieves L2-3 B6 boundary
            (23, 510, new[] { (0, 905.0), (25, 2625.0) }),   // T10 GSP SW & S Wales: relieves the SW export boundary
        };
        foreach ((int branch, double moved, (int Gen, double Pg)[] changes) in expected)
        {
            GridModel post = m.CopyWith(branches: m.Branches.Where((_, j) => j != branch).ToList());
            CorrectiveRedispatch plan = ContingencyAnalysis.PlanCorrectiveRedispatch(post);
            Assert.Equal(RedispatchOutcome.Applied, plan.Outcome);
            SameBits(moved, plan.MovedMw, $"branch {branch} MW moved");
            for (int i = 0; i < m.Generators.Count; i++)
            {
                double want = post.Generators[i].Pg;
                foreach ((int gen, double pg) in changes) if (gen == i) want = pg;
                SameBits(want, plan.Model.Generators[i].Pg, $"branch {branch} generator {i}");
            }
        }
    }

    // =============================================================================================================
    // N071: an OLTC follows the bus type the solve ENDED with: a PV bus pinned at a reactive limit is regulated
    // =============================================================================================================

    /// <summary>Bus 1 slack (1.0 pu) feeds bus 2 through transformer T12 (x 0.1 pu) whose OLTC regulates bus 2 (its To end,
    /// target 1.0 +/- 0.01 pu). Bus 2 is PV (Vg 1.0) but its unit can give only 10 MVAr against a 60 MVAr load.</summary>
    private static GridModel PvBusBehindOltc() => new()
    {
        BaseMva = 100,
        Buses = new List<Bus> { new(1, BusType.Slack), new(2, BusType.Pv, Pd: 50, Qd: 60) },
        Generators = new List<Generator> { new(1, Pg: 50, Vg: 1.0), new(2, Pg: 0, Vg: 1.0, Qmin: -10, Qmax: 10) },
        Branches = new List<Branch> { new(1, 2, 0.0, 0.1) { Name = "T12" } },
        Oltcs = new List<Oltc> { new("T12", 2) },
    };

    [Fact]
    public void N071_a_pv_bus_pinned_at_qmax_is_regulated_by_its_tap_changer()
    {
        // Measured before N071: the OLTC held its tap at 1.0 because the MODEL types bus 2 PV, although the solve had
        // pinned its unit at Qmax (10 MVAr) and it sat at 0.9457323728577838 pu, below the 0.99 pu band edge; and
        // TapsSettled still said true. Now the hold follows the solved type: the tap steps down 4 x 0.0125 to 0.95, bus 2
        // rises until its unit can hold 1.0 pu again (8.56 MVAr, back to PV), and the final pass holds the tap there.
        var ss = new SteadyStateSolver();
        PowerFlowResult r = ss.Solve(PvBusBehindOltc());
        Assert.True(r.Converged);
        Assert.True(ss.TapsSettled);
        Assert.Equal(4, ss.OltcPasses);
        Assert.Equal(0.95, ss.RegulatedModel.Branches[0].TapRatio, 12);
        Assert.Equal(1.0, r.Vm[1], 12);
        Assert.Equal(1, ss.TapsHeldAtGeneratorBus);                            // bus 2 is PV again on the final pass
        Assert.Equal(new[] { BusType.Slack, BusType.Pv }, r.SolvedBusTypes!);
    }

    [Fact]
    public void N071_both_engines_report_the_solved_bus_types_identically()
    {
        // At the untapped point the unit is pinned: the Newton engines report bus 2 as PQ, the model still says PV.
        GridModel m = PvBusBehindOltc();
        PowerFlowResult sparse = new SparseNewtonRaphsonPowerFlow().Solve(m);
        PowerFlowResult dense = new NewtonRaphsonPowerFlow().Solve(m);
        Assert.Equal(new[] { BusType.Slack, BusType.Pq }, sparse.SolvedBusTypes!);
        Assert.Equal(sparse.SolvedBusTypes!, dense.SolvedBusTypes!);
        SameBits(sparse.Vm[1], dense.Vm[1], "Vm bus 2");
        SameBits(0.9457323728577838, sparse.Vm[1], "Vm bus 2 at tap 1.0");
        // With limits off nothing is pinned, and the reported types are the model's.
        PowerFlowResult off = new SparseNewtonRaphsonPowerFlow().Solve(m, new PowerFlowOptions { EnforceQLimits = false });
        Assert.Equal(new[] { BusType.Slack, BusType.Pv }, off.SolvedBusTypes!);
    }

    [Fact]
    public void N071_bundled_oltc_models_are_unchanged_to_the_bit()
    {
        // Measured before and after on the base solve of GB reduced, gb-synthetic and gb-full (defaults and the study set:
        // every Vm and every tap identical) and on a post-outage sample (GB reduced every branch, gb-full and gb-synthetic
        // every 40th: every result identical): no OLTC-controlled PV bus is Q-limited there. Pinned here on GB reduced
        // and gb-full's base solves: the counts and the lowest voltage.
        foreach ((string dir, int passes, int held, double vmin) in new[]
        {
            ("uk", 3, 0, 0.9980210494649094),
            // Moved 2026-10 (D1, Mark's determinism rule 2026-10-04: identical bits on every CPU tier and OS): deterministic
            // fdlibm sin / cos in the Newton solve. Before -> after: gb-full Vmin 0.9601721125721315 -> 0.9601721125721316 (one ulp;
            // passes, held taps and uk unchanged).
            ("gb-full", 4, 71, 0.9601721125721316),
        })
        {
            GridModel m = JsonGridLoader.LoadDirectory(Path.Combine(AppContext.BaseDirectory, "data", dir));
            var ss = new SteadyStateSolver();
            PowerFlowResult r = ss.Solve(m);
            Assert.True(r.Converged, dir);
            Assert.True(ss.TapsSettled, dir);
            Assert.Equal(passes, ss.OltcPasses);
            Assert.Equal(held, ss.TapsHeldAtGeneratorBus);
            SameBits(vmin, r.Vm.Min(), dir + " Vmin");
        }
    }

    // =============================================================================================================
    // N051: `synth --scale 0.02` has no solution because the generated data overloads its 11 kV layer
    // =============================================================================================================

    [Fact]
    public void N051_small_synthetic_scales_build_one_overloaded_network_with_no_solution()
    {
        // Scale multiplies the 11 kV secondaries per primary (floor 2). Every scale up to 0.36 builds the same 1,082-bus
        // network (0.02 and 0.3 are identical). Before N108 every zone kept its GB demand there, so its secondaries
        // carried a median 175 % of their 30 MVA rating and the Newton solve stalled near a 0.3 pu mismatch. Scale 1.0
        // (2,227 buses, median 63 %) converges in 6 iterations.
        GridModel tiny = GridSim.Core.Synthetic.SyntheticGb.Build(new GridSim.Core.Synthetic.SyntheticGb.Options { Scale = 0.02 });
        GridModel small = GridSim.Core.Synthetic.SyntheticGb.Build(new GridSim.Core.Synthetic.SyntheticGb.Options { Scale = 0.3 });
        Assert.Equal(1082, tiny.BusCount);
        Assert.Equal(small.BusCount, tiny.BusCount);
        Assert.Equal(small.Branches.Count, tiny.Branches.Count);
        var opt = new PowerFlowOptions { MaxIterations = 60, EnforceQLimits = false };   // the synth verb's check solve
        // Moved 2026-10 (N108, Ruling R1, Mark delegated 2026-10-04): below scale 1.0 each zone's demand, embedded plant
        // and transmission fleet now shrink with its 11 kV layer, so a small scale is a smaller GB and it solves.
        // Before: Assert.False(... Converged) (GB's 39.5 GW on 554 secondaries, no solution). After: converged, with
        // 14.5 GW of demand (the `synth --scale 0.02` summary: 4 Newton iterations, mismatch 3.8E-012, V 0.951-1.040 pu).
        Assert.True(new SparseNewtonRaphsonPowerFlow().Solve(tiny, opt).Converged);

        GridModel full = GridSim.Core.Synthetic.SyntheticGb.Build();
        Assert.Equal(2227, full.BusCount);
        PowerFlowResult r = new SparseNewtonRaphsonPowerFlow().Solve(full, opt);
        Assert.True(r.Converged);
        Assert.Equal(6, r.Iterations);
    }

    [Theory]
    [InlineData(0.0)]
    [InlineData(-1.0)]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    public void N051_a_synthetic_scale_that_is_not_a_positive_finite_number_fails_loud(double scale)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            GridSim.Core.Synthetic.SyntheticGb.Build(new GridSim.Core.Synthetic.SyntheticGb.Options { Scale = scale }));
    }

    // =============================================================================================================
    // N070: the AC loss loop inflates each DC island's demand by that island's own losses
    // =============================================================================================================

    /// <summary>case14 twice, as two islands: the copy (bus ids + 100) at 1.3x the load.</summary>
    private static GridModel TwoIslandCase14()
    {
        GridModel a = IeeeCases.Case14();
        var buses = a.Buses.ToList();
        var gens = a.Generators.ToList();
        var branches = a.Branches.ToList();
        buses.AddRange(a.Buses.Select(b => b with { Id = b.Id + 100, Pd = b.Pd * 1.3, Qd = b.Qd * 1.3 }));
        gens.AddRange(a.Generators.Select(g => g with { Bus = g.Bus + 100 }));
        branches.AddRange(a.Branches.Select(b => b with { From = b.From + 100, To = b.To + 100 }));
        return a.CopyWith(buses: buses, generators: gens, branches: branches);
    }

    [Fact]
    public void N070_each_island_pays_for_its_own_losses()
    {
        // Before N070 one system-wide ratio was used: island 0 was dispatched 2.2961060308310266 MW above its demand plus
        // losses and island 1 2.2964030498212082 MW below, so island 1's slack (dispatched at its 332.4 MW Pmax) really ran
        // at 334.6964030498212 MW. Now each island's slack produces what the dispatch gave it, to within the loss tolerance.
        GridModel m = TwoIslandCase14();
        Assert.Equal(2, DistributionFactors.FindIslands(m).Count);
        AcOpfResult ac = AcOptimalPowerFlow.Solve(m);
        Assert.True(ac.Converged);
        GridModel dispatched = m.CopyWith(generators: m.Generators.Select((g, i) => g with { Pg = ac.Dispatch[i].Pg }).ToList());
        PowerFlowResult r = new SparseNewtonRaphsonPowerFlow().Solve(dispatched);
        Assert.True(r.Converged);
        foreach (int slackId in new[] { 1, 101 })
        {
            int i = m.Buses.ToList().FindIndex(b => b.Id == slackId);
            double slackAc = r.PInjMw[i] + m.Buses[i].Pd;
            double slackDispatched = ac.Dispatch.Where((_, k) => m.Generators[k].Bus == slackId).Sum(d => d.Pg);
            Assert.True(Math.Abs(slackAc - slackDispatched) < 1e-3,
                $"island slack {slackId}: AC {slackAc:R} MW vs dispatched {slackDispatched:R} MW");
        }
    }

    [Fact]
    public void N070_a_connected_network_keeps_its_loss_loop_to_the_bit()
    {
        // Measured before and after: case14 loss 15.85798410498625 MW, cost 13742.899205249312 /h, 2 iterations; case24
        // loss 33.48224980228801 MW, cost 144174.1034908353 /h, 5 iterations.
        AcOpfResult a = AcOptimalPowerFlow.Solve(IeeeCases.Case14());
        Assert.Equal(2, a.Iterations);
        SameBits(15.85798410498625, a.TotalLossMw, "case14 loss");
        SameBits(13742.899205249312, a.TotalCostPerHour, "case14 cost");
        AcOpfResult b = AcOptimalPowerFlow.Solve(IeeeCases.Case24Rts());
        Assert.Equal(5, b.Iterations);
        // Moved 2026-10 (D1, Mark's determinism rule 2026-10-04: identical bits on every CPU tier and OS): the AC loss
        // loop's power flows use the deterministic fdlibm sin / cos. Before -> after: case24 loss 33.48224980228801 ->
        // 33.482249802288464 (case14 and both costs and iteration counts unchanged; the comment above records the pre-N070 values).
        SameBits(33.482249802288464, b.TotalLossMw, "case24 loss");
        SameBits(144174.1034908353, b.TotalCostPerHour, "case24 cost");
    }

    // =============================================================================================================
    // N072: gb-full's security DC-OPF is Infeasible because of the data, not the LP
    // =============================================================================================================

    [Fact]
    public void N072_gb_full_has_three_circuits_no_dispatch_can_bring_inside_their_rating()
    {
        // Under the unconstrained merit order 121 of gb-full's 5,876 rated circuits are over their rating (DC). For each
        // circuit the flow is (load-only flow) + sum_i a_i Pg_i, a_i its PTDF to unit i's bus; letting EVERY unit move
        // anywhere in [Pmin, Pmax] (a relaxation: the balance row is dropped, so it can only widen the range) bounds the
        // flow any dispatch can give it. For three circuits even that range misses [-rating, +rating], so no dispatch is
        // secure and the security LP's Infeasible is the right answer: the case's ratings do not carry its demand.
        GridModel m = JsonGridLoader.LoadDirectory(Path.Combine(AppContext.BaseDirectory, "data", "gb-full"));
        DistributionFactors df = DistributionFactors.Build(m);
        DcOptimalPowerFlow.DcOpfResult merit = DcOptimalPowerFlow.Solve(m, enforceLineLimits: false);

        Dictionary<int, double> Injections(Func<int, double> pgOf)
        {
            var inj = new Dictionary<int, double>();
            foreach (Bus b in m.Buses) if (b.Pd != 0) inj[b.Id] = inj.GetValueOrDefault(b.Id) - b.Pd;
            for (int i = 0; i < m.Generators.Count; i++)
                inj[m.Generators[i].Bus] = inj.GetValueOrDefault(m.Generators[i].Bus) + pgOf(i);
            return inj;
        }
        double[] flows = df.DcFlows(Injections(i => merit.Dispatch[i].Pg), m.BaseMva);
        double[] loadOnly = df.DcFlows(Injections(_ => 0.0), m.BaseMva);
        int over = 0;
        for (int l = 0; l < m.Branches.Count; l++)
            if (m.Branches[l].InService && m.Branches[l].RateAMva > 0 && Math.Abs(flows[l]) > m.Branches[l].RateAMva + 1e-3) over++;
        Assert.Equal(121, over);

        var columns = new Dictionary<int, double[]>();
        (double Min, double Max) Range(int l)
        {
            double lo = loadOnly[l], hi = loadOnly[l];
            foreach (Generator g in m.Generators)
            {
                if (!columns.TryGetValue(g.Bus, out double[]? col)) { col = df.PtdfColumn(g.Bus); columns[g.Bus] = col; }
                double pmax = double.IsInfinity(g.Pmax) ? DcOptimalPowerFlow.UnboundedPmaxMw : g.Pmax;
                double a = col[l] * g.Pmin, b = col[l] * pmax;
                lo += Math.Min(a, b);
                hi += Math.Max(a, b);
            }
            return (lo, hi);
        }
        foreach ((int branch, string name, double rating) in new[]
        {
            (2157, "COPP C1-_COPP T1 132kV", 109.0),   // reachable flow -164.4 .. -125.7 MW
            (2527, "ENW-ENW 132kV#23", 150.0),         // 168.8 MW whatever the dispatch (no unit moves it)
            (2528, "ENW-ENW 132kV#24", 150.0),         // its parallel twin
        })
        {
            Assert.Equal(name, m.Branches[branch].Name);
            Assert.Equal(rating, m.Branches[branch].RateAMva);
            (double lo, double hi) = Range(branch);
            Assert.True(lo > rating || hi < -rating, $"{name}: reachable flow [{lo:F1}, {hi:F1}] MW meets its {rating} MW rating");
        }
    }

    // =============================================================================================================
    // N019: FactorDense reports a clamped pivot; the RMS converter network refuses a singular converter block
    // =============================================================================================================

    [Fact]
    public void N019_factor_dense_counts_clamped_pivots_and_solves_unchanged()
    {
        // A non-singular matrix: no clamp, and the replayed solve is bit-identical to the one-shot SolveDense.
        var good = new System.Numerics.Complex[,] { { new(4, 1), new(1, 0) }, { new(1, 0), new(3, -1) } };
        var b = new[] { new System.Numerics.Complex(1, 2), new System.Numerics.Complex(-1, 0.5) };
        DenseComplexLu lu = DenseComplexLuSolver.FactorDense(good, 2);
        Assert.Equal(0, lu.ClampedPivots);
        System.Numerics.Complex[] x = lu.Solve(b), y = DenseComplexLuSolver.SolveDense(good, b, 2);
        for (int i = 0; i < 2; i++)
        {
            SameBits(y[i].Real, x[i].Real, $"x{i} re");
            SameBits(y[i].Imaginary, x[i].Imaginary, $"x{i} im");
        }

        // A singular matrix (second row twice the first): one pivot clamped, and the count says so.
        var bad = new System.Numerics.Complex[,] { { new(1, 1), new(2, 0) }, { new(2, 2), new(4, 0) } };
        Assert.Equal(1, DenseComplexLuSolver.FactorDense(bad, 2).ClampedPivots);
    }

    [Fact]
    public void N019_a_singular_converter_block_is_refused_by_the_rms_converter_network()
    {
        // One machine node (0) and two converter nodes (1, 2). Node 2 has no admittance at all, so Ycc is singular.
        // Before N019 the network was built on the clamped factor's fabricated "inverse"; now it is refused.
        var y = new System.Numerics.Complex[3, 3];
        y[0, 0] = new(0, -10); y[0, 1] = new(0, 5); y[1, 0] = new(0, 5); y[1, 1] = new(0, -5);
        var ex = Assert.Throws<ArgumentException>(() => new GridSim.Core.Dynamics.Models.ConverterNetwork(y, 1));
        Assert.Contains("singular", ex.Message, StringComparison.Ordinal);

        // Connected, it builds.
        y[1, 2] = new(0, 2); y[2, 1] = new(0, 2); y[2, 2] = new(0, -4); y[1, 1] = new(0, -7);
        _ = new GridSim.Core.Dynamics.Models.ConverterNetwork(y, 1);
    }

    // =============================================================================================================
    // N003: the raw-unit distribution feeders converge once their units are normalised (data, not the solver)
    // =============================================================================================================

    private static string MatpowerData(string rel) => Path.Combine(AppContext.BaseDirectory, "data", "matpower", rel);

    [Theory]
    [InlineData("case22.m")]
    [InlineData("case33bw.m")]
    [InlineData("case69.m")]
    [InlineData("case85.m")]
    public void N003_raw_unit_feeders_fail_as_parsed_and_solve_once_normalised(string file)
    {
        // The files give ohms and kW. Parsed verbatim the load is about 1000x too large and neither engine converges
        // (mismatch 60 to 124 pu, flat from the first iteration). Every user surface (CLI, web, desktop) loads .m files
        // through MatpowerUnitNormalizer (R021), and then both Newton engines converge in 3 or 4 iterations, bit-identical,
        // and agree with the backward/forward sweep.
        // Moved 2026-10 (N165, authorised by Mark's 2026-10-02 best-judgement ruling): ParseFile now normalises a raw
        // feeder by default, so the raw model is asked for explicitly (AsWritten); every expectation is unchanged.
        // Before: ParseFile(path). After: ParseFile(path, MatpowerUnitHandling.AsWritten).
        GridModel raw = GridSim.Core.IO.Matpower.MatpowerParser.ParseFile(MatpowerData(Path.Combine("dist", file)),
            GridSim.Core.IO.Matpower.MatpowerUnitHandling.AsWritten);
        Assert.True(GridSim.Core.IO.Matpower.MatpowerUnitNormalizer.LooksRawUnits(raw));
        Assert.False(new SparseNewtonRaphsonPowerFlow().Solve(raw).Converged);

        GridModel m = GridSim.Core.IO.Matpower.MatpowerUnitNormalizer.Normalize(raw);
        PowerFlowResult sparse = new SparseNewtonRaphsonPowerFlow().Solve(m);
        PowerFlowResult dense = new NewtonRaphsonPowerFlow().Solve(m);
        Assert.True(sparse.Converged);
        Assert.InRange(sparse.Iterations, 3, 4);
        for (int i = 0; i < m.BusCount; i++) SameBits(sparse.Vm[i], dense.Vm[i], $"{file} Vm {i}");
        PowerFlowResult bfs = new BackwardForwardSweep().Solve(m);
        Assert.True(bfs.Converged);
        Assert.Equal(sparse.Vm.Min(), bfs.Vm.Min(), 6);
    }

    [Fact]
    public void N003_case33bw_normalised_matches_baran_and_wu()
    {
        // Baran & Wu (1989): minimum voltage 0.9131 pu, real loss 202.7 kW.
        GridModel m = GridSim.Core.IO.Matpower.MatpowerUnitNormalizer.Normalize(
            GridSim.Core.IO.Matpower.MatpowerParser.ParseFile(MatpowerData(Path.Combine("dist", "case33bw.m"))));
        PowerFlowResult r = new SparseNewtonRaphsonPowerFlow().Solve(m);
        Assert.True(r.Converged);
        Assert.Equal(0.9131, r.Vm.Min(), 4);
        Assert.Equal(0.2027, r.TotalLossMw, 4);
    }

    // =============================================================================================================
    // N052: three pglib files carry a placeholder dispatch with no power-flow solution (data, not the solver)
    // =============================================================================================================

    [Theory]
    [InlineData("pglib_opf_case3_lmbd.m", 315.0, 2000.0)]
    [InlineData("pglib_opf_case162_ieee_dtc.m", 7239.06, 5516.0)]
    [InlineData("pglib_opf_case240_pserc.m", 144179.7282, 100642.85)]
    public void N052_pglib_placeholder_dispatch_fails_and_a_balanced_dispatch_solves(string file, double demandMw, double statedMw)
    {
        // As shipped, the stated generation is far from the demand (case3: a 1,685 MW surplus over lines of 111-161 MW
        // V^2/X capability; case162 and case240: deficits of 1,723.1 and 43,536.9 MW, above the slack unit's Pmax), so the
        // power flow has no solution at any cap. At the DC-OPF's balanced dispatch each converges in a few iterations.
        GridModel m = GridSim.Core.IO.Matpower.MatpowerParser.ParseFile(MatpowerData(Path.Combine("nesta", file)));
        Assert.Equal(demandMw, m.Buses.Sum(b => b.Pd), 6);
        Assert.Equal(statedMw, m.Generators.Sum(g => g.Pg), 6);
        var cap = new PowerFlowOptions { MaxIterations = 100 };
        Assert.False(new SparseNewtonRaphsonPowerFlow().Solve(m, cap).Converged);

        DcOptimalPowerFlow.DcOpfResult dc = DcOptimalPowerFlow.Solve(m);
        Assert.True(dc.Feasible);
        GridModel balanced = m.CopyWith(generators: m.Generators.Select((g, i) => g with { Pg = dc.Dispatch[i].Pg }).ToList());
        PowerFlowResult r = new SparseNewtonRaphsonPowerFlow().Solve(balanced, cap);
        Assert.True(r.Converged, $"{file} at the DC-OPF dispatch: {r.Status}");
        Assert.InRange(r.Iterations, 1, 12);
    }

    [Fact]
    public void N052_case3_lmbd_at_its_published_opf_point_reproduces_the_published_table()
    {
        // The pglib header's OPF solution: Pg 148.07 (slack), 170.01, 0.00 MW; Vm 1.100, 0.926, 0.900 pu; angles 0,
        // 7.259, -17.267 deg; Qg 54.70, -8.79, -4.84 MVAr. Solved at those setpoints GridSim gives the same to the one
        // decimal place the asserts check (GR-878: the table publishes two or three; the table's rounding of Pg and Vm is
        // the only difference).
        GridModel m = GridSim.Core.IO.Matpower.MatpowerParser.ParseFile(MatpowerData(Path.Combine("nesta", "pglib_opf_case3_lmbd.m")));
        double[] vg = { 1.100, 0.926, 0.900 };
        double[] pg = { 148.07, 170.01, 0.0 };
        GridModel opf = m.CopyWith(generators: m.Generators.Select((g, i) => g with { Pg = pg[i], Vg = vg[i] }).ToList());
        PowerFlowResult r = new SparseNewtonRaphsonPowerFlow().Solve(opf, new PowerFlowOptions { EnforceQLimits = false });
        Assert.True(r.Converged);
        Assert.Equal(7.259, r.VaDeg[1], 1);
        Assert.Equal(-17.267, r.VaDeg[2], 1);
        Assert.Equal(148.07, r.PInjMw[0] + 110.0, 1);
        Assert.Equal(54.70, r.QInjMvar[0] + 40.0, 1);
        Assert.Equal(-8.79, r.QInjMvar[1] + 40.0, 1);
        Assert.Equal(-4.84, r.QInjMvar[2] + 50.0, 1);
    }

    // =============================================================================================================
    // N053: the loss reconciliation accounts for bus shunt conductance
    // =============================================================================================================

    [Fact]
    public void N053_case300_losses_reconcile_once_the_shunt_conductance_is_counted()
    {
        // case300 has 17 buses with a conductive shunt (Gs, 1.3 MW at 1 pu). Their draw is in TotalLossMw (the sum of net
        // injections) but in no branch, so the residual used to be 1.2108721600802141 MW. Now it is ShuntPMw
        // (1.2108721600843708 MW = sum Gs Vm^2) and the residual is -4.156675004196586E-12 MW, rounding.
        GridModel m = IeeeCases.Case300();
        PowerFlowResult r = SolverSelector.CreateNewton(m).Solve(m);
        Assert.True(r.Converged);
        LossAllocationResult a = LossAllocation.Allocate(m, r);
        double gs = 0;
        for (int i = 0; i < m.BusCount; i++) gs += m.Buses[i].Gs * r.Vm[i] * r.Vm[i];
        SameBits(gs, a.ShuntPMw, "shunt draw");
        // Moved 2026-10 (D1, Mark's determinism rule 2026-10-04: identical bits on every CPU tier and OS): deterministic
        // fdlibm sin / cos in the case300 Newton solve. Before -> after: 1.2108721600843708 -> 1.2108721600843737.
        SameBits(1.2108721600843737, a.ShuntPMw, "shunt draw value");
        Assert.InRange(Math.Abs(a.ReconciliationResidualMw), 0, 1e-9);
        SameBits(r.TotalLossMw - a.TotalPLossMw - a.ShuntPMw, a.ReconciliationResidualMw, "residual definition");
    }

    [Fact]
    public void N053_without_conductive_shunts_the_residual_is_unchanged_to_the_bit()
    {
        foreach (GridModel m in new[] { IeeeCases.Case14(), IeeeCases.Case118(), Uk() })
        {
            PowerFlowResult r = SolverSelector.CreateNewton(m).Solve(m);
            LossAllocationResult a = LossAllocation.Allocate(m, r);
            Assert.Equal(0.0, a.ShuntPMw);
            SameBits(r.TotalLossMw - a.TotalPLossMw, a.ReconciliationResidualMw, m.Name ?? "case");
        }
    }
}
