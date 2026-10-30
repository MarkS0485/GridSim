using GridSim.Core.Numerics;
using Xunit;

namespace GridSim.Tests;

/// <summary>
/// Pins the shared vectorised primitives (<see cref="SimdKernels"/>) that the dense LU,
/// normal-equations gain and residual-covariance kernels dispatch through. Two contracts:
/// the bit-exact subtract/add paths must match the scalar loop to the bit at whatever vector
/// width this CPU runs (tail handling across lane boundaries is where SIMD kernels usually
/// break), and the reductions must agree with the scalar sum to floating-point tolerance.
/// Counts deliberately straddle the lane width (1..40) so the vector body AND the scalar tail
/// are both exercised at every tier.
/// </summary>
public class SimdKernelsTests
{
    private static double[] Ramp(int n, int seed)
    {
        var a = new double[n];
        // Deterministic, spread across magnitudes so cancellation shows up if the order is wrong.
        ulong s = (ulong)(seed * 2654435761u + 1);
        for (int i = 0; i < n; i++)
        {
            s = s * 6364136223846793005UL + 1442695040888963407UL;
            a[i] = ((s >> 11) * (1.0 / (1UL << 53))) * 2000.0 - 1000.0;
        }
        return a;
    }

    [Fact]
    public void Capabilities_are_self_consistent()
    {
        int lanes = SimdCapabilities.DoubleLanes;
        Assert.Contains(lanes, new[] { 1, 2, 4, 8 });
        SimdTier tier = SimdCapabilities.Tier;
        Assert.Equal(tier switch
        {
            SimdTier.Avx512 => 8,
            SimdTier.Avx256 => 4,
            SimdTier.Sse128 => 2,
            _ => 1,
        }, lanes);
        Assert.False(string.IsNullOrWhiteSpace(SimdCapabilities.Describe()));
    }

    [Fact]
    public void SubtractScaledRow_is_bit_exact_vs_scalar_at_all_lengths()
    {
        const double factor = 3.14159;
        for (int count = 0; count <= 40; count++)
        {
            double[] dst = Ramp(count + 5, 1);
            double[] src = Ramp(count + 5, 2);
            var expected = (double[])dst.Clone();
            for (int t = 0; t < count; t++) expected[3 + t] -= factor * src[1 + t];

            SimdKernels.SubtractScaledRow(dst, 3, src, 1, count, factor);

            for (int i = 0; i < dst.Length; i++)
                Assert.Equal(BitConverter.DoubleToInt64Bits(expected[i]),
                             BitConverter.DoubleToInt64Bits(dst[i]));
        }
    }

    [Fact]
    public void AddScaledRow_bitexact_matches_scalar_add_at_all_lengths()
    {
        const double factor = -2.71828;
        for (int count = 0; count <= 40; count++)
        {
            double[] dst = Ramp(count + 5, 3);
            double[] src = Ramp(count + 5, 4);
            var expected = (double[])dst.Clone();
            for (int t = 0; t < count; t++) expected[2 + t] += factor * src[0 + t];

            SimdKernels.AddScaledRow(dst, 2, src, 0, count, factor, fastFma: false);

            for (int i = 0; i < dst.Length; i++)
                Assert.Equal(BitConverter.DoubleToInt64Bits(expected[i]),
                             BitConverter.DoubleToInt64Bits(dst[i]));
        }
    }

    [Fact]
    public void Dot_agrees_with_scalar_within_tolerance()
    {
        for (int count = 0; count <= 40; count++)
        {
            double[] x = Ramp(count + 4, 5);
            double[] y = Ramp(count + 4, 6);
            double simd = SimdKernels.Dot(x, 2, y, 1, count);
            double exact = SimdKernels.DotExact(x, 2, y, 1, count);
            // Vector reductions reassociate the sum, so allow a relative epsilon, not bit-equality.
            double scale = Math.Max(1.0, Math.Abs(exact));
            Assert.True(Math.Abs(simd - exact) <= 1e-9 * scale, $"count={count} simd={simd} exact={exact}");
        }
    }

    [Fact]
    public void Vectorised_SinCos_matches_Math_SinCos_to_1e12()
    {
        // Sweep a dense grid over several periods plus the exact quadrant boundaries, and a batch of
        // pseudo-random angles across the swing model's plausible range. The approximation must track
        // Math.SinCos to ~1e-13; assert a comfortable 1e-12.
        var angles = new List<double>();
        for (double a = -50.0; a <= 50.0; a += 0.0007) angles.Add(a);
        for (int k = -8; k <= 8; k++) angles.Add(k * Math.PI / 2.0);          // quadrant edges
        var rng = new Random(20260717);
        for (int i = 0; i < 5000; i++) angles.Add((rng.NextDouble() - 0.5) * 400.0);

        double[] x = angles.ToArray();
        var sin = new double[x.Length];
        var cos = new double[x.Length];
        SimdKernels.SinCos(x, sin, cos, x.Length);

        double worst = 0;
        for (int i = 0; i < x.Length; i++)
        {
            (double s, double c) = Math.SinCos(x[i]);
            worst = Math.Max(worst, Math.Max(Math.Abs(s - sin[i]), Math.Abs(c - cos[i])));
        }
        Assert.True(worst < 1e-12, $"worst abs error {worst:E3} exceeds 1e-12");
    }

    [Fact]
    public void AddScaledRow_fma_matches_scalar_fma_at_all_lengths()
    {
        const double factor = 1.4142135;
        for (int count = 0; count <= 40; count++)
        {
            double[] dst = Ramp(count + 5, 7);
            double[] src = Ramp(count + 5, 8);
            var expected = (double[])dst.Clone();
            if (SimdCapabilities.FmaAvailable)
                for (int t = 0; t < count; t++)
                    expected[1 + t] = Math.FusedMultiplyAdd(factor, src[0 + t], expected[1 + t]);
            else
                for (int t = 0; t < count; t++) expected[1 + t] += factor * src[0 + t];

            SimdKernels.AddScaledRow(dst, 1, src, 0, count, factor, fastFma: true);

            // The FMA vector path and the scalar FMA reference round identically per element.
            for (int i = 0; i < dst.Length; i++)
                Assert.Equal(BitConverter.DoubleToInt64Bits(expected[i]),
                             BitConverter.DoubleToInt64Bits(dst[i]));
        }
    }
}
