using GridSim.Core.Dynamics;
using Xunit;

namespace GridSim.Tests;

/// <summary>
/// The TSGB device in the swing-equation model: synthetic inertia must cut RoCoF,
/// and fast frequency response must lift the nadir  -  with both off, behaviour is
/// unchanged.
/// </summary>
public class TsgbDynamicsTests
{
    // 100 GVAs spinning, 30 GW demand, lose 1320 MW (a large GB infeed) at t=1s.
    private static FrequencyResponse Trip(DynamicsConfig cfg) =>
        FrequencyDynamics.Simulate(100_000, 30_000, new[] { new FrequencyEvent(1.0, -1320) }, cfg);

    [Fact]
    public void Synthetic_inertia_reduces_rocof()
    {
        FrequencyResponse baseline = Trip(new DynamicsConfig());
        FrequencyResponse withTsgb = Trip(new DynamicsConfig { DeviceRotationalInertiaMwS = 100_000 }); // +100 GVAs

        Assert.True(Math.Abs(withTsgb.InitialRocofHzPerS) < Math.Abs(baseline.InitialRocofHzPerS),
            $"RoCoF should fall: {baseline.InitialRocofHzPerS:F3} -> {withTsgb.InitialRocofHzPerS:F3}");
    }

    [Fact]
    public void Fast_response_lifts_the_nadir()
    {
        FrequencyResponse baseline = Trip(new DynamicsConfig());
        FrequencyResponse withTsgb = Trip(new DynamicsConfig { FastResponseMw = 2000 });

        Assert.True(withTsgb.NadirHz > baseline.NadirHz + 1e-4,
            $"nadir should lift: {baseline.NadirHz:F3} -> {withTsgb.NadirHz:F3}");
    }

    [Fact]
    public void Defaults_match_no_device()
    {
        FrequencyResponse a = Trip(new DynamicsConfig());
        FrequencyResponse b = Trip(new DynamicsConfig { DeviceRotationalInertiaMwS = 0, FastResponseMw = 0 });
        Assert.Equal(a.InitialRocofHzPerS, b.InitialRocofHzPerS, 9);
        Assert.Equal(a.NadirHz, b.NadirHz, 9);
    }
}
