using System.Collections.Generic;
using GridSim.Core.Assets;
using GridSim.Core.Diagnostics;
using GridSim.Core.Dynamics;
using GridSim.Core.Model;
using Xunit;

namespace GridSim.Tests;

/// <summary>
/// Regression tests for the live (stateful) asset path: the integrator must advance a real clock so the
/// controllers' detection/compute latency works (else the live twin is silently passive), and a fleet
/// Reset must restore state of charge.
/// </summary>
public class AssetLiveIntegratorTests
{
    [Fact]
    public void Live_integrator_advances_the_clock_so_compute_latency_engages()
    {
        // A pure-FFR grid-following unit with a 1 s compute latency: it must inject ~0 before the latency
        // elapses and a real response after. The old code passed clockS=0 every substep, freezing arming.
        AssetClass cls = AssetTestHelpers.GflClass("ffr",
            headroomUpFraction: 0.5, headroomDownFraction: 0.5, droopGain: 1.0, computeLatencyS: 1.0);
        AssetDynamicsModel bundle = AssetTestHelpers.Bundle(new[] { cls }, new AssetBinding("storage", "ffr"));
        GridModel m = AssetTestHelpers.Model(bundle, new Generator(1, Pg: 0, MBase: 400, H: 0, Type: "storage"));
        AssetDynamicsFleet fleet = AssetDynamicsFleet.Build(m, 50.0);

        var integ = new AssetAwareFrequencyIntegrator(m.SystemInertiaGvaS * 1000.0, AssetTestHelpers.BaseDemandMw, fleet);

        // Step 0.5 s of a 1000 MW deficit: frequency is falling, the unit is armed, but 1 s latency not elapsed.
        for (int i = 0; i < 5; i++) integ.Step(0.1, -1000.0);
        Assert.True(System.Math.Abs(integ.NetFleetMw) < 1e-6, $"before compute latency the fleet is idle, got {integ.NetFleetMw}");

        // Step past the latency: the fleet now responds.
        for (int i = 0; i < 15; i++) integ.Step(0.1, -1000.0);
        Assert.True(integ.NetFleetMw > 1.0, $"after compute latency the fleet must respond, got {integ.NetFleetMw}");
    }

    [Fact]
    public void Fleet_reset_restores_state_of_charge()
    {
        var p = new Dictionary<string, double>
        {
            [AssetParams.SynInertiaMwsPerHz] = 800.0,
            [AssetParams.HeadroomUpFraction] = 1.0, [AssetParams.HeadroomDownFraction] = 1.0,
            [AssetParams.CapacityMwh] = 2.0, [AssetParams.SocInit] = 0.55,
            [AssetParams.SocMin] = 0.05, [AssetParams.SocMax] = 0.95,
        };
        var prov = new Dictionary<string, AssetClassProvenance> { [AssetClass.DefaultKey] = new(ProvenanceTag.CuratedPlaceholder, "[VERIFY]") };
        var cls = new AssetClass("gfm", AssetKind.GridFormingInverter, "grid-forming", p, prov);
        AssetDynamicsModel bundle = AssetTestHelpers.Bundle(new[] { cls }, new AssetBinding("storage", "gfm"));
        GridModel m = AssetTestHelpers.Model(bundle, new Generator(1, Pg: 0, MBase: 500, H: 0, Type: "storage"));
        AssetDynamicsFleet fleet = AssetDynamicsFleet.Build(m, 50.0);
        fleet.Reset();

        // Discharge: frequency below nominal so the GFM injects (SoC falls).
        for (int i = 0; i < 50; i++) fleet.StepCoi(49.5, -0.5, 0.0, 0.02, i * 0.02);
        double drifted = fleet.Assets[0].SocFraction;
        Assert.True(drifted < 0.55, $"SoC should have fallen from 0.55, got {drifted}");

        fleet.Reset();
        Assert.Equal(0.55, fleet.Assets[0].SocFraction, 6);   // restored to SocInit
    }
}
