using System;
using GridSim.Core.Estimation;
using GridSim.Core.IO;
using GridSim.Core.Model;
using GridSim.Core.PowerFlow;
using Xunit;

namespace GridSim.Tests;

/// <summary>
/// The per-state error covariance diag(G^-1) - the estimator's own confidence in each state, and the
/// foundation for real-time confidence bounds and (later) a recursive filter's prior covariance. The math
/// helper is checked against a brute-force full inverse to machine precision; the estimator integration is
/// checked for finite, positive, sensibly-referenced bounds.
/// </summary>
public class StateCovarianceTests
{
    // Brute-force inverse via Gauss-Jordan (test-independent of the production LU) for the diagonal check.
    private static double[,] Inverse(double[,] a, int n)
    {
        var m = new double[n, 2 * n];
        for (int i = 0; i < n; i++)
        {
            for (int j = 0; j < n; j++) m[i, j] = a[i, j];
            m[i, n + i] = 1.0;
        }
        for (int col = 0; col < n; col++)
        {
            int piv = col;
            for (int r = col + 1; r < n; r++)
                if (Math.Abs(m[r, col]) > Math.Abs(m[piv, col])) piv = r;
            for (int j = 0; j < 2 * n; j++) (m[col, j], m[piv, j]) = (m[piv, j], m[col, j]);
            double d = m[col, col];
            for (int j = 0; j < 2 * n; j++) m[col, j] /= d;
            for (int r = 0; r < n; r++)
            {
                if (r == col) continue;
                double f = m[r, col];
                if (f == 0) continue;
                for (int j = 0; j < 2 * n; j++) m[r, j] -= f * m[col, j];
            }
        }
        var inv = new double[n, n];
        for (int i = 0; i < n; i++)
            for (int j = 0; j < n; j++) inv[i, j] = m[i, n + j];
        return inv;
    }

    [Fact]
    public void Covariance_diagonal_matches_a_brute_force_inverse_to_machine_precision()
    {
        // A fixed symmetric-positive-definite gain G = A^T A + 2I (deterministic, well-conditioned).
        const int n = 4;
        double[,] a =
        {
            { 1.0, 0.5, 0.0, 0.2 },
            { 0.3, 1.2, 0.4, 0.0 },
            { 0.0, 0.6, 1.1, 0.5 },
            { 0.7, 0.0, 0.3, 1.4 },
        };
        var g = new double[n, n];
        for (int i = 0; i < n; i++)
            for (int j = 0; j < n; j++)
            {
                double s = i == j ? 2.0 : 0.0;
                for (int k = 0; k < n; k++) s += a[k, i] * a[k, j];
                g[i, j] = s;
            }

        double[,] gInv = Inverse((double[,])g.Clone(), n);
        double[] diag = BadDataAnalysis.StateCovarianceDiagonal(g, n); // g is consumed by the factorization

        for (int i = 0; i < n; i++)
            Assert.True(Math.Abs(diag[i] - gInv[i, i]) < 1e-12,
                $"state {i}: diag(G^-1) {diag[i]} vs brute-force {gInv[i, i]}");
    }

    [Fact]
    public void Estimator_exposes_finite_positive_per_bus_confidence_when_requested()
    {
        GridModel model = IeeeCases.Case9();
        PowerFlowResult truth = new NewtonRaphsonPowerFlow().Solve(model);
        MeasurementSet set = WlsEstimatorTests.ExactFullSet(model, truth);

        EstimationResult est = new WlsStateEstimator().Estimate(
            model, set, new EstimationOptions { ComputeStateCovariance = true });

        Assert.Equal(EstimationStatus.Converged, est.Status);
        Assert.NotNull(est.StateVmSigma);
        Assert.NotNull(est.StateVaSigmaDeg);
        for (int i = 0; i < model.BusCount; i++)
        {
            Assert.True(double.IsFinite(est.StateVmSigma![i]) && est.StateVmSigma[i] > 0,
                $"bus {truth.BusIds[i]}: Vm sigma {est.StateVmSigma[i]}");
            Assert.True(double.IsFinite(est.StateVaSigmaDeg![i]) && est.StateVaSigmaDeg[i] >= 0,
                $"bus {truth.BusIds[i]}: Va sigma {est.StateVaSigmaDeg[i]}");
        }

        // The slack bus carries no angle state, so its angle uncertainty is exactly zero (fixed reference).
        int slackIdx = -1;
        for (int i = 0; i < model.Buses.Count; i++)
            if (model.Buses[i].Type == BusType.Slack) { slackIdx = i; break; }
        Assert.True(slackIdx >= 0);
        int slackId = model.Buses[slackIdx].Id;
        int slackRow = -1;
        for (int i = 0; i < est.BusIds.Count; i++)
            if (est.BusIds[i] == slackId) { slackRow = i; break; }
        Assert.True(slackRow >= 0);
        Assert.Equal(0.0, est.StateVaSigmaDeg![slackRow]);
    }

    [Fact]
    public void Covariance_is_null_unless_requested()
    {
        GridModel model = IeeeCases.Case9();
        PowerFlowResult truth = new NewtonRaphsonPowerFlow().Solve(model);
        EstimationResult est = new WlsStateEstimator().Estimate(model, WlsEstimatorTests.ExactFullSet(model, truth));
        Assert.Null(est.StateVmSigma);
        Assert.Null(est.StateVaSigmaDeg);
    }
}
