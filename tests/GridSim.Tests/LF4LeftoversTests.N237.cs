using GridSim.Core.Events;
using GridSim.Core.Evidence;
using GridSim.Core.Replay;
using GridSim.Core.Scenarios;
using Xunit;

namespace GridSim.Tests;

/// <summary>
/// N237 (LF4, Ruling R1): gb-2019-08-09 was "Reproduced" on the nadir VALUE only. The simulated nadir (about 48.82 Hz)
/// is the model's first arrest at about 14 s; the recorded 48.8 Hz is the LFDD dip at 75.9 s (ESO interim report of 16
/// August 2019, Table 1: LFDD at 16:53:49.398, fault at 16:52:33.490), and the real first arrest was a shallower 49.1 Hz at
/// about 25 s. The record now carries the nadir time, and the replay and the evidence dossier state the timing gap in
/// words. The verdict itself does not change (reporting only); the model-side cause is in the LF4 notes.
/// </summary>
public sealed partial class LF4LeftoversTests
{
    private static readonly DateTime N237Now = new(2026, 10, 6, 0, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void N237_the_2019_record_carries_its_nadir_time_in_the_built_in_and_both_bundled_catalogues()
    {
        Assert.Equal(75.9, EventCatalog.Get("gb-2019-08-09", utcNow: N237Now).Outcome.NadirSeconds);
        string events = Path.Combine(CliProcess.Root(), "data", "events");
        Assert.Equal(75.9, EventCatalog.Load(Path.Combine(events, "gb-events.json"), N237Now).Single(e => e.Id == "gb-2019-08-09").Outcome.NadirSeconds);
        Assert.Equal(75.9, Assert.Single(EventCatalog.Load(Path.Combine(events, "gb-2019-08-09-estimated.json"), N237Now)).Outcome.NadirSeconds);
        // Events whose record gives no nadir time keep none.
        Assert.Null(EventCatalog.Get("gb-2023-12-22", utcNow: N237Now).Outcome.NadirSeconds);
    }

    [Fact]
    public void N237_the_2019_replay_states_that_its_nadir_time_is_not_reproduced()
    {
        // Moved 2026-10 (LF6 N317, Ruling R1 (Mark delegated EE calls 2026-10-04); source ESO S192 Tables 1, 3 and 4): the
        // 2019 event now carries the recorded response, and its simulated nadir is the LFDD instant (76.0 s against the
        // recorded 75.9 s), so the CURRENT event has no timing note (pinned in Lf6CalibrationBoundsTests.N317). This test
        // keeps checking the N237 mechanism on the event as it was (generic governor, 1 %/Hz damping, RoCoF trip at 3.0 s),
        // whose nadir is the first arrest at about 14 s: every assertion below is unchanged.
        ReplayResult r = ReplayValidator.Validate(Lf6CalibrationBoundsTests.Pre2019N317(EventCatalog.Get("gb-2019-08-09", utcNow: N237Now)));
        Assert.Equal(75.9, r.RecordedNadirSeconds);
        Assert.Equal(r.Simulated.NadirSeconds, r.SimulatedNadirSeconds);
        // The simulated nadir is the first arrest, well before the LFDD instant.
        Assert.True(r.SimulatedNadirSeconds < 30.0, $"simulated nadir at {r.SimulatedNadirSeconds:R} s");
        Assert.NotNull(r.NadirTimingNote);
        Assert.Contains("nadir time NOT reproduced", r.NadirTimingNote, StringComparison.Ordinal);
        Assert.Contains("recorded at 75.9 s", r.NadirTimingNote, StringComparison.Ordinal);
        Assert.Contains(r.NadirTimingNote!, r.Summary, StringComparison.Ordinal);
        if (r.Verdict == ReplayVerdict.Reproduced)
            Assert.Contains("the verdict rests on the value only", r.NadirTimingNote, StringComparison.Ordinal);
    }

    [Fact]
    public void N237_a_replay_whose_record_has_no_nadir_time_is_unchanged()
    {
        ReplayResult r = ReplayValidator.Validate(EventCatalog.Get("gb-2023-12-22", utcNow: N237Now));
        Assert.Null(r.RecordedNadirSeconds);
        Assert.Null(r.NadirTimingNote);
        Assert.DoesNotContain("nadir time", r.Summary, StringComparison.Ordinal);
    }

    [Fact]
    public void N237_the_evidence_dossier_carries_the_timing_statement_with_the_baseline_verdict()
    {
        // Moved 2026-10 (LF6 N317, Ruling R1; source ESO S192 Tables 1, 3 and 4): as above, the dossier statement is checked
        // on the 2019 event as it was before N317; the current event's dossier carries none (Lf6CalibrationBoundsTests.N317).
        GridEvent ev = Lf6CalibrationBoundsTests.Pre2019N317(EventCatalog.Get("gb-2019-08-09", utcNow: N237Now));
        EvidenceReport rep = EvidenceReport.Build(EvidenceSpec.Default(ev, new DeviceConfig("lf4-n237-test", 10_000, 500)));
        Assert.StartsWith($"Baseline {rep.BaselineVerdict} [nadir time NOT reproduced: ", rep.Summary, StringComparison.Ordinal);
        Assert.Contains("recorded at 75.9 s", rep.Summary, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(76.0, 75.9, true)]     // 0.1 s apart: within 2.0 s
    [InlineData(77.9, 75.9, true)]     // 2.0 s apart (to round-off): within
    [InlineData(78.0, 75.9, false)]    // 2.1 s apart: not
    [InlineData(14.06, 75.9, false)]
    public void N237_the_timing_note_uses_the_back_test_tolerance(double sim, double rec, bool within)
    {
        Assert.Equal(2.0, ReplayTolerance.Default.NadirTimeToleranceS);
        string? note = ReplayValidator.NadirTimingNote(rec, sim, valueMatched: true, ReplayTolerance.Default.NadirTimeToleranceS);
        Assert.Equal(within, note is null);
    }

    [Fact]
    public void N237_the_timing_note_wording_and_its_input_checks()
    {
        Assert.Null(ReplayValidator.NadirTimingNote(null, 14.06, true, 2.0));
        Assert.Equal("nadir time NOT reproduced: simulated nadir at 14.1 s, recorded at 75.9 s, 61.8 s apart (tolerance 2.0 s); "
                     + "the nadir value matches but at a different point of the event, so the verdict rests on the value only",
            ReplayValidator.NadirTimingNote(75.9, 14.06, true, 2.0));
        Assert.Equal("nadir time NOT reproduced: simulated nadir at 14.1 s, recorded at 75.9 s, 61.8 s apart (tolerance 2.0 s)",
            ReplayValidator.NadirTimingNote(75.9, 14.06, false, 2.0));
        foreach (double bad in new[] { -1.0, double.NaN, double.PositiveInfinity })
            Assert.Throws<ArgumentOutOfRangeException>(() => ReplayValidator.NadirTimingNote(bad, 14.06, true, 2.0));
    }
}
