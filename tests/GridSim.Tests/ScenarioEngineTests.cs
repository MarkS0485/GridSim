using System;
using System.Collections.Generic;
using System.Linq;
using GridSim.Core.Diagnostics;
using GridSim.Core.Dynamics;
using GridSim.Core.Events;
using GridSim.Core.Replay;
using GridSim.Core.Scenarios;
using Xunit;

namespace GridSim.Tests;

/// <summary>
/// The counterfactual scenario engine: it refuses to run unless the baseline reproduces the record; the inertia
/// risk curve worsens monotonically as inertia falls; and the device lifts the nadir at every inertia level.
/// </summary>
public class ScenarioEngineTests
{
    // A self-consistent event (recorded outcome = what the physics produces) so the gate opens.
    private static GridEvent SelfConsistent(double inertiaMwS = 155_000, double demandMw = 32_000, double lossMw = 1320)
    {
        var cfg = new DynamicsConfig();
        var ev = new[] { new FrequencyEvent(1.0, -lossMw) };
        FrequencyResponse truth = FrequencyDynamics.Simulate(inertiaMwS, demandMw, ev, cfg);
        return new GridEvent(
            "syn", "self-consistent", new DateTime(2020, 1, 1, 0, 0, 0, DateTimeKind.Utc),
            inertiaMwS, demandMw,
            new List<PowerStep> { new(1.0, -lossMw, "loss", ProvenanceTag.Synthetic) },
            new List<RecordedAction>(),
            new RecordedOutcome(truth.InitialRocofHzPerS, truth.NadirHz, truth.SettleHz, 0, null, ProvenanceTag.Synthetic),
            ProvenanceTag.Synthetic);
    }

    private static DeviceConfig Tsgb() => new("tsgb", RotationalInertiaMwS: 40_000, FastResponseMw: 1500);

    [Fact] // Wave 2 regression: sweep cells/risk points must cover the full loss window, not 30 s
    public void Risk_curve_no_device_uses_the_full_loss_window()
    {
        GridEvent ev = EventCatalog.Get("gb-2019-08-09", utcNow: new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc));
        ScenarioSpec spec = ScenarioSpec.Default(ev, Tsgb());
        ScenarioMatrix m = ScenarioEngine.Run(spec);

        // Reconstruct the no-device, nominal-corner, recorded-services sim at the first axis inertia
        // with the FULL window, exactly as a correct engine must. gb-2019 stages losses out to 90 s,
        // so a 30 s-truncated engine would report a materially shallower nadir than this reference.
        var losses = ev.ExogenousLossEvents();
        double lastLossAt = losses.Max(l => l.AtSeconds);
        double inertiaGva = spec.Axes.InertiaGvaS[0];
        DynamicsConfig cfg = ev.ApplyRecordedServices(spec.BaseConfig);
        cfg = cfg with { DurationSeconds = Math.Max(cfg.DurationSeconds, lastLossAt + 30.0) };
        cfg = ScenarioCorners.Apply(DeviceConfig.None.Apply(cfg), Corner.Nominal);
        FrequencyResponse full = FrequencyDynamics.Simulate(inertiaGva * 1000.0, ev.PreEventDemandMw, losses, cfg);

        Assert.True(lastLossAt > 30.0, "test event must stage losses past the 30 s default to be meaningful");
        Assert.Equal(full.NadirHz, m.RiskCurve.Points[0].NadirNoDevice, 3);
    }

    [Fact]
    public void The_gate_throws_when_the_baseline_is_not_reproduced()
    {
        GridEvent good = SelfConsistent();
        GridEvent bad = good with { Outcome = good.Outcome with { NadirHz = good.Outcome.NadirHz + 3.0 } };
        Assert.Throws<BaselineNotReproducedException>(() => ScenarioEngine.Run(ScenarioSpec.Default(bad, Tsgb())));
    }

    [Fact]
    public void The_risk_curve_worsens_monotonically_as_inertia_falls()
    {
        ScenarioMatrix m = ScenarioEngine.Run(ScenarioSpec.Default(SelfConsistent(), Tsgb()));
        // Default axis is descending inertia 260..50: |RoCoF| must rise and nadir must fall at each step.
        var pts = m.RiskCurve.Points;
        Assert.True(pts.Count >= 2);
        for (int i = 1; i < pts.Count; i++)
        {
            Assert.True(pts[i].InertiaGvaS < pts[i - 1].InertiaGvaS, "axis should descend");
            Assert.True(Math.Abs(pts[i].RocofNoDevice) > Math.Abs(pts[i - 1].RocofNoDevice),
                $"|RoCoF| should rise as inertia falls ({pts[i - 1].RocofNoDevice} -> {pts[i].RocofNoDevice})");
            Assert.True(pts[i].NadirNoDevice < pts[i - 1].NadirNoDevice + 1e-9,
                $"nadir should deepen as inertia falls ({pts[i - 1].NadirNoDevice} -> {pts[i].NadirNoDevice})");
        }
    }

    [Fact]
    public void The_device_lifts_the_nadir_and_cuts_rocof_at_every_level()
    {
        ScenarioMatrix m = ScenarioEngine.Run(ScenarioSpec.Default(SelfConsistent(), Tsgb()));
        foreach (RiskPoint p in m.RiskCurve.Points)
        {
            Assert.True(p.NadirWithDevice > p.NadirNoDevice, $"device should lift nadir at {p.InertiaGvaS} GVA.s");
            Assert.True(Math.Abs(p.RocofWithDevice) < Math.Abs(p.RocofNoDevice), $"device should cut RoCoF at {p.InertiaGvaS} GVA.s");
        }
    }

    [Fact]
    public void The_matrix_has_a_cell_per_inertia_times_device_times_corner()
    {
        var spec = ScenarioSpec.Default(SelfConsistent(), Tsgb());
        ScenarioMatrix m = ScenarioEngine.Run(spec);
        int expected = spec.Axes.InertiaGvaS.Count * spec.Axes.Devices.Count * spec.Axes.Corners.Count;
        Assert.Equal(expected, m.Cells.Count);
    }

    [Fact]
    public void Corners_bracket_the_nadir_but_not_the_initial_rocof()
    {
        var spec = ScenarioSpec.Default(SelfConsistent(), Tsgb());
        ScenarioMatrix m = ScenarioEngine.Run(spec);
        // At a fixed inertia+device, best/worst corners must give the SAME RoCoF (set by E at t=0) but different nadirs.
        var byKey = m.Cells.Where(c => Math.Abs(c.InertiaGvaS - 155) < 1e-9 && c.Device == "tsgb").ToList();
        Assert.Equal(2, byKey.Count);
        Assert.Equal(byKey[0].RocofHzPerS, byKey[1].RocofHzPerS, 9);
        Assert.NotEqual(byKey[0].NadirHz, byKey[1].NadirHz);
    }
}
