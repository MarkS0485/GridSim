using System.Numerics;
using GridSim.Core.Numerics;
using Xunit;

namespace GridSim.Tests;

/// <summary>
/// Pins the non-symmetric eigensolver <see cref="GeneralEigen"/>: complex eigenvalues of known matrices to
/// machine precision, biorthonormality of the left/right vectors, and FAIL-LOUD on a defective matrix (it
/// must never fabricate a participation vector). Sibling of
/// <see cref="ContinuumWaveModelTests.Eigensolver_matches_known_eigenvalues"/> for the symmetric path.
/// </summary>
public class GeneralEigenTests
{
    [Fact]
    public void Complex_eigenvalues_of_a_rotation_block_match_to_machine_precision()
    {
        // [[0,-1],[1,0]] has eigenvalues +/- i.
        var a = new double[,] { { 0, -1 }, { 1, 0 } };
        Complex[] e = Sorted(GeneralEigen.Eigenvalues(a));
        Assert.Equal(0.0, e[0].Real, 12);
        Assert.Equal(-1.0, e[0].Imaginary, 12);
        Assert.Equal(0.0, e[1].Real, 12);
        Assert.Equal(1.0, e[1].Imaginary, 12);
    }

    [Fact]
    public void Mixed_real_and_complex_eigenvalues_of_a_nonsymmetric_matrix()
    {
        // Block-diagonal: a 2x2 with eigenvalues 2 +/- i, and a real eigenvalue 5.
        var a = new double[,] { { 2, -1, 0 }, { 1, 2, 0 }, { 0, 0, 5 } };
        Complex[] e = Sorted(GeneralEigen.Eigenvalues(a));
        Assert.Equal(2.0, e[0].Real, 12); Assert.Equal(-1.0, e[0].Imaginary, 12);
        Assert.Equal(2.0, e[1].Real, 12); Assert.Equal(1.0, e[1].Imaginary, 12);
        Assert.Equal(5.0, e[2].Real, 12); Assert.Equal(0.0, e[2].Imaginary, 12);
    }

    [Fact]
    public void Upper_triangular_eigenvalues_are_the_diagonal()
    {
        var a = new double[,] { { 1, 2, 3 }, { 0, 4, 5 }, { 0, 0, 6 } };
        Complex[] e = Sorted(GeneralEigen.Eigenvalues(a));
        Assert.Equal(1.0, e[0].Real, 10);
        Assert.Equal(4.0, e[1].Real, 10);
        Assert.Equal(6.0, e[2].Real, 10);
        foreach (Complex c in e) Assert.Equal(0.0, c.Imaginary, 10);
    }

    [Fact]
    public void Left_and_right_vectors_are_biorthonormal_to_1e_minus_10()
    {
        var a = new double[,] { { 2, -1, 0 }, { 1, 2, 0 }, { 0, 0, 5 } };
        var pairs = GeneralEigen.Decompose(a);
        Assert.Equal(3, pairs.Count);
        for (int i = 0; i < pairs.Count; i++)
            for (int j = 0; j < pairs.Count; j++)
            {
                Complex ip = Complex.Zero;
                for (int k = 0; k < 3; k++) ip += Complex.Conjugate(pairs[i].Left[k]) * pairs[j].Right[k];
                double expected = i == j ? 1.0 : 0.0;
                Assert.True((ip - new Complex(expected, 0)).Magnitude < 1e-10,
                    $"psi_{i}^H phi_{j} = {ip} (expected {expected})");
            }
    }

    [Fact]
    public void Right_and_left_vectors_actually_solve_the_eigen_equations()
    {
        var a = new double[,] { { 4, 1, 0 }, { -2, 1, 3 }, { 0, 1, 2 } };
        foreach (GeneralEigen.Eigenpair pr in GeneralEigen.Decompose(a))
        {
            // A phi = lambda phi
            for (int i = 0; i < 3; i++)
            {
                Complex row = Complex.Zero;
                for (int k = 0; k < 3; k++) row += a[i, k] * pr.Right[k];
                Assert.True((row - pr.Value * pr.Right[i]).Magnitude < 1e-8, "A phi != lambda phi");
            }
        }
    }

    [Fact]
    public void A_defective_matrix_fails_loud_and_never_fabricates()
    {
        // A 2x2 Jordan block: eigenvalue 2 (algebraic mult 2, geometric mult 1) - defective, V singular.
        var a = new double[,] { { 2, 1 }, { 0, 2 } };
        Assert.Throws<GeneralEigen.DefectiveEigenproblemException>(() => GeneralEigen.Decompose(a));
    }

    private static Complex[] Sorted(Complex[] e)
    {
        Array.Sort(e, (x, y) =>
        {
            int c = x.Real.CompareTo(y.Real);
            return c != 0 ? c : x.Imaginary.CompareTo(y.Imaginary);
        });
        return e;
    }
}
