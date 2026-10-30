using System;
using System.Collections.Generic;
using GridSim.Core.Estimation;
using GridSim.Core.Estimation.Security;
using GridSim.Core.IO;
using GridSim.Core.Model;
using GridSim.Core.PowerFlow;
using Xunit;

namespace GridSim.Tests;

/// <summary>
/// The FDIA screen: model-based bad-data detection is provably bypassable, so a measurement-consistent
/// (chi-square PASS) estimate whose state is physically implausible is a residual-cloaked-attack signature.
/// A clean estimate raises no flag; an estimate driven out of the voltage band while staying
/// measurement-consistent is flagged.
/// </summary>
public class FdiaScreenTests
{
    private static (GridModel Model, EstimationResult Estimate) CleanEstimate()
    {
        GridModel model = IeeeCases.Case9();
        PowerFlowResult truth = new NewtonRaphsonPowerFlow().Solve(model);
        EstimationResult est = new WlsStateEstimator().Estimate(model, WlsEstimatorTests.ExactFullSet(model, truth));
        return (model, est);
    }

    [Fact]
    public void A_clean_estimate_raises_no_fdia_flag()
    {
        (GridModel model, EstimationResult est) = CleanEstimate();
        FdiaScreenReport report = FdiaScreen.Screen(model, est);

        Assert.True(report.ChiSquarePassed);
        Assert.False(report.SuspectedFdia);
        Assert.False(report.PhysicallyImpossible);
        Assert.Empty(report.LimitViolations);
    }

    [Fact]
    public void A_consistent_but_out_of_band_state_is_flagged_as_possible_fdia()
    {
        (GridModel model, EstimationResult est) = CleanEstimate();
        Assert.True(est.ChiSquarePassed); // still marked measurement-consistent (residual-cloaked)

        // Drive a bus voltage far outside the [0.9, 1.1] operational band, as a null-space attack would.
        est.Vm[0] = 1.35;

        FdiaScreenReport report = FdiaScreen.Screen(model, est);
        Assert.True(report.ChiSquarePassed);
        Assert.True(report.SuspectedFdia);
        Assert.NotEmpty(report.LimitViolations);
    }

    // October 2026 (docs-programme finding): --fdia-screen flagged noise-level limit breaches as SUSPECTED and exited 3
    // on clean case30 / case118 synthetic sets (case118: Qg -14.13 vs Qmin -14.00 MVAr with 1 MVAr telemetry). A breach
    // now counts only when it exceeds 3 x the estimate's uncertainty bound (the measuring telemetry's sigma).

    private static FeasibilityConstraints BandBelowBus1(EstimationResult est, int bus1Id, double breachPu) => new()
    {
        PerBusVm = new Dictionary<int, (double Min, double Max)> { [bus1Id] = (0.9, est.Vm[0] - breachPu) },
    };

    [Fact]
    public void A_limit_breach_inside_three_sigma_is_listed_but_not_counted()
    {
        (GridModel model, EstimationResult est) = CleanEstimate();
        int bus1 = est.BusIds[0];
        // Bus 1's voltage meter has sigma 0.004 pu, so 3 sigma = 0.012 pu. A 0.002 pu breach is noise-sized.
        FdiaScreenReport report = FdiaScreen.Screen(model, est, BandBelowBus1(est, bus1, 0.002));
        Assert.Single(report.LimitViolations);
        Assert.Empty(report.SignificantLimitViolations);
        Assert.False(report.SuspectedFdia);
        Assert.Contains("not counted", report.Summary);

        // With no margin (k = 0) the old behaviour returns: the same breach is flagged.
        Assert.True(FdiaScreen.Screen(model, est, BandBelowBus1(est, bus1, 0.002), significanceSigmas: 0).SuspectedFdia);
    }

    [Fact]
    public void A_limit_breach_beyond_three_sigma_is_counted()
    {
        (GridModel model, EstimationResult est) = CleanEstimate();
        int bus1 = est.BusIds[0];
        FdiaScreenReport report = FdiaScreen.Screen(model, est, BandBelowBus1(est, bus1, 0.02));   // 5 sigma
        Assert.Single(report.SignificantLimitViolations);
        Assert.True(report.SuspectedFdia);
    }

    [Fact]
    public void Clean_case118_synthetic_telemetry_is_not_flagged()
    {
        // The exact finding: the default synthetic set on case118 (seed 20260713, nominal noise) used to be SUSPECTED.
        GridModel model = IeeeCases.Case118();
        // The CLI --synth ground truth: Newton-Raphson with Q-limits enforced (so gens 32 and 105 sit exactly on Qmin).
        PowerFlowResult truth = new NewtonRaphsonPowerFlow().Solve(model, new PowerFlowOptions { MaxIterations = 100, EnforceQLimits = true });
        Assert.True(truth.Converged);
        MeasurementSet set = SyntheticMeasurements.Sample(model, truth, new SyntheticMeasurementOptions());
        EstimationResult est = new WlsStateEstimator().Estimate(model, set);
        Assert.Equal(ChiSquareOutcome.Passed, est.ChiSquareTest);
        FdiaScreenReport report = FdiaScreen.Screen(model, est);
        Assert.False(report.SuspectedFdia, report.Summary);
        Assert.Empty(report.SignificantLimitViolations);
    }

    [Fact]
    public void The_screen_has_a_near_zero_false_positive_rate_on_clean_noisy_data()
    {
        // The screen's clean-data behaviour, measured as a RATE rather than a single point. Over many
        // seeds of realistic Gaussian measurement noise (each meter at its own sigma), a converged clean
        // estimate stays physically plausible and in-band, so the residual-cloaked-FDIA flag should almost
        // never fire. This bounds the false-positive rate an operator would see on honest, noisy data.
        GridModel model = IeeeCases.Case9();
        PowerFlowResult truth = new NewtonRaphsonPowerFlow().Solve(model);
        MeasurementSet exact = WlsEstimatorTests.ExactFullSet(model, truth);

        const int trials = 100;
        int converged = 0, falsePositives = 0;
        for (int seed = 0; seed < trials; seed++)
        {
            var rng = new Random(20260713 + seed);
            double NextGaussian()
            {
                double u1 = 1.0 - rng.NextDouble(), u2 = 1.0 - rng.NextDouble();
                return Math.Sqrt(-2.0 * Math.Log(u1)) * Math.Cos(2.0 * Math.PI * u2);
            }

            var noisy = new List<Measurement>(exact.Measurements.Count);
            foreach (Measurement m in exact.Measurements)
                noisy.Add(m with { Value = m.Value + m.Sigma * NextGaussian() });
            var set = new MeasurementSet { Case = model.Name, BaseMva = model.BaseMva, Measurements = noisy };

            EstimationResult est = new WlsStateEstimator().Estimate(model, set);
            if (est.Status != EstimationStatus.Converged) continue;
            converged++;
            if (FdiaScreen.Screen(model, est).SuspectedFdia) falsePositives++;
        }

        Assert.True(converged >= 90, $"most clean noisy trials should converge ({converged}/{trials})");
        double fpr = (double)falsePositives / converged;
        Assert.True(fpr <= 0.02,
            $"FDIA-screen false-positive rate on clean noisy data should be near zero, was {fpr:P1} ({falsePositives}/{converged})");
    }
}
