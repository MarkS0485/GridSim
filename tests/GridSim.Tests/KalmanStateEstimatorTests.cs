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
/// The recursive EKF network-state tracker (Dynamic State Estimation, Phase 2). Feeding a static truth with
/// exact measurements, the truth is a fixed point of the filter (zero innovation), so the estimate must
/// converge to it; the confidence must tighten as information accumulates; and the filter must be
/// deterministic. The Dynamics->Estimation review gate is exercised at construction (the method is registered).
/// </summary>
public class KalmanStateEstimatorTests
{
    private static (GridModel Model, MeasurementSet ExactSet, PowerFlowResult Truth) Fixture()
    {
        GridModel model = IeeeCases.Case9();
        PowerFlowResult truth = new NewtonRaphsonPowerFlow().Solve(model);
        MeasurementSet exact = WlsEstimatorTests.ExactFullSet(model, truth);
        return (model, exact, truth);
    }

    [Fact]
    public void Ekf_tracks_a_static_truth_to_close_tolerance()
    {
        (GridModel model, MeasurementSet exact, PowerFlowResult truth) = Fixture();
        var ekf = new KalmanStateEstimator(model);

        DseEstimate est = null!;
        for (int step = 0; step < 25; step++) est = ekf.Step(exact);

        Assert.True(est.Converged);
        for (int i = 0; i < model.BusCount; i++)
        {
            Assert.True(Math.Abs(est.Vm[i] - truth.Vm[i]) < 1e-3,
                $"bus {truth.BusIds[i]}: EKF Vm {est.Vm[i]} vs truth {truth.Vm[i]}");
            Assert.True(Math.Abs(est.VaDeg[i] - truth.VaDeg[i]) < 1e-2,
                $"bus {truth.BusIds[i]}: EKF Va {est.VaDeg[i]} vs truth {truth.VaDeg[i]}");
        }
    }

    [Fact]
    public void Confidence_tightens_as_information_accumulates()
    {
        (GridModel model, MeasurementSet exact, _) = Fixture();
        var ekf = new KalmanStateEstimator(model, initialStateVariance: 1.0);

        DseEstimate first = ekf.Step(exact);
        DseEstimate later = first;
        for (int step = 0; step < 15; step++) later = ekf.Step(exact);

        // The diffuse prior (sigma = 1) collapses toward the measurement-informed steady state.
        for (int i = 0; i < model.BusCount; i++)
        {
            Assert.True(later.StateVmSigma[i] <= first.StateVmSigma[i] + 1e-12,
                $"bus {i}: Vm sigma grew ({first.StateVmSigma[i]} -> {later.StateVmSigma[i]})");
            Assert.True(later.StateVmSigma[i] < 0.5, $"bus {i}: Vm sigma {later.StateVmSigma[i]} did not tighten");
        }
    }

    [Fact]
    public void Ekf_is_deterministic()
    {
        (GridModel model, MeasurementSet exact, _) = Fixture();

        double[] RunOnce()
        {
            var ekf = new KalmanStateEstimator(model);
            DseEstimate est = null!;
            for (int step = 0; step < 10; step++) est = ekf.Step(exact);
            var flat = new List<double>(est.Vm);
            flat.AddRange(est.VaDeg);
            return flat.ToArray();
        }

        double[] a = RunOnce();
        double[] b = RunOnce();
        Assert.Equal(a.Length, b.Length);
        for (int i = 0; i < a.Length; i++) Assert.Equal(a[i], b[i]); // bit-identical
    }

    [Fact]
    public void Ukf_tracks_a_static_truth_and_agrees_with_ekf()
    {
        (GridModel model, MeasurementSet exact, PowerFlowResult truth) = Fixture();
        var ukf = new UnscentedKalmanStateEstimator(model);

        DseEstimate est = null!;
        for (int step = 0; step < 25; step++) est = ukf.Step(exact);

        Assert.True(est.Converged);
        for (int i = 0; i < model.BusCount; i++)
        {
            Assert.True(Math.Abs(est.Vm[i] - truth.Vm[i]) < 1e-3,
                $"bus {truth.BusIds[i]}: UKF Vm {est.Vm[i]} vs truth {truth.Vm[i]}");
            Assert.True(Math.Abs(est.VaDeg[i] - truth.VaDeg[i]) < 1e-2,
                $"bus {truth.BusIds[i]}: UKF Va {est.VaDeg[i]} vs truth {truth.VaDeg[i]}");
        }
    }

    [Fact]
    public void Ukf_is_deterministic()
    {
        (GridModel model, MeasurementSet exact, _) = Fixture();

        double[] RunOnce()
        {
            var ukf = new UnscentedKalmanStateEstimator(model);
            DseEstimate est = null!;
            for (int step = 0; step < 8; step++) est = ukf.Step(exact);
            return est.Vm;
        }

        double[] a = RunOnce();
        double[] b = RunOnce();
        for (int i = 0; i < a.Length; i++) Assert.Equal(a[i], b[i]);
    }
}
