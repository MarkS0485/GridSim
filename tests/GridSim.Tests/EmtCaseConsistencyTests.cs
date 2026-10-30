using System;
using System.Collections.Generic;
using GridSim.Core.Dynamics.Emt;
using Xunit;

namespace GridSim.Tests;

/// <summary>
/// R077 (October 2026 review): <see cref="EmtCase"/> repeats the network and timestep the engine is built with,
/// and <see cref="EmtEngine.Simulate"/> used to ignore the case's network and use the case's dt only for time
/// stamps and the post-CDA step (the companions and Bergeron delays kept the constructor dt). A second Simulate
/// continued the old state with time restarted at zero. Now the case is checked against the engine, a second run
/// is refused, and <see cref="EmtEngine.Reset"/> returns the engine to its as-built state bit for bit.
/// </summary>
public class EmtCaseConsistencyTests
{
    private const double Dt = 2e-6;

    // V source -> switch 11 (initially closed) -> series R-L 12 -> node 2 -> C 13 to ground, plus a lossless
    // travelling-wave line 14 from node 2 to node 3 loaded by a resistor 15. Exercises every kind of state the
    // engine carries: switch conductances, R-L and C histories, and the Bergeron delay buffers.
    private static EmtNetwork Net(double r = 0.01) => new(4, new List<EmtElement>
    {
        new VoltageSource(10, 0, 0, t => Math.Cos(2.0 * Math.PI * 50.0 * t)),
        new SwitchElement(11, 0, 1, Ron: 1e-6, Roff: 1e9, InitiallyClosed: true),
        new SeriesBranch(12, 1, 2, r, 1e-3),
        new ShuntElement(13, 2, C: 1e-3),
        new BergeronLine(14, 2, 3, SurgeImpedance: 0.5, TauSeconds: 1e-4),
        new ShuntElement(15, 3, G: 1.0),
    });

    private static readonly EmtProbe[] Probes =
    {
        new("v2", EmtProbeKind.NodeVoltage, 2),
        new("v3", EmtProbeKind.NodeVoltage, 3),
        new("i12", EmtProbeKind.BranchCurrent, 12),
        new("i14", EmtProbeKind.BranchCurrent, 14),
    };

    // Open the switch at 0.5 ms and close it again at 1 ms: two CDA bursts and two re-factorisations.
    private static readonly EmtEvent[] Events = { new SwitchEvent(5e-4, 11, Close: false), new SwitchEvent(1e-3, 11, Close: true) };

    private static EmtCase Case(EmtNetwork net, double dt = Dt) => new(net, dt, 2e-3, Probes, Events);

    private static void AssertBitIdentical(EmtResult a, EmtResult b)
    {
        Assert.Equal(a.Points.Count, b.Points.Count);
        Assert.Equal(a.Stats.Steps, b.Stats.Steps);
        Assert.Equal(a.Stats.Refactorizations, b.Stats.Refactorizations);
        Assert.Equal(a.Stats.MaxProbeAbs, b.Stats.MaxProbeAbs);
        for (int k = 0; k < a.Points.Count; k++)
        {
            Assert.Equal(a.Points[k].TSeconds, b.Points[k].TSeconds);
            for (int j = 0; j < a.Points[k].Probe.Length; j++) Assert.Equal(a.Points[k].Probe[j], b.Points[k].Probe[j]);
        }
    }

    [Fact]
    public void A_case_with_a_different_dt_is_refused()
    {
        EmtNetwork net = Net();
        var engine = new EmtEngine(net, Dt);
        var ex = Assert.Throws<ArgumentException>(() => engine.Simulate(Case(net, Dt * 2.0)));
        Assert.Contains("DtSeconds", ex.Message);
        // Even a one-ulp difference is a different timestep.
        Assert.Throws<ArgumentException>(() => engine.Simulate(Case(net, Math.BitIncrement(Dt))));
        Assert.Throws<ArgumentException>(() => engine.Simulate(Case(net, double.NaN)));
    }

    [Fact]
    public void A_case_with_a_different_network_is_refused()
    {
        var engine = new EmtEngine(Net(), Dt);
        var ex = Assert.Throws<ArgumentException>(() => engine.Simulate(Case(Net(r: 0.02))));
        Assert.Contains("not the network this engine was built from", ex.Message);

        var smaller = new EmtNetwork(3, new List<EmtElement> { new VoltageSource(10, 0, 0, _ => 1.0), new ShuntElement(13, 2, C: 1e-3) });
        var ex2 = Assert.Throws<ArgumentException>(() => engine.Simulate(Case(smaller)));
        Assert.Contains("node count", ex2.Message);
    }

    [Fact]
    public void Refused_cases_leave_the_engine_unadvanced()
    {
        // A refused case must not consume the engine's single run: the correct case still runs afterwards.
        EmtNetwork net = Net();
        var engine = new EmtEngine(net, Dt);
        Assert.Throws<ArgumentException>(() => engine.Simulate(Case(net, Dt * 2.0)));
        Assert.Throws<ArgumentException>(() => engine.Simulate(new EmtCase(net, Dt, 1e-3, Probes, new EmtEvent[] { new SwitchEvent(1e-4, 12, true) })));
        AssertBitIdentical(new EmtEngine(net, Dt).Simulate(Case(net)), engine.Simulate(Case(net)));
    }

    [Fact]
    public void An_equivalent_rebuilt_network_is_accepted_and_gives_the_same_trajectory()
    {
        // A separately-built network with the same elements (and different, uncomparable lambda objects) is the
        // same network; existing callers rely on this.
        EmtNetwork net = Net();
        EmtResult viaSame = new EmtEngine(net, Dt).Simulate(Case(net));
        EmtResult viaRebuilt = new EmtEngine(net, Dt).Simulate(Case(Net()));
        AssertBitIdentical(viaSame, viaRebuilt);
    }

    [Fact]
    public void The_engine_exposes_the_network_and_timestep_it_was_built_with()
    {
        EmtNetwork net = Net();
        var engine = new EmtEngine(net, Dt);
        Assert.Same(net, engine.Network);
        Assert.Equal(Dt, engine.DtSeconds);
    }

    [Fact]
    public void A_second_simulate_without_reset_is_refused()
    {
        EmtNetwork net = Net();
        var engine = new EmtEngine(net, Dt);
        engine.Simulate(Case(net));
        var ex = Assert.Throws<InvalidOperationException>(() => engine.Simulate(Case(net)));
        Assert.Contains("Reset()", ex.Message);
    }

    [Fact]
    public void Simulate_after_a_manual_step_is_refused()
    {
        EmtNetwork net = Net();
        var engine = new EmtEngine(net, Dt);
        engine.Step(Dt);
        Assert.Throws<InvalidOperationException>(() => engine.Simulate(Case(net)));
    }

    [Fact]
    public void Reset_then_simulate_is_bit_identical_to_a_fresh_engine()
    {
        EmtNetwork net = Net();
        EmtResult fresh = new EmtEngine(net, Dt).Simulate(Case(net));

        var engine = new EmtEngine(net, Dt);
        engine.Simulate(Case(net));     // leaves the switch toggled twice, histories and delay lines full
        engine.Reset();
        EmtResult again = engine.Simulate(Case(net));
        AssertBitIdentical(fresh, again);

        // And once more, after a manual step as well.
        engine.Reset();
        engine.Step(Dt);
        engine.Reset();
        AssertBitIdentical(fresh, engine.Simulate(Case(net)));
    }

    [Fact]
    public void Reset_restores_an_initially_open_switch()
    {
        // A switch that starts OPEN and is closed by an event must be open again after Reset.
        var elements = new List<EmtElement>
        {
            new VoltageSource(10, 0, 0, _ => 1.0),
            new SwitchElement(11, 0, 1, Ron: 1e-6, Roff: 1e9, InitiallyClosed: false),
            new SeriesBranch(12, 1, 2, 0.01, 1e-3),
            new ShuntElement(13, 2, C: 1e-3),
        };
        var net = new EmtNetwork(3, elements);
        var probes = new[] { new EmtProbe("v2", EmtProbeKind.NodeVoltage, 2) };
        var c = new EmtCase(net, Dt, 1e-3, probes, new EmtEvent[] { new SwitchEvent(2e-4, 11, Close: true) });
        EmtResult fresh = new EmtEngine(net, Dt).Simulate(c);
        var engine = new EmtEngine(net, Dt);
        engine.Simulate(c);
        engine.Reset();
        AssertBitIdentical(fresh, engine.Simulate(c));
    }

    [Fact]
    public void Three_phase_engine_refuses_a_second_simulate()
    {
        var net = new ThreePhaseNetwork(
            BusCount: 2,
            Sources: new[] { new ThreePhaseSource(0, 1.0, 50.0) },
            Branches: new[] { new ThreePhaseBranch(0, 1, 1.0, 1e-3) },
            Shunts: new[] { new ThreePhaseShunt(1, G: 0.1) },
            Faults: Array.Empty<ThreePhaseFault>());
        var engine = new ThreePhaseEmtEngine(net, 20e-6, 50.0);
        engine.Simulate(1e-3, probeBus: 1);
        var ex = Assert.Throws<InvalidOperationException>(() => engine.Simulate(1e-3, probeBus: 1));
        Assert.Contains("new engine", ex.Message);
    }
}
