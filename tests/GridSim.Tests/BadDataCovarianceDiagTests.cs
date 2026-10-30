using GridSim.Core.Abstractions;
using GridSim.Core.Estimation;
using GridSim.Core.IO;
using GridSim.Core.Model;
using GridSim.Core.PowerFlow;
using Xunit;

namespace GridSim.Tests;

/// <summary>Pins the residual-covariance diagonal against a second, independent linear-solve
/// path (the validated <see cref="DenseLuSolver"/>, refactored per row): the factor-once LU inside
/// BadDataAnalysis must produce the same leverages, and a well-covered case14 set has no critical
/// measurements (regression for a false critical flag on QInj@bus5, found by se_residual_check.py).</summary>
public class BadDataCovarianceDiagTests
{
    [Fact]
    public void Residual_covariance_matches_an_independent_solve_path()
    {
        GridModel model = IeeeCases.Case14();
        PowerFlowResult truth = new NewtonRaphsonPowerFlow().Solve(model);
        MeasurementSet set = SyntheticMeasurements.Sample(model, truth);

        // Converged state without the bad-data loop.
        EstimationResult est = new WlsStateEstimator().Estimate(
            model, set, new EstimationOptions { DetectBadData = false });
        Assert.Equal(EstimationStatus.Converged, est.Status);

        var mm = new MeasurementModel(model, set);
        int n = model.BusCount;
        int m = mm.MeasurementCount;
        int s = mm.StateSize;
        var vm = new double[n];
        var va = new double[n];
        for (int i = 0; i < n; i++) { vm[i] = est.Vm[i]; va[i] = est.VaDeg[i] * Math.PI / 180.0; }

        var w = new double[m];
        for (int r = 0; r < m; r++) w[r] = 1.0 / (mm.SigmaPu[r] * mm.SigmaPu[r]);
        var h = new double[m];
        mm.EvaluateH(vm, va, h);
        var residualPu = new double[m];
        for (int r = 0; r < m; r++) residualPu[r] = mm.ZPu[r] - h[r];
        var jac = new double[m, s];
        mm.EvaluateJacobian(vm, va, jac);

        var gain = new double[s, s];
        for (int r = 0; r < m; r++)
            for (int c1 = 0; c1 < s; c1++)
            {
                if (jac[r, c1] == 0) continue;
                double wh = w[r] * jac[r, c1];
                for (int c2 = 0; c2 < s; c2++) gain[c1, c2] += wh * jac[r, c2];
            }

        var gainForAnalysis = new double[s, s];
        Array.Copy(gain, gainForAnalysis, gain.Length);
        (double[] normalized, bool[] critical) =
            BadDataAnalysis.NormalizedResiduals(mm, jac, gainForAnalysis, w, residualPu);

        // Independent path: refactor the untouched gain per measurement row with DenseLuSolver.
        var solver = new DenseLuSolver();
        var hRow = new double[s];
        for (int r = 0; r < m; r++)
        {
            for (int c = 0; c < s; c++) hRow[c] = jac[r, c];
            double[] t = solver.Solve(gain, hRow);
            double hGh = 0;
            for (int c = 0; c < s; c++) hGh += hRow[c] * t[c];
            double sigmaSq = 1.0 / w[r];
            double leverage = hGh * w[r];
            double omega = sigmaSq - hGh;

            Assert.True(leverage < 1.0 - 1e-6,
                $"row {r} ({mm.Measurements[r].Kind} @ {mm.Measurements[r].Describe()}): leverage {leverage} ~ 1 on a fully redundant set");
            Assert.False(critical[r],
                $"row {r} ({mm.Measurements[r].Kind} @ {mm.Measurements[r].Describe()}): flagged critical but independent leverage is {leverage}");
            double rnIndependent = Math.Abs(residualPu[r]) / Math.Sqrt(omega);
            Assert.True(Math.Abs(normalized[r] - rnIndependent) < 1e-6 * Math.Max(1.0, rnIndependent),
                $"row {r}: normalized residual {normalized[r]} vs independent {rnIndependent}");
        }
    }
}
