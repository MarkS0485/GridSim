using System;
using System.Collections.Generic;
using System.Numerics;
using GridSim.Core.Distribution;
using Xunit;

namespace GridSim.Tests;

/// <summary>
/// The three-phase node-voltage WLS distribution estimator. On a network solved by the sweep, a full set of
/// per-phase voltage magnitudes plus branch current phasors recovers the truth to machine precision. Because
/// the magnitudes alone cannot fix the angles, recovering the correct angles validates the (linear) current
/// model that anchors them.
/// </summary>
public class DistributionThreePhaseWlsTests
{
    private static MultiPhaseModel RadialUnbalanced()
    {
        Complex[] src = MultiPhaseModel.BalancedNominal(1.0);
        var z = PhaseImpedance.Diagonal(new Complex(0.02, 0.06));
        return new MultiPhaseModel(
            new[] { new PhaseBus(0, true, src), new PhaseBus(1, false), new PhaseBus(2, false) },
            new[] { new PhaseBranch(0, 1, z), new PhaseBranch(1, 2, z) },
            new[]
            {
                new PhaseLoad(1, new[] { new Complex(0.6, 0.2), new Complex(0.25, 0.08), new Complex(0.4, 0.12) }),
                new PhaseLoad(2, new[] { new Complex(0.3, 0.1), new Complex(0.5, 0.15), new Complex(0.2, 0.06) }),
            });
    }

    [Fact]
    public void Recovers_the_sweep_truth_to_machine_precision()
    {
        MultiPhaseModel model = RadialUnbalanced();
        MultiPhaseResult truth = new ThreePhaseSweep().Solve(model);
        Assert.True(truth.Converged);

        var idToIndex = new Dictionary<int, int>();
        for (int i = 0; i < truth.BusIds.Count; i++) idToIndex[truth.BusIds[i]] = i;

        // Per-phase voltage magnitudes at the non-source buses.
        var vmags = new List<ThreePhaseVMag>();
        foreach (int id in new[] { 1, 2 })
            for (int p = 0; p < 3; p++)
                vmags.Add(new ThreePhaseVMag(id, p, truth.V[idToIndex[id]][p].Magnitude, 0.001));

        // Per-phase branch current phasors, computed from the truth voltages via the series admittance.
        var currents = new List<ThreePhaseCurrent>();
        foreach (PhaseBranch br in model.Branches)
        {
            PhaseImpedance y = br.Z.Invert();
            Complex[] vf = truth.V[idToIndex[br.From]], vt = truth.V[idToIndex[br.To]];
            Complex[] diff = { vf[0] - vt[0], vf[1] - vt[1], vf[2] - vt[2] };
            Complex[] i = y.Multiply(diff);
            for (int p = 0; p < 3; p++)
                currents.Add(new ThreePhaseCurrent(br.From, br.To, p, i[p], 0.001));
        }

        ThreePhaseWlsResult r = ThreePhaseWlsEstimator.Estimate(model, vmags, currents);

        Assert.True(r.Converged, $"did not converge (iters {r.Iterations}, maxDelta {r.MaxDelta})");
        Assert.True(r.ObjectiveJ < 1e-10, $"objective {r.ObjectiveJ}");
        foreach (int id in new[] { 1, 2 })
        {
            int bi = idToIndex[id];
            for (int p = 0; p < 3; p++)
            {
                Assert.True((r.V[bi][p] - truth.V[bi][p]).Magnitude < 1e-8,
                    $"bus {id} phase {p}: {r.V[bi][p]} vs truth {truth.V[bi][p]}");
            }
        }
    }

    [Fact]
    public void Robust_wlav_mode_is_more_resistant_to_a_gross_measurement()
    {
        MultiPhaseModel model = RadialUnbalanced();
        MultiPhaseResult truth = new ThreePhaseSweep().Solve(model);
        var idToIndex = new Dictionary<int, int>();
        for (int i = 0; i < truth.BusIds.Count; i++) idToIndex[truth.BusIds[i]] = i;

        var vmags = new List<ThreePhaseVMag>();
        foreach (int id in new[] { 1, 2 })
            for (int p = 0; p < 3; p++)
                vmags.Add(new ThreePhaseVMag(id, p, truth.V[idToIndex[id]][p].Magnitude, 0.01));
        var currents = new List<ThreePhaseCurrent>();
        foreach (PhaseBranch br in model.Branches)
        {
            PhaseImpedance y = br.Z.Invert();
            Complex[] vf = truth.V[idToIndex[br.From]], vt = truth.V[idToIndex[br.To]];
            Complex[] i = y.Multiply(new[] { vf[0] - vt[0], vf[1] - vt[1], vf[2] - vt[2] });
            for (int p = 0; p < 3; p++) currents.Add(new ThreePhaseCurrent(br.From, br.To, p, i[p], 0.01));
        }

        // Corrupt one voltage-magnitude measurement (bus 1 phase A) with a gross error.
        vmags[0] = vmags[0] with { Value = vmags[0].Value + 0.2 };

        ThreePhaseWlsResult plain = ThreePhaseWlsEstimator.Estimate(model, vmags, currents);
        ThreePhaseWlsResult rob = ThreePhaseWlsEstimator.Estimate(model, vmags, currents, robust: true);

        int b1 = idToIndex[1];
        double plainErr = (plain.V[b1][0] - truth.V[b1][0]).Magnitude;
        double robErr = (rob.V[b1][0] - truth.V[b1][0]).Magnitude;
        Assert.True(robErr < plainErr, $"WLAV err {robErr} should be below plain WLS err {plainErr}");
    }
}
