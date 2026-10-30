using System;
using System.Collections.Generic;
using System.Numerics;
using GridSim.Core.Distribution;
using Xunit;

namespace GridSim.Tests;

/// <summary>The branch-current DSSE: from the branch current phasors it recovers the node voltages by a
/// forward sweep, matching the sweep truth to machine precision - cross-validating the node-voltage estimator.</summary>
public class BranchCurrentDsseTests
{
    [Fact]
    public void Recovers_the_sweep_voltages_from_branch_currents()
    {
        Complex[] src = MultiPhaseModel.BalancedNominal(1.0);
        var z = PhaseImpedance.Diagonal(new Complex(0.02, 0.06));
        var model = new MultiPhaseModel(
            new[] { new PhaseBus(0, true, src), new PhaseBus(1, false), new PhaseBus(2, false) },
            new[] { new PhaseBranch(0, 1, z), new PhaseBranch(1, 2, z) },
            new[]
            {
                new PhaseLoad(1, new[] { new Complex(0.5, 0.2), new Complex(0.3, 0.1), new Complex(0.4, 0.12) }),
                new PhaseLoad(2, new[] { new Complex(0.2, 0.05), new Complex(0.45, 0.15), new Complex(0.25, 0.08) }),
            });

        MultiPhaseResult truth = new ThreePhaseSweep().Solve(model);
        var idToIndex = new Dictionary<int, int>();
        for (int i = 0; i < truth.BusIds.Count; i++) idToIndex[truth.BusIds[i]] = i;

        // Branch current phasors from the truth voltages (I_from = Y (V_from - V_to)).
        var currents = new List<ThreePhaseCurrent>();
        foreach (PhaseBranch br in model.Branches)
        {
            PhaseImpedance y = br.Z.Invert();
            Complex[] vf = truth.V[idToIndex[br.From]], vt = truth.V[idToIndex[br.To]];
            Complex[] i = y.Multiply(new[] { vf[0] - vt[0], vf[1] - vt[1], vf[2] - vt[2] });
            for (int p = 0; p < 3; p++) currents.Add(new ThreePhaseCurrent(br.From, br.To, p, i[p], 0.001));
        }

        BranchCurrentEstimate est = BranchCurrentDsse.Estimate(model, currents);

        Assert.True(est.Converged);
        foreach (int id in new[] { 1, 2 })
        {
            int bi = idToIndex[id];
            for (int p = 0; p < 3; p++)
                Assert.True((est.V[bi][p] - truth.V[bi][p]).Magnitude < 1e-9,
                    $"bus {id} phase {p}: {est.V[bi][p]} vs truth {truth.V[bi][p]}");
        }
    }
}
