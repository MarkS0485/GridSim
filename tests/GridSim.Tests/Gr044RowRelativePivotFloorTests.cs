using System.Numerics;
using GridSim.Core.Abstractions;
using GridSim.Core.Numerics;
using GridSim.Core.PowerFlow;
using GridSim.Gpu;
using Xunit;

namespace GridSim.Tests;

/// <summary>
/// GR-044 (November 2026): every real LU (dense, batched, sparse, sparse replay, multi-case, Kron, the GPU and ROCm
/// LUs) tests a pivot against the floor of the ORIGINAL row that wins it, 1e-12 times that row's largest entry, in
/// place of one floor for the whole matrix. A whole-matrix floor is not invariant under row scaling: it refused
/// well-posed systems such as diag(1e6, 1e-9) purely because of units. These tests pin:
/// <list type="bullet">
/// <item>the old refusals that are not singular by their own row's scale now solve, exactly;</item>
/// <item>a row that has cancelled to 1e-12 of its own scale is still refused, at exactly the boundary;</item>
/// <item>every system the whole-matrix floor accepted gives the same bits (a reference copy of the old LU);</item>
/// <item>an infinite entry keeps the whole-matrix (infinite) floor, so it is refused as before;</item>
/// <item>the ROCm post-factorisation check gives the CPU verdict with the getrf pivots;</item>
/// <item>the opt-in diagonal preference may now take a diagonal that lies between its row floor and the old floor.</item>
/// </list>
/// </summary>
public class Gr044RowRelativePivotFloorTests
{
    private static long Bits(double v) => BitConverter.DoubleToInt64Bits(v);

    private static CsrMatrix Csr(double[,] a)
    {
        int n = a.GetLength(0);
        var b = new CsrMatrix.Builder(n);
        for (int i = 0; i < n; i++)
            for (int j = 0; j < n; j++)
                if (a[i, j] != 0.0) b.Add(i, j, a[i, j]);
        return b.Build();
    }

    // ------------------------------------------------------------------------------------------ the helpers

    [Fact]
    public void Row_floors_are_the_row_scales_times_the_relative_floor_and_never_above_the_matrix_floor()
    {
        double[] f = NumericTolerances.RowPivotFloorsRowMajor(new double[] { 1e6, -2.0, 0.0, 1e-9 }, 2, 1e6);
        Assert.Equal(NumericTolerances.PivotFloor(1e6), f[0]);
        Assert.Equal(NumericTolerances.PivotFloor(1e-9), f[1]);
        Assert.True(f[1] <= NumericTolerances.PivotFloor(1e6));

        // CSC: rows are the original row indices; an empty row has floor 0 (an exact zero pivot is still caught by <=).
        double[] c = NumericTolerances.RowPivotFloorsCsc(3, new[] { 0, 2, 3, 3 }, new[] { 0, 2, 0 }, new[] { 4.0, -8.0, 5.0 }, 8.0);
        Assert.Equal(new[] { NumericTolerances.PivotFloor(5.0), 0.0, NumericTolerances.PivotFloor(8.0) }, c);
    }

    [Fact]
    public void An_infinite_matrix_scale_keeps_the_whole_matrix_floor_on_every_row_and_bad_scales_fail_loud()
    {
        var s = new[] { 1.0, double.PositiveInfinity };
        NumericTolerances.ToRowPivotFloors(s, double.PositiveInfinity);
        Assert.Equal(new[] { double.PositiveInfinity, double.PositiveInfinity }, s);
        Assert.Throws<ArgumentException>(() => NumericTolerances.ToRowPivotFloors(new[] { double.NaN }, 1.0));
        Assert.Throws<ArgumentException>(() => NumericTolerances.ToRowPivotFloors(new[] { -1.0 }, 1.0));
        Assert.Throws<ArgumentException>(() => NumericTolerances.ToRowPivotFloors(new[] { 2.0 }, 1.0));
        Assert.Throws<ArgumentOutOfRangeException>(() => NumericTolerances.ToRowPivotFloors(new[] { 1.0 }, double.NaN));
        Assert.Throws<ArgumentException>(() => NumericTolerances.RowPivotFloorsCsc(2, new[] { 0, 1, 1 }, new[] { 5 }, new[] { 1.0 }, 1.0));
    }

    [Fact]
    public void FirstSingularPivot_maps_each_U_row_to_its_original_row_through_the_getrf_pivots()
    {
        // A = [[1, 0], [1, 1e-13]] after getrf (no swap: ipiv = 1, 2): U = [[1, 0], [0, 1e-13]], L21 = 1. Row 1 has scale
        // 1, floor 1e-12, so U(1,1) = 1e-13 is singular. With the rows given the other way round (ipiv = 2, 2) the same U
        // row 1 comes from original row 0.
        double[] lu = { 1.0, 1.0, 0.0, 1e-13 };
        double[] floors = { NumericTolerances.PivotFloor(1.0), NumericTolerances.PivotFloor(1.0) };
        Assert.Equal(1, NumericTolerances.FirstSingularPivot(lu, 2, new[] { 1, 2 }, floors));
        double[] smallRow = { NumericTolerances.PivotFloor(1.0), NumericTolerances.PivotFloor(1e-12) };
        Assert.Equal(-1, NumericTolerances.FirstSingularPivot(lu, 2, new[] { 1, 2 }, smallRow));    // U row 1 = row 1 (floor 1e-24)
        Assert.Equal(1, NumericTolerances.FirstSingularPivot(lu, 2, new[] { 2, 2 }, smallRow));     // U row 1 = row 0 (floor 1e-12)
        Assert.Throws<ArgumentException>(() => NumericTolerances.FirstSingularPivot(lu, 2, new[] { 0, 2 }, floors));
        Assert.Throws<ArgumentException>(() => NumericTolerances.FirstSingularPivot(lu, 2, new[] { 1 }, floors));
    }

    // ------------------------------------------------------------------------------------------ old refusals now solve

    [Fact]
    public void A_diagonal_system_in_mixed_units_now_solves_exactly_in_every_real_LU()
    {
        // diag(1e6, 1e-9): the whole-matrix floor (1e-6) refused the 1e-9 pivot, though the system is perfectly
        // conditioned row by row. The row floor of row 1 is 1e-21.
        var a = new double[,] { { 1e6, 0.0 }, { 0.0, 1e-9 } };
        var x = new[] { 1.0, 1.0 };
        new DenseLuSolver().SolveInPlace((double[,])a.Clone(), x);
        Assert.Equal(Bits(1.0 / 1e6), Bits(x[0]));
        Assert.Equal(Bits(1.0 / 1e-9), Bits(x[1]));

        double[]? xs = SparseLu.Solve(Csr(a), new[] { 1.0, 1.0 });
        Assert.NotNull(xs);
        Assert.Equal(Bits(x[0]), Bits(xs![0]));
        Assert.Equal(Bits(x[1]), Bits(xs[1]));

        double[] aFlat = { 1e6, 0.0, 0.0, 1e-9 };
        double[] bFlat = { 1.0, 1.0 };
        Assert.Equal(new[] { true }, new CpuBatchedLuSolver().SolveBatch(1, 2, aFlat, bFlat));
        Assert.Equal(Bits(x[0]), Bits(bFlat[0]));
        Assert.Equal(Bits(x[1]), Bits(bFlat[1]));

        // The ILGPU dense LU (on the device where there is one, the CPU LU otherwise) gives the same answer.
        var g = new[] { 1.0, 1.0 };
        new GpuDenseLinearSolver().SolveInPlace((double[,])a.Clone(), g);
        Assert.Equal(Bits(x[0]), Bits(g[0]));
        Assert.Equal(Bits(x[1]), Bits(g[1]));
    }

    [Fact]
    public void A_weakly_tied_bus_is_now_reduced_to_finite_admittances()
    {
        // The bus row is [j1e-13, 0, -j1e-13]: its own floor is 1e-25, so its pivot -j1e-13 is well clear of it, and the
        // fold-in is finite: Y00' = Y00 - Y02 * Y20 / Y22 (the reduction's own expression), Y11 untouched.
        var y = new Complex[3, 3];
        y[0, 0] = new Complex(0, -10); y[1, 1] = new Complex(0, -10);
        y[0, 1] = new Complex(0, 5); y[1, 0] = new Complex(0, 5);
        y[0, 2] = new Complex(0, 1e-13); y[2, 0] = new Complex(0, 1e-13);
        y[2, 2] = new Complex(0, -1e-13);
        Complex[,] r = KronReduction.Reduce(y, new[] { 0, 1 });
        Complex expected00 = y[0, 0] - y[0, 2] * y[2, 0] / y[2, 2];
        Assert.Equal(Bits(expected00.Real), Bits(r[0, 0].Real));
        Assert.Equal(Bits(expected00.Imaginary), Bits(r[0, 0].Imaginary));
        Assert.Equal(y[1, 1], r[1, 1]);
        Assert.Equal(y[0, 1], r[0, 1]);

        var cells = new Dictionary<(int Row, int Col), Complex>();
        for (int i = 0; i < 3; i++) for (int j = 0; j < 3; j++) if (y[i, j] != Complex.Zero) cells[(i, j)] = y[i, j];
        Complex[,] rs = SparseKronReduction.Reduce(3, cells, new[] { 0, 1 });
        for (int i = 0; i < 2; i++)
            for (int j = 0; j < 2; j++)
            {
                Assert.Equal(Bits(r[i, j].Real), Bits(rs[i, j].Real));
                Assert.Equal(Bits(r[i, j].Imaginary), Bits(rs[i, j].Imaginary));
            }
    }

    // ------------------------------------------------------------------------------------------ the boundary

    [Fact]
    public void A_row_cancelled_to_its_own_floor_is_refused_at_exactly_the_boundary_in_every_real_LU()
    {
        // [[1, 0], [1, p]]: column 0 ties, the first row wins, row 1 becomes [0, p]. Row 1's scale is 1, so its floor is
        // exactly 1e-12: p AT it is singular, the next double above it is not.
        double atFloor = NumericTolerances.PivotFloor(1.0);
        double above = Math.BitIncrement(atFloor);

        Assert.Throws<InvalidOperationException>(() => new DenseLuSolver().SolveInPlace(new double[,] { { 1, 0 }, { 1, atFloor } }, new[] { 1.0, 1.0 }));
        var x = new[] { 1.0, 2.0 };
        new DenseLuSolver().SolveInPlace(new double[,] { { 1, 0 }, { 1, above } }, x);
        Assert.Equal(Bits((2.0 - 1.0) / above), Bits(x[1]));

        Assert.Null(SparseLu.Solve(Csr(new double[,] { { 1, 0 }, { 1, atFloor } }), new[] { 1.0, 1.0 }));
        Assert.NotNull(SparseLu.Solve(Csr(new double[,] { { 1, 0 }, { 1, above } }), new[] { 1.0, 1.0 }));

        double[] aFlat = { 1, 0, 1, atFloor, 1, 0, 1, above };
        double[] bFlat = { 1, 1, 1, 2 };
        Assert.Equal(new[] { false, true }, new CpuBatchedLuSolver().SolveBatch(2, 2, aFlat, bFlat));
        Assert.Equal(Bits(x[1]), Bits(bFlat[3]));

        Assert.Throws<InvalidOperationException>(() => KronReduction.Reduce(new Complex[,] { { 1, 0 }, { 1, atFloor } }, new[] { 0 }));
        KronReduction.Reduce(new Complex[,] { { 1, 0 }, { 1, above } }, new[] { 0 });

        Assert.ThrowsAny<InvalidOperationException>(() => new GpuDenseLinearSolver().SolveInPlace(new double[,] { { 1, 0 }, { 1, atFloor } }, new[] { 1.0, 1.0 }));
    }

    [Fact]
    public void A_near_singular_row_in_small_units_is_still_refused()
    {
        // The corrected NumericsTests case: row 1 has scale 1 and its pivot 1e-13 is below its own 1e-12 floor.
        Assert.Throws<InvalidOperationException>(() => new DenseLuSolver().Solve(new double[,] { { 1e6, 0.0 }, { 1.0, 1e-13 } }, new[] { 1.0, 1.0 }));
        Assert.Null(SparseLu.Solve(Csr(new double[,] { { 1e6, 0.0 }, { 1.0, 1e-13 } }), new[] { 1.0, 1.0 }));
    }

    [Fact]
    public void A_matrix_with_an_infinite_entry_is_refused_as_before()
    {
        // The matrix scale is infinite, so every row keeps the infinite whole-matrix floor and the first pivot is refused.
        Assert.Throws<InvalidOperationException>(() => new DenseLuSolver().SolveInPlace(
            new double[,] { { 1, double.PositiveInfinity }, { 1, 1 } }, new[] { 1.0, 1.0 }));
        Assert.Throws<InvalidOperationException>(() => KronReduction.Reduce(
            new Complex[,] { { 1, double.PositiveInfinity }, { 1, 1 } }, new[] { 0 }));
    }

    // ------------------------------------------------------------------------------------------ accepted solves keep their bits

    /// <summary>The dense LU as it was before GR-044 (one whole-matrix floor), verbatim in its arithmetic.</summary>
    private static bool ReferenceSolve(double[,] src, double[] b)
    {
        int n = b.Length;
        var m = (double[,])src.Clone();
        double aNorm = 0.0;
        foreach (double v in m) { double av = Math.Abs(v); if (av > aNorm) aNorm = av; }
        double floor = NumericTolerances.PivotFloor(aNorm);
        for (int k = 0; k < n; k++)
        {
            int p = k;
            double max = Math.Abs(m[k, k]);
            for (int i = k + 1; i < n; i++) { double v = Math.Abs(m[i, k]); if (v > max) { max = v; p = i; } }
            if (max <= floor) return false;
            if (p != k)
            {
                for (int j = k; j < n; j++) (m[k, j], m[p, j]) = (m[p, j], m[k, j]);
                (b[k], b[p]) = (b[p], b[k]);
            }
            double pivot = m[k, k];
            for (int i = k + 1; i < n; i++)
            {
                double factor = m[i, k] / pivot;
                if (factor == 0.0) continue;
                for (int j = k; j < n; j++) m[i, j] -= factor * m[k, j];
                b[i] -= factor * b[k];
            }
        }
        for (int i = n - 1; i >= 0; i--)
        {
            double sum = b[i];
            for (int j = i + 1; j < n; j++) sum -= m[i, j] * b[j];
            b[i] = sum / m[i, i];
        }
        return true;
    }

    [Fact]
    public void Every_system_the_whole_matrix_floor_accepted_keeps_its_bits_and_only_refusals_turn_into_solves()
    {
        var rng = new Random(44);
        int acceptedBoth = 0, newlySolved = 0;
        for (int trial = 0; trial < 400; trial++)
        {
            int n = 1 + rng.Next(9);
            var a = new double[n, n];
            for (int i = 0; i < n; i++)
            {
                // Rows in very different units (1e-15 .. 1e6), some near-dependent, so both verdicts and both floors occur.
                double rowScale = Math.Pow(10, rng.Next(-15, 7));
                for (int j = 0; j < n; j++) a[i, j] = (rng.NextDouble() * 2 - 1) * rowScale;
            }
            if (n > 1 && trial % 3 == 0)
            {
                int r = rng.Next(n), s = (r + 1) % n;
                double eps = new[] { 0.0, 1e-16, 1e-13, 1e-12, 1e-11 }[rng.Next(5)];
                for (int j = 0; j < n; j++) a[r, j] = a[s, j] * 3.0 + eps * a[s, j];
            }
            var bRef = Enumerable.Range(0, n).Select(i => Math.Sin(i + trial)).ToArray();
            var bNew = (double[])bRef.Clone();
            bool refOk = ReferenceSolve(a, bRef);
            bool newOk;
            try { new DenseLuSolver().SolveInPlace((double[,])a.Clone(), bNew); newOk = true; }
            catch (InvalidOperationException) { newOk = false; }
            if (refOk)
            {
                Assert.True(newOk, $"trial {trial}: a system the whole-matrix floor accepted was refused");
                for (int i = 0; i < n; i++) Assert.Equal(Bits(bRef[i]), Bits(bNew[i]));
                acceptedBoth++;
            }
            else if (newOk) newlySolved++;
        }
        Assert.True(acceptedBoth > 50, $"only {acceptedBoth} systems accepted by both");
        Assert.True(newlySolved > 0, "the set must hold systems only the row floor accepts");
    }

    // ------------------------------------------------------------------------------------------ ROCm check

    /// <summary>The textbook partial-pivoting LU (getrf's algorithm) with its one-based pivot vector, column-major.</summary>
    private static (double[] Lu, int[] Ipiv, double Scale, double[] RowScales) HostLu(double[,] src)
    {
        int n = src.GetLength(0);
        var m = (double[,])src.Clone();
        double scale = NumericTolerances.MaxAbs(src);
        var rowScales = new double[n];
        for (int i = 0; i < n; i++) for (int j = 0; j < n; j++) rowScales[i] = Math.Max(rowScales[i], Math.Abs(src[i, j]));
        var ipiv = new int[n];
        for (int k = 0; k < n; k++)
        {
            int p = k;
            double max = Math.Abs(m[k, k]);
            for (int i = k + 1; i < n; i++) { double v = Math.Abs(m[i, k]); if (v > max) { max = v; p = i; } }
            ipiv[k] = p + 1;
            for (int j = 0; j < n; j++) (m[k, j], m[p, j]) = (m[p, j], m[k, j]);
            if (m[k, k] == 0.0) continue;
            for (int i = k + 1; i < n; i++)
            {
                double f = m[i, k] / m[k, k];
                if (f == 0.0) continue;
                m[i, k] = f;
                for (int j = k + 1; j < n; j++) m[i, j] -= f * m[k, j];
            }
        }
        var col = new double[n * n];
        for (int j = 0; j < n; j++) for (int i = 0; i < n; i++) col[j * n + i] = m[i, j];
        return (col, ipiv, scale, rowScales);
    }

    [Fact]
    public void The_rocm_row_aware_check_gives_the_cpu_verdict_on_mixed_unit_and_near_singular_matrices()
    {
        var rng = new Random(20261102);
        int singular = 0, total = 0;
        for (int n = 1; n <= 10; n++)
        {
            for (int trial = 0; trial < 40; trial++)
            {
                var a = new double[n, n];
                for (int i = 0; i < n; i++)
                {
                    double rowScale = Math.Pow(10, rng.Next(-12, 4));
                    for (int j = 0; j < n; j++) a[i, j] = (rng.NextDouble() * 2 - 1) * rowScale;
                }
                if (trial % 2 == 1)
                {
                    int r = rng.Next(n);
                    double eps = new[] { 0.0, 1e-16, 1e-13, 1e-12, 1e-11, 1e-9 }[rng.Next(6)];
                    for (int j = 0; j < n; j++)
                    {
                        double comb = 0;
                        for (int i = 0; i < n; i++) if (i != r) comb += (i % 3 - 1) * 0.5 * a[i, j];
                        a[r, j] = comb + eps * (rng.NextDouble() * 2 - 1);
                    }
                }
                bool cpuSingular;
                try { new DenseLuSolver().SolveInPlace((double[,])a.Clone(), new double[n]); cpuSingular = false; }
                catch (InvalidOperationException) { cpuSingular = true; }
                (double[] lu, int[] ipiv, double scale, double[] rowScales) = HostLu(a);
                bool rocmSingular;
                try { RocmDenseLinearSolver.ThrowIfNearSingular(lu, ipiv, n, scale, rowScales); rocmSingular = false; }
                catch (InvalidOperationException) { rocmSingular = true; }
                Assert.True(cpuSingular == rocmSingular, $"order {n}, trial {trial}: CPU singular {cpuSingular}, ROCm check singular {rocmSingular}");
                total++;
                if (cpuSingular) singular++;
            }
        }
        Assert.True(singular > 20 && singular < total - 20, $"the set must hold both verdicts in number ({singular} of {total})");
    }

    // ------------------------------------------------------------------------------------------ the opt-in diagonal preference

    [Fact]
    public void The_opt_in_diagonal_preference_may_now_take_a_diagonal_between_its_row_floor_and_the_old_floor()
    {
        // A = [[1e-12, 1e-12], [1.5e-12, 1]], preference 0.5. Column 0's largest candidate is row 1 (1.5e-12); the
        // diagonal row 0 (1e-12) is within the threshold (>= 0.75e-12). Under the old whole-matrix floor (1e-12) the
        // diagonal was "not above the singular floor", so row 1 was taken. Row 0's own floor is 1e-24, so the diagonal is
        // taken now. Before: L = [1, 0.6666666666666666, 1], U = [1.5e-12, 1, -0.6666666666656667],
        // x(1, 2) = (999999999999.4999, 0.50000000000075).
        int[] ap = { 0, 2, 4 }, ai = { 0, 1, 0, 1 }, q = { 0, 1 };
        double[] ax = { 1e-12, 1.5e-12, 1e-12, 1.0 };
        SparseLuFactorization f = SparseLuFactorization.Factor(2, ap, ai, ax, q, keepPlan: true, diagonalPreference: 0.5)!;
        SparseLuPlan plan = f.Plan!;
        Assert.Equal(0, plan.PivotRow[0]);
        Assert.Equal(new[] { Bits(1.0), Bits(1.5), Bits(1.0) }, f.LValues.ToArray().Select(Bits).ToArray());
        Assert.Equal(new[] { Bits(1e-12), Bits(1e-12), Bits(0.9999999999985) }, f.UValues.ToArray().Select(Bits).ToArray());
        double[] x = f.Solve(new[] { 1.0, 2.0 });
        Assert.Equal(Bits(999999999999.5), Bits(x[0]));
        Assert.Equal(Bits(0.5000000000007501), Bits(x[1]));

        // The replay and the multi-case kernel take the same pivot and give the same bits.
        SparseLuFactorization replay = SparseLuFactorization.Refactor(plan, ax)!;
        Assert.Equal(f.LValues.ToArray().Select(Bits), replay.LValues.ToArray().Select(Bits));
        Assert.Equal(f.UValues.ToArray().Select(Bits), replay.UValues.ToArray().Select(Bits));
        MultiCaseSparseLu.Batch batch = MultiCaseSparseLu.Factor(plan, new[] { ap }, new[] { ai }, new[] { ax });
        Assert.Equal(new[] { true }, batch.SharedLanes);
        Assert.False(batch.IsSingular(0));
        Assert.Equal(f.UValues.ToArray().Select(Bits), batch.Lane(0)!.UValues.ToArray().Select(Bits));

        // Off by default: plain partial pivoting takes row 1, as it always did.
        Assert.Equal(1, SparseLuFactorization.Factor(2, ap, ai, ax, q, keepPlan: true)!.Plan!.PivotRow[0]);
    }

    [Fact]
    public void The_multi_case_kernel_and_the_replay_refuse_and_accept_exactly_as_the_single_factorisation()
    {
        // Plan on a regular matrix; lanes: the mixed-unit diagonal (now accepted) and a row cancelled to its own floor.
        int[] ap = { 0, 2, 4 }, ai = { 0, 1, 0, 1 }, q = { 0, 1 };
        double[] plan = { 4, 1, 1, 3 };
        SparseLuPlan p = SparseLuFactorization.Factor(2, ap, ai, plan, q, keepPlan: true)!.Plan!;
        double[] mixed = { 1e6, 1e-300, 1e-300, 1e-9 };          // both rows keep their pivots; row 1 scale 1e-9
        double[] cancelled = { 1, 1, 0.5, 0.5 + 1e-13 };          // row 1 = row 0 / 2 + 1e-13 in column 1: below 1e-12 x 1
        foreach (double[] ax in new[] { mixed, cancelled })
        {
            SparseLuFactorization? single = SparseLuFactorization.Factor(2, ap, ai, ax, q);
            MultiCaseSparseLu.Batch batch = MultiCaseSparseLu.Factor(p, new[] { ap }, new[] { ai }, new[] { ax });
            Assert.Equal(single is null, batch.IsSingular(0));
            SparseLuFactorization? replay = SparseLuFactorization.Refactor(p, ax);
            if (single is null) { Assert.Null(replay); continue; }
            Assert.NotNull(replay);
            Assert.Equal(single.UValues.ToArray().Select(Bits), replay!.UValues.ToArray().Select(Bits));
        }
        Assert.NotNull(SparseLuFactorization.Factor(2, ap, ai, mixed, q));
        Assert.Null(SparseLuFactorization.Factor(2, ap, ai, cancelled, q));
    }
}
