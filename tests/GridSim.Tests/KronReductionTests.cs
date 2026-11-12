using System.Numerics;
using GridSim.Core.PowerFlow;
using Xunit;

namespace GridSim.Tests;

/// <summary>
/// Kron (star-mesh) reduction on hand-verifiable networks. Eliminating the centre of a symmetric
/// 3-leg star (each leg admittance y) must yield a delta with 2y/3 on the diagonal and -y/3 off it.
/// </summary>
public class KronReductionTests
{
    private static void AssertClose(Complex expected, Complex actual, double tol = 1e-12)
    {
        Assert.True((expected - actual).Magnitude < tol, $"expected {expected}, got {actual}");
    }

    [Fact]
    public void Symmetric_star_reduces_to_a_delta()
    {
        var y = new Complex(0.0, -2.0);   // a leg admittance (e.g. 1/(jX))
        // Nodes 0,1,2 are the outer (kept) buses; node 3 is the centre to eliminate.
        var Y = new Complex[4, 4];
        for (int i = 0; i < 3; i++)
        {
            Y[i, i] = y;
            Y[i, 3] = -y;
            Y[3, i] = -y;
        }
        Y[3, 3] = 3.0 * y;

        Complex[,] r = KronReduction.Reduce(Y, new[] { 0, 1, 2 });

        Complex diag = 2.0 * y / 3.0;
        Complex off = -y / 3.0;
        for (int i = 0; i < 3; i++)
            for (int j = 0; j < 3; j++)
                AssertClose(i == j ? diag : off, r[i, j]);
    }

    [Fact]
    public void Eliminating_a_near_isolated_bus_throws_rather_than_emitting_infinities()
    {
        // Node 2 (to be eliminated) has a self-admittance ~1e-13 of its own row's scale - near-singular.
        // Dividing the fold-in by it would blow the reduced admittances up, so the relative near-singular
        // guard must reject it (not only the exact-zero case).
        // GR-044 test correction (approved): the node used to be tied by j1e-13 with a self-admittance of -j1e-13, which
        // a whole-matrix floor refused only because of units (the reduction is finite: Y00' = -j10 + j1e-13). A
        // whole-matrix absolute pivot floor is not invariant under row scaling; the row-relative floor (1e-12 x the
        // pivot row's own largest entry) refuses exactly the rows that are singular at rounding level relative to their
        // own scale. The intent is kept with a tie of j1 and a self-admittance of -j1e-13 (pivot 1e-13 of its row);
        // the old matrix now reduces, pinned by Gr044RowRelativePivotFloorTests.
        var Y = new Complex[3, 3];
        Y[0, 0] = new Complex(0, -10); Y[1, 1] = new Complex(0, -10);
        Y[0, 1] = new Complex(0, 5); Y[1, 0] = new Complex(0, 5);
        Y[0, 2] = new Complex(0, 1); Y[2, 0] = new Complex(0, 1);
        Y[2, 2] = new Complex(0, -1e-13);
        Assert.Throws<InvalidOperationException>(() => KronReduction.Reduce(Y, new[] { 0, 1 }));
    }

    [Fact]
    public void Keeping_every_bus_returns_the_original_matrix()
    {
        var Y = new Complex[2, 2]
        {
            { new Complex(1, -3), new Complex(-1, 3) },
            { new Complex(-1, 3), new Complex(1, -3) },
        };
        Complex[,] r = KronReduction.Reduce(Y, new[] { 0, 1 });
        for (int i = 0; i < 2; i++)
            for (int j = 0; j < 2; j++)
                AssertClose(Y[i, j], r[i, j]);
    }
}
