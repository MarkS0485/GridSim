using GridSim.Core.Numerics;
using Xunit;

namespace GridSim.Tests;

/// <summary>
/// DOC-6 (October 2026), D-DOC-026 (docs-session finding, NUM): <see cref="CsrMatrix.Builder"/> sorted each row by column
/// alone with Array.Sort, an introsort that does not keep equal keys in order, so the summation order of a duplicated
/// cell in a row of more than 16 entries was set by the sort's pivots. It now sorts by (column, insertion index), so a
/// duplicated cell is always summed left to right in the order its entries were added. These tests pin that order
/// against an independent reference (a plain left-to-right fold per cell), bit for bit.
/// </summary>
public sealed class Doc6CsrStableOrderTests
{
    // ---- D-DOC-026: CsrMatrix.Builder sums duplicate cells in insertion order --------------------------------------

    /// <summary>The reference: each cell's entries summed left to right in the order they were added.</summary>
    private static Dictionary<(int, int), double> InsertionOrderSums(IEnumerable<(int R, int C, double V)> entries)
    {
        var sums = new Dictionary<(int, int), double>();
        foreach ((int r, int c, double v) in entries)
            sums[(r, c)] = sums.TryGetValue((r, c), out double s) ? s + v : v;
        return sums;
    }

    [Fact]
    public void D026_duplicate_cells_are_summed_in_insertion_order_in_long_rows()
    {
        // Rows of 17 to 80 entries over a few columns, so every cell is duplicated many times, with values whose sum
        // depends on the grouping (1e16 + 1 - 1e16 + 1 is 1 left to right, 0 or 2 in other orders). Under the old
        // column-only key the introsort's pivots decided the grouping; the stable (column, insertion) key gives
        // exactly the left-to-right sum of the reference, bit for bit, every time.
        var rng = new Random(20261005);
        double[] palette = { 1e16, 1.0, -1e16, 3.0, -2.5e15, 0.1, 7e-3, -1.0 };
        int differs = 0;
        for (int trial = 0; trial < 400; trial++)
        {
            int n = 1 + rng.Next(4);
            var entries = new List<(int R, int C, double V)>();
            for (int r = 0; r < n; r++)
            {
                int len = 17 + rng.Next(64);
                for (int k = 0; k < len; k++) entries.Add((r, rng.Next(Math.Min(n, 3) + 3) % n, palette[rng.Next(palette.Length)]));
            }
            var b = new CsrMatrix.Builder(n);
            foreach (var (r, c, v) in entries) b.Add(r, c, v);
            CsrMatrix m = b.Build();
            Dictionary<(int, int), double> expected = InsertionOrderSums(entries);
            for (int r = 0; r < n; r++)
                for (int k = m.RowPtr[r]; k < m.RowPtr[r + 1]; k++)
                {
                    double want = expected.TryGetValue((r, m.ColIdx[k]), out double w) ? w : 0.0;   // an injected diagonal is 0
                    Assert.Equal(BitConverter.DoubleToInt64Bits(want), BitConverter.DoubleToInt64Bits(m.Val[k]));
                }
            // How often a different (shuffled) order would have changed a sum: shows the test can see the difference.
            var shuffled = entries.OrderBy(_ => rng.Next()).ToList();
            Dictionary<(int, int), double> other = InsertionOrderSums(shuffled);
            if (expected.Any(kv => BitConverter.DoubleToInt64Bits(kv.Value) != BitConverter.DoubleToInt64Bits(other[kv.Key]))) differs++;
        }
        Assert.True(differs > 50, $"the palette should make the order visible; only {differs} of 400 trials differed");
    }

    [Fact]
    public void D026_a_three_entry_row_keeps_insertion_order()
    {
        // The 3-entry case the old comment got wrong: keys (1, 1, 0) go through a three-comparison network. Two
        // duplicates cannot change a sum, so this pins the order through the column pattern and the diagonal pointer.
        var b = new CsrMatrix.Builder(2);
        b.Add(1, 1, 0.1); b.Add(1, 1, 0.2); b.Add(1, 0, -0.3);
        CsrMatrix m = b.Build();
        Assert.Equal(new[] { 0, 1 }, m.ColIdx[m.RowPtr[1]..m.RowPtr[2]]);
        Assert.Equal(BitConverter.DoubleToInt64Bits(0.1 + 0.2), BitConverter.DoubleToInt64Bits(m.Val[m.DiagPtr[1]]));
    }
}
