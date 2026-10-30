using GridSim.Core.Abstractions;
using GridSim.Core.IO;
using GridSim.Core.IO.Json;
using GridSim.Core.Model;
using GridSim.Core.Numerics;
using GridSim.Core.PowerFlow;
using Xunit;

namespace GridSim.Tests;

/// <summary>
/// The multi-case sparse LU (<see cref="MultiCaseSparseLu"/>): every lane's L, U and solution must hold exactly the bits
/// the single-case factorisation (<see cref="SparseLuFactorization.Factor"/>) gives for that case alone, whether the lane
/// shared the plan or fell back (a moved pivot, a dropped zero), and a singular lane must be reported as the single path
/// reports it. The same checks run with hardware intrinsics off (the CI leg with DOTNET_EnableHWIntrinsic=0), where
/// the lane-wise operations run element by element.
/// </summary>
public class MultiCaseSparseLuTests
{
    private static void SameBits(ReadOnlySpan<double> a, ReadOnlySpan<double> b, string what)
    {
        Assert.Equal(a.Length, b.Length);
        for (int i = 0; i < a.Length; i++)
            Assert.True(BitConverter.DoubleToInt64Bits(a[i]) == BitConverter.DoubleToInt64Bits(b[i]), $"{what} [{i}]: {a[i]:R} vs {b[i]:R}");
    }

    /// <summary>A deterministic random sparse matrix in CSC form with a strong diagonal.</summary>
    private static (int[] Ap, int[] Ai, double[] Ax) Random(int n, int perCol, ulong seed)
    {
        ulong s = seed;
        double Next() { s ^= s << 13; s ^= s >> 7; s ^= s << 17; return ((s >> 11) * (1.0 / (1UL << 53))) - 0.5; }
        var ap = new int[n + 1];
        var ai = new List<int>();
        var ax = new List<double>();
        for (int c = 0; c < n; c++)
        {
            var rows = new SortedDictionary<int, double> { [c] = 4 + Next() };
            for (int k = 0; k < perCol; k++)
            {
                Next();
                int r = (int)((s >> 33) % (ulong)n);
                if (!rows.ContainsKey(r)) rows[r] = Next();
            }
            foreach (var kv in rows) { ai.Add(kv.Key); ax.Add(kv.Value); }
            ap[c + 1] = ai.Count;
        }
        return (ap, ai.ToArray(), ax.ToArray());
    }

    private static double[] Perturb(double[] ax, ulong seed, double size)
    {
        ulong s = seed;
        return ax.Select(v => { s = s * 6364136223846793005UL + 1442695040888963407UL; return v * (1 + size * (((s >> 11) * (1.0 / (1UL << 53))) - 0.5)); }).ToArray();
    }

    /// <summary>Check every lane of <paramref name="batch"/> against the single-case path for its CSC arrays.</summary>
    private static void EveryLaneMatchesTheSinglePath(MultiCaseSparseLu.Batch batch, int n, int[][] ap, int[][] ai, double[][] ax, int[] q)
    {
        var b = new double[batch.Count][];
        var x = new double[batch.Count][];
        for (int l = 0; l < batch.Count; l++)
        {
            b[l] = Enumerable.Range(0, n).Select(i => Math.Cos(i + l)).ToArray();
            x[l] = new double[n];
        }
        batch.SolveInto(b, x);
        for (int l = 0; l < batch.Count; l++)
        {
            SparseLuFactorization? single = SparseLuFactorization.Factor(n, ap[l], ai[l], ax[l], q);
            SparseLuFactorization? lane = batch.Lane(l);
            Assert.Equal(single is null, lane is null);
            Assert.Equal(single is null, batch.IsSingular(l));
            if (single is null) { Assert.All(x[l], v => Assert.True(double.IsNaN(v))); continue; }
            SameBits(single.LValues, lane!.LValues, $"lane {l} L");
            SameBits(single.UValues, lane.UValues, $"lane {l} U");
            SameBits(single.Solve(b[l]), x[l], $"lane {l} x");
        }
    }

    [Theory]
    [InlineData(40, 3, 3UL, 4)]
    [InlineData(400, 4, 11UL, 4)]
    [InlineData(400, 4, 12UL, 3)]
    [InlineData(400, 4, 13UL, 2)]
    [InlineData(200, 3, 14UL, 1)]
    public void Every_lane_holds_the_bits_of_its_single_case_factorisation(int n, int perCol, ulong seed, int count)
    {
        (int[] ap, int[] ai, double[] ax) = Random(n, perCol, seed);
        int[] q = Enumerable.Range(0, n).ToArray();
        SparseLuPlan plan = SparseLuFactorization.Factor(n, ap, ai, ax, q, keepPlan: true)!.Plan!;
        double[][] axs = Enumerable.Range(0, count).Select(l => Perturb(ax, seed * 31 + (ulong)l, 0.02)).ToArray();
        int[][] aps = Enumerable.Repeat(ap, count).ToArray(), ais = Enumerable.Repeat(ai, count).ToArray();
        MultiCaseSparseLu.Batch batch = MultiCaseSparseLu.Factor(plan, aps, ais, axs);
        Assert.All(batch.SharedLanes, s => Assert.True(s));   // small perturbations keep the pivots: every lane shares the plan
        EveryLaneMatchesTheSinglePath(batch, n, aps, ais, axs, q);
    }

    [Fact]
    public void Lanes_that_cannot_share_the_plan_fall_back_and_still_give_the_single_case_bits()
    {
        // 3 x 3: column 0 = (1, 3, 0.5) pivots on row 1 in the plan.
        int[] ap = { 0, 3, 5, 7 }, ai = { 0, 1, 2, 0, 1, 1, 2 }, q = { 0, 1, 2 };
        double[] ax = { 1, 3, 0.5, 2, 5, 1, 4 };
        SparseLuPlan plan = SparseLuFactorization.Factor(3, ap, ai, ax, q, keepPlan: true)!.Plan!;
        // Lane 0: nudged, shares. Lane 1: column 0 = (3, 1, 0.5): the pivot moves to row 0. Lane 2: singular.
        // Lane 3: the entry (2, 0) is an exact zero, which the CSC form drops: another pattern.
        double[] same = { 1.01, 3, 0.5, 2, 5, 1, 4 }, moved = { 3, 1, 0.5, 2, 5, 1, 4 }, singular = { 1, 3, 0.5, 2, 6, 1e-20, 4 };
        int[] apZ = { 0, 2, 4, 6 }, aiZ = { 0, 1, 0, 1, 1, 2 };
        double[] axZ = { 1, 3, 2, 5, 1, 4 };
        int[][] aps = { ap, ap, ap, apZ }, ais = { ai, ai, ai, aiZ };
        double[][] axs = { same, moved, singular, axZ };
        MultiCaseSparseLu.Batch batch = MultiCaseSparseLu.Factor(plan, aps, ais, axs);
        Assert.Equal(new[] { true, false, false, false }, batch.SharedLanes);
        Assert.True(batch.IsSingular(2));
        EveryLaneMatchesTheSinglePath(batch, 3, aps, ais, axs, q);
    }

    private sealed class Capture : ISparseLinearSolver
    {
        private readonly CpuSparseLuSolver _inner = new();
        public CsrMatrix? Last;
        public string Name => "capture";
        public double[]? Solve(CsrMatrix a, double[] b) { Last = a; return _inner.Solve(a, b); }
    }

    [Fact]
    public void Four_gb_full_jacobians_in_the_lanes_hold_their_single_case_bits()
    {
        GridModel m = JsonGridLoader.LoadDirectory(Path.Combine(CliProcess.Root(), "data", "gb-full"));
        var cap = new Capture();
        Assert.True(new SparseNewtonRaphsonPowerFlow(cap).Solve(m).Converged);
        CsrMatrix j = cap.Last!;
        (int[] ap, int[] ai, double[] ax) = SparseLu.ToCsc(j);
        int[] q = SparseLu.Ordering(j);
        SparseLuPlan plan = SparseLuFactorization.Factor(j.N, ap, ai, ax, q, keepPlan: true)!.Plan!;
        // Four cases: the converged Jacobian with its values scaled by 1 +/- 1e-6 (a Monte Carlo spread on one pattern).
        double[][] axs = Enumerable.Range(0, 4).Select(l => Perturb(ax, 7UL + (ulong)l, 2e-6)).ToArray();
        int[][] aps = Enumerable.Repeat(ap, 4).ToArray(), ais = Enumerable.Repeat(ai, 4).ToArray();
        MultiCaseSparseLu.Batch batch = MultiCaseSparseLu.Factor(plan, aps, ais, axs);
        EveryLaneMatchesTheSinglePath(batch, j.N, aps, ais, axs, q);
    }

    /// <summary>A CSR matrix with <paramref name="m"/>'s structure and values scaled entry by entry (a family member).</summary>
    private static CsrMatrix Member(CsrMatrix m, ulong seed, double size)
    {
        ulong s = seed;
        var b = new CsrMatrix.Builder(m.N);
        for (int i = 0; i < m.N; i++)
            for (int k = m.RowPtr[i]; k < m.RowPtr[i + 1]; k++)
            {
                s = s * 6364136223846793005UL + 1442695040888963407UL;
                b.Add(i, m.ColIdx[k], m.Val[k] * (1 + size * (((s >> 11) * (1.0 / (1UL << 53))) - 0.5)));
            }
        return b.Build();
    }

    /// <summary>Every lane of a family batch against the public single-case path, SparseLu.Factor on the CSR matrix.</summary>
    private static void EveryLaneMatchesSparseLuFactor(MultiCaseSparseLu.Batch batch, CsrMatrix[] members)
    {
        int n = members[0].N;
        var b = members.Select((_, l) => Enumerable.Range(0, n).Select(i => Math.Sin(3 * i + l)).ToArray()).ToArray();
        var x = members.Select(_ => new double[n]).ToArray();
        batch.SolveInto(b, x);
        for (int l = 0; l < members.Length; l++)
        {
            SparseLuFactorization? single = SparseLu.Factor(members[l]);
            Assert.Equal(single is null, batch.IsSingular(l));
            if (single is null) continue;
            SparseLuFactorization lane = batch.Lane(l)!;
            SameBits(single.LValues, lane.LValues, $"lane {l} L");
            SameBits(single.UValues, lane.UValues, $"lane {l} U");
            SameBits(single.Solve(b[l]), x[l], $"lane {l} x");
            SameBits(SparseLu.Solve(members[l], b[l])!, x[l], $"lane {l} x via SparseLu.Solve");
        }
    }

    [Fact]
    public void A_family_batch_gives_each_member_the_bits_of_sparse_lu_factor()
    {
        GridModel m = IeeeCases.Case300();
        var cap = new Capture();
        Assert.True(new SparseNewtonRaphsonPowerFlow(cap).Solve(m).Converged);
        CsrMatrix template = Member(cap.Last!, 1UL, 0.0);
        var family = new SparseLuFamily(template);
        Assert.NotNull(family.Plan);
        CsrMatrix[] members = Enumerable.Range(0, 4).Select(l => Member(template, 100UL + (ulong)l, 1e-3)).ToArray();
        Assert.All(members, a => Assert.True(family.IsMember(a)));
        EveryLaneMatchesSparseLuFactor(family.Factor(members), members);
        EveryLaneMatchesSparseLuFactor(family.Factor(members.Take(3).ToArray()), members.Take(3).ToArray());
    }

    [Fact]
    public void A_matrix_outside_the_family_is_refused_by_name()
    {
        var b1 = new CsrMatrix.Builder(3);
        b1.Add(0, 0, 4); b1.Add(1, 1, 4); b1.Add(2, 2, 4); b1.Add(0, 1, 1); b1.Add(1, 0, 1);
        CsrMatrix a = b1.Build();
        var b2 = new CsrMatrix.Builder(3);
        b2.Add(0, 0, 4); b2.Add(1, 1, 4); b2.Add(2, 2, 4); b2.Add(1, 2, 1); b2.Add(2, 1, 1);
        CsrMatrix stranger = b2.Build();
        var family = new SparseLuFamily(a);
        Assert.False(family.IsMember(stranger));
        var ex = Assert.Throws<ArgumentException>(() => family.Factor(new[] { a, stranger }));
        Assert.Contains("matrix 1", ex.Message, StringComparison.Ordinal);
        Assert.Throws<ArgumentOutOfRangeException>(() => family.Factor(Array.Empty<CsrMatrix>()));
        Assert.Throws<ArgumentOutOfRangeException>(() => family.Factor(new[] { a, a, a, a, a }));
        var nan = new CsrMatrix.Builder(3);
        nan.Add(0, 0, double.NaN); nan.Add(1, 1, 4); nan.Add(2, 2, 4); nan.Add(0, 1, 1); nan.Add(1, 0, 1);
        Assert.Throws<ArgumentException>(() => family.Factor(new[] { nan.Build() }));
    }

    [Fact]
    public void A_singular_template_leaves_every_member_to_the_single_path()
    {
        // Template: a singular 2 x 2 (rows equal); members: one regular, one singular.
        CsrMatrix Make(double a00, double a01, double a10, double a11)
        {
            var b = new CsrMatrix.Builder(2);
            b.Add(0, 0, a00); b.Add(0, 1, a01); b.Add(1, 0, a10); b.Add(1, 1, a11);
            return b.Build();
        }
        var family = new SparseLuFamily(Make(1, 2, 1, 2));
        Assert.Null(family.Plan);
        CsrMatrix[] members = { Make(3, 1, 1, 2), Make(2, 4, 1, 2) };
        MultiCaseSparseLu.Batch batch = family.Factor(members);
        Assert.Equal(new[] { false, false }, batch.SharedLanes);
        Assert.False(batch.IsSingular(0));
        Assert.True(batch.IsSingular(1));
        EveryLaneMatchesSparseLuFactor(batch, members);
    }

    [Fact]
    public void A_family_under_the_diagonal_preference_rule_gives_each_member_the_bits_of_that_rule()
    {
        GridModel m = IeeeCases.Case300();
        var cap = new Capture();
        Assert.True(new SparseNewtonRaphsonPowerFlow(cap).Solve(m).Converged);
        CsrMatrix template = Member(cap.Last!, 1UL, 0.0);
        var family = new SparseLuFamily(template, diagonalPreference: 0.1);
        Assert.Equal(0.1, family.Plan!.DiagonalPreference);
        CsrMatrix[] members = Enumerable.Range(0, 4).Select(l => Member(template, 300UL + (ulong)l, 1e-2)).ToArray();
        MultiCaseSparseLu.Batch batch = family.Factor(members);
        Assert.All(batch.SharedLanes, s => Assert.True(s));   // the rule keeps the pivot order: every lane shares
        int n = template.N;
        var b = members.Select((_, l) => Enumerable.Range(0, n).Select(i => Math.Cos(5 * i - l)).ToArray()).ToArray();
        var x = members.Select(_ => new double[n]).ToArray();
        batch.SolveInto(b, x);
        for (int l = 0; l < members.Length; l++)
        {
            SparseLuFactorization single = SparseLu.Factor(members[l], 0.1)!;
            SameBits(single.LValues, batch.Lane(l)!.LValues, $"lane {l} L");
            SameBits(single.UValues, batch.Lane(l)!.UValues, $"lane {l} U");
            SameBits(single.Solve(b[l]), x[l], $"lane {l} x");
        }
        // FactorAlone gives the same bits without the lanes.
        MultiCaseSparseLu.Batch alone = family.FactorAlone(members);
        Assert.All(alone.SharedLanes, s => Assert.False(s));
        for (int l = 0; l < members.Length; l++)
            SameBits(batch.Lane(l)!.UValues, alone.Lane(l)!.UValues, $"alone lane {l} U");
    }
}
