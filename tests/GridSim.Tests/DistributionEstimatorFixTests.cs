using System.Numerics;
using GridSim.Core.Distribution;
using Xunit;

namespace GridSim.Tests;

/// <summary>
/// R233: BranchCurrentDsse rejects meshed networks (Converged = false) instead of silently ignoring loop
/// branches. R236: ThreePhaseWlsEstimator honours current measurements authored with reversed endpoints and
/// fails loud on ones that match no branch. R237: its objective excludes source-bus magnitude residuals the
/// fit cannot reduce.
/// </summary>
public class DistributionEstimatorFixTests
{
    private static readonly PhaseImpedance Z = PhaseImpedance.Diagonal(new Complex(0.02, 0.06));

    private static MultiPhaseModel Feeder(params PhaseBranch[] branches) => new(
        new[] { new PhaseBus(0, true, MultiPhaseModel.BalancedNominal(1.0)), new PhaseBus(1, false), new PhaseBus(2, false) },
        branches,
        new[]
        {
            new PhaseLoad(1, new[] { new Complex(0.5, 0.2), new Complex(0.3, 0.1), new Complex(0.4, 0.12) }),
            new PhaseLoad(2, new[] { new Complex(0.2, 0.05), new Complex(0.45, 0.15), new Complex(0.25, 0.08) }),
        });

    private static MultiPhaseModel Radial() => Feeder(new PhaseBranch(0, 1, Z), new PhaseBranch(1, 2, Z));

    /// <summary>Exact branch-current phasors and per-phase magnitudes from the sweep truth of a radial feeder.</summary>
    private static (MultiPhaseResult Truth, List<ThreePhaseCurrent> Currents, List<ThreePhaseVMag> VMags) Truth(MultiPhaseModel model)
    {
        MultiPhaseResult truth = new ThreePhaseSweep().Solve(model);
        var idx = new Dictionary<int, int>();
        for (int i = 0; i < truth.BusIds.Count; i++) idx[truth.BusIds[i]] = i;
        var currents = new List<ThreePhaseCurrent>();
        foreach (PhaseBranch br in model.Branches)
        {
            Complex[] vf = truth.V[idx[br.From]], vt = truth.V[idx[br.To]];
            Complex[] i = br.Z.Invert().Multiply(new[] { vf[0] - vt[0], vf[1] - vt[1], vf[2] - vt[2] });
            for (int p = 0; p < 3; p++) currents.Add(new ThreePhaseCurrent(br.From, br.To, p, i[p], 0.001));
        }
        var vmags = new List<ThreePhaseVMag>();
        foreach (int id in new[] { 1, 2 })
            for (int p = 0; p < 3; p++) vmags.Add(new ThreePhaseVMag(id, p, truth.V[idx[id]][p].Magnitude, 0.001));
        return (truth, currents, vmags);
    }

    // ------------------------------------------------------------------ R233

    [Fact]
    public void Branch_current_dsse_flags_a_meshed_network()
    {
        // A loop-closing branch 0-2 makes the network meshed; its current measurement could never be used.
        (_, List<ThreePhaseCurrent> currents, _) = Truth(Radial());
        MultiPhaseModel meshed = Feeder(new PhaseBranch(0, 1, Z), new PhaseBranch(1, 2, Z), new PhaseBranch(0, 2, Z));
        var withLoop = currents.ToList();
        for (int p = 0; p < 3; p++) withLoop.Add(new ThreePhaseCurrent(0, 2, p, new Complex(0.1, 0.0), 0.001));
        Assert.False(BranchCurrentDsse.Estimate(meshed, withLoop).Converged);
    }

    [Fact]
    public void Branch_current_dsse_flags_parallel_branches()
    {
        (_, List<ThreePhaseCurrent> currents, _) = Truth(Radial());
        MultiPhaseModel parallel = Feeder(new PhaseBranch(0, 1, Z), new PhaseBranch(0, 1, Z), new PhaseBranch(1, 2, Z));
        Assert.False(BranchCurrentDsse.Estimate(parallel, currents).Converged);
    }

    [Fact]
    public void Branch_current_dsse_still_converges_on_a_radial_feeder()
    {
        MultiPhaseModel model = Radial();
        (_, List<ThreePhaseCurrent> currents, _) = Truth(model);
        Assert.True(BranchCurrentDsse.Estimate(model, currents).Converged);
    }

    // ------------------------------------------------------------------ R236

    [Fact]
    public void Reversed_endpoint_currents_give_the_same_estimate_as_model_oriented_ones()
    {
        MultiPhaseModel model = Radial();
        (MultiPhaseResult truth, List<ThreePhaseCurrent> currents, List<ThreePhaseVMag> vmags) = Truth(model);
        // Author branch 1-2's measurements from the other end: current into bus 2 is -I_from.
        List<ThreePhaseCurrent> reversed = currents
            .Select(c => c.FromBusId == 1 && c.ToBusId == 2 ? new ThreePhaseCurrent(2, 1, c.Phase, -c.Value, c.Sigma) : c)
            .ToList();

        ThreePhaseWlsResult a = ThreePhaseWlsEstimator.Estimate(model, vmags, currents);
        ThreePhaseWlsResult b = ThreePhaseWlsEstimator.Estimate(model, vmags, reversed);
        Assert.True(b.Converged);
        // Negating an exact double is exact, so the two inputs are identical after re-orientation: bit-equal.
        for (int i = 0; i < a.V.Length; i++)
            for (int p = 0; p < 3; p++) Assert.Equal(a.V[i][p], b.V[i][p]);
        Assert.Equal(a.ObjectiveJ, b.ObjectiveJ);
    }

    [Fact]
    public void A_current_matching_no_branch_fails_loud()
    {
        MultiPhaseModel model = Radial();
        (_, List<ThreePhaseCurrent> currents, List<ThreePhaseVMag> vmags) = Truth(model);
        var bad = currents.ToList();
        bad.Add(new ThreePhaseCurrent(0, 2, 0, new Complex(0.1, 0.0), 0.001));   // no 0-2 branch exists
        Assert.Throws<ArgumentException>(() => ThreePhaseWlsEstimator.Estimate(model, vmags, bad));
    }

    // ------------------------------------------------------------------ R237

    [Fact]
    public void Source_bus_magnitude_residuals_are_not_counted_in_the_objective()
    {
        MultiPhaseModel model = Radial();
        (_, List<ThreePhaseCurrent> currents, List<ThreePhaseVMag> vmags) = Truth(model);
        ThreePhaseWlsResult clean = ThreePhaseWlsEstimator.Estimate(model, vmags, currents);

        // A source-bus magnitude 0.05 pu off the fixed source voltage: no state can reduce it, the fit ignores
        // it, and it must not inflate J (it used to add (0.05/0.001)^2 = 2500).
        var withSource = vmags.ToList();
        withSource.Add(new ThreePhaseVMag(0, 0, 1.05, 0.001));
        ThreePhaseWlsResult r = ThreePhaseWlsEstimator.Estimate(model, withSource, currents);
        Assert.Equal(clean.ObjectiveJ, r.ObjectiveJ);
        for (int i = 0; i < clean.V.Length; i++)
            for (int p = 0; p < 3; p++) Assert.Equal(clean.V[i][p], r.V[i][p]);
    }
}
