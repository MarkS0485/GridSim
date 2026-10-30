using GridSim.Core.Estimation;
using GridSim.Core.IO;
using GridSim.Core.Model;
using GridSim.Core.PowerFlow;
using Xunit;

namespace GridSim.Tests;

public class WlsEstimatorTests
{
    /// <summary>Every measurable quantity, exactly as solved (zero noise): all bus voltages,
    /// all injections, all branch flows at both ends. The machine-precision recovery fixture.</summary>
    internal static MeasurementSet ExactFullSet(GridModel model, PowerFlowResult truth)
    {
        var ms = new List<Measurement>();
        for (int i = 0; i < model.BusCount; i++)
        {
            int id = truth.BusIds[i];
            ms.Add(new Measurement(MeasurementKind.VmPu, truth.Vm[i], 0.004, Bus: id));
            ms.Add(new Measurement(MeasurementKind.PInjMw, truth.PInjMw[i], 1.0, Bus: id));
            ms.Add(new Measurement(MeasurementKind.QInjMvar, truth.QInjMvar[i], 1.0, Bus: id));
        }
        foreach (LineFlow lf in truth.LineFlows)
        {
            ms.Add(new Measurement(MeasurementKind.PFlowMw, lf.PFromMw, 1.6, From: lf.From, To: lf.To, Circuit: NullIfEmpty(lf.Name)));
            ms.Add(new Measurement(MeasurementKind.QFlowMvar, lf.QFromMvar, 1.6, From: lf.From, To: lf.To, Circuit: NullIfEmpty(lf.Name)));
            ms.Add(new Measurement(MeasurementKind.PFlowMw, lf.PToMw, 1.6, From: lf.From, To: lf.To, End: BranchEnd.To, Circuit: NullIfEmpty(lf.Name)));
            ms.Add(new Measurement(MeasurementKind.QFlowMvar, lf.QToMvar, 1.6, From: lf.From, To: lf.To, End: BranchEnd.To, Circuit: NullIfEmpty(lf.Name)));
        }
        return new MeasurementSet { Case = model.Name, BaseMva = model.BaseMva, Measurements = ms };

        static string? NullIfEmpty(string s) => string.IsNullOrEmpty(s) ? null : s;
    }

    private static void AssertRecoversTruth(GridModel model)
    {
        PowerFlowResult truth = new NewtonRaphsonPowerFlow().Solve(model);
        Assert.True(truth.Converged);

        EstimationResult est = new WlsStateEstimator().Estimate(model, ExactFullSet(model, truth));

        Assert.Equal(EstimationStatus.Converged, est.Status);
        Assert.True(est.Converged);
        for (int i = 0; i < model.BusCount; i++)
        {
            Assert.True(Math.Abs(est.Vm[i] - truth.Vm[i]) < 1e-10,
                $"bus {truth.BusIds[i]}: Vm {est.Vm[i]} vs truth {truth.Vm[i]}");
            Assert.True(Math.Abs(est.VaDeg[i] - truth.VaDeg[i]) < 1e-9,
                $"bus {truth.BusIds[i]}: Va {est.VaDeg[i]} vs truth {truth.VaDeg[i]}");
        }
        Assert.True(est.ObjectiveJ < 1e-16, $"J = {est.ObjectiveJ}");
    }

    [Fact]
    public void Exact_full_measurement_set_recovers_case9_truth_to_machine_precision()
        => AssertRecoversTruth(IeeeCases.Case9());

    [Fact]
    public void Exact_full_measurement_set_recovers_case14_truth_to_machine_precision()
        => AssertRecoversTruth(IeeeCases.Case14());

    [Fact]
    public void Minimal_observable_set_still_recovers_truth()
    {
        // Injections at every bus + one voltage magnitude: barely more than exactly determined
        // (2n measurements vs 2n-1 states) - recovery must not depend on heavy redundancy.
        GridModel model = IeeeCases.Case9();
        PowerFlowResult truth = new NewtonRaphsonPowerFlow().Solve(model);
        var ms = new List<Measurement> { new(MeasurementKind.VmPu, truth.Vm[0], 0.004, Bus: truth.BusIds[0]) };
        for (int i = 0; i < model.BusCount; i++)
        {
            ms.Add(new Measurement(MeasurementKind.PInjMw, truth.PInjMw[i], 1.0, Bus: truth.BusIds[i]));
            ms.Add(new Measurement(MeasurementKind.QInjMvar, truth.QInjMvar[i], 1.0, Bus: truth.BusIds[i]));
        }
        var set = new MeasurementSet { Case = model.Name, BaseMva = model.BaseMva, Measurements = ms };

        EstimationResult est = new WlsStateEstimator().Estimate(model, set);
        Assert.Equal(EstimationStatus.Converged, est.Status);
        for (int i = 0; i < model.BusCount; i++)
        {
            Assert.True(Math.Abs(est.Vm[i] - truth.Vm[i]) < 1e-8, $"Vm[{i}] off by {est.Vm[i] - truth.Vm[i]}");
            Assert.True(Math.Abs(est.VaDeg[i] - truth.VaDeg[i]) < 1e-7, $"Va[{i}] off by {est.VaDeg[i] - truth.VaDeg[i]}");
        }
    }

    [Fact]
    public void Measurement_jacobian_matches_central_finite_differences()
    {
        GridModel model = IeeeCases.Case9();
        PowerFlowResult truth = new NewtonRaphsonPowerFlow().Solve(model);
        MeasurementSet set = ExactFullSet(model, truth);

        // Probe at a deliberately off-solution state so no partial degenerates to zero by luck.
        var mm = new MeasurementModel(model, set);
        int n = model.BusCount;
        var vm = new double[n];
        var va = new double[n];
        for (int i = 0; i < n; i++)
        {
            vm[i] = truth.Vm[i] * (1.0 + 0.01 * Math.Sin(i + 1));
            va[i] = truth.VaDeg[i] * Math.PI / 180.0 + 0.005 * Math.Cos(2 * i + 1);
        }

        int mCount = mm.MeasurementCount;
        int s = mm.StateSize;
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
            target[bus] = keep + eps;
            mm.EvaluateH(vm, va, hPlus);
            target[bus] = keep - eps;
            mm.EvaluateH(vm, va, hMinus);
            target[bus] = keep;
            for (int r = 0; r < mCount; r++)
            {
                double numeric = (hPlus[r] - hMinus[r]) / (2 * eps);
                maxErr = Math.Max(maxErr, Math.Abs(analytic[r, c] - numeric));
            }
        }
        Assert.True(maxErr < 1e-6, $"max |analytic - numeric| = {maxErr}");
    }

    [Fact]
    public void Warm_start_from_truth_converges_immediately()
    {
        GridModel model = IeeeCases.Case14();
        PowerFlowResult truth = new NewtonRaphsonPowerFlow().Solve(model);
        EstimationResult est = new WlsStateEstimator().Estimate(
            model, ExactFullSet(model, truth), new EstimationOptions { WarmStart = truth });
        Assert.Equal(EstimationStatus.Converged, est.Status);
        Assert.True(est.Iterations <= 2, $"took {est.Iterations} iterations from a warm start at the answer");
    }

    [Fact]
    public void Slack_angle_stays_exactly_at_the_model_reference()
    {
        GridModel model = IeeeCases.Case9();
        PowerFlowResult truth = new NewtonRaphsonPowerFlow().Solve(model);
        EstimationResult est = new WlsStateEstimator().Estimate(model, ExactFullSet(model, truth));

        int slackIdx = model.Buses.ToList().FindIndex(b => b.Type == BusType.Slack);
        Assert.Equal(model.Buses[slackIdx].VaDeg, est.VaDeg[slackIdx]);
    }

    [Fact]
    public void Projected_power_flow_result_closes_against_truth()
    {
        GridModel model = IeeeCases.Case9();
        PowerFlowResult truth = new NewtonRaphsonPowerFlow().Solve(model);
        EstimationResult est = new WlsStateEstimator().Estimate(model, ExactFullSet(model, truth));

        PowerFlowResult back = est.ToPowerFlowResult(model);
        Assert.True(back.Converged);
        for (int i = 0; i < model.BusCount; i++)
        {
            Assert.True(Math.Abs(back.PInjMw[i] - truth.PInjMw[i]) < 1e-6);
            Assert.True(Math.Abs(back.QInjMvar[i] - truth.QInjMvar[i]) < 1e-6);
        }
        Assert.True(Math.Abs(back.TotalLossMw - truth.TotalLossMw) < 1e-6);
        for (int k = 0; k < truth.LineFlows.Count; k++)
            Assert.True(Math.Abs(back.LineFlows[k].PFromMw - truth.LineFlows[k].PFromMw) < 1e-6);
    }

    [Fact]
    public void An_undeterminable_set_reports_unobservable_not_nan()
    {
        GridModel model = IeeeCases.Case9();
        var set = new MeasurementSet
        {
            Case = model.Name,
            BaseMva = model.BaseMva,
            Measurements = new[] { new Measurement(MeasurementKind.VmPu, 1.04, 0.004, Bus: 1) },
        };
        EstimationResult est = new WlsStateEstimator().Estimate(model, set);
        Assert.Equal(EstimationStatus.Unobservable, est.Status);
        Assert.False(est.Converged);
        Assert.All(est.Vm, v => Assert.True(double.IsFinite(v)));
    }

    [Fact]
    public void Estimation_is_deterministic()
    {
        GridModel model = IeeeCases.Case14();
        PowerFlowResult truth = new NewtonRaphsonPowerFlow().Solve(model);
        MeasurementSet set = ExactFullSet(model, truth);

        EstimationResult a = new WlsStateEstimator().Estimate(model, set);
        EstimationResult b = new WlsStateEstimator().Estimate(model, set);
        Assert.Equal(a.Iterations, b.Iterations);
        for (int i = 0; i < model.BusCount; i++)
        {
            Assert.Equal(a.Vm[i], b.Vm[i]);         // bit-identical
            Assert.Equal(a.VaDeg[i], b.VaDeg[i]);
        }
    }

    [Fact]
    public void Voltage_only_pmu_set_recovers_the_state_exactly()
    {
        // WS-E: with PMU angles (VaDeg) + voltage magnitudes at every bus, the state is fully
        // observable with ZERO power measurements - every h is linear, Gauss-Newton lands in one
        // step, and the recovery is exact.
        GridModel model = IeeeCases.Case14();
        PowerFlowResult truth = new NewtonRaphsonPowerFlow().Solve(model);

        var ms = new List<Measurement>();
        for (int i = 0; i < model.BusCount; i++)
        {
            int id = truth.BusIds[i];
            ms.Add(new Measurement(MeasurementKind.VmPu, truth.Vm[i], 0.004, Bus: id));
            if (model.Buses[i].Type != BusType.Slack)
                ms.Add(new Measurement(MeasurementKind.VaDeg, truth.VaDeg[i], 0.02, Bus: id, Source: "pmu"));
        }
        var set = new MeasurementSet { Case = model.Name, Measurements = ms };

        EstimationResult est = new WlsStateEstimator().Estimate(model, set);

        Assert.Equal(EstimationStatus.Converged, est.Status);
        Assert.True(est.ChiSquarePassed);
        Assert.Empty(est.Removed);
        for (int i = 0; i < model.BusCount; i++)
        {
            Assert.True(Math.Abs(est.Vm[i] - truth.Vm[i]) < 1e-10, $"Vm[{i}] off by {est.Vm[i] - truth.Vm[i]}");
            Assert.True(Math.Abs(est.VaDeg[i] - truth.VaDeg[i]) < 1e-9, $"Va[{i}] off by {est.VaDeg[i] - truth.VaDeg[i]}");
        }
    }

    [Fact]
    public void A_slack_angle_row_is_legal_and_uninformative()
    {
        // The slack's angle is the reference, not a state: a VaDeg row there has an empty
        // Jacobian row - legal, constant residual, no effect on the estimate.
        GridModel model = IeeeCases.Case14();
        PowerFlowResult truth = new NewtonRaphsonPowerFlow().Solve(model);
        MeasurementSet set = WlsEstimatorTests.ExactFullSet(model, truth);

        int slack = Enumerable.Range(0, model.BusCount).First(i => model.Buses[i].Type == BusType.Slack);
        var ms = set.Measurements.ToList();
        ms.Add(new Measurement(MeasurementKind.VaDeg, truth.VaDeg[slack], 0.02, Bus: truth.BusIds[slack], Source: "pmu"));
        set = set with { Measurements = ms };

        EstimationResult est = new WlsStateEstimator().Estimate(model, set);
        Assert.Equal(EstimationStatus.Converged, est.Status);
        for (int i = 0; i < model.BusCount; i++)
            Assert.True(Math.Abs(est.Vm[i] - truth.Vm[i]) < 1e-8);
    }
}
