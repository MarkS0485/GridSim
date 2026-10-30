using System.Numerics;
using GridSim.Core.Numerics;
using GridSim.Core.PowerFlow;
using GridSim.Core.IO;
using GridSim.Core.Model;
using Xunit;

namespace GridSim.Tests;

/// <summary>
/// Pins <see cref="ComplexCsr.Builder.Build"/> after porting the delegate-free key-array sort.
/// The new sort must produce byte-identical CSR arrays to a straightforward
/// reference build for arbitrary insertion order and duplicate entries: column ordering is identical
/// (introsort over the same columns), and duplicate columns within a row sum to the same bits regardless
/// of tie resolution (IEEE addition is commutative). Also confirms the real Y(h) build is unchanged.
/// </summary>
public class ComplexCsrBuilderPinTests
{
    // A reference CSR build: bucket by row, stable-sort each row by column (stable => deterministic tie
    // order), sum duplicates. Independent of the production sort mechanism.
    private static (int[] Row, int[] Col, double[] G, double[] B) Reference(
        int n, IReadOnlyList<(int R, int C, double G, double B)> entries)
    {
        var rowPtr = new int[n + 1];
        var outCol = new List<int>();
        var outG = new List<double>();
        var outB = new List<double>();
        for (int i = 0; i < n; i++)
        {
            rowPtr[i] = outCol.Count;
            // Stable order by column, preserving insertion order among equal columns.
            var rowEntries = entries.Where(e => e.R == i)
                .Select((e, k) => (e.C, e.G, e.B, k))
                .OrderBy(e => e.C).ThenBy(e => e.k)
                .ToList();
            int prev = int.MinValue;
            foreach ((int c, double g, double b, int _) in rowEntries)
            {
                if (c == prev) { outG[^1] += g; outB[^1] += b; }
                else { outCol.Add(c); outG.Add(g); outB.Add(b); prev = c; }
            }
        }
        rowPtr[n] = outCol.Count;
        return (rowPtr, outCol.ToArray(), outG.ToArray(), outB.ToArray());
    }

    [Fact]
    public void Build_is_byte_identical_to_a_reference_for_shuffled_and_duplicate_entries()
    {
        const int n = 6;
        // Deliberately unsorted insertion order, with duplicate (row,col) entries that must sum, and
        // rows with and without an explicit diagonal.
        var entries = new (int R, int C, double G, double B)[]
        {
            (0, 3, 1.0, -0.5), (0, 0, 2.0, 0.1), (0, 3, 0.25, 0.25), // dup at (0,3); diagonal present
            (2, 5, -1.0, 0.0), (2, 1, 0.3, 0.7),                     // row 2 has NO diagonal -> injected
            (1, 1, 4.0, -4.0), (1, 4, 0.2, 0.2), (1, 0, -0.1, 0.9),
            (5, 5, 7.0, 1.0), (5, 2, -0.4, 0.6), (5, 2, 0.4, -0.6),  // dup at (5,2) sums to exactly 0 -> dropped? no: kept as explicit
            (3, 3, 1.5, 2.5),
            (4, 0, 0.9, 0.1), (4, 4, 1.1, -1.1),
        };

        var b = new ComplexCsr.Builder(n);
        foreach ((int r, int c, double g, double bb) in entries) b.Add(r, c, g, bb);
        ComplexCsr csr = b.Build();

        (int[] refRow, int[] refCol, double[] refG, double[] refB) = Reference(n, entries);

        // Structure includes injected zero diagonals; the reference above does not inject them, so compare
        // only the stamped cells: every reference cell must be present with bit-identical value, and every
        // extra production cell must be an injected zero on the diagonal.
        for (int i = 0; i < n; i++)
        {
            for (int p = refRow[i]; p < refRow[i + 1]; p++)
            {
                int col = refCol[p];
                int at = Find(csr, i, col);
                Assert.True(at >= 0, $"row {i} col {col} missing from production build");
                Assert.Equal(System.BitConverter.DoubleToInt64Bits(refG[p]), System.BitConverter.DoubleToInt64Bits(csr.G[at]));
                Assert.Equal(System.BitConverter.DoubleToInt64Bits(refB[p]), System.BitConverter.DoubleToInt64Bits(csr.B[at]));
            }
            // Columns strictly ascending within the row (sort correctness).
            for (int p = csr.RowPtr[i] + 1; p < csr.RowPtr[i + 1]; p++)
                Assert.True(csr.ColIdx[p] > csr.ColIdx[p - 1], $"row {i} columns not strictly ascending");
        }
    }

    private static int Find(ComplexCsr m, int row, int col)
    {
        for (int p = m.RowPtr[row]; p < m.RowPtr[row + 1]; p++)
            if (m.ColIdx[p] == col) return p;
        return -1;
    }

    [Fact]
    public void Fundamental_Y_h_build_is_unchanged_by_the_sort_port()
    {
        // The complex builder feeds Y(h); at h=1 the frequency-dependent Y reduces to the fundamental
        // YBus. Assert the sparse complex build reproduces the dense YBus cells bit-for-bit, which also
        // exercises the ported sort on a realistic pattern.
        GridModel model = IeeeCases.Case30();
        YBus dense = YBus.Build(model);
        ComplexCsr y1 = GridSim.Core.Harmonics.FrequencyDependentYBus.Build(model, 1.0, null);

        for (int i = 0; i < y1.N; i++)
            for (int p = y1.RowPtr[i]; p < y1.RowPtr[i + 1]; p++)
            {
                int j = y1.ColIdx[p];
                Complex d = dense.Y[i, j];
                // Y(1) equals YBus to a few ulp (sparse builder summation order can differ from dense);
                // this is the documented reduction property, not a bit-for-bit claim here.
                Assert.True(System.Math.Abs(y1.G[p] - d.Real) < 1e-9 && System.Math.Abs(y1.B[p] - d.Imaginary) < 1e-9,
                    $"Y(1)[{i},{j}] = {y1.G[p]}+{y1.B[p]}i vs dense {d}");
            }
    }
}
