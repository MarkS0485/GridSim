using GridSim.Core.Numerics;
using Xunit;

namespace GridSim.Tests;

/// <summary>
/// R102 (October 2026 review): nothing failed if a vector width silently stopped being used. The existing
/// <see cref="SimdKernelsTests"/> only exercise the tier THIS machine selects, so on the i3-N305 dev box (no AVX-512)
/// the AVX-512 arms never ran, and a broken or deleted arm would pass. These tests drive every tier's arm through the
/// explicit-tier seam (Vector512 / Vector256 / Vector128 run in software where the hardware lacks them: slow but
/// exact), and check that the selected tier matches the hardware flags.
/// </summary>
public class SimdTierSeamTests
{
    public static TheoryData<SimdTier> AllTiers => new() { SimdTier.Scalar, SimdTier.Sse128, SimdTier.Avx256, SimdTier.Avx512 };

    private static double[] Ramp(int n, int seed)
    {
        var a = new double[n];
        ulong s = (ulong)(seed * 2654435761u + 1);
        for (int i = 0; i < n; i++)
        {
            s = s * 6364136223846793005UL + 1442695040888963407UL;
            a[i] = ((s >> 11) * (1.0 / (1UL << 53))) * 2000.0 - 1000.0;
        }
        return a;
    }

    [Theory]
    [MemberData(nameof(AllTiers))]
    public void SubtractScaledRow_is_bit_exact_vs_scalar_at_every_tier(SimdTier tier)
    {
        const double factor = 3.14159;
        for (int count = 0; count <= 40; count++)   // straddles every lane width: vector body AND scalar tail
        {
            double[] dst = Ramp(count + 5, 1), src = Ramp(count + 5, 2);
            var expected = (double[])dst.Clone();
            for (int t = 0; t < count; t++) expected[3 + t] -= factor * src[1 + t];

            SimdKernels.SubtractScaledRow(ref dst[0], 3, ref src[0], 1, count, factor, tier);

            for (int i = 0; i < dst.Length; i++)
                Assert.Equal(BitConverter.DoubleToInt64Bits(expected[i]), BitConverter.DoubleToInt64Bits(dst[i]));
        }
    }

    [Theory]
    [MemberData(nameof(AllTiers))]
    public void AddScaledRow_without_fma_is_bit_exact_vs_scalar_at_every_tier(SimdTier tier)
    {
        const double factor = -0.7071067811865476;
        for (int count = 0; count <= 40; count++)
        {
            double[] dst = Ramp(count + 4, 3), src = Ramp(count + 4, 4);
            var expected = (double[])dst.Clone();
            for (int t = 0; t < count; t++) expected[2 + t] += factor * src[2 + t];

            SimdKernels.AddScaledRow(ref dst[0], 2, ref src[0], 2, count, factor, fastFma: false, tier);

            for (int i = 0; i < dst.Length; i++)
                Assert.Equal(BitConverter.DoubleToInt64Bits(expected[i]), BitConverter.DoubleToInt64Bits(dst[i]));
        }
    }

    [Theory]
    [MemberData(nameof(AllTiers))]
    public void Dot_agrees_with_the_scalar_sum_at_every_tier(SimdTier tier)
    {
        for (int count = 0; count <= 40; count++)
        {
            double[] x = Ramp(count + 1, 5), y = Ramp(count + 1, 6);
            double exact = SimdKernels.DotExact(x, 1, y, 1, count);
            double got = SimdKernels.Dot(ref x[0], 1, ref y[0], 1, count, tier);
            // A vector dot changes the summation order (and may use FMA), so agreement is to rounding: the standard
            // bound for any summation order is (count + 1) * eps * sum|x_i y_i|. Not bit-exact by design.
            double scale = 0;
            for (int t = 0; t < count; t++) scale += Math.Abs(x[1 + t] * y[1 + t]);
            Assert.True(Math.Abs(got - exact) <= (count + 1) * 2.220446049250313e-16 * scale,
                $"tier {tier}, count {count}: {got:R} vs {exact:R}");
        }
    }

    [Fact]
    public void The_production_overloads_are_exactly_the_active_tier()
    {
        // The seam must not change production: the tier-less overloads give the same bits as the active tier.
        double[] x = Ramp(37, 7), y = Ramp(37, 8);
        Assert.Equal(BitConverter.DoubleToInt64Bits(SimdKernels.Dot(ref x[0], 0, ref y[0], 0, 37, SimdCapabilities.Tier)),
                     BitConverter.DoubleToInt64Bits(SimdKernels.Dot(ref x[0], 0, ref y[0], 0, 37)));

        double[] a = Ramp(37, 9), b = (double[])a.Clone(), s = Ramp(37, 10);
        SimdKernels.SubtractScaledRow(ref a[0], 0, ref s[0], 0, 37, 1.5);
        SimdKernels.SubtractScaledRow(ref b[0], 0, ref s[0], 0, 37, 1.5, SimdCapabilities.Tier);
        Assert.Equal(a, b);
    }

    [Fact]
    public void The_active_tier_is_what_the_hardware_flags_say()
    {
        // R102: a dispatch edit that silently drops a width (for example a deleted Avx512 arm in SimdCapabilities)
        // now fails here on whatever machine runs the suite.
        Assert.Equal(SimdCapabilities.ExpectedTierFromHardware, SimdCapabilities.Tier);
    }

    [Fact]
    public void Avx512_unused_is_reported_exactly_when_the_cpu_has_it_and_the_tier_is_narrower()
    {
        bool expected = System.Runtime.Intrinsics.X86.Avx512F.IsSupported && SimdCapabilities.Tier < SimdTier.Avx512;
        Assert.Equal(expected, SimdCapabilities.Avx512Unused);
    }

    [Fact]
    public void Simdcheck_has_an_expect_gate_that_exits_3()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "GridSim.slnx"))) dir = dir.Parent;
        string src = File.ReadAllText(Path.Combine(dir!.FullName, "src", "GridSim.Cli", "Program.cs"));
        Assert.Contains("FlagString(rest, \"--expect\")", src);
        Assert.Contains("simdcheck: FAILED - expected at least", src);
        Assert.Contains("SimdCapabilities.Avx512Unused", src);
    }
}
