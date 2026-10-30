using System.Text.Json;
using GridSim.Core.Diagnostics;
using GridSim.Core.IO;
using GridSim.Core.PowerFlow;
using Xunit;

namespace GridSim.Tests;

/// <summary>
/// Locks the additive <c>physics</c> block in the solve export (schema stays <c>/1</c>). This block is
/// what the independent instrument <c>tools/crossval/physics_audit.py</c> reads to cross-check the C#
/// audit, so its shape must not silently drift - the same guard <see cref="CrossvalContractTests"/>
/// gives the rest of the export. Python-free, runs in CI.
/// </summary>
public class PhysicsExportContractTests
{
    private static JsonElement Export()
    {
        var trace = new SolveTrace();
        PowerFlowResult r = new NewtonRaphsonPowerFlow().Solve(GridSim.Core.IO.IeeeCases.Case14(), new PowerFlowOptions { Trace = trace });
        var meta = new SolveExportMeta("case14", "cpu", "deadbeef", "2026-07-17T00:00:00Z", 1.0, "test");
        string json = SolveExport.ToJson(GridSim.Core.IO.IeeeCases.Case14(), r, trace, meta);
        return JsonDocument.Parse(json).RootElement.Clone();
    }

    [Fact]
    public void Physics_block_is_present_with_the_gate_verdict()
    {
        JsonElement physics = Export().GetProperty("physics");
        Assert.True(physics.GetProperty("gatePass").GetBoolean());          // case14 is a clean solution
        int[] hard = physics.GetProperty("hardGateLaws").EnumerateArray().Select(e => e.GetInt32()).ToArray();
        Assert.Equal(new[] { 1, 2, 3, 6, 8 }, hard);
    }

    [Fact]
    public void Every_law_carries_the_full_field_set_in_order()
    {
        JsonElement laws = Export().GetProperty("physics").GetProperty("laws");
        Assert.Equal(10, laws.GetArrayLength());
        for (int i = 0; i < 10; i++)
        {
            JsonElement law = laws[i];
            Assert.Equal(i + 1, law.GetProperty("number").GetInt32());
            foreach (string f in new[] { "name", "statement", "status", "residual", "unit", "tolerance", "detail" })
                Assert.True(law.TryGetProperty(f, out _), $"physics.laws[{i}].{f} missing - would break physics_audit.py");
        }
        // The hard laws hold on a clean solve.
        Assert.Equal("Holds", laws[0].GetProperty("status").GetString());   // energy conservation
        Assert.Equal("Constitutive", laws[3].GetProperty("status").GetString()); // Faraday
    }
}
