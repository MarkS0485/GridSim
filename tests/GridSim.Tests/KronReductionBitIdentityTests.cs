using System.Numerics;
using GridSim.Core.IO;
using GridSim.Core.Model;
using GridSim.Core.PowerFlow;
using Xunit;

namespace GridSim.Tests;

/// <summary>
/// R126 (October 2026 review, S5): Kron reduction now keeps its active nodes in an array (O(1) removal), copies the
/// pivot row once per step, and computes the update term for exactly-zero pivot-row entries once per (row, zero sign
/// pattern), skipping it when it is exactly (+0, +0). Every reduced admittance must be bit-for-bit what the original
/// loop gives: compared here with the original, kept verbatim, on network admittances (IEEE 14, 118, 300 and a
/// generator-augmented network), on random matrices salted with all four signed complex zeros, and on matrices holding
/// infinities and NaNs (where the zero shortcut must not skip). For the NaN inputs every finite result is compared bit
/// for bit and every NaN result must be NaN in the original too: the sign and payload bits of a NaN are set by the
/// instruction sequence the JIT emits and can differ between two code shapes doing the same arithmetic (seen with
/// DOTNET_EnableHWIntrinsic=0), so they are not a number to compare.
/// </summary>
public class KronReductionBitIdentityTests
{
    /// <summary>The pre-R126 Reduce, verbatim.</summary>
    private static Complex[,] ReferenceReduce(Complex[,] y, IReadOnlyList<int> keepIndices)
    {
        int n = y.GetLength(0);
        var keep = new HashSet<int>(keepIndices);
        var w = (Complex[,])y.Clone();
        var active = Enumerable.Range(0, n).ToList();
        IEnumerable<int> toEliminate = Enumerable.Range(0, n).Where(i => !keep.Contains(i));
        double aNorm = 0.0;
        foreach (Complex c in w) { double mag = c.Magnitude; if (mag > aNorm) aNorm = mag; }
        double pivotFloor = GridSim.Core.Numerics.NumericTolerances.PivotFloor(aNorm);
        foreach (int k in toEliminate)
        {
            Complex pivot = w[k, k];
            if (pivot.Magnitude <= pivotFloor) throw new InvalidOperationException("singular");
            foreach (int i in active)
            {
                if (i == k) continue;
                Complex yik = w[i, k];
                if (yik == Complex.Zero) continue;
                foreach (int j in active)
                {
                    if (j == k) continue;
                    w[i, j] -= yik * w[k, j] / pivot;
                }
            }
            active.Remove(k);
        }
        int m = keepIndices.Count;
        var reduced = new Complex[m, m];
        for (int a = 0; a < m; a++)
            for (int b = 0; b < m; b++)
                reduced[a, b] = w[keepIndices[a], keepIndices[b]];
        return reduced;
    }

    private static void AssertBits(Complex[,] expected, Complex[,] actual, string what)
    {
        Assert.Equal(expected.GetLength(0), actual.GetLength(0));
        Assert.Equal(expected.GetLength(1), actual.GetLength(1));
        for (int i = 0; i < expected.GetLength(0); i++)
            for (int j = 0; j < expected.GetLength(1); j++)
            {
                Assert.True(BitConverter.DoubleToInt64Bits(expected[i, j].Real) == BitConverter.DoubleToInt64Bits(actual[i, j].Real),
                    $"{what} [{i},{j}] real: {expected[i, j].Real:R} vs {actual[i, j].Real:R}");
                Assert.True(BitConverter.DoubleToInt64Bits(expected[i, j].Imaginary) == BitConverter.DoubleToInt64Bits(actual[i, j].Imaginary),
                    $"{what} [{i},{j}] imag: {expected[i, j].Imaginary:R} vs {actual[i, j].Imaginary:R}");
            }
    }

    [Theory]
    [InlineData("14")]
    [InlineData("118")]
    [InlineData("300")]
    public void Network_admittance_reduced_to_generator_buses_is_bit_identical(string name)
    {
        GridModel m = name switch { "14" => IeeeCases.Case14(), "118" => IeeeCases.Case118(), _ => IeeeCases.Case300() };
        YBus yb = YBus.Build(m);
        int[] keep = m.Generators.Select(g => yb.IndexOf(g.Bus)).Distinct().ToArray();
        AssertBits(ReferenceReduce(yb.Y, keep), KronReduction.Reduce(yb.Y, keep), name);
        // Keep order matters for the output layout only: reversed keep list, same elimination set.
        int[] reversed = keep.Reverse().ToArray();
        AssertBits(ReferenceReduce(yb.Y, reversed), KronReduction.Reduce(yb.Y, reversed), name + " reversed");
    }

    [Fact]
    public void Generator_augmented_network_like_the_classical_init_is_bit_identical()
    {
        // The classical-init shape: n buses, one internal node per generator behind 1/(jX'd), loads as shunts.
        GridModel m = IeeeCases.Case118();
        YBus yb = YBus.Build(m);
        int n = yb.N, g = m.Generators.Count;
        var ya = new Complex[n + g, n + g];
        for (int i = 0; i < n; i++)
            for (int j = 0; j < n; j++) ya[i, j] = yb.Y[i, j];
        for (int i = 0; i < n; i++)
            if (m.Buses[i].Pd != 0 || m.Buses[i].Qd != 0)
                ya[i, i] += Complex.Conjugate(new Complex(m.Buses[i].Pd, m.Buses[i].Qd) / m.BaseMva);
        var keep = new int[g];
        for (int k = 0; k < g; k++)
        {
            int t = yb.IndexOf(m.Generators[k].Bus), node = n + k;
            Complex yGen = Complex.One / new Complex(0.0, 0.3 * m.BaseMva / Math.Max(1e-6, m.Generators[k].MBase));
            ya[t, t] += yGen; ya[node, node] += yGen; ya[t, node] -= yGen; ya[node, t] -= yGen;
            keep[k] = node;
        }
        AssertBits(ReferenceReduce(ya, keep), KronReduction.Reduce(ya, keep), "augmented 118");
    }

    private static Complex SignedZero(Random rng) => new(rng.Next(2) == 0 ? 0.0 : -0.0, rng.Next(2) == 0 ? 0.0 : -0.0);

    [Fact]
    public void Random_matrices_salted_with_every_signed_zero_are_bit_identical()
    {
        var rng = new Random(126);
        for (int trial = 0; trial < 120; trial++)
        {
            int n = 2 + rng.Next(30);
            var y = new Complex[n, n];
            for (int i = 0; i < n; i++)
            {
                for (int j = 0; j < n; j++)
                {
                    int pick = rng.Next(10);
                    y[i, j] = pick < 5 ? SignedZero(rng)
                        : pick < 6 ? new Complex(rng.Next(2) == 0 ? 0.0 : -0.0, rng.NextDouble() - 0.5)   // one zero component
                        : new Complex(rng.NextDouble() - 0.5, rng.NextDouble() - 0.5);
                }
                // A dominant diagonal of either sign pattern keeps every pivot above the floor.
                y[i, i] = new Complex((rng.Next(2) == 0 ? 1 : -1) * (n + rng.NextDouble()), (rng.Next(2) == 0 ? 1 : -1) * (n + rng.NextDouble()));
            }
            int keepCount = rng.Next(1, n);
            int[] keep = Enumerable.Range(0, n).OrderBy(_ => rng.Next()).Take(keepCount).ToArray();
            AssertBits(ReferenceReduce(y, keep), KronReduction.Reduce(y, keep), $"trial {trial} n={n}");
        }
    }

    [Fact]
    public void Matrices_with_infinities_and_NaNs_are_bit_identical()
    {
        var rng = new Random(7);
        for (int trial = 0; trial < 40; trial++)
        {
            int n = 3 + rng.Next(10);
            var y = new Complex[n, n];
            for (int i = 0; i < n; i++)
                for (int j = 0; j < n; j++)
                    y[i, j] = rng.Next(4) == 0 ? SignedZero(rng) : new Complex(rng.NextDouble() - 0.5, rng.NextDouble() - 0.5);
            for (int i = 0; i < n; i++) y[i, i] = new Complex(n + 1.0, -(n + 1.0));
            // One off-diagonal infinity or NaN (a NaN disables the zero shortcut's skip; an infinity makes NaN terms).
            int r = rng.Next(n), c = (rng.Next(n - 1) + r + 1) % n;
            y[r, c] = trial % 2 == 0 ? new Complex(double.PositiveInfinity, 0.0) : new Complex(double.NaN, 1.0);
            int[] keep = Enumerable.Range(0, n).Where(x => x % 3 == 0).ToArray();
            // An infinite entry makes the matrix scale infinite, so the pivot floor is infinite and both versions refuse
            // the first elimination; the outcome must match either way.
            Complex[,]? expected = null, actual = null;
            string? expectedError = null, actualError = null;
            try { expected = ReferenceReduce(y, keep); } catch (InvalidOperationException) { expectedError = "singular"; }
            try { actual = KronReduction.Reduce(y, keep); } catch (InvalidOperationException) { actualError = "singular"; }
            Assert.Equal(expectedError, actualError);
            if (expected is null || actual is null) continue;
            Assert.Equal(expected.Length, actual.Length);
            static void Same(double e, double a)
            {
                if (double.IsNaN(e)) Assert.True(double.IsNaN(a), $"expected NaN, got {a:R}");
                else Assert.Equal(BitConverter.DoubleToInt64Bits(e), BitConverter.DoubleToInt64Bits(a));
            }
            for (int i = 0; i < keep.Length; i++)
                for (int j = 0; j < keep.Length; j++)
                {
                    Same(expected[i, j].Real, actual[i, j].Real);
                    Same(expected[i, j].Imaginary, actual[i, j].Imaginary);
                }
        }
    }

    [Fact]
    public void Singular_pivot_still_fails_loud_with_the_same_message_shape()
    {
        var y = new Complex[3, 3];
        y[0, 0] = new Complex(1, -1); y[2, 2] = new Complex(1, -1);   // bus 1 isolated: zero self-admittance
        var e = Assert.Throws<InvalidOperationException>(() => KronReduction.Reduce(y, new[] { 0, 2 }));
        Assert.StartsWith("Cannot eliminate bus index 1: self-admittance", e.Message);
    }
}
