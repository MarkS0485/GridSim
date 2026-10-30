using System;
using System.Collections.Generic;
using System.Linq;
using GridSim.Core.Diagnostics;
using GridSim.Core.Dynamics;
using GridSim.Core.Events;
using GridSim.Core.Fleet;
using GridSim.Core.Replay;
using Xunit;

namespace GridSim.Tests;

/// <summary>
/// Recorded system response services (DC/DM/DR held at an event, volumes from the procurement record):
/// the general fast-response kernel, the swing-model integration, fleet parity, the Events -> Dynamics
/// mapping (including the completeness flag that zeroes the double-counting governor term), and the
/// gb-2023-12-22 catalogue entry that carries the first measured service set.
/// </summary>
public class ResponseServiceTests
{
    [Fact]
    public void Kernel_general_form_full_delivery_at_declared_deviation()
    {
        // DM-style: full 150 MW at 0.2 Hz deviation, not the device's 0.5 Hz.
        Assert.Equal(150.0, SwingKernel.FastResponseTarget(-0.2, 150, 0.015, 0.2), 9);
        Assert.Equal(75.0, SwingKernel.FastResponseTarget(-0.1, 150, 0.015, 0.2), 9);
        Assert.Equal(150.0, SwingKernel.FastResponseTarget(-0.7, 150, 0.015, 0.2), 9); // capped
        Assert.Equal(0.0, SwingKernel.FastResponseTarget(-0.01, 150, 0.015, 0.2)); // deadband
        Assert.Equal(-150.0, SwingKernel.FastResponseTarget(0.3, 150, 0.015, 0.2), 9); // symmetric high side
    }

    [Fact]
    public void Kernel_knee_form_matches_the_published_dc_curve()
    {
        // DC-L, 873 MW: deadband 0.015, 5% by 0.2 Hz deviation, 100% by 0.5 Hz.
        double Dc(double df) => SwingKernel.FastResponseTarget(df, 873, 0.015, 0.5, 0.2, 0.05);
        Assert.Equal(0.0, Dc(-0.01));                          // inside deadband
        Assert.Equal(873 * 0.05, Dc(-0.2), 6);                 // the knee: 5% at 0.2 Hz
        Assert.Equal(873 * (0.05 + 0.95 * 0.5), Dc(-0.35), 6); // mid steep segment: 52.5%
        Assert.Equal(873.0, Dc(-0.5), 6);                      // full at 0.5
        Assert.Equal(873.0, Dc(-0.7), 6);                      // capped beyond
        Assert.Equal(-Dc(-0.35), Dc(0.35), 9);                 // symmetric high side
        // Low-gain band is genuinely low-gain: far below the line-through-zero form.
        Assert.True(Math.Abs(Dc(-0.1)) < 873 * 0.05);

        // DM-L shape: knee at the deadband, fraction 0 = ramp from deadband to full.
        double Dm(double df) => SwingKernel.FastResponseTarget(df, 150, 0.1, 0.2, 0.1, 0.0);
        Assert.Equal(0.0, Dm(-0.1));
        Assert.Equal(75.0, Dm(-0.15), 6);                      // halfway up the ramp
        Assert.Equal(150.0, Dm(-0.2), 6);
        // Knee <= 0 falls back to the line-through-zero form exactly.
        foreach (double df in new[] { -0.6, -0.3, -0.05, 0.15 })
            Assert.Equal(
                SwingKernel.FastResponseTarget(df, 300, 0.015, 0.5),
                SwingKernel.FastResponseTarget(df, 300, 0.015, 0.5, 0.0, 0.0));
    }

    [Fact]
    public void Kernel_device_overload_unchanged_by_general_form()
    {
        // The 3-arg device form must remain the 0.5 Hz special case, bit-identical.
        foreach (double df in new[] { -0.6, -0.3, -0.05, 0.0, 0.2 })
            Assert.Equal(
                SwingKernel.FastResponseTarget(df, 1140, 0.015),
                SwingKernel.FastResponseTarget(df, 1140, 0.015, 0.5));
    }

    [Fact]
    public void Services_arrest_the_nadir()
    {
        var loss = new[] { new FrequencyEvent(0.5, -1000) };
        var bare = new DynamicsConfig { DurationSeconds = 40, ResponsiveFraction = 0.0 };
        var with = bare with
        {
            SystemResponseServices = new[] { new SystemResponseService(873, 0.015, 0.5, 0.3) },
        };
        FrequencyResponse no = FrequencyDynamics.Simulate(161_000, 22_400, loss, bare);
        FrequencyResponse yes = FrequencyDynamics.Simulate(161_000, 22_400, loss, with);
        Assert.True(yes.NadirHz > no.NadirHz + 0.2,
            $"service should lift the nadir materially: {no.NadirHz:F3} -> {yes.NadirHz:F3}");
        // Inertia is untouched by a power service: the first-instant RoCoF is set before delivery ramps.
        Assert.Equal(no.Points[26].RocofHzPerS, yes.Points[26].RocofHzPerS, 2); // t=0.52 s, just after the step
    }

    [Fact]
    public void Empty_service_list_is_the_identity()
    {
        var loss = new[] { new FrequencyEvent(0.5, -1000) };
        var cfg = new DynamicsConfig { DurationSeconds = 30 };
        FrequencyResponse a = FrequencyDynamics.Simulate(161_000, 22_400, loss, cfg);
        FrequencyResponse b = FrequencyDynamics.Simulate(161_000, 22_400, loss,
            cfg with { SystemResponseServices = Array.Empty<SystemResponseService>() });
        Assert.Equal(a.NadirHz, b.NadirHz);
        Assert.Equal(a.InitialRocofHzPerS, b.InitialRocofHzPerS);
    }

    [Fact]
    public void Fleet_integrates_services_identically_to_the_scalar_model()
    {
        // A zero-unit fleet with services must match FrequencyDynamics with the same services:
        // the fleet generalises the physics, it doesn't fork it.
        var loss = new[] { new FrequencyEvent(0.5, -1383) };
        var cfg = new DynamicsConfig
        {
            DurationSeconds = 40,
            ResponsiveFraction = 0.0,
            SystemResponseServices = new[]
            {
                new SystemResponseService(873, 0.015, 0.5, 0.3),
                new SystemResponseService(150, 0.1, 0.2, 0.3),
                new SystemResponseService(203, 0.015, 0.2, 4.0),
            },
        };
        FrequencyResponse scalar = FrequencyDynamics.Simulate(161_000, 22_400, loss, cfg);
        FleetResult fleet = FleetDynamics.Simulate(161_000, 22_400, loss,
            FleetSpec.Uniform(0, 0, 0, maxStaggerS: 0), cfg);
        Assert.Equal(scalar.NadirHz, fleet.Response.NadirHz, 12);
        Assert.Equal(scalar.SettleHz, fleet.Response.SettleHz, 12);
    }

    [Fact]
    public void Distributed_coi_with_services_equals_the_lumped_model()
    {
        // Services are keyed off the COI like every other aggregate response term, so the
        // multi-domain COI must still equal the lumped trajectory with services active (T1).
        var grid = new GridSim.Core.Model.GridModel
        {
            BaseMva = 100,
            Buses = new GridSim.Core.Model.Bus[]
            {
                new(1, GridSim.Core.Model.BusType.Slack), new(2, GridSim.Core.Model.BusType.Pq),
            },
            Generators = new GridSim.Core.Model.Generator[]
            {
                new(Bus: 1, Pg: 800, MBase: 1000, H: 5.0, Type: "ccgt"),
                new(Bus: 2, Pg: 400, MBase: 500, H: 3.0, Type: "hydro"),
            },
            Branches = Array.Empty<GridSim.Core.Model.Branch>(),
        };
        var loss = new[] { new FrequencyEvent(1.0, -300) };
        var cfg = new DynamicsConfig
        {
            SystemResponseServices = new[]
            {
                new SystemResponseService(200, 0.015, 0.5, 0.3, 0.2, 0.05),
                new SystemResponseService(50, 0.015, 0.2, 4.0, 0.015, 0.0),
            },
        };
        FrequencyResponse lumped = FrequencyDynamics.Simulate(grid.SystemInertiaGvaS * 1000.0, 1200, loss, cfg);
        FrequencyResponse coi = new GridSim.Core.Dynamics.Distributed.DistributedInertiaModel()
            .Simulate(grid, 1200, loss, cfg with { InertiaModel = InertiaModel.Distributed });
        Assert.Equal(lumped.Points.Count, coi.Points.Count);
        for (int k = 0; k < lumped.Points.Count; k++)
            Assert.True(Math.Abs(lumped.Points[k].Hz - coi.Points[k].Hz) < 1e-6,
                $"k={k}: lumped {lumped.Points[k].Hz:F9} vs coi {coi.Points[k].Hz:F9}");
        Assert.True(coi.NadirHz > FrequencyDynamics.Simulate(
            grid.SystemInertiaGvaS * 1000.0, 1200, loss).NadirHz, "services must actually bite");
    }

    [Fact]
    public void ApplyRecordedServices_maps_and_completeness_zeroes_the_governor()
    {
        GridEvent ev = Make(complete: true);
        DynamicsConfig cfg = ev.ApplyRecordedServices(new DynamicsConfig());
        Assert.Equal(2, cfg.SystemResponseServices.Count);
        Assert.Equal(873, cfg.SystemResponseServices[0].Mw);
        Assert.Equal(0.2, cfg.SystemResponseServices[1].FullDeliveryDeviationHz);
        Assert.Equal(0.0, cfg.ResponsiveFraction); // complete list => no generic governor on top

        GridEvent partial = Make(complete: false);
        Assert.Equal(new DynamicsConfig().ResponsiveFraction,
            partial.ApplyRecordedServices(new DynamicsConfig()).ResponsiveFraction);

        GridEvent none = Make(complete: false) with { ResponseServices = null };
        Assert.Empty(none.ApplyRecordedServices(new DynamicsConfig()).SystemResponseServices);
    }

    [Fact]
    public void Catalogue_dec23_carries_the_measured_service_set()
    {
        GridEvent ev = EventCatalog.Builtin().Single(e => e.Id == "gb-2023-12-22");
        Assert.True(ev.ResponseServicesComplete);
        Assert.Equal(1.0, ev.Outcome.RocofMeasurementWindowS);
        List<ResponseService> svcs = (ev.ResponseServices ?? []).ToList();
        Assert.Equal(3, svcs.Count);
        Assert.Equal(873 + 150 + 203, svcs.Sum(s => s.Mw)); // EAC auction 70 cleared volumes
        Assert.All(svcs, s => Assert.Equal(ProvenanceTag.Recorded, s.Provenance));
    }

    [Fact]
    public void Replay_reports_the_corner_envelope_and_verdict_stays_nominal()
    {
        GridEvent ev = Make(complete: true);
        ReplayResult res = ReplayValidator.Validate(ev);
        // Best corner (more damping) arrests higher than nominal; worst arrests lower.
        Assert.True(res.NadirBestCornerHz >= res.SimulatedNadirHz,
            $"best {res.NadirBestCornerHz:F3} should be >= nominal {res.SimulatedNadirHz:F3}");
        Assert.True(res.NadirWorstCornerHz <= res.SimulatedNadirHz,
            $"worst {res.NadirWorstCornerHz:F3} should be <= nominal {res.SimulatedNadirHz:F3}");
        // The envelope is reporting only: a nominal validate with the same tolerances must give the
        // same verdict whether or not the record sits inside the bracket.
        Assert.Equal(ReplayValidator.Validate(ev).Verdict, res.Verdict);
    }

    [Fact]
    public void Replay_scores_a_windowed_record_against_the_sampled_grid_not_the_sliding_max()
    {
        // Same event twice: an instantaneous record vs a 1 s-window record. The windowed score must be
        // gentler than the instantaneous one (a sampled logger can never catch the un-braked first
        // instant), so the windowed variant's simulated RoCoF magnitude must be smaller.
        GridEvent instant = Make(complete: true);
        GridEvent windowed = instant with { Outcome = instant.Outcome with { RocofMeasurementWindowS = 1.0 } };
        ReplayResult ri = ReplayValidator.Validate(instant);
        ReplayResult rw = ReplayValidator.Validate(windowed);
        Assert.True(Math.Abs(rw.SimulatedRocofHzPerS) < Math.Abs(ri.SimulatedRocofHzPerS),
            $"windowed {rw.SimulatedRocofHzPerS:F3} should be gentler than instantaneous {ri.SimulatedRocofHzPerS:F3}");
    }

    private static GridEvent Make(bool complete) => new(
        Id: "test-svc",
        Title: "service test event",
        WhenUtc: new DateTime(2023, 12, 22, 13, 9, 51, DateTimeKind.Utc),
        PreEventInertiaMwS: 161_000,
        PreEventDemandMw: 22_400,
        Losses: new List<PowerStep> { new(0.5, -1000, "loss", ProvenanceTag.Recorded) },
        RecordedActions: new List<RecordedAction>(),
        Outcome: new RecordedOutcome(-0.155, 49.3, 50.0, 0, null, ProvenanceTag.Recorded),
        Provenance: ProvenanceTag.Recorded,
        Notes: null,
        ResponseServices: new List<ResponseService>
        {
            new(873, 0.015, 0.5, 0.3, "DCL", ProvenanceTag.Recorded),
            new(150, 0.1, 0.2, 0.3, "DML", ProvenanceTag.Recorded),
        },
        ResponseServicesComplete: complete);
}
