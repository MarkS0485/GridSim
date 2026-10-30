using System;
using System.Collections.Generic;
using GridSim.Core.Dynamics.Emt;
using Xunit;

namespace GridSim.Tests;

/// <summary>Switching transients: closing a switch onto an L-C branch produces the classic 1-cos transient
/// recovery voltage peaking near twice the source, and the Critical Damping Adjustment removes the spurious
/// trapezoidal Nyquist oscillation the discontinuity would otherwise excite.</summary>
public class EmtSwitchingTests
{
    // node0 = ideal V (Dirichlet); switch node0->node1; series R,L node1->node2; C node2->ground.
    private static (EmtNetwork net, EmtProbe vc) BuildSwitchedLc(double v, double r, double l, double c)
    {
        var elements = new List<EmtElement>
        {
            new VoltageSource(10, 0, 0, _ => v),
            new SwitchElement(11, 0, 1, Ron: 1e-6, Roff: 1e9, InitiallyClosed: false),
            new SeriesBranch(12, 1, 2, r, l),
            new ShuntElement(13, 2, C: c),
        };
        return (new EmtNetwork(3, elements), new EmtProbe("v:2", EmtProbeKind.NodeVoltage, 2));
    }

    [Fact]
    public void Closing_onto_an_lc_branch_gives_a_trv_peak_near_twice_the_source()
    {
        const double v = 1.0, r = 0.01, l = 1e-3, c = 1e-3, dt = 2e-6, t0 = 1e-3;
        (EmtNetwork net, EmtProbe vc) = BuildSwitchedLc(v, r, l, c);
        var events = new[] { new SwitchEvent(t0, 11, Close: true) };
        EmtResult res = new EmtEngine(net, dt).Simulate(new EmtCase(net, dt, 8e-3, new[] { vc }, events));

        double peak = 0.0;
        foreach (EmtPoint p in res.Points) peak = Math.Max(peak, p.Probe[0]);
        Assert.InRange(peak, 1.9, 2.02);          // 1-cos overshoot to ~2V, barely damped
        Assert.True(res.Stats.Refactorizations >= 3, $"expected initial + CDA refactors, got {res.Stats.Refactorizations}");
    }

    [Fact]
    public void Cda_suppresses_the_numerical_nyquist_oscillation()
    {
        // Switching a capacitor onto a near-ideal voltage step is the classic trapezoidal-chatter case: the
        // cap current i_C = (2C/dt)(v - v_prev) - i_prev alternates sign every step (2*dt oscillation) once v
        // has stepped. CDA's backward-Euler half-steps (no -i_prev term) kill it. Circuit: V -> switch (small
        // Ron) -> node1, C node1->ground. Roughness = peak |2nd difference| of the cap current after the event.
        const double v = 1.0, ron = 1e-4, c = 1e-3, dt = 2e-6, t0 = 1e-3;
        double rough(bool cda)
        {
            var elements = new List<EmtElement>
            {
                new VoltageSource(10, 0, 0, _ => v),
                new SwitchElement(11, 0, 1, Ron: ron, Roff: 1e9, InitiallyClosed: false),
                new ShuntElement(13, 1, C: c),
            };
            var net = new EmtNetwork(2, elements);
            var probe = new EmtProbe("iC", EmtProbeKind.BranchCurrent, 13);
            var events = new[] { new SwitchEvent(t0, 11, Close: true) };
            EmtResult res = new EmtEngine(net, dt, new EmtOptions { UseCda = cda }).Simulate(new EmtCase(net, dt, 3e-3, new[] { probe }, events));
            var pts = res.Points;
            int k0 = (int)Math.Round(t0 / dt) + 2; // just past the CDA burst
            double m = 0.0;
            for (int k = k0 + 1; k < pts.Count - 1; k++)
                m = Math.Max(m, Math.Abs(pts[k + 1].Probe[0] - 2 * pts[k].Probe[0] + pts[k - 1].Probe[0]));
            return m;
        }

        double withCda = rough(true), noCda = rough(false);
        Assert.True(withCda < noCda * 0.5, $"CDA roughness {withCda:E3} vs no-CDA {noCda:E3} (expected CDA much smoother)");
    }
}
