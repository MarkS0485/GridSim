using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using GridSim.Core;
using GridSim.Core.Dynamics.Models;
using GridSim.Core.Dynamics.Models.Wecc;
using GridSim.Core.IO;
using GridSim.Core.Model;
using GridSim.Core.Numerics;
using GridSim.Core.PowerFlow;
using Xunit;

namespace GridSim.Tests;

/// <summary>
/// REGC_B's exact network-interface solution (DECISIONS D36 of the WECC expansion): <see cref="Regcb.SolveWithNetwork"/>
/// against a Thevenin node, the terminal-voltage frame (no error on a steadily turning terminal, which the start-of-step hold
/// of D32 had), the held frame where no consistent frame exists, blocking, and the kernel path with two units.
/// </summary>
public class WeccRegcBNetworkSolveTests
{
    private const double Dt = 0.005;
    private static readonly Complex Ze = new(0.0025, 0.2);

    private static RegcbParameters P(int pq) => new() { Tg = 0.02, Rrpwr = 0.5, Imax = 1.2, Tfltr = 0.02, Iqrmax = 999, Iqrmin = -999, Te = 0.02, Re = 0.0025, Xe = 0.2, PqFlag = pq };

    // A Thevenin node: source vg behind zg, with the unit's Norton admittance y = 1 / Ze (rating 1) stamped. Returns a, c1, c2.
    private static (Complex A, Complex C1, Complex C2, Complex Zdd) Node(Complex vg, Complex zg)
    {
        Complex y = 1.0 / Ze;
        Complex zdd = 1.0 / (1.0 / zg + y);
        return (zdd * vg / zg, 1.0 - zdd * y, zdd, zdd);
    }

    // Every solution must satisfy the network equation V c1 = a + c2 I, and its own model: I = L((E - V e^{-j phi}) / Z) e^{j phi}
    // with phi = arg V unless the frame is held.
    private static void AssertConsistent(Regcb rb, RegcbNetworkSolution s, Complex a, Complex c1, Complex c2, double tol)
    {
        Assert.True((s.Voltage * c1 - a - c2 * s.Current).Magnitude <= tol, $"network residual {(s.Voltage * c1 - a - c2 * s.Current).Magnitude}");
        if (!s.FrameHeld) Assert.True(Math.Abs(Regcb.Wrap(DetMath.Phase(s.Voltage) - s.FrameAngle)) <= tol, "frame is not the terminal angle");
        Complex axis = DetMath.FromPolar(1.0, s.FrameAngle);
        Complex model = rb.Limit((rb.Source - s.Voltage * Complex.Conjugate(axis)) / rb.Impedance, out _) * axis;
        Assert.True((model - s.Current).Magnitude <= tol, $"model residual {(model - s.Current).Magnitude}");
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    public void An_unlimited_solution_reproduces_the_operating_point_exactly(int pq)
    {
        var rb = new Regcb(P(pq), Complex.One, 0.6, 0.1);
        Complex v0 = DetMath.FromPolar(1.0, 0.3), i0 = new Complex(0.6, -0.1) * DetMath.FromPolar(1.0, 0.3);
        Complex zg = new(0.02, 0.5);   // weaker than Xe
        (Complex a, Complex c1, Complex c2, _) = Node(v0 - zg * i0, zg);
        RegcbNetworkSolution s = rb.SolveWithNetwork(a, c1, c2, 0.25, false, Complex.Zero);
        Assert.False(s.FrameHeld);
        Assert.True((s.Voltage - v0).Magnitude <= 1e-14, $"{s.Voltage}");
        Assert.True((s.Current - i0).Magnitude <= 1e-14, $"{s.Current}");
        Assert.Equal(0.3, s.FrameAngle, 14);
        AssertConsistent(rb, s, a, c1, c2, 1e-14);
    }

    [Theory]
    [InlineData(0, 0.3)]
    [InlineData(1, 0.3)]
    [InlineData(0, 0.6)]
    [InlineData(1, 0.6)]
    public void A_deep_sag_behind_a_weak_grid_is_solved_on_the_current_limit(int pq, double vg)
    {
        var rb = new Regcb(P(pq), Complex.One, 0.9, 0.0);
        (Complex a, Complex c1, Complex c2, _) = Node(new Complex(vg, 0.0), new Complex(0.0, 0.1));
        RegcbNetworkSolution s = rb.SolveWithNetwork(a, c1, c2, 0.0, false, Complex.Zero);
        Assert.Equal(1.2, s.Current.Magnitude, 12);
        AssertConsistent(rb, s, a, c1, c2, 1e-12);
    }

    [Theory]
    [InlineData(0, 10.0)]
    [InlineData(1, 1.2)]
    [InlineData(1, 10.0)]
    public void With_no_consistent_terminal_frame_the_frame_is_held_and_the_held_solution_is_exact(int pq, double imax)
    {
        // A bolted fault at the terminal side (no grid voltage behind a small impedance) while the source still has its
        // quadrature part Xe Ip: no terminal angle makes the source consistent. (With Q priority and a saturating limit the
        // current is purely reactive and a consistent frame exists, so that case uses an Imax the fault does not reach.)
        var rb = new Regcb(P(pq) with { Imax = imax }, Complex.One, 0.9, 0.0);
        (Complex a, Complex c1, Complex c2, _) = Node(Complex.Zero, new Complex(0.0, 0.05));
        RegcbNetworkSolution s = rb.SolveWithNetwork(a, c1, c2, 0.4, false, Complex.Zero);
        Assert.True(s.FrameHeld);
        Assert.Equal(0.4, s.FrameAngle);
        AssertConsistent(rb, s, a, c1, c2, 1e-12);
    }

    [Fact]
    public void A_blocked_unit_passes_no_current_and_the_node_sees_only_the_norton_admittance()
    {
        var rb = new Regcb(P(0), Complex.One, 0.6, 0.1);
        (Complex a, Complex c1, Complex c2, _) = Node(new Complex(0.95, 0.1), new Complex(0.01, 0.4));
        RegcbNetworkSolution s = rb.SolveWithNetwork(a, c1, c2, 0.0, true, Complex.Zero);
        Assert.Equal(Complex.Zero, s.Current);
        Assert.Equal(a / c1, s.Voltage);
        // a / c1 is the grid's open-circuit voltage at the node: vg with no current through zg.
        Assert.True((s.Voltage - new Complex(0.95, 0.1)).Magnitude <= 1e-14);
    }

    [Fact]
    public void An_isolated_node_is_refused()
    {
        var rb = new Regcb(P(0), Complex.One, 0.6, 0.1);
        var ex = Assert.Throws<InvalidOperationException>(() => rb.SolveWithNetwork(Complex.One, Complex.Zero, Ze, 0.0, false, Complex.Zero));
        Assert.Contains("no network connection besides the unit itself", ex.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(0.01)]
    [InlineData(-0.02)]
    public void A_terminal_turning_off_nominal_leaves_no_power_error(double df)
    {
        // The grid behind a weak impedance turns at (1 + df) pu frequency. The source is in the terminal frame of each solution,
        // so the unit holds P0 and Q0 to rounding. Under the start-of-step hold (D32) the error was |E| |V| (2 pi f0 df dt) / |Z|:
        // here 1 x 1 x (2 pi 60 x 0.01 x 0.005) / 0.2 = 0.094 pu for df = 0.01.
        var unit = new RmsConverterUnit(1, "b", RatingPu: 1.0, P0: 0.6, Q0: 0.1, V0: Complex.One, new RmsConverterParameters()) { Wecc = new WeccPlant(null) { RegcB = P(0) } };
        var s = new WeccConverterState(unit);
        Complex zg = new(0.01, 0.4), i0 = new(0.6, -0.1);
        Complex vg0 = Complex.One - zg * i0;
        double dTheta = 2.0 * Math.PI * 60.0 * df * Dt;
        double worstP = 0.0, worstQ = 0.0;
        for (int k = 0; k < 2000; k++)
        {
            (Complex a, _, _, Complex zdd) = Node(vg0 * DetMath.FromPolar(1.0, dTheta * k), zg);
            Complex inj = s.NetworkSolvedCurrent(a, zdd);
            Complex v = a + zdd * inj;
            Complex ic = s.Commit(v);
            Complex sc = v * Complex.Conjugate(s.TerminalCurrent(ic, v));
            worstP = Math.Max(worstP, Math.Abs(sc.Real - 0.6));
            worstQ = Math.Max(worstQ, Math.Abs(sc.Imaginary - 0.1));
            s.Step(k * Dt, v, new[] { v }, Dt);
        }
        Assert.True(worstP <= 1e-12 && worstQ <= 1e-12, $"P error {worstP}, Q error {worstQ}");
    }

    [Fact]
    public void Two_regc_b_units_in_wscc9_start_at_rest_and_converge_through_a_fault()
    {
        GridModel m = IeeeCases.Case9();
        PowerFlowResult pf = new NewtonRaphsonPowerFlow().Solve(m);
        var xdp = new Dictionary<int, double> { [1] = 0.0608, [2] = 0.1198, [3] = 0.1813 };
        List<SubtransientInit.MachineData> data = m.Generators.Select(g =>
        {
            double x = xdp[g.Bus];
            var d = new SubtransientInit.MachineData(Math.Max(g.H, 1.0), 0.0, x, x, x, x, x, x, 8.0, 0.03, 0.4, 0.05);
            return g.Bus != 1 ? d with { Renewable = WeccRegcBReecDTests.BPlant() } : d;
        }).ToList();
        MultiMachineSubtransient.Result rest = RmsTransientStability.Study(m, pf, data, 4, 50.0, 0.1, stepSeconds: Dt, durationSeconds: 2.0);
        Assert.Equal(0, rest.ConverterIterationFailures);
        foreach (RmsConverterTrace c in rest.Converters!)
            Assert.True(c.ActivePowerPu.Max(x => Math.Abs(x - c.ActivePowerPu[0])) <= 1e-9, $"{c.Name} not at rest");
        MultiMachineSubtransient.Result r = RmsTransientStability.Study(m, pf, data, 9, 0.1, 0.1, stepSeconds: Dt, durationSeconds: 10.0);
        Assert.Equal(0, r.ConverterIterationFailures);
        Assert.True(r.StayedInSynchronism);
        Assert.Equal(WeccRenewableDigestTests.Digest(r), WeccRenewableDigestTests.Digest(RmsTransientStability.Study(m, pf, data, 9, 0.1, 0.1, stepSeconds: Dt, durationSeconds: 10.0)));
        // Only the bus-1 machine is left (no governor, D = 0), so the units' power swings slowly with it after the fault
        // (bus 2: 1.63 -> 1.5859 at 3 s, 1.6346 at 6 s, 1.6324 at 10 s); the bound pins recovery to within that swing.
        foreach (RmsConverterTrace c in r.Converters!)
            Assert.True(Math.Abs(c.ActivePowerPu[^1] - c.ActivePowerPu[0]) <= 0.005, $"{c.Name}: P {c.ActivePowerPu[0]} -> {c.ActivePowerPu[^1]} at 3 s {c.ActivePowerPu[600]} at 6 s {c.ActivePowerPu[1200]} V {c.TerminalVoltagePu[^1]}");
    }
}
