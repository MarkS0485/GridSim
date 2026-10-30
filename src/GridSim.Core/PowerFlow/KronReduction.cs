using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using System.Runtime.CompilerServices;

namespace GridSim.Core.PowerFlow;

/// <summary>
/// Kron (star-mesh) reduction of a bus admittance matrix: eliminate the buses you don't want to keep
/// and fold their effect into the retained buses, so an N-bus network becomes an equivalent network
/// among only the retained nodes (e.g. the generator buses). This is the standard construction behind
/// classical multimachine transient stability and behind physically-derived inter-domain synchronising
/// coefficients: the imaginary part of the reduced admittance between two retained buses is their
/// synchronising susceptance.
///
/// Uses sequential single-node elimination (Y_ij <- Y_ij - Y_ik*Y_kj / Y_kk), the numerically standard
/// form; equivalent to Y_kept - Y_ke*Y_ee^-1*Y_ek but without forming the inverse.
/// </summary>
public static class KronReduction
{
    /// <summary>
    /// The message a near-singular pivot fails with. One text for the dense reduction here and the sparse one
    /// (<see cref="SparseKronReduction"/>, stream P, October 2026), which fail at the same bus with the same numbers;
    /// the wording is exactly the one this class always used (moved here unchanged, so every message is the same string).
    /// </summary>
    internal static string NearSingularPivotMessage(int k, double pivotMagnitude, double pivotFloor, double aNorm) =>
        $"Cannot eliminate bus index {k}: self-admittance {pivotMagnitude:E3} is at or below the near-singular tolerance {pivotFloor:E3} (matrix scale {aNorm:E3}) - bus is (near-)isolated.";

    /// <summary>
    /// Reduce <paramref name="y"/> to the buses in <paramref name="keepIndices"/> (matrix indices, not
    /// bus ids), eliminating the rest. Returns the reduced admittance among the kept buses, in the order
    /// given by <paramref name="keepIndices"/>.
    /// </summary>
    public static Complex[,] Reduce(Complex[,] y, IReadOnlyList<int> keepIndices)
    {
        int n = y.GetLength(0);
        if (y.GetLength(1) != n) throw new ArgumentException("Admittance matrix must be square.");

        var keep = new HashSet<int>(keepIndices);
        if (keep.Count != keepIndices.Count) throw new ArgumentException("keepIndices must be distinct.");
        foreach (int k in keepIndices)
            if (k < 0 || k >= n) throw new ArgumentOutOfRangeException(nameof(keepIndices), $"index {k} out of range");

        // Work on a mutable copy; eliminate the non-kept nodes one at a time.
        var w = (Complex[,])y.Clone();
        IEnumerable<int> toEliminate = Enumerable.Range(0, n).Where(i => !keep.Contains(i));

        // Reference scale for the near-singular pivot test. Dividing by a self-admittance that is a tiny
        // fraction of the matrix scale (a near-isolated bus) blows the reduced admittances up to NaN/Inf,
        // so reject it as (numerically) singular rather than only the exact-zero case.
        double aNorm = 0.0;
        bool inputHasNaN = false;   // R126: see the zero-row shortcut below
        foreach (Complex c in w)
        {
            double mag = c.Magnitude; if (mag > aNorm) aNorm = mag;
            if (double.IsNaN(c.Real) || double.IsNaN(c.Imaginary)) inputHasNaN = true;
        }
        double pivotFloor = GridSim.Core.Numerics.NumericTolerances.PivotFloor(aNorm);   // N008: the shared floor (aNorm x 1e-12, same bits)

        // October 2026 review (R126). The elimination is the historical one - the same nodes in the same order, and
        // for every entry the same update w[i,j] -= yik * w[k,j] / pivot, as the same expression on the same values -
        // so every reduced admittance is BIT-IDENTICAL. Three things changed, none of which touches a value:
        //  1. The active nodes are an array with O(1) swap-removal instead of a List with O(n) Remove. The order the
        //     nodes are visited in within one elimination step does not matter: each entry is updated once per step,
        //     from row k and column k, which that step never changes (it skips i == k and j == k).
        //  2. Row k is copied into a contiguous buffer once per step and the rows are reached by reference, without the
        //     two-dimensional index arithmetic and bounds checks per entry.
        //  3. The zero-row shortcut. Most of row k is zero on a network matrix. For an exactly zero w[k,j], with yik and
        //     the pivot finite, the update term yik * w[k,j] / pivot is a complex zero whose two signs IEEE 754 fixes
        //     from the signs of the operands alone (finite times zero is a signed zero, a zero over a finite non-zero
        //     pivot is a signed zero, no NaN can arise), so it depends only on yik, the pivot and the signs of the two
        //     zero components (four combinations). It is therefore computed once per (row i, sign combination) with the
        //     same expression, and only the subtraction is done per entry. When the term is exactly (+0, +0) the
        //     subtraction is skipped: x - (+0) is x for every x in round-to-nearest, signed zeros included
        //     (-0 - (+0) = -0), and for a quiet NaN. A signalling NaN would be quietened by the subtraction, so if the
        //     input holds any NaN nothing is skipped (NaNs made by the elimination itself are quiet). A term with a -0
        //     component is always applied: subtracting -0 adds +0, which turns a -0 entry into +0. When yik or the pivot
        //     is not finite the term can be NaN, whose sign and payload bits depend on the instruction sequence; those
        //     rows take the per-entry update, the same expression as every non-zero entry.
        //  Every finite result is identical to the bit; a NaN result is NaN in the same places (its payload bits are
        //  set by the instruction sequence the JIT emits, and are not a number to compare).
        // Precomputing yik / pivot once per row (the finding's suggestion) would change the rounding and is NOT done.
        var act = new int[n];        // active (not yet eliminated) node indices, in no particular order
        var posInAct = new int[n];   // where each active node sits in act
        for (int i = 0; i < n; i++) { act[i] = i; posInAct[i] = i; }
        int nAct = n;

        var rowK = new Complex[n];
        var nonZeroCols = new int[n];
        var zeroCols = new int[4][];
        for (int c = 0; c < 4; c++) zeroCols[c] = new int[n];
        var zeroCount = new int[4];
        // The four signed complex zeros, indexed by (Real is negative ? 1 : 0) | (Imaginary is negative ? 2 : 0).
        Complex[] signedZero = { new(0.0, 0.0), new(-0.0, 0.0), new(0.0, -0.0), new(-0.0, -0.0) };

        foreach (int k in toEliminate)
        {
            Complex pivot = w[k, k];
            if (pivot.Magnitude <= pivotFloor)
                throw new InvalidOperationException(NearSingularPivotMessage(k, pivot.Magnitude, pivotFloor, aNorm));

            bool pivotFinite = double.IsFinite(pivot.Real) && double.IsFinite(pivot.Imaginary);

            // Row k (constant during this step) into the buffer, its active columns split into nonzero and zero ones.
            int nonZero = 0;
            Array.Clear(zeroCount);
            for (int a = 0; a < nAct; a++)
            {
                int j = act[a];
                if (j == k) continue;
                Complex v = w[k, j];
                rowK[j] = v;
                if (v.Real == 0.0 && v.Imaginary == 0.0)
                {
                    int c = (double.IsNegative(v.Real) ? 1 : 0) | (double.IsNegative(v.Imaginary) ? 2 : 0);
                    zeroCols[c][zeroCount[c]++] = j;
                }
                else nonZeroCols[nonZero++] = j;
            }

            for (int a = 0; a < nAct; a++)
            {
                int i = act[a];
                if (i == k) continue;
                Complex yik = w[i, k];
                if (yik == Complex.Zero) continue;
                ref Complex rowI = ref w[i, 0];

                for (int b = 0; b < nonZero; b++)
                {
                    int j = nonZeroCols[b];
                    ref Complex wij = ref Unsafe.Add(ref rowI, j);
                    wij -= yik * rowK[j] / pivot;
                }

                bool shortcut = pivotFinite && double.IsFinite(yik.Real) && double.IsFinite(yik.Imaginary);
                for (int c = 0; c < 4; c++)
                {
                    int count = zeroCount[c];
                    if (count == 0) continue;
                    if (!shortcut)
                    {
                        // A non-finite yik or pivot: the per-entry update, as for the non-zero entries.
                        int[] zc = zeroCols[c];
                        for (int b = 0; b < count; b++)
                        {
                            int j = zc[b];
                            ref Complex wij = ref Unsafe.Add(ref rowI, j);
                            wij -= yik * rowK[j] / pivot;
                        }
                        continue;
                    }
                    Complex t = yik * signedZero[c] / pivot;   // the term every zero entry of this sign pattern gives
                    if (!inputHasNaN && BitConverter.DoubleToInt64Bits(t.Real) == 0L && BitConverter.DoubleToInt64Bits(t.Imaginary) == 0L)
                        continue;   // x - (+0, +0) == x exactly
                    int[] cols = zeroCols[c];
                    for (int b = 0; b < count; b++)
                    {
                        ref Complex wij = ref Unsafe.Add(ref rowI, cols[b]);
                        wij -= t;
                    }
                }
            }

            // Remove k from the active set (swap with the last active node).
            int at = posInAct[k];
            int last = act[--nAct];
            act[at] = last;
            posInAct[last] = at;
        }

        // Extract the reduced matrix in keepIndices order.
        int m = keepIndices.Count;
        var reduced = new Complex[m, m];
        for (int a = 0; a < m; a++)
            for (int b = 0; b < m; b++)
                reduced[a, b] = w[keepIndices[a], keepIndices[b]];
        return reduced;
    }
}
