using GridSim.Core.Numerics;
using Xunit;

namespace GridSim.Tests;

/// <summary>
/// The mixed-integer branch-and-bound (<see cref="MilpBranchAndBound"/>): small MILPs with known optima (a knapsack, an
/// uncapacitated facility location, a three-unit four-period unit commitment), agreement with brute-force enumeration
/// (every whole-valued assignment, the continuous part solved by the dense <see cref="BoundedSimplex"/>) on generated
/// problems, infeasible problems at the root and below it, the node cap reported with its incumbent and bound, whole
/// integer columns in the answer, bit-for-bit repeatability, and malformed problems refused.
/// </summary>
public class MilpBranchAndBoundTests
{
    private const double Inf = double.PositiveInfinity;

    private static long Bits(double v) => BitConverter.DoubleToInt64Bits(v);

    /// <summary>The continuous part of an answer comes from an interior point solve to a relative tolerance of 1e-9, so an
    /// objective with continuous columns is checked to that relative accuracy (whole-valued answers are exact).</summary>
    private static void Close(double expected, double actual) =>
        Assert.True(Math.Abs(expected - actual) <= 1e-9 * (1 + Math.Abs(expected)), $"expected {expected:R}, got {actual:R}");

    /// <summary>A builder for equality-form MILPs: columns with bounds, cost and an integer flag, rows as sparse entries.</summary>
    private sealed class Builder
    {
        public readonly List<(int, int, double)> A = new();
        public readonly List<double> B = new(), C = new(), Lo = new(), Hi = new();
        public readonly List<bool> Int = new();

        public int Col(double cost, double lo, double hi, bool integer)
        {
            C.Add(cost); Lo.Add(lo); Hi.Add(hi); Int.Add(integer);
            return C.Count - 1;
        }

        public void Row(double rhs, params (int Col, double Val)[] entries)
        {
            int r = B.Count;
            B.Add(rhs);
            foreach ((int c, double v) in entries) A.Add((r, c, v));
        }

        /// <summary>sum entries &lt;= rhs, with a new non-negative slack column.</summary>
        public void Le(double rhs, params (int Col, double Val)[] entries)
        {
            int s = Col(0, 0, Inf, false);
            Row(rhs, entries.Append((s, 1.0)).ToArray());
        }

        public MilpProblem Build() => new(
            new SparseLpProblem(B.Count, C.Count, A, B.ToArray(), C.ToArray(), Lo.ToArray(), Hi.ToArray()), Int.ToArray());
    }

    [Fact]
    public void The_textbook_knapsack_takes_15()
    {
        // The well-known five-item knapsack (weights 12, 2, 1, 1, 4 kg; values 4, 2, 1, 2, 10; capacity 15 kg): the
        // optimum leaves out the 12 kg item and takes the other four, value 15 at 8 kg.
        double[] w = { 12, 2, 1, 1, 4 }, v = { 4, 2, 1, 2, 10 };
        var b = new Builder();
        var x = new int[5];
        for (int i = 0; i < 5; i++) x[i] = b.Col(-v[i], 0, 1, true);
        b.Le(15, x.Select((c, i) => (c, w[i])).ToArray());
        MilpResult r = MilpBranchAndBound.Solve(b.Build());
        Assert.True(r.Optimal, r.Message);
        Assert.Equal(-15.0, r.Objective);
        Assert.Equal(new double[] { 0, 1, 1, 1, 1 }, r.X![..5]);
    }

    [Fact]
    public void The_facility_location_opens_only_the_second_site_for_58()
    {
        // Uncapacitated facility location: fixed costs 30, 25, 40; assignment costs below; every customer served once,
        // only from an open site. Enumerating the seven non-empty site sets gives 58 (site 1 alone) as the optimum.
        double[] f = { 30, 25, 40 };
        double[,] cost = { { 5, 12, 9, 14 }, { 10, 4, 11, 8 }, { 7, 9, 3, 6 } };
        var b = new Builder();
        var y = new int[3];
        for (int i = 0; i < 3; i++) y[i] = b.Col(f[i], 0, 1, true);
        var x = new int[3, 4];
        for (int i = 0; i < 3; i++)
            for (int k = 0; k < 4; k++)
                x[i, k] = b.Col(cost[i, k], 0, 1, false);
        for (int k = 0; k < 4; k++) b.Row(1, (x[0, k], 1.0), (x[1, k], 1.0), (x[2, k], 1.0));
        for (int i = 0; i < 3; i++)
            for (int k = 0; k < 4; k++)
                b.Le(0, (x[i, k], 1.0), (y[i], -1.0));
        MilpResult r = MilpBranchAndBound.Solve(b.Build());
        Assert.True(r.Optimal, r.Message);
        Close(58.0, r.Objective);
        Assert.Equal(new double[] { 0, 1, 0 }, r.X![..3]);
    }

    [Fact]
    public void A_three_unit_commitment_matches_enumeration_at_18320()
    {
        // Three units, four periods (demand 150, 300, 450, 250 MW), all off before the first period.
        //   unit  Pmin Pmax  cost/MWh  no-load/period  start-up
        //   A      50  200     10          100            500
        //   B      40  150     20           50            200
        //   C      20  100     35           20             50
        // Enumerating all 4,096 on/off schedules (each dispatched by merit order above the minimums) gives 18,320:
        // A on throughout, B on from period 2, C on in period 3 only.
        double[] demand = { 150, 300, 450, 250 };
        (double Pmin, double Pmax, double Mc, double Nl, double Su)[] units =
            { (50, 200, 10, 100, 500), (40, 150, 20, 50, 200), (20, 100, 35, 20, 50) };
        var b = new Builder();
        var u = new int[3, 4];
        var p = new int[3, 4];
        var v = new int[3, 4];
        for (int g = 0; g < 3; g++)
            for (int t = 0; t < 4; t++)
            {
                u[g, t] = b.Col(units[g].Nl, 0, 1, true);
                p[g, t] = b.Col(units[g].Mc, 0, Inf, false);
                v[g, t] = b.Col(units[g].Su, 0, 1, true);
            }
        for (int t = 0; t < 4; t++) b.Row(demand[t], (p[0, t], 1.0), (p[1, t], 1.0), (p[2, t], 1.0));
        for (int g = 0; g < 3; g++)
            for (int t = 0; t < 4; t++)
            {
                b.Le(0, (p[g, t], 1.0), (u[g, t], -units[g].Pmax));
                b.Le(0, (p[g, t], -1.0), (u[g, t], units[g].Pmin));
                if (t == 0) b.Le(0, (u[g, t], 1.0), (v[g, t], -1.0));
                else b.Le(0, (u[g, t], 1.0), (u[g, t - 1], -1.0), (v[g, t], -1.0));
            }
        MilpResult r = MilpBranchAndBound.Solve(b.Build());
        Assert.True(r.Optimal, r.Message);
        Close(18320.0, r.Objective);
        double[] expectOn = { 1, 1, 1, 1, 0, 1, 1, 1, 0, 0, 1, 0 };
        for (int g = 0; g < 3; g++)
            for (int t = 0; t < 4; t++)
                Assert.Equal(expectOn[g * 4 + t], r.X![u[g, t]]);
    }

    [Fact]
    public void Generated_milps_agree_with_brute_force_enumeration()
    {
        // 40 generated problems: four integer columns in 0..3, two continuous columns in 0..5, three "<=" rows with
        // random coefficients (some problems infeasible). The reference enumerates all 256 whole assignments and solves
        // the continuous part of each by the dense bounded simplex; both must agree on feasibility and the optimum.
        var rng = new Random(20261025);
        int feasible = 0, infeasible = 0;
        for (int trial = 0; trial < 40; trial++)
        {
            const int ni = 4, nc = 2, m = 3;
            var a = new double[m, ni + nc];
            var rhs = new double[m];
            var cost = new double[ni + nc];
            for (int j = 0; j < ni + nc; j++) cost[j] = rng.Next(-9, 10) + (j >= ni ? 0.5 : 0);
            for (int i = 0; i < m; i++)
            {
                for (int j = 0; j < ni + nc; j++) a[i, j] = rng.Next(-4, 7);
                rhs[i] = rng.Next(-3, 15);
            }

            var b = new Builder();
            for (int j = 0; j < ni; j++) b.Col(cost[j], 0, 3, true);
            for (int j = 0; j < nc; j++) b.Col(cost[ni + j], 0, 5, false);
            for (int i = 0; i < m; i++) b.Le(rhs[i], Enumerable.Range(0, ni + nc).Select(j => (j, a[i, j])).ToArray());
            MilpResult r = MilpBranchAndBound.Solve(b.Build());

            double best = Inf;
            for (int code = 0; code < 256; code++)
            {
                var z = new double[ni];
                for (int j = 0; j < ni; j++) z[j] = (code >> (2 * j)) & 3;
                // Continuous part: rows sum_c a x_c + s = rhs - sum_i a z, x_c in [0, 5], s >= 0.
                var ad = new double[m, nc + m];
                var bd = new double[m];
                for (int i = 0; i < m; i++)
                {
                    double fixedPart = 0;
                    for (int j = 0; j < ni; j++) fixedPart += a[i, j] * z[j];
                    bd[i] = rhs[i] - fixedPart;
                    for (int j = 0; j < nc; j++) ad[i, j] = a[i, ni + j];
                    ad[i, nc + i] = 1;
                }
                var cd = new double[nc + m];
                for (int j = 0; j < nc; j++) cd[j] = cost[ni + j];
                var lod = new double[nc + m];
                var hid = new double[nc + m];
                for (int j = 0; j < nc; j++) hid[j] = 5;
                for (int i = 0; i < m; i++) hid[nc + i] = Inf;
                (LpStatus st, double[] _, double obj) = BoundedSimplex.Solve(ad, bd, cd, lod, hid);
                if (st != LpStatus.Optimal) continue;
                double total = obj;
                for (int j = 0; j < ni; j++) total += cost[j] * z[j];
                best = Math.Min(best, total);
            }

            if (double.IsPositiveInfinity(best))
            {
                infeasible++;
                Assert.True(r.Status == MilpStatus.Infeasible, $"trial {trial}: {r.Status} {r.Message}");
            }
            else
            {
                feasible++;
                Assert.True(r.Optimal, $"trial {trial}: {r.Message}");
                Assert.True(Math.Abs(r.Objective - best) <= 1e-6 * (1 + Math.Abs(best)), $"trial {trial}: {r.Objective} vs brute force {best}");
                for (int j = 0; j < ni; j++) Assert.Equal(Math.Round(r.X![j]), r.X![j]);
            }
        }
        Assert.True(feasible >= 20, $"only {feasible} feasible trials: the generator should exercise the search");
        Assert.True(infeasible >= 1, "the generator should include at least one infeasible problem");
    }

    [Fact]
    public void A_problem_with_no_whole_point_below_a_feasible_root_is_infeasible()
    {
        // 2 x = 1 with x whole in 0..5: the relaxation (x = 0.5) is feasible, both children are not.
        var b = new Builder();
        int x = b.Col(1, 0, 5, true);
        b.Row(1, (x, 2.0));
        MilpResult r = MilpBranchAndBound.Solve(b.Build());
        Assert.Equal(MilpStatus.Infeasible, r.Status);
        Assert.Null(r.X);
        Assert.Equal(3, r.Nodes);
    }

    [Fact]
    public void An_infeasible_root_is_reported_as_such()
    {
        var b = new Builder();
        int x = b.Col(1, 0, 1, true), y = b.Col(1, 0, 1, true);
        b.Row(5, (x, 1.0), (y, 1.0));
        MilpResult r = MilpBranchAndBound.Solve(b.Build());
        // x + y = 5 with both in 0..1: the phase-one LP's least violation is 3, so the root is proven infeasible.
        Assert.Equal(MilpStatus.Infeasible, r.Status);
        Assert.Contains("root relaxation is infeasible", r.Message);
        Assert.Equal(1, r.Nodes);
    }

    [Fact]
    public void Crossing_rounded_bounds_are_infeasible_without_a_node()
    {
        var b = new Builder();
        int x = b.Col(1, 0.2, 0.8, true);
        b.Row(0.5, (x, 1.0));
        MilpResult r = MilpBranchAndBound.Solve(b.Build());
        Assert.Equal(MilpStatus.Infeasible, r.Status);
        Assert.Equal(0, r.Nodes);
        Assert.Contains("no whole value", r.Message);
    }

    [Fact]
    public void The_node_cap_stops_loudly_with_the_incumbent_and_bound()
    {
        // A 12-item knapsack whose root relaxation is fractional; capped at one node.
        var b = new Builder();
        var cols = new List<(int, double)>();
        for (int i = 0; i < 12; i++) cols.Add((b.Col(-(7 + (i * 5) % 11), 0, 1, true), 3 + (i * 7) % 9));
        b.Le(20, cols.ToArray());
        MilpResult r = MilpBranchAndBound.Solve(b.Build(), new MilpOptions { NodeCap = 1 });
        Assert.Equal(MilpStatus.NodeCap, r.Status);
        Assert.Equal(1, r.Nodes);
        Assert.Contains("node cap 1 reached", r.Message);
        Assert.True(double.IsFinite(r.Bound));
        MilpResult full = MilpBranchAndBound.Solve(b.Build());
        Assert.True(full.Optimal, full.Message);
        Assert.True(full.Nodes > 1, "the full search must need more than the root");
        Assert.True(r.Bound <= full.Objective + 1e-6, $"the capped bound {r.Bound} must not exceed the optimum {full.Objective}");
    }

    [Fact]
    public void Best_bound_and_depth_first_reach_the_same_optimum()
    {
        double[] w = { 12, 2, 1, 1, 4, 6, 7, 3 }, v = { 4, 2, 1, 2, 10, 7, 8, 3 };
        var b = new Builder();
        var cols = new (int, double)[w.Length];
        for (int i = 0; i < w.Length; i++) cols[i] = (b.Col(-v[i], 0, 1, true), w[i]);
        b.Le(17, cols);
        MilpResult bb = MilpBranchAndBound.Solve(b.Build());
        MilpResult df = MilpBranchAndBound.Solve(b.Build(), new MilpOptions { Order = MilpNodeOrder.DepthFirst });
        Assert.True(bb.Optimal, bb.Message);
        Assert.True(df.Optimal, df.Message);
        Assert.Equal(bb.Objective, df.Objective);
    }

    [Fact]
    public void The_search_is_bit_identical_from_run_to_run()
    {
        var b = new Builder();
        var cols = new List<(int, double)>();
        for (int i = 0; i < 10; i++) cols.Add((b.Col(-(5 + (i * 3) % 7) + 0.25 * i, 0, 2, true), 2 + (i * 5) % 6));
        int c = b.Col(0.75, 0, 4, false);
        cols.Add((c, -1.0));
        b.Le(13, cols.ToArray());
        MilpResult r1 = MilpBranchAndBound.Solve(b.Build());
        MilpResult r2 = MilpBranchAndBound.Solve(b.Build());
        Assert.True(r1.Optimal, r1.Message);
        Assert.Equal(r1.Nodes, r2.Nodes);
        Assert.Equal(r1.LpIterations, r2.LpIterations);
        Assert.Equal(Bits(r1.Objective), Bits(r2.Objective));
        Assert.Equal(Bits(r1.Bound), Bits(r2.Bound));
        Assert.Equal(r1.X!.Select(Bits), r2.X!.Select(Bits));
    }

    [Fact]
    public void Malformed_problems_and_settings_are_refused()
    {
        var b = new Builder();
        int x = b.Col(1, 0, 1, true);
        b.Row(1, (x, 1.0));
        MilpProblem good = b.Build();
        Assert.Throws<ArgumentException>(() => MilpBranchAndBound.Solve(good with { Integer = new bool[2] }));
        Assert.Throws<ArgumentOutOfRangeException>(() => MilpBranchAndBound.Solve(good, new MilpOptions { NodeCap = 0 }));
        Assert.Throws<ArgumentOutOfRangeException>(() => MilpBranchAndBound.Solve(good, new MilpOptions { IntegralityTolerance = 0.5 }));
        Assert.Throws<ArgumentOutOfRangeException>(() => MilpBranchAndBound.Solve(good, new MilpOptions { RelativeGap = double.NaN }));

    }
}
