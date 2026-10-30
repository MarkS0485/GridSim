using System;
using System.Collections.Generic;
using GridSim.Core.Estimation;
using GridSim.Core.Estimation.Dse;
using GridSim.Core.IO;
using GridSim.Core.Model;
using GridSim.Core.PowerFlow;
using Xunit;

namespace GridSim.Tests;

/// <summary>
/// The Ensemble Kalman Filter tracker: it converges the ensemble mean toward a static truth (well below the
/// flat-start error) and is deterministic for a fixed seed.
/// </summary>
public class EnsembleKalmanStateEstimatorTests
{
    private static (GridModel Model, MeasurementSet Exact, PowerFlowResult Truth) Fixture()
    {
        GridModel model = IeeeCases.Case9();
        PowerFlowResult truth = new NewtonRaphsonPowerFlow().Solve(model);
        return (model, WlsEstimatorTests.ExactFullSet(model, truth), truth);
    }

    private static double VaRmsErrorDeg(DseEstimate est, PowerFlowResult truth)
    {
        double sum = 0;
        for (int i = 0; i < truth.BusIds.Count; i++)
        {
            double d = est.VaDeg[i] - truth.VaDeg[i];
            sum += d * d;
        }
        return Math.Sqrt(sum / truth.BusIds.Count);
    }

    [Fact]
    public void Ensemble_mean_converges_toward_the_truth()
    {
        (GridModel model, MeasurementSet exact, PowerFlowResult truth) = Fixture();
        var enkf = new EnsembleKalmanStateEstimator(model, ensembleSize: 120);

        DseEstimate est = null!;
        for (int step = 0; step < 40; step++) est = enkf.Step(exact);

        // Flat start has zero angles; the truth angles span several degrees. The filter must pull the mean
        // angle error well below that flat-start error.
        double flatStartErr = VaRmsErrorDeg(
            new DseEstimate(truth.BusIds, new double[model.BusCount], new double[model.BusCount],
                new double[model.BusCount], new double[model.BusCount], true, null),
            truth);
        double err = VaRmsErrorDeg(est, truth);
        Assert.True(err < 0.25 * flatStartErr, $"EnKF angle RMS {err} vs flat-start {flatStartErr}");
    }

    [Fact]
    public void Is_deterministic_for_a_fixed_seed()
    {
        (GridModel model, MeasurementSet exact, _) = Fixture();

        double[] RunOnce()
        {
            var enkf = new EnsembleKalmanStateEstimator(model, ensembleSize: 40, seed: 20260713);
            DseEstimate est = null!;
            for (int step = 0; step < 6; step++) est = enkf.Step(exact);
            return est.Vm;
        }

        double[] a = RunOnce();
        double[] b = RunOnce();
        for (int i = 0; i < a.Length; i++) Assert.Equal(a[i], b[i]); // bit-identical
    }
}
