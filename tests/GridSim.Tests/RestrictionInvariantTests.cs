using System;
using System.IO;
using GridSim.Core.Diagnostics;
using GridSim.Core.Estimation;
using Xunit;

namespace GridSim.Tests;

/// <summary>
/// The five testable invariants named by <c>UnderstandingRestrictions.md</c> ("an invariant that isn't
/// tested is a hope"). They belong next to the machine-epsilon solver checks and the
/// <see cref="ForwardInferenceGuardTests"/> boundary pins. Each fact maps to one numbered invariant.
///
/// Invariant #1 (no ingester accepts a stamp newer than <c>UtcNow-169h</c>) is enforced at every dated
/// seam by <see cref="ForwardInferenceGuard"/>; the seams are static methods, so rather than a marker
/// interface these tests exercise a representative seam directly and the per-seam suites
/// (<see cref="ForwardInferenceGuardTests"/>, <c>GdaReplayLoopTests</c>) pin the individual call sites.
/// </summary>
public class RestrictionInvariantTests
{
    private static readonly DateTime Now = new(2026, 7, 1, 12, 0, 0, DateTimeKind.Utc);
    private static readonly DateTime Historical = Now.AddHours(-(ForwardInferenceGuard.HorizonHours + 5));

    // GR-884: one folder per test run (a fixed name was shared by concurrent test processes, which then read each
    // other's files).
    private static readonly string RunDir = Path.Combine(Path.GetTempPath(), $"gridsim-restriction-tests-{Guid.NewGuid():N}");

    private static string TempPath(string name)
    {
        string dir = RunDir;
        Directory.CreateDirectory(dir);
        return Path.Combine(dir, name);
    }

    private static MeasurementSet OneVoltageSet(DateTime? takenUtc) => new()
    {
        Case = "restriction-invariant",
        TakenUtc = takenUtc,
        Measurements = new[] { new Measurement(MeasurementKind.VmPu, 1.0, 0.01, Bus: 1) },
    };

    // ---- Invariant #1: no ingester accepts a stamp newer than UtcNow - 169h --------------------------

    [Fact]
    public void Inv1_The_horizon_is_a_single_knob()
    {
        Assert.Equal(169.0, ForwardInferenceGuard.HorizonHours);
        Assert.Equal(Now.AddHours(-ForwardInferenceGuard.HorizonHours), ForwardInferenceGuard.Horizon(Now));
    }

    [Fact]
    public void Inv1_A_dated_ingestion_seam_rejects_a_fresh_stamp()
    {
        // MeasurementSet.Load is one of the dated ingestion seams; a fresh TakenUtc must be refused.
        string path = TempPath("fresh-set.json");
        OneVoltageSet(Now).Save(path);
        Assert.Throws<ForwardInferenceException>(() => MeasurementSet.Load(path, Now));
    }

    [Fact]
    public void Inv1_A_dated_ingestion_seam_accepts_a_historical_stamp()
    {
        string path = TempPath("historical-set.json");
        OneVoltageSet(Historical).Save(path);
        MeasurementSet loaded = MeasurementSet.Load(path, Now); // must not throw
        Assert.Equal(Historical, loaded.TakenUtc);
    }

    // ---- Invariant #2: no public-data path emits an output stamped later than its newest input --------

    [Fact]
    public void Inv2_The_state_time_channel_is_guarded_end_to_end()
    {
        // The estimate's validity time IS the measurement set's TakenUtc. A future state-time cannot enter
        // (rejected on load), and a historical one round-trips unchanged - so no estimate can inherit a
        // future-dated state time.
        string future = TempPath("future-state.json");
        OneVoltageSet(Now.AddHours(24)).Save(future);
        Assert.Throws<ForwardInferenceException>(() => MeasurementSet.Load(future, Now));

        string past = TempPath("past-state.json");
        OneVoltageSet(Historical).Save(past);
        Assert.Equal(Historical, MeasurementSet.Load(past, Now).TakenUtc);
    }

    // ---- Invariant #3: solver forward trajectories are labelled model-output ------------------------

    [Fact]
    public void Inv3_Model_output_provenance_labels_are_distinct_from_recorded()
    {
        // The labelling vocabulary that keeps a model trajectory from masquerading as a recorded grid claim
        // must exist and be distinct. A forward solve is Synthetic/Counterfactual, never Recorded.
        Assert.NotEqual(ProvenanceTag.Recorded, ProvenanceTag.Synthetic);
        Assert.NotEqual(ProvenanceTag.Recorded, ProvenanceTag.Counterfactual);
        Assert.NotEqual(ProvenanceTag.Synthetic, ProvenanceTag.Counterfactual);
    }

    // ---- Invariant #4: real-time features prove on replay / permitted-age streams, never live --------

    [Fact]
    public void Inv4_A_realtime_window_stamped_fresh_is_refused()
    {
        // The real-time engine (Phase 1) calls the same guard on every window's timestamp; a fresh window is
        // refused, so a resident sub-cycle service is structurally incapable of nowcasting. The live engine is
        // exercised end-to-end by RealtimeEngineTests once Phase 1 lands; here we pin the guard obligation.
        Assert.Throws<ForwardInferenceException>(
            () => ForwardInferenceGuard.RejectFuture(Now.AddMinutes(-1), Now, "real-time window"));
        ForwardInferenceGuard.RejectFuture(Historical, Now, "real-time window"); // permitted-age window passes
    }

    // ---- Invariant #5: any Dynamics -> Estimation fusion is gated by explicit review ----------------

    [Fact]
    public void Inv5_A_dynamics_fused_method_without_review_is_rejected()
    {
        Assert.Throws<RestrictionViolationException>(
            () => RestrictionReview.AssertPermitted("SomeUnreviewedEkf", usesDynamicsModel: true));
        Assert.False(RestrictionReview.IsReviewed("SomeUnreviewedEkf"));
    }

    [Fact]
    public void Inv5_A_static_measurement_only_method_is_permitted_without_review()
    {
        // Filters, not predictors: static estimators need no review-log entry.
        RestrictionReview.AssertPermitted("Wls", usesDynamicsModel: false);   // must not throw
        RestrictionReview.AssertPermitted("Huber", usesDynamicsModel: false);
        RestrictionReview.AssertPermitted("Feasible", usesDynamicsModel: false);
    }

    [Fact]
    public void Inv5_Every_reviewed_entry_certifies_present_past_output_and_carries_a_note()
    {
        // Guards future DSE entries: a reviewed dynamics-fused method must certify present/past-only output
        // and name its review rationale. Vacuously true until Phase 2 populates the table.
        Assert.All(RestrictionReview.Reviewed, e =>
        {
            Assert.True(e.UsesDynamicsModel, $"'{e.Method}' is in the review log but not marked dynamics-fused.");
            Assert.True(e.OutputPresentPastOnly, $"'{e.Method}' must certify present/past-only output.");
            Assert.False(string.IsNullOrWhiteSpace(e.ReviewNote), $"'{e.Method}' must carry a review note.");
        });
    }

    // Invariant #5 is certified by review (AssertPermitted checks the attestation); AssertOutputHistorical
    // is the MECHANICAL enforcement of that certification at the emit boundary - so a recursive DSE predict
    // step (which integrates the model forward internally) can never leak a future-dated state into a frame.

    [Fact]
    public void Inv5_A_present_past_stamped_output_is_permitted()
    {
        // A frame stamped at a historical (>=169h old) window time passes the output guard.
        RestrictionReview.AssertOutputHistorical(Historical, Now, "test estimate"); // does not throw
    }

    [Fact]
    public void Inv5_A_future_dated_output_is_rejected_mechanically()
    {
        // A frame stamped inside the horizon (a nowcast / future-dated state) is refused at emit, even though
        // the review-log attestation would pass - the mechanical check does not trust the attestation alone.
        DateTime tooFresh = Now.AddHours(-(ForwardInferenceGuard.HorizonHours - 10)); // 10h inside the horizon
        Assert.Throws<RestrictionViolationException>(
            () => RestrictionReview.AssertOutputHistorical(tooFresh, Now, "test estimate"));
    }

    [Fact]
    public void Inv5_The_output_guard_boundary_matches_the_input_horizon()
    {
        // The output guard uses exactly the same 169h horizon as the input ForwardInferenceGuard: a stamp
        // one second past the horizon is rejected, one second before is accepted (both stamped as UTC).
        DateTime horizon = ForwardInferenceGuard.Horizon(Now);
        RestrictionReview.AssertOutputHistorical(horizon.AddSeconds(-1), Now); // just historical -> ok
        Assert.Throws<RestrictionViolationException>(
            () => RestrictionReview.AssertOutputHistorical(horizon.AddSeconds(1), Now));
    }
}
