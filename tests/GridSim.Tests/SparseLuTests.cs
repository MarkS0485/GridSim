using GridSim.Core.Numerics;
using Xunit;

namespace GridSim.Tests;

public class SparseLuTests
{
    private static (CsrMatrix A, double[] b, double[] xTrue) RandomSparse(int n, int seed, bool diagDominant)
    {
        var rng = new Random(seed);
        var bld = new CsrMatrix.Builder(n);
        // dense reference for residual
        var dense = new double[n, n];
        for (int i = 0; i < n; i++)
        {
            double rowAbs = 0;
            int offdiag = rng.Next(2, 7);
            for (int k = 0; k < offdiag; k++)
            {
                int j = rng.Next(n);
                if (j == i) continue;
                double v = rng.NextDouble() * 2 - 1;
                bld.Add(i, j, v); dense[i, j] += v; rowAbs += Math.Abs(v);
            }
            double diag = diagDominant ? rowAbs + 1 + rng.NextDouble() : (rng.NextDouble() * 2 - 1) + (rng.Next(2) == 0 ? 2 : -2);
            bld.Add(i, i, diag); dense[i, i] += diag;
        }
        CsrMatrix A = bld.Build();
        var xTrue = new double[n];
        for (int i = 0; i < n; i++) xTrue[i] = rng.NextDouble() * 10 - 5;
        var b = new double[n];
        for (int i = 0; i < n; i++) { double s = 0; for (int j = 0; j < n; j++) s += dense[i, j] * xTrue[j]; b[i] = s; }
        return (A, b, xTrue);
    }

    [Theory]
    [InlineData(50, true)]
    [InlineData(200, true)]
    [InlineData(200, false)]
    [InlineData(500, false)]
    public void Solves_random_sparse_systems(int n, bool diagDominant)
    {
        (CsrMatrix A, double[] b, double[] xTrue) = RandomSparse(n, 1234 + n, diagDominant);
        double[]? x = SparseLu.Solve(A, b);
        Assert.NotNull(x);

        // residual ||A x - b|| / ||b||
        var ax = new double[n];
        A.Multiply(x!, ax);
        double rn = 0, bn = 0, xerr = 0;
        for (int i = 0; i < n; i++) { rn += (ax[i] - b[i]) * (ax[i] - b[i]); bn += b[i] * b[i]; xerr = Math.Max(xerr, Math.Abs(x![i] - xTrue[i])); }
        double rel = Math.Sqrt(rn / Math.Max(bn, 1e-30));
        Assert.True(rel < 1e-8, $"residual {rel:E2}, max x err {xerr:E2}");
    }
}
