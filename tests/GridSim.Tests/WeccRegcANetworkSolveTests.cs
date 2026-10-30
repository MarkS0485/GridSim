using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using GridSim.Core.Dynamics.Models;
using GridSim.Core.Dynamics.Models.Wecc;
using GridSim.Core.IO;
using GridSim.Core.Model;
using GridSim.Core.Numerics;
using GridSim.Core.PowerFlow;
using Xunit;

namespace GridSim.Tests;

/// <summary>
/// REGC_A's exact network-interface solution (DECISIONS D38 of the WECC expansion): <see cref="Regca.SolveWithNetwork"/>
/// against a Thevenin node, and the kernel's rescue of a step whose plain substitution hits the iteration cap.
/// </summary>
public class WeccRegcANetworkSolveTests
{
    private static Regca Unit(double p0 = 0.8, double q0 = 0.2) =>
        new(new RegcaParameters { Lvplsw = 0, Accel = 0.0, Khv = 0.7, Volim = 1.2, Iolim = -1.3, Lvpnt0 = 0.4, Lvpnt1 = 0.8 }, 1.0, p0, q0);

    // Thevenin node: vg behind zg (REGC_A has no Norton admittance): V = a + zg I.
    private static void AssertConsistent(Regca rb, CurrentSourceNetworkSolver.Solution s, Complex a, Complex c2, double tol)
    {
        Assert.True((s.Voltage - a - c2 * s.Current).Magnitude <= tol, $"network residual {(s.Voltage - a - c2 * s.Current).Magnitude}");
        if (!s.FrameHeld) Assert.True(Math.Abs(Regcb.Wrap(DetMath.Phase(s.Voltage) - s.FrameAngle)) <= tol, "frame is not the terminal angle");
        (double ip, double iq) = rb.Output(s.Voltage.Magnitude);
        Complex model = new Complex(ip, -iq) * DetMath.FromPolar(1.0, s.FrameAngle);
        Assert.True((model - s.Current).Magnitude <= tol, $"model residual {(model - s.Current).Magnitude}");
    }

    [Theory]
    [InlineData(1.0, 0.3)]     // normal: LVACM 1, HVRCM off
    [InlineData(0.55, 0.3)]    // a sag into the LVACM ramp
    [InlineData(1.3, 0.3)]     // a swell into HVRCM
    [InlineData(0.3, 0.05)]    // a deep sag behind a stiff connection
    public void The_solution_satisfies_the_network_and_the_model_in_the_terminal_frame(double vg, double xg)
    {
        Regca rb = Unit();
        Complex zg = new(0.01, xg), a = vg * DetMath.FromPolar(1.0, 0.2);
        CurrentSourceNetworkSolver.Solution s = rb.SolveWithNetwork(a, Complex.One, zg, 0.2, 1.0);
        AssertConsistent(rb, s, a, zg, 1e-12);
    }

    [Fact]
    public void Active_current_into_a_terminal_short_has_no_consistent_frame_so_the_frame_is_held()
    {
        // No grid voltage behind a pure reactance: x = |j X g(x)| needs the active current to vanish, which LVACM only gives
        // below Lvpnt0; above it no terminal angle is consistent and the frame is held.
        Regca rb = Unit(p0: 1.0, q0: 0.0);
        Complex zg = new(0.0, 0.6);
        CurrentSourceNetworkSolver.Solution s = rb.SolveWithNetwork(Complex.Zero, Complex.One, zg, 0.7, 1.0);
        Assert.Equal(0.7, s.FrameAngle);
        AssertConsistent(rb, s, Complex.Zero, zg, 1e-12);
    }

    [Fact]
    public void An_operating_point_is_reproduced_exactly()
    {
        Regca rb = Unit();
        Complex v0 = DetMath.FromPolar(1.0, -0.4), i0 = new Complex(0.8, -0.2) * DetMath.FromPolar(1.0, -0.4), zg = new(0.02, 0.5);
        CurrentSourceNetworkSolver.Solution s = rb.SolveWithNetwork(v0 - zg * i0, Complex.One, zg, -0.35, 1.0);
        Assert.False(s.FrameHeld);
        Assert.True((s.Voltage - v0).Magnitude <= 1e-14 && (s.Current - i0).Magnitude <= 1e-14, $"{s.Voltage} {s.Current}");
    }

    [Fact]
    public void Regc_a_steps_that_substitution_cannot_settle_are_rescued_and_the_run_converges()
    {
        // IEEE 118 with its four largest non-slack units as REGC_A + REEC_A and a bolted fault at bus 30: plain substitution hits
        // the 50-pass cap on 95 steps; the exact rescue solves them all (62 steps rescued: the trajectory after a rescued step
        // differs from the failed one's).
        GridModel m0 = IeeeCases.Case118();
        int slackBus = m0.Buses.Single(b => b.Type == BusType.Slack).Id;
        var conv = new HashSet<int>(Enumerable.Range(0, m0.Generators.Count).Where(k => m0.Generators[k].Bus != slackBus && m0.Generators[k].Pg > 1.0)
            .OrderByDescending(k => m0.Generators[k].Pg).ThenBy(k => k).Take(4));
        GridModel m = m0.CopyWith(generators: m0.Generators.Select((g, k) => conv.Contains(k) ? g with { MBase = 2.0 * Math.Max(Math.Max(g.Pg, Math.Abs(g.Qg)), 10.0) } : g).ToList());
        PowerFlowResult pf = new NewtonRaphsonPowerFlow().Solve(m);
        var data = m.Generators.Select((g, k) =>
        {
            var d = new SubtransientInit.MachineData(Math.Max(g.H, 3.0), 0.0, 1.6, 0.25, 0.18, 1.55, 0.4, 0.18, 7.0, 0.03, 0.5, 0.05);
            return conv.Contains(k) ? d with { Renewable = new WeccPlant(new RegcaParameters { Lvplsw = 0, Accel = 0.0 },
                new ReecParameters { VFlag = 0, QFlag = 0, Vdip = 0.8, Vup = 1.2, Qmax = 1.0, Qmin = -1.0, Imax = 1.3 }) } : d;
        }).ToList();
        MultiMachineSubtransient.Result r = RmsTransientStability.Study(m, pf, data, 30, 0.1, 0.1, stepSeconds: 0.005, durationSeconds: 2.0);
        Assert.Equal(0, r.ConverterIterationFailures);
        Assert.Equal(62, r.ConverterRescuedSteps);
        Assert.Equal(WeccRenewableDigestTests.Digest(r), WeccRenewableDigestTests.Digest(RmsTransientStability.Study(m, pf, data, 30, 0.1, 0.1, stepSeconds: 0.005, durationSeconds: 2.0)));
        // At rest no step needs the rescue, so the historical substitution numbers stand.
        MultiMachineSubtransient.Result rest = RmsTransientStability.Study(m, pf, data, 30, 50.0, 0.1, stepSeconds: 0.005, durationSeconds: 1.0);
        Assert.Equal(0, rest.ConverterRescuedSteps);
        Assert.Equal(0, rest.ConverterIterationFailures);
    }
}
