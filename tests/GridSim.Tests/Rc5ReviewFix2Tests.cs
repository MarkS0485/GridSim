using System.Numerics;
using GridSim.Core.Dynamics.Models;
using GridSim.Core.Model;
using GridSim.Core.PowerFlow;
using Xunit;

namespace GridSim.Tests;

/// <summary>
/// REVIEW-FIX-2 (2.1.085): a compensator or STATCOM on a bus that also has a generator. The power flow carries the device as
/// an extra PF-only generator, so the bus's reactive injection includes the device's Q; the RMS start must take that Q out
/// of the generator at the bus (as it already does for LCC and VSC converters), or the device's Q is counted twice and the
/// run does not start flat.
/// </summary>
public sealed class Rc5ReviewFix2Tests
{
    /// <summary>The power flow carries a regulating compensator or STATCOM as a PF-only generator and refuses to put one on a
    /// bus that also has a generator (HvdcCoupling.CheckFactsBuses), so the double count cannot arise through a solved case.
    /// The binding still reports what the device injects as drawn power, as the LCC and VSC converters do, so that a bus
    /// with a generator would never count the device's Q twice. Checked on the Sink case: the drawn power at each device bus
    /// is minus the injection the device starts on, and the refusal is pinned.</summary>
    [Fact]
    public void A_compensator_and_a_statcom_report_their_injection_as_drawn_power_and_a_generator_bus_is_refused()
    {
        GridModel m = Rc5Wp6FactsHvdcDynamicsTests.Sink();
        PowerFlowResult pf = new NewtonRaphsonPowerFlow().Solve(m, new PowerFlowOptions { Tolerance = 1e-12 });
        Assert.True(pf.Converged);
        var v = new Dictionary<int, Complex>();
        for (int i = 0; i < pf.BusIds.Count; i++) v[pf.BusIds[i]] = Complex.FromPolarCoordinates(pf.Vm[i], pf.VaDeg[i] * Math.PI / 180.0);
        var devices = new RmsNetworkDeviceData[]
        {
            new SvcDeviceData("svc1", new Csvgn1Data(25, 0, 0, 0, 0, 0.05, 0, 1, 0, 50, 100)),
            new StatcomDeviceData("st1", new Statcom1Data(100, 0.03, 0, 50, 0.01, 1.0, 1.0)),
        };
        (IReadOnlyList<RmsDeviceBinding.DeviceTerminal> terminals, Dictionary<int, (double P, double Q)> drawn) =
            RmsDeviceBinding.Bind(m, pf, devices, v, m.BaseMva);
        Assert.Equal(2, terminals.Count);
        foreach (RmsDeviceBinding.DeviceTerminal t in terminals)
        {
            // The terminal's start Q (system pu, exporting positive) is what the power flow's AC model carried; the drawn
            // power is its negative in Mvar. Exact: the same product, negated.
            (double P, double Q) d = drawn[t.Bus];
            Assert.Equal(0.0, d.P);
            Assert.Equal(-t.Q0 * m.BaseMva, d.Q);
            Assert.NotEqual(0.0, d.Q);
        }
        // The SVC is 20 Mvar of susceptance less its 10 Mvar BINIT, at V^2; the STATCOM's is its solved shunt Mvar.
        Assert.Equal(-pf.FactsDevices![0].ShuntMvar, drawn[7].Q, 12);
        // A compensator or STATCOM at a generator bus is refused by the power flow, before any RMS start.
        GridModel bad = new()
        {
            Name = "rc5-reviewfix2-bad", BaseMva = 100,
            Buses = new[] { new Bus(1, BusType.Slack, BaseKv: 345), new Bus(2, BusType.Pv, Qd: 20, BaseKv: 345) },
            Generators = new[] { new Generator(1, 0, Vg: 1.0, MBase: 1000, H: 5, Name: "A"), new Generator(2, 0, Vg: 1.0, Qmin: -500, Qmax: 500, MBase: 500, H: 5, Name: "B") },
            Branches = new[] { new Branch(1, 2, 0, 0.05) },
            StaticVarCompensators = new[] { new StaticVarCompensator(2, -100, 100, 10, 1.0, 1.0, "svc1") },
        };
        var ex = Assert.Throws<InvalidOperationException>(() => new NewtonRaphsonPowerFlow().Solve(bad, new PowerFlowOptions { Tolerance = 1e-12 }));
        Assert.Contains("also has generators", ex.Message, StringComparison.Ordinal);
    }

    private static LccLinkPlan LccPlan(GridModel m, PowerFlowResult pf, LccLinkResult r)
    {
        Complex V(int bus)
        {
            int i = pf.BusIds.ToList().IndexOf(bus);
            return Complex.FromPolarCoordinates(pf.Vm[i], pf.VaDeg[i] * Math.PI / 180.0);
        }
        LccDcLink l = m.LccLinks[0];
        return new LccLinkPlan(l, r, new Lcc1Data(5, 18, 0.01, 0.02, 0.5, 0.8, 0.1, 0, 0, 20, 100, 50, 300, 150, 450, 300), m.BaseMva,
            V(l.Rectifier.Bus), V(l.Inverter.Bus));
    }

    /// <summary>The LCC start residual is the power flow's AC/DC settling gap, at most 10 times 1e-9 (1 + |component|) per component. A
    /// start whose gap is larger is refused, so an equation or sign error cannot be absorbed into the correction.</summary>
    [Fact]
    public void The_lcc_start_residual_is_at_most_the_settling_tolerance_and_a_larger_gap_is_refused()
    {
        GridModel m = Rc5Wp6FactsHvdcDynamicsTests.Sink();
        PowerFlowResult pf = new NewtonRaphsonPowerFlow().Solve(m, new PowerFlowOptions { Tolerance = 1e-12 });
        Assert.True(pf.Converged);
        LccLinkResult r = pf.LccLinks![0];
        LccLinkPlan plan = LccPlan(m, pf, r);
        // Each component against its own scale, with the 10x margin the start allows over the settling tolerance.
        double tol = 10 * LccDcLinks.CouplingTolerance;
        double rp = Math.Abs(plan.ResidualR.Real) * m.BaseMva, rq = Math.Abs(plan.ResidualR.Imaginary) * m.BaseMva;
        double ip = Math.Abs(plan.ResidualI.Real) * m.BaseMva, iq = Math.Abs(plan.ResidualI.Imaginary) * m.BaseMva;
        Assert.True(rp <= tol * (1 + Math.Abs(r.RectifierMw)), $"rectifier P residual {rp:R} MW");
        Assert.True(rq <= tol * (1 + Math.Abs(r.RectifierMvar)), $"rectifier Q residual {rq:R} Mvar");
        Assert.True(ip <= tol * (1 + Math.Abs(r.InverterMw)), $"inverter P residual {ip:R} MW");
        Assert.True(iq <= tol * (1 + Math.Abs(r.InverterMvar)), $"inverter Q residual {iq:R} Mvar");
        // A power-flow power 1e-3 MW off the converter equations is far above the bound.
        var ex = Assert.Throws<ArgumentException>(() => LccPlan(m, pf, r with { RectifierMw = r.RectifierMw + 1e-3 }));
        Assert.Contains("differ", ex.Message, StringComparison.Ordinal);
        ex = Assert.Throws<ArgumentException>(() => LccPlan(m, pf, r with { InverterMvar = r.InverterMvar + 1e-3 }));
        Assert.Contains("differ", ex.Message, StringComparison.Ordinal);
    }
}
