using GridSim.Core.Dynamics;
using Xunit;

namespace GridSim.Tests;

public class FrequencyDynamicsTests
{
    [Fact]
    public void No_event_stays_at_nominal()
    {
        var r = FrequencyDynamics.Simulate(200_000, 38_000, Array.Empty<FrequencyEvent>(),
            new DynamicsConfig { DurationSeconds = 5 });
        Assert.All(r.Points, p => Assert.Equal(50.0, p.Hz, 6));
        Assert.Equal(0.0, r.InitialRocofHzPerS, 6);
    }

    [Fact]
    public void Initial_rocof_matches_the_swing_equation()
    {
        const double e = 200_000, dP = -1000, f0 = 50;
        var r = FrequencyDynamics.Simulate(e, 38_000, new[] { new FrequencyEvent(0.5, dP) });
        Assert.Equal(f0 * dP / (2 * e), r.InitialRocofHzPerS, 3);  // -0.125 Hz/s
    }

    [Fact]
    public void Lower_inertia_steepens_rocof_and_deepens_nadir()
    {
        var ev = new[] { new FrequencyEvent(1, -1000) };
        var hi = FrequencyDynamics.Simulate(300_000, 38_000, ev);
        var lo = FrequencyDynamics.Simulate(150_000, 38_000, ev);

        Assert.True(Math.Abs(lo.InitialRocofHzPerS) > Math.Abs(hi.InitialRocofHzPerS));
        Assert.True(lo.NadirHz < hi.NadirHz);
        Assert.True(hi.NadirHz < 50.0);
    }

    [Fact]
    public void Frequency_recovers_above_the_nadir()
    {
        var r = FrequencyDynamics.Simulate(200_000, 38_000, new[] { new FrequencyEvent(1, -1000) });
        Assert.True(r.NadirHz < 50.0);
        Assert.True(r.SettleHz > r.NadirHz, "governor + damping should recover frequency above the nadir");
    }

    [Fact]
    public void Generation_loss_drives_frequency_down_load_gain_up()
    {
        var loss = FrequencyDynamics.Simulate(200_000, 38_000, new[] { new FrequencyEvent(1, -1000) });
        var surplus = FrequencyDynamics.Simulate(200_000, 38_000, new[] { new FrequencyEvent(1, +1000) });
        Assert.True(loss.NadirHz < 50.0);
        Assert.True(surplus.Points.Max(p => p.Hz) > 50.0);
    }
}
