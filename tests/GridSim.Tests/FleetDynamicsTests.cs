using System.Collections.Generic;
using GridSim.Core.Dynamics;
using GridSim.Core.Fleet;
using Xunit;

namespace GridSim.Tests;

/// <summary>
/// Fleet emergent behaviour: intrinsic inertia is N-additive (RoCoF is unchanged by how the same MW.s is split),
/// fast response is NOT (a staggered fleet has less FFR online at the nadir than one aggregate unit, so the nadir
/// is deeper), and a single unit at zero stagger reduces exactly to <see cref="FrequencyDynamics.Simulate"/>.
/// </summary>
public class FleetDynamicsTests
{
    private static readonly FrequencyEvent[] Trip = { new(1.0, -1320) };
    private const double BaseE = 50_000, Demand = 30_000;

    [Fact]
    public void Intrinsic_inertia_is_N_additive()
    {
        // Same total device inertia, split 1 vs 100 ways, zero FFR: the initial RoCoF must be identical,
        // and must match the closed-form f0/(2E)*deltaP with E = base + total device inertia.
        FleetResult one = FleetDynamics.Simulate(BaseE, Demand, Trip, FleetSpec.Uniform(1, 40_000, 0));
        FleetResult many = FleetDynamics.Simulate(BaseE, Demand, Trip, FleetSpec.Uniform(100, 40_000, 0));

        Assert.Equal(one.Response.InitialRocofHzPerS, many.Response.InitialRocofHzPerS, 12);
        double expectedRocof = 50.0 / (2.0 * (BaseE + 40_000)) * -1320;
        Assert.Equal(expectedRocof, many.Response.InitialRocofHzPerS, 6);
    }

    [Fact]
    public void Fast_response_is_non_additive_when_staggered()
    {
        // Same total FFR budget: one aggregate unit vs 100 units staggered over 4 s. At the nadir the fleet has
        // fewer units online, so it delivers less FFR and the nadir is deeper - the emergent non-additivity.
        FleetResult aggregate = FleetDynamics.Simulate(BaseE, Demand, Trip, FleetSpec.Uniform(1, 10_000, 2000, maxStaggerS: 0));
        FleetResult fleet = FleetDynamics.Simulate(BaseE, Demand, Trip, FleetSpec.Uniform(100, 10_000, 2000, maxStaggerS: 4.0));

        Assert.Equal(aggregate.Response.InitialRocofHzPerS, fleet.Response.InitialRocofHzPerS, 12); // inertia still additive
        Assert.True(fleet.FfrAtNadirMw < aggregate.FfrAtNadirMw,
            $"staggered fleet should have less FFR online at the nadir: {fleet.FfrAtNadirMw:F0} vs {aggregate.FfrAtNadirMw:F0} MW");
        Assert.True(fleet.Response.NadirHz < aggregate.Response.NadirHz,
            $"staggered fleet nadir should be deeper: {fleet.Response.NadirHz:F3} vs {aggregate.Response.NadirHz:F3} Hz");
    }

    [Fact]
    public void Stagger_is_relative_to_the_event_not_absolute_time()
    {
        // Regression for the t=0-anchoring bug: put the loss AFTER the whole stagger window. If
        // stagger were measured from absolute t=0, every unit would already be armed before the loss even
        // landed, so a staggered fleet would be indistinguishable from one aggregate device (gap = 0). With
        // stagger measured from each unit's own deadband crossing, the staggered fleet must still show a
        // strictly smaller FFR-at-nadir and a deeper nadir.
        var lateTrip = new FrequencyEvent[] { new(10.0, -1320) };   // loss at t=10 s, well past the 4 s stagger
        FleetResult aggregate = FleetDynamics.Simulate(BaseE, Demand, lateTrip, FleetSpec.Uniform(1, 10_000, 2000, maxStaggerS: 0));
        FleetResult fleet = FleetDynamics.Simulate(BaseE, Demand, lateTrip, FleetSpec.Uniform(100, 10_000, 2000, maxStaggerS: 4.0));

        Assert.True(fleet.FfrAtNadirMw < aggregate.FfrAtNadirMw,
            $"event-relative stagger must still bite when the loss is late: {fleet.FfrAtNadirMw:F0} vs {aggregate.FfrAtNadirMw:F0} MW");
        Assert.True(fleet.Response.NadirHz < aggregate.Response.NadirHz,
            $"staggered fleet nadir should be deeper: {fleet.Response.NadirHz:F3} vs {aggregate.Response.NadirHz:F3} Hz");
    }

    [Fact]
    public void One_unit_zero_stagger_equals_Simulate()
    {
        var cfg = new DynamicsConfig { DeviceRotationalInertiaMwS = 40_000, FastResponseMw = 1500 };
        FrequencyResponse baseline = FrequencyDynamics.Simulate(BaseE, Demand, Trip, cfg);

        // The fleet unit carries the device inertia/FFR; the cfg's device slots are zeroed inside FleetDynamics.
        FleetSpec one = FleetSpec.Uniform(1, 40_000, 1500, ffrTauS: cfg.FastResponseTimeConstantS,
            deadbandHz: cfg.FastResponseDeadbandHz, maxStaggerS: 0);
        FrequencyResponse fleet = FleetDynamics.Simulate(BaseE, Demand, Trip, one, cfg).Response;

        Assert.Equal(baseline.InitialRocofHzPerS, fleet.InitialRocofHzPerS, 12);
        Assert.Equal(baseline.NadirHz, fleet.NadirHz, 12);
        Assert.Equal(baseline.SettleHz, fleet.SettleHz, 12);
        Assert.Equal(baseline.Points.Count, fleet.Points.Count);
        for (int i = 0; i < baseline.Points.Count; i += 137) // spot-check the trajectory
            Assert.Equal(baseline.Points[i].Hz, fleet.Points[i].Hz, 12);
    }

    [Fact]
    public void A_sub_deadband_event_never_arms_the_fleet_ffr()
    {
        // Fleet units only arm once system frequency leaves THEIR deadband. A deficit small enough (against a huge
        // inertia) that |deltaf| stays inside the 0.015 Hz deadband must leave every unit dormant - zero FFR ever, at
        // peak and at the nadir. Pins the deadband gate the non-additivity tests always drive straight past.
        var tinyLoss = new FrequencyEvent[] { new(1.0, -5.0) };
        FleetResult r = FleetDynamics.Simulate(5_000_000, Demand, tinyLoss, FleetSpec.Uniform(100, 10_000, 2000, maxStaggerS: 4.0));
        Assert.Equal(0.0, r.PeakFleetFfrMw, 6);
        Assert.Equal(0.0, r.FfrAtNadirMw, 6);
    }

    [Fact]
    public void Integrator_sums_the_inertia_budget()
    {
        var fleet = FleetSpec.Uniform(10, 40_000, 1500);
        var integ = new MultiDeviceFrequencyIntegrator(BaseE, Demand, fleet);
        Assert.Equal(BaseE + 40_000, integ.SystemInertiaMwS, 6);

        // Drive it a few seconds under a deficit - frequency must fall and FFR must engage.
        for (int i = 0; i < 250; i++) integ.Step(0.02, -1320);
        Assert.True(integ.FrequencyHz < 50.0, "frequency should fall under a sustained deficit");
        Assert.True(integ.FleetFfrMw > 0, "fast response should engage");
    }
}
