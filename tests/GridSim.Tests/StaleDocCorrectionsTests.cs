using GridSim.Core.Events;
using Xunit;

namespace GridSim.Tests;

/// <summary>
/// R095 and R259 (October 2026 review): XML docs that described work as missing or "a later slice" after it had
/// shipped, and a class documented as wired into an estimator that does not use it. Source-level checks so the stale
/// claims do not return; the event count is checked against the catalogue itself.
/// </summary>
public class StaleDocCorrectionsTests
{
    private static string Core(params string[] parts)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "GridSim.slnx"))) dir = dir.Parent;
        return File.ReadAllText(Path.Combine(new[] { dir!.FullName, "src", "GridSim.Core" }.Concat(parts).ToArray()));
    }

    [Fact]
    public void Event_catalog_doc_counts_the_built_ins_it_ships()
    {
        Assert.Equal(3, EventCatalog.Builtin().Count);
        string src = Core("Events", "EventCatalog.cs");
        Assert.Contains("Ships three built-ins", src);
        Assert.DoesNotContain("Ships two built-ins", src);
    }

    [Fact]
    public void Short_circuit_docs_no_longer_call_shipped_work_a_later_slice()
    {
        string iec = Core("Protection", "ShortCircuit", "Iec60909.cs");
        Assert.DoesNotContain("needs the fault-current decomposition\n    /// (a later slice)", iec.Replace("\r\n", "\n"));
        Assert.DoesNotContain("data the model does not yet carry; use <see cref=\"FromSequenceImpedances\"/>", iec);
        string oc = Core("Protection", "Coordination", "OvercurrentCoordination.cs");
        Assert.DoesNotContain("fault-analysis modules and a later slice", oc);
    }

    [Fact]
    public void Hybrid_multi_rate_scheduler_doc_says_it_is_experimental_and_undriven()
    {
        // R095 rest: the scheduler is referenced only by its tests; its summary claimed "the resident engine drives" it.
        string src = Core("Simulation", "HybridMultiRateScheduler.cs");
        Assert.Contains("EXPERIMENTAL (R095)", src);
        Assert.Contains("NOTHING drives it yet", src);
        Assert.DoesNotContain("- which the\n/// resident engine drives.", src.Replace("\r\n", "\n"));
    }

    [Fact]
    public void Projection_statistics_doc_says_it_is_not_yet_wired()
    {
        string src = Core("Estimation", "ProjectionStatistics.cs");
        Assert.Contains("NOT YET WIRED (R259", src);
        Assert.DoesNotContain("It is the\n/// masking-immune leverage source for the Schweppe-Huber GM estimator", src.Replace("\r\n", "\n"));
    }
}
