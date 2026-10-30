using GridSim.Core.Analysis;
using GridSim.Core.IO;
using GridSim.Core.Model;
using GridSim.Core.PowerFlow;
using Xunit;

namespace GridSim.Tests;

/// <summary>
/// S4 solver and data follow-ups (October 2026, agent "S4 solver and data follow-ups"): N111 (acopf failures), N113
/// (intl models), N114 (gb-synthetic outages). Diagnosis tests pin the EVIDENCE of each diagnosis, so a later change
/// that alters the physics or the data shows up here; fix tests pin the corrected behaviour.
/// </summary>
public class S4SolverDataFollowUpsTests
{
    private static string Root() => CliProcess.Root();

    // =============================================================================================================
    // N111: acopf failures are the case's data and physics, and are now reported as such
    // =============================================================================================================
    //
    // Diagnosis (N111). case118 / case300: MATPOWER files carry no fuel
    // types, so the fuel-type DC-OPF prices every unit at the flat 50 GBP/MWh and the (unrated) LP is fully degenerate:
    // it returns one arbitrary vertex, moving 35 of case118's 54 units (25 to Pmax, 28 to Pmin; bus 89 607 -> 0 MW) and
    // 65 of case300's 69 (50 at Pmax). At that dispatch the AC power flow with reactive limits stops (case118 lowest
    // voltage 0.7966 pu, case300 0.7806 pu), while the same dispatch solves with reactive limits off (case118 in 8
    // iterations, losses 725.667 MW against 132.481 at the stated dispatch): the units' reactive limits cannot hold the
    // voltages at that dispatch. With the cases' own cost curves (--costs case) both converge (4 and 6 passes). Not a
    // solver defect; the defect was the message, which blamed the loss loop for an AC failure at pass 0.

    [Fact]
    public void N111_case118_acopf_fails_at_pass_0_on_the_ac_solve_and_says_so()
    {
        GridModel m = IeeeCases.Case118();
        AcOpfResult r = AcOptimalPowerFlow.Solve(m);
        Assert.False(r.Converged);
        Assert.Equal(AcOpfFailure.AcPowerFlowNotConverged, r.Failure);
        Assert.NotNull(r.AcFailure);
        Assert.Equal(0, r.AcFailure!.Pass);
        Assert.Equal(PowerFlowStatus.MaxIterations, r.AcFailure.Status);
        Assert.InRange(r.AcFailure.MinVmPu, 0.79, 0.80);   // 0.7966 pu where it stopped (measured)
        // Every unit priced the same: the degenerate-LP case the CLI explains.
        Assert.All(r.Dispatch, d => Assert.Equal(50.0, d.CostPerMwh));
    }

    [Fact]
    public void N111_case118_the_failing_dispatch_solves_without_reactive_limits_so_the_limit_is_physical()
    {
        GridModel m = IeeeCases.Case118();
        DcOptimalPowerFlow.DcOpfResult dc = DcOptimalPowerFlow.Solve(m);
        GridModel at = m.CopyWith(generators: m.Generators.Select((g, i) => g with { Pg = dc.Dispatch[i].Pg }).ToList());
        var solver = SolverSelector.CreateNewton(at);
        PowerFlowResult qOn = solver.Solve(at);
        PowerFlowResult qOff = solver.Solve(at, new PowerFlowOptions { EnforceQLimits = false });
        Assert.False(qOn.Converged);
        Assert.True(qOff.Converged);
        Assert.Equal(8, qOff.Iterations);
        Assert.InRange(qOff.TotalLossMw, 725.66, 725.67);   // against 132.481 MW at the stated dispatch
    }

    [Fact]
    public void N111_case118_and_case300_converge_on_their_own_cost_curves()
    {
        AcOpfResult r118 = AcOptimalPowerFlow.Solve(IeeeCases.Case118(), useGeneratorCosts: true);
        Assert.True(r118.Converged);
        Assert.Equal(AcOpfFailure.None, r118.Failure);
        Assert.Equal(4, r118.Iterations);
        Assert.InRange(r118.TotalLossMw, 132.5035, 132.5045);
        AcOpfResult r300 = AcOptimalPowerFlow.Solve(IeeeCases.Case300(), useGeneratorCosts: true);
        Assert.True(r300.Converged);
        Assert.Equal(6, r300.Iterations);
        Assert.InRange(r300.TotalLossMw, 413.0145, 413.0155);
    }

    [Fact]
    public void N111_a_converged_run_reports_no_failure_and_keeps_its_numbers()
    {
        // case14 (the S4 N070 pins: loss 15.85798410498625 MW, cost 13742.899205249312, 2 iterations): unchanged.
        AcOpfResult r = AcOptimalPowerFlow.Solve(IeeeCases.Case14());
        Assert.True(r.Converged);
        Assert.Equal(AcOpfFailure.None, r.Failure);
        Assert.Null(r.AcFailure);
        Assert.Equal(2, r.Iterations);
        Assert.Equal(BitConverter.DoubleToInt64Bits(15.85798410498625), BitConverter.DoubleToInt64Bits(r.TotalLossMw));
        Assert.Equal(BitConverter.DoubleToInt64Bits(13742.899205249312), BitConverter.DoubleToInt64Bits(r.TotalCostPerHour));
    }

    [Fact]
    public void N111_the_iteration_cap_is_reported_as_the_cap()
    {
        AcOpfResult r = AcOptimalPowerFlow.Solve(IeeeCases.Case24Rts(), maxIterations: 2);   // converges in 5
        Assert.False(r.Converged);
        Assert.Equal(AcOpfFailure.IterationCap, r.Failure);
        Assert.Equal(2, r.Iterations);
    }

    [Fact]
    public void N111_gb_synthetic_settles_on_an_infeasible_dc_dispatch()
    {
        // Moved 2026-10 (N109, Ruling R1, Mark delegated 2026-10-04): the security DC-OPF of gb-synthetic was Infeasible
        // only because of 53 rated lines no dispatch can relieve; they are now left out and named. Before: Converged
        // false, Failure DcDispatchInfeasible, no AC failure. After: Converged true, Failure None, no AC failure, and the
        // final (loss-inflated) pass's 86 left-out lines in UnrelievableLines (53 at the unscaled demand).
        // Moved 2026-10 (N204, Ruling R1 follow-up, Mark delegated 2026-10-04): the builder now sizes the radial ladder
        // to its peak demand. Before -> after: final-pass left-out lines 86 -> 5 (2 at the unscaled demand, was 53);
        // Converged true, Failure None, no AC failure unchanged.
        AcOpfResult r = AcOptimalPowerFlow.Solve(GridSim.Core.Synthetic.SyntheticGb.Build(new()));
        Assert.True(r.Converged);
        Assert.Equal(AcOpfFailure.None, r.Failure);
        Assert.Null(r.AcFailure);
        Assert.Equal(5, r.UnrelievableLines.Count);
    }

    [Fact]
    public void N111_cli_acopf_names_the_failed_ac_solve_and_the_flat_price()
    {
        CliProcess.Result r = CliProcess.RunBuilt("acopf", "case118");
        Assert.Equal(3, r.ExitCode);
        Assert.Contains("the AC power flow at the DC-OPF dispatch of loss pass 0", r.StdErr, StringComparison.Ordinal);
        Assert.Contains("every unit is priced the same (50 GBP/MWh", r.StdErr, StringComparison.Ordinal);
        Assert.DoesNotContain("the loss iteration did not converge", r.StdErr, StringComparison.Ordinal);
    }

    // =============================================================================================================
    // N113: iberia, france and continental-europe have no AC solution as shipped: DATA (data/intl/README.md)
    // =============================================================================================================
    //
    // Every unit but the island slacks is a fixed (pq) injection, so one bus per island holds voltage against the whole
    // country's line charging; the per-km impedances are ordinary overhead-line values; france and continental-europe
    // also place generation far from their synthetic demand. Ireland (60 buses) solves.

    private static GridModel Intl(string name) => GridSim.Core.IO.Json.JsonGridLoader.LoadDirectory(Path.Combine(Root(), "data", "intl", name));

    [Theory]
    [InlineData("iberia", 225, 227, 22811)]
    [InlineData("france", 293, 295, 34473)]
    [LargeCaseInlineData(CaseSizes.ContinentalEurope, Engine.Sparse, "continental-europe", 1604, 1608, 140717)]
    public void N113_the_three_maps_do_not_solve_and_only_their_slacks_hold_voltage(string name, int pqUnits, int units, int chargingMvar)
    {
        GridModel m = Intl(name);
        Assert.Equal(units, m.Generators.Count);
        // Only slack buses: no bus is PV, because every non-slack unit is a fixed injection.
        Assert.Equal(0, m.Buses.Count(b => b.Type == BusType.Pv));
        Assert.Equal(units - pqUnits, m.Buses.Count(b => b.Type == BusType.Slack));
        Assert.Equal(chargingMvar, (int)Math.Round(m.Branches.Sum(b => b.B) * m.BaseMva));
        var solver = SolverSelector.CreateNewton(m);
        Assert.False(solver.Solve(m).Converged);
        Assert.False(solver.Solve(m, new PowerFlowOptions { EnforceQLimits = false, MaxIterations = 100 }).Converged);
    }

    [Fact]
    public void N113_ireland_solves()
    {
        GridModel m = Intl("ireland-all-island");
        PowerFlowResult r = SolverSelector.CreateNewton(m).Solve(m);
        Assert.True(r.Converged);
        Assert.Equal(5, r.Iterations);
        Assert.InRange(r.Vm.Max(), 1.1257, 1.1259);   // 1.1258 pu at bus 9: line charging, with one bus holding voltage
    }

    [Fact]
    public void N113_iberia_fails_even_with_no_load_but_solves_without_charging_or_with_every_unit_holding_voltage()
    {
        GridModel m = Intl("iberia");
        var opts = new PowerFlowOptions { EnforceQLimits = false, MaxIterations = 100 };
        GridModel noLoad = ContinuationPowerFlow.ScaleLoads(m, 0).CopyWith(generators: m.Generators.Select(g => g with { Pg = 0 }).ToList());
        var solver = SolverSelector.CreateNewton(m);
        Assert.False(solver.Solve(noLoad, opts).Converged);
        Assert.True(solver.Solve(noLoad.CopyWith(branches: noLoad.Branches.Select(b => b with { B = 0 }).ToList()), opts).Converged);

        // Every generator bus made PV (reactive limits off): the stored dispatch then solves, in 4 iterations.
        var genBuses = m.Generators.Select(g => g.Bus).ToHashSet();
        GridModel pv = m.CopyWith(buses: m.Buses.Select(b => b.Type == BusType.Pq && genBuses.Contains(b.Id) ? b with { Type = BusType.Pv } : b).ToList());
        PowerFlowResult r = solver.Solve(pv, opts);
        Assert.True(r.Converged);
        Assert.Equal(4, r.Iterations);
    }

    [Fact]
    public void N113_the_cli_notes_the_unsolvable_maps_at_load()
    {
        CliProcess.Result r = CliProcess.RunBuilt("pf", Path.Combine(Root(), "data", "intl", "iberia"));
        Assert.Equal(3, r.ExitCode);
        Assert.Contains("note: data/intl/iberia: this indicative map has no AC power-flow solution as shipped", r.StdErr, StringComparison.Ordinal);
        // The map that solves gets no note.
        CliProcess.Result ie = CliProcess.RunBuilt("pf", Path.Combine(Root(), "data", "intl", "ireland-all-island"));
        Assert.Equal(0, ie.ExitCode);
        Assert.DoesNotContain("no AC power-flow solution", ie.StdErr, StringComparison.Ordinal);
    }

    // =============================================================================================================
    // N114: gb-synthetic outages that "do not solve" island the network: its structure is radial below the GSPs
    // =============================================================================================================
    //
    // 2,169 of gb-synthetic's 2,268 branches are bridges (each outage splits the network): every 33/11 kV and 11/6.6 kV
    // secondary transformer, every 132/33 kV primary transformer and most GSP transformers feed their load alone.
    // The every-40th-branch sample (57 outages) under the study solver: 2 solve, 55 island, 0 fail to converge on a
    // connected network. Not a solver defect; SyntheticGb builds single-transformer radial feeds.

    [Fact]
    public void N114_gb_synthetic_is_radial_below_the_gsps()
    {
        GridModel m = GridSim.Core.Synthetic.SyntheticGb.Build(new());
        Assert.Equal(2268, m.Branches.Count);
        int bridges = Enumerable.Range(0, m.Branches.Count).Count(k => SplitsWithout(m, k));
        Assert.Equal(2169, bridges);
    }

    [Fact]
    public void N114_the_sampled_outages_island_rather_than_fail_to_converge()
    {
        GridModel m = GridSim.Core.Synthetic.SyntheticGb.Build(new());

        // P0 (October 2026, suite runtime): the same 57 outages (every 40th branch), screened by the same study solver
        // with the same options and classified by the same assertions, but solved in parallel, one study solver per
        // worker thread (a solver instance is single-threaded, as ContingencyAnalysis documents). This test was the
        // longest single item in the suite (about 3 minutes, almost all of it BiCGSTAB running to its cap on the
        // islanded outages' singular Jacobians) and, because the tests of one class run one after another, it alone
        // set the end of the whole run. What it proves is unchanged: each outage's result does not depend on which
        // solver instance screened it or in what order (the solver's only cross-call state is CpuSparseLuSolver's
        // cached ordering, a pure function of the Jacobian's pattern; the parallel contingency screens rely on the
        // same fact and are pinned equal to the serial ones), and the tally below is taken in branch order after
        // every outage has been screened.
        var outages = new List<int>();
        for (int k = 0; k < m.Branches.Count; k += 40) outages.Add(k);
        var results = new ContingencyResult[outages.Count];
        Parallel.For(0, outages.Count,
            () => ContingencyAnalysis.CreateStudySolver(m),
            (i, _, solver) =>
            {
                results[i] = ContingencyAnalysis.ScreenOutage(m, new[] { outages[i] }, solver, ContingencyAnalysis.StudyOptions);
                return solver;
            },
            _ => { });

        int solved = 0, islanded = 0, other = 0;
        for (int i = 0; i < outages.Count; i++)
        {
            int k = outages[i];
            ContingencyResult r = results[i];
            if (r.Converged) solved++;
            else if (r.Islanded) { islanded++; Assert.True(SplitsWithout(m, k), $"branch {k} labelled islanded but does not split"); }
            else other++;
        }
        Assert.Equal((2, 55, 0), (solved, islanded, other));
    }

    /// <summary>True when removing branch <paramref name="k"/> leaves the in-service network in more than one piece.</summary>
    private static bool SplitsWithout(GridModel m, int k)
    {
        var index = m.Buses.Select((b, i) => (b.Id, i)).ToDictionary(t => t.Id, t => t.i);
        var adj = Enumerable.Range(0, m.Buses.Count).Select(_ => new List<int>()).ToArray();
        for (int j = 0; j < m.Branches.Count; j++)
        {
            Branch br = m.Branches[j];
            if (j == k || !br.InService) continue;
            adj[index[br.From]].Add(index[br.To]);
            adj[index[br.To]].Add(index[br.From]);
        }
        var seen = new bool[m.Buses.Count];
        var stack = new Stack<int>();
        stack.Push(0); seen[0] = true; int count = 1;
        while (stack.Count > 0)
            foreach (int w in adj[stack.Pop()])
                if (!seen[w]) { seen[w] = true; count++; stack.Push(w); }
        return count < m.Buses.Count;
    }

    // =============================================================================================================
    // N127 / N128: the GB reference models' losses and GBnetwork.m's stored voltages (data/matpower/gb/README.md)
    // =============================================================================================================

    [Fact]
    public void N127_gb_full_has_zero_resistance_transformers()
    {
        // 1,229 of gb-full's 2,620 transformers have R = 0 exactly (GBnetwork.m: 29 of 1,314), one of the two data
        // differences behind its lower losses (0.98 % against 2.04 % of demand).
        GridModel m = GridSim.Core.IO.Json.JsonGridLoader.LoadDirectory(Path.Combine(Root(), "data", "gb-full"));
        List<Branch> tx = m.Branches.Where(b => b.IsTransformer).ToList();
        Assert.Equal(2620, tx.Count);
        Assert.Equal(1229, tx.Count(b => b.R == 0));
    }

    [Fact]
    public void N128_gbnetwork_stored_voltages_leave_the_recorded_mismatch_and_rounding_explains_its_size()
    {
        GridModel m = GridSim.Core.IO.Matpower.MatpowerUnitNormalizer.Normalize(
            GridSim.Core.IO.Matpower.MatpowerParser.ParseFile(Path.Combine(Root(), "data", "matpower", "gb", "GBnetwork.m")));
        SparseYBus y = YBus.BuildSparse(m);

        // The stored state: largest P mismatch over non-slack buses, largest Q mismatch over PQ buses.
        (double p, int pBus, double q, int qBus) stored = Mismatch(m, y, m.Buses.Select(b => b.Vm).ToArray(), m.Buses.Select(b => b.VaDeg).ToArray());
        Assert.InRange(stored.p, 28.2835130954, 28.2835130955);
        Assert.Equal(1676, stored.pBus);
        // Moved 2026-10 (C7, as above): with DetMath the stored-state Q mismatch is 210.67221853205206 MVAr on every OS (the
        // Windows CRT value sat inside the old range, the glibc / fdlibm value 5e-11 above it). Before -> after:
        // InRange(210.672218531, 210.672218532) -> exactly 210.67221853205206. Nothing on the data or Y-bus path changed.
        Assert.Equal(210.67221853205206, stored.q);
        Assert.Equal(1082, stored.qBus);

        // GridSim's own exact solution of the file (reactive limits off), printed to the file's 4 decimals, leaves a
        // mismatch of the same size: the rounding on x = 0.0001 pu branches, not inconsistent data.
        PowerFlowResult sol = SolverSelector.CreateNewton(m).Solve(m, new PowerFlowOptions { EnforceQLimits = false, MaxIterations = 100 });
        Assert.True(sol.Converged);
        (double p, int pBus, double q, int qBus) rounded = Mismatch(m, y,
            sol.Vm.Select(v => Math.Round(v, 4, MidpointRounding.AwayFromZero)).ToArray(),
            sol.VaDeg.Select(v => Math.Round(v, 4, MidpointRounding.AwayFromZero)).ToArray());
        Assert.InRange(rounded.p, 28.0, 30.0);   // 28.952260202150594 MW measured
        Assert.InRange(rounded.q, 209.0, 211.0); // 210.07180761275723 MVAr measured
        Assert.Equal(1082, rounded.qBus);
    }

    /// <summary>The largest |P| mismatch over non-slack buses and |Q| mismatch over PQ buses at a given state (MW, MVAr),
    /// with the generators' stated Pg / Qg and the bus demand.</summary>
    private static (double P, int PBus, double Q, int QBus) Mismatch(GridModel m, SparseYBus y, double[] vm, double[] vaDeg)
    {
        int n = m.Buses.Count;
        var v = new System.Numerics.Complex[n];
        // Moved 2026-10 (C7, final batch; N128 / N175, Ruling R1 and Mark's go on the final-batch moves 2026-10-10): the
        // helper turned polar voltages into rectangular ones with System.Numerics.Complex.FromPolarCoordinates, which calls
        // the platform's cos / sin (Windows CRT vs glibc), so its pin held on one OS only (N175 in test code). It now uses
        // GridSim's deterministic fdlibm (DetMath), the same bits on every OS. Before -> after: FromPolarCoordinates(vm, a)
        // -> new Complex(vm DetMath.Cos(a), vm DetMath.Sin(a)).
        for (int i = 0; i < n; i++)
        {
            double a = vaDeg[i] * Math.PI / 180;
            v[i] = new System.Numerics.Complex(vm[i] * GridSim.Core.Numerics.DetMath.Cos(a), vm[i] * GridSim.Core.Numerics.DetMath.Sin(a));
        }
        var cur = new System.Numerics.Complex[n];
        foreach (KeyValuePair<(int Row, int Col), System.Numerics.Complex> c in y.Cells) cur[c.Key.Row] += c.Value * v[c.Key.Col];
        var pg = new double[n];
        var qg = new double[n];
        foreach (Generator g in m.Generators) { int k = y.IndexOf(g.Bus); pg[k] += g.Pg; qg[k] += g.Qg; }
        double mp = 0, mq = 0; int bp = -1, bq = -1;
        for (int i = 0; i < n; i++)
        {
            Bus b = m.Buses[i];
            System.Numerics.Complex s = v[i] * System.Numerics.Complex.Conjugate(cur[i]) * m.BaseMva;
            double dp = Math.Abs(s.Real - (pg[i] - b.Pd)), dq = Math.Abs(s.Imaginary - (qg[i] - b.Qd));
            if (b.Type != BusType.Slack && dp > mp) { mp = dp; bp = b.Id; }
            if (b.Type == BusType.Pq && dq > mq) { mq = dq; bq = b.Id; }
        }
        return (mp, bp, mq, bq);
    }

    [Fact]
    public void N111_cli_dcopf_tells_line_limit_infeasibility_from_short_capability()
    {
        // Moved 2026-10 (N109, Ruling R1, Mark delegated 2026-10-04): gb-synthetic is no longer line-limit infeasible (its
        // 53 unrelievable lines are left out and named). Before: exit 1 with the "security LP Infeasible" line. After:
        // exit 0, the 53 lines named and the "secure for every rated line except" summary. The "security LP Infeasible"
        // message stays pinned on a case that is jointly infeasible (N109UnrelievableLinesTests, case9 links at 50 MVA).
        // Moved 2026-10 (N204, Ruling R1 follow-up, Mark delegated 2026-10-04): the builder now sizes the radial ladder to
        // its peak demand, leaving only the two meshed supergrid transformers. Before -> after: "53 rated line(s) cannot be
        // brought within rating" -> "2 rated line(s) ..."; "except the 53 listed above" -> "except the 2 listed above";
        // exit 0 unchanged.
        CliProcess.Result r = CliProcess.RunBuilt("dcopf", "gb-synthetic");
        // (N172, Ruling R1, also moved this line on main: the infeasible exit 1 -> 3. With N109 gb-synthetic is no longer
        // infeasible, so the exit is 0; the jointly infeasible case and its exit code are pinned in N109UnrelievableLinesTests.)
        Assert.Equal(0, r.ExitCode);
        Assert.Contains("2 rated line(s) cannot be brought within rating by any dispatch; left out of the security LP and still overloaded:", r.StdOut, StringComparison.Ordinal);
        Assert.Contains("secure for every rated line except the 2 listed above", r.StdOut, StringComparison.Ordinal);
        Assert.DoesNotContain("generation capability cannot meet demand", r.StdOut, StringComparison.Ordinal);
    }
}
