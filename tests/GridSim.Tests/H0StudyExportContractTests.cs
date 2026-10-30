using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using GridSim.Core.Assets;
using GridSim.Core.Diagnostics;
using GridSim.Core.Dynamics;
using GridSim.Core.Events;
using GridSim.Core.IO;
using GridSim.Core.Model;
using GridSim.Core.Replay;
using GridSim.Core.Scenarios;
using Xunit;

namespace GridSim.Tests;

/// <summary>
/// The H=0 study export schema contract (schema <c>gridsim-h0-study/1</c>): the fields are locked, the
/// real-vs-synthetic separation is on the wire (<c>deviceRotationalInertiaMwS==0</c> on every cell, and
/// <c>systemInertiaGvaS</c> excludes the virtual former <c>virtualInertiaMwS</c>), and the un-formable
/// sentinel is JSON <c>null</c> (not a huge finite E-floor number).
/// </summary>
public class H0StudyExportContractTests
{
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

    private static GridModel Model()
    {
        var gens = new List<Generator>
        {
            new(1, Pg: 20_000, MBase: 10_000, H: 4.0, Name: "sync", Type: "ccgt"),   // E = 40 000 MW.s
            new(1, Pg: 2_000, MBase: 3_000, H: 0, Name: "w1", Type: "wind"),
            new(1, Pg: 800, MBase: 1_500, H: 0, Name: "s1", Type: "storage"),
        };
        AssetClass gfl = AssetTestHelpers.GflClass("gfl-test", rocofTripHzPerS: 1.0);
        AssetClass gfm = AssetTestHelpers.GfmClass("gfm-test", synInertiaMwsPerHz: 1600.0, dampingMwPerHz: 200.0);
        AssetDynamicsModel bundle = AssetTestHelpers.Bundle(
            new[] { gfl, gfm },
            new AssetBinding("wind*", "gfl-test"), new AssetBinding("storage", "gfm-test"));
        return new GridModel
        {
            BaseMva = 100,
            Buses = new List<Bus> { new(1, BusType.Slack, BaseKv: 400, Name: "GB") },
            Generators = gens,
            Branches = Array.Empty<Branch>(),
            AssetDynamics = bundle,
        };
    }

    private static H0StudySpec Spec() => new(
        SelfConsistent(), Model(),
        new[] { 1.0, 0.5, 0.0 }, new[] { 0.0, 1.0 }, new[] { Corner.Best },
        "gfm-test", "gfl-test",
        new DynamicsConfig { EnableAssetDynamics = true }, ReplayTolerance.Default);

    internal static JsonElement Export()   // internal (was private) so HHygieneTests.R178 can read the same export
    {
        H0StudyResult r = H0StudyEngine.Run(Spec());
        var meta = new H0StudyMeta("syn", "deadbeef", "2026-07-20T00:00:00Z", "test-host", "gfm-test", "gfl-test");
        return JsonDocument.Parse(H0StudyExport.ToJson(r, meta)).RootElement.Clone();
    }

    [Fact]
    public void Schema_and_top_level_blocks_are_locked()
    {
        JsonElement root = Export();
        Assert.Equal("gridsim-h0-study/1", root.GetProperty("schema").GetString());
        Assert.Equal("Reproduced", root.GetProperty("baseline").GetProperty("verdict").GetString());
        Assert.True(root.TryGetProperty("inertiaTable", out _));
        Assert.True(root.TryGetProperty("axes", out _));
        Assert.True(root.TryGetProperty("swing", out _));           // the sidecar re-integration inputs
        Assert.True(root.GetProperty("cells").GetArrayLength() > 0);
    }

    [Fact]
    public void Every_cell_carries_the_locked_fields()
    {
        foreach (JsonElement c in Export().GetProperty("cells").EnumerateArray())
        {
            foreach (string f in new[]
            {
                "syncInertiaScale", "gfmFraction", "corner", "systemInertiaGvaS", "deviceRotationalInertiaMwS",
                "virtualInertiaMwS", "formingDampingMwPerHz", "effectiveFormingInertiaMwS",
                "bareInertiaRocofHzPerS", "rocofHzPerS", "nadirHz", "formingDependent", "formedBy",
                "lfddAtRisk", "cascadeLossOfMains",
            })
                Assert.True(c.TryGetProperty(f, out _), $"cell missing '{f}'");

            JsonElement fleet = c.GetProperty("fleet");
            Assert.True(fleet.TryGetProperty("trippedCount", out _));
            Assert.True(fleet.TryGetProperty("totalTrippedMw", out _));
        }
    }

    [Fact]
    public void Device_rotational_inertia_is_zero_on_every_cell()
    {
        // The invariant on the wire: this study carries no device; the (1/2)Jw^2 device column is never leaked into.
        foreach (JsonElement c in Export().GetProperty("cells").EnumerateArray())
            Assert.Equal(0.0, c.GetProperty("deviceRotationalInertiaMwS").GetDouble());
    }

    [Fact]
    public void Real_system_inertia_excludes_the_virtual_former()
    {
        // At scale==0 the REAL inertia is exactly 0 while the all-GFM cell carries a large virtual Ev - proof
        // that systemInertiaGvaS never absorbs virtualInertiaMwS (Ev lives only in effectiveFormingInertiaMwS).
        JsonElement cell = Export().GetProperty("cells").EnumerateArray()
            .First(c => c.GetProperty("syncInertiaScale").GetDouble() == 0.0
                     && c.GetProperty("gfmFraction").GetDouble() == 1.0);
        Assert.Equal(0.0, cell.GetProperty("systemInertiaGvaS").GetDouble());
        Assert.True(cell.GetProperty("virtualInertiaMwS").GetDouble() > 0.0);
        Assert.Equal(cell.GetProperty("virtualInertiaMwS").GetDouble(),
                     cell.GetProperty("effectiveFormingInertiaMwS").GetDouble(), 6);
    }

    [Fact]
    public void Bare_inertia_rocof_is_a_null_sentinel_at_zero_real_inertia()
    {
        foreach (JsonElement c in Export().GetProperty("cells").EnumerateArray())
            if (c.GetProperty("syncInertiaScale").GetDouble() == 0.0)
                Assert.Equal(JsonValueKind.Null, c.GetProperty("bareInertiaRocofHzPerS").ValueKind);
    }

    [Fact]
    public void Un_formable_cell_sentinels_rocof_and_nadir_as_null()
    {
        // scale==0, gfm==0: no real inertia AND no virtual former -> forming-dependent, RoCoF/nadir undefined.
        JsonElement cell = Export().GetProperty("cells").EnumerateArray()
            .First(c => c.GetProperty("syncInertiaScale").GetDouble() == 0.0
                     && c.GetProperty("gfmFraction").GetDouble() == 0.0);
        Assert.True(cell.GetProperty("formingDependent").GetBoolean());
        Assert.Equal(JsonValueKind.Null, cell.GetProperty("rocofHzPerS").ValueKind);
        Assert.Equal(JsonValueKind.Null, cell.GetProperty("nadirHz").ValueKind);
        Assert.True(cell.GetProperty("cascadeLossOfMains").GetBoolean());
    }
}
