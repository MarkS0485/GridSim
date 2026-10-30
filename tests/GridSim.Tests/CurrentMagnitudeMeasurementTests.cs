using System;
using System.Collections.Generic;
using System.Linq;
using GridSim.Core.Estimation;
using GridSim.Core.IO;
using GridSim.Core.Model;
using GridSim.Core.PowerFlow;
using Xunit;

namespace GridSim.Tests;

/// <summary>
/// The branch current-magnitude measurement kind |I| = sqrt(P^2 + Q^2)/Vm. Its analytic Jacobian must match
/// central finite differences (the row is built from the exact flow kernels), and an exact set augmented
/// with current-magnitude measurements must still recover the truth to machine precision.
/// </summary>
public class CurrentMagnitudeMeasurementTests
{
    private static MeasurementSet ExactSetWithCurrents(GridModel model, PowerFlowResult truth)
    {
        var ms = new List<Measurement>(WlsEstimatorTests.ExactFullSet(model, truth).Measurements);
        var vmById = new Dictionary<int, double>();
        for (int i = 0; i < truth.BusIds.Count; i++) vmById[truth.BusIds[i]] = truth.Vm[i];

        var vaById = new Dictionary<int, double>();
        for (int i = 0; i < truth.BusIds.Count; i++) vaById[truth.BusIds[i]] = truth.VaDeg[i];

        foreach (LineFlow lf in truth.LineFlows)
        {
            double pPu = lf.PFromMw / model.BaseMva, qPu = lf.QFromMvar / model.BaseMva;
            double sMag = Math.Sqrt(pPu * pPu + qPu * qPu);
            double imag = sMag / vmById[lf.From];
            string? ckt = string.IsNullOrEmpty(lf.Name) ? null : lf.Name;
            ms.Add(new Measurement(MeasurementKind.IMagPu, imag, 0.002, From: lf.From, To: lf.To, Circuit: ckt));
            // ang(I) = Va_from - atan2(Q, P), in degrees, referenced to the slack datum.
            double iAngDeg = vaById[lf.From] - Math.Atan2(qPu, pPu) * 180.0 / Math.PI;
            ms.Add(new Measurement(MeasurementKind.IAngDeg, iAngDeg, 0.05, From: lf.From, To: lf.To, Circuit: ckt));
        }
        return new MeasurementSet { Case = model.Name, BaseMva = model.BaseMva, Measurements = ms };
    }

    [Fact]
    public void Current_magnitude_jacobian_matches_central_finite_differences()
    {
        GridModel model = IeeeCases.Case9();
        PowerFlowResult truth = new NewtonRaphsonPowerFlow().Solve(model);
        var mm = new MeasurementModel(model, ExactSetWithCurrents(model, truth));

        int n = model.BusCount;
        var vm = new double[n];
        var va = new double[n];
        for (int i = 0; i < n; i++)
        {
            vm[i] = truth.Vm[i] * (1.0 + 0.01 * Math.Sin(i + 1)); // off-solution so no partial degenerates
            va[i] = truth.VaDeg[i] * Math.PI / 180.0 + 0.005 * Math.Cos(2 * i + 1);
        }

        int mCount = mm.MeasurementCount, s = mm.StateSize;
        var analytic = new double[mCount, s];
        mm.EvaluateJacobian(vm, va, analytic);

        const double eps = 1e-7;
        var hPlus = new double[mCount];
        var hMinus = new double[mCount];
        double maxErr = 0;
        for (int c = 0; c < s; c++)
        {
            int bus = mm.StateBusIndex(c, out bool isAngle);
            double[] target = isAngle ? va : vm;
            double keep = target[bus];
            target[bus] = keep + eps; mm.EvaluateH(vm, va, hPlus);
            target[bus] = keep - eps; mm.EvaluateH(vm, va, hMinus);
            target[bus] = keep;
            for (int r = 0; r < mCount; r++)
                maxErr = Math.Max(maxErr, Math.Abs(analytic[r, c] - (hPlus[r] - hMinus[r]) / (2 * eps)));
        }
        Assert.True(maxErr < 1e-6, $"max |analytic - numeric| = {maxErr}");
    }

    [Fact]
    public void Exact_set_with_currents_recovers_truth_to_machine_precision()
    {
        GridModel model = IeeeCases.Case9();
        PowerFlowResult truth = new NewtonRaphsonPowerFlow().Solve(model);
        EstimationResult est = new WlsStateEstimator().Estimate(model, ExactSetWithCurrents(model, truth));

        Assert.Equal(EstimationStatus.Converged, est.Status);
        for (int i = 0; i < model.BusCount; i++)
        {
            Assert.True(Math.Abs(est.Vm[i] - truth.Vm[i]) < 1e-9, $"bus {truth.BusIds[i]} Vm");
            Assert.True(Math.Abs(est.VaDeg[i] - truth.VaDeg[i]) < 1e-8, $"bus {truth.BusIds[i]} Va");
        }
    }
}
