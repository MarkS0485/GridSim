using GridSim.Core.Abstractions;

namespace GridSim.Core.PowerFlow;

/// <summary>
/// A dense LU solve (Gaussian elimination with partial pivoting) for Ax = b.
/// Correct and obvious; plenty for the Jacobians of small networks.
/// <see cref="SolveInPlace"/> works directly on the caller's arrays (no copies), so
/// the Newton-Raphson loop can reuse one Jacobian/mismatch buffer set across
/// iterations. When networks get large, a sparse factorisation or a GPU backend can
/// replace this behind <see cref="ILinearSolver"/> without touching the solver.
///
/// Performance: the elimination runs over a flat, row-major <c>double[]</c> copy of the
/// matrix rather than the caller's <c>double[,]</c>. 1-D indexing lets the JIT drop the
/// per-access 2-D bounds checks, keeps each row contiguous for cache locality, and lets
/// the inner row-update vectorise through the bit-exact
/// <see cref="GridSim.Core.Numerics.SimdKernels.SubtractScaledRow(ref double, int, ref double, int, int, double)"/>
/// (a separate multiply and subtract per element at every vector width, never a fused multiply-add; D-DOC-209,
/// October 2026: the old DenseLuKernels forwarder had no caller and is gone). The pivot search, row swaps and back
/// substitution keep the original scalar order, so the result is bit-for-bit identical to
/// the textbook version. The factored matrix is written back into <paramref name="a"/> on
/// completion, preserving the in-place contract.
/// </summary>
public sealed class DenseLuSolver : ILinearSolver
{
    public string Name => "dense-lu (cpu)";

    public void SolveInPlace(double[,] a, double[] b)
    {
        int n = b.Length;
        if (a.GetLength(0) != n || a.GetLength(1) != n)
            throw new ArgumentException("A must be square and match b's length.");
        if (n == 0) return;

        // Flat row-major working copy. double[,] is stored row-major and contiguous,
        // so a straight block copy gives element (i,j) at m[i*n + j].
        var m = new double[n * n];
        Buffer.BlockCopy(a, 0, m, 0, n * n * sizeof(double));

        FactorSolveInPlace(m, b, n);

        // Preserve the in-place contract: hand the factored matrix back to the caller.
        Buffer.BlockCopy(m, 0, a, 0, n * n * sizeof(double));
    }

    /// <summary>
    /// The LU factorisation with partial pivoting and the solve, in place on a flat row-major matrix
    /// <paramref name="m"/> (<c>n*n</c> entries, element (i, j) at <c>m[i*n + j]</c>) and right-hand side
    /// <paramref name="b"/> (<c>n</c> entries; overwritten with the solution). This is the body
    /// <see cref="SolveInPlace"/> always ran on its private copy, now shared so the batched CPU solver can factor
    /// each system directly on its own segment of the batch array (R071: it used to build a <c>double[n,n]</c>, a new
    /// solver and that solver's flat copy for every system). Same operations in the same order; the row update is
    /// the same SIMD kernel through its by-reference overload, which splits work by element count only, never by
    /// memory alignment, so the bits do not depend on where the segment sits. Throws
    /// <see cref="InvalidOperationException"/> on a near-singular pivot (m and b are then partly overwritten).
    /// </summary>
    internal static void FactorSolveInPlace(Span<double> m, Span<double> b, int n)
        => FactorSolveInPlace(m, b, n, GridSim.Core.Numerics.SimdCapabilities.Tier);

    /// <summary>
    /// <see cref="FactorSolveInPlace(Span{double}, Span{double}, int)"/> with the row update run at an explicit vector
    /// <paramref name="tier"/>: the R102 test seam (every tier's arm runs on any machine, in software where the hardware
    /// lacks it). D-DOC-183 (October 2026): it lets a test drive a whole Newton power flow through each tier and compare
    /// the results bit for bit. Production passes <see cref="GridSim.Core.Numerics.SimdCapabilities.Tier"/>; the kernel is
    /// the bit-exact one at every tier, so the tier changes the speed only.
    /// </summary>
    internal static void FactorSolveInPlace(Span<double> m, Span<double> b, int n, GridSim.Core.Numerics.SimdTier tier)
    {
        if (m.Length != n * n) throw new ArgumentException($"m must hold n*n = {n * n} entries, got {m.Length}.", nameof(m));
        if (b.Length != n) throw new ArgumentException($"b must hold n = {n} entries, got {b.Length}.", nameof(b));
        if (n == 0) return;
        ref double m0 = ref System.Runtime.InteropServices.MemoryMarshal.GetReference(m);

        // Reference scale for the near-singular pivot test: the largest magnitude anywhere in A.
        // A pivot that has collapsed to a tiny fraction of this is numerically singular - dividing
        // by it would emit NaN/Inf and corrupt the whole Newton step, so fail loudly instead. The
        // relative floor makes the test scale-invariant (an exact-zero pivot is the aNorm==0 subcase).
        double aNorm = 0.0;
        foreach (double v in m)
        {
            // A NaN entry is refused (the sparse LU refuses it too): it never wins the max or fails the pivot test, so it
            // used to be factored into a NaN solution reported as success.
            if (double.IsNaN(v))
                throw new InvalidOperationException("LU solve: the matrix contains a NaN entry.");
            double av = Math.Abs(v); if (av > aNorm) aNorm = av;
        }
        // Bug #40: a NaN or infinite right-hand side is refused too (it used to come back as a NaN / Inf solution reported
        // as success). A read-only pre-check: on finite input nothing below changes.
        int badRow = GridSim.Core.Numerics.NumericTolerances.FirstNonFinite(b);
        if (badRow >= 0)
            throw new InvalidOperationException(GridSim.Core.Numerics.NumericTolerances.NonFiniteRightHandSide("LU solve", b[badRow], badRow));
        double pivotFloor = GridSim.Core.Numerics.NumericTolerances.PivotFloor(aNorm);   // N008: the shared floor (aNorm x 1e-12, same bits)

        for (int k = 0; k < n; k++)
        {
            int kk = k * n;

            // Partial pivot: largest |m[i,k]| on/below the diagonal (scalar, original order).
            int p = k;
            double max = Math.Abs(m[kk + k]);
            for (int i = k + 1; i < n; i++)
            {
                double v = Math.Abs(m[i * n + k]);
                if (v > max) { max = v; p = i; }
            }
            if (max <= pivotFloor)
                throw new InvalidOperationException(
                    $"Near-singular matrix in LU solve: pivot {max:E3} at column {k} is at or below the tolerance {pivotFloor:E3} (matrix scale {aNorm:E3}).");

            if (p != k)
            {
                int pp = p * n;
                for (int j = k; j < n; j++) (m[kk + j], m[pp + j]) = (m[pp + j], m[kk + j]);
                (b[k], b[p]) = (b[p], b[k]);
            }

            double pivot = m[kk + k];
            int count = n - k; // columns k..n-1 of the trailing submatrix
            for (int i = k + 1; i < n; i++)
            {
                int ii = i * n;
                double factor = m[ii + k] / pivot;
                if (factor == 0.0) continue;
                GridSim.Core.Numerics.SimdKernels.SubtractScaledRow(ref m0, ii + k, ref m0, kk + k, count, factor, tier);
                b[i] -= factor * b[k];
            }
        }

        // Back substitution (scalar, original summation order for identical rounding).
        for (int i = n - 1; i >= 0; i--)
        {
            int ii = i * n;
            double sum = b[i];
            for (int j = i + 1; j < n; j++) sum -= m[ii + j] * b[j];
            b[i] = sum / m[ii + i];
        }
    }
}
