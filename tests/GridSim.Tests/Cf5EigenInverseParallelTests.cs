using System;
using System.Numerics;
using GridSim.Core.Numerics;
using Xunit;

namespace GridSim.Tests;

/// <summary>
/// CF5 (N197): the eigenvector-matrix inverse behind participation factors eliminates its rows on several threads at
/// large orders. Every worker count must give the same bits as the sequential loop, and the result must be the inverse.
/// </summary>
public class Cf5EigenInverseParallelTests
{
    // A deterministic, well-conditioned complex matrix (diagonally weighted so no pivot is near zero).
    private static Complex[,] Matrix(int n, int seed)
    {
        var rng = new Random(seed);
        var m = new Complex[n, n];
        for (int i = 0; i < n; i++)
            for (int j = 0; j < n; j++)
                m[i, j] = new Complex(rng.NextDouble() - 0.5, rng.NextDouble() - 0.5) + (i == j ? new Complex(n * 0.1, 0.0) : Complex.Zero);
        return m;
    }

    [Theory]
    [InlineData(300, 2)]
    [InlineData(300, 4)]
    [InlineData(300, 7)]
    [InlineData(64, 3)]
    public void Every_worker_count_gives_the_sequential_bits(int n, int workers)
    {
        Complex[,] a = Matrix(n, 20261011 + n);
        Complex[,] seq = GeneralEigen.InvertComplexWith(a, n, 1)!;
        Complex[,] par = GeneralEigen.InvertComplexWith(a, n, workers)!;
        Assert.NotNull(seq);
        Assert.NotNull(par);
        for (int i = 0; i < n; i++)
            for (int j = 0; j < n; j++)
            {
                Assert.Equal(BitConverter.DoubleToInt64Bits(seq[i, j].Real), BitConverter.DoubleToInt64Bits(par[i, j].Real));
                Assert.Equal(BitConverter.DoubleToInt64Bits(seq[i, j].Imaginary), BitConverter.DoubleToInt64Bits(par[i, j].Imaginary));
            }
    }

    [Fact]
    public void The_parallel_inverse_is_the_inverse()
    {
        const int n = 300;
        Complex[,] a = Matrix(n, 7);
        Complex[,] inv = GeneralEigen.InvertComplexWith(a, n, 4)!;
        double worst = 0.0;
        for (int i = 0; i < n; i++)
            for (int j = 0; j < n; j++)
            {
                Complex s = Complex.Zero;
                for (int k = 0; k < n; k++) s += a[i, k] * inv[k, j];
                worst = Math.Max(worst, (s - (i == j ? Complex.One : Complex.Zero)).Magnitude);
            }
        Assert.True(worst < 1e-12, $"max |A inv - I| = {worst:E3}");
    }

    [Fact]
    public void A_singular_matrix_is_refused_on_every_path()
    {
        const int n = 260;
        Complex[,] a = Matrix(n, 3);
        for (int j = 0; j < n; j++) a[5, j] = a[9, j];   // two equal rows
        Assert.Null(GeneralEigen.InvertComplexWith(a, n, 1));
        Assert.Null(GeneralEigen.InvertComplexWith(a, n, 4));
    }

    [Fact]
    public void The_threshold_is_where_the_parallel_path_starts()
    {
        Assert.Equal(256, GeneralEigen.ParallelInverseMinOrder);
    }
}
