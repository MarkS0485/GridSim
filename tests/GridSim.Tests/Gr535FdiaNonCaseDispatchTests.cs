using GridSim.Core.Estimation;
using GridSim.Core.Estimation.Security;
using GridSim.Core.IO;
using GridSim.Core.Model;
using GridSim.Core.PowerFlow;
using Xunit;

namespace GridSim.Tests;

/// <summary>
/// GR-535: the FDIA screen's tests all used telemetry sampled from the case file's own dispatch. Real telemetry differs
/// from the case dispatch by megawatts at many buses; the screen audits KCL against the estimate's zero-injection rows,
/// not against the case file's Pg - Pd, so a correct estimate of a different dispatch must not read "impossible". These
/// pin that, with exact telemetry from an 8 % heavier dispatch, screened against the ORIGINAL case, and with co-located
/// meters of different sigma at every bus (the screen keeps the smallest, GR-077).
/// </summary>
public class Gr535FdiaNonCaseDispatchTests
{
    private static (GridModel CaseModel, EstimationResult Estimate) HeavierDispatch(bool colocated)
    {
        GridModel model = IeeeCases.Case9();
        int slack = model.Buses.Single(b => b.Type == BusType.Slack).Id;
        // The real operating point: every load and every non-slack unit 8 % above the case file.
        GridModel actual = model.CopyWith(
            buses: model.Buses.Select(b => b with { Pd = b.Pd * 1.08, Qd = b.Qd * 1.08 }).ToList(),
            generators: model.Generators.Select(g => g.Bus == slack ? g : g with { Pg = g.Pg * 1.08 }).ToList());
        PowerFlowResult truth = new NewtonRaphsonPowerFlow().Solve(actual);
        Assert.True(truth.Converged);
        MeasurementSet exact = WlsEstimatorTests.ExactFullSet(actual, truth);
        var rows = exact.Measurements.ToList();
        if (colocated)
            // A second, looser voltage meter at every bus (same value, 10x the sigma).
            rows.AddRange(exact.Measurements.Where(m => m.Kind == MeasurementKind.VmPu)
                .Select(m => m with { Sigma = m.Sigma * 10.0, Source = "second-meter" }).ToList());
        var set = new MeasurementSet { Case = exact.Case, BaseMva = exact.BaseMva, TakenUtc = exact.TakenUtc, Measurements = rows };
        // Estimated against the CASE model, whose stored dispatch is not what the telemetry saw.
        EstimationResult est = new WlsStateEstimator().Estimate(model, set);
        Assert.True(est.Converged, $"estimate status {est.Status}");
        return (model, est);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Gr535_a_correct_estimate_of_a_different_dispatch_is_not_physically_impossible(bool colocated)
    {
        (GridModel model, EstimationResult est) = HeavierDispatch(colocated);
        FdiaScreenReport report = FdiaScreen.Screen(model, est);
        Assert.True(report.ChiSquarePassed, report.Summary);
        Assert.False(report.PhysicallyImpossible, report.Summary);
        Assert.False(report.SuspectedFdia, report.Summary);
    }
}
