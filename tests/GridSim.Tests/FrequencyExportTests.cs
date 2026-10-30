using System;
using System.Text.Json;
using GridSim.Core.Diagnostics;
using GridSim.Core.Dynamics;
using GridSim.Core.IO;
using Xunit;

namespace GridSim.Tests;

/// <summary>
/// The swing-solve export: it must carry the schema tag, keep the inertia budget split (system + device (1/2)J*omega^2 ->
/// E) with the fast response as a SEPARATE column, emit the full trajectory, and be deterministic.
/// </summary>
public class FrequencyExportTests
{
    private static (FrequencyResponse resp, DynamicsConfig cfg, FrequencyEvent[] ev) Run()
    {
        var cfg = new DynamicsConfig { DeviceRotationalInertiaMwS = 40_000, FastResponseMw = 2000 };
        var ev = new[] { new FrequencyEvent(1.0, -1320) };
        FrequencyResponse resp = FrequencyDynamics.Simulate(120_000, 30_000, ev, cfg);
        return (resp, cfg, ev);
    }

    private static FrequencyExportMeta Meta() =>
        new("test-event", "deadbeef", "2026-07-01T00:00:00Z", 1.5, "test-host", ProvenanceTag.Synthetic);

    [Fact]
    public void Export_carries_schema_and_split_inertia_budget()
    {
        var (resp, cfg, ev) = Run();
        string json = FrequencyExport.ToJson(120_000, 30_000, ev, cfg, resp, Meta());
        using var doc = JsonDocument.Parse(json);
        JsonElement root = doc.RootElement;

        Assert.Equal("gridsim-frequency-export/1", root.GetProperty("schema").GetString());

        JsonElement inertia = root.GetProperty("inertia");
        Assert.Equal(120_000, inertia.GetProperty("systemInertiaMwS").GetDouble());
        Assert.Equal(40_000, inertia.GetProperty("deviceRotationalInertiaMwS").GetDouble());
        // E = system + device rotational, and the FFR must NOT be folded into it.
        Assert.Equal(160_000, inertia.GetProperty("effectiveInertiaMwS").GetDouble());

        JsonElement ffr = root.GetProperty("fastResponse");
        Assert.Equal(2000, ffr.GetProperty("fastResponseMw").GetDouble());

        // Provenance survives the round-trip.
        Assert.Equal("Synthetic", root.GetProperty("meta").GetProperty("provenance").GetString());
    }

    [Fact]
    public void Export_includes_the_full_trajectory_and_headline()
    {
        var (resp, cfg, ev) = Run();
        string json = FrequencyExport.ToJson(120_000, 30_000, ev, cfg, resp, Meta());
        using var doc = JsonDocument.Parse(json);
        JsonElement root = doc.RootElement;

        int points = root.GetProperty("points").GetArrayLength();
        Assert.Equal(resp.Points.Count, points);

        Assert.Equal(resp.NadirHz, root.GetProperty("headline").GetProperty("nadirHz").GetDouble(), 9);
    }

    [Fact]
    public void Export_is_deterministic()
    {
        var (resp, cfg, ev) = Run();
        string a = FrequencyExport.ToJson(120_000, 30_000, ev, cfg, resp, Meta());
        string b = FrequencyExport.ToJson(120_000, 30_000, ev, cfg, resp, Meta());
        Assert.Equal(a, b);
    }
}
