using GridSim.Core.Events;
using GridSim.Core.Replay;
using Xunit;

namespace GridSim.Tests;

/// <summary>
/// The per-sample RMSE against an attached NESO trace (docs-programme finding): a trace shorter than the scored window
/// used to be compared against its LAST sample (the nearest-sample lookup clamps), and an event instant outside the
/// trace anchored on an end sample. Now samples past the trace end are counted and left out, and an event outside the
/// trace gives no RMSE; both are reported in <see cref="ReplayResult.TraceNote"/>. A trace that covers the whole window
/// is scored exactly as before.
/// </summary>
public class ReplayTraceCoverageTests
{
    private static string Root()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "GridSim.slnx"))) dir = dir.Parent;
        Assert.True(dir is not null, $"GridSim.slnx not found above {AppContext.BaseDirectory}");
        return dir!.FullName;
    }

    private static string FullTrace() => Path.Combine(Root(), "data", "neso", "gb-2023-12-22-frequency.csv");

    private static ReplayResult ReplayWith(string tracePath)
    {
        GridEvent ev = EventCatalog.Builtin().Single(e => e.Id == "gb-2023-12-22");
        ev = ev with { Outcome = ev.Outcome with { NesoTraceCsvPath = tracePath } };
        return ReplayValidator.Validate(ev);
    }

    private static string WriteTrace(Func<string[], IEnumerable<string>> pick)
    {
        string[] lines = File.ReadAllLines(FullTrace());
        string path = Path.Combine(Path.GetTempPath(), $"gridsim-trace-{Guid.NewGuid():N}.csv");
        File.WriteAllLines(path, pick(lines));
        return path;
    }

    [Fact]
    public void A_trace_that_covers_the_window_scores_every_sample_and_has_no_note()
    {
        ReplayResult r = ReplayWith(FullTrace());
        Assert.NotNull(r.PerSampleRmseHz);
        Assert.Equal(0, r.TraceSamplesPastEnd);
        Assert.True(r.TraceSamplesScored > 0);
        Assert.Null(r.TraceNote);
    }

    [Fact]
    public void A_short_trace_is_scored_only_where_it_reaches_and_says_so()
    {
        // Header plus the rows up to 13:10:01, ten seconds after the 13:09:51 event instant.
        string path = WriteTrace(l => l.Take(183));
        try
        {
            ReplayResult full = ReplayWith(FullTrace());
            ReplayResult r = ReplayWith(path);
            Assert.NotNull(r.PerSampleRmseHz);
            Assert.True(r.TraceSamplesPastEnd > 0, "samples past the trace end must be counted");
            Assert.Equal(full.TraceSamplesScored, r.TraceSamplesScored + r.TraceSamplesPastEnd);
            Assert.NotNull(r.TraceNote);
            Assert.Contains("past its end not scored", r.TraceNote);
            Assert.Contains(r.TraceNote!, r.Summary);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void A_trace_that_misses_the_event_instant_gives_no_rmse()
    {
        // Header plus rows from 13:13:38, well after the event instant.
        string path = WriteTrace(l => new[] { l[0] }.Concat(l.Skip(399)));
        try
        {
            ReplayResult r = ReplayWith(path);
            Assert.Null(r.PerSampleRmseHz);
            Assert.NotNull(r.TraceNote);
            Assert.Contains("does not contain the event instant", r.TraceNote);
        }
        finally { File.Delete(path); }
    }
}
