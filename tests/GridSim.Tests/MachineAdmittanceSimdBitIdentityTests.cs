using System.Numerics;
using System.Security.Cryptography;
using GridSim.Core.Dynamics.Models;
using GridSim.Core.IO;
using GridSim.Core.Model;
using GridSim.Core.Numerics;
using GridSim.Core.PowerFlow;
using Xunit;

namespace GridSim.Tests;

/// <summary>
/// R116 (October 2026 review, S5): the dense RMS network product <c>I_m = Y E</c> now runs on the vector units
/// (<c>MachineAdmittanceColumns</c>: one lane per row, each row summed over the columns in the historical order). These
/// tests compare every tier's arm (AVX-512, AVX2, SSE, scalar - all run on any CPU through the explicit-tier seam) with
/// the historical scalar loop, kept verbatim below, bit for bit; and pin whole dense RMS runs on IEEE 118 and IEEE 300
/// (54 and 69 machines, so every vector arm carries real rows) to digests RECORDED FROM THE SCALAR CODE BEFORE THE
/// CHANGE (1.95.126).
/// </summary>
public class MachineAdmittanceSimdBitIdentityTests
{
    /// <summary>The pre-R116 product (DenseMachineNetwork.MultiplyMachineInto), verbatim.</summary>
    private static void ReferenceMultiply(Complex[,] y, Complex[] e, Complex[] im)
    {
        int n = e.Length;
        for (int i = 0; i < n; i++)
        {
            double sr = 0.0, si = 0.0;
            for (int j = 0; j < n; j++)
            {
                double g = y[i, j].Real, b = y[i, j].Imaginary;
                sr += g * e[j].Real - b * e[j].Imaginary;
                si += g * e[j].Imaginary + b * e[j].Real;
            }
            im[i] = new Complex(sr, si);
        }
    }

    private static void AssertBits(Complex[] expected, Complex[] actual, string what)
    {
        Assert.Equal(expected.Length, actual.Length);
        for (int i = 0; i < expected.Length; i++)
        {
            Assert.True(BitConverter.DoubleToInt64Bits(expected[i].Real) == BitConverter.DoubleToInt64Bits(actual[i].Real),
                $"{what} row {i} real: {expected[i].Real:R} vs {actual[i].Real:R}");
            Assert.True(BitConverter.DoubleToInt64Bits(expected[i].Imaginary) == BitConverter.DoubleToInt64Bits(actual[i].Imaginary),
                $"{what} row {i} imag: {expected[i].Imaginary:R} vs {actual[i].Imaginary:R}");
        }
    }

    /// <summary>A finite value with awkward bits now and then: signed zeros, subnormals, huge and tiny magnitudes.</summary>
    private static double Awkward(Random rng) => rng.Next(12) switch
    {
        0 => 0.0,
        1 => -0.0,
        2 => double.Epsilon * rng.Next(1, 1000),
        3 => -1e300 * rng.NextDouble(),
        4 => 1e-300 * rng.NextDouble(),
        _ => (rng.NextDouble() - 0.5) * Math.Pow(10, rng.Next(-6, 7)),
    };

    [Theory]
    [InlineData(SimdTier.Scalar)]
    [InlineData(SimdTier.Sse128)]
    [InlineData(SimdTier.Avx256)]
    [InlineData(SimdTier.Avx512)]
    public void Every_tier_matches_the_scalar_product_bit_for_bit(SimdTier tier)
    {
        var rng = new Random(116 + (int)tier);
        for (int n = 0; n <= 41; n++)   // every remainder of every width, and n = 0
        {
            for (int trial = 0; trial < 3; trial++)
            {
                var y = new Complex[n, n];
                for (int i = 0; i < n; i++)
                    for (int j = 0; j < n; j++) y[i, j] = new Complex(Awkward(rng), Awkward(rng));
                var e = new Complex[n];
                for (int j = 0; j < n; j++) e[j] = new Complex(Awkward(rng), Awkward(rng));

                var expected = new Complex[n];
                ReferenceMultiply(y, e, expected);
                var actual = new Complex[n];
                Array.Fill(actual, new Complex(double.NaN, double.NaN));   // dirty: every row must be written
                new MachineAdmittanceColumns(y).MultiplyInto(e, actual, tier);
                AssertBits(expected, actual, $"{tier} n={n} trial {trial}");
            }
        }
    }

    [Fact]
    public void Networks_use_the_copy_taken_at_construction_and_match_the_reference()
    {
        var rng = new Random(9);
        const int n = 13;
        var y = new Complex[n, n];
        for (int i = 0; i < n; i++)
            for (int j = 0; j < n; j++) y[i, j] = new Complex(rng.NextDouble(), -rng.NextDouble());
        var e = Enumerable.Range(0, n).Select(k => Complex.FromPolarCoordinates(1.0 + 0.01 * k, 0.1 * k)).ToArray();
        var expected = new Complex[n];
        ReferenceMultiply(y, e, expected);

        var net = new DenseMachineNetwork(y);
        AssertBits(expected, net.Solve(e, Array.Empty<Complex>()).MachineCurrents, "Solve");
        var im = new Complex[n];
        net.SolveInto(e, Array.Empty<Complex>(), im, Array.Empty<Complex>());
        AssertBits(expected, im, "SolveInto");
        Assert.Throws<ArgumentException>(() => net.Solve(new Complex[n - 1], Array.Empty<Complex>()));
    }

    private static string Digest(MultiMachineSubtransient.Result r)
    {
        using var ms = new MemoryStream();
        using (var w = new BinaryWriter(ms, System.Text.Encoding.UTF8, leaveOpen: true))
        {
            w.Write(r.StayedInSynchronism);
            w.Write(r.ConverterIterationFailures);
            foreach (MultiMachineSubtransient.MachineTrace t in r.Machines)
                foreach (IReadOnlyList<double> s in new[] { t.TimeSeconds, t.DeltaRad, t.SpeedPu, t.EqpPu, t.EdpPu, t.EqppPu, t.EddpPu })
                {
                    w.Write(s.Count);
                    foreach (double v in s) w.Write(BitConverter.DoubleToInt64Bits(v));
                }
            foreach (RmsConverterTrace c in r.Converters ?? Array.Empty<RmsConverterTrace>())
            {
                foreach (IReadOnlyList<double> s in new[] { c.TerminalVoltagePu, c.ActivePowerPu, c.ReactivePowerPu, c.ActiveCurrentPu, c.ReactiveCurrentPu })
                {
                    w.Write(s.Count);
                    foreach (double v in s) w.Write(BitConverter.DoubleToInt64Bits(v));
                }
                foreach (bool b in c.InDipMode) w.Write(b);
            }
        }
        return Convert.ToHexString(SHA256.HashData(ms.ToArray()));
    }

    /// <summary>A dense RMS fault-and-clear run on a MATPOWER case: every generator a machine (H = max(H, 3) s,
    /// X''d = 0.2 pu machine base), a bolted fault at the middle bus from 0.1 s cleared after 80 ms, 2 s at 5 ms. With
    /// <paramref name="battery"/> every fifth generator is converter plant, so the run goes through ConverterNetwork.</summary>
    internal static MultiMachineSubtransient.Result DenseRun(GridModel b, bool battery)
    {
        var gens = b.Generators.Select((g, k) => battery && k % 5 == 4 ? g with { Type = "battery" } : g).ToList();
        GridModel m = b.CopyWith(generators: gens);
        PowerFlowResult pf = new NewtonRaphsonPowerFlow().Solve(m);
        Assert.True(pf.Converged);
        var data = m.Generators.Select(g =>
        {
            const double x = 0.2;
            return new SubtransientInit.MachineData(Math.Max(g.H, 3.0), 0.0, 3 * x, x, 0.8 * x, 2.5 * x, 1.5 * x, 0.8 * x, 8.0, 0.03, 0.4, 0.05);
        }).ToList();
        int fault = m.Buses[m.Buses.Count / 2].Id;
        // N271: the dense Kron run is now the test oracle (was sparseNetwork: false); same network, so the digests are unchanged.
        using (SubtransientInit.DenseKronReferenceScope())
            return RmsTransientStability.Study(m, pf, data, fault, 0.1, 0.08, stepSeconds: 0.005, durationSeconds: 2.0);
    }

    [Theory]
    // Moved 2026-10 (D1, Mark's determinism rule 2026-10-04: identical bits on every CPU tier and OS): the three
    // recorded digests moved because the RMS run's power flow, machine initialisation and network interface now use GridSim's
    // deterministic fdlibm sin / cos / atan2 instead of the Windows C runtime. Before -> after: 118/no battery 256B6130... ->
    // AB8F39EF..., 118/battery 3BA9F372... -> E846BB1C..., 300/no battery BB764EAA... -> 21EBA8DC...
    // (hashes abbreviated). The SIMD-vs-scalar theory above still proves the kernel.
    [InlineData("118", false, "AB8F39EFFA60F7A514ED30A4FCD4E0BAD9781DE5319F543321D91FD895870E81")]
    [InlineData("118", true, "E846BB1CC3F725C7608CDF96E927159FE0EA4452BCC582F4AC316190170FC2CF")]
    [InlineData("300", false, "21EBA8DC9DC4FD342CDB62B1FAAE8F6E73FBD10640C2278A945BF33336026869")]
    public void Dense_rms_runs_are_bit_identical_to_the_scalar_kernel(string name, bool battery, string recordedBefore)
    {
        GridModel b = name == "118" ? IeeeCases.Case118() : IeeeCases.Case300();
        Assert.Equal(recordedBefore, Digest(DenseRun(b, battery)));
    }
}
