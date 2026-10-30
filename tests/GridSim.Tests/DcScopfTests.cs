using GridSim.Core.Analysis;
using GridSim.Core.Model;
using GridSim.Core.Numerics;
using GridSim.Core.PowerFlow;
using Xunit;

namespace GridSim.Tests;

/// <summary>
/// Preventive DC SCOPF (<see cref="DcScopf"/>): a three-bus case against hand calculation (dispatch, cost, prices, the
/// binding post-outage limit, RateC), an independent formulation (one angle vector per outage state, no LODF, no PTDF,
/// solved by the interior point method) on a meshed six-bus case and on case30, the plain DC-OPF when nothing is secured,
/// every secured post-outage flow re-checked on the network with the branch removed, constraint generation against the full
/// LP, bit-for-bit repeatability, infeasibility reported, and the refusals.
/// </summary>
public class DcScopfTests
{
    private static long Bits(double v) => BitConverter.DoubleToInt64Bits(v);

    private static void Close(double expected, double actual, double rel = 1e-9) =>
        Assert.True(Math.Abs(expected - actual) <= rel * (1 + Math.Abs(expected)), $"expected {expected:R}, got {actual:R}");

    /// <summary>Triangle 1-2-3, X = 0.1 pu each; unit A at bus 1 (10/MWh), unit B at bus 2 (30/MWh), 150 MW at bus 3.
    /// Line 1-2 rated 120 MW (RateC 135), the others 200 MW.</summary>
    private static GridModel Triangle(double rate12 = 120, double rateC12 = 135) => new()
    {
        Name = "scopf-triangle",
        BaseMva = 100,
        Buses = new[] { new Bus(1, BusType.Slack), new Bus(2, BusType.Pv), new Bus(3, BusType.Pq, Pd: 150) },
        Generators = new[]
        {
            new Generator(1, 0, Pmin: 0, Pmax: 300, Name: "A"),
            new Generator(2, 0, Pmin: 0, Pmax: 300, Name: "B"),
        },
        Branches = new[]
        {
            new Branch(1, 2, 0, 0.1, RateAMva: rate12, Name: "L12", RateCMva: rateC12),
            new Branch(1, 3, 0, 0.1, RateAMva: 200, Name: "L13"),
            new Branch(2, 3, 0, 0.1, RateAMva: 200, Name: "L23"),
        },
    };

    private static readonly double[] TriangleCost = { 10, 30 };

    // Hand calculation. With A = x and B = 150 - x, the base flows are f12 = (2x - 150)/3, f13 = (x + 150)/3,
    // f23 = (300 - x)/3. Outage of L13: everything from bus 1 goes 1-2-3, so f12' = x (<= 120 binds: x <= 120).
    // Outage of L23: B's output goes 2-1-3, f12' = x - 150 (>= -120: x >= 30), f13' = 150. Outage of L12: f13' = x,
    // f23' = 150 - x. Cheapest secure dispatch: x = 120, B = 30, cost 1200 + 900 = 2100 (unsecured: x = 150, 1500).
    // Prices: B is marginal at buses 2 and 3 (30); at bus 1 an extra MW of demand is met by A without loading L12 after
    // the outage of L13 (f12' is bus 1's net injection), so 10.
    [Fact]
    public void Three_bus_case_matches_hand_calculation()
    {
        ScopfResult r = DcScopf.Solve(Triangle(), new ScopfOptions { CostPerGen = TriangleCost });
        Assert.True(r.Optimal, r.Message);
        Close(120, r.Dispatch[0].Pmw);
        Close(30, r.Dispatch[1].Pmw);
        Close(2100, r.TotalCost);
        Assert.Equal(new[] { 0, 1, 2 }, r.Contingencies);
        Assert.Empty(r.IslandingOutages);
        Close(10, r.Prices[0].Lmp);
        Close(30, r.Prices[1].Lmp);
        Close(30, r.Prices[2].Lmp);
        Close(30, r.BaseFlowsMw[0]);    // (240 - 150) / 3
        Close(90, r.BaseFlowsMw[1]);    // (120 + 150) / 3
        Close(60, r.BaseFlowsMw[2]);    // (300 - 120) / 3
        ScopfBinding b = Assert.Single(r.Bindings);
        Assert.Equal(0, b.Branch);
        Assert.Equal(1, b.Contingency);
        Close(120, b.FlowMw);
        Assert.Equal(120, b.RateMw);
    }

    [Fact]
    public void Emergency_rating_after_an_outage_is_used_when_asked()
    {
        // RateC 135 on L12: x <= 135, B = 15, cost 1350 + 450 = 1800.
        ScopfResult r = DcScopf.Solve(Triangle(), new ScopfOptions { CostPerGen = TriangleCost, PostContingencyRating = PostFaultRating.RateC });
        Assert.True(r.Optimal, r.Message);
        Close(135, r.Dispatch[0].Pmw);
        Close(1800, r.TotalCost);
        // RateB is not given on any branch, so it falls back to RateA: the RateA answer.
        ScopfResult rb = DcScopf.Solve(Triangle(), new ScopfOptions { CostPerGen = TriangleCost, PostContingencyRating = PostFaultRating.RateB });
        Close(2100, rb.TotalCost);
    }

    [Fact]
    public void Nothing_secured_is_the_dc_opf()
    {
        // No outage listed: the base-case limits alone, which the DC-OPF solves by its own (PTDF) LP.
        foreach (GridModel m in new[] { Triangle(), Triangle(rate12: 20) })
        {
            ScopfResult r = DcScopf.Solve(m, new ScopfOptions { CostPerGen = TriangleCost, Contingencies = Array.Empty<int>() });
            DcOptimalPowerFlow.DcOpfResult opf = DcOptimalPowerFlow.Solve(m, TriangleCost);
            Assert.True(r.Optimal, r.Message);
            Assert.True(opf.Feasible);
            Close(opf.TotalCost, r.TotalCost);
        }
        // Unsecured triangle: all from A (150 MW, cost 1500).
        Close(1500, DcScopf.Solve(Triangle(), new ScopfOptions { CostPerGen = TriangleCost, Contingencies = Array.Empty<int>() }).TotalCost);
    }

    [Fact]
    public void Infeasible_security_is_reported_not_dispatched()
    {
        // L12 rated 10 MW (no RateC): the outage of L13 needs x <= 10, the outage of L23 needs x >= 140.
        ScopfResult r = DcScopf.Solve(Triangle(rate12: 10, rateC12: 0), new ScopfOptions { CostPerGen = TriangleCost });
        Assert.Equal(ScopfStatus.Infeasible, r.Status);
        Assert.False(r.Optimal);
        Assert.Empty(r.Dispatch);
        Assert.True(double.IsNaN(r.TotalCost));
        // The full LP says the same.
        ScopfResult full = DcScopf.Solve(Triangle(rate12: 10, rateC12: 0), new ScopfOptions { CostPerGen = TriangleCost, ConstraintGeneration = false });
        Assert.Equal(ScopfStatus.Infeasible, full.Status);
    }

    /// <summary>The Wood and Wollenberg six-bus network (reactances of their example 6A), units at buses 1-3, 70 MW at
    /// each of buses 4-6, every line rated 70 MW (RateC 80 on all but L56).</summary>
    private static GridModel SixBus(double rate = 70, double rateC = 80)
    {
        (int F, int T, double X)[] lines =
        {
            (1, 2, 0.2), (1, 4, 0.2), (1, 5, 0.3), (2, 3, 0.25), (2, 4, 0.1), (2, 5, 0.3),
            (2, 6, 0.2), (3, 5, 0.26), (3, 6, 0.1), (4, 5, 0.4), (5, 6, 0.3),
        };
        return new GridModel
        {
            Name = "scopf-six-bus",
            BaseMva = 100,
            Buses = new[]
            {
                new Bus(1, BusType.Slack), new Bus(2, BusType.Pv), new Bus(3, BusType.Pv),
                new Bus(4, BusType.Pq, Pd: 70), new Bus(5, BusType.Pq, Pd: 70), new Bus(6, BusType.Pq, Pd: 70),
            },
            Generators = new[]
            {
                new Generator(1, 0, Pmin: 20, Pmax: 200, Name: "G1"),
                new Generator(2, 0, Pmin: 20, Pmax: 150, Name: "G2"),
                new Generator(3, 0, Pmin: 20, Pmax: 180, Name: "G3"),
            },
            Branches = lines.Select((l, i) => new Branch(l.F, l.T, 0, l.X, RateAMva: rate, Name: $"L{l.F}{l.T}",
                RateCMva: i == 10 ? 0 : rateC)).ToArray(),
        };
    }

    private static readonly double[] SixCost = { 11, 13, 17 };

    [Theory]
    [InlineData(PostFaultRating.RateA)]
    [InlineData(PostFaultRating.RateC)]
    public void Six_bus_case_matches_an_independent_formulation(PostFaultRating rating)
    {
        GridModel m = SixBus();
        ScopfResult r = DcScopf.Solve(m, new ScopfOptions { CostPerGen = SixCost, PostContingencyRating = rating });
        Assert.True(r.Optimal, r.Message);
        Assert.Contains(r.Bindings, b => b.Contingency >= 0);   // a post-outage limit binds: the test is not vacuous
        double unsecured = DcOptimalPowerFlow.Solve(m, SixCost).TotalCost;
        Assert.True(r.TotalCost > unsecured + 1, $"secured {r.TotalCost} vs unsecured {unsecured}");

        double independent = IndependentOptimum(m, SixCost, r.Contingencies, rating);
        Close(independent, r.TotalCost, 1e-8);
        CheckPostOutageFlowsByRemoval(m, r, rating);
    }

    [Fact]
    public void Case30_matches_an_independent_formulation_and_the_full_lp()
    {
        GridModel m = S10TestSupport.LoadMatpower("lib/case30.m").Model;
        // Distinct linear costs (2 + index per MWh) so the optimum is not a tie; post-outage ratings at RateA.
        double[] cost = Enumerable.Range(0, m.Generators.Count).Select(g => 2.0 + g).ToArray();
        ScopfResult r = DcScopf.Solve(m, new ScopfOptions { CostPerGen = cost });
        Assert.True(r.Optimal, r.Message);
        Assert.NotEmpty(r.IslandingOutages);   // case30 has radial branches; they are listed, not secured
        foreach (int k in r.IslandingOutages) Assert.DoesNotContain(k, r.Contingencies);
        Assert.Contains(r.Bindings, b => b.Contingency >= 0);

        ScopfResult full = DcScopf.Solve(m, new ScopfOptions { CostPerGen = cost, ConstraintGeneration = false });
        Assert.True(full.Optimal, full.Message);
        Close(full.TotalCost, r.TotalCost);
        Assert.True(r.PostContingencyRows < full.PostContingencyRows, $"{r.PostContingencyRows} vs {full.PostContingencyRows}");

        Close(IndependentOptimum(m, cost, r.Contingencies, PostFaultRating.RateA), r.TotalCost, 1e-8);
        CheckPostOutageFlowsByRemoval(m, r, PostFaultRating.RateA);
    }

    [Fact]
    public void Six_bus_emergency_rating_secures_more_cheaply()
    {
        ScopfResult a = DcScopf.Solve(SixBus(), new ScopfOptions { CostPerGen = SixCost });
        ScopfResult c = DcScopf.Solve(SixBus(), new ScopfOptions { CostPerGen = SixCost, PostContingencyRating = PostFaultRating.RateC });
        Assert.True(a.Optimal && c.Optimal);
        Assert.True(c.TotalCost < a.TotalCost - 1, $"RateC {c.TotalCost} vs RateA {a.TotalCost}");
        Assert.All(c.Bindings.Where(b => b.Contingency >= 0), b => Assert.Equal(80, b.RateMw));
    }

    [Fact]
    public void Repeat_solves_are_bit_identical()
    {
        GridModel m = SixBus();
        ScopfResult a = DcScopf.Solve(m, new ScopfOptions { CostPerGen = SixCost });
        ScopfResult b = DcScopf.Solve(m, new ScopfOptions { CostPerGen = SixCost });
        Assert.Equal(Bits(a.TotalCost), Bits(b.TotalCost));
        Assert.Equal(a.Dispatch.Select(d => Bits(d.Pmw)), b.Dispatch.Select(d => Bits(d.Pmw)));
        Assert.Equal(a.Prices.Select(p => Bits(p.Lmp)), b.Prices.Select(p => Bits(p.Lmp)));
        Assert.Equal(a.BaseFlowsMw.Select(Bits), b.BaseFlowsMw.Select(Bits));
        Assert.Equal(a.Rounds, b.Rounds);
    }

    [Fact]
    public void Islanding_outages_are_listed_by_default_and_refused_when_named()
    {
        GridModel t = Triangle();
        GridModel m = t.CopyWith(
            buses: t.Buses.Append(new Bus(4, BusType.Pq, Pd: 10)).ToArray(),
            branches: t.Branches.Append(new Branch(3, 4, 0, 0.1, RateAMva: 50, Name: "L34")).ToArray());
        ScopfResult r = DcScopf.Solve(m, new ScopfOptions { CostPerGen = TriangleCost });
        Assert.True(r.Optimal, r.Message);
        Assert.Equal(new[] { 3 }, r.IslandingOutages);
        Assert.Equal(new[] { 0, 1, 2 }, r.Contingencies);
        var ex = Assert.Throws<ArgumentException>(() => DcScopf.Solve(m, new ScopfOptions { CostPerGen = TriangleCost, Contingencies = new[] { 3 } }));
        Assert.Contains("islands the network", ex.Message);
    }

    [Fact]
    public void Bad_input_is_refused()
    {
        GridModel m = Triangle();
        Assert.Throws<ArgumentException>(() => DcScopf.Solve(m, new ScopfOptions { CostPerGen = new double[] { 1 } }));
        Assert.Throws<ArgumentException>(() => DcScopf.Solve(m, new ScopfOptions { CostPerGen = new[] { 1, double.NaN } }));
        Assert.Throws<ArgumentException>(() => DcScopf.Solve(m, new ScopfOptions { CostPerGen = TriangleCost, Contingencies = new[] { 7 } }));
        Assert.Throws<ArgumentException>(() => DcScopf.Solve(m, new ScopfOptions { CostPerGen = TriangleCost, Contingencies = new[] { 1, 1 } }));
        Assert.Throws<ArgumentOutOfRangeException>(() => DcScopf.Solve(m, new ScopfOptions { CostPerGen = TriangleCost, MaxRounds = 0 }));
        GridModel open = m.CopyWith(branches: m.Branches.Select((b, i) => i == 1 ? b with { InService = false } : b).ToArray());
        Assert.Throws<ArgumentException>(() => DcScopf.Solve(open, new ScopfOptions { CostPerGen = TriangleCost, Contingencies = new[] { 1 } }));
        GridModel split = m.CopyWith(buses: m.Buses.Append(new Bus(9, BusType.Pq)).ToArray());
        Assert.Contains("DC islands", Assert.Throws<ArgumentException>(() => DcScopf.Solve(split, new ScopfOptions { CostPerGen = TriangleCost })).Message);
    }

    [Fact]
    public void Round_limit_is_reported_not_an_optimum()
    {
        // The triangle needs a second solve (the first, unsecured, breaks L12 after the outage of L13).
        ScopfResult r = DcScopf.Solve(Triangle(), new ScopfOptions { CostPerGen = TriangleCost, MaxRounds = 1 });
        Assert.Equal(ScopfStatus.RoundLimit, r.Status);
        Assert.Empty(r.Dispatch);
    }

    // ---- The independent reference ------------------------------------------------------------------------------

    /// <summary>
    /// The preventive SCOPF written without distribution factors: one bus-angle vector and one flow per branch for the base
    /// state and for every outage state (the outaged branch removed from that state's network), all sharing the dispatch;
    /// base flows within RateA, outage-state flows within the post-outage rating. Solved by the interior point method (not
    /// the simplex the SCOPF uses). Returns the optimal cost.
    /// </summary>
    private static double IndependentOptimum(GridModel m, double[] cost, IReadOnlyList<int> outages, PostFaultRating rating)
    {
        int nBus = m.Buses.Count, nBr = m.Branches.Count, G = m.Generators.Count;
        var idx = new Dictionary<int, int>();
        for (int i = 0; i < nBus; i++) idx[m.Buses[i].Id] = i;
        int slack = m.Buses.ToList().FindIndex(b => b.Type == BusType.Slack);
        var a = new List<(int, int, double)>();
        var c = new List<double>(); var lo = new List<double>(); var hi = new List<double>(); var rhs = new List<double>();
        int Col(double cc, double l, double h) { c.Add(cc); lo.Add(l); hi.Add(h); return c.Count - 1; }
        for (int g = 0; g < G; g++) Col(cost[g], m.Generators[g].Pmin, m.Generators[g].Pmax);

        var states = new List<int> { -1 };
        states.AddRange(outages);
        foreach (int k in states)
        {
            int theta0 = c.Count;
            for (int i = 0; i < nBus; i++) Col(0, i == slack ? 0 : double.NegativeInfinity, i == slack ? 0 : double.PositiveInfinity);
            int bal0 = rhs.Count;
            for (int i = 0; i < nBus; i++) rhs.Add(m.Buses[i].Pd);
            for (int g = 0; g < G; g++) a.Add((bal0 + idx[m.Generators[g].Bus], g, 1.0));
            for (int l = 0; l < nBr; l++)
            {
                Branch br = m.Branches[l];
                if (!br.InService || l == k) continue;
                double rate = k < 0 ? br.RateAMva : DcScopf.PostRating(br, rating);
                int f = rate > 0 ? Col(0, -rate, rate) : Col(0, double.NegativeInfinity, double.PositiveInfinity);
                // Balance: flow leaves its from bus and arrives at its to bus. Flow: f = base (theta_from - theta_to) / X.
                a.Add((bal0 + idx[br.From], f, -1.0));
                a.Add((bal0 + idx[br.To], f, 1.0));
                int row = rhs.Count;
                rhs.Add(0);
                a.Add((row, f, 1.0));
                a.Add((row, theta0 + idx[br.From], -m.BaseMva / br.X));
                a.Add((row, theta0 + idx[br.To], m.BaseMva / br.X));
            }
        }
        var lp = new SparseLpProblem(rhs.Count, c.Count, a, rhs.ToArray(), c.ToArray(), lo.ToArray(), hi.ToArray());
        SparseLpResult r = SparseInteriorPointLp.Solve(lp, 1e-11, 500);
        Assert.True(r.Optimal, r.Message);
        return r.Objective;
    }

    /// <summary>For every secured outage, remove the branch, solve the DC flows of the secured dispatch on what is left (a
    /// fresh factorisation, no LODF) and check every rated branch against its post-outage rating.</summary>
    private static void CheckPostOutageFlowsByRemoval(GridModel m, ScopfResult r, PostFaultRating rating)
    {
        var inj = new Dictionary<int, double>();
        foreach (Bus b in m.Buses) inj[b.Id] = inj.GetValueOrDefault(b.Id) - b.Pd;
        foreach (ScopfDispatch d in r.Dispatch) inj[d.Bus] = inj.GetValueOrDefault(d.Bus) + d.Pmw;
        double[] baseFlow = DistributionFactors.Build(m).DcFlows(inj, m.BaseMva);
        for (int l = 0; l < m.Branches.Count; l++)
        {
            Close(baseFlow[l], r.BaseFlowsMw[l], 1e-8);
            if (m.Branches[l].RateAMva > 0) Assert.True(Math.Abs(baseFlow[l]) <= m.Branches[l].RateAMva + 1e-6, $"base flow on {l}");
        }
        foreach (int k in r.Contingencies)
        {
            GridModel cut = m.CopyWith(branches: m.Branches.Select((b, i) => i == k ? b with { InService = false } : b).ToArray());
            double[] flow = DistributionFactors.Build(cut).DcFlows(inj, m.BaseMva);
            for (int l = 0; l < m.Branches.Count; l++)
            {
                if (l == k || !(m.Branches[l].RateAMva > 0)) continue;
                double limit = DcScopf.PostRating(m.Branches[l], rating);
                Assert.True(Math.Abs(flow[l]) <= limit + 1e-5, $"outage {k}: branch {l} carries {flow[l]:R} MW against {limit}");
            }
        }
    }
}
