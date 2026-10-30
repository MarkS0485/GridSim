using System.Text.Json;
using GridSim.Core.Dynamics.SmallSignal;
using GridSim.Core.IO;
using GridSim.Core.Model;
using GridSim.Core.PowerFlow;
using Xunit;

namespace GridSim.Tests;

/// <summary>
/// Locks the <c>gridsim-smallsignal/1</c> wire contract so the sidecar (<c>crossval_smallsignal.py</c>)
/// can never silently break: the schema tag, the load-bearing <c>aMatrix</c> (the eig input the sidecar
/// recomputes), the kind-tagged states (which let a downstream check assert the real/synthetic separation
/// survives serialisation), and the eigenvalues / participation / RHP fields. Needs no Python.
/// </summary>
public class SmallSignalExportContractTests
{
    private static JsonElement Export()
    {
        GridModel model = IeeeCases.Case9();
        PowerFlowResult pf = new NewtonRaphsonPowerFlow().Solve(model);
        var xdp = new[] { 0.0608, 0.1198, 0.1813 };
        SmallSignalModel ssm = SmallSignalAssembler.FromSolvedCase(model, pf, xdp);
        ModalResult modal = ModalAnalysis.Analyze(ssm);
        string json = SmallSignalExport.ToJson("WSCC-9", ssm, modal);
        return JsonDocument.Parse(json).RootElement.Clone();
    }

    [Fact]
    public void The_schema_tag_is_what_the_sidecar_expects()
    {
        Assert.StartsWith("gridsim-smallsignal/", Export().GetProperty("schema").GetString());
    }

    [Fact]
    public void The_aMatrix_is_a_square_real_matrix_of_the_state_count()
    {
        JsonElement doc = Export();
        JsonElement a = doc.GetProperty("aMatrix");
        int n = doc.GetProperty("states").GetArrayLength();
        Assert.Equal(n, a.GetArrayLength());
        foreach (JsonElement row in a.EnumerateArray()) Assert.Equal(n, row.GetArrayLength());
    }

    [Fact]
    public void Every_state_carries_a_kind_tag_for_the_real_synthetic_separation()
    {
        foreach (JsonElement s in Export().GetProperty("states").EnumerateArray())
            foreach (string f in new[] { "index", "kind", "ownerId", "name", "synthetic" })
                Assert.True(s.TryGetProperty(f, out _), $"state.{f} missing");
    }

    [Fact]
    public void The_modal_result_fields_the_sidecar_diffs_against_are_present()
    {
        JsonElement doc = Export();
        foreach (string f in new[] { "eigenvalues", "dampingRatios", "frequenciesHz", "participation",
                                     "rhpEigenvalueCount", "minDampingRatio", "kext", "angleNodeBuses", "f0" })
            Assert.True(doc.TryGetProperty(f, out _), $"top-level {f} missing - would break the sidecar");

        JsonElement eig0 = doc.GetProperty("eigenvalues")[0];
        Assert.True(eig0.TryGetProperty("re", out _));
        Assert.True(eig0.TryGetProperty("im", out _));
    }

    [Fact]
    public void Participation_rows_align_to_modes_and_states()
    {
        JsonElement doc = Export();
        int modes = doc.GetProperty("eigenvalues").GetArrayLength();
        int n = doc.GetProperty("states").GetArrayLength();
        JsonElement part = doc.GetProperty("participation");
        Assert.Equal(modes, part.GetArrayLength());
        foreach (JsonElement row in part.EnumerateArray()) Assert.Equal(n, row.GetArrayLength());
    }
}
