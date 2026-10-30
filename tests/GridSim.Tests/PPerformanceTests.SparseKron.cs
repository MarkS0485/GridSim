using System.Numerics;
using GridSim.Core.Dynamics.Distributed;
using GridSim.Core.Dynamics.Models;
using GridSim.Core.IO;
using GridSim.Core.Model;
using GridSim.Core.PowerFlow;
using Xunit;

namespace GridSim.Tests;

public partial class PPerformanceTests
{
    // =================================================================================================================
    // SparseKronReduction (R105, N290, R007): the Kron reduction on the nonzero pattern gives the dense reduction's bits.
    // =================================================================================================================

    /// <summary>The dense array a set of cells stands for: every position without a cell is the default (+0, +0).</summary>
    private static Complex[,] DenseOf(int n, IReadOnlyDictionary<(int Row, int Col), Complex> cells)
    {
        var y = new Complex[n, n];
        foreach (KeyValuePair<(int Row, int Col), Complex> c in cells) y[c.Key.Row, c.Key.Col] = c.Value;
        return y;
    }

    /// <summary>Every position whose bits are not (+0, +0) as a cell, plus (when asked) some explicit (+0, +0) cells.</summary>
    private static Dictionary<(int Row, int Col), Complex> CellsOf(Complex[,] y, Random? explicitZeros = null)
    {
        int n = y.GetLength(0);
        var cells = new Dictionary<(int Row, int Col), Complex>();
        for (int i = 0; i < n; i++)
            for (int j = 0; j < n; j++)
            {
                Complex v = y[i, j];
                bool positiveZero = BitConverter.DoubleToInt64Bits(v.Real) == 0L && BitConverter.DoubleToInt64Bits(v.Imaginary) == 0L;
                if (!positiveZero || (explicitZeros is not null && explicitZeros.Next(8) == 0)) cells[(i, j)] = v;
            }
        return cells;
    }

    /// <summary>Bit for bit, with the NaN rule of KronReductionBitIdentityTests: a NaN result must be NaN in both (its
    /// payload bits are set by the instruction sequence and are not a number to compare).</summary>
    private static void AssertSameMatrix(Complex[,] expected, Complex[,] actual, string what)
    {
        Assert.Equal(expected.GetLength(0), actual.GetLength(0));
        Assert.Equal(expected.GetLength(1), actual.GetLength(1));
        for (int i = 0; i < expected.GetLength(0); i++)
            for (int j = 0; j < expected.GetLength(1); j++)
            {
                AssertSameDouble(expected[i, j].Real, actual[i, j].Real, $"{what} [{i},{j}] real");
                AssertSameDouble(expected[i, j].Imaginary, actual[i, j].Imaginary, $"{what} [{i},{j}] imag");
            }
    }

    private static void AssertSameDouble(double e, double a, string what)
    {
        if (double.IsNaN(e)) Assert.True(double.IsNaN(a), $"{what}: expected NaN, got {a:R}");
        else Assert.True(SameBits(e, a), $"{what}: {e:R} ({BitConverter.DoubleToInt64Bits(e):X16}) vs {a:R} ({BitConverter.DoubleToInt64Bits(a):X16})");
    }

    /// <summary>Both reductions on the same cells: the same matrix, or the same exception type and message.</summary>
    private static void AssertSparseMatchesDense(int n, IReadOnlyDictionary<(int Row, int Col), Complex> cells, IReadOnlyList<int> keep, string what)
    {
        Complex[,]? expected = null, actual = null;
        Exception? expectedError = null, actualError = null;
        try { expected = KronReduction.Reduce(DenseOf(n, cells), keep); } catch (Exception ex) { expectedError = ex; }
        try { actual = SparseKronReduction.Reduce(n, cells, keep); } catch (Exception ex) { actualError = ex; }
        Assert.True((expectedError is null) == (actualError is null),
            $"{what}: dense {(expectedError is null ? "succeeded" : "threw " + expectedError.Message)}, sparse {(actualError is null ? "succeeded" : "threw " + actualError.Message)}");
        if (expectedError is not null)
        {
            Assert.Equal(expectedError.GetType(), actualError!.GetType());
            Assert.Equal(expectedError.Message, actualError.Message);
            return;
        }
        AssertSameMatrix(expected!, actual!, what);
    }

    [Theory]
    [InlineData("14")]
    [InlineData("30")]
    [InlineData("57")]
    [InlineData("118")]
    [InlineData("300")]
    public void SparseKron_network_admittance_reduced_to_generator_buses_is_bit_identical(string name)
    {
        GridModel m = name switch
        {
            "14" => IeeeCases.Case14(), "30" => IeeeCases.Case30(), "57" => IeeeCases.Case57(),
            "118" => IeeeCases.Case118(), _ => IeeeCases.Case300(),
        };
        SparseYBus sy = YBus.BuildSparse(m);
        YBus yb = YBus.Build(m);
        int[] keep = m.Generators.Select(g => yb.IndexOf(g.Bus)).Distinct().ToArray();
        // The production cells (YBus.BuildSparse) against the production dense array (YBus.Build).
        AssertSameMatrix(KronReduction.Reduce(yb.Y, keep), SparseKronReduction.Reduce(sy.N, sy.Cells, keep), name);
        int[] reversed = keep.Reverse().ToArray();
        AssertSameMatrix(KronReduction.Reduce(yb.Y, reversed), SparseKronReduction.Reduce(sy.N, sy.Cells, reversed), name + " reversed");
        // Keep a scattered half of all buses too (a different elimination set, more fill among kept nodes).
        int[] half = Enumerable.Range(0, yb.N).Where(i => i % 2 == 1).ToArray();
        AssertSameMatrix(KronReduction.Reduce(yb.Y, half), SparseKronReduction.Reduce(sy.N, sy.Cells, half), name + " half");
    }

    [Fact]
    public void SparseKron_random_sparse_matrices_with_signed_zeros_and_one_sided_cells_are_bit_identical()
    {
        // Sparse patterns that are NOT structurally symmetric (a cell without its mirror), cells holding every signed
        // complex zero, explicit (+0, +0) cells, entries with one zero component, lossless (purely imaginary) rows, and
        // kept nodes in no particular order. Every one must give the dense reduction's bits.
        var rng = new Random(2610);
        for (int trial = 0; trial < 400; trial++)
        {
            int n = 2 + rng.Next(40);
            double density = 0.03 + 0.25 * rng.NextDouble();
            bool lossless = trial % 5 == 0;
            var y = new Complex[n, n];
            for (int i = 0; i < n; i++)
            {
                for (int j = 0; j < n; j++)
                {
                    if (i == j || rng.NextDouble() > density) continue;
                    int pick = rng.Next(10);
                    double re = lossless ? (rng.Next(2) == 0 ? 0.0 : -0.0) : rng.NextDouble() - 0.5;
                    y[i, j] = pick < 2 ? SignedZero(rng)
                        : pick < 4 ? new Complex(rng.Next(2) == 0 ? 0.0 : -0.0, rng.NextDouble() - 0.5)
                        : new Complex(re, rng.NextDouble() - 0.5);
                }
                double dre = lossless ? 0.0 : (rng.Next(2) == 0 ? 1 : -1) * (n + rng.NextDouble());
                y[i, i] = new Complex(dre, (rng.Next(2) == 0 ? 1 : -1) * (n + rng.NextDouble()));
            }
            int keepCount = rng.Next(1, n);
            int[] keep = Enumerable.Range(0, n).OrderBy(_ => rng.Next()).Take(keepCount).ToArray();
            AssertSparseMatchesDense(n, CellsOf(y, rng), keep, $"trial {trial} n={n}");
        }
    }

    private static Complex SignedZero(Random rng) => new(rng.Next(2) == 0 ? 0.0 : -0.0, rng.Next(2) == 0 ? 0.0 : -0.0);

    [Fact]
    public void SparseKron_kept_nodes_in_separate_islands_reduce_to_the_same_zeros()
    {
        // Two islands, each with a kept node: the cross terms are structurally zero, (+0, +0) in both.
        var y = new Complex[6, 6];
        void Line(int a, int b, Complex z) { Complex yl = Complex.One / z; y[a, a] += yl; y[b, b] += yl; y[a, b] -= yl; y[b, a] -= yl; }
        Line(0, 1, new Complex(0.01, 0.1)); Line(1, 2, new Complex(0.0, 0.2));
        Line(3, 4, new Complex(0.02, 0.05)); Line(4, 5, new Complex(0.0, 0.3));
        AssertSparseMatchesDense(6, CellsOf(y), new[] { 0, 5 }, "islands");
        AssertSparseMatchesDense(6, CellsOf(y), new[] { 2, 3, 0 }, "islands 3 kept");
    }

    [Fact]
    public void SparseKron_infinities_NaNs_and_an_overflow_mid_elimination_give_the_dense_answer()
    {
        // The proof needs finite numbers; where it does not have them the dense code runs. The outcome must match.
        var rng = new Random(7);
        for (int trial = 0; trial < 60; trial++)
        {
            int n = 3 + rng.Next(10);
            var y = new Complex[n, n];
            for (int i = 0; i < n; i++)
                for (int j = 0; j < n; j++)
                    if (i == j || rng.Next(3) == 0) y[i, j] = rng.Next(4) == 0 ? SignedZero(rng) : new Complex(rng.NextDouble() - 0.5, rng.NextDouble() - 0.5);
            for (int i = 0; i < n; i++) y[i, i] = new Complex(n + 1.0, -(n + 1.0));
            int r = rng.Next(n), c = (rng.Next(n - 1) + r + 1) % n;
            y[r, c] = trial % 2 == 0 ? new Complex(double.PositiveInfinity, 0.0) : new Complex(double.NaN, 1.0);
            int[] keep = Enumerable.Range(0, n).Where(x => x % 3 == 0).ToArray();
            AssertSparseMatchesDense(n, CellsOf(y), keep, $"non-finite trial {trial}");
        }

        // Finite input whose elimination overflows: 1e300 * 1e300 makes the next pivot non-finite.
        var o = new Complex[3, 3];
        o[0, 0] = new Complex(1e300, 0.0); o[0, 1] = new Complex(1e300, 0.0); o[1, 0] = new Complex(1e300, 0.0);
        o[1, 1] = new Complex(1e300, 0.0); o[1, 2] = Complex.One; o[2, 1] = Complex.One; o[2, 2] = new Complex(1e300, 0.0);
        AssertSparseMatchesDense(3, CellsOf(o), new[] { 2 }, "overflow");
        var big = new Complex[3, 3];
        big[0, 0] = new Complex(1e290, 0.0); big[0, 1] = new Complex(1e300, 0.0); big[1, 0] = new Complex(1e300, 0.0);
        big[1, 1] = new Complex(1e300, 0.0); big[1, 2] = Complex.One; big[2, 1] = Complex.One; big[2, 2] = new Complex(1e300, 0.0);
        AssertSparseMatchesDense(3, CellsOf(big), new[] { 2 }, "overflow 2");
    }

    [Fact]
    public void SparseKron_a_near_singular_pivot_fails_with_the_dense_message_exactly()
    {
        // Bus 1 isolated: zero self-admittance. The same exception type and the same text (KronReductionBitIdentityTests'
        // own case), and an isolated bus inside a network.
        var y = new Complex[3, 3];
        y[0, 0] = new Complex(1, -1); y[2, 2] = new Complex(1, -1);
        AssertSparseMatchesDense(3, CellsOf(y), new[] { 0, 2 }, "isolated");
        var e = Assert.Throws<InvalidOperationException>(() => SparseKronReduction.Reduce(3, CellsOf(y), new[] { 0, 2 }));
        Assert.StartsWith("Cannot eliminate bus index 1: self-admittance", e.Message, StringComparison.Ordinal);

        GridModel m = IeeeCases.Case14();
        SparseYBus sy = YBus.BuildSparse(m);
        var cells = new Dictionary<(int Row, int Col), Complex>(sy.Cells);
        // Remove every cell of bus index 7 (its row and column): an isolated, unkept bus.
        foreach ((int Row, int Col) key in cells.Keys.Where(k => k.Row == 7 || k.Col == 7).ToList()) cells.Remove(key);
        AssertSparseMatchesDense(sy.N, cells, new[] { 0, 1, 2 }, "case14 with bus index 7 isolated");
    }

    [Fact]
    public void SparseKron_argument_checks_are_the_dense_ones()
    {
        var y = new Complex[3, 3];
        y[0, 0] = Complex.One; y[1, 1] = Complex.One; y[2, 2] = Complex.One;
        Dictionary<(int Row, int Col), Complex> cells = CellsOf(y);
        AssertSparseMatchesDense(3, cells, new[] { 0, 0 }, "duplicate keep");
        AssertSparseMatchesDense(3, cells, new[] { 3 }, "keep out of range");
        AssertSparseMatchesDense(3, cells, new[] { -1 }, "keep negative");
        // A cell outside the matrix cannot be a dense entry: refused, naming the cell.
        var outside = new Dictionary<(int Row, int Col), Complex>(cells) { [(0, 3)] = Complex.One };
        var ex = Assert.Throws<ArgumentOutOfRangeException>(() => SparseKronReduction.Reduce(3, outside, new[] { 0 }));
        Assert.Contains("(0, 3)", ex.Message, StringComparison.Ordinal);
    }

    // =================================================================================================================
    // R105 / N290: DomainCoupling.Synchronising and ImpactShares on the sparse path, against the verbatim 1.95.225 code.
    // =================================================================================================================

    /// <summary>Hand-made swing-mass domains: the generators dealt round-robin into <paramref name="count"/> domains.</summary>
    private static List<SpeedDomain> RoundRobinDomains(GridModel m, int count)
    {
        var members = Enumerable.Range(0, count).Select(_ => new List<int>()).ToList();
        for (int g = 0; g < m.Generators.Count; g++) members[g % count].Add(g);
        return members.Select((list, d) => new SpeedDomain(d, 2, 3000, DomainKind.Synchronous, list, 1000.0 * (d + 1), 1000.0, 0.0, 1.0)).ToList();
    }

    private static void AssertSameCoupling((double[,] K, CouplingReport Report) expected, (double[,] K, CouplingReport Report) actual, string what)
    {
        Assert.Equal(expected.Report, actual.Report);   // a record of an enum, a string and two ints
        Assert.Equal(expected.K.GetLength(0), actual.K.GetLength(0));
        for (int i = 0; i < expected.K.GetLength(0); i++)
            for (int j = 0; j < expected.K.GetLength(1); j++)
                Assert.True(SameBits(expected.K[i, j], actual.K[i, j]), $"{what}: K[{i},{j}] {expected.K[i, j]:R} vs {actual.K[i, j]:R}");
    }

    private static IEnumerable<(string Name, GridModel Model, List<SpeedDomain> Masses)> CouplingModels()
    {
        foreach ((string name, GridModel m) in new[]
                 {
                     ("case9", IeeeCases.Case9()), ("case14", IeeeCases.Case14()), ("case30", IeeeCases.Case30()),
                     ("case57", IeeeCases.Case57()), ("case118", IeeeCases.Case118()), ("case300", IeeeCases.Case300()),
                 })
        {
            yield return (name + " x2", m, RoundRobinDomains(m, 2));
            yield return (name + " x3", m, RoundRobinDomains(m, 3));
        }
        // An isolated bus (R025: dropped from the reduction) and an open branch.
        GridModel c14 = IeeeCases.Case14();
        GridModel isolated = c14.CopyWith(buses: c14.Buses.Concat(new[] { new Bus(999, BusType.Pq) }).ToList());
        yield return ("case14 + isolated bus", isolated, RoundRobinDomains(isolated, 3));
        GridModel open = c14.CopyWith(branches: c14.Branches.Select((b, j) => j == 3 ? b with { InService = false } : b).ToList());
        yield return ("case14 open branch", open, RoundRobinDomains(open, 2));
    }

    [Fact]
    public void R105_the_synchronising_matrix_is_bit_identical_to_the_dense_code()
    {
        var dist = new DistributedConfig();
        foreach ((string name, GridModel m, List<SpeedDomain> masses) in CouplingModels())
            AssertSameCoupling(PPerformanceOracles.OldSynchronising(m, masses, dist), DomainCoupling.Synchronising(m, masses, dist), name);
    }

    [Fact]
    public void N290_the_impact_shares_at_every_bus_are_bit_identical_to_the_dense_code()
    {
        foreach ((string name, GridModel m, List<SpeedDomain> masses) in CouplingModels())
        {
            if (name.StartsWith("case300", StringComparison.Ordinal) && name.EndsWith("x3", StringComparison.Ordinal)) continue;   // x2 covers case300
            foreach (Bus b in m.Buses)
            {
                (double[] Shares, string Rule)? expected = null, actual = null;
                Exception? expectedError = null, actualError = null;
                try { expected = PPerformanceOracles.OldImpactShares(m, masses, b.Id); } catch (Exception ex) { expectedError = ex; }
                try { actual = DomainCoupling.ImpactShares(m, masses, b.Id); } catch (Exception ex) { actualError = ex; }
                string who = $"{name} bus {b.Id}";
                Assert.True((expectedError is null) == (actualError is null), $"{who}: {expectedError?.Message} / {actualError?.Message}");
                if (expectedError is not null)
                {
                    Assert.Equal(expectedError.GetType(), actualError!.GetType());
                    Assert.Equal(expectedError.Message, actualError.Message);
                    continue;
                }
                Assert.Equal(expected!.Value.Rule, actual!.Value.Rule);
                Assert.Equal(expected.Value.Shares.Length, actual.Value.Shares.Length);
                for (int d = 0; d < expected.Value.Shares.Length; d++)
                    Assert.True(SameBits(expected.Value.Shares[d], actual.Value.Shares[d]), $"{who}: share {d}");
            }
        }
    }
}
