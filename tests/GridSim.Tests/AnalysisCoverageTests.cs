using GridSim.Core.Analysis;
using GridSim.Core.Dynamics;
using GridSim.Core.IO;
using GridSim.Core.IO.Matpower;
using GridSim.Core.Model;
using GridSim.Core.PowerFlow;
using GridSim.Core.Simulation;
using Xunit;

namespace GridSim.Tests;

/// <summary>
/// Coverage for the analysis layer (N-1 contingency screening, scenario load-scaling and
/// warm-start plumbing, RoCoF / inertia dynamics) plus an independent MATPOWER cross-check that
/// parses the committed .m libraries directly and validates parser+solver against each file's own
/// stored solved operating point.
///
/// Deliberately complementary to <see cref="ChainAndScenarioTests"/> and <see cref="IeeeCasesTests"/>:
/// this file leans on the model-level <see cref="RocofScreening.Screen(GridModel, DynamicsConfig?)"/>
/// overload, order-independent (multiset) contingency comparison, the <see cref="ContingencyResult"/>
/// security invariant over real data, a self-contained radial islanding case, the
/// <see cref="GridScenario.AtLoad"/> demand-scaling path, and reads each parsed model's own
/// Bus.Vm/VaDeg rather than hard-coded published arrays.
/// </summary>
public class AnalysisCoverageTests
{
    private static string LibDir => Path.Combine(AppContext.BaseDirectory, "data", "matpower", "lib");

    // -------------------------------------------------------------------------------------------
    // 1. N-1 contingency screening
    // -------------------------------------------------------------------------------------------

    [Theory]
    [InlineData("14")]
    [InlineData("118")]
    public void Screen_sequential_and_parallel_agree_order_independently(string name)
    {
        GridModel m = name == "14" ? IeeeCases.Case14() : IeeeCases.Case118();

        var seq = ContingencyAnalysis.ScreenBranches(m);
        var par = ContingencyAnalysis.ScreenBranchesParallel(m, () => new NewtonRaphsonPowerFlow());
        Assert.Equal(m.Branches.Count, seq.Count);
        Assert.Equal(seq.Count, par.Count);

        // Compare as multisets: sort both by a stable composite key so the check does not rely on
        // completion order (parallel workers finish out of order even though results are returned in
        // branch order). Duplicate branch labels (e.g. parallel circuits) are disambiguated by the
        // numeric fields.
        ContingencyResult[] a = seq.OrderBy(Key).ToArray();
        ContingencyResult[] b = par.OrderBy(Key).ToArray();
        for (int i = 0; i < a.Length; i++)
        {
            Assert.Equal(a[i].Outage, b[i].Outage);
            Assert.Equal(a[i].Converged, b[i].Converged);
            Assert.Equal(a[i].Secure, b[i].Secure);
            Assert.Equal(a[i].MaxBranchLoadingPercent, b[i].MaxBranchLoadingPercent, 6);
            Assert.Equal(a[i].MinVm, b[i].MinVm, 9);
            Assert.Equal(a[i].MaxVm, b[i].MaxVm, 9);
            Assert.Equal(a[i].Violations.Count, b[i].Violations.Count);
        }

        static string Key(ContingencyResult r) =>
            $"{r.Outage}|{r.Converged}|{r.MaxBranchLoadingPercent:F6}|{r.MinVm:F6}|{r.MaxVm:F6}";
    }

    [Fact]
    public void Every_contingency_result_is_self_consistent()
    {
        var results = ContingencyAnalysis.ScreenBranches(IeeeCases.Case118());
        Assert.NotEmpty(results);
        foreach (ContingencyResult c in results)
        {
            // The Secure invariant: secure iff it converged AND has no violations.
            Assert.Equal(c.Converged && c.Violations.Count == 0, c.Secure);

            if (!c.Converged)
            {
                // Non-converged (islanded) rows carry no measured state, just the marker note.
                Assert.False(c.Secure);
                Assert.NotEmpty(c.Violations);
            }
            else
            {
                // A converged row reports a sane voltage window.
                Assert.True(c.MinVm > 0 && c.MinVm <= c.MaxVm);
                // Any recorded violation must contradict security.
                if (c.Violations.Count > 0) Assert.False(c.Secure);
            }
        }
    }

    [Fact]
    public void Radial_outage_is_reported_nonconverged_not_thrown()
    {
        // A single line feeding one load bus: outaging it islands the load. The screen must report
        // this as non-converged rather than propagating a solver exception.
        var model = new GridModel
        {
            Name = "radial",
            BaseMva = 100,
            Buses = new[]
            {
                new Bus(1, BusType.Slack, BaseKv: 132, Name: "grid"),
                new Bus(2, BusType.Pq, Pd: 50, Qd: 20, BaseKv: 132, Name: "town"),
            },
            Generators = new[] { new Generator(1, Pg: 0, Vg: 1.0, Name: "slack") },
            Branches = new[] { new Branch(1, 2, 0.01, 0.1, 0.02, Name: "feeder") },
        };

        // Sanity: the intact model solves.
        Assert.True(new NewtonRaphsonPowerFlow().Solve(model).Converged);

        IReadOnlyList<ContingencyResult> results = ContingencyAnalysis.ScreenBranches(model); // must not throw
        ContingencyResult only = Assert.Single(results);
        Assert.False(only.Converged, "outaging the only feeder should island the load (non-converged)");
        Assert.False(only.Secure);
        Assert.NotEmpty(only.Violations);
    }

    // -------------------------------------------------------------------------------------------
    // 2. Load scaling (GridScenario.AtLoad)
    // -------------------------------------------------------------------------------------------

    [Fact]
    public void AtLoad_scales_demand_proportionally_and_rebalances_slack()
    {
        GridModel bas = IeeeCases.Case14();
        const double factor = 1.3;
        GridModel scaled = GridScenario.AtLoad(bas, factor);

        // Every bus's Pd/Qd is scaled by exactly the factor.
        var basePd = bas.Buses.ToDictionary(b => b.Id, b => b.Pd);
        var baseQd = bas.Buses.ToDictionary(b => b.Id, b => b.Qd);
        foreach (Bus b in scaled.Buses)
        {
            Assert.Equal(basePd[b.Id] * factor, b.Pd, 9);
            Assert.Equal(baseQd[b.Id] * factor, b.Qd, 9);
        }

        // The slack plant is re-balanced to carry demand (+loss allowance) less the non-slack fleet:
        // its dispatched Pg grows with the higher demand.
        int slack = bas.Buses.First(b => b.Type == BusType.Slack).Id;
        double basePg = bas.Generators.First(g => g.Bus == slack).Pg;
        double scaledPg = scaled.Generators.First(g => g.Bus == slack).Pg;
        double totalScaledDemand = scaled.Buses.Sum(b => b.Pd);
        Assert.True(scaledPg > basePg, $"slack Pg {scaledPg:F1} should exceed base {basePg:F1} under heavier load");
        Assert.True(scaledPg <= totalScaledDemand * 1.05, "slack should not overshoot total demand + loss allowance");
    }

    [Fact]
    public void Scaled_model_solves_and_losses_rise_with_load()
    {
        GridModel bas = IeeeCases.Case14();
        var solver = new NewtonRaphsonPowerFlow();
        var opt = new PowerFlowOptions { EnforceQLimits = false };

        var light = solver.Solve(GridScenario.AtLoad(bas, 1.0), opt);
        var heavy = solver.Solve(GridScenario.AtLoad(bas, 1.3), opt);

        Assert.True(light.Converged && heavy.Converged);
        Assert.True(heavy.TotalLossMw > light.TotalLossMw,
            $"losses should rise with load: {heavy.TotalLossMw:F2} MW vs {light.TotalLossMw:F2} MW");
    }

    // -------------------------------------------------------------------------------------------
    // 3. Warm start (GridScenario.WithSolvedVoltages)
    // -------------------------------------------------------------------------------------------

    [Fact]
    public void Warm_start_converges_in_at_most_one_iteration_beating_the_cold_solve()
    {
        GridModel m = IeeeCases.Case57();
        var solver = new NewtonRaphsonPowerFlow();
        // Q-limits off so we measure the NR core's warm-start behaviour, not the PV->PQ outer passes.
        var opt = new PowerFlowOptions { EnforceQLimits = false };

        var cold = solver.Solve(m, opt);
        Assert.True(cold.Converged);
        Assert.True(cold.Iterations >= 2, "a cold solve of case57 should need several iterations");

        var warm = solver.Solve(GridScenario.WithSolvedVoltages(m, cold), opt);
        Assert.True(warm.Converged);
        Assert.True(warm.Iterations <= 1, $"warm-started re-solve took {warm.Iterations} iterations");
        Assert.True(warm.Iterations < cold.Iterations, "warm start must beat the cold solve");

        // No drift: re-solving the identical operating point reproduces it exactly.
        for (int i = 0; i < cold.Vm.Length; i++)
        {
            Assert.Equal(cold.Vm[i], warm.Vm[i], 8);
            Assert.Equal(cold.VaDeg[i], warm.VaDeg[i], 6);
        }
    }

    // -------------------------------------------------------------------------------------------
    // 4. RoCoF / dynamics
    // -------------------------------------------------------------------------------------------

    [Fact]
    public void Model_rocof_screen_ranks_worst_case_first()
    {
        // The model overload screens every dispatched generator: each loss sheds its Pg and removes
        // its H*MBase spinning energy. Results must come back worst-RoCoF first.
        GridModel m = IeeeCases.Case118();
        IReadOnlyList<RocofScreeningResult> res = RocofScreening.Screen(m);

        int dispatched = m.Generators.Count(g => g.Pg > 0);
        Assert.Equal(dispatched, res.Count);
        Assert.True(res.Count >= 2, "need at least two dispatched units to rank");

        for (int i = 1; i < res.Count; i++)
            Assert.True(Math.Abs(res[i - 1].InitialRocofHzPerS) >= Math.Abs(res[i].InitialRocofHzPerS),
                $"RoCoF ranking not monotone at {i}: {res[i - 1].InitialRocofHzPerS:F4} then {res[i].InitialRocofHzPerS:F4}");

        foreach (RocofScreeningResult r in res)
            Assert.True(r.LostMw > 0, $"{r.Name} should shed a positive infeed");
    }

    [Theory]
    [InlineData(60_000)]
    [InlineData(100_000)]
    [InlineData(220_000)]
    public void Inertia_estimator_round_trips_the_simulator(double inertiaMwS)
    {
        const double lossMw = 1000;
        // Simulate a clean loss, then invert its first (governor/damping-free) RoCoF sample.
        FrequencyResponse resp = FrequencyDynamics.Simulate(
            inertiaMwS, 30_000, new[] { new FrequencyEvent(1.0, -lossMw) });

        double est = InertiaEstimator.EstimateInertiaMwS(resp, lossMw);
        Assert.Equal(inertiaMwS, est, 0);   // exact to the nearest MWs at t=0

        // The forward predictor is the exact inverse of the estimator.
        double predicted = InertiaEstimator.PredictRocof(inertiaMwS, -lossMw);
        Assert.Equal(inertiaMwS, InertiaEstimator.EstimateInertiaMwS(lossMw, predicted), 3);
    }

    // -------------------------------------------------------------------------------------------
    // 5. Published-value cross-check: parse the .m libraries directly and validate parser + solver
    //    against each file's own stored solved operating point (read from the parsed Bus records,
    //    not a hard-coded array). Angles are re-anchored to the slack bus.
    // -------------------------------------------------------------------------------------------

    [Theory]
    [InlineData("case14.m", 0.005, 0.2)]
    [InlineData("case118.m", 0.03, 1.0)]
    public void Parsed_case_solves_to_its_own_stored_operating_point(string file, double vmTol, double vaTol)
    {
        string path = Path.Combine(LibDir, file);
        Assert.True(File.Exists(path), $"bundled file missing: {path}");   // GR-404: the file is committed (BundledDataPresenceTests checks it), so a missing one fails instead of passing silently

        GridModel m = MatpowerParser.ParseFile(path);
        var r = new NewtonRaphsonPowerFlow().Solve(m);
        Assert.True(r.Converged, $"{file} did not converge (mismatch {r.MaxMismatch:E2})");

        // The stored solved point lives in the parsed model's own Bus.Vm / Bus.VaDeg (bus matrix
        // columns 8/9 of the .m file).
        double[] storedVm = m.Buses.Select(b => b.Vm).ToArray();
        double[] storedVa = m.Buses.Select(b => b.VaDeg).ToArray();

        var vm = new Dictionary<int, double>(r.BusIds.Count);
        var va = new Dictionary<int, double>(r.BusIds.Count);
        for (int i = 0; i < r.BusIds.Count; i++) { vm[r.BusIds[i]] = r.Vm[i]; va[r.BusIds[i]] = r.VaDeg[i]; }

        int slackIdx = 0;
        for (int i = 0; i < m.Buses.Count; i++) if (m.Buses[i].Type == BusType.Slack) { slackIdx = i; break; }
        double storedSlack = storedVa[slackIdx];             // slack angle in the file's reference frame
        double solvedSlack = va[m.Buses[slackIdx].Id];       // slack angle in the solved frame

        for (int i = 0; i < m.Buses.Count; i++)
        {
            int id = m.Buses[i].Id;
            Assert.True(Math.Abs(vm[id] - storedVm[i]) < vmTol,
                $"{file} bus {id}: Vm {vm[id]:F4} vs stored {storedVm[i]:F4}");
            // Both sides referenced to the slack bus before comparing.
            double dva = (va[id] - solvedSlack) - (storedVa[i] - storedSlack);
            Assert.True(Math.Abs(dva) < vaTol,
                $"{file} bus {id}: Va {va[id] - solvedSlack:F3} vs stored {storedVa[i] - storedSlack:F3} (deg)");
        }
    }
}
