using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using GridSim.Core.Diagnostics;
using GridSim.Core.Dynamics;
using GridSim.Core.Events;
using GridSim.Core.Evidence;
using GridSim.Core.Replay;
using GridSim.Core.Scenarios;
using Xunit;

namespace GridSim.Tests;

/// <summary>
/// The unified evidence dossier: every column is provenance-labelled, intrinsic inertia and FFR are never merged,
/// the avoided-cost arithmetic is exact and grounded in the recorded shed, and the whole counterfactual section
/// is withheld unless the baseline reproduces the record.
/// </summary>
public class EvidenceReportTests
{
    // A self-consistent event whose recorded outcome (incl. an LFDD shed) is exactly what the no-device model
    // produces, so the baseline reproduces - and whose losses are deep enough that LFDD really did fire.
    private static GridEvent SelfConsistentWithLfdd(
        double inertiaMwS, double demandMw, double lossMw, double lfddShedMw)
    {
        var cfg = new DynamicsConfig();
        var withLfdd = new[] { new FrequencyEvent(1.0, -lossMw), new FrequencyEvent(3.0, +lfddShedMw) };
        FrequencyResponse truth = FrequencyDynamics.Simulate(inertiaMwS, demandMw, withLfdd, cfg);
        return new GridEvent(
            "syn-lfdd", "self-consistent with LFDD", new DateTime(2019, 1, 1, 0, 0, 0, DateTimeKind.Utc),
            inertiaMwS, demandMw,
            new List<PowerStep> { new(1.0, -lossMw, "loss", ProvenanceTag.Synthetic) },
            new List<RecordedAction> { new(3.0, +lfddShedMw, "LFDD", ProvenanceTag.Synthetic) },
            new RecordedOutcome(truth.InitialRocofHzPerS, truth.NadirHz, truth.SettleHz, lfddShedMw, null, ProvenanceTag.Synthetic),
            ProvenanceTag.Synthetic);
    }

    private static GridEvent SelfConsistentNoLfdd(double inertiaMwS = 200_000, double demandMw = 35_000, double lossMw = 1320)
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

    [Fact]
    public void Columns_are_provenance_labelled_and_never_merge_inertia_with_ffr()
    {
        var device = new DeviceConfig("tsgb", 40_000, 2000);
        EvidenceReport r = EvidenceReport.Build(EvidenceSpec.Default(SelfConsistentNoLfdd(), device));

        // Moved 2026-10 (END-PASS CF6 row 9, end pass; N359 and main's ruling on CF6-F3 / F4, technical ch. 8 "trust only
        // moves down": the simulated column is Weakest(DerivedFromRecorded, the event's effective tag), Ruling R1 and Mark's
        // go on the end-pass moves 2026-10-11): the event this test builds is Synthetic (self-consistent), so its simulated
        // column can claim no more than Synthetic; DerivedFromRecorded said it came from a record. Built-in recorded events
        // keep DerivedFromRecorded. Before -> after: DerivedFromRecorded -> Synthetic (observed: Expected DerivedFromRecorded,
        // Actual Synthetic, 1.95.273-275 runs).
        Assert.Equal(ProvenanceTag.Synthetic, r.Columns[1].Provenance);
        EvidenceColumn cf = r.Columns.Single(c => c.Provenance == ProvenanceTag.Counterfactual);
        // The device's rotational inertia and FFR appear in SEPARATE fields, both non-zero, never summed.
        Assert.Equal(40.0, cf.DeviceRotationalInertiaGvaS, 6);
        Assert.Equal(2.0, cf.DeviceFfrGw, 6);
        // Recorded/simulated columns carry no device.
        Assert.All(r.Columns.Where(c => c.Provenance != ProvenanceTag.Counterfactual),
            c => Assert.Equal(0.0, c.DeviceRotationalInertiaGvaS + c.DeviceFfrGw, 9));
    }

    [Fact]
    public void Avoided_cost_arithmetic_is_exact_and_grounded_in_the_recorded_shed()
    {
        // Deep loss with a recorded 600 MW LFDD shed; a strong device keeps the counterfactual above LFDD.
        GridEvent ev = SelfConsistentWithLfdd(90_000, 30_000, lossMw: 2200, lfddShedMw: 600);
        var device = new DeviceConfig("tsgb", 60_000, 3000);
        var spec = EvidenceSpec.Default(ev, device) with { AvoidedCost = new AvoidedCostInputs(6000, 0.5) };

        EvidenceReport r = EvidenceReport.Build(spec);
        Assert.Equal(ReplayVerdict.Reproduced, r.BaselineVerdict);
        Assert.NotNull(r.AvoidedCost);
        // 600 MW x 0.5 h x £6000/MWh = £1,800,000
        Assert.Equal(600 * 0.5 * 6000, r.AvoidedCost!.AvoidedCostGbp, 3);
        Assert.Equal(ProvenanceTag.CuratedPlaceholder, r.AvoidedCost.Provenance); // VoLL is policy, flagged
    }

    [Fact]
    public void The_counterfactual_is_withheld_when_the_baseline_fails()
    {
        GridEvent good = SelfConsistentNoLfdd();
        GridEvent bad = good with { Outcome = good.Outcome with { NadirHz = good.Outcome.NadirHz + 3.0 } };
        EvidenceReport r = EvidenceReport.Build(EvidenceSpec.Default(bad, new DeviceConfig("tsgb", 40_000, 2000)));

        Assert.NotEqual(ReplayVerdict.Reproduced, r.BaselineVerdict);
        Assert.False(r.CounterfactualUnlocked);
        Assert.Null(r.RiskCurve);
        Assert.Null(r.AvoidedCost);
        Assert.Equal(2, r.Columns.Count); // recorded + simulated only; no counterfactual
    }

    [Fact]
    public void A_marginal_baseline_withholds_the_counterfactual()
    {
        // The gate's MIDDLE case was unspecified by tests: only Reproduced unlocks, so a Marginal baseline must
        // withhold the counterfactual/risk-curve/fleet/avoided-cost sections just like a Failed one does.
        GridEvent good = SelfConsistentNoLfdd();
        GridEvent marginal = good with
        {
            Outcome = good.Outcome with
            {
                InitialRocofHzPerS = good.Outcome.InitialRocofHzPerS * 1.2,   // ~16.7% off - marginal band
                NadirHz = good.Outcome.NadirHz - 0.2,                          // 0.2 Hz off - marginal band
            },
        };
        EvidenceReport r = EvidenceReport.Build(EvidenceSpec.Default(marginal, new DeviceConfig("tsgb", 40_000, 2000)));

        Assert.Equal(ReplayVerdict.Marginal, r.BaselineVerdict);
        Assert.False(r.CounterfactualUnlocked);
        Assert.Null(r.RiskCurve);
        Assert.Null(r.Fleet);
        Assert.Null(r.AvoidedCost);
        Assert.Equal(2, r.Columns.Count);   // recorded + simulated only
    }

    [Fact]
    public void The_default_spec_uses_the_ten_second_onset_spread()
    {
        // The flagship dossier's fleet section must spread onset across ~10 s (GB primary response window), not the
        // old sub-second default that finishes before the nadir and hides the non-additivity. (regression)
        EvidenceSpec spec = EvidenceSpec.Default(SelfConsistentNoLfdd(), new DeviceConfig("tsgb", 40_000, 2000));
        Assert.Equal(100, spec.FleetUnits);
        Assert.Equal(10.0, spec.FleetStaggerSeconds, 9);
    }

    [Fact] // Wave 2 regression: counterfactual must replay the FULL loss window, not the 30 s default
    public void Counterfactual_replays_full_window_and_does_not_falsely_claim_avoided_cost()
    {
        // gb-2019-08-09 stages -1,247 MW of losses between 40-90 s (past the 30 s default) and sheds
        // 892 MW of LFDD. A ZERO device cannot avert the deep late-event fall, so the full-window
        // counterfactual must stay AT RISK of LFDD and NO avoided cost may be claimed. Pre-fix the
        // counterfactual truncated at 30 s, saw only the initial cluster, reported a ~49 Hz nadir,
        // and falsely unlocked a concrete avoided-cost figure.
        GridEvent ev = EventCatalog.Get("gb-2019-08-09", utcNow: new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc));
        var zeroDevice = new DeviceConfig("none", 0, 0);

        EvidenceReport r = EvidenceReport.Build(EvidenceSpec.Default(ev, zeroDevice));

        Assert.Equal(ReplayVerdict.Reproduced, r.BaselineVerdict);
        EvidenceColumn cf = r.Columns.Single(c => c.Provenance == ProvenanceTag.Counterfactual);
        Assert.True(cf.LfddAtRisk, "a zero device on the full-window 2019 event must fall through LFDD");
        Assert.Null(r.AvoidedCost);
    }

    [Fact]
    public void Report_serialises_with_the_schema_tag()
    {
        EvidenceReport r = EvidenceReport.Build(EvidenceSpec.Default(SelfConsistentNoLfdd(), new DeviceConfig("tsgb", 40_000, 2000)));
        using var doc = JsonDocument.Parse(r.ToJson());
        Assert.Equal("gridsim-evidence/1", doc.RootElement.GetProperty("schema").GetString());
        Assert.Equal("syn", doc.RootElement.GetProperty("report").GetProperty("eventId").GetString());
    }
}
