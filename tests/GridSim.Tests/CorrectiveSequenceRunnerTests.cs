using System.Text.Json;
using System.Text.Json.Serialization;
using GridSim.Core.Analysis;
using GridSim.Core.Analysis.TransmissionSecurity;
using GridSim.Core.Model;
using GridSim.Core.PowerFlow;
using Xunit;

namespace GridSim.Tests;

/// <summary>
/// RC3 P5: ordered corrective N-1-1 over the sequenced-state runner.
/// <para>Fixture: three lossless parallel circuits (0, 1, 2; X = 0.3 pu) carry a scheduled remote export from unit 0 at
/// bus 1 to bus 2 (demand 180 MW, local unit 1 at 30 MW); a lossless tie (branch 3, X = 0.05) joins bus 2 to the slack
/// at bus 3 (demand 20 MW, unit 2 at 20 MW). Every bus is voltage-controlled at 1.0 pu, so each 1-2 circuit carries
/// P = P0/n and, with both terminal voltages at 1.0, |S| at both ends = 2 sin(d/2)/X = sqrt(2(1 - cos d))/X with
/// sin d = X P (per unit): the closed-form oracle <see cref="Mva"/>. The slack supplies exactly its own 20 MW.</para>
/// <para>Ratings: circuits 0 and 1 A 60 / B 80 / C 120 MVA; circuit 2 A 60 / B 65 / C 70 MVA; tie 100 MVA.
/// Sequence 2 then 0 leaves circuit 1 alone: 150 MW gives 154.18 MVA (above B 80) while the prescribed action (unit 0
/// down 90 MW, unit 1 up 90 MW) gives 60.25 MVA (within B). Actions ramp at 10 MW/min: 10 minutes cover 90 MW, 5 do
/// not (shortfall 40 MW each).</para>
/// <para>Tolerance: 10x the Newton mismatch times base MVA, as the W1 analytical tests.</para>
/// </summary>
public sealed class CorrectiveSequenceRunnerTests
{
    private static readonly double PowerTol = 10 * ContingencyAnalysis.StudyOptions.Tolerance * 100;

    internal static GridModel Case(double x = .3, double g0 = 150, double g1 = 30, double pd2 = 180, double rateA = 60) => new()
    {
        BaseMva = 100,
        Buses = [new Bus(1, BusType.Pv), new Bus(2, BusType.Pv, Pd: pd2), new Bus(3, BusType.Slack, Pd: 20)],
        Generators = [new Generator(1, g0, Qmin: -200, Qmax: 200, Pmin: 0, Pmax: 250, Name: "remote"),
            new Generator(2, g1, Qmin: -200, Qmax: 200, Pmin: 0, Pmax: 150, Name: "local"),
            new Generator(3, 20, Qmin: -200, Qmax: 200, Pmin: 0, Pmax: 100, Name: "slack")],
        Branches = [new Branch(1, 2, 0, x, RateAMva: rateA, RateBMva: 80, RateCMva: 120, Name: "c0"),
            new Branch(1, 2, 0, x, RateAMva: rateA, RateBMva: 80, RateCMva: 120, Name: "c1"),
            new Branch(1, 2, 0, x, RateAMva: rateA, RateBMva: 65, RateCMva: 70, Name: "c2"),
            new Branch(2, 3, 0, .05, RateAMva: 100, RateBMva: 100, RateCMva: 100, Name: "tie")]
    };

    internal static CorrectiveActionPolicy Policy(double minutes = 10) => new(
    [
        new GeneratorActionEnvelope(0, 1, ActionDirection.Down, 0, 200, 100, 10, 15),
        new GeneratorActionEnvelope(1, 2, ActionDirection.Up, 0, 150, 100, 10, 15),
    ], minutes);

    private static readonly GeneratorAction[] Corrective = [new(0, 1, -90), new(1, 2, 90)];
    private static readonly BranchOutage C0 = new(0, 1, 2), C1 = new(1, 1, 2), C2 = new(2, 1, 2), Tie = new(3, 2, 3);

    private static CorrectiveSequence S(BranchOutage first, BranchOutage second, CorrectiveProposal proposal,
        GeneratorAction[]? actions = null) => new(first, second, proposal, actions);

    private static CorrectiveSequenceRequest Request(CorrectiveActionPolicy policy, params CorrectiveSequence[] sequences) =>
        new(sequences, .95, 1.05, PostFaultRating.RateC, PostFaultRating.RateB, PostFaultRating.RateB, policy);

    /// <summary>Closed-form |S| (MVA) of one of <paramref name="n"/> identical lossless circuits sharing
    /// <paramref name="pMw"/> between two 1.0 pu buses.</summary>
    private static double Mva(double pMw, int n, double x = .3)
    {
        double s = pMw / 100 / n * x;
        return Math.Sqrt(2 * (1 - Math.Sqrt(1 - s * s))) / x * 100;
    }

    private static LimitEvidence Row(SecurityState s, string kind, int index) =>
        Assert.Single(s.Limits, l => l.Kind == kind && l.Index == index);

    private static void AssertBlockedAfter(CorrectiveSequenceResult r, int failedStep)
    {
        Assert.Equal(CorrectiveSequenceRunner.StepOrder, r.Steps.Select(s => s.Name));
        Assert.Equal(Enumerable.Range(0, 9), r.Steps.Select(s => s.Order));
        Assert.Equal(failedStep, r.FirstFailedStep);
        Assert.Equal("Failed", r.Steps[failedStep].Status);
        Assert.All(r.Steps.Take(failedStep), s => Assert.Equal("Completed", s.Status));
        Assert.All(r.Steps.Skip(failedStep + 1), s =>
        {
            Assert.Equal("Blocked", s.Status);
            Assert.Contains($"step {failedStep} ({CorrectiveSequenceRunner.StepOrder[failedStep]}) failed", s.Reason);
            Assert.Null(s.State);
        });
    }

    [Fact]
    public void Oracle_values_used_by_the_fixture()
    {
        Assert.InRange(Mva(150, 3), 50.1, 50.2);
        Assert.InRange(Mva(150, 2), 75.4, 75.5);
        Assert.InRange(Mva(150, 1), 154.1, 154.2);
        Assert.InRange(Mva(60, 1), 60.2, 60.3);
    }

    [Fact]
    public void Sequence_is_secure_only_with_the_explicitly_allowed_corrective_action()
    {
        var result = CorrectiveSequenceRunner.Run(Case(), Request(Policy(),
            S(C2, C0, CorrectiveProposal.None),
            S(C2, C0, CorrectiveProposal.Prescribed, Corrective)));

        Assert.Equal("SECURE", result.Intact.Disposition);
        Assert.Equal("NOT SECURE", result.Status);
        Assert.Equal("Violations", result.Outcome);
        Assert.Equal(new CorrectiveSequenceCounts(2, 2, 2, 1, 0), result.Counts);
        Assert.Equal([new UnitSetpoint(0, 1, 150), new UnitSetpoint(1, 2, 30), new UnitSetpoint(2, 3, 20)], result.InitialDispatch);

        // Without action: every gate passes until the final state, where the last circuit is over its B rating.
        CorrectiveSequenceResult none = result.Sequences[0];
        Assert.Equal("Violations", none.Outcome);
        AssertBlockedAfter(none, 8);
        Assert.Equal("VIOLATIONx2", none.TerminalDisposition);
        Assert.Empty(none.Steps[3].Proposed!);
        Assert.Empty(none.Steps[5].Applied!);
        SecurityState unrelieved = none.Steps[8].State!;
        foreach (string end in new[] { "BranchFrom", "BranchTo" })
        {
            LimitEvidence row = Row(unrelieved, end, 1);
            Assert.InRange(Math.Abs(row.Observed - Mva(150, 1)), 0, PowerTol);
            Assert.Equal(80, row.Upper);
            Assert.Equal("B", row.Source);
            Assert.True(row.Violated);
        }

        // With the prescribed action: every step completes and the reviewer can rebuild the whole sequence.
        CorrectiveSequenceResult fixedUp = result.Sequences[1];
        Assert.Equal("Verified", fixedUp.Outcome);
        Assert.Equal("SECURE", fixedUp.Status);
        Assert.Null(fixedUp.FirstFailedStep);
        Assert.All(fixedUp.Steps, s => Assert.Equal("Completed", s.Status));
        Assert.Equal([2], fixedUp.Steps[1].OutagesApplied);
        SecurityState first = fixedUp.Steps[2].State!;
        Assert.Equal(2, first.OutageIndex);
        foreach (int c in new[] { 0, 1 })
        {
            LimitEvidence row = Row(first, "BranchFrom", c);
            Assert.InRange(Math.Abs(row.Observed - Mva(150, 2)), 0, PowerTol);
            Assert.Equal(120, row.Upper);
            Assert.Equal("C", row.Source);
        }
        Assert.Empty(fixedUp.Steps[2].TemporaryEmergencyLoading!);   // 75.5 MVA is within B 80
        Assert.Equal(Corrective, fixedUp.Steps[3].Proposed);
        Assert.All(fixedUp.Steps[4].Checks!, c =>
        {
            Assert.True(c.Accepted);
            Assert.Equal(100, c.CapMw);
            Assert.Equal(9, c.RequiredMinutes);
            Assert.Null(c.ShortfallMw);
        });
        Assert.InRange(Math.Abs(fixedUp.Steps[4].Checks![0].RealisedStartMw!.Value - 150), 0, PowerTol);
        Assert.Equal([new SetpointChange(0, 1, 150, 60), new SetpointChange(1, 2, 30, 120)], fixedUp.Steps[5].Applied);
        Assert.Equal([2], fixedUp.Steps[5].OutagesApplied);
        SecurityState intermediate = fixedUp.Steps[6].State!;
        Assert.InRange(Math.Abs(Row(intermediate, "BranchFrom", 0).Observed - Mva(60, 2)), 0, PowerTol);
        Assert.InRange(Math.Abs(Row(intermediate, "GeneratorP", 1).Observed - 120), 0, PowerTol);
        Assert.InRange(Math.Abs(Row(intermediate, "GeneratorP", 2).Observed - 20), 0, PowerTol);   // slack: lossless
        Assert.Equal([2, 0], fixedUp.Steps[7].OutagesApplied);
        SecurityState final = fixedUp.Steps[8].State!;
        Assert.Equal("SECURE", final.Disposition);
        Assert.Equal(0, final.OutageIndex);
        foreach (string end in new[] { "BranchFrom", "BranchTo" })
        {
            LimitEvidence row = Row(final, end, 1);
            Assert.InRange(Math.Abs(row.Observed - Mva(60, 1)), 0, PowerTol);
            Assert.Equal(80, row.Upper);
        }
        // Both outages are retained in the final projection: their rows carry no thermal evidence.
        Assert.DoesNotContain(final.Limits, l => l.Kind.StartsWith("Branch", StringComparison.Ordinal) && l.Index is 0 or 2);
        Assert.InRange(Math.Abs(Row(final, "GeneratorP", 0).Observed - 60), 0, PowerTol);
    }

    [Fact]
    public void Halved_window_exhausts_the_ramp_budget_and_leaves_the_sequence_unverified()
    {
        var result = CorrectiveSequenceRunner.Run(Case(), Request(Policy(minutes: 5),
            S(C2, C0, CorrectiveProposal.Prescribed, Corrective)));

        CorrectiveSequenceResult r = Assert.Single(result.Sequences);
        Assert.Equal("NOT SECURE", result.Status);
        Assert.Equal("ActionRejected", result.Outcome);
        Assert.Equal("ActionRejected", r.Outcome);
        AssertBlockedAfter(r, 4);
        Assert.Contains("no partial subset", r.Steps[4].Reason);
        Assert.All(r.Steps[4].Checks!, c =>
        {
            Assert.False(c.Accepted);
            Assert.Equal(5, c.AvailableMinutes);
            Assert.Equal(50, c.CapMw);
            Assert.Equal(40, c.ShortfallMw);   // exact: 90 MW requested, 10 MW/min x 5 min available
            Assert.Equal(9, c.RequiredMinutes);
            Assert.Contains("ramp capacity 50 MW", Assert.Single(c.Reasons));
        });
        Assert.Equal(new CorrectiveSequenceCounts(1, 1, 0, 0, 0), result.Counts);
    }

    [Fact]
    public void Order_changes_the_outcome_and_a_prohibited_first_state_blocks_every_later_step()
    {
        var result = CorrectiveSequenceRunner.Run(Case(), Request(Policy(),
            S(C0, C2, CorrectiveProposal.Prescribed, Corrective),
            S(C2, C0, CorrectiveProposal.Prescribed, Corrective)));

        CorrectiveSequenceResult ab = result.Sequences[0], ba = result.Sequences[1];
        Assert.Equal((C0, C2), (ab.First, ab.Second));
        Assert.Equal((C2, C0), (ba.First, ba.Second));
        // Losing circuit 0 first puts 75.5 MVA on circuit 2, above its 70 MVA first-stage rating: prohibited.
        Assert.Equal("Violations", ab.Outcome);
        AssertBlockedAfter(ab, 2);
        Assert.Contains("Prohibited first-stage state", ab.Steps[2].Reason);
        LimitEvidence over = Row(ab.Steps[2].State!, "BranchFrom", 2);
        Assert.InRange(Math.Abs(over.Observed - Mva(150, 2)), 0, PowerTol);
        Assert.Equal(70, over.Upper);
        Assert.True(over.Violated);
        Assert.Equal("Verified", ba.Outcome);
        Assert.Equal(new CorrectiveSequenceCounts(2, 2, 1, 1, 0), result.Counts);
    }

    [Fact]
    public void Temporary_emergency_loading_is_reported_separately_from_a_prohibited_state()
    {
        var request = new CorrectiveSequenceRequest([S(C2, C0, CorrectiveProposal.Prescribed, Corrective)], .95, 1.05,
            PostFaultRating.RateC, PostFaultRating.RateA, PostFaultRating.RateB, Policy());
        CorrectiveSequenceResult r = Assert.Single(CorrectiveSequenceRunner.Run(Case(), request).Sequences);

        Assert.Equal("Verified", r.Outcome);
        Assert.Equal("Completed", r.Steps[2].Status);
        TemporaryLoading[] loading = r.Steps[2].TemporaryEmergencyLoading!.ToArray();
        Assert.Equal(4, loading.Length);   // circuits 0 and 1, both ends: above A 60, within C 120
        Assert.All(loading, t =>
        {
            Assert.Contains(t.Branch, new[] { 0, 1 });
            Assert.InRange(Math.Abs(t.ObservedMva - Mva(150, 2)), 0, PowerTol);
            Assert.Equal((120, "C", 60, "A"), (t.StageRatingMva, t.StageSource, t.ContinuousRatingMva, t.ContinuousSource));
        });
        Assert.Equal("A", Row(r.Steps[6].State!, "BranchFrom", 0).Source);
    }

    [Fact]
    public void Islanding_second_outage_is_incomplete_and_not_solved()
    {
        var result = CorrectiveSequenceRunner.Run(Case(), Request(Policy(),
            S(C2, Tie, CorrectiveProposal.Prescribed, Corrective)));
        CorrectiveSequenceResult r = Assert.Single(result.Sequences);
        Assert.Equal("Incomplete", r.Outcome);
        Assert.Equal("ISLANDED", r.TerminalDisposition);
        AssertBlockedAfter(r, 7);
        Assert.Equal([2, 3], r.Steps[7].OutagesApplied);
        Assert.StartsWith("ISLANDED", r.Steps[7].Reason);
        Assert.Equal("Incomplete", result.Outcome);
        Assert.Equal(new CorrectiveSequenceCounts(1, 1, 0, 0, 1), result.Counts);
    }

    [Fact]
    public void Ineligible_wrong_direction_and_unbalanced_proposals_are_rejected_whole()
    {
        var result = CorrectiveSequenceRunner.Run(Case(), Request(Policy(),
            S(C2, C0, CorrectiveProposal.Prescribed, [new(0, 1, -90), new(2, 3, 90)]),
            S(C2, C0, CorrectiveProposal.Prescribed, [new(0, 1, 10), new(1, 2, -10)]),
            S(C2, C0, CorrectiveProposal.Prescribed, [new(0, 1, -90), new(1, 2, 80)])));

        CorrectiveSequenceResult slack = result.Sequences[0];
        AssertBlockedAfter(slack, 4);
        Assert.True(slack.Steps[4].Checks![0].Accepted);
        ActionCheck refused = slack.Steps[4].Checks![1];
        Assert.False(refused.Accepted);
        Assert.Null(refused.CapMw);
        Assert.Contains("slack bus", Assert.Single(refused.Reasons));
        Assert.Contains("1 of 2", slack.Steps[4].Reason);

        CorrectiveSequenceResult direction = result.Sequences[1];
        AssertBlockedAfter(direction, 4);
        Assert.All(direction.Steps[4].Checks!, c => Assert.Contains("is not permitted", Assert.Single(c.Reasons)));

        CorrectiveSequenceResult unbalanced = result.Sequences[2];
        AssertBlockedAfter(unbalanced, 4);
        Assert.All(unbalanced.Steps[4].Checks!, c => Assert.True(c.Accepted));
        Assert.Contains("Unbalanced proposal", unbalanced.Steps[4].Reason);
        Assert.Contains("-10 MW", unbalanced.Steps[4].Reason);
        Assert.All(result.Sequences, s => Assert.Equal("ActionRejected", s.Outcome));
    }

    [Fact]
    public void Unsolvable_intermediate_state_cannot_be_bypassed_by_the_second_outage()
    {
        // Weak circuits (X = 1.0): the first outage leaves 75 MW per circuit (solvable), but the prescribed increase
        // to 210 MW asks 105 MW of each remaining circuit, beyond its 100 MW static transfer limit 1/X.
        var policy = new CorrectiveActionPolicy([new(0, 1, ActionDirection.Both, 0, 250, 100, 10, 15),
            new(1, 2, ActionDirection.Both, 0, 150, 100, 10, 15)], 10);
        var result = CorrectiveSequenceRunner.Run(Case(x: 1, g1: 80, pd2: 230), Request(policy,
            S(C2, C0, CorrectiveProposal.Prescribed, [new(0, 1, 60), new(1, 2, -60)])));
        CorrectiveSequenceResult r = Assert.Single(result.Sequences);
        Assert.Equal("Completed", r.Steps[2].Status);
        Assert.Equal("Incomplete", r.Outcome);
        Assert.Equal("NO SOLUTION", r.TerminalDisposition);
        AssertBlockedAfter(r, 6);
        Assert.NotEqual(PowerFlowStatus.Converged, r.Steps[6].State!.SolverStatus);
        Assert.Empty(r.Steps[6].State!.Limits);
    }

    [Fact]
    public void Unsolvable_first_state_blocks_actions_and_both_later_solves()
    {
        var result = CorrectiveSequenceRunner.Run(Case(x: 1, g0: 210, g1: 20, pd2: 230, rateA: 100), Request(Policy(),
            S(C2, C0, CorrectiveProposal.Prescribed, Corrective)));
        Assert.Equal("SECURE", result.Intact.Disposition);
        CorrectiveSequenceResult r = Assert.Single(result.Sequences);
        Assert.Equal("NO SOLUTION", r.TerminalDisposition);
        AssertBlockedAfter(r, 2);
        Assert.Null(r.Steps[3].Proposed);
        Assert.Equal("Incomplete", result.Outcome);
    }

    [Fact]
    public void Insecure_intact_base_blocks_every_sequence_but_keeps_every_row()
    {
        var result = CorrectiveSequenceRunner.Run(Case(x: 1, g0: 210, g1: 20, pd2: 230), Request(Policy(),
            S(C2, C0, CorrectiveProposal.None), S(C0, C2, CorrectiveProposal.None)));
        Assert.StartsWith("VIOLATIONx", result.Intact.Disposition);
        Assert.Equal("Violations", result.Outcome);
        Assert.NotNull(result.Reason);
        Assert.All(result.Sequences, r =>
        {
            Assert.Equal("Blocked", r.Outcome);
            AssertBlockedAfter(r, 0);
        });
        Assert.Equal(new CorrectiveSequenceCounts(2, 0, 0, 0, 2), result.Counts);
    }

    [Fact]
    public void Model_outside_the_preventive_envelope_is_unevaluated_not_passed()
    {
        GridModel model = Case();
        model = model.CopyWith(branches: [.. model.Branches.Take(3), model.Branches[3] with { RateAMva = 0 }]);
        var result = CorrectiveSequenceRunner.Run(model, Request(Policy(), S(C2, C0, CorrectiveProposal.None)));
        Assert.Equal("UNEVALUATED", result.Intact.Disposition);
        Assert.Equal("Incomplete", result.Outcome);
        AssertBlockedAfter(Assert.Single(result.Sequences), 0);
    }

    [Fact]
    public void Planned_redispatch_is_only_a_candidate_checked_against_the_policy()
    {
        var result = CorrectiveSequenceRunner.Run(Case(), Request(Policy(), S(C2, C0, CorrectiveProposal.PlannedRedispatch)));
        CorrectiveSequenceResult r = Assert.Single(result.Sequences);
        SequenceStep proposal = r.Steps[3];
        Assert.Equal("Completed", proposal.Status);
        Assert.Contains("PlanCorrectiveRedispatch: Applied", proposal.ProposalDiagnostic);
        Assert.NotEmpty(proposal.Proposed!);
        Assert.InRange(Math.Abs(proposal.Proposed!.Sum(a => a.DeltaMw)), 0, 1e-6);
        Assert.Equal(proposal.Proposed!.Count, r.Steps[4].Checks!.Count);
        // The DC planner relieves the first-stage RATE_A overload only, so the sequence is not verified.
        Assert.Equal("NOT SECURE", r.Status);
    }

    [Fact]
    public void Repeat_runs_are_bit_identical_and_the_input_model_is_unchanged()
    {
        GridModel model = Case();
        Generator[] generators = model.Generators.ToArray();
        Branch[] branches = model.Branches.ToArray();
        CorrectiveSequenceRequest request = Request(Policy(),
            S(C2, C0, CorrectiveProposal.Prescribed, Corrective), S(C0, C2, CorrectiveProposal.None),
            S(C2, Tie, CorrectiveProposal.Prescribed, Corrective), S(C2, C0, CorrectiveProposal.PlannedRedispatch));
        var options = new JsonSerializerOptions { Converters = { new JsonStringEnumConverter() },
            NumberHandling = JsonNumberHandling.AllowNamedFloatingPointLiterals };
        string a = JsonSerializer.Serialize(CorrectiveSequenceRunner.Run(model, request), options);
        string b = JsonSerializer.Serialize(CorrectiveSequenceRunner.Run(model, request), options);
        Assert.Equal(a, b);
        Assert.Equal(generators, model.Generators);
        Assert.Equal(branches, model.Branches);
    }

    [Fact]
    public void Invalid_requests_are_refused_before_any_solve()
    {
        GridModel model = Case();
        void Refused(CorrectiveSequenceRequest request) =>
            Assert.ThrowsAny<ArgumentException>(() => CorrectiveSequenceRunner.Run(model, request));
        Refused(Request(Policy()));
        Refused(Request(Policy(), S(C0, C0, CorrectiveProposal.None)));
        Refused(Request(Policy(), S(new(0, 2, 1), C2, CorrectiveProposal.None)));
        Refused(Request(Policy(), S(new(9, 1, 2), C2, CorrectiveProposal.None)));
        Refused(Request(Policy(), S(C0, C2, CorrectiveProposal.Prescribed)));
        Refused(Request(Policy(), S(C0, C2, CorrectiveProposal.None, Corrective)));
        Refused(Request(Policy(), S(C0, C2, CorrectiveProposal.Prescribed, [new(0, 1, double.NaN)])));
        Refused(Request(Policy(), S(C0, C2, CorrectiveProposal.None), S(C0, C2, CorrectiveProposal.None)));
        Refused(Request(new([new(2, 3, ActionDirection.Up, 0, 100, 10, 1, 1)], 10), S(C0, C2, CorrectiveProposal.None)));
        Refused(Request(new([new(0, 2, ActionDirection.Up, 0, 100, 10, 1, 1)], 10), S(C0, C2, CorrectiveProposal.None)));
        Refused(Request(new([new(0, 1, ActionDirection.Up, 100, 0, 10, 1, 1)], 10), S(C0, C2, CorrectiveProposal.None)));
        Refused(Request(Policy(minutes: -1), S(C0, C2, CorrectiveProposal.None)));
        Refused(new CorrectiveSequenceRequest([S(C0, C2, CorrectiveProposal.None)], 1.1, 0.9,
            PostFaultRating.RateA, PostFaultRating.RateA, PostFaultRating.RateA, Policy()));
        Model_is_still_usable(model);
    }

    private static void Model_is_still_usable(GridModel model) =>
        Assert.Equal("SECURE", CorrectiveSequenceRunner.Run(model, Request(Policy(),
            S(C2, C0, CorrectiveProposal.Prescribed, Corrective))).Status);
}
