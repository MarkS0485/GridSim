using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using GridSim.Core.Distribution;
using Xunit;

namespace GridSim.Tests;

/// <summary>Pseudo-measurement generation: nominal voltage-magnitude priors for the unmetered non-source
/// buses only, deterministic and with a loose sigma.</summary>
public class DistributionPseudoMeasurementsTests
{
    [Fact]
    public void Generates_nominal_voltage_pseudos_for_unmetered_buses_only()
    {
        var model = new MultiPhaseModel(
            new[] { new PhaseBus(0, true, MultiPhaseModel.BalancedNominal()), new PhaseBus(1, false), new PhaseBus(2, false) },
            new[] { new PhaseBranch(0, 1, PhaseImpedance.Diagonal(new Complex(0.02, 0.05))),
                    new PhaseBranch(1, 2, PhaseImpedance.Diagonal(new Complex(0.02, 0.05))) },
            Array.Empty<PhaseLoad>());

        IReadOnlyList<ThreePhaseVMag> pseudos =
            DistributionPseudoMeasurements.NominalVoltageMagnitudes(model, new HashSet<int> { 1 }, nominalPu: 1.0, sigma: 0.05);

        // Source (0) excluded, bus 1 metered -> excluded; only bus 2's three phases remain.
        Assert.Equal(3, pseudos.Count);
        Assert.All(pseudos, m => Assert.Equal(2, m.BusId));
        Assert.All(pseudos, m => Assert.Equal(1.0, m.Value));
        Assert.All(pseudos, m => Assert.Equal(0.05, m.Sigma));
        Assert.Equal(new[] { 0, 1, 2 }, pseudos.Select(m => m.Phase).OrderBy(p => p));
    }

    // GR-584: a single-phase lateral gets a prior only on the phase it carries. It used to get three, two of them on
    // phases the bus does not have (a nominal 1 pu where the true voltage is exactly 0).
    [Fact]
    public void A_single_phase_lateral_gets_pseudos_only_on_its_own_phase()
    {
        var za = new Complex[3, 3];
        za[0, 0] = new Complex(0.02, 0.06);
        var model = new MultiPhaseModel(
            new[] { new PhaseBus(0, true, MultiPhaseModel.BalancedNominal()), new PhaseBus(1, false), new PhaseBus(2, false) },
            new[] { new PhaseBranch(0, 1, PhaseImpedance.Diagonal(new Complex(0.02, 0.05))),
                    new PhaseBranch(1, 2, new PhaseImpedance(za), Phase.A) },
            Array.Empty<PhaseLoad>());

        IReadOnlyList<ThreePhaseVMag> pseudos =
            DistributionPseudoMeasurements.NominalVoltageMagnitudes(model, new HashSet<int>(), nominalPu: 1.0, sigma: 0.05);

        Assert.Equal(new[] { 0, 1, 2 }, pseudos.Where(m => m.BusId == 1).Select(m => m.Phase).OrderBy(p => p));
        Assert.Equal(new[] { 0 }, pseudos.Where(m => m.BusId == 2).Select(m => m.Phase));
        Assert.Equal(4, pseudos.Count);
    }
}
