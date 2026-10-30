using System;
using System.Collections.Generic;
using System.Numerics;
using GridSim.Core.Distribution;
using GridSim.Core.Dynamics.Emt;
using Xunit;

namespace GridSim.Tests;

/// <summary>
/// R235: a three-phase load on an unknown bus fails loud instead of vanishing from the sweep.
/// R241: an EMT switch event naming a non-switch fails loud, and a no-op switch event leaves the trajectory
/// bit-identical (no spurious CDA damping burst or re-factor).
/// </summary>
public class DistributionAndEmtInputValidationTests
{
    // ---- R235 ---------------------------------------------------------------------------------------

    private static PhaseImpedance Z() => PhaseImpedance.Diagonal(new Complex(0.01, 0.03));

    [Fact]
    public void A_load_on_an_unknown_bus_is_refused_at_model_construction()
    {
        var buses = new[] { new PhaseBus(1, true, MultiPhaseModel.BalancedNominal()), new PhaseBus(643, false) };
        var branches = new[] { new PhaseBranch(1, 643, Z()) };
        var s = new[] { new Complex(0.1, 0.02), new Complex(0.1, 0.02), new Complex(0.1, 0.02) };

        // 634 is a typo for 643. Before R235 the sweep dropped it and reported Converged, under-loaded.
        var ex = Assert.Throws<ArgumentException>(() =>
            new MultiPhaseModel(buses, branches, new[] { new PhaseLoad(634, s) }));
        Assert.Contains("634", ex.Message);

        // The correct bus id still builds and solves.
        var ok = new MultiPhaseModel(buses, branches, new[] { new PhaseLoad(643, s) });
        Assert.Equal(MultiPhaseStatus.Converged, new ThreePhaseSweep().Solve(ok).Status);
    }

    // ---- R241 ---------------------------------------------------------------------------------------

    // V source -> switch 11 (initially closed) -> series R-L 12 -> C 13 to ground.
    private static EmtNetwork Net() => new(3, new List<EmtElement>
    {
        new VoltageSource(10, 0, 0, _ => 1.0),
        new SwitchElement(11, 0, 1, Ron: 1e-6, Roff: 1e9, InitiallyClosed: true),
        new SeriesBranch(12, 1, 2, 0.01, 1e-3),
        new ShuntElement(13, 2, C: 1e-3),
    });

    private static EmtResult Run(params EmtEvent[] events)
    {
        EmtNetwork net = Net();
        const double dt = 2e-6;
        var probe = new EmtProbe("v:2", EmtProbeKind.NodeVoltage, 2);
        return new EmtEngine(net, dt).Simulate(new EmtCase(net, dt, 2e-3, new[] { probe }, events));
    }

    [Fact]
    public void A_switch_event_on_a_non_switch_element_fails_loud()
    {
        // Element 12 is a series branch, not a switch. Before R241 this did nothing but still ran the CDA
        // burst and two re-factors, and the user believed the branch had opened.
        var ex = Assert.Throws<ArgumentException>(() => Run(new SwitchEvent(1e-3, 12, Close: false)));
        Assert.Contains("element 12", ex.Message);
        Assert.Contains("11", ex.Message);   // the valid switch ids are listed

        Assert.Throws<ArgumentException>(() => Run(new SwitchEvent(1e-3, 99, Close: false)));
    }

    [Fact]
    public void A_no_op_switch_event_leaves_the_run_bit_identical_to_no_event()
    {
        // Closing an already-closed switch changes nothing, so the trajectory must equal the event-free run
        // exactly, with no extra re-factorisations (before R241 the CDA half-steps perturbed it).
        EmtResult none = Run();
        EmtResult noop = Run(new SwitchEvent(1e-3, 11, Close: true));

        Assert.Equal(none.Stats.Refactorizations, noop.Stats.Refactorizations);
        Assert.Equal(none.Points.Count, noop.Points.Count);
        for (int k = 0; k < none.Points.Count; k++)
            Assert.Equal(none.Points[k].Probe[0], noop.Points[k].Probe[0]);
    }

    [Fact]
    public void A_real_switch_event_still_fires_the_cda_and_refactors()
    {
        EmtResult none = Run();
        EmtResult opened = Run(new SwitchEvent(1e-3, 11, Close: false));
        Assert.True(opened.Stats.Refactorizations > none.Stats.Refactorizations);
    }
}
