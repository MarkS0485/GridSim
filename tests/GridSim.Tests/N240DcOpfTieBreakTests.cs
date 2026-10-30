using GridSim.Core.Analysis;
using GridSim.Core.IO.Json;
using GridSim.Core.IO.Matpower;
using GridSim.Core.Model;
using GridSim.Core.Numerics;
using Xunit;
using Xunit.Abstractions;

namespace GridSim.Tests;

/// <summary>
/// N240 (October 2026; DOC-6 NF-1, ruled by main: a fixed, documented tie-break in the DC-OPF simplex so a degenerate
/// optimum does not depend on summation order). pglib case588's security DC-OPF has a whole face of optimal dispatches
/// (every MATPOWER unit costs the same 50 GBP/MWh on the fuel-type merit order), and a last-bit change in the PTDF sums
/// (D-DOC-026, the CSR summation order) moved 44.06 MW between gen 64 and gen 70 at identical cost bits. The DC-OPF now
/// runs the simplex with a lowest-index pricing tie-break (BoundedSimplex.DcOpfPricingTieMargin, relative 1e-10):
/// reduced costs that differ only by round-off count as tied and the lowest column index enters.
///
/// These tests prove:
///   1. every LP whose pivot path meets no near-tie (Diagnostics.TieBreaks = 0) gives the same bits as the strict rule,
///      on every bundled case the fuel-type DC-OPF runs an LP for; where the tie-break did decide a pivot, the optimal
///      cost is the same (to 1e-12 relative) and the move is reported;
///   2. the dispatch no longer depends on the last bits of the PTDF: every entry scaled by a random factor within 1e-13
///      of 1 (the size of a summation-order change) leaves the tie-broken dispatch within 1e-6 MW, where the strict rule
///      moves case588 by up to 251.7 MW and case1354pegase by up to 1,333.3 MW;
///   3. the structured solver and the dense reference still agree bit for bit with the tie-break on (randomised
///      degenerate LPs of the DC-OPF's shape);
///   4. the margin is refused outside [0, 1e-3], and 0 is the old code path for every other BoundedSimplex caller.
/// </summary>
public class N240DcOpfTieBreakTests
{
    private readonly ITestOutputHelper _out;

    public N240DcOpfTieBreakTests(ITestOutputHelper output) => _out = output;

    private static string DataDir() => Path.Combine(PresentationConstantsAntiDriftTests.RepoRoot(), "data");

    private static GridModel Load(string rel)
    {
        string path = Path.Combine(DataDir(), rel.Replace('/', Path.DirectorySeparatorChar));
        if (rel.EndsWith(".m", StringComparison.OrdinalIgnoreCase))
            return MatpowerUnitNormalizer.Normalize(MatpowerParser.ParseFile(path));
        return JsonGridLoader.LoadDirectory(path);
    }

    private static long Bits(double v) => BitConverter.DoubleToInt64Bits(v);

    /// <summary>The fuel-type security LP exactly as DcOptimalPowerFlow builds it (tie-break on).</summary>
    private static DcOptimalPowerFlow.SecurityLp FuelLp(GridModel m)
    {
        double[] cost = m.Generators.Select(gen => DcOptimalPowerFlow.FuelCostPerMwh(gen.Type)).ToArray();
        double[] pmin = m.Generators.Select(gen => gen.Pmin).ToArray();
        double[] pmax = m.Generators.Select(gen => double.IsInfinity(gen.Pmax) ? DcOptimalPowerFlow.UnboundedPmaxMw : gen.Pmax).ToArray();
        return DcOptimalPowerFlow.BuildSecurityLp(m, cost, pmin, pmax);
    }

    /// <summary>The same problem with the margin at 0: the strict rule, the code every solve ran before N240.</summary>
    private static DcOpfSecuritySimplex.Problem Strict(DcOpfSecuritySimplex.Problem p) => p with { PricingTieMargin = 0.0 };

    private static double Cost(DcOpfSecuritySimplex.Problem p, double[] x)
    {
        double s = 0;
        for (int j = 0; j < p.Generators; j++) s += p.C[j] * x[j];
        return s;
    }

    // The bundled cases whose fuel-type DC-OPF runs the security LP on one island (the parity set, plus the two large
    // recorded ones).
    public static IEnumerable<object[]> Cases() => new[]
    {
        "matpower/lib/case9.m", "matpower/lib/case24_ieee_rts.m", "matpower/lib/case30.m", "matpower/lib/case145.m",
        "matpower/rts/pglib_opf_case73_ieee_rts.m", "matpower/nesta/pglib_opf_case3_lmbd.m",
        "matpower/nesta/pglib_opf_case5_pjm.m", "matpower/nesta/pglib_opf_case30_as.m",
        "matpower/nesta/pglib_opf_case162_ieee_dtc.m", "matpower/nesta/pglib_opf_case240_pserc.m",
        "matpower/nesta/pglib_opf_case588_sdet.m", "matpower/dist/case533mt_hi.m", "matpower/benchmarks/cigre_mv.m",
        "intl/ireland-all-island", "matpower/pegase/case1354pegase.m", "gb-synthetic",
    }.Select(c => new object[] { c });

    [Theory]
    [MemberData(nameof(Cases))]
    public void A_pivot_path_with_no_near_tie_is_bit_identical_and_a_tie_broken_one_keeps_the_optimal_cost(string rel)
    {
        GridModel m = Load(rel);
        Assert.Single(DistributionFactors.FindIslands(m));
        DcOpfSecuritySimplex.Problem on = FuelLp(m).Problem;
        Assert.Equal(BoundedSimplex.DcOpfPricingTieMargin, on.PricingTieMargin);

        DcOpfSecuritySimplex.Detailed a = DcOpfSecuritySimplex.SolveDetailed(on);
        DcOpfSecuritySimplex.Detailed b = DcOpfSecuritySimplex.SolveDetailed(Strict(on));
        Assert.Equal(0, b.Diagnostics.TieBreaks);   // the strict rules never count a tie-break
        Assert.Equal(b.Status, a.Status);

        int moved = 0; double maxMove = 0; int maxAt = -1;
        for (int j = 0; j < on.Generators; j++)
        {
            if (Bits(a.X[j]) == Bits(b.X[j])) continue;
            moved++;
            double d = Math.Abs(a.X[j] - b.X[j]);
            if (d > maxMove) { maxMove = d; maxAt = j; }
        }
        double ca = Cost(on, a.X), cb = Cost(on, b.X);
        _out.WriteLine($"{rel}: status {a.Status}, tie-breaks {a.Diagnostics.TieBreaks}, iterations {a.Diagnostics.PhaseOneIterations}+{a.Diagnostics.PhaseTwoIterations} "
            + $"(strict {b.Diagnostics.PhaseOneIterations}+{b.Diagnostics.PhaseTwoIterations}); units moved {moved}, max |dPg| {maxMove:R} MW at gen {maxAt}; "
            + $"cost {ca:R} vs strict {cb:R}");

        if (a.Diagnostics.TieBreaks == 0)
        {
            // Proof by construction: no pivot was decided by a margin, so the path and every number are the strict ones.
            for (int j = 0; j < a.X.Length; j++) Assert.Equal(Bits(b.X[j]), Bits(a.X[j]));
            Assert.Equal(b.Diagnostics.PhaseOneIterations, a.Diagnostics.PhaseOneIterations);
            Assert.Equal(b.Diagnostics.PhaseTwoIterations, a.Diagnostics.PhaseTwoIterations);
        }
        else if (a.Status == LpStatus.Optimal)
        {
            // A tie-broken path ends at an optimum of the same LP: the same optimal cost, to round-off.
            Assert.True(Math.Abs(ca - cb) <= 1e-12 * Math.Max(1.0, Math.Abs(cb)), $"{rel}: cost {ca:R} vs {cb:R}");
        }
    }

    /// <summary>Every PTDF entry scaled by 1 + e, e uniform in [-1e-13, 1e-13] (a fixed seed per trial): the size of a
    /// change in summation order (a few units in the last place of each entry).</summary>
    private static DcOpfSecuritySimplex.Problem Nudged(DcOpfSecuritySimplex.Problem p, int seed)
    {
        var rng = new Random(seed);
        var ptdf = (double[])p.Ptdf.Clone();
        for (int k = 0; k < ptdf.Length; k++) ptdf[k] *= 1 + 1e-13 * (rng.NextDouble() * 2 - 1);
        return p with { Ptdf = ptdf };
    }

    [Theory]
    [InlineData("matpower/nesta/pglib_opf_case588_sdet.m", true)]
    [InlineData("matpower/pegase/case1354pegase.m", true)]
    [InlineData("matpower/nesta/pglib_opf_case162_ieee_dtc.m", false)]
    [InlineData("matpower/nesta/pglib_opf_case240_pserc.m", false)]
    [InlineData("matpower/lib/case145.m", false)]
    [InlineData("matpower/lib/case24_ieee_rts.m", false)]
    public void The_tie_broken_dispatch_does_not_depend_on_the_last_bits_of_the_ptdf(string rel, bool strictIsFragile)
    {
        DcOpfSecuritySimplex.Problem on = FuelLp(Load(rel)).Problem;
        DcOpfSecuritySimplex.Detailed baseOn = DcOpfSecuritySimplex.SolveDetailed(on);
        DcOpfSecuritySimplex.Detailed baseOff = DcOpfSecuritySimplex.SolveDetailed(Strict(on));
        Assert.Equal(LpStatus.Optimal, baseOn.Status);
        double worstOn = 0, worstOff = 0;
        int movedOff = 0;
        for (int seed = 1; seed <= 20; seed++)
        {
            DcOpfSecuritySimplex.Problem nudged = Nudged(on, seed);
            DcOpfSecuritySimplex.Detailed nOn = DcOpfSecuritySimplex.SolveDetailed(nudged);
            DcOpfSecuritySimplex.Detailed nOff = DcOpfSecuritySimplex.SolveDetailed(Strict(nudged));
            Assert.Equal(LpStatus.Optimal, nOn.Status);
            double mOff = 0;
            for (int j = 0; j < on.Generators; j++)
            {
                worstOn = Math.Max(worstOn, Math.Abs(nOn.X[j] - baseOn.X[j]));
                if (nOff.Status == LpStatus.Optimal) mOff = Math.Max(mOff, Math.Abs(nOff.X[j] - baseOff.X[j]));
            }
            if (mOff > 1e-6) movedOff++;
            worstOff = Math.Max(worstOff, mOff);
        }
        _out.WriteLine($"{rel}: largest dispatch move under 1e-13 PTDF changes (20 trials): tie-break on {worstOn:R} MW, strict rule {worstOff:R} MW ({movedOff} of 20 trials moved more than 1e-6 MW)");
        Assert.True(worstOn <= 1e-6, $"{rel}: the tie-broken dispatch moved {worstOn:R} MW under 1e-13 PTDF changes");
        // The defect N240 names, reproduced: under the strict rule the same changes move a degenerate case's dispatch.
        if (strictIsFragile) Assert.True(worstOff > 1.0, $"{rel}: the strict rule was expected to move, moved {worstOff:R} MW");
    }

    /// <summary>The randomised degenerate LPs of the parity tests, solved by both solvers with the tie-break on: still
    /// bit-identical (zero signs aside, as in DcOpfSecuritySimplexParityTests).</summary>
    [Fact]
    public void Structured_and_dense_simplex_agree_bit_for_bit_with_the_tie_break_on()
    {
        var rng = new Random(20261005);
        int ties = 0;
        for (int trial = 0; trial < 600; trial++)
        {
            int g = rng.Next(1, 9), lines = rng.Next(0, 9), u = rng.Next(1, g + 1);
            var genCol = new int[g];
            for (int i = 0; i < g; i++) genCol[i] = i < u ? i : rng.Next(u);
            // Values that are equal only up to round-off (1/3 and 0.1 + 0.2 - 0.3 + 1/3), so near-ties occur.
            double[] palette = { 0.0, 0.25, -0.25, 0.5, 1.0 / 3.0, 0.1 + 0.2 - 0.3 + 1.0 / 3.0, -0.75, 0.6, 0.6000000000000001 };
            var ptdf = new double[lines * u];
            for (int k = 0; k < ptdf.Length; k++) ptdf[k] = palette[rng.Next(palette.Length)];
            int n = g + lines;
            var c = new double[n]; var lo = new double[n]; var hi = new double[n];
            double[] costs = { 10.0, 10.0, 10.000000000000002, 20.0, 45.5 };
            double loSum = 0, hiSum = 0;
            for (int i = 0; i < g; i++)
            {
                c[i] = costs[rng.Next(costs.Length)];
                lo[i] = rng.Next(4) == 0 ? rng.Next(0, 20) : 0.0;
                hi[i] = lo[i] + rng.Next(5, 120);
                loSum += lo[i]; hiSum += hi[i];
            }
            var b = new double[1 + lines];
            b[0] = loSum + (hiSum - loSum) * rng.NextDouble();
            for (int r = 0; r < lines; r++)
            {
                double rate = rng.Next(5, 80);
                hi[g + r] = 2.0 * rate;
                b[1 + r] = rate - rng.Next(-60, 61) * 0.5;
            }
            var p = DcOptimalPowerFlow.WithTieBreak(new DcOpfSecuritySimplex.Problem(g, lines, u, genCol, ptdf, b, c, lo, hi));
            var a = new double[1 + lines, n];
            for (int i = 0; i < g; i++) a[0, i] = 1.0;
            for (int r = 0; r < lines; r++)
            {
                for (int i = 0; i < g; i++) a[1 + r, i] = ptdf[r * u + genCol[i]];
                a[1 + r, g + r] = 1.0;
            }
            (LpStatus ds, double[] dx, double _) = BoundedSimplex.Solve(a, b, c, lo, hi, pricingTieMargin: BoundedSimplex.DcOpfPricingTieMargin);
            (LpStatus ss, double[] sx, DcOpfSecuritySimplex.Diagnostics diag) = DcOpfSecuritySimplex.Solve(p);
            ties += diag.TieBreaks;
            Assert.True(ds == ss, $"trial {trial}: dense {ds}, structured {ss}");
            for (int j = 0; j < dx.Length; j++)
            {
                if (dx[j] == 0.0 && sx[j] == 0.0) continue;
                Assert.True(Bits(dx[j]) == Bits(sx[j]), $"trial {trial} x[{j}]: dense {dx[j]:R}, structured {sx[j]:R}");
            }
        }
        _out.WriteLine($"tie-breaks over 600 randomised LPs: {ties}");
        Assert.True(ties > 0, "the randomised set must exercise the tie-break");
    }

    [Fact]
    public void The_margin_defaults_to_off_and_is_refused_outside_its_range()
    {
        Assert.Equal(1e-10, BoundedSimplex.DcOpfPricingTieMargin);
        var p = new DcOpfSecuritySimplex.Problem(1, 0, 1, new[] { 0 }, Array.Empty<double>(), new[] { 5.0 }, new[] { 1.0 }, new[] { 0.0 }, new[] { 10.0 });
        Assert.Equal(0.0, p.PricingTieMargin);
        Assert.Throws<ArgumentOutOfRangeException>(() => DcOpfSecuritySimplex.Solve(p with { PricingTieMargin = -1e-10 }));
        Assert.Throws<ArgumentOutOfRangeException>(() => DcOpfSecuritySimplex.Solve(p with { PricingTieMargin = double.NaN }));
        var a = new double[,] { { 1.0 } };
        Assert.Throws<ArgumentOutOfRangeException>(() => BoundedSimplex.Solve(a, new[] { 5.0 }, new[] { 1.0 }, new[] { 0.0 }, new[] { 10.0 }, pricingTieMargin: 0.5));
        // Margins on, a one-unit LP: the same answer as the strict rule (no tie to break).
        (LpStatus s, double[] x, _) = DcOpfSecuritySimplex.Solve(DcOptimalPowerFlow.WithTieBreak(p));
        Assert.Equal(LpStatus.Optimal, s);
        Assert.Equal(5.0, x[0]);
    }

    /// <summary>pglib case588 at the DC-OPF level: the dispatch of gen 64 and gen 70 (the pair N240 found) is recorded in
    /// the output, and the dense reference agrees with the structured solve bit for bit with the tie-break on.</summary>
    [Fact]
    public void Case588_dispatch_is_the_dense_references_with_the_tie_break_on()
    {
        GridModel m = Load("matpower/nesta/pglib_opf_case588_sdet.m");
        DcOptimalPowerFlow.DcOpfResult s = DcOptimalPowerFlow.Solve(m, enforceLineLimits: true);
        DcOptimalPowerFlow.DcOpfResult d = DcOptimalPowerFlow.SolveDenseReference(m, enforceLineLimits: true);
        Assert.Equal(LpStatus.Optimal, s.SecurityLpStatus);
        for (int i = 0; i < s.Dispatch.Count; i++) Assert.Equal(Bits(d.Dispatch[i].Pg), Bits(s.Dispatch[i].Pg));
        _out.WriteLine($"case588: gen 64 (bus {s.Dispatch[64].GenBus}) {s.Dispatch[64].Pg:R} MW, gen 70 (bus {s.Dispatch[70].GenBus}) {s.Dispatch[70].Pg:R} MW; "
            + $"total cost {s.TotalCost:R} ({Bits(s.TotalCost):X16}), generation {s.TotalGenMw:R} ({Bits(s.TotalGenMw):X16})");
    }
}
