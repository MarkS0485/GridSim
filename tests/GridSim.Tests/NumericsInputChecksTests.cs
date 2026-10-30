using GridSim.Core.Numerics;
using Xunit;

namespace GridSim.Tests;

/// <summary>
/// Input and result checks in the numerics layer: ILU(0) names a missing diagonal (GR-461), the prepared sparse LU
/// refuses an ordering that is not a permutation and an out-of-range row index (GR-462).
/// </summary>
public class NumericsInputChecksTests
{
    [Fact]
    public void Ilu0_names_the_row_with_no_stored_diagonal()
    {
        var b = new CsrMatrix.Builder(2);
        b.Add(0, 0, 2.0);
        b.Add(0, 1, 1.0);
        b.Add(1, 0, 1.0);   // row 1 has no diagonal entry
        CsrMatrix a = b.Build(ensureDiagonal: false);
        ArgumentException ex = Assert.Throws<ArgumentException>(() => Ilu0.Factor(a));
        Assert.Contains("row 1", ex.Message, StringComparison.Ordinal);
    }

    // The 2 x 2 matrix [[4, 1], [1, 3]] in CSC (symmetric, so CSR and CSC agree).
    private static (int[] Ap, int[] Ai, double[] Ax) Csc() => (new[] { 0, 2, 4 }, new[] { 0, 1, 0, 1 }, new[] { 4.0, 1.0, 1.0, 3.0 });

    [Fact]
    public void The_prepared_sparse_LU_factors_a_valid_input()
    {
        (int[] ap, int[] ai, double[] ax) = Csc();
        Assert.NotNull(SparseLu.FactorPrepared(2, ap, ai, ax, new[] { 1, 0 }));
    }

    [Fact]
    public void The_prepared_sparse_LU_refuses_an_ordering_that_is_not_a_permutation()
    {
        (int[] ap, int[] ai, double[] ax) = Csc();
        Assert.Throws<ArgumentException>(() => SparseLu.FactorPrepared(2, ap, ai, ax, new[] { 0, 0 }));
        Assert.Throws<ArgumentException>(() => SparseLu.FactorPrepared(2, ap, ai, ax, new[] { 0, 2 }));
        Assert.Throws<ArgumentException>(() => SparseLu.FactorPrepared(2, ap, ai, ax, new[] { 0 }));
    }

    [Fact]
    public void The_prepared_sparse_LU_refuses_an_out_of_range_row_index()
    {
        (int[] ap, int[] ai, double[] ax) = Csc();
        ai[3] = 2;
        Assert.Throws<ArgumentException>(() => SparseLu.FactorPrepared(2, ap, ai, ax, new[] { 0, 1 }));
    }
}
