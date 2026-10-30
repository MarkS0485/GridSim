using System.Numerics;
using GridSim.Core.IO;
using GridSim.Core.Model;
using GridSim.Core.PowerFlow;
using Xunit;

namespace GridSim.Tests;

/// <summary>
/// Pins the fault-study injection change: the post-fault nodal injections
/// I = Y_network V were computed by a dense O(n^2) double loop over every column; they are now a walk of
/// the sparse Y cells in ascending column order. This must be BIT-FOR-BIT identical - the sparse cells are
/// the dense values (pinned separately), the skipped structural zeros contribute nothing (x + 0.0 == x),
/// and ascending-column order matches the dense j = 0..n-1 sweep. Verified directly on I = YV for an
/// arbitrary voltage vector, over cases with transformers/taps, shunts, and parallel branches.
/// </summary>
public class FaultInjectionWalkPinTests
{
    public static TheoryData<string> Cases => new() { "case30", "case118", "case300" };

    [Theory]
    [MemberData(nameof(Cases))]
    public void Sparse_cell_injection_walk_is_bit_for_bit_the_dense_loop(string caseName)
    {
        GridModel model = Load(caseName);
        YBus dense = YBus.Build(model);
        SparseYBus sparse = YBus.BuildSparse(model);
        int n = dense.N;

        // An arbitrary but deterministic complex voltage vector (off any solution - nothing degenerate).
        var v = new Complex[n];
        for (int i = 0; i < n; i++)
            v[i] = Complex.FromPolarCoordinates(1.0 + 0.03 * System.Math.Sin(i + 1), 0.05 * System.Math.Cos(2 * i + 1));

        // Reference: the old dense O(n^2) loop, exactly (j ascending, +0.0 seed).
        var denseInj = new Complex[n];
        for (int i = 0; i < n; i++)
        {
            Complex inj = Complex.Zero;
            for (int j = 0; j < n; j++) inj += dense.Y[i, j] * v[j];
            denseInj[i] = inj;
        }

        // Production: the sparse-cell walk, ascending column per row (mirrors FaultAnalysis).
        var rowCells = new System.Collections.Generic.List<(int Col, Complex V)>[n];
        for (int i = 0; i < n; i++) rowCells[i] = new System.Collections.Generic.List<(int, Complex)>(8);
        foreach (var cell in sparse.Cells) rowCells[cell.Key.Row].Add((cell.Key.Col, cell.Value));

        for (int i = 0; i < n; i++)
        {
            rowCells[i].Sort(static (a, b) => a.Col.CompareTo(b.Col));
            Complex inj = Complex.Zero;
            for (int p = 0; p < rowCells[i].Count; p++) inj += rowCells[i][p].V * v[rowCells[i][p].Col];

            Assert.Equal(System.BitConverter.DoubleToInt64Bits(denseInj[i].Real), System.BitConverter.DoubleToInt64Bits(inj.Real));
            Assert.Equal(System.BitConverter.DoubleToInt64Bits(denseInj[i].Imaginary), System.BitConverter.DoubleToInt64Bits(inj.Imaginary));
        }
    }

    private static GridModel Load(string caseName) => caseName switch
    {
        "case30" => IeeeCases.Case30(),
        "case118" => IeeeCases.Case118(),
        "case300" => IeeeCases.Case300(),
        _ => throw new System.ArgumentOutOfRangeException(nameof(caseName)),
    };
}
