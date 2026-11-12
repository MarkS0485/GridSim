using GridSim.Core.Estimation;
using GridSim.Core.Estimation.Security;
using GridSim.Core.IO;
using GridSim.Core.Model;
using GridSim.Core.PowerFlow;
using Xunit;

namespace GridSim.Tests;

/// <summary>
/// Static bug hunt of 2026-11-01: #189, the FDIA screen keeps the SMALLEST sigma for branch-flow rows that measure the
/// same line (GR-077, fail towards flagging), as it already did for bus rows.
/// </summary>
public sealed partial class BugHunt1101WebTests
{
    // Case 9 with exact telemetry (sigma 1.6 MW / MVAr on every flow) plus a LOOSE second meter (sigma 40) at both ends of
    // one line, with the same circuit label. Returns the model with that line's rating set to its projected |S| minus
    // breachMva, and the estimate.
    private static (GridModel Model, EstimationResult Estimate, int From, int To) RatingBreach(double breachMva)
    {
        GridModel model = IeeeCases.Case9();
        PowerFlowResult truth = new NewtonRaphsonPowerFlow().Solve(model);
        MeasurementSet exact = WlsEstimatorTests.ExactFullSet(model, truth);
        LineFlow line = truth.LineFlows[0];
        string? circuit = string.IsNullOrEmpty(line.Name) ? null : line.Name;
        var rows = exact.Measurements.ToList();
        rows.Add(new Measurement(MeasurementKind.PFlowMw, line.PFromMw, 40, From: line.From, To: line.To, Circuit: circuit, Source: "pseudo"));
        rows.Add(new Measurement(MeasurementKind.QFlowMvar, line.QFromMvar, 40, From: line.From, To: line.To, Circuit: circuit, Source: "pseudo"));
        rows.Add(new Measurement(MeasurementKind.PFlowMw, line.PToMw, 40, From: line.From, To: line.To, End: BranchEnd.To, Circuit: circuit, Source: "pseudo"));
        rows.Add(new Measurement(MeasurementKind.QFlowMvar, line.QToMvar, 40, From: line.From, To: line.To, End: BranchEnd.To, Circuit: circuit, Source: "pseudo"));
        var set = new MeasurementSet { Case = exact.Case, BaseMva = exact.BaseMva, TakenUtc = exact.TakenUtc, Measurements = rows };
        EstimationResult est = new WlsStateEstimator().Estimate(model, set);
        Assert.True(est.Converged);

        // Read the projected |S| on that line from a screen with a 1 MVA rating, then rate the line just below it.
        int k = Enumerable.Range(0, model.Branches.Count).First(i => model.Branches[i].From == line.From && model.Branches[i].To == line.To);
        GridModel probe = model.CopyWith(branches: model.Branches.Select((b, i) => i == k ? b with { RateAMva = 1 } : b).ToList());
        double s = FdiaScreen.Screen(probe, est).LimitViolations.Single(v => v.Kind == "Rating" && v.A == line.From && v.B == line.To).Value;
        GridModel rated = model.CopyWith(branches: model.Branches.Select((b, i) => i == k ? b with { RateAMva = s - breachMva } : b).ToList());
        return (rated, est, line.From, line.To);
    }

    [Fact]
    public void B189_a_loose_second_meter_on_a_line_no_longer_widens_the_margin_and_hides_a_rating_breach()
    {
        // A 10 MVA breach is about 6 x the tight meters' 1.6 sigma, but well inside 3 x the loose meter's 40. With the
        // largest sigma kept (the bug), the breach was "not counted"; with the smallest it is counted, as for bus rows.
        (GridModel model, EstimationResult est, int from, int to) = RatingBreach(10);
        FdiaScreenReport report = FdiaScreen.Screen(model, est);
        Assert.Contains(report.LimitViolations, v => v.Kind == "Rating" && v.A == from && v.B == to);
        Assert.Contains(report.SignificantLimitViolations, v => v.Kind == "Rating" && v.A == from && v.B == to);
    }

    [Fact]
    public void B189_a_breach_inside_the_tight_meters_noise_is_still_not_counted()
    {
        // 1 MVA is inside 3 x 1.6: listed, not counted (the margin rule itself is unchanged).
        (GridModel model, EstimationResult est, int from, int to) = RatingBreach(1);
        FdiaScreenReport report = FdiaScreen.Screen(model, est);
        Assert.Contains(report.LimitViolations, v => v.Kind == "Rating" && v.A == from && v.B == to);
        Assert.DoesNotContain(report.SignificantLimitViolations, v => v.Kind == "Rating" && v.A == from && v.B == to);
    }
}
