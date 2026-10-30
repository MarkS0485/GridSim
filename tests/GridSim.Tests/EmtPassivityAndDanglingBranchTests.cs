using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using GridSim.Core.Dynamics.Emt;
using GridSim.Core.IO;
using GridSim.Core.Model;
using GridSim.Core.PowerFlow;
using Xunit;

namespace GridSim.Tests;

/// <summary>
/// N029 / N030 (October 2026 EMT follow-ups).
/// <para>N030: <see cref="EmtNetwork.FromGridModel"/> used to skip an in-service branch whose end bus is not in the
/// model with a bare <c>continue</c>, so a live branch vanished from the network. It is now refused by name.</para>
/// <para>N029: with loads on, a negative real demand (<c>Pd &lt; 0</c>) became a NEGATIVE conductance and negative
/// line charging became a NEGATIVE capacitance. Neither is passive, and the run grew without bound. Negative
/// charging is now a shunt reactor with exactly the branch's (negative) susceptance at the system frequency;
/// negative demand is a bounded current source by default, or refused by bus with
/// <see cref="EmtNegativeDemand.Refuse"/>. These tests pin every path against independent oracles (Ohm's law at
/// DC, the phasor solution of <see cref="AdmittanceStamp.Branch"/> at 50 Hz) and pin the refusals' messages.</para>
/// </summary>
public class EmtPassivityAndDanglingBranchTests
{
    private const double F = 50.0;
    private static readonly double W = 2.0 * Math.PI * F;

    /// <summary>Two buses: an ideal source at bus 1, a load at bus 2 (Pd, Qd in MW / MVAr on 100 MVA), one branch.</summary>
    private static GridModel TwoBus(double r, double x, double b, double tap, double pd, double qd) => new()
    {
        BaseMva = 100.0,
        Buses = new[] { new Bus(1, BusType.Slack), new Bus(2, BusType.Pq, Pd: pd, Qd: qd) },
        Generators = new[] { new Generator(1, 80.0) },
        Branches = new[] { new Branch(1, 2, r, x, b, tap) },
    };

    /// <summary>The fundamental phasor (cosine reference) of probe j over the last full 50 Hz cycle.</summary>
    private static Complex LastCycleFundamental(EmtResult res, int j, double omega, double dt)
    {
        int nPer = (int)Math.Round(2.0 * Math.PI / (omega * dt));
        Complex acc = Complex.Zero;
        for (int k = res.Points.Count - nPer; k < res.Points.Count; k++)
            acc += res.Points[k].Probe[j] * Complex.FromPolarCoordinates(1.0, -omega * res.Points[k].TSeconds);
        return acc * 2.0 / nPer;
    }

    // ---- N030: dangling in-service branch ---------------------------------------------------------------------

    [Fact]
    public void In_service_branch_to_a_missing_bus_is_refused_naming_the_branch()
    {
        var m = new GridModel
        {
            BaseMva = 100.0,
            Buses = new[] { new Bus(1, BusType.Slack), new Bus(2, BusType.Pq, Pd: 10.0) },
            Generators = new[] { new Generator(1, 10.0) },
            Branches = new[] { new Branch(1, 2, 0.01, 0.1), new Branch(2, 3, 0.01, 0.1, Name: "stray") },
        };
        var ex = Assert.Throws<ArgumentException>(() =>
            EmtNetwork.FromGridModel(m, new EmtBuildOptions(new[] { new EmtSourceSpec(1) })));
        Assert.Contains("branch #1 (2->3, 'stray')", ex.Message);
        Assert.Contains("To bus 3", ex.Message);
        Assert.Contains("is not in the model", ex.Message);
    }

    [Fact]
    public void In_service_branch_from_a_missing_bus_is_refused_naming_the_from_bus()
    {
        var m = new GridModel
        {
            BaseMva = 100.0,
            Buses = new[] { new Bus(1, BusType.Slack), new Bus(2, BusType.Pq, Pd: 10.0) },
            Generators = new[] { new Generator(1, 10.0) },
            Branches = new[] { new Branch(7, 2, 0.01, 0.1), new Branch(1, 2, 0.01, 0.1) },
        };
        var ex = Assert.Throws<ArgumentException>(() =>
            EmtNetwork.FromGridModel(m, new EmtBuildOptions(new[] { new EmtSourceSpec(1) })));
        Assert.Contains("branch #0 (7->2)", ex.Message);
        Assert.Contains("From bus 7", ex.Message);
    }

    [Fact]
    public void Out_of_service_branch_to_a_missing_bus_is_still_skipped()
    {
        // Out of service means not in the network, so a dangling end does not matter and the build succeeds with
        // exactly one series element (the live branch).
        var m = new GridModel
        {
            BaseMva = 100.0,
            Buses = new[] { new Bus(1, BusType.Slack), new Bus(2, BusType.Pq, Pd: 10.0) },
            Generators = new[] { new Generator(1, 10.0) },
            Branches = new[] { new Branch(1, 2, 0.01, 0.1), new Branch(2, 3, 0.01, 0.1, InService: false) },
        };
        EmtNetwork net = EmtNetwork.FromGridModel(m, new EmtBuildOptions(new[] { new EmtSourceSpec(1) }));
        Assert.Single(net.SeriesBranchIds);
    }

    // ---- N029: negative line charging becomes a shunt reactor -------------------------------------------------

    [Theory]
    [InlineData(1.00)]
    [InlineData(0.95)]
    public void Negative_line_charging_becomes_exact_shunt_reactors_never_a_negative_capacitor(double tap)
    {
        const double b = -0.1;
        GridModel m = TwoBus(0.02, 0.2, b, tap, 80.0, 30.0);
        EmtNetwork net = EmtNetwork.FromGridModel(m, new EmtBuildOptions(new[] { new EmtSourceSpec(1, FrequencyHz: F) }));

        // No element anywhere may carry a negative G, C or L (the passivity the fix restores).
        foreach (ShuntElement s in net.Elements.OfType<ShuntElement>())
            Assert.True(s.G >= 0 && s.C >= 0 && s.L >= 0, $"shunt {s.Id} is not passive: G={s.G:R} C={s.C:R} L={s.L:R}");

        // The two charging reactors, exactly as specified: L_end = 1/((|B|/2)*w0), From end scaled by a^2.
        double lEnd = 1.0 / ((-b / 2.0) * W);
        double lFrom = tap == 1.0 ? lEnd : lEnd * (tap * tap);
        ShuntElement[] reactors = net.Elements.OfType<ShuntElement>().Where(s => s.C == 0 && s.G == 0 && s.L > 0).ToArray();
        Assert.Equal(2, reactors.Length);
        Assert.Equal(net.BusToNode[1], reactors[0].Bus);
        Assert.Equal(lFrom, reactors[0].L);
        Assert.Equal(net.BusToNode[2], reactors[1].Bus);
        Assert.Equal(lEnd, reactors[1].L);
    }

    [Theory]
    [InlineData(0.02, 0.20, -0.10, 1.00)]
    [InlineData(0.02, 0.20, -0.10, 0.95)]
    [InlineData(0.01, 0.15, -0.04, 1.05)]
    public void Negative_charging_steady_state_matches_the_admittance_stamp_phasor(double r, double x, double b, double tap)
    {
        // The power flow's own branch stamp with a negative b is the oracle; the EMT must settle on it.
        GridModel m = TwoBus(r, x, b, tap, 80.0, 30.0);
        EmtNetwork net = EmtNetwork.FromGridModel(m,
            new EmtBuildOptions(new[] { new EmtSourceSpec(1, Rs: 0, AmplitudePu: 1.0, FrequencyHz: F) }, IncludeLoads: true));
        const double dt = 1e-5;
        var probes = new[] { new EmtProbe("v2", EmtProbeKind.NodeVoltage, net.BusToNode[2]) };
        EmtResult res = new EmtEngine(net, dt).Simulate(new EmtCase(net, dt, 1.0, probes));

        BranchStamp st = AdmittanceStamp.Branch(r, x, b, tap, 0.0);
        Complex yLoad = new Complex(80.0, -30.0) / 100.0;
        Complex v2Phasor = -st.Ytf * Complex.One / (st.Ytt + yLoad);
        Complex v2Emt = LastCycleFundamental(res, 0, W, dt);
        double err = (v2Emt - v2Phasor).Magnitude;
        Assert.True(err < 2e-6, $"|V2_emt - V2_phasor| = {err:R} (EMT {v2Emt}, phasor {v2Phasor})");
    }

    // ---- N029: negative demand ---------------------------------------------------------------------------------

    [Fact]
    public void Negative_demand_never_builds_a_negative_conductance()
    {
        GridModel m = TwoBus(0.1, 0.2, 0.0, 1.0, -50.0, 20.0);
        EmtNetwork net = EmtNetwork.FromGridModel(m, new EmtBuildOptions(new[] { new EmtSourceSpec(1) }));
        foreach (ShuntElement s in net.Elements.OfType<ShuntElement>())
            Assert.True(s.G >= 0 && s.C >= 0 && s.L >= 0, $"shunt {s.Id} is not passive: G={s.G:R} C={s.C:R} L={s.L:R}");
        // The real part is one current source at bus 2; the reactive part (Qd = 20 MVAr, inductive) stays a
        // constant-impedance inductor L = 1/((Qd/Base)*w0).
        CurrentSource cs = Assert.Single(net.Elements.OfType<CurrentSource>());
        Assert.Equal(net.BusToNode[2], cs.Bus);
        Assert.Equal(0.5, cs.Inj(0.0));
        Assert.Equal(0.5, cs.Inj(0.123));                                  // DC reference: constant
        ShuntElement load = Assert.Single(net.Elements.OfType<ShuntElement>());
        Assert.Equal(0.0, load.G);
        Assert.Equal(1.0 / ((20.0 / 100.0) * W), load.L);
    }

    [Fact]
    public void Negative_demand_as_a_current_source_gives_the_dc_ohms_law_voltage()
    {
        // DC study (the CLI default): ideal 1 pu source at bus 1, a purely resistive arm R = 0.1 pu (X = 0, so no
        // inductor: the rest initial condition is already the exact solution), generation |Pd| = 50 MW on 100 MVA
        // injected at bus 2 as 0.5 pu. Ohm's law: V2 = 1 + 0.5 * 0.1 = 1.05 pu at every instant. The old negative
        // conductance (G = -0.5) would instead have given V2 = 1/(1 - 0.05).
        GridModel m = TwoBus(0.1, 0.0, 0.0, 1.0, -50.0, 0.0);
        EmtNetwork net = EmtNetwork.FromGridModel(m, new EmtBuildOptions(new[] { new EmtSourceSpec(1) }));
        const double dt = 1e-5;
        var probes = new[] { new EmtProbe("v2", EmtProbeKind.NodeVoltage, net.BusToNode[2]) };
        EmtResult res = new EmtEngine(net, dt).Simulate(new EmtCase(net, dt, 0.01, probes));
        // Every STEPPED sample (t > 0). The t = 0 sample is the rest initial-condition solve, which does not include
        // current-source injections (follow-up N032), so it reads 0 here; first written in 1.95.066, scoped to the
        // stepped solution at the combined run before it had ever passed.
        foreach (EmtPoint p in res.Points.Where(p => p.TSeconds > 0))
            Assert.True(Math.Abs(p.Probe[0] - 1.05) < 1e-12, $"V2 = {p.Probe[0]:R} at t = {p.TSeconds:R}, expected 1.05");
    }

    [Fact]
    public void Negative_demand_as_a_current_source_matches_the_ac_phasor()
    {
        // 50 Hz study: the injection is I = 0.5 pu in phase with the source EMF (cos reference, phase 0). Bus 2
        // also carries a bus shunt Gs = 40 MW (G = 0.4 pu) so the series inductor is not driven by the current
        // source alone (a current-forced inductor rings undamped under the trapezoidal rule). Nodal phasor
        // oracle at bus 2: (V2 - V1)/z + G*V2 = I, so V2 = (I + V1/z) / (1/z + G), with V1 = 1, z = r + jx.
        const double r = 0.1, x = 0.2, g = 0.4;
        var m = new GridModel
        {
            BaseMva = 100.0,
            Buses = new[] { new Bus(1, BusType.Slack), new Bus(2, BusType.Pq, Pd: -50.0, Gs: 40.0) },
            Generators = new[] { new Generator(1, 80.0) },
            Branches = new[] { new Branch(1, 2, r, x) },
        };
        EmtNetwork net = EmtNetwork.FromGridModel(m, new EmtBuildOptions(new[] { new EmtSourceSpec(1, FrequencyHz: F) }));
        const double dt = 1e-5;
        var probes = new[] { new EmtProbe("v2", EmtProbeKind.NodeVoltage, net.BusToNode[2]) };
        EmtResult res = new EmtEngine(net, dt).Simulate(new EmtCase(net, dt, 0.5, probes));
        Complex z = new Complex(r, x);
        Complex v2Phasor = (0.5 + Complex.One / z) / (Complex.One / z + g);
        Complex v2Emt = LastCycleFundamental(res, 0, W, dt);
        double err = (v2Emt - v2Phasor).Magnitude;
        Assert.True(err < 2e-6, $"|V2_emt - V2_phasor| = {err:R} (EMT {v2Emt}, phasor {v2Phasor})");
    }

    [Fact]
    public void Refuse_mode_lists_every_negative_demand_bus_and_says_why()
    {
        var m = new GridModel
        {
            BaseMva = 100.0,
            Buses = new[]
            {
                new Bus(1, BusType.Slack), new Bus(2, BusType.Pq, Pd: -50.0), new Bus(3, BusType.Pq, Pd: 30.0),
                new Bus(4, BusType.Pq, Pd: -12.5, Qd: 4.0),
            },
            Generators = new[] { new Generator(1, 80.0) },
            Branches = new[] { new Branch(1, 2, 0.01, 0.1), new Branch(2, 3, 0.01, 0.1), new Branch(3, 4, 0.01, 0.1) },
        };
        var ex = Assert.Throws<ArgumentException>(() => EmtNetwork.FromGridModel(m,
            new EmtBuildOptions(new[] { new EmtSourceSpec(1) }, NegativeDemand: EmtNegativeDemand.Refuse)));
        Assert.Contains("2 bus(es)", ex.Message);
        Assert.Contains("bus 2 (Pd = -50 MW)", ex.Message);
        Assert.Contains("bus 4 (Pd = -12.5 MW)", ex.Message);
        Assert.DoesNotContain("bus 3", ex.Message);
        Assert.Contains("not passive", ex.Message);

        // Loads off: negative demand is not modelled at all, so Refuse has nothing to refuse.
        EmtNetwork net = EmtNetwork.FromGridModel(m,
            new EmtBuildOptions(new[] { new EmtSourceSpec(1) }, IncludeLoads: false, NegativeDemand: EmtNegativeDemand.Refuse));
        Assert.Empty(net.Elements.OfType<CurrentSource>());
    }

    [Fact]
    public void Bundled_ieee300_builds_a_passive_network_by_default()
    {
        // IEEE 300 carries 8 negative-Pd buses and 6 negative-charging branches (counted from the case file). By
        // default none of them may become a negative G, C or L: 8 current sources, every shunt passive.
        GridModel m = IeeeCases.Case300();
        Assert.Equal(8, m.Buses.Count(bb => bb.Pd < 0));
        Assert.Equal(6, m.Branches.Count(br => br.InService && br.B < 0));
        EmtNetwork net = EmtNetwork.FromGridModel(m, new EmtBuildOptions(new[] { new EmtSourceSpec(m.Generators[0].Bus, FrequencyHz: F) }));
        Assert.Equal(8, net.Elements.OfType<CurrentSource>().Count());
        // (IEEE 300 has no bus shunt with Gs < 0, so G >= 0 holds for every shunt, loads and bus shunts alike.)
        foreach (ShuntElement s in net.Elements.OfType<ShuntElement>())
            Assert.True(s.G >= 0 && s.C >= 0 && s.L >= 0, $"shunt {s.Id} is not passive: G={s.G:R} C={s.C:R} L={s.L:R}");
        // The engine accepts the whole build (no refused or non-finite element).
        _ = new EmtEngine(net, 20e-6);
    }
}
