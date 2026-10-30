using System.Numerics;
using GridSim.Core.Abstractions;
using GridSim.Core.Analysis;
using GridSim.Core.Dynamics;
using GridSim.Core.Estimation;
using GridSim.Core.IO;
using GridSim.Core.IO.Json;
using GridSim.Core.Model;
using GridSim.Core.Numerics;
using GridSim.Core.PowerFlow;
using GridSim.Core.Protection;
using GridSim.Core.Protection.ShortCircuit;
using GridSim.Core.Storage;
using GridSim.Gpu;
using Xunit;
using Xunit.Abstractions;

namespace GridSim.Tests;

/// <summary>DOC-16 D209: see the summary of <see cref="Doc16NumericsDeterminismTests"/>.</summary>
public partial class Doc16NumericsDeterminismTests
{
    // =================================================================================================================
    // D-DOC-209 / 210: the CPU side of the GPU findings.
    // =================================================================================================================

    [Fact]
    public void D210_the_shared_pivot_floor_decides_at_the_exact_boundary_in_every_cpu_lu()
    {
        // aNorm = 1, so the shared floor is exactly 1e-12: a pivot AT it is singular, the next double above it is not.
        double atFloor = NumericTolerances.PivotFloor(1.0);
        double above = Math.BitIncrement(atFloor);
        Assert.Equal(1e-12, atFloor);

        // The dense real LU (the WLS estimator's and the GPU fallback's solver).
        Assert.Throws<InvalidOperationException>(() => new DenseLuSolver().SolveInPlace(new double[,] { { 1, 0 }, { 0, atFloor } }, new[] { 1.0, 1.0 }));
        var x = new[] { 1.0, 1.0 };
        new DenseLuSolver().SolveInPlace(new double[,] { { 1, 0 }, { 0, above } }, x);
        Assert.Equal(1.0 / above, x[1]);

        // The batched CPU LU: the singular system alone is masked (NaN, flag false); the others are the dense LU's bits.
        double[] aFlat = { 2, 1, 1, 3,   1, 0, 0, atFloor,   1, 0, 0, above };
        double[] bFlat = { 1, 2,   1, 1,   1, 1 };
        bool[] ok = new CpuBatchedLuSolver().SolveBatch(3, 2, aFlat, bFlat);
        Assert.Equal(new[] { true, false, true }, ok);
        Assert.True(double.IsNaN(bFlat[2]) && double.IsNaN(bFlat[3]));
        var b0 = new[] { 1.0, 2.0 };
        new DenseLuSolver().SolveInPlace(new double[,] { { 2, 1 }, { 1, 3 } }, b0);
        Assert.Equal(Bits(b0[0]), Bits(bFlat[0]));
        Assert.Equal(Bits(b0[1]), Bits(bFlat[1]));
        Assert.Equal(Bits(1.0 / above), Bits(bFlat[5]));

        // The Kron reduction (the RMS / swing network reduction).
        Assert.Throws<InvalidOperationException>(() => KronReduction.Reduce(new Complex[,] { { 1, 0 }, { 0, atFloor } }, new[] { 0 }));
        KronReduction.Reduce(new Complex[,] { { 1, 0 }, { 0, above } }, new[] { 0 });

        // The ROCm post-factorisation check (host-callable): the same boundary on U(k,k).
        Assert.Throws<InvalidOperationException>(() => RocmDenseLinearSolver.ThrowIfNearSingular(new[] { 1.0, 0, 0, atFloor }, 2, 1.0));
        RocmDenseLinearSolver.ThrowIfNearSingular(new[] { 1.0, 0, 0, above }, 2, 1.0);
    }

    /// <summary>The textbook partial-pivoting LU (first largest pivot, separate multiply and subtract): the arithmetic of
    /// <see cref="DenseLuSolver"/>'s elimination, returned column-major with the matrix scale. Stands in for hipSOLVER's
    /// getrf, which cannot be run here (no AMD device).</summary>
    private static (double[] Lu, double Scale) HostLu(double[,] src)
    {
        int n = src.GetLength(0);
        var m = (double[,])src.Clone();
        double scale = NumericTolerances.MaxAbs(src);
        for (int k = 0; k < n; k++)
        {
            int p = k;
            double max = Math.Abs(m[k, k]);
            for (int i = k + 1; i < n; i++) { double v = Math.Abs(m[i, k]); if (v > max) { max = v; p = i; } }
            for (int j = 0; j < n; j++) (m[k, j], m[p, j]) = (m[p, j], m[k, j]);
            if (m[k, k] == 0.0) continue;
            for (int i = k + 1; i < n; i++)
            {
                double f = m[i, k] / m[k, k];
                if (f == 0.0) continue;
                m[i, k] = f;
                for (int j = k + 1; j < n; j++) m[i, j] -= f * m[k, j];
            }
        }
        var col = new double[n * n];
        for (int j = 0; j < n; j++) for (int i = 0; i < n; i++) col[j * n + i] = m[i, j];
        return (col, scale);
    }

    [Fact]
    public void D210_the_rocm_near_singular_check_gives_the_cpu_lus_verdict_on_a_randomised_set()
    {
        // D-DOC-210: the parity claim rested on one 3 x 3. Here: orders 1 to 12, 60 matrices each, half well conditioned
        // and half made near-singular (a row replaced by a combination of others plus a perturbation at, below or above
        // the floor's scale). For every matrix the ROCm check on the host factors must agree with the CPU LU exactly.
        var rng = new Random(20261005);
        int agree = 0, singular = 0;
        for (int n = 1; n <= 12; n++)
        {
            for (int trial = 0; trial < 60; trial++)
            {
                var a = new double[n, n];
                for (int i = 0; i < n; i++) for (int j = 0; j < n; j++) a[i, j] = rng.NextDouble() * 2 - 1;
                if (trial % 2 == 1)
                {
                    int r = rng.Next(n);
                    double eps = new[] { 0.0, 1e-16, 1e-13, 1e-12, 1e-11, 1e-9 }[rng.Next(6)];
                    for (int j = 0; j < n; j++)
                    {
                        double comb = 0;
                        for (int i = 0; i < n; i++) if (i != r) comb += (i % 3 - 1) * 0.5 * a[i, j];
                        a[r, j] = comb + eps * (rng.NextDouble() * 2 - 1);
                    }
                }
                bool cpuSingular;
                try { new DenseLuSolver().SolveInPlace((double[,])a.Clone(), new double[n]); cpuSingular = false; }
                catch (InvalidOperationException) { cpuSingular = true; }
                (double[] lu, double scale) = HostLu(a);
                bool rocmSingular;
                try { RocmDenseLinearSolver.ThrowIfNearSingular(lu, n, scale); rocmSingular = false; }
                catch (InvalidOperationException) { rocmSingular = true; }
                Assert.True(cpuSingular == rocmSingular, $"order {n}, trial {trial}: CPU singular {cpuSingular}, ROCm check singular {rocmSingular}");
                agree++;
                if (cpuSingular) singular++;
            }
        }
        _out.WriteLine($"{agree} matrices, verdicts identical ({singular} singular). The device's own getrf is NOT exercised (no AMD hardware).");
        Assert.True(singular > 20 && singular < agree - 20, "the set must hold both verdicts in number");
    }
}
