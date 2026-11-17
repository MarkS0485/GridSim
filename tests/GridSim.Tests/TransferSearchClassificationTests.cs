using GridSim.Core.Analysis.TransmissionSecurity;
using Xunit;

namespace GridSim.Tests;

/// <summary>
/// RC3 P4 search logic with an injected evaluator (no physics): ordered feasible / violating / feasible trials,
/// an unknown first upper trial, control-state and binding changes, repeat-solve disagreement and budget accounting.
/// Each case must stay distinct from a verified physical limit.
/// </summary>
public sealed class TransferSearchClassificationTests
{
    private static TransferEvaluation E(TransferClass c, string signature = "", string kind = "Voltage", string? fingerprint = null) =>
        new(c, c == TransferClass.Violation ? [new TransferLimit(0, kind, 2, "pu", .8, .9, -.1, "lower", null)] : [],
            signature, c == TransferClass.Unknown ? "injected NO SOLUTION" : null, fingerprint ?? c + "/" + signature + "/" + kind);

    private static TransferSearchOptions Options(double max = 100, double step = 10, double tol = .5, int trials = 100, int probes = 1) =>
        new(max, step, tol, trials, probes);

    [Fact]
    public void Feasible_violating_feasible_is_qualified_non_monotone_and_keeps_the_region_connected_to_zero()
    {
        var o = TransferSearch.Run(t => E(t < 20 ? TransferClass.Feasible : t < 30 ? TransferClass.Violation : TransferClass.Feasible), Options());

        Assert.Equal("QUALIFIED", o.Status);
        Assert.Equal("Limited", o.Outcome);
        Assert.Equal("NonMonotone", Assert.Single(o.Qualifications).Code);
        Assert.Equal(19.6875, o.Lower!.TransferMw);
        Assert.Equal(20, o.Upper!.TransferMw);
        Assert.Equal(new double[] { 0, 10, 20, 30, 15, 17.5, 18.75, 19.375, 19.6875, 19.6875, 20 }, o.Trials.Select(t => t.TransferMw));
    }

    [Fact]
    public void Unknown_first_upper_trial_gives_a_verified_lower_bound_but_no_limit()
    {
        var o = TransferSearch.Run(t => E(t < 25 ? TransferClass.Feasible : TransferClass.Unknown), Options());

        Assert.Equal("INCOMPLETE", o.Status);
        Assert.Equal("UnknownAbove", o.Outcome);
        Assert.Equal(20, o.Lower!.TransferMw);
        Assert.Equal(TransferClass.Unknown, o.Upper!.Evaluation.Class);
        Assert.Equal(30, o.Upper.TransferMw);
        Assert.Equal(0, o.Accounting.RefinementTrials);
        Assert.Equal(0, o.Accounting.ProbesEvaluated);
    }

    [Fact]
    public void Unknown_midpoint_stops_refinement_and_qualifies_the_bracket()
    {
        var o = TransferSearch.Run(t => E(t < 15 ? TransferClass.Feasible : t < 20 ? TransferClass.Unknown : TransferClass.Violation), Options());
        Assert.Equal("QUALIFIED", o.Status);
        Assert.Equal("UnknownInBracket", Assert.Single(o.Qualifications).Code);
        Assert.Equal((10, 20), (o.Lower!.TransferMw, o.Upper!.TransferMw));
    }

    [Fact]
    public void Control_state_change_within_the_sampled_region_qualifies_the_bound()
    {
        var o = TransferSearch.Run(t => t < 40 ? E(TransferClass.Feasible)
            : t < 55 ? E(TransferClass.Feasible, "outage 0:2=Pq") : E(TransferClass.Violation, "outage 0:2=Pq"), Options());
        Assert.Equal("QUALIFIED", o.Status);
        TransferQualification q = Assert.Single(o.Qualifications);
        Assert.Equal("ControlStateChange", q.Code);
        Assert.Contains("40 MW: outage 0:2=Pq", q.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public void Binding_limit_change_inside_the_bracket_is_reported()
    {
        var o = TransferSearch.Run(t => t < 55 ? E(TransferClass.Feasible)
            : t < 60 ? E(TransferClass.Violation, kind: "BranchFrom") : E(TransferClass.Violation), Options());
        Assert.Equal("QUALIFIED", o.Status);
        Assert.Equal("BindingLimitChanged", Assert.Single(o.Qualifications).Code);
        Assert.Equal("BranchFrom", o.Upper!.Evaluation.Violations[0].Kind);
    }

    [Fact]
    public void Disagreeing_endpoint_resolve_leaves_the_bracket_unverified()
    {
        var seen = new HashSet<double>();
        var o = TransferSearch.Run(t =>
        {
            bool repeat = !seen.Add(t);
            return E(t < 45 ? TransferClass.Feasible : TransferClass.Violation, fingerprint: repeat ? "changed" : null);
        }, Options());
        Assert.Equal("QUALIFIED", o.Status);
        Assert.Equal(2, o.Qualifications.Count(q => q.Code == "RepeatSolveMismatch"));
    }

    [Fact]
    public void Budget_exhaustion_before_any_violation_is_incomplete_with_exact_accounting()
    {
        var o = TransferSearch.Run(t => E(t < 55 ? TransferClass.Feasible : TransferClass.Violation), Options(trials: 4));
        Assert.Equal("INCOMPLETE", o.Status);
        Assert.Equal("BudgetExhausted", o.Outcome);
        Assert.Equal(30, o.Lower!.TransferMw);
        Assert.Null(o.Upper);
        Assert.Equal(new TransferAccounting(4, 1, 10, 3, 7, 0, 0, 0, 0, 0), o.Accounting);
    }

    [Fact]
    public void Budget_exhaustion_during_refinement_counts_the_unevaluated_bisections()
    {
        var o = TransferSearch.Run(t => E(t < 55 ? TransferClass.Feasible : TransferClass.Violation), Options(trials: 8));
        Assert.Equal("QUALIFIED", o.Status);
        Assert.Equal("ToleranceNotReached", Assert.Single(o.Qualifications).Code);
        Assert.Equal((50, 60), (o.Lower!.TransferMw, o.Upper!.TransferMw));
        Assert.Equal(new TransferAccounting(8, 2, 10, 6, 0, 1, 0, 0, 5, 0), o.Accounting);
    }

    [Fact]
    public void Feasible_cap_is_at_least_the_cap_with_no_upper_bound()
    {
        var o = TransferSearch.Run(_ => E(TransferClass.Feasible), Options(max: 35));
        Assert.Equal("VERIFIED", o.Status);
        Assert.Equal("AtLeastMax", o.Outcome);
        Assert.Equal(new double[] { 0, 10, 20, 30, 35, 35 }, o.Trials.Select(t => t.TransferMw));
        Assert.Null(o.Upper);
    }

    [Fact]
    public void Violating_reference_is_not_reported_as_zero_headroom()
    {
        var o = TransferSearch.Run(_ => E(TransferClass.Violation), Options());
        Assert.Equal("NOT SECURE", o.Status);
        Assert.Equal("NoAdmissibleStart", o.Outcome);
        Assert.Null(o.Lower);
        var zeroMargin = TransferSearch.Run(t => E(t <= 0 ? TransferClass.Feasible : TransferClass.Violation), Options(tol: 10));
        Assert.Equal("VERIFIED", zeroMargin.Status);
        Assert.Equal(0, zeroMargin.Lower!.TransferMw);
    }
}
