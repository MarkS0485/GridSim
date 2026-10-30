using System;
using System.Collections.Generic;
using GridSim.Core.Dynamics.Emt;
using Xunit;

namespace GridSim.Tests;

/// <summary>The EMT acceptance gate: the Dommel companion-model engine must reproduce the closed-form
/// series-RLC step response - over-, critically- and under-damped - to a tight tolerance. The analytic
/// current here is an INDEPENDENT oracle (textbook second-order response), not the production code path, so
/// agreement is real validation that the engine solves the electromagnetic equations correctly.</summary>
public class EmtRlcAnalyticTests
{
    // Series RLC: ideal V-step source (node 0) -> R,L branch -> node 1 -> C to ground.
    // The branch current i(t) is the series loop current; v_C(0)=0, i(0)=0.
    private static EmtEngine BuildSeriesRlc(double r, double l, double c, double v, double dt)
    {
        var elements = new List<EmtElement>
        {
            new VoltageSource(3, Bus: 0, Rs: 0, Emf: _ => v),  // ideal step -> node 0 Dirichlet
            new SeriesBranch(1, From: 0, To: 1, R: r, L: l),
            new ShuntElement(2, Bus: 1, C: c),
        };
        return new EmtEngine(new EmtNetwork(nodeCount: 2, elements), dt);
    }

    /// <summary>Closed-form series-RLC step-response current, i(0)=0, v_C(0)=0.</summary>
    private static double AnalyticCurrent(double r, double l, double c, double v, double t)
    {
        double w0 = 1.0 / Math.Sqrt(l * c);
        double zeta = (r / 2.0) * Math.Sqrt(c / l);
        if (Math.Abs(zeta - 1.0) < 1e-12)                     // critically damped
            return (v / l) * t * Math.Exp(-w0 * t);
        if (zeta < 1.0)                                       // underdamped
        {
            double wd = w0 * Math.Sqrt(1.0 - zeta * zeta);
            return (v / (wd * l)) * Math.Exp(-zeta * w0 * t) * Math.Sin(wd * t);
        }
        double disc = w0 * Math.Sqrt(zeta * zeta - 1.0);      // overdamped
        double s1 = -zeta * w0 + disc, s2 = -zeta * w0 - disc;
        return (v / (l * (s1 - s2))) * (Math.Exp(s1 * t) - Math.Exp(s2 * t));
    }

    private static double MaxError(double r, double l, double c, double v, double dt, double duration)
    {
        EmtEngine eng = BuildSeriesRlc(r, l, c, v, dt);
        var probes = new[] { new EmtProbe("i", EmtProbeKind.BranchCurrent, Target: 1) };
        EmtResult res = eng.Simulate(new EmtCase(BuildNet(r, l, c, v), dt, duration, probes));
        double maxErr = 0.0;
        foreach (EmtPoint p in res.Points)
            maxErr = Math.Max(maxErr, Math.Abs(p.Probe[0] - AnalyticCurrent(r, l, c, v, p.TSeconds)));
        return maxErr;
    }

    private static EmtNetwork BuildNet(double r, double l, double c, double v) => new(2, new List<EmtElement>
    {
        new VoltageSource(3, 0, 0, _ => v),
        new SeriesBranch(1, 0, 1, r, l),
        new ShuntElement(2, 1, C: c),
    });

    [Theory]
    [InlineData(0.4, 1e-3, 1e-3)]   // underdamped  (zeta = 0.2)
    [InlineData(2.0, 1e-3, 1e-3)]   // critically damped (zeta = 1)
    [InlineData(4.0, 1e-3, 1e-3)]   // overdamped   (zeta = 2)
    public void Series_rlc_step_response_matches_closed_form(double r, double l, double c)
    {
        double dt = 1e-6, duration = 3e-3, v = 1.0;
        double maxErr = MaxError(r, l, c, v, dt, duration);
        Assert.True(maxErr < 1e-5, $"max |i_emt - i_analytic| = {maxErr:E3} exceeded tolerance");
    }

    [Fact]
    public void Trapezoidal_error_is_second_order_in_dt()
    {
        // Halving dt should cut the peak error ~4x (O(dt^2)).
        double r = 0.4, l = 1e-3, c = 1e-3, v = 1.0, duration = 2e-3;
        double eCoarse = MaxError(r, l, c, v, 2e-6, duration);
        double eFine = MaxError(r, l, c, v, 1e-6, duration);
        double ratio = eCoarse / eFine;
        Assert.True(ratio > 3.0, $"error ratio {ratio:F2} (expected ~4 for 2nd-order)");
    }

    [Fact]
    public void Run_is_deterministic()
    {
        double dt = 1e-6, duration = 1e-3, v = 1.0;
        var probes = new[] { new EmtProbe("i", EmtProbeKind.BranchCurrent, 1) };
        EmtResult a = new EmtEngine(BuildNet(0.4, 1e-3, 1e-3, v), dt).Simulate(new EmtCase(BuildNet(0.4, 1e-3, 1e-3, v), dt, duration, probes));
        EmtResult b = new EmtEngine(BuildNet(0.4, 1e-3, 1e-3, v), dt).Simulate(new EmtCase(BuildNet(0.4, 1e-3, 1e-3, v), dt, duration, probes));
        Assert.Equal(a.Points.Count, b.Points.Count);
        for (int i = 0; i < a.Points.Count; i++)
            Assert.Equal(a.Points[i].Probe[0], b.Points[i].Probe[0], 12);
    }
}
