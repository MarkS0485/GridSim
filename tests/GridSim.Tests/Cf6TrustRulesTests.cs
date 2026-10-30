using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using GridSim.Core.Diagnostics;
using GridSim.Core.Dynamics;
using GridSim.Core.Events;
using GridSim.Core.Evidence;
using GridSim.Core.Harmonics;
using GridSim.Core.IO;
using GridSim.Core.Replay;
using GridSim.Core.Scenarios;
using Xunit;

namespace GridSim.Tests;

/// <summary>
/// CF6 (N359, with main's ruling on CF6-F3 / F4): the three trust rules CF5 left as reviewer rules, now enforced in code
/// with CF5's downgrade-only <see cref="ProvenanceTrust"/>: (1) replay-event needs an event that is Recorded or
/// DerivedFromRecorded throughout (refused by name otherwise), while the study verbs accept placeholder or synthetic
/// events and inherit the weaker tag; (2) <c>--voll-source</c> does not raise the VoLL tag (and <see cref="AvoidedCost.FromLfddAvoided"/>
/// refuses a raised one); (3) the harmonic export headline is not raised from DerivedFromRecorded to Recorded.
/// </summary>
public sealed class Cf6TrustRulesTests : IDisposable
{
    private static readonly DateTime Now = new(2026, 7, 1, 12, 0, 0, DateTimeKind.Utc);
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "gridsim-cf6-trust-" + Guid.NewGuid().ToString("N"));

    public Cf6TrustRulesTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    // A self-consistent synthetic event (the EvidenceReportTests construction): its outcome is what the no-device model
    // produces, so the gate reproduces it when it is allowed to run.
    private static GridEvent SelfConsistent(ProvenanceTag eventTag)
    {
        var cfg = new DynamicsConfig();
        var steps = new[] { new FrequencyEvent(1.0, -1320.0) };
        FrequencyResponse truth = FrequencyDynamics.Simulate(200_000, 35_000, steps, cfg);
        return new GridEvent(
            "syn-cf6", "self-consistent CF6 event", new DateTime(2019, 1, 1, 0, 0, 0, DateTimeKind.Utc), 200_000, 35_000,
            new List<PowerStep> { new(1.0, -1320.0, "loss", ProvenanceTag.Synthetic) },
            new List<RecordedAction>(),
            new RecordedOutcome(truth.InitialRocofHzPerS, truth.NadirHz, truth.SettleHz, 0, null, ProvenanceTag.Synthetic),
            eventTag);
    }

    // ------------------------------------------------------------------ (1) replays need recorded events; studies downgrade

    [Fact]
    public void The_effective_provenance_is_the_weakest_of_the_parts()
    {
        Assert.Equal(ProvenanceTag.CuratedPlaceholder, ReplayValidator.EffectiveProvenance(EventCatalog.Get("gb-largest-loss-1320", utcNow: Now)));
        // The built-in recorded events carry DerivedFromRecorded loss figures beside Recorded ones.
        Assert.Equal(ProvenanceTag.DerivedFromRecorded, ReplayValidator.EffectiveProvenance(EventCatalog.Get("gb-2019-08-09", utcNow: Now)));
        Assert.Equal(ProvenanceTag.DerivedFromRecorded, ReplayValidator.EffectiveProvenance(EventCatalog.Get("gb-2023-12-22", utcNow: Now)));
        // A Recorded event whose loss is a placeholder is effectively a placeholder (CF6-F3 part 1).
        GridEvent rec = SelfConsistent(ProvenanceTag.Recorded) with
        {
            Outcome = SelfConsistent(ProvenanceTag.Recorded).Outcome with { Provenance = ProvenanceTag.Recorded },
            Losses = new List<PowerStep> { new(1.0, -1320.0, "placeholder loss", ProvenanceTag.CuratedPlaceholder) },
        };
        Assert.Equal(ProvenanceTag.CuratedPlaceholder, ReplayValidator.EffectiveProvenance(rec));
        // Two different tags of the same rank weaken to CuratedPlaceholder.
        Assert.Equal(ProvenanceTag.CuratedPlaceholder, ProvenanceTrust.Weakest(new[] { ProvenanceTag.Synthetic, ProvenanceTag.Interpolated }, "x"));
        Assert.Equal(ProvenanceTag.Synthetic, ProvenanceTrust.Weakest(new[] { ProvenanceTag.Recorded, ProvenanceTag.Synthetic }, "x"));
        Assert.Throws<InvalidDataException>(() => ProvenanceTrust.Weakest(new[] { ProvenanceTag.Counterfactual }, "x"));
    }

    [Theory]
    [InlineData(ProvenanceTag.CuratedPlaceholder)]
    [InlineData(ProvenanceTag.Synthetic)]
    [InlineData(ProvenanceTag.Interpolated)]
    public void A_replay_refuses_an_event_that_is_not_recorded_by_name(ProvenanceTag tag)
    {
        GridEvent ev = SelfConsistent(tag);
        InvalidDataException ex = Assert.Throws<InvalidDataException>(() => ReplayValidator.RequireRecordedReplay(ev));
        Assert.Contains("event 'syn-cf6'", ex.Message, StringComparison.Ordinal);
        Assert.Contains("a replay presents its output as a reproduction of a recorded event", ex.Message, StringComparison.Ordinal);
        // The gate itself (shared by the study verbs) still runs it.
        Assert.Equal(ReplayVerdict.Reproduced, ReplayValidator.Validate(ev).Verdict);
    }

    [Theory]
    [InlineData("gb-2019-08-09")]
    [InlineData("gb-2023-12-22")]
    public void The_builtin_recorded_events_still_replay(string id)
    {
        GridEvent ev = EventCatalog.Get(id, utcNow: Now);
        ReplayValidator.RequireRecordedReplay(ev);
        Assert.Equal(id, ReplayValidator.Validate(ev).EventId);
    }

    [Fact]
    public void A_recorded_event_with_a_placeholder_loss_is_refused_naming_the_loss()
    {
        GridEvent rec = SelfConsistent(ProvenanceTag.Recorded) with
        {
            Losses = new List<PowerStep> { new(1.0, -1320.0, "placeholder loss", ProvenanceTag.CuratedPlaceholder) },
        };
        InvalidDataException ex = Assert.Throws<InvalidDataException>(() => ReplayValidator.RequireRecordedReplay(rec));
        Assert.Contains("loss 0 (placeholder loss, CuratedPlaceholder)", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Evidence_on_a_placeholder_event_builds_and_every_tag_is_downgraded()
    {
        var device = new DeviceConfig("tsgb", 60_000, 3000);
        EvidenceReport r = EvidenceReport.Build(EvidenceSpec.Default(SelfConsistent(ProvenanceTag.CuratedPlaceholder), device));
        Assert.Equal(ReplayVerdict.Reproduced, r.BaselineVerdict);
        Assert.Equal(ProvenanceTag.CuratedPlaceholder, r.EventProvenance);
        Assert.Equal(ProvenanceTag.CuratedPlaceholder, r.Columns[1].Provenance);   // simulated (no device)
        Assert.DoesNotContain(r.Columns, c => c.Provenance is ProvenanceTag.Recorded or ProvenanceTag.DerivedFromRecorded);
        // A recorded built-in keeps DerivedFromRecorded on its simulated column.
        EvidenceReport aug = EvidenceReport.Build(EvidenceSpec.Default(EventCatalog.Get("gb-2019-08-09", utcNow: Now), device));
        Assert.Equal(ProvenanceTag.DerivedFromRecorded, aug.Columns[1].Provenance);
        Assert.Equal(ProvenanceTag.DerivedFromRecorded, aug.EventProvenance);
    }

    [Fact]
    public void Cli_replay_event_refuses_the_screening_event_but_fleet_studies_it_with_the_downgraded_tag()
    {
        CliProcess.Result replay = CliProcess.RunBuilt("replay-event", "gb-largest-loss-1320", "--out", Path.Combine(_dir, "r"));
        Assert.True(replay.ExitCode == 1, replay.ToString());
        Assert.Contains("event 'gb-largest-loss-1320'", replay.StdErr, StringComparison.Ordinal);
        Assert.Contains("is effectively CuratedPlaceholder", replay.StdErr, StringComparison.Ordinal);
        CliProcess.Result fleet = CliProcess.RunBuilt("fleet", "--event", "gb-largest-loss-1320", "--out", Path.Combine(_dir, "f"));
        Assert.True(fleet.ExitCode == 0, fleet.ToString());
        Assert.Contains("input provenance: [CuratedPlaceholder]", fleet.StdOut, StringComparison.Ordinal);
        Assert.Contains("provenance=CuratedPlaceholder", File.ReadAllText(Path.Combine(_dir, "f", "run-metadata.json")), StringComparison.Ordinal);
        // A recorded event prints no such line.
        CliProcess.Result aug = CliProcess.RunBuilt("fleet", "--event", "gb-2019-08-09", "--out", Path.Combine(_dir, "g"));
        Assert.True(aug.ExitCode == 0, aug.ToString());
        Assert.DoesNotContain("input provenance:", aug.StdOut, StringComparison.Ordinal);
    }

    // ------------------------------------------------------------------ (2) --voll-source does not raise the tag

    [Fact]
    public void A_cited_voll_keeps_the_policy_tag_and_records_the_citation()
    {
        AvoidedCostResult cited = AvoidedCost.FromLfddAvoided(892, new AvoidedCostInputs(6000, 0.5,
            VollProvenance: AvoidedCost.VollBaseProvenance, DurationProvenance: ProvenanceTag.CuratedPlaceholder, VollSourceId: "S324"));
        Assert.Equal(ProvenanceTag.CuratedPlaceholder, AvoidedCost.VollBaseProvenance);
        Assert.Equal(892 * 6000.0 * 0.5, cited.AvoidedCostGbp);
        Assert.Equal(ProvenanceTag.CuratedPlaceholder, cited.VollProvenance);
        Assert.Equal(ProvenanceTag.CuratedPlaceholder, cited.Provenance);
        Assert.Equal("S324", cited.VollSourceId);
    }

    [Theory]
    [InlineData(ProvenanceTag.Recorded, "S324")]
    [InlineData(ProvenanceTag.Recorded, null)]
    [InlineData(ProvenanceTag.DerivedFromRecorded, "S322")]
    [InlineData(ProvenanceTag.Synthetic, null)]
    public void A_raised_voll_tag_is_refused_with_or_without_a_citation(ProvenanceTag voll, string? source)
    {
        InvalidDataException ex = Assert.Throws<InvalidDataException>(() => AvoidedCost.FromLfddAvoided(892,
            new AvoidedCostInputs(6000, 0.5, VollProvenance: voll, DurationProvenance: ProvenanceTag.CuratedPlaceholder, VollSourceId: source)));
        Assert.Contains("trust only moves down", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void An_output_tag_on_the_duration_is_refused_and_the_defaults_pass_unchanged()
    {
        Assert.Throws<InvalidDataException>(() => AvoidedCost.FromLfddAvoided(1,
            new AvoidedCostInputs(DurationProvenance: ProvenanceTag.Counterfactual)));
        AvoidedCostResult d = AvoidedCost.FromLfddAvoided(600, new AvoidedCostInputs());
        Assert.Equal(600 * 6000.0 * 0.5, d.AvoidedCostGbp);
        Assert.Equal(ProvenanceTag.CuratedPlaceholder, d.Provenance);
    }

    [Fact]
    public void Cli_evidence_voll_source_prints_the_citation_but_not_a_raised_tag()
    {
        CliProcess.Result ok = CliProcess.RunBuilt("evidence", "--event", "gb-2019-08-09", "--voll-source", "S324", "--out", Path.Combine(_dir, "e"));
        Assert.True(ok.ExitCode == 0, ok.ToString());
        Assert.Contains("VoLL [CuratedPlaceholder]   duration [CuratedPlaceholder]", ok.StdOut, StringComparison.Ordinal);
        Assert.Contains("VoLL source: S324", ok.StdOut, StringComparison.Ordinal);
        Assert.DoesNotContain("VoLL [Recorded]", ok.StdOut, StringComparison.Ordinal);
    }

    // ------------------------------------------------------------------ (3) harmonic export headline

    [Fact]
    public void The_harmonic_headline_is_the_weakest_measured_tag_never_raised()
    {
        HarmonicSource assumed = HarmonicSourceLibrary.Make("6-pulse", bus: 5, fundamentalCurrentPu: 0.8);
        HarmonicSource recorded = assumed with { Provenance = ProvenanceTag.Recorded };
        HarmonicSource derived = assumed with { Provenance = ProvenanceTag.DerivedFromRecorded };

        Assert.Equal(ProvenanceTag.Recorded, HarmonicExport.HeadlineProvenance(new[] { recorded, recorded }));
        // Before N359 this read Recorded (a raise of the derived source's trust).
        Assert.Equal(ProvenanceTag.DerivedFromRecorded, HarmonicExport.HeadlineProvenance(new[] { recorded, derived }));
        Assert.Equal(ProvenanceTag.DerivedFromRecorded, HarmonicExport.HeadlineProvenance(new[] { derived }));
        Assert.Equal(ProvenanceTag.CuratedPlaceholder, HarmonicExport.HeadlineProvenance(new[] { recorded, assumed }));
        Assert.Equal(ProvenanceTag.CuratedPlaceholder, HarmonicExport.HeadlineProvenance(new[] { assumed }));
        Assert.Equal(ProvenanceTag.CuratedPlaceholder, HarmonicExport.HeadlineProvenance(Array.Empty<HarmonicSource>()));

        // Every headline passes the downgrade-only check against its own sources.
        foreach (HarmonicSource[] set in new[] { new[] { recorded, derived }, new[] { recorded }, new[] { assumed, derived } })
            ProvenanceTrust.RequireNotRaised(set.Select(s => s.Provenance), HarmonicExport.HeadlineProvenance(set), "test");

        // An output tag on a source is refused as an input.
        HarmonicSource counterfactual = assumed with { Provenance = ProvenanceTag.Counterfactual };
        Assert.Throws<InvalidDataException>(() => HarmonicExport.HeadlineProvenance(new[] { recorded, counterfactual }));
    }

    [Fact]
    public void The_trust_class_no_longer_lists_these_rules_as_reviewer_rules()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "GridSim.slnx"))) dir = dir.Parent;
        string src = File.ReadAllText(Path.Combine(dir!.FullName, "src", "GridSim.Core", "Diagnostics", "ProvenanceTrust.cs"));
        Assert.DoesNotContain("What is still NOT enforced", src, StringComparison.Ordinal);
        Assert.Contains("ReplayValidator.RequireRecordedReplay", src, StringComparison.Ordinal);
        Assert.Contains("AvoidedCost.VollBaseProvenance", src, StringComparison.Ordinal);
        Assert.Contains("HarmonicExport.HeadlineProvenance", src, StringComparison.Ordinal);
    }
}
