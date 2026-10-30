using GridSim.Core.Dynamics.Emt;
using GridSim.Core.IO.Matpower;
using GridSim.Core.Model;
using Xunit;

namespace GridSim.Tests;

/// <summary>
/// N031 and N032: the EMT rest initial-condition solve.
/// <para>N031: an island tied to ground only by an OPEN switch (a fault switch before it closes, 1e-9 S) used to be
/// treated as anchored, so it entered the rest matrix held by 1e-9 S beside its own conductances. On pglib case240
/// with a fault switch on its last bus (8034, joined to the network only through an inductive arm) the matrix also
/// carries series capacitors with R = 1e-5 (1e5 S): the ratio is past the sparse LU's pivot floor, which is relative
/// to the largest entry, and the whole run failed ("singular over 8 node(s)"). An open switch now
/// neither joins nor anchors, so such an island is pinned at 0 V like any other floating part.</para>
/// <para>N032: a node held at 0 V by a capacitor dropped the current injected into it at t = 0+ (a current source,
/// or a resistive branch from a node at a non-zero rest voltage): its capacitor's trapezoidal history was seeded
/// with zero current, so the trapezoidal rule integrated half a step of charge too little and the node carried a
/// lasting offset of -I*dt/(2C). The capacitor is now seeded with the node's inflow.</para>
/// </summary>
public class EmtRestStateN031N032Tests
{
    private static string DataDir => Path.Combine(AppContext.BaseDirectory, "data");

    // ---------------------------------------------------------------- N032

    [Fact]
    public void A_constant_current_into_a_capacitor_charges_it_as_a_ramp_from_the_first_step()
    {
        // One node, a 1 A current source into a 1 mF capacitor to ground. Exact answer v(t) = I*t/C, a ramp, which
        // the trapezoidal rule integrates exactly. Before the fix v(n) = I*(n - 1/2)*dt/C: the half-step offset
        // -I*dt/(2C) = -5e-4 V at every sample.
        const double i0 = 1.0, c = 1e-3, dt = 1e-6;
        var net = new EmtNetwork(1, new List<EmtElement>
        {
            new CurrentSource(1, Bus: 0, Inj: _ => i0),
            new ShuntElement(2, Bus: 0, C: c),
        });
        var eng = new EmtEngine(net, dt);
        EmtResult res = eng.Simulate(new EmtCase(net, dt, 100 * dt, new[] { new EmtProbe("v", EmtProbeKind.NodeVoltage, 0) }));
        for (int n = 1; n < res.Points.Count; n++)
        {
            double exact = i0 * (n * dt) / c;
            double v = res.Points[n].Probe[0];
            Assert.True(Math.Abs(v - exact) <= 1e-12 * exact,
                $"step {n}: v = {v:R}, exact ramp {exact:R} (the old half-step offset would be {-i0 * dt / (2 * c):R})");
        }
    }

    [Fact]
    public void Parallel_capacitors_at_one_node_share_the_injection_by_capacitance()
    {
        // Same ramp with two capacitors in parallel (1 mF and 3 mF): v(t) = I*t/(C1 + C2).
        const double i0 = 2.0, c1 = 1e-3, c2 = 3e-3, dt = 1e-6;
        var net = new EmtNetwork(1, new List<EmtElement>
        {
            new CurrentSource(1, Bus: 0, Inj: _ => i0),
            new ShuntElement(2, Bus: 0, C: c1),
            new ShuntElement(3, Bus: 0, C: c2),
        });
        EmtResult res = new EmtEngine(net, dt).Simulate(new EmtCase(net, dt, 50 * dt, new[] { new EmtProbe("v", EmtProbeKind.NodeVoltage, 0) }));
        for (int n = 1; n < res.Points.Count; n++)
        {
            double exact = i0 * (n * dt) / (c1 + c2);
            Assert.True(Math.Abs(res.Points[n].Probe[0] - exact) <= 1e-12 * exact, $"step {n}: {res.Points[n].Probe[0]:R} vs {exact:R}");
        }
    }

    [Fact]
    public void An_rc_circuit_charged_through_a_pure_resistance_arm_follows_the_closed_form_from_the_first_step()
    {
        // 1 V ideal source -> 1 ohm pure-R arm -> node 1 with a 1 mF capacitor to ground. Exact
        // v1(t) = 1 - exp(-t/RC). At t = 0+ the capacitor carries (1 - 0)/R = 1 A; it used to be seeded with 0, an
        // error of about V*dt/(2RC) = 5e-4 V at the first step. Trapezoidal's own error grows by about (dt/RC)^3/12 =
        // 8.3e-11 per step (measured: 1.07e-9 at step 13); the bound is 1e-8, more than four orders below the old error.
        const double r = 1.0, c = 1e-3, dt = 1e-6;
        var net = new EmtNetwork(2, new List<EmtElement>
        {
            new VoltageSource(10, Bus: 0, Rs: 0, Emf: _ => 1.0),
            new SeriesBranch(1, From: 0, To: 1, R: r, L: 0.0),
            new ShuntElement(2, Bus: 1, C: c),
        });
        EmtResult res = new EmtEngine(net, dt).Simulate(new EmtCase(net, dt, 20 * dt, new[] { new EmtProbe("v1", EmtProbeKind.NodeVoltage, 1) }));
        for (int n = 1; n < res.Points.Count; n++)
        {
            double t = n * dt;
            double exact = 1.0 - Math.Exp(-t / (r * c));
            Assert.True(Math.Abs(res.Points[n].Probe[0] - exact) < 1e-8, $"step {n}: {res.Points[n].Probe[0]:R} vs {exact:R}");
        }
    }

    [Fact]
    public void Backward_euler_was_and_is_exact_for_the_ramp()
    {
        // Backward Euler's capacitor history ignores the previous current, so the seed cannot matter: the ramp is
        // exact for it before and after. Guards that the fix did not leak into the backward-Euler path.
        const double i0 = 1.0, c = 1e-3, dt = 1e-6;
        var net = new EmtNetwork(1, new List<EmtElement>
        {
            new CurrentSource(1, Bus: 0, Inj: _ => i0),
            new ShuntElement(2, Bus: 0, C: c),
        });
        EmtResult res = new EmtEngine(net, dt, new EmtOptions { Rule = IntegrationRule.BackwardEuler })
            .Simulate(new EmtCase(net, dt, 20 * dt, new[] { new EmtProbe("v", EmtProbeKind.NodeVoltage, 0) }));
        for (int n = 1; n < res.Points.Count; n++)
        {
            double exact = i0 * (n * dt) / c;
            Assert.True(Math.Abs(res.Points[n].Probe[0] - exact) <= 1e-12 * exact);
        }
    }

    [Fact]
    public void The_t0_sample_is_the_de_energised_state()
    {
        // Documented convention (technical ch. 21, "Rest initial condition"): the t = 0 row is recorded before the
        // sources switch on, so every node voltage reads 0 there, including an ideal source's own node. Pinned so a
        // change of convention is a deliberate decision (recorded for Mark under N032), not an accident.
        var net = new EmtNetwork(2, new List<EmtElement>
        {
            new VoltageSource(10, Bus: 0, Rs: 0, Emf: _ => 1.0),
            new SeriesBranch(1, From: 0, To: 1, R: 1.0, L: 0.0),
            new ShuntElement(2, Bus: 1, G: 1.0),
            new CurrentSource(3, Bus: 1, Inj: _ => 0.25),
        });
        const double dt = 1e-6;
        EmtResult res = new EmtEngine(net, dt).Simulate(new EmtCase(net, dt, 2 * dt, new[]
        {
            new EmtProbe("v0", EmtProbeKind.NodeVoltage, 0), new EmtProbe("v1", EmtProbeKind.NodeVoltage, 1),
        }));
        Assert.Equal(0.0, res.Points[0].Probe[0]);
        Assert.Equal(0.0, res.Points[0].Probe[1]);
        // From the first step: source node at 1 V; node 1 by superposition (1 V through 1 ohm, 1 S to ground, 0.25 A
        // injected) = (1 + 0.25) / 2 = 0.625 V.
        Assert.Equal(1.0, res.Points[1].Probe[0]);
        Assert.Equal(0.625, res.Points[1].Probe[1], 15);
    }

    // ---------------------------------------------------------------- N031

    /// <summary>A source feeding node 1 through an inductor, and an island of nodes 2-3 joined to node 1 only by an
    /// inductor: the island's only conducting element is a 1e-5 ohm (1e5 S) arm, and an open switch ties node 3 to
    /// ground. Exactly the shape that broke pglib case240.</summary>
    private static EmtNetwork IslandBehindOpenSwitch(bool switchClosed) => new(4, new List<EmtElement>
    {
        new VoltageSource(10, Bus: 0, Rs: 0, Emf: _ => 1.0),
        new SeriesBranch(1, From: 0, To: 1, R: 0.01, L: 1e-3),
        new ShuntElement(2, Bus: 1, G: 1.0),
        new SeriesBranch(3, From: 1, To: 2, R: 0.0, L: 1e-3),
        new SeriesBranch(4, From: 2, To: 3, R: 1e-5, L: 0.0),
        new SwitchElement(5, 3, EmtNetwork.Ground, Ron: 0.01, Roff: 1e9, InitiallyClosed: switchClosed),
    });

    [Fact]
    public void An_island_tied_to_ground_only_by_an_open_switch_is_pinned_instead_of_failing_the_rest_solve()
    {
        // Before: the open switch anchored nodes 2-3, so the island's block of the rest matrix was
        // [[1e5, -1e5], [-1e5, 1e5 + 1e-9]], whose second pivot (about 1e-9) is below the LU's pivot floor relative
        // to the 1e5 entries, and the constructor threw "initial-condition matrix is singular".
        var eng = new EmtEngine(IslandBehindOpenSwitch(switchClosed: false), 1e-6);
        Assert.Equal(2, eng.InitialConditionFloatingNodes);
    }

    [Fact]
    public void A_closed_switch_still_anchors_its_island()
    {
        var eng = new EmtEngine(IslandBehindOpenSwitch(switchClosed: true), 1e-6);
        Assert.Equal(0, eng.InitialConditionFloatingNodes);
    }

    [Fact]
    public void The_island_fault_run_completes_and_reset_reproduces_it_bit_for_bit()
    {
        // Close the switch at 10 us and clear it at 30 us: the run must complete with finite values, and a Reset
        // followed by a second run must give the same bits (the open-switch flag is construction state).
        EmtNetwork net = IslandBehindOpenSwitch(switchClosed: false);
        const double dt = 1e-6;
        var events = new List<EmtEvent> { new SwitchEvent(10e-6, 5, Close: true), new SwitchEvent(30e-6, 5, Close: false) };
        var probes = new[] { new EmtProbe("v3", EmtProbeKind.NodeVoltage, 3), new EmtProbe("i1", EmtProbeKind.BranchCurrent, 1) };
        var eng = new EmtEngine(net, dt);
        EmtResult a = eng.Simulate(new EmtCase(net, dt, 50 * dt, probes, events));
        eng.Reset();
        EmtResult b = eng.Simulate(new EmtCase(net, dt, 50 * dt, probes, events));
        Assert.Equal(a.Points.Count, b.Points.Count);
        for (int n = 0; n < a.Points.Count; n++)
            for (int j = 0; j < probes.Length; j++)
            {
                Assert.True(double.IsFinite(a.Points[n].Probe[j]));
                Assert.Equal(a.Points[n].Probe[j], b.Points[n].Probe[j]);
            }
        Assert.Equal(2, eng.InitialConditionFloatingNodes);
    }

    [Fact]
    public void Pglib_case240_with_a_fault_switch_on_its_last_bus_builds_its_rest_state()
    {
        // The N031 reproduction: the CLI's default build (DC source at the first generator's bus, loads on) plus an
        // open fault switch on the last node, as `gridsim emt pglib_opf_case240_pserc.m --event fault:8034@...` adds.
        // It used to throw "EMT initial-condition matrix is singular over 8 node(s) even after pinning 56 floating
        // node(s)". The fault switch must not change the floating count of the plain build (57).
        GridModel m = MatpowerParser.ParseFile(Path.Combine(DataDir, "matpower", "nesta", "pglib_opf_case240_pserc.m"));
        int src = m.Generators[0].Bus;
        EmtNetwork plain = EmtNetwork.FromGridModel(m, new EmtBuildOptions(new[] { new EmtSourceSpec(src) }));
        int plainFloating = new EmtEngine(plain, 20e-6).InitialConditionFloatingNodes;
        Assert.Equal(57, plainFloating);

        int last = plain.BusToNode[m.Buses[^1].Id];
        var elements = new List<EmtElement>(plain.Elements)
        {
            new SwitchElement(1_000_000, last, EmtNetwork.Ground, Ron: 0.01, Roff: 1e9, InitiallyClosed: false),
        };
        var faulted = new EmtNetwork(plain.NodeCount, elements, plain.BusToNode, plain.SeriesBranchIds);
        var eng = new EmtEngine(faulted, 20e-6);
        Assert.Equal(plainFloating, eng.InitialConditionFloatingNodes);
    }
}
