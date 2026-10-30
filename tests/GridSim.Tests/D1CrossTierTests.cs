using System.Numerics;
using GridSim.Core.Dynamics.Distributed;
using GridSim.Core.Estimation;
using GridSim.Core.Numerics;
using Xunit;

namespace GridSim.Tests;

/// <summary>
/// D1 (October 2026, Mark's determinism rule 2026-10-04: "a 1 bit change is enough to break machine epsilon"): the
/// three paths the inertia re-appraisal found tier-dependent (R279 / R113 / R125) now give the same bits at every
/// vector width. Each test drives every tier's arm through the R102 explicit-tier seam (Vector512 / Vector256 /
/// Vector128 run in software where the CPU lacks them: slow but exact) and requires EXACT equality across the four
/// tiers, and with a plain scalar statement of the defined arithmetic, so neither a width-dependent order nor a fused
/// multiply-add can come back unnoticed. The full suite also runs in the three SIMD modes (normal,
/// DOTNET_EnableAVX2=0, DOTNET_EnableHWIntrinsic=0), where the production overloads select different tiers.
/// </summary>
public class D1CrossTierTests
{
    private static readonly SimdTier[] Tiers = { SimdTier.Scalar, SimdTier.Sse128, SimdTier.Avx256, SimdTier.Avx512 };

    private static double[] Ramp(int n, int seed, double scale = 1000.0)
    {
        var a = new double[n];
        ulong s = (ulong)(seed * 2654435761u + 1);
        for (int i = 0; i < n; i++)
        {
            s = s * 6364136223846793005UL + 1442695040888963407UL;
            a[i] = ((s >> 11) * (1.0 / (1UL << 53))) * 2.0 * scale - scale;
        }
        return a;
    }

    private static long Bits(double d) => BitConverter.DoubleToInt64Bits(d);

    // The defined order of SimdKernels.Dot, written as plain scalar statements: four partial sums over the
    // multiple-of-four body (element t into partial t mod 4, each product rounded on its own), combined as
    // (s0 + s1) + (s2 + s3), then the tail left to right.
    private static double DefinedOrderDot(double[] x, int xOff, double[] y, int yOff, int count)
    {
        double s0 = 0.0, s1 = 0.0, s2 = 0.0, s3 = 0.0;
        int body = count & ~3;
        for (int t = 0; t < body; t += 4)
        {
            double p0 = x[xOff + t] * y[yOff + t];
            double p1 = x[xOff + t + 1] * y[yOff + t + 1];
            double p2 = x[xOff + t + 2] * y[yOff + t + 2];
            double p3 = x[xOff + t + 3] * y[yOff + t + 3];
            s0 += p0; s1 += p1; s2 += p2; s3 += p3;
        }
        double s = (s0 + s1) + (s2 + s3);
        for (int t = body; t < count; t++) s += x[xOff + t] * y[yOff + t];
        return s;
    }

    [Fact]
    public void Dot_is_bit_identical_at_every_tier_and_equals_the_defined_order()
    {
        for (int count = 0; count <= 75; count++)   // every residue mod 8 several times, vector body and tail
        {
            for (int off = 0; off < 3; off++)
            {
                double[] x = Ramp(count + 3, 11 + count), y = Ramp(count + 3, 97 + count, 0.001);
                long want = Bits(DefinedOrderDot(x, off, y, 2 - off + 0, count));
                foreach (SimdTier tier in Tiers)
                {
                    double got = SimdKernels.Dot(ref x[0], off, ref y[0], 2 - off, count, tier);
                    Assert.True(want == Bits(got), $"count {count}, offset {off}, tier {tier}: {got:R} vs defined order {BitConverter.Int64BitsToDouble(want):R}");
                }
                // The production overloads are the active tier, so they too equal the defined order.
                Assert.Equal(want, Bits(SimdKernels.Dot(x, off, y, 2 - off, count)));
            }
        }
    }

    [Fact]
    public void Batch_SinCos_is_bit_identical_at_every_tier_and_to_DetMath_for_every_element()
    {
        // Lengths straddling every vector width (the old kernel switched formula at floor(n/w)*w), angles over the
        // swing model's range and beyond, quadrant edges, and the self term 0.
        var angles = new List<double> { 0.0, -0.0, Math.PI / 4, Math.PI / 2, -Math.PI, 3 * Math.PI / 4, 1e6, -1e9 };
        angles.AddRange(Ramp(500, 5, 7.0));
        angles.AddRange(Ramp(200, 6, 400.0));
        double[] all = angles.ToArray();
        for (int n = 0; n <= 37; n++)
        {
            foreach (SimdTier tier in Tiers)
            {
                double[] x = all.Skip(n * 7).Take(n).ToArray();
                var s = new double[n];
                var c = new double[n];
                SimdKernels.SinCos(x, s, c, n, tier);
                for (int i = 0; i < n; i++)
                {
                    (double ws, double wc) = DetMath.SinCos(x[i]);
                    Assert.True(Bits(ws) == Bits(s[i]) && Bits(wc) == Bits(c[i]), $"tier {tier}, n {n}, element {i} ({x[i]:R})");
                }
            }
        }
        // And the whole array in one call (a long batch).
        var sa = new double[all.Length];
        var ca = new double[all.Length];
        SimdKernels.SinCos(all, sa, ca, all.Length);
        for (int i = 0; i < all.Length; i++)
        {
            Assert.Equal(Bits(DetMath.Sin(all[i])), Bits(sa[i]));
            Assert.Equal(Bits(DetMath.Cos(all[i])), Bits(ca[i]));
        }
    }

    [Fact]
    public void Dense_WLS_gain_is_bit_identical_at_every_tier_and_has_no_fused_multiply_add()
    {
        // A sparse-ish Jacobian (zeros skipped by the kernel, as in a real H) with weights over several decades.
        foreach (int s in new[] { 1, 3, 4, 7, 8, 9, 16, 23, 40 })
        {
            int m = 2 * s + 5;
            double[] flat = Ramp(m * s, 31 + s, 3.0);
            var jac = new double[m, s];
            for (int r = 0; r < m; r++)
                for (int c = 0; c < s; c++)
                    jac[r, c] = ((r * 7 + c * 3) % 5 == 0) ? 0.0 : flat[r * s + c];
            double[] w = Ramp(m, 41 + s, 1.0).Select(v => DetMath.Pow(10.0, 4.0 * Math.Abs(v))).ToArray();
            double[] z = Ramp(m, 51 + s, 1.0), h = Ramp(m, 61 + s, 1.0);

            // The plain scalar statement of the gain (separate multiply, then add, upper triangle mirrored).
            var wantGain = new double[s, s];
            var wantRhs = new double[s];
            for (int r = 0; r < m; r++)
            {
                double wres = w[r] * (z[r] - h[r]);
                for (int c = 0; c < s; c++)
                {
                    double hrc = jac[r, c];
                    if (hrc == 0) continue;
                    wantRhs[c] += hrc * wres;
                    double whrc = w[r] * hrc;
                    for (int c2 = c; c2 < s; c2++) { double p = whrc * jac[r, c2]; wantGain[c, c2] += p; }
                }
            }
            for (int c = 0; c < s; c++)
                for (int c2 = c + 1; c2 < s; c2++)
                    wantGain[c2, c] = wantGain[c, c2];

            foreach (SimdTier tier in Tiers)
            {
                var gain = new double[s, s];
                var rhs = new double[s];
                DenseWlsStep.FormNormalEquations(z, h, w, jac, gain, rhs, tier);
                for (int i = 0; i < s; i++)
                {
                    Assert.True(Bits(wantRhs[i]) == Bits(rhs[i]), $"s {s}, tier {tier}, rhs[{i}]");
                    for (int j = 0; j < s; j++)
                        Assert.True(Bits(wantGain[i, j]) == Bits(gain[i, j]), $"s {s}, tier {tier}, gain[{i},{j}]: {gain[i, j]:R} vs {wantGain[i, j]:R}");
                }
            }
            // Production overload = the active tier = the scalar statement.
            var g2 = new double[s, s];
            var r2 = new double[s];
            DenseWlsStep.FormNormalEquations(z, h, w, jac, g2, r2);
            for (int i = 0; i < s; i++)
                for (int j = 0; j < s; j++)
                    Assert.Equal(Bits(wantGain[i, j]), Bits(g2[i, j]));
        }
    }

    [Fact]
    public void Classical_swing_trajectory_is_bit_identical_at_every_tier()
    {
        // Seven machines (a 4-lane tail and a 2-lane tail at the wider tiers), a reduced admittance with real
        // transfer conductances, a fault that is cleared, and a governor plus flux-decay hook so every branch of
        // the integrator runs.
        const int n = 7;
        double[] h = { 6.0, 3.5, 4.2, 2.8, 5.1, 3.3, 7.7 };
        double[] d = { 0.5, 0.3, 0.2, 0.4, 0.6, 0.1, 0.3 };
        double[] pm = { 0.9, 0.7, 0.5, 0.6, 0.8, 0.4, 1.0 };
        double[] eq = { 1.05, 1.02, 1.08, 1.01, 1.04, 1.06, 1.03 };
        double[] delta0 = { 0.3, 0.1, -0.2, 0.05, 0.25, -0.1, 0.4 };
        var y = new Complex[n, n];
        var yf = new Complex[n, n];
        for (int i = 0; i < n; i++)
            for (int j = 0; j < n; j++)
            {
                double g = i == j ? 0.4 + 0.05 * i : 0.02 * ((i + j) % 3);
                double b = i == j ? -6.0 - 0.3 * i : 1.0 + 0.1 * ((i * j) % 4);
                y[i, j] = new Complex(g, b);
                yf[i, j] = (i == 2 || j == 2) ? new Complex(g * 0.2, b * 0.2) : new Complex(g, b);
            }
        var fault = new ClassicalMultiMachine.Fault(0.1, 0.25, yf);

        ClassicalMultiMachine.Result Run(SimdTier tier) => ClassicalMultiMachine.SimulateAtTier(
            tier, h, d, pm, eq, delta0, y, 50.0, 0.002, 1.5, fault, 2.0 * Math.PI,
            (i, w) => pm[i] - 20.0 * w, (i, e, id) => e + 0.001 * (1.0 - e) - 0.0005 * id);

        ClassicalMultiMachine.Result reference = Run(SimdTier.Scalar);
        foreach (SimdTier tier in Tiers)
        {
            ClassicalMultiMachine.Result r = Run(tier);
            Assert.Equal(reference.StayedInSynchronism, r.StayedInSynchronism);
            for (int i = 0; i < n; i++)
            {
                var a = reference.Machines[i];
                var b = r.Machines[i];
                Assert.Equal(a.DeltaRad.Count, b.DeltaRad.Count);
                for (int k = 0; k < a.DeltaRad.Count; k++)
                {
                    Assert.True(Bits(a.DeltaRad[k]) == Bits(b.DeltaRad[k]), $"tier {tier}, machine {i}, step {k}: delta");
                    Assert.True(Bits(a.SpeedPu[k]) == Bits(b.SpeedPu[k]), $"tier {tier}, machine {i}, step {k}: omega");
                }
            }
        }
        // The production entry point is the active tier, so it is the same trajectory.
        ClassicalMultiMachine.Result prod = ClassicalMultiMachine.Simulate(h, d, pm, eq, delta0, y, 50.0, 0.002, 1.5, fault,
            2.0 * Math.PI, (i, w) => pm[i] - 20.0 * w, (i, e, id) => e + 0.001 * (1.0 - e) - 0.0005 * id);
        for (int i = 0; i < n; i++)
            Assert.Equal(reference.Machines[i].DeltaRad.Select(Bits), prod.Machines[i].DeltaRad.Select(Bits));
    }

    // ---- exact pins, run by the suite in all three SIMD modes -----------------------------------------------------

    private static string Sha(IEnumerable<double> xs)
    {
        using var h = System.Security.Cryptography.IncrementalHash.CreateHash(System.Security.Cryptography.HashAlgorithmName.SHA256);
        foreach (double x in xs) h.AppendData(BitConverter.GetBytes(BitConverter.DoubleToInt64Bits(x)));
        return Convert.ToHexString(h.GetHashAndReset());
    }

    private static IEnumerable<double> EstimateBits(string name)
    {
        GridSim.Core.Model.GridModel m = name == "14" ? GridSim.Core.IO.IeeeCases.Case14() : GridSim.Core.IO.IeeeCases.Case57();
        var truth = new GridSim.Core.PowerFlow.NewtonRaphsonPowerFlow().Solve(m, new GridSim.Core.PowerFlow.PowerFlowOptions { EnforceQLimits = true });
        MeasurementSet set = SyntheticMeasurements.Sample(m, truth, new SyntheticMeasurementOptions { NoiseScale = 1.0 });
        // One gross error so bad-data analysis (Dot) runs and removes it.
        var ms = set.Measurements.ToList();
        int k = ms.FindIndex(x => x.Kind == MeasurementKind.PInjMw);
        ms[k] = ms[k] with { Value = ms[k].Value + 40.0 * ms[k].Sigma };
        EstimationResult est = new WlsStateEstimator().Estimate(m, set with { Measurements = ms }, new EstimationOptions());
        Assert.Equal(EstimationStatus.Converged, est.Status);
        Assert.NotEmpty(est.Removed);   // the bad-data loop really ran
        foreach (double v in est.Vm) yield return v;
        foreach (double v in est.VaDeg) yield return v;
        yield return est.ObjectiveJ;
        foreach (MeasurementResidual r in est.Residuals) { yield return r.Residual; yield return r.NormalizedResidual; }
        foreach (BadDataRemoval b in est.Removed) yield return b.NormalizedResidual;
    }

    /// <summary>
    /// D1: the dense WLS estimate with bad-data analysis (the former FMA gain and lane-order Dot paths) and the
    /// classical swing (the former lane-order Dot and width-dependent SinCos) pinned to exact digests. Recorded at D1 in
    /// normal mode; the suite runs this in normal, DOTNET_EnableAVX2=0 and DOTNET_EnableHWIntrinsic=0 mode, where the
    /// production overloads select the AVX2, SSE and scalar arms, so passing in all three is the cross-mode proof.
    /// </summary>
    // Test changed 2026-10-19: the two estimate digests re-captured. 1.95.299 (Mark's ruling 2026-10-15, GR-066/383/418)
    // solves every CPU WLS estimate through the canonical sparse step instead of the dense LU at or below 200 buses; its
    // changelog says these small-case pins move in their last bits and are re-captured at the batch run. Before -> after:
    // case14 EBE934B45EFB4EB38E0C10102756A7FE169CD9B5875FF02895773EFBC34211B4 -> 8DC7C8403CD9C951CCBF417D4FD3E738FDA14F8E57743300D3B98D12A603B725,
    // case57 C22C6FD76F963E335DF9A7452DCF5C28372E4345BD047691F673AFE250A1DD86 -> 466AAED2B9D2BE5900CFF9CC39792E72E3ACBB1CAAAE47481E19E330C0002A48
    // (normal mode, 2026-10-19 run). The swing digest did not move. The cross-mode proof still needs the other two modes.
    [Theory]
    [InlineData("estimate-case14", "8DC7C8403CD9C951CCBF417D4FD3E738FDA14F8E57743300D3B98D12A603B725")]
    [InlineData("estimate-case57", "466AAED2B9D2BE5900CFF9CC39792E72E3ACBB1CAAAE47481E19E330C0002A48")]
    [InlineData("swing-7", "FAE86C7D05775B277AE9D3663C6D872FA752F3F37587ADFDEA319DCFDE75CAA6")]
    public void Formerly_tier_dependent_paths_give_the_pinned_bits_in_every_mode(string what, string digest)
    {
        IEnumerable<double> bits = what switch
        {
            "estimate-case14" => EstimateBits("14"),
            "estimate-case57" => EstimateBits("57"),
            _ => SwingBits(),
        };
        string actual = Sha(bits.ToList());
        Assert.True(actual == digest, $"{what}: digest {actual}, pinned {digest}");
    }

    private static IEnumerable<double> SwingBits()
    {
        const int n = 7;
        double[] h = { 6.0, 3.5, 4.2, 2.8, 5.1, 3.3, 7.7 };
        double[] d = { 0.5, 0.3, 0.2, 0.4, 0.6, 0.1, 0.3 };
        double[] pm = { 0.9, 0.7, 0.5, 0.6, 0.8, 0.4, 1.0 };
        double[] eq = { 1.05, 1.02, 1.08, 1.01, 1.04, 1.06, 1.03 };
        double[] delta0 = { 0.3, 0.1, -0.2, 0.05, 0.25, -0.1, 0.4 };
        var y = new Complex[n, n];
        var yf = new Complex[n, n];
        for (int i = 0; i < n; i++)
            for (int j = 0; j < n; j++)
            {
                double g = i == j ? 0.4 + 0.05 * i : 0.02 * ((i + j) % 3);
                double b = i == j ? -6.0 - 0.3 * i : 1.0 + 0.1 * ((i * j) % 4);
                y[i, j] = new Complex(g, b);
                yf[i, j] = (i == 2 || j == 2) ? new Complex(g * 0.2, b * 0.2) : new Complex(g, b);
            }
        var r = ClassicalMultiMachine.Simulate(h, d, pm, eq, delta0, y, 50.0, 0.002, 1.5, new ClassicalMultiMachine.Fault(0.1, 0.25, yf));
        foreach (var mt in r.Machines) { foreach (double v in mt.DeltaRad) yield return v; foreach (double v in mt.SpeedPu) yield return v; }
    }
}
