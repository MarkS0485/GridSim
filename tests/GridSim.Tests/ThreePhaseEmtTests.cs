using System;
using System.Collections.Generic;
using GridSim.Core.Dynamics.Emt;
using Xunit;

namespace GridSim.Tests;

/// <summary>Three-phase (abc) EMT with mutual coupling: a balanced coupled line reproduces the analytic
/// positive-sequence phasor (the proof mutual coupling is handled - not three independent circuits), and a
/// single-line-to-ground fault produces the genuine unbalance (faulted-phase collapse + zero-sequence) that a
/// single-phase model cannot.</summary>
public class ThreePhaseEmtTests
{
    private const double Rs = 1.0, Ls = 1e-3, Rm = 0.2, Lm = 0.3e-3, Rload = 10.0, F = 50.0, Dt = 20e-6;

    private static ThreePhaseNetwork Net(IReadOnlyList<ThreePhaseFault> faults) => new(
        BusCount: 2,
        Sources: new[] { new ThreePhaseSource(0, 1.0, F) },
        Branches: new[] { new ThreePhaseBranch(0, 1, Rs, Ls, Rm, Lm) },
        Shunts: new[] { new ThreePhaseShunt(1, G: 1.0 / Rload) },
        Faults: faults);

    private static double Rms(double[][] v, int phase, double[] t, double t0, double t1)
    {
        double sum = 0; int n = 0;
        for (int k = 0; k < t.Length; k++) if (t[k] >= t0 && t[k] <= t1) { sum += v[k][phase] * v[k][phase]; n++; }
        return n > 0 ? Math.Sqrt(sum / n) : 0;
    }

    [Fact]
    public void Balanced_coupled_line_matches_the_positive_sequence_phasor()
    {
        var eng = new ThreePhaseEmtEngine(Net(Array.Empty<ThreePhaseFault>()), Dt, F);
        (double[] t, double[][] v) = eng.Simulate(0.1, probeBus: 1);

        // Positive-sequence divider: Z1 = (Rs-Rm) + jw(Ls-Lm), load Rload. Va(t) = |Vload| cos(wt + phi).
        double w = 2 * Math.PI * F, r1 = Rs - Rm, x1 = w * (Ls - Lm);
        double zmag = Math.Sqrt((r1 + Rload) * (r1 + Rload) + x1 * x1);
        double vmag = Rload / zmag, phi = -Math.Atan2(x1, r1 + Rload);

        double maxErr = 0, maxImbalance = 0;
        for (int k = 0; k < t.Length; k++)
            if (t[k] >= 0.08) // steady state (positive-seq transient tau ~ 65 us decays in << 1 cycle)
            {
                maxErr = Math.Max(maxErr, Math.Abs(v[k][0] - vmag * Math.Cos(w * t[k] + phi)));
                maxImbalance = Math.Max(maxImbalance, Math.Abs(v[k][0] + v[k][1] + v[k][2])); // zero-seq = 0 when balanced
            }
        Assert.True(maxErr < 5e-3, $"phase-a vs positive-sequence phasor: max err {maxErr:E3}");
        Assert.True(maxImbalance < 5e-3, $"balanced set should sum to ~0: {maxImbalance:E3}");
    }

    [Fact]
    public void Single_line_to_ground_fault_collapses_the_faulted_phase_and_creates_zero_sequence()
    {
        var faults = new[] { new ThreePhaseFault(1, new[] { 0 }, Rf: 0.01, AtSeconds: 0.04, ClearSeconds: 0.09) };
        var eng = new ThreePhaseEmtEngine(Net(faults), Dt, F);
        (double[] t, double[][] v) = eng.Simulate(0.13, probeBus: 1);

        double rmsA = Rms(v, 0, t, 0.05, 0.085);   // during fault, settled
        double rmsB = Rms(v, 1, t, 0.05, 0.085);
        double rmsC = Rms(v, 2, t, 0.05, 0.085);
        double rmsBefore = Rms(v, 0, t, 0.02, 0.038); // phase-a pre-fault
        double rmsAfter = Rms(v, 0, t, 0.10, 0.128);  // phase-a post-clear (recovered)

        Assert.True(rmsA < 0.25 * rmsB, $"faulted phase A ({rmsA:F3}) should collapse vs healthy B ({rmsB:F3})");
        Assert.True(rmsC > 0.5 * rmsBefore, "healthy phase C should stay energised");
        Assert.True(rmsAfter > 0.7 * rmsBefore, "phase A should recover after the fault clears");

        // Zero-sequence (va+vb+vc) is ~0 balanced but non-zero during an unbalanced fault - the hallmark a
        // single-phase model cannot show.
        double zeroSeqFault = 0; int nf = 0;
        for (int k = 0; k < t.Length; k++) if (t[k] >= 0.05 && t[k] <= 0.085) { zeroSeqFault += Math.Abs(v[k][0] + v[k][1] + v[k][2]); nf++; }
        Assert.True(zeroSeqFault / nf > 0.05, "an SLG fault must create a zero-sequence voltage");
    }
}
