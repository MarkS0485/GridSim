using System.Text.Json;
using GridSim.Core.Diagnostics;
using GridSim.Core.IO;
using GridSim.Core.PowerFlow;
using Xunit;

namespace GridSim.Tests;

/// <summary>
/// The cross-validation contract: <c>tools/crossval/crossval.py</c> rebuilds the network and re-solves it in
/// pandapower using ONLY the fields GridSim's solve-export records. These tests lock that field contract so the
/// sidecar can never silently break - they need no Python, so they run in CI. The live pandapower diff is
/// env-gated (needs pandapower installed); this guarantees the data it consumes is always present and shaped.
/// </summary>
public class CrossvalContractTests
{
    private static JsonElement Export()
    {
        var trace = new SolveTrace();
        var r = new NewtonRaphsonPowerFlow().Solve(IeeeCases.Case9(), new PowerFlowOptions { Trace = trace });
        var meta = new SolveExportMeta("case9", "cpu", "deadbeef", "2026-07-02T00:00:00Z", 1.0, "test");
        string json = SolveExport.ToJson(IeeeCases.Case9(), r, trace, meta);
        return JsonDocument.Parse(json).RootElement.Clone();
    }

    [Fact]
    public void The_schema_tag_is_what_the_sidecar_expects()
    {
        Assert.StartsWith("gridsim-solve-export/", Export().GetProperty("schema").GetString());
    }

    [Fact]
    public void The_model_inputs_the_sidecar_reads_are_all_present()
    {
        JsonElement model = Export().GetProperty("model");
        Assert.True(model.TryGetProperty("baseMva", out _));

        JsonElement bus = model.GetProperty("buses")[0];
        foreach (string f in new[] { "id", "type", "pd", "qd", "gs", "bs", "vm", "baseKv" })
            Assert.True(bus.TryGetProperty(f, out _), $"bus.{f} missing - would break crossval.py build_ppc");

        JsonElement gen = model.GetProperty("generators")[0];
        foreach (string f in new[] { "bus", "pg", "qg", "qmin", "qmax", "vg", "mBase", "pmax" })
            Assert.True(gen.TryGetProperty(f, out _), $"generator.{f} missing");

        JsonElement br = model.GetProperty("branches")[0];
        foreach (string f in new[] { "from", "to", "r", "x", "b", "rateAMva", "tap", "phaseShiftDeg" })
            Assert.True(br.TryGetProperty(f, out _), $"branch.{f} missing");
    }

    [Fact]
    public void The_result_fields_the_sidecar_diffs_against_are_present()
    {
        JsonElement bus = Export().GetProperty("result").GetProperty("buses")[0];
        foreach (string f in new[] { "id", "vm", "vaDeg" })
            Assert.True(bus.TryGetProperty(f, out _), $"result bus.{f} missing - the diff target");
    }

    [Fact]
    public void The_bus_type_strings_map_to_matpower_codes()
    {
        // crossval.py build_ppc maps these (case-insensitively); a rename in the model would silently
        // mis-type buses in pandapower. Lock the exact serialised spelling here.
        var types = new System.Collections.Generic.HashSet<string>();
        foreach (JsonElement b in Export().GetProperty("model").GetProperty("buses").EnumerateArray())
            types.Add(b.GetProperty("type").GetString()!.ToLowerInvariant());
        Assert.Subset(new System.Collections.Generic.HashSet<string> { "pq", "pv", "slack" }, types);
    }
}
