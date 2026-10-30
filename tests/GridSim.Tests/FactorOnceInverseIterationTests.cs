using System.Numerics;
using GridSim.Core.Numerics;
using Xunit;

namespace GridSim.Tests;

/// <summary>
/// R124 (October 2026 review): GeneralEigen's inverse iteration refactorised the same shifted matrix on every one of
/// its 40 iterations. It now factors once and back-substitutes. That is only acceptable if the factor-once solve is
/// BIT-IDENTICAL to the one-shot SolveDense on the same matrix and right-hand side, through a whole inverse-iteration
/// sequence (each step's normalised output is the next step's right-hand side). This pins it on shifted matrices that
/// sit as close to singular as inverse iteration makes them.
/// </summary>
public class FactorOnceInverseIterationTests
{
    private static double[,] Matrix(int n, int seed)
    {
        var a = new double[n, n];
        ulong s = (ulong)(seed * 2654435761u + 7);
        for (int i = 0; i < n; i++)
            for (int j = 0; j < n; j++)
            {
                s = s * 6364136223846793005UL + 1442695040888963407UL;
                a[i, j] = ((s >> 11) * (1.0 / (1UL << 53))) * 2.0 - 1.0;
            }
        return a;
    }

    private static void Normalize(Complex[] v)
    {
        double norm = Math.Sqrt(v.Sum(x => x.Real * x.Real + x.Imaginary * x.Imaginary));
        for (int i = 0; i < v.Length; i++) v[i] /= norm;
    }

    [Theory]
    [InlineData(4, 1)]
    [InlineData(9, 2)]
    [InlineData(16, 3)]
    public void Factor_once_back_substitution_is_bit_identical_to_one_shot_solves(int n, int seed)
    {
        double[,] a = Matrix(n, seed);
        // Shift a hair off a genuine eigenvalue, exactly as inverse iteration does.
        Complex mu = GeneralEigen.Eigenvalues(a)[n / 2];
        Complex shift = mu + new Complex(1e-10, 3e-11);
        var m = new Complex[n, n];
        for (int i = 0; i < n; i++)
            for (int j = 0; j < n; j++) m[i, j] = new Complex(a[i, j], 0.0) - (i == j ? shift : Complex.Zero);

        var vOld = new Complex[n];
        for (int i = 0; i < n; i++) vOld[i] = new Complex(1.0 + 0.1 * i, 0.01 * (i + 1));
        Normalize(vOld);
        var vNew = (Complex[])vOld.Clone();
        DenseComplexLu lu = DenseComplexLuSolver.FactorDense(m, n);

        for (int it = 0; it < 8; it++)
        {
            Complex[] wOld = DenseComplexLuSolver.SolveDense(m, vOld, n);
            Complex[] wNew = lu.Solve(vNew);
            for (int i = 0; i < n; i++)
            {
                Assert.Equal(BitConverter.DoubleToInt64Bits(wOld[i].Real), BitConverter.DoubleToInt64Bits(wNew[i].Real));
                Assert.Equal(BitConverter.DoubleToInt64Bits(wOld[i].Imaginary), BitConverter.DoubleToInt64Bits(wNew[i].Imaginary));
            }
            Normalize(wOld); Normalize(wNew);
            vOld = wOld; vNew = wNew;
        }
    }

    [Fact]
    public void Inverse_iteration_factors_once()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "GridSim.slnx"))) dir = dir.Parent;
        string src = File.ReadAllText(Path.Combine(dir!.FullName, "src", "GridSim.Core", "Numerics", "GeneralEigen.cs"));
        Assert.Contains("DenseComplexLu lu = DenseComplexLuSolver.FactorDense(m, n);", src);
        Assert.DoesNotContain("w = DenseComplexLuSolver.SolveDense(m, v, n);", src);
    }
}
