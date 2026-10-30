using System.Collections.Generic;
using GridSim.Core.Dynamics.Emt;
using Xunit;

namespace GridSim.Tests;

/// <summary>Reduce-to-steady-state: a network energised from a DC source must relax to the resistive DC nodal
/// solution (inductors become shorts, capacitors open). This is the bridge between the time-domain EMT engine
/// and the algebraic solution the rest of GridSim computes.</summary>
public class EmtSteadyStateTests
{
    [Fact]
    public void Rl_load_energised_dc_settles_to_the_resistive_solution()
    {
        // node0 = ideal V source -> [R,L] -> node1 -> [Rload] -> ground.
        const double v = 1.0, r = 1.0, rload = 1.0, l = 1e-3;
        var elements = new List<EmtElement>
        {
            new VoltageSource(3, 0, 0, _ => v),
            new SeriesBranch(1, 0, 1, r, l),
            new ShuntElement(2, 1, G: 1.0 / rload),
        };
        var net = new EmtNetwork(2, elements);
        var probes = new[]
        {
            new EmtProbe("i", EmtProbeKind.BranchCurrent, 1),
            new EmtProbe("v1", EmtProbeKind.NodeVoltage, 1),
        };

        // Time constant tau = L/(R+Rload) = 5e-4; 1e-2 s is 20 tau -> fully settled.
        EmtResult res = new EmtEngine(net, 1e-5).Simulate(new EmtCase(net, 1e-5, 1e-2, probes));
        EmtPoint last = res.Points[^1];

        double iDc = v / (r + rload);            // 0.5
        double v1Dc = v * rload / (r + rload);   // 0.5
        Assert.Equal(iDc, last.Probe[0], 6);
        Assert.Equal(v1Dc, last.Probe[1], 6);
    }
}
