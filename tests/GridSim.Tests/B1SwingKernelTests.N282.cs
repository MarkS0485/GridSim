using GridSim.Core.Physics.Experimental;
using Xunit;

namespace GridSim.Tests;

/// <summary>
/// N282 (ruled by main under Ruling R1): the wave model counts its zero (rigid-body) modes as the number of connected
/// components of the synchronising graph, skips exactly that many and takes the first mode after them as the dominant
/// inter-area mode. Before, the first mode above 1e-6 Hz was taken, which picks the centre-of-inertia mode when its
/// round-off lifts it above the threshold and an island's rigid-body mode on an islanded network.
/// </summary>
public sealed partial class B1SwingKernelTests
{
    private static void Tie(double[,] k, int a, int b, double v) { k[a, b] = v; k[b, a] = v; }

    [Fact]
    public void N282_an_islanded_network_skips_one_rigid_body_mode_per_island()
    {
        // Two islands of two machines each: two zero modes, then the two intra-island modes.
        double[] h = { 5.0, 4.0, 3.0, 6.0 };
        var k = new double[4, 4];
        Tie(k, 0, 1, 1.5);
        Tie(k, 2, 3, 0.8);
        ContinuumWaveModel.WaveModes w = ContinuumWaveModel.Analyze(h, k, 50.0);
        Assert.Equal(2, w.RigidBodyModeCount);
        Assert.Equal(w.ModeFrequenciesHz[2], w.DominantInterAreaHz);
        Assert.True(w.DominantInterAreaHz > 0.01, $"the dominant mode must be a real oscillation, got {w.DominantInterAreaHz:R} Hz");
        Assert.Equal(2, w.InterAreaModesHz.Count);
        Assert.Equal(w.ModeFrequenciesHz.Skip(2), w.InterAreaModesHz);
        // The dominant shape is a real swing: two machines against each other in one island, the other island still.
        Assert.Equal(4, w.DominantModeShape.Count);
    }

    [Fact]
    public void N282_a_machine_with_no_coupling_is_an_island_of_its_own()
    {
        double[] h = { 5.0, 4.0, 3.0 };
        var k = new double[3, 3];
        Tie(k, 0, 1, 1.5);   // machine 2 is coupled to nothing
        ContinuumWaveModel.WaveModes w = ContinuumWaveModel.Analyze(h, k, 50.0);
        Assert.Equal(2, w.RigidBodyModeCount);
        Assert.Equal(w.ModeFrequenciesHz[2], w.DominantInterAreaHz);
        Assert.Single(w.InterAreaModesHz);
    }

    [Fact]
    public void N282_with_no_coupling_at_all_there_is_no_oscillation_mode()
    {
        double[] h = { 5.0, 4.0 };
        ContinuumWaveModel.WaveModes w = ContinuumWaveModel.Analyze(h, new double[2, 2], 50.0);
        Assert.Equal(2, w.RigidBodyModeCount);
        Assert.Equal(0.0, w.DominantInterAreaHz);
        Assert.Empty(w.InterAreaModesHz);
        Assert.Empty(w.DominantModeShape);
    }

    [Fact]
    public void N282_a_connected_network_selects_what_the_threshold_rule_selected()
    {
        // Connected networks whose COI mode rounds below 1e-6 Hz and whose slowest real mode is above it: the cases the
        // old rule got right. One rigid-body mode; the dominant mode, the inter-area list and the shape are the old ones.
        var cases = new List<(double[] H, double[,] K)>();
        {
            var k = new double[4, 4];
            Tie(k, 0, 1, 20.0); Tie(k, 2, 3, 20.0); Tie(k, 1, 2, 0.5);
            cases.Add((new[] { 5.0, 5.0, 4.0, 4.0 }, k));
        }
        cases.Add((new[] { 3.5, 1e8 }, new double[,] { { 0, 1.7 }, { 1.7, 0 } }));
        {
            var k = new double[3, 3];
            Tie(k, 0, 1, 3.0); Tie(k, 1, 2, 2.0); Tie(k, 0, 2, 0.4);
            cases.Add((new[] { 6.0, 2.0, 3.5 }, k));
        }
        foreach ((double[] h, double[,] k) in cases)
        {
            ContinuumWaveModel.WaveModes w = ContinuumWaveModel.Analyze(h, k, 50.0);
            Assert.Equal(1, w.RigidBodyModeCount);
            int oldIdx = -1;
            for (int i = 0; i < w.ModeFrequenciesHz.Count; i++)
                if (w.ModeFrequenciesHz[i] > ContinuumWaveModel.NonZeroModeThresholdHz) { oldIdx = i; break; }
            Assert.Equal(1, oldIdx);   // precondition: the old rule picked index 1 here
            AssertBits(w.ModeFrequenciesHz[oldIdx], w.DominantInterAreaHz, "dominant");
            Assert.Equal(w.ModeFrequenciesHz.Skip(1), w.InterAreaModesHz);
        }
    }

    [Fact]
    public void N282_the_component_count_is_the_graph_count()
    {
        var kl = new double[5, 5];
        kl[0, 1] = -1.0; kl[1, 0] = -1.0;
        kl[2, 3] = -0.0;            // a clamped zero coupling is no edge
        kl[3, 4] = -2.0;            // a one-sided entry still joins the pair
        Assert.Equal(3, ContinuumWaveModel.SynchronisingComponents(kl));   // {0,1}, {2}, {3,4}
    }
}
