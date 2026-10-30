using System;
using System.Collections.Generic;
using GridSim.Core.Dynamics.Emt;
using Xunit;

namespace GridSim.Tests;

/// <summary>The Bergeron travelling-wave line: exact wave propagation, matched-line absorption (no reflection),
/// open-end voltage doubling, and the correct travel delay - the distributed-parameter behaviour a lumped pi
/// section cannot reproduce.</summary>
public class EmtLineTests
{
    private const double V = 1.0, Zc = 100.0, Tau = 1e-3, Dt = 1e-5;

    private static double At(EmtResult res, double t, int probe)
    {
        double best = double.MaxValue, val = double.NaN;
        foreach (EmtPoint p in res.Points)
        {
            double d = Math.Abs(p.TSeconds - t);
            if (d < best) { best = d; val = p.Probe[probe]; }
        }
        return val;
    }

    private static EmtResult Run(IReadOnlyList<EmtElement> elements, int nodes, EmtProbe[] probes, double dur)
    {
        var net = new EmtNetwork(nodes, elements);
        return new EmtEngine(net, Dt).Simulate(new EmtCase(net, Dt, dur, probes));
    }

    [Fact]
    public void Matched_line_absorbs_the_wave_with_no_reflection()
    {
        // Thevenin source (Rs = Zc) -> line -> matched load (Zc). Sending end sits at V/2; the wave reaches the
        // far end at tau and is absorbed (stays V/2, no reflection).
        var elements = new List<EmtElement>
        {
            new VoltageSource(1, 0, Rs: Zc, Emf: _ => V),
            new BergeronLine(2, 0, 1, Zc, Tau),
            new ShuntElement(3, 1, G: 1.0 / Zc),
        };
        var probes = new[] { new EmtProbe("v1", EmtProbeKind.NodeVoltage, 1), new EmtProbe("v0", EmtProbeKind.NodeVoltage, 0) };
        EmtResult res = Run(elements, 2, probes, 3e-3);

        Assert.True(At(res, 0.5e-3, 0) < 0.02, "far end must be quiet before the wave arrives");
        Assert.Equal(0.5, At(res, 2e-3, 0), 2);   // far end settles at V/2, no reflection
        Assert.Equal(0.5, At(res, 2.9e-3, 0), 2);
        Assert.Equal(0.5, At(res, 2e-3, 1), 2);   // sending end stays V/2 (matched, no returning wave)
    }

    [Fact]
    public void Open_ended_line_doubles_the_voltage_at_the_far_end()
    {
        // Thevenin source (Rs = Zc) -> line -> open end. The V/2 incident wave reflects +1 at the open end ->
        // far end reaches V at tau; the reflection returns and the matched source reaches V at 2*tau.
        var elements = new List<EmtElement>
        {
            new VoltageSource(1, 0, Rs: Zc, Emf: _ => V),
            new BergeronLine(2, 0, 1, Zc, Tau),
        };
        var probes = new[] { new EmtProbe("v1", EmtProbeKind.NodeVoltage, 1), new EmtProbe("v0", EmtProbeKind.NodeVoltage, 0) };
        EmtResult res = Run(elements, 2, probes, 3e-3);

        Assert.True(At(res, 0.9e-3, 0) < 0.05, "open far end quiet before tau");
        Assert.Equal(1.0, At(res, 1.5e-3, 0), 2);   // open-end doubling: V/2 incident -> V
        Assert.Equal(0.5, At(res, 0.5e-3, 1), 2);   // sending end at V/2 before the reflection returns
        Assert.Equal(1.0, At(res, 2.5e-3, 1), 2);   // reflection absorbed at the matched source -> V at 2*tau
    }
}
