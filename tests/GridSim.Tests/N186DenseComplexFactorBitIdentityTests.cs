using System.Numerics;
using System.Reflection;
using GridSim.Core.Numerics;
using Xunit;

namespace GridSim.Tests;

/// <summary>
/// N186 (October 2026): <see cref="DenseComplexLuSolver.FactorDense"/> now eliminates column by column on a
/// column-major copy, a block of columns at a time, with a vectorised inner update. The factor must be bit-for-bit the
/// one the previous row-by-row loop produced. The previous loop is kept below VERBATIM (<see cref="OldFactor"/>, copied
/// from 1.95.133) and every stored multiplier, U entry, pivot row, clamp floor and clamp count is compared bit for bit,
/// at every vector tier (the AVX-512 / AVX2 / SSE arms run in software where the CPU lacks them, so every arm is
/// checked on any machine) and at several block widths. The matrices are chosen to reach every branch: pivoting,
/// zero multipliers (the skip), signed zeros, a singular matrix (clamped pivots), the all-zero matrix, a column of
/// zeros under the diagonal, and orders that straddle the vector widths and block widths.
/// </summary>
public sealed class N186DenseComplexFactorBitIdentityTests
{
    // ---- the previous factor, verbatim (1.95.133 ComplexLinearSolvers.cs), returning its raw arrays ------------------

    private static (Complex[] A, int[] Pivots, double ClampFloor, int Clamped) OldFactor(Complex[,] aIn, int n)
    {
        var a = new Complex[n * n];
        for (int i = 0; i < n; i++)
            for (int j = 0; j < n; j++) a[i * n + j] = aIn[i, j];
        var pivots = new int[n];
        double clampFloor = NumericTolerances.PivotFloor(DenseComplexLuSolver.MaxMagnitude(a));
        int clamped = 0;

        for (int col = 0; col < n; col++)
        {
            int piv = col; double best = a[col * n + col].Magnitude;
            for (int r = col + 1; r < n; r++) { double mm = a[r * n + col].Magnitude; if (mm > best) { best = mm; piv = r; } }
            pivots[col] = piv;
            if (piv != col)
                for (int j = col; j < n; j++) (a[col * n + j], a[piv * n + j]) = (a[piv * n + j], a[col * n + j]);
            Complex d = a[col * n + col];
            if (d.Magnitude <= clampFloor) { d = new Complex(clampFloor > 0.0 ? clampFloor : 1.0, 0.0); clamped++; }
            for (int r = col + 1; r < n; r++)
            {
                Complex factor = a[r * n + col] / d;
                a[r * n + col] = factor;
                if (factor == Complex.Zero) continue;
                for (int j = col + 1; j < n; j++) a[r * n + j] -= factor * a[col * n + j];
            }
        }
        return (a, pivots, clampFloor, clamped);
    }

    // ---- matrices --------------------------------------------------------------------------------------------------

    private static ulong Next(ref ulong s) => s = s * 6364136223846793005UL + 1442695040888963407UL;
    private static double U(ref ulong s) => ((Next(ref s) >> 11) * (1.0 / (1UL << 53))) * 2.0 - 1.0;

    /// <summary>Dense random complex matrix; <paramref name="zeroFraction"/> of the entries are exact zeros (of both
    /// signs), so many multipliers are zero and the skip runs.</summary>
    private static Complex[,] Random(int n, int seed, double zeroFraction = 0.0)
    {
        var m = new Complex[n, n];
        ulong s = (ulong)(seed * 2654435761u + 11);
        for (int i = 0; i < n; i++)
            for (int j = 0; j < n; j++)
            {
                double pick = (U(ref s) + 1.0) / 2.0;
                if (pick < zeroFraction)
                    m[i, j] = (Next(ref s) & 1) == 0 ? Complex.Zero : new Complex(-0.0, -0.0);
                else
                    m[i, j] = new Complex(U(ref s) * 10.0, U(ref s));
            }
        return m;
    }

    public static TheoryData<SimdTier> AllTiers => new() { SimdTier.Scalar, SimdTier.Sse128, SimdTier.Avx256, SimdTier.Avx512 };

    private static readonly FieldInfo AField = typeof(DenseComplexLu).GetField("_a", BindingFlags.NonPublic | BindingFlags.Instance)!;
    private static readonly FieldInfo PivotsField = typeof(DenseComplexLu).GetField("_pivots", BindingFlags.NonPublic | BindingFlags.Instance)!;
    private static readonly FieldInfo FloorField = typeof(DenseComplexLu).GetField("_clampFloor", BindingFlags.NonPublic | BindingFlags.Instance)!;

    private static void AssertSameFactor(Complex[,] m, int n, SimdTier tier, int block, string what)
    {
        var old = OldFactor(m, n);
        DenseComplexLu lu = DenseComplexLuSolver.FactorDenseAt(m, n, tier, block);
        var a = (Complex[])AField.GetValue(lu)!;
        var pivots = (int[])PivotsField.GetValue(lu)!;
        Assert.Equal(old.Pivots, pivots);
        Assert.Equal(BitConverter.DoubleToInt64Bits(old.ClampFloor), BitConverter.DoubleToInt64Bits((double)FloorField.GetValue(lu)!));
        Assert.Equal(old.Clamped, lu.ClampedPivots);
        Assert.Equal(old.A.Length, a.Length);
        for (int i = 0; i < a.Length; i++)
        {
            if (BitConverter.DoubleToInt64Bits(old.A[i].Real) != BitConverter.DoubleToInt64Bits(a[i].Real) ||
                BitConverter.DoubleToInt64Bits(old.A[i].Imaginary) != BitConverter.DoubleToInt64Bits(a[i].Imaginary))
                Assert.Fail($"{what}, tier {tier}, block {block}: entry ({i / n},{i % n}) old {old.A[i]:R} new {a[i]:R}");
        }
    }

    // ---- tests -----------------------------------------------------------------------------------------------------

    [Theory]
    [MemberData(nameof(AllTiers))]
    public void Every_order_from_0_to_40_matches_the_previous_factor_at_every_block_width(SimdTier tier)
    {
        foreach (int block in new[] { 1, 3, 16, 64 })
            for (int n = 0; n <= 40; n++)
            {
                AssertSameFactor(Random(n, n), n, tier, block, $"dense n={n}");
                AssertSameFactor(Random(n, 100 + n, zeroFraction: 0.5), n, tier, block, $"half-zero n={n}");
            }
    }

    [Theory]
    [MemberData(nameof(AllTiers))]
    public void A_larger_matrix_matches_across_several_blocks(SimdTier tier)
    {
        AssertSameFactor(Random(97, 7), 97, tier, 16, "dense n=97");
        AssertSameFactor(Random(97, 8, zeroFraction: 0.8), 97, tier, 16, "sparse n=97");
    }

    [Theory]
    [MemberData(nameof(AllTiers))]
    public void Singular_and_degenerate_matrices_match_including_the_clamp(SimdTier tier)
    {
        // Rank-deficient: row 3 = row 1 + row 2, so a pivot vanishes and is clamped.
        Complex[,] m = Random(9, 3);
        for (int j = 0; j < 9; j++) m[3, j] = m[1, j] + m[2, j];
        foreach (int block in new[] { 1, 2, 16 })
        {
            AssertSameFactor(m, 9, tier, block, "rank-deficient");
            AssertSameFactor(new Complex[6, 6], 6, tier, block, "all zero");
        }
        Assert.True(DenseComplexLuSolver.FactorDense(m, 9).ClampedPivots > 0);   // the clamp really ran

        // A first column that is zero below the diagonal (no pivoting, every multiplier of step 0 zero).
        Complex[,] z = Random(12, 4);
        for (int i = 1; i < 12; i++) z[i, 0] = (i % 2 == 0) ? Complex.Zero : new Complex(-0.0, 0.0);
        AssertSameFactor(z, 12, tier, 5, "zero first column");

        // A shifted matrix next to an eigenvalue, exactly as inverse iteration builds it.
        double[,] r = N186ParticipationFixtures.RandomMatrix(24);
        Complex mu = GeneralEigen.Eigenvalues(r)[5];
        Complex shift = mu + new Complex(1e-10, 0.37e-10);
        var sh = new Complex[24, 24];
        for (int i = 0; i < 24; i++)
            for (int j = 0; j < 24; j++) sh[i, j] = new Complex(r[i, j], 0.0) - (i == j ? shift : Complex.Zero);
        AssertSameFactor(sh, 24, tier, 16, "inverse-iteration shift");
    }

    [Fact]
    public void A_nan_entry_still_fails_loud_with_the_same_message()
    {
        Complex[,] m = Random(5, 9);
        m[2, 3] = new Complex(double.NaN, 0.0);
        var ex = Assert.Throws<ArgumentException>(() => DenseComplexLuSolver.FactorDense(m, 5));
        Assert.Equal("Dense complex LU: the matrix contains a NaN entry, so it cannot be factorised.", ex.Message);
    }

    [Fact]
    public void The_production_entry_point_uses_the_selected_tier_and_block_width()
    {
        Complex[,] m = Random(33, 21);
        var a1 = (Complex[])AField.GetValue(DenseComplexLuSolver.FactorDense(m, 33))!;
        var a2 = (Complex[])AField.GetValue(DenseComplexLuSolver.FactorDenseAt(m, 33, SimdCapabilities.Tier, DenseComplexLuSolver.FactorBlockColumns))!;
        Assert.Equal(a1.Select(c => (BitConverter.DoubleToInt64Bits(c.Real), BitConverter.DoubleToInt64Bits(c.Imaginary))),
                     a2.Select(c => (BitConverter.DoubleToInt64Bits(c.Real), BitConverter.DoubleToInt64Bits(c.Imaginary))));
        Assert.Throws<ArgumentOutOfRangeException>(() => DenseComplexLuSolver.FactorDenseAt(m, 33, SimdTier.Scalar, 0));
    }

    // ---- the kernels on their own ----------------------------------------------------------------------------------

    [Theory]
    [MemberData(nameof(AllTiers))]
    public void The_four_column_kernel_is_bit_exact_vs_the_scalar_statement_at_every_tier(SimdTier tier)
    {
        var specials = new[] { 0.0, -0.0, 1e-308, -3.5, double.Epsilon, 1e300, -1e300, double.NaN };
        Complex[] us = { new(0.7, -1.3), new(-0.0, 0.0), new(2e300, 3e300), new(-4.25, 0.0), new(0.0, double.NaN) };
        for (int count = 0; count <= 19; count++)
            for (int pick = 0; pick < us.Length; pick++)
            {
                Complex u0 = us[pick], u1 = us[(pick + 1) % us.Length], u2 = us[(pick + 2) % us.Length], u3 = us[(pick + 3) % us.Length];
                ulong s = (ulong)(count * 131 + pick * 7 + 3);
                int stride = count + 3;
                var c = new Complex[5 * stride + 2];   // multipliers first, then four destination columns
                for (int i = 0; i < c.Length; i++)
                {
                    ulong k = Next(ref s) % 6;
                    c[i] = k switch
                    {
                        0 => Complex.Zero,
                        1 => new Complex(-0.0, 0.0),
                        2 => new Complex(0.0, specials[(int)(Next(ref s) % (ulong)specials.Length)]),
                        3 => new Complex(specials[(int)(Next(ref s) % (ulong)specials.Length)], -0.0),
                        _ => new Complex(U(ref s) * 5.0, U(ref s)),
                    };
                }
                var expected = (Complex[])c.Clone();
                int lOff = 1;
                int[] dst = { stride + 1, 2 * stride + 1, 3 * stride + 1, 4 * stride + 1 };
                Complex[] u = { u0, u1, u2, u3 };
                for (int t = 0; t < count; t++)
                {
                    Complex factor = expected[lOff + t];
                    if (factor == Complex.Zero) continue;
                    for (int q = 0; q < 4; q++) expected[dst[q] + t] -= factor * u[q];
                }
                SimdKernels.SubtractScaledComplexColumns4(c, dst[0], dst[1], dst[2], dst[3], lOff, count, u0, u1, u2, u3, tier);
                for (int i = 0; i < c.Length; i++)
                {
                    Assert.Equal(BitConverter.DoubleToInt64Bits(expected[i].Real), BitConverter.DoubleToInt64Bits(c[i].Real));
                    Assert.Equal(BitConverter.DoubleToInt64Bits(expected[i].Imaginary), BitConverter.DoubleToInt64Bits(c[i].Imaginary));
                }
            }
    }

    [Theory]
    [MemberData(nameof(AllTiers))]
    public void The_complex_column_kernel_is_bit_exact_vs_the_scalar_statement_at_every_tier(SimdTier tier)
    {
        var specials = new[] { 0.0, -0.0, 1e-308, -3.5, double.Epsilon, 1e300, -1e300 };
        for (int count = 0; count <= 23; count++)
            foreach (Complex u in new[] { new Complex(0.7, -1.3), new Complex(-0.0, 0.0), new Complex(2e300, 3e300) })
            {
                ulong s = (ulong)(count * 977 + 5);
                var c = new Complex[2 * count + 7];
                for (int i = 0; i < c.Length; i++)
                {
                    // A mix of ordinary values, exact zeros of both signs (whole and half) and extremes.
                    ulong k = Next(ref s) % 6;
                    c[i] = k switch
                    {
                        0 => Complex.Zero,
                        1 => new Complex(-0.0, -0.0),
                        2 => new Complex(0.0, specials[(int)(Next(ref s) % (ulong)specials.Length)]),
                        3 => new Complex(specials[(int)(Next(ref s) % (ulong)specials.Length)], 0.0),
                        _ => new Complex(U(ref s) * 5.0, U(ref s)),
                    };
                }
                var expected = (Complex[])c.Clone();
                int dstOff = count + 5, lOff = 2;   // overlapping arrays as FactorDense uses: one flat matrix
                for (int t = 0; t < count; t++)
                {
                    Complex factor = expected[lOff + t];
                    if (factor == Complex.Zero) continue;
                    expected[dstOff + t] -= factor * u;
                }
                SimdKernels.SubtractScaledComplexColumn(c, dstOff, lOff, count, u, tier);
                for (int i = 0; i < c.Length; i++)
                {
                    Assert.Equal(BitConverter.DoubleToInt64Bits(expected[i].Real), BitConverter.DoubleToInt64Bits(c[i].Real));
                    Assert.Equal(BitConverter.DoubleToInt64Bits(expected[i].Imaginary), BitConverter.DoubleToInt64Bits(c[i].Imaginary));
                }
            }
    }
}
