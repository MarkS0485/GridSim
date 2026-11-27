using GridSim.Core.Dynamics;
using GridSim.Core.Frequency;
using Xunit;

namespace GridSim.Tests;

/// <summary>Post-RC3 batch L: DP-26 (AssessTrace reports the samples it cannot score).</summary>
public class FixKlCoreTests
{
    private static readonly DateTime T0 = new(2020, 1, 1, 0, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void Dp26_nan_samples_are_counted_and_a_finite_trace_reports_none()
    {
        var finite = new[] { new FrequencySample(T0, 50.0), new FrequencySample(T0.AddSeconds(1), 49.9), new FrequencySample(T0.AddSeconds(2), 49.95) };
        Assert.Equal(0, FrequencyLimits.AssessTrace(finite).SamplesNonFinite);

        var gappy = new[]
        {
            new FrequencySample(T0, 50.0), new FrequencySample(T0.AddSeconds(1), double.NaN),
            new FrequencySample(T0.AddSeconds(2), 49.95), new FrequencySample(T0.AddSeconds(3), double.NaN),
        };
        FrequencyTraceReport r = FrequencyLimits.AssessTrace(gappy);
        Assert.Equal(2, r.SamplesNonFinite);
        Assert.Equal(4, r.Samples);
        Assert.Equal(49.95, r.MinHz);   // the statistics over the finite samples are what they were
    }
}

public class FixKlDynamicsPostEventTests
{
    private static GridSim.Core.Analysis.DynamicsStudy.DynamicsPostEventTopology Post(int islands, string status) =>
        new(new[] { "4-5 (index 6)" }, islands, status, null, "note");

    [Fact]
    public void Cli27_a_post_event_power_flow_that_did_not_converge_is_flagged_but_islanding_and_convergence_are_not()
    {
        Assert.True(GridSim.Core.Analysis.DynamicsStudy.DynamicsStudyRunner.PostEventNotConverged(Post(1, "MaxIterations")));
        Assert.True(GridSim.Core.Analysis.DynamicsStudy.DynamicsStudyRunner.PostEventNotConverged(Post(1, "NonFinite")));
        Assert.False(GridSim.Core.Analysis.DynamicsStudy.DynamicsStudyRunner.PostEventNotConverged(Post(1, "Converged")));
        Assert.False(GridSim.Core.Analysis.DynamicsStudy.DynamicsStudyRunner.PostEventNotConverged(Post(0, "Converged (intact network: the initial solve)")));
        Assert.False(GridSim.Core.Analysis.DynamicsStudy.DynamicsStudyRunner.PostEventNotConverged(Post(2, "NOT SOLVED (islanded)")));
    }
}

public class FixKlSolveImportTests
{
    private static string Case9Export(Action<System.Text.Json.Nodes.JsonObject> edit)
    {
        string path = TestTemp.Under("fixkl-io03-" + Guid.NewGuid().ToString("N") + ".json");
        try
        {
            var m = GridSim.Core.IO.IeeeCases.Case9();
            var trace = new GridSim.Core.Diagnostics.SolveTrace();
            var r = new GridSim.Core.PowerFlow.NewtonRaphsonPowerFlow().Solve(m, new GridSim.Core.PowerFlow.PowerFlowOptions { Trace = trace });
            Assert.True(r.Converged);
            GridSim.Core.IO.SolveExport.WriteJson(path, m, r, trace, new GridSim.Core.IO.SolveExportMeta("c9", "cpu", "c", "2026-10-05T00:00:00Z", 1.0, "t"));
            var node = System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(path))!.AsObject();
            edit(node);
            return node.ToJsonString();
        }
        finally { try { File.Delete(path); } catch (IOException) { } }
    }

    [Fact]
    // IO-03 empty-list half: the ruled refusal of a converged document with no bus rows was withdrawn because existing
    // tests (Doc8 D067, BugHunt1029 BH40, SolveImportTests GR180) pin an empty list as readable, converged or not, and
    // there is no proof that reading it is wrong. This pins the behaviour kept.
    public void Io03_an_empty_result_bus_list_is_read_whether_or_not_the_document_is_converged()
    {
        string converged = Case9Export(n => n["result"]!["buses"] = new System.Text.Json.Nodes.JsonArray());
        GridSim.Core.IO.SolveImport.Read(converged);

        string notConverged = Case9Export(n =>
        {
            n["result"]!["buses"] = new System.Text.Json.Nodes.JsonArray();
            n["solve"]!["converged"] = false;
            n["solve"]!.AsObject().Remove("status");
        });
        Assert.False(GridSim.Core.IO.SolveImport.Read(notConverged).Result.Converged);
    }
}
