using GridSim.Core.Numerics;
using Xunit;

namespace GridSim.Tests;

/// <summary>
/// R114 (October 2026 review): the EMT engines solved every step through SparseLuFactorization.Solve, which allocates
/// two fresh n-vectors, and re-derived the RCM ordering on every refactor. They now solve into reused buffers
/// (SolveInto) and reuse the ordering while the CSR pattern is unchanged. Both must be bit-identical.
/// </summary>
public class SparseSolveIntoBitIdentityTests
{
    private static CsrMatrix Laplacian(int n, int seed)
    {
        // A diagonally dominant random sparse matrix (an EMT-like nodal conductance matrix).
        var b = new CsrMatrix.Builder(n);
        ulong s = (ulong)(seed * 2654435761u + 3);
        double Next() { s = s * 6364136223846793005UL + 1442695040888963407UL; return ((s >> 11) * (1.0 / (1UL << 53))) + 0.1; }
        var diag = new double[n];
        for (int i = 0; i < n; i++)
            for (int k = 1; k <= 3; k++)
            {
                int j = (i + k * 7 + seed) % n;
                if (j == i) continue;
                double g = Next();
                b.Add(i, j, -g); b.Add(j, i, -g);
                diag[i] += g; diag[j] += g;
            }
        for (int i = 0; i < n; i++) b.Add(i, i, diag[i] + 0.05);
        return b.Build();
    }

    [Theory]
    [InlineData(10, 1)]
    [InlineData(57, 2)]
    [InlineData(300, 3)]
    public void SolveInto_is_bit_identical_to_Solve_and_cached_ordering_to_a_fresh_one(int n, int seed)
    {
        CsrMatrix a = Laplacian(n, seed);
        var rhs = new double[n];
        for (int i = 0; i < n; i++) rhs[i] = Math.Sin(i + seed) * 3.0;

        SparseLuFactorization fresh = SparseLu.Factor(a)!;
        SparseLuFactorization reused = SparseLu.FactorWithOrdering(a, SparseLu.Ordering(a))!;
        double[] want = fresh.Solve(rhs);
        var x = new double[n];
        var work = new double[n];
        for (int round = 0; round < 3; round++)   // reused buffers carry stale values between rounds
        {
            reused.SolveInto(rhs, x, work);
            for (int i = 0; i < n; i++)
                Assert.Equal(BitConverter.DoubleToInt64Bits(want[i]), BitConverter.DoubleToInt64Bits(x[i]));
        }
    }

    [Fact]
    public void SolveInto_refuses_short_buffers()
    {
        SparseLuFactorization f = SparseLu.Factor(Laplacian(10, 4))!;
        Assert.Throws<ArgumentException>(() => f.SolveInto(new double[10], new double[9], new double[10]));
    }
}
