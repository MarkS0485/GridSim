using GridSim.Core.Estimation;
using GridSim.Core.Estimation.Security;
using GridSim.Core.IO;
using GridSim.Core.Model;
using GridSim.Core.PowerFlow;
using Xunit;

namespace GridSim.Tests;

/// <summary>
/// GR-521 (an agreed behaviour change): Huber and Schweppe-GM reported their chi-square as a verdict although J at an
/// M-estimate is not the WLS minimum, so it is not chi-square(m - n) distributed under H0 (the reason LAV already gave
/// no verdict). They now report ChiSquareIsVerdict = false, and every consumer reads that: the FDIA screen refuses to
/// read it, the estimate export writes the no-verdict shape (without a LAV objective), and the audit line says
/// chi2=no-verdict. WLS and QR keep their verdict (Cf6LavExactTests). Only the flag moves; the estimate is unchanged.
/// </summary>
public class Gr521RobustChiSquareNoVerdictTests
{
    private static readonly SolveExportMeta Meta = new("case14", "cpu", "c", "2026-11-01T00:00:00Z", 1.0, "test");

    private static (GridModel Model, MeasurementSet Set) Case14()
    {
        GridModel model = IeeeCases.Case14();
        PowerFlowResult truth = new NewtonRaphsonPowerFlow().Solve(model);
        return (model, SyntheticMeasurements.Sample(model, truth, new SyntheticMeasurementOptions { Seed = 2 }));
    }

    private static EstimationResult Run(GridModel model, MeasurementSet set, EstimationMethod method) =>
        new WlsStateEstimator().Estimate(model, set, new EstimationOptions { Method = method });

    [Theory]
    [InlineData(EstimationMethod.Huber)]
    [InlineData(EstimationMethod.SchweppeGm)]
    public void Gr521_an_m_estimate_gives_no_chi_square_verdict_and_no_lav_objective(EstimationMethod method)
    {
        (GridModel model, MeasurementSet set) = Case14();
        EstimationResult e = Run(model, set, method);
        Assert.True(e.Converged);
        Assert.False(e.ChiSquareIsVerdict);
        Assert.Null(e.LavObjective);
    }

    [Theory]
    [InlineData(EstimationMethod.Huber)]
    [InlineData(EstimationMethod.SchweppeGm)]
    public void Gr521_the_fdia_screen_refuses_an_m_estimate(EstimationMethod method)
    {
        (GridModel model, MeasurementSet set) = Case14();
        EstimationResult e = Run(model, set, method);
        var ex = Assert.Throws<InvalidOperationException>(() => FdiaScreen.Screen(model, e));
        Assert.Contains("not a verdict", ex.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(EstimationMethod.Huber)]
    [InlineData(EstimationMethod.SchweppeGm)]
    public void Gr521_the_export_writes_the_no_verdict_shape_without_a_lav_objective(EstimationMethod method)
    {
        (GridModel model, MeasurementSet set) = Case14();
        EstimationResult e = Run(model, set, method);
        System.Text.Json.JsonElement est = System.Text.Json.JsonDocument.Parse(EstimateExport.ToJson(model, set, e, Meta))
            .RootElement.GetProperty("estimate");
        Assert.Equal(System.Text.Json.JsonValueKind.Null, est.GetProperty("chiSquarePassed").ValueKind);
        Assert.Equal(System.Text.Json.JsonValueKind.Null, est.GetProperty("chiSquareTest").ValueKind);
        Assert.False(est.GetProperty("chiSquareVerdict").GetBoolean());
        Assert.False(est.TryGetProperty("lavObjective", out _));
    }

    [Theory]
    [InlineData(EstimationMethod.Huber)]
    [InlineData(EstimationMethod.SchweppeGm)]
    public void Gr521_the_audit_line_says_no_verdict_without_an_objective(EstimationMethod method)
    {
        (GridModel model, MeasurementSet set) = Case14();
        string line = EstimationAuditTrail.Outcome(method.ToString(), Run(model, set, method), set.Measurements.Count);
        Assert.Contains(" chi2=no-verdict ", line, StringComparison.Ordinal);
        Assert.DoesNotContain("lav_objective", line, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(EstimationMethod.Constrained)]
    [InlineData(EstimationMethod.Feasible)]
    public void Gr521_the_constrained_least_squares_methods_keep_their_verdict(EstimationMethod method)
    {
        (GridModel model, MeasurementSet set) = Case14();
        Assert.True(Run(model, set, method).ChiSquareIsVerdict);
    }
}
