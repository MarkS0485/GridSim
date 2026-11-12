using System.Numerics;
using GridSim.Core.Numerics;
using Xunit;

namespace GridSim.Tests;

/// <summary>
/// GR-033: GeneralEigen checked only biorthonormality (true for ANY invertible V) and, for repeated-eigenvalue clusters,
/// the eigen-equation residual. A plain inverse iteration left unconverged at its sweep cap was returned as an
/// eigenvector. Every pair's residual ||A v - lambda v|| is now checked at the cluster bound and reported on the pair.
/// </summary>
public class Gr033EigenpairResidualTests
{
    // A small non-symmetric matrix with distinct real and complex eigenvalues.
    private static double[,] Sample() => new double[,]
    {
        { -1.0, 2.0, 0.5 },
        { -3.0, -0.5, 1.0 },
        { 0.2, 0.4, -2.0 },
    };

    /// <summary>The same residual expression GeneralEigen uses, in the same order, so the comparison is exact.</summary>
    private static double Residual(double[,] a, Complex lambda, Complex[] v)
    {
        int n = a.GetLength(0);
        double s = 0.0;
        for (int i = 0; i < n; i++)
        {
            Complex r = -lambda * v[i];
            for (int j = 0; j < n; j++) r += a[i, j] * v[j];
            s += r.Real * r.Real + r.Imaginary * r.Imaginary;
        }
        return Math.Sqrt(s);
    }

    [Fact]
    public void Gr033_every_pair_reports_its_own_eigen_equation_residual()
    {
        double[,] a = Sample();
        IReadOnlyList<GeneralEigen.Eigenpair> pairs = GeneralEigen.Decompose(a);
        Assert.Equal(3, pairs.Count);
        foreach (GeneralEigen.Eigenpair p in pairs)
        {
            Assert.Equal(Residual(a, p.Value, p.Right), p.Residual);   // exact: the reported value is the checked one
            Assert.True(p.Residual <= 1e-8, $"mode {p.Value}: residual {p.Residual:E3}");
        }
    }

    [Fact]
    public void Gr033_an_unconverged_inverse_iteration_fails_loud_instead_of_returning_the_start_vector()
    {
        // Zero inverse-iteration sweeps returns the fixed start vector for every mode: not an eigenvector. Before GR-033
        // it passed biorthonormality (V was invertible) and came back as a decomposition.
        var ex = Assert.Throws<GeneralEigen.DefectiveEigenproblemException>(() => GeneralEigen.Decompose(Sample(), inverseIterations: 0));
        Assert.Contains("inverse iteration did not converge", ex.Message);
    }
}
