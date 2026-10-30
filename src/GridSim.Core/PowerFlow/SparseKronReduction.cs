using System;
using System.Collections.Generic;
using System.Numerics;

namespace GridSim.Core.PowerFlow;

/// <summary>
/// The Kron reduction of <see cref="KronReduction.Reduce"/>, done on the nonzero pattern of the matrix instead of the
/// full dense array, giving the SAME BITS in every entry of the result (stream P, October 2026: R105, N290, R007).
///
/// <para><b>Why.</b> The dense reduction allocates the whole n x n complex matrix (about 200 MB at gb-full's 3,539 buses,
/// about 320 MB for the 4,491-node machine-augmented network of the classical stability model) and visits every active
/// row and column at every elimination step. A network admittance has a handful of entries per row, so almost all of
/// that work and memory is spent on zeros. This class keeps, per row, only the entries that are or become nonzero
/// (the original pattern plus the fill-in of the elimination) and eliminates the same nodes in the same order with the
/// same arithmetic.</para>
///
/// <para><b>Why it is bit-identical (the proof).</b> The dense reduction eliminates the non-kept nodes k in ascending
/// index order, and at step k, for every active row i != k with w[i,k] != 0 (the test is <c>yik == Complex.Zero</c>,
/// which a signed zero also passes) and every active column j != k, does <c>w[i,j] -= yik * w[k,j] / pivot</c> with
/// <c>pivot = w[k,k]</c> (its zero-row shortcut is proved and tested to give exactly this per-entry update; see the
/// R126 comment in <see cref="KronReduction.Reduce"/>). Call the PATTERN the set of positions that are stored here: every
/// input cell, its mirror (j,i), every diagonal, and the fill: at step k, every pair (i,j) of active neighbours of k.
/// The pattern is structurally symmetric, so the rows i with a stored (i,k) are exactly the columns of row k.</para>
/// <list type="number">
/// <item><b>Outside the pattern the dense matrix holds exactly (+0, +0) at every step.</b> Initially, every position
///   that is not an input cell is the default (+0, +0). At step k a position (i,j) outside the pattern can only receive
///   an update when w[i,k] is stored-and-nonzero (otherwise the dense code skips the row), and then w[k,j] must be
///   outside the pattern (else the fill would have put (i,j) in it), so by induction w[k,j] = (+0, +0) and the term is
///   <c>yik * (+0, +0) / pivot</c>. With yik and the pivot finite and the pivot nonzero (the pivot test guarantees
///   |pivot| &gt; 0), every product in the complex multiply is a signed zero, so the product is a complex zero, and a
///   complex zero divided by a finite nonzero complex is a complex zero (the divide's denominators are nonzero: in each
///   branch of the scaled division the denominator is c + d*r or d + c*r with |r| &lt;= 1 and no cancellation). Then
///   <c>+0 - z</c> is +0 for z = +0 or -0 in round-to-nearest. So the position stays (+0, +0).</item>
/// <item><b>Inside the pattern every value is the dense value, bit for bit.</b> The same steps k in the same order; the
///   same rows (stored (i,k) with yik != 0, every other row has yik = (+0, +0) by (1) and is skipped in both); for each
///   such row every stored (i,j) with j active and j != k receives <c>w[i,j] -= yik * w[k,j] / pivot</c> with the same
///   operands: w[k,j] is the stored value, or (+0, +0) by (1) when (k,j) is outside the pattern, and that zero term is
///   applied here too, as the dense code applies it (it can turn a -0 component into +0, so it is not skipped). Each
///   entry is updated once per step and only from row k and column k, which that step never changes, so the order in
///   which the entries of a step are visited does not matter (the same argument the dense code's R126 comment makes).
///   A fill entry starts at (+0, +0), the dense value by (1), and receives the same update.</item>
/// <item><b>The pivot test and its failure are the same.</b> The matrix scale is the largest entry magnitude over the
///   input (the positions outside the input are zeros and cannot raise a maximum; with every input finite the maximum
///   does not depend on the visiting order), so the floor is the same double; the pivot is the same value by (2), so
///   the same step fails, with the same message (<see cref="KronReduction.NearSingularPivotMessage"/>).</item>
/// </list>
/// <para><b>Where the proof needs finite numbers, the dense code itself is run.</b> Step (1) needs yik and the pivot
/// finite. If any input entry is not finite (NaN or infinite), or an elimination step meets a non-finite pivot or a
/// non-finite yik (an overflow), the sparse attempt is abandoned and the matrix is reduced by
/// <see cref="KronReduction.Reduce"/> on the dense array built from the same cells: the answer is then the dense answer
/// by construction. No admittance GridSim builds from a valid model takes that path.</para>
///
/// <para><b>Cost.</b> Work and memory follow the fill-in of the elimination in the given (natural) order, which is not
/// reordered: a fill-reducing order (minimum degree, as the sparse LU uses) would change which nodes are eliminated first
/// and so the rounding of every reduced value, and is therefore not used. On a network the fill is far below n x n in
/// the early steps; the last steps, among the kept nodes' neighbours, can densify, and in the worst case (a matrix that
/// fills completely) the cost is that of the dense reduction plus the bookkeeping. Measure at the batch.</para>
/// </summary>
public static class SparseKronReduction
{
    /// <summary>
    /// Reduce the n x n matrix whose entries are <paramref name="cells"/> (keyed (row, column) by matrix index; every
    /// position without a cell is (+0, +0), exactly as in a freshly allocated dense array) to the nodes in
    /// <paramref name="keepIndices"/>, eliminating the rest. Returns the reduced matrix in <paramref name="keepIndices"/>
    /// order: bit for bit what <see cref="KronReduction.Reduce"/> returns for the dense array of the same cells, and the
    /// same exception (type and message) when that fails.
    /// </summary>
    public static Complex[,] Reduce(int n, IReadOnlyDictionary<(int Row, int Col), Complex> cells, IReadOnlyList<int> keepIndices)
    {
        ArgumentNullException.ThrowIfNull(cells);
        ArgumentNullException.ThrowIfNull(keepIndices);
        if (n < 0) throw new ArgumentOutOfRangeException(nameof(n), $"The matrix size must be 0 or more (got {n}).");

        // The dense reduction's own argument checks, in its order and with its exact exceptions.
        var keepSet = new HashSet<int>(keepIndices);
        if (keepSet.Count != keepIndices.Count) throw new ArgumentException("keepIndices must be distinct.");
        foreach (int k in keepIndices)
            if (k < 0 || k >= n) throw new ArgumentOutOfRangeException(nameof(keepIndices), $"index {k} out of range");

        // A cell outside the matrix is a caller error (the dense array could not have held it): fail loud.
        bool allFinite = true;
        double aNorm = 0.0;
        foreach (KeyValuePair<(int Row, int Col), Complex> cell in cells)
        {
            (int r, int c) = cell.Key;
            if (r < 0 || r >= n || c < 0 || c >= n)
                throw new ArgumentOutOfRangeException(nameof(cells), $"cell ({r}, {c}) lies outside the {n} x {n} matrix");
            Complex v = cell.Value;
            if (!IsFinite(v)) { allFinite = false; continue; }
            // The dense code takes the largest magnitude over every entry; the entries without a cell are zeros.
            double mag = v.Magnitude;
            if (mag > aNorm) aNorm = mag;
        }
        // Any NaN or infinity in the input: the proof does not cover it, so the dense code answers.
        if (!allFinite) return DenseReduce(n, cells, keepIndices);
        double pivotFloor = GridSim.Core.Numerics.NumericTolerances.PivotFloor(aNorm);   // the dense code's floor, same double

        Complex[,]? reduced = TryReduceFinite(n, cells, keepIndices, keepSet, pivotFloor, aNorm);
        return reduced ?? DenseReduce(n, cells, keepIndices);
    }

    /// <summary>The dense reduction of the same cells: the fallback where the proof needs finite numbers it does not have.</summary>
    private static Complex[,] DenseReduce(int n, IReadOnlyDictionary<(int Row, int Col), Complex> cells, IReadOnlyList<int> keepIndices)
    {
        var y = new Complex[n, n];
        foreach (KeyValuePair<(int Row, int Col), Complex> cell in cells) y[cell.Key.Row, cell.Key.Col] = cell.Value;
        return KronReduction.Reduce(y, keepIndices);
    }

    private static bool IsFinite(Complex v) => double.IsFinite(v.Real) && double.IsFinite(v.Imaginary);

    /// <summary>
    /// The elimination on the pattern, or null when a step meets a non-finite pivot or multiplier (the caller then runs
    /// the dense code). Throws the dense code's exception for a near-singular pivot.
    /// </summary>
    private static Complex[,]? TryReduceFinite(int n, IReadOnlyDictionary<(int Row, int Col), Complex> cells,
        IReadOnlyList<int> keepIndices, HashSet<int> keepSet, double pivotFloor, double aNorm)
    {
        // ---- The symmetric pattern: every cell, its mirror and every diagonal, each position once per row. ----
        var neighbours = new List<int>[n];
        for (int i = 0; i < n; i++) neighbours[i] = new List<int> { i };   // the diagonal is always stored
        foreach (KeyValuePair<(int Row, int Col), Complex> cell in cells)
        {
            (int r, int c) = cell.Key;
            if (r == c) continue;
            neighbours[r].Add(c);
            neighbours[c].Add(r);
        }
        var cols = new List<int>[n];
        var vals = new List<Complex>[n];
        var seenAt = new long[n];          // seenAt[j] == visit: column j already placed in the row being visited
        long visit = 0;
        for (int i = 0; i < n; i++)
        {
            visit++;
            var ci = new List<int>(neighbours[i].Count);
            var vi = new List<Complex>(neighbours[i].Count);
            foreach (int j in neighbours[i])
            {
                if (seenAt[j] == visit) continue;
                seenAt[j] = visit;
                ci.Add(j);
                // The dense array's value: the cell, or the default (+0, +0) of a freshly allocated array.
                vi.Add(cells.TryGetValue((i, j), out Complex v) ? v : default);
            }
            cols[i] = ci;
            vals[i] = vi;
            neighbours[i] = null!;   // no longer needed
        }

        // ---- The elimination: the non-kept nodes in ascending index order, as the dense code. ----
        var eliminated = new bool[n];
        var rowK = new Complex[n];         // row k's values, read through the marker below
        var rowKMark = new long[n];        // rowKMark[j] == step: rowK[j] holds w[k,j] for this step
        long step = 0;
        var colsK = new List<int>();       // the active columns of row k (j != k): also the rows to update
        for (int k = 0; k < n; k++)
        {
            if (keepSet.Contains(k)) continue;
            step++;

            // Row k: the pivot and the active entries (the step reads them and never changes them).
            List<int> ck = cols[k];
            List<Complex> vk = vals[k];
            Complex pivot = default;
            colsK.Clear();
            for (int a = 0; a < ck.Count; a++)
            {
                int j = ck[a];
                if (j == k) { pivot = vk[a]; continue; }
                if (eliminated[j]) continue;
                rowK[j] = vk[a];
                rowKMark[j] = step;
                colsK.Add(j);
            }

            // A non-finite pivot is outside the proof: let the dense code answer (it does not stop on it either).
            if (!IsFinite(pivot)) return null;
            if (pivot.Magnitude <= pivotFloor)
                throw new InvalidOperationException(KronReduction.NearSingularPivotMessage(k, pivot.Magnitude, pivotFloor, aNorm));

            // Every row i with a stored (i,k) (the columns of row k, by symmetry). Rows outside are skipped by the dense
            // code too: their w[i,k] is (+0, +0).
            foreach (int i in colsK)
            {
                List<int> ci = cols[i];
                List<Complex> vi = vals[i];

                // w[i,k], the multiplier (stored by symmetry).
                Complex yik = default;
                for (int a = 0; a < ci.Count; a++) if (ci[a] == k) { yik = vi[a]; break; }
                bool update = yik != Complex.Zero;   // the dense code's test: a zero multiplier skips the whole row
                if (update && !IsFinite(yik)) return null;   // outside the proof: the dense code answers

                // One pass over row i: drop the eliminated columns and k (never read again), and apply the update to every
                // other stored entry, with w[k,j] = (+0, +0) where (k,j) is not stored (proof, item 2).
                visit++;
                int w = 0;
                for (int a = 0; a < ci.Count; a++)
                {
                    int j = ci[a];
                    if (j == k || eliminated[j]) continue;
                    Complex wij = vi[a];
                    if (update)
                    {
                        Complex wkj = rowKMark[j] == step ? rowK[j] : default;
                        wij -= yik * wkj / pivot;   // the dense code's expression, operand for operand
                    }
                    seenAt[j] = visit;
                    ci[w] = j;
                    vi[w] = wij;
                    w++;
                }
                ci.RemoveRange(w, ci.Count - w);
                vi.RemoveRange(w, vi.Count - w);

                // The fill: every active column of row k not yet in row i. Its dense value is (+0, +0) (proof, item 1),
                // which then receives the same update. Kept even when the row is skipped (as (+0, +0)), so the pattern
                // stays symmetric.
                foreach (int j in colsK)
                {
                    if (seenAt[j] == visit) continue;
                    Complex wij = default;
                    if (update) wij -= yik * rowK[j] / pivot;
                    ci.Add(j);
                    vi.Add(wij);
                }
            }

            eliminated[k] = true;
            cols[k] = null!;   // row k is never read again
            vals[k] = null!;
        }

        // ---- The reduced matrix among the kept nodes, in keepIndices order. ----
        int m = keepIndices.Count;
        var posOfKept = new int[n];
        for (int i = 0; i < n; i++) posOfKept[i] = -1;
        for (int b = 0; b < m; b++) posOfKept[keepIndices[b]] = b;
        var reduced = new Complex[m, m];   // positions outside the pattern stay (+0, +0), the dense value
        for (int a = 0; a < m; a++)
        {
            List<int> ci = cols[keepIndices[a]];
            List<Complex> vi = vals[keepIndices[a]];
            for (int t = 0; t < ci.Count; t++)
            {
                int b = posOfKept[ci[t]];
                if (b >= 0) reduced[a, b] = vi[t];   // an eliminated column left in a kept row is not part of the result
            }
        }
        return reduced;
    }
}
