using GridSim.Core.Dynamics;
using Xunit;

namespace GridSim.Tests;

/// <summary>
/// The intrinsic-vs-synthetic invariant, enforced at the swing-equation chokepoint:
/// (a) the renamed field is byte-identical to its <c>[Obsolete]</c> alias;
/// (b) the device's rotational inertia is <b>pure (1/2)J*omega^2</b> - it enters only via E, so adding it is identical to
///     adding the same MW.s of system inertia (there is NO df/dt-proportional "synthetic inertia" term);
/// (c) the fast-response term does NOT reduce E (it is a separate power injection, never blurred with inertia).
/// </summary>
public class DynamicsConfigInvariantTests
{
    private static FrequencyResponse Trip(double systemInertiaMwS, DynamicsConfig cfg) =>
        FrequencyDynamics.Simulate(systemInertiaMwS, 30_000, new[] { new FrequencyEvent(1.0, -1320) }, cfg);

    [Fact]
    public void Renamed_field_and_obsolete_alias_are_identical()
    {
#pragma warning disable CS0618 // exercising the back-compat alias on purpose
        var viaAlias = new DynamicsConfig { SyntheticInertiaMwS = 80_000 };
#pragma warning restore CS0618
        var viaCanonical = new DynamicsConfig { DeviceRotationalInertiaMwS = 80_000 };

        Assert.Equal(viaCanonical.DeviceRotationalInertiaMwS, viaAlias.DeviceRotationalInertiaMwS, 9);

        FrequencyResponse a = Trip(100_000, viaAlias);
        FrequencyResponse b = Trip(100_000, viaCanonical);
        Assert.Equal(b.InitialRocofHzPerS, a.InitialRocofHzPerS, 12);
        Assert.Equal(b.NadirHz, a.NadirHz, 12);
    }

    [Fact]
    public void Device_inertia_is_pure_half_J_omega_squared()
    {
        // Adding X MW.s of device rotational inertia MUST equal adding X MW.s of system inertia:
        // both simply increase E. If a separate df/dt term existed, these would diverge.
        const double x = 60_000;
        FrequencyResponse asDevice = Trip(100_000, new DynamicsConfig { DeviceRotationalInertiaMwS = x });
        FrequencyResponse asSystem = Trip(100_000 + x, new DynamicsConfig());

        Assert.Equal(asSystem.InitialRocofHzPerS, asDevice.InitialRocofHzPerS, 12);
        Assert.Equal(asSystem.NadirHz, asDevice.NadirHz, 12);
        Assert.Equal(asSystem.SettleHz, asDevice.SettleHz, 12);
    }

    [Fact]
    public void Fast_response_does_not_reduce_inertia()
    {
        // FFR is a power injection, not stored energy: it must NOT change E, so the initial RoCoF (set purely by
        // E at the event instant, before FFR ramps past its deadband) is unchanged by adding fast response.
        FrequencyResponse noDevice = Trip(100_000, new DynamicsConfig());
        FrequencyResponse ffrOnly = Trip(100_000, new DynamicsConfig { FastResponseMw = 3000 });

        Assert.Equal(noDevice.InitialRocofHzPerS, ffrOnly.InitialRocofHzPerS, 12);
        // ...but it DOES lift the nadir - proving the two terms act through different channels.
        Assert.True(ffrOnly.NadirHz > noDevice.NadirHz + 1e-4,
            $"FFR should lift the nadir: {noDevice.NadirHz:F4} -> {ffrOnly.NadirHz:F4}");
    }
}
