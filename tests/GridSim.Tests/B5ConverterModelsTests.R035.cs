using System.Numerics;
using GridSim.Core.Dynamics.Distributed;
using GridSim.Core.IO;
using GridSim.Core.Model;
using GridSim.Core.Numerics;
using GridSim.Core.Physics.Experimental;
using GridSim.Core.PowerFlow;
using Xunit;

namespace GridSim.Tests;

/// <summary>
/// R035 (with the R185 remainder): the wave model handed an ASYMMETRIC synchronising Laplacian (transfer conductances
/// make k_ij differ from k_ji) to the symmetric Jacobi eigen-solver, which reads only the upper triangle, so it
/// diagonalised a matrix whose rows do not sum to zero: the centre-of-inertia mode was lifted off zero and the inter-area
/// mode came out wrong. The coupling is now symmetrised explicitly, k_sym = (k_ij + k_ji) / 2 (the lossless synchronising
/// part, the lens's stated premise), and the part set aside is reported. The stiffness figure from a solved case is now in
/// the MW/rad its name states. Symmetric inputs (every direct caller) are unchanged bit for bit: the region carries the
/// pre-R035 Analyze verbatim (1.95.223, names changed only) as the oracle.
/// </summary>
public sealed partial class B5ConverterModelsTests
{
    [Fact]
    public void R035_the_review_two_machine_case_keeps_an_exact_coi_null_and_the_true_inter_area_mode()
    {
        // FULL-REVIEW-2026-10.md R035 failure scenario: two machines, E = 1, H = 5 s each, Yred12 = 0.5 + j5, delta12 = 0.3.
        double d12 = 0.3;
        double k12 = 5.0 * Math.Cos(d12) - 0.5 * Math.Sin(d12);    // 4.629
        double k21 = 5.0 * Math.Cos(-d12) - 0.5 * Math.Sin(-d12);  // 4.924
        var k = new double[,] { { 0.0, k12 }, { k21, 0.0 } };
        double[] h = { 5.0, 5.0 };
        ContinuumWaveModel.WaveModes w = ContinuumWaveModel.Analyze(h, k, 50.0);

        // The true problem M^-1 K_L (equal masses) has eigenvalues 0 and (k12 + k21) / M: the symmetric part has the same.
        double m = 2.0 * 5.0 / (2.0 * Math.PI * 50.0);
        double trueHz = Math.Sqrt((k12 + k21) / m) / (2.0 * Math.PI);   // about 2.76 Hz
        Assert.True(w.CoiModeHz < ContinuumWaveModel.NonZeroModeThresholdHz, $"COI mode {w.CoiModeHz:R} Hz must be ~0");
        Assert.True(Math.Abs(w.DominantInterAreaHz - trueHz) <= 1e-12 * trueHz,
            $"dominant {w.DominantInterAreaHz:R} Hz vs the true {trueHz:R} Hz (the pre-R035 code reported about 0.33 Hz)");
        Assert.InRange(w.DominantInterAreaHz, 2.7, 2.8);
        AssertBits(Math.Abs(k12 - k21), w.MaxCouplingAsymmetry, "asymmetry set aside");
        // The mean stiffness is that of the symmetric coupling.
        AssertBits(Math.Max(0.0, 0.5 * (k12 + k21)), w.MeanSynchronisingMwPerRad, "mean K of the symmetric part");
    }

    [Fact]
    public void R035_a_symmetric_coupling_gives_the_pre_R035_numbers_bit_for_bit()
    {
        var rng = new Random(20261006);
        for (int trial = 0; trial < 40; trial++)
        {
            int n = 2 + rng.Next(9);
            var h = new double[n];
            var k = new double[n, n];
            var d = new double[n];
            for (int i = 0; i < n; i++)
            {
                h[i] = trial % 7 == 0 && i == 0 ? 0.0 : 0.5 + 9.0 * rng.NextDouble();   // a zero H reaches the floor
                d[i] = rng.NextDouble();
            }
            for (int i = 0; i < n; i++)
                for (int j = i + 1; j < n; j++)
                {
                    double r = rng.NextDouble();
                    // Mostly positive ties, some absent (islands), some negative (clamped).
                    double v = r < 0.2 ? 0.0 : r < 0.3 ? -rng.NextDouble() : 20.0 * rng.NextDouble();
                    k[i, j] = v; k[j, i] = v;
                }
            double[]? damping = trial % 2 == 0 ? d : null;
            ContinuumWaveModel.WaveModes now = ContinuumWaveModel.Analyze(h, k, trial % 3 == 0 ? 60.0 : 50.0, damping);
            OldWave old = OldAnalyze(h, k, trial % 3 == 0 ? 60.0 : 50.0, damping);

            Assert.Equal(old.Freqs.Length, now.ModeFrequenciesHz.Count);
            for (int i = 0; i < old.Freqs.Length; i++) AssertBits(old.Freqs[i], now.ModeFrequenciesHz[i], $"trial {trial} mode {i}");
            AssertBits(old.Coi, now.CoiModeHz, $"trial {trial} COI");
            Assert.Equal(old.InterArea.Length, now.InterAreaModesHz.Count);
            for (int i = 0; i < old.InterArea.Length; i++) AssertBits(old.InterArea[i], now.InterAreaModesHz[i], $"trial {trial} inter-area {i}");
            AssertBits(old.Dominant, now.DominantInterAreaHz, $"trial {trial} dominant");
            AssertBits(old.Zeta, now.DominantDampingRatio, $"trial {trial} zeta");
            AssertBits(old.MeanK, now.MeanSynchronisingMwPerRad, $"trial {trial} mean K");
            AssertBits(old.MeanH, now.MeanInertiaSeconds, $"trial {trial} mean H");
            Assert.Equal(old.Rigid, now.RigidBodyModeCount);
            Assert.Equal(old.Shape.Length, now.DominantModeShape.Count);
            for (int i = 0; i < old.Shape.Length; i++)
            {
                Assert.Equal(old.Shape[i].Machine, now.DominantModeShape[i].Machine);
                AssertBits(old.Shape[i].Amplitude, now.DominantModeShape[i].Amplitude, $"trial {trial} shape {i}");
            }
            AssertBits(0.0, now.MaxCouplingAsymmetry, $"trial {trial} asymmetry of a symmetric coupling");
        }
    }

    [Fact]
    public void R035_case9_analyses_the_symmetric_part_and_reports_mw_per_rad()
    {
        GridModel m = IeeeCases.Case9();
        PowerFlowResult r = new NewtonRaphsonPowerFlow().Solve(m);
        Assert.True(r.Converged);
        ContinuumWaveModel.WaveModes w = ContinuumWaveModel.FromSolvedCase(m, r);

        // Rebuild the per-unit coefficients exactly as FromSolvedCase does and analyse them directly.
        ClassicalInit.Prepared p = ClassicalInit.FromSolvedCase(m, r, null);
        int g = p.Eq.Length;
        var k = new double[g, g];
        for (int i = 0; i < g; i++)
            for (int j = 0; j < g; j++)
            {
                if (i == j) continue;
                double dij = p.Delta0[i] - p.Delta0[j];
                Complex y = p.YReduced[i, j];
                k[i, j] = p.Eq[i] * p.Eq[j] * (y.Imaginary * DetMath.Cos(dij) - y.Real * DetMath.Sin(dij));
            }
        ContinuumWaveModel.WaveModes direct = ContinuumWaveModel.Analyze(p.H, k, m.FrequencyHz, d: null, machineIds: p.GeneratorBuses.ToArray());

        // The modes do not depend on the reporting scale; the two stiffness figures are the per-unit ones times the base.
        Assert.Equal(direct.ModeFrequenciesHz.Count, w.ModeFrequenciesHz.Count);
        for (int i = 0; i < w.ModeFrequenciesHz.Count; i++) AssertBits(direct.ModeFrequenciesHz[i], w.ModeFrequenciesHz[i], $"mode {i}");
        AssertBits(direct.DominantInterAreaHz, w.DominantInterAreaHz, "dominant");
        AssertBits(direct.MeanSynchronisingMwPerRad * m.BaseMva, w.MeanSynchronisingMwPerRad, "mean K in MW/rad");
        AssertBits(direct.MaxCouplingAsymmetry * m.BaseMva, w.MaxCouplingAsymmetry, "asymmetry in MW/rad");

        // case9 has load admittances, so the reduced network has transfer conductances and the coupling is asymmetric.
        Assert.True(w.MaxCouplingAsymmetry > 0.0, "case9's coupling should be asymmetric (transfer conductances)");
        // The symmetric part's rows sum to zero, so the COI mode is ~0 (round-off of the eigen-solve only, far below any
        // electromechanical mode) and is not the dominant mode.
        Assert.True(w.CoiModeHz < 1e-4, $"COI mode {w.CoiModeHz:R} Hz");
        Assert.Equal(1, w.RigidBodyModeCount);
        Assert.True(w.DominantInterAreaHz > 0.1, $"dominant {w.DominantInterAreaHz:R} Hz should be a real electromechanical mode");
    }

    [Fact]
    public void R035_a_coupling_of_the_wrong_size_is_refused()
    {
        var ex = Assert.Throws<ArgumentException>(() => ContinuumWaveModel.Analyze(new[] { 5.0, 5.0, 5.0 }, new double[2, 2], 50.0));
        Assert.Equal("k", ex.ParamName);
    }

    // ---- the pre-R035 Analyze, verbatim from 1.95.223 (names changed only), as the bit-identity oracle ----

    private sealed record OldWave(double[] Freqs, double Coi, double[] InterArea, double Dominant, double Zeta, double MeanK,
        double MeanH, ContinuumWaveModel.MachineAmplitude[] Shape, int Rigid);

    private static OldWave OldAnalyze(double[] h, double[,] k, double f0, double[]? d = null, int[]? machineIds = null)
    {
        int n = h.Length;
        double ws = 2.0 * Math.PI * f0;
        int[] ids = machineIds ?? Enumerable.Range(0, n).ToArray();

        var m = new double[n];
        for (int i = 0; i < n; i++) m[i] = 2.0 * Math.Max(ContinuumWaveModel.MinInertiaConstantS, h[i]) / ws;

        var kl = new double[n, n];
        double kSum = 0.0; int kCount = 0;
        for (int i = 0; i < n; i++)
        {
            double diag = 0.0;
            for (int j = 0; j < n; j++)
            {
                if (i == j) continue;
                double kij = Math.Max(0.0, k[i, j]);
                kl[i, j] = -kij;
                diag += kij;
                if (j > i && kij > 0) { kSum += kij; kCount++; }
            }
            kl[i, i] = diag;
        }

        var a = new double[n, n];
        for (int i = 0; i < n; i++)
            for (int j = 0; j < n; j++)
                a[i, j] = kl[i, j] / Math.Sqrt(m[i] * m[j]);

        (double[] lambda, double[,] psi) = SymmetricEigen.EigenDecomposition(a);
        var freqs = new double[n];
        for (int i = 0; i < n; i++) freqs[i] = Math.Sqrt(Math.Max(0.0, lambda[i])) / (2.0 * Math.PI);

        int rigid = ContinuumWaveModel.SynchronisingComponents(kl);
        double coi = freqs[0];
        var interArea = freqs.Skip(rigid).ToArray();

        int domIdx = rigid < n ? rigid : -1;
        double dominant = domIdx >= 0 ? freqs[domIdx] : 0.0;

        var shape = new List<ContinuumWaveModel.MachineAmplitude>(n);
        if (domIdx >= 0)
        {
            var phi = new double[n];
            double maxAbs = 0.0;
            for (int i = 0; i < n; i++) { phi[i] = psi[i, domIdx] / Math.Sqrt(m[i]); maxAbs = Math.Max(maxAbs, Math.Abs(phi[i])); }
            double norm = maxAbs > 0 ? 1.0 / maxAbs : 1.0;
            int imax = 0; for (int i = 1; i < n; i++) if (Math.Abs(phi[i]) > Math.Abs(phi[imax])) imax = i;
            double sign = phi[imax] < 0 ? -1.0 : 1.0;
            for (int i = 0; i < n; i++) shape.Add(new ContinuumWaveModel.MachineAmplitude(ids[i], phi[i] * norm * sign));
        }

        double zeta = 0.0;
        if (d is not null && dominant > ContinuumWaveModel.MinDampedModeHz)
        {
            double meanDoverM = 0.0;
            for (int i = 0; i < n; i++) meanDoverM += d[i] / m[i];
            meanDoverM /= n;
            zeta = meanDoverM / (2.0 * (2.0 * Math.PI * dominant));
        }

        double meanK = kCount > 0 ? kSum / kCount : 0.0;
        double meanH = h.Average();
        return new OldWave(freqs, coi, interArea, dominant, zeta, meanK, meanH, shape.ToArray(), rigid);
    }
}
