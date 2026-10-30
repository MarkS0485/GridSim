using GridSim.Core.Model;
using GridSim.Core.Physics.Experimental;
using GridSim.Core.PowerFlow;
using Xunit;

namespace GridSim.Tests;

/// <summary>
/// The propagating wave-field visualisation core: a disturbance at the source spreads outward at a
/// FINITE speed, so distant buses carry a larger phase delay and a smaller amplitude. Pins those
/// monotonic properties and the source reference (amplitude 1, phase 0).
/// </summary>
public class WaveFieldTests
{
    [Fact]
    public void The_ripple_delays_and_decays_with_distance_from_the_source()
    {
        // Radial chain 1-2-3-4 (no map coords -> hop distance). Source = bus 1.
        var m = new GridModel
        {
            BaseMva = 100,
            Buses = new[]
            {
                new Bus(1, BusType.Slack), new Bus(2, BusType.Pq, Pd: 10),
                new Bus(3, BusType.Pq, Pd: 10), new Bus(4, BusType.Pq, Pd: 10),
            },
            Generators = new[] { new Generator(1, Pg: 0) },
            Branches = new[]
            {
                new Branch(1, 2, 0.01, 0.05), new Branch(2, 3, 0.01, 0.05), new Branch(3, 4, 0.01, 0.05),
            },
        };
        PowerFlowResult r = new NewtonRaphsonPowerFlow().Solve(m);
        WaveField.WaveFieldResult w = WaveField.Compute(m, r, sourceBusId: 1);

        Assert.True(w.FrequencyHz > 0);
        Assert.Equal(1, w.SourceBus);
        WaveField.NodeWave n1 = w.Nodes.Single(x => x.Bus == 1);
        WaveField.NodeWave n2 = w.Nodes.Single(x => x.Bus == 2);
        WaveField.NodeWave n4 = w.Nodes.Single(x => x.Bus == 4);

        // Source: full amplitude, no delay.
        Assert.Equal(1.0, n1.Amplitude, 9);
        Assert.Equal(0.0, n1.PhaseRad, 9);

        // Finite-speed propagation: phase delay grows with distance; amplitude decays.
        Assert.True(n4.PhaseRad > n2.PhaseRad, "farther bus must lag more");
        Assert.True(n2.PhaseRad > 0.0);
        Assert.True(n4.Amplitude < n2.Amplitude, "farther bus must be weaker");
        Assert.True(n2.Amplitude < 1.0);
    }
}
