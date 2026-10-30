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
/// The measurement-noise-floor characterisation tool (<see cref="NoiseFloorAnalysis"/>): from a corpus of
/// estimation residuals it recovers, per source-tag x kind class, how the class's true noise compares to
/// its declared sigma (a spread of ~1 = well calibrated; k = declared sigma is k x too small). Validated by
/// recovery on controlled residual distributions, and exercised end-to-end on real estimator residuals.
/// </summary>
public class NoiseFloorAnalysisTests
{
    // Deterministic standard-normal draws (seeded Box-Muller) - reproducible statistics.
    private static double[] StandardNormal(int n, int seed)
    {
        var rng = new Random(seed);
        var z = new double[n];
        for (int i = 0; i < n; i += 2)
        {
            double u1 = 1.0 - rng.NextDouble(), u2 = rng.NextDouble();
            double r = Math.Sqrt(-2.0 * Math.Log(u1)), th = 2.0 * Math.PI * u2;
            z[i] = r * Math.Cos(th);
            if (i + 1 < n) z[i + 1] = r * Math.Sin(th);
        }
        return z;
    }

    private static MeasurementResidual Res(string source, MeasurementKind kind, double normalized)
        => new(new Measurement(kind, 0.0, 1.0, Bus: 1, Source: source), Predicted: 0.0, Residual: normalized, NormalizedResidual: normalized);

    [Fact]
    public void Recovers_per_class_sigma_scaling_and_flags_miscalibration()
    {
        // The estimator emits the ABSOLUTE normalized residual |r|/sqrt(omega), so a well-calibrated
        // class is half-normal (|N(0,1)|). Feed |z| (matching the real producer), not signed z.
        double[] z = StandardNormal(600, seed: 20260719);
        var residuals = new List<MeasurementResidual>();
        // Well-calibrated class: |normalized residuals| ~ |N(0,1)|.
        foreach (double v in z) residuals.Add(Res("scada", MeasurementKind.VmPu, Math.Abs(v)));
        // Under-declared sigma by 2x: |normalized residuals| ~ |N(0,2)|.
        foreach (double v in z) residuals.Add(Res("pseudo", MeasurementKind.PInjMw, Math.Abs(2.0 * v)));
        // Under-declared sigma by 1.5x.
        foreach (double v in z) residuals.Add(Res("scada", MeasurementKind.QInjMvar, Math.Abs(1.5 * v)));

        NoiseFloorReport report = NoiseFloorAnalysis.Characterise(residuals);

        NoiseFloorClass good = Find(report, "scada", MeasurementKind.VmPu);
        NoiseFloorClass under = Find(report, "pseudo", MeasurementKind.PInjMw);
        NoiseFloorClass under15 = Find(report, "scada", MeasurementKind.QInjMvar);

        // Well-calibrated: robust scale factor ~1, clean shape, NOT flagged (the whole point of the fix).
        Assert.InRange(good.SpreadRobust, 0.85, 1.15);
        Assert.InRange(good.Bias, -0.15, 0.15);
        Assert.False(good.Miscalibrated);

        // Under-declared by 2x: robust factor ~2, flagged, and the suggested scale recovers the factor.
        Assert.InRange(under.SpreadRobust, 1.7, 2.3);
        Assert.True(under.Miscalibrated);
        Assert.InRange(under.SuggestedSigmaScale, 1.7, 2.3);

        // Under-declared by 1.5x: robust factor ~1.5, flagged.
        Assert.InRange(under15.SpreadRobust, 1.3, 1.7);
        Assert.True(under15.Miscalibrated);

        // The report ranks the worst class first.
        Assert.Equal(under.Source, report.Classes[0].Source);
        Assert.Contains(report.Miscalibrated, c => c.Kind == MeasurementKind.PInjMw);
    }

    [Fact]
    public void Small_classes_are_reported_but_never_flagged()
    {
        var residuals = new List<MeasurementResidual>();
        for (int i = 0; i < 4; i++) residuals.Add(Res("rare", MeasurementKind.VmPu, 5.0 * (i - 1.5))); // huge spread, but n<8
        NoiseFloorReport report = NoiseFloorAnalysis.Characterise(residuals, minSamplesPerClass: 8);
        NoiseFloorClass c = Assert.Single(report.Classes);
        Assert.Equal(4, c.Count);
        Assert.False(c.Miscalibrated); // too few samples to trust
    }

    [Fact]
    public void Non_finite_normalized_residuals_are_skipped()
    {
        var residuals = new List<MeasurementResidual>
        {
            Res("scada", MeasurementKind.VmPu, 0.5),
            new(new Measurement(MeasurementKind.VmPu, 0, 1, Bus: 1, Source: "scada"), 0, 0, NormalizedResidual: double.NaN, IsCritical: true),
        };
        NoiseFloorReport report = NoiseFloorAnalysis.Characterise(residuals);
        Assert.Equal(2, report.TotalSamples);
        Assert.Equal(1, report.SkippedNonFinite);
        Assert.Equal(1, report.Classes.Single().Count);
    }

    [Fact]
    public void Runs_end_to_end_on_real_estimator_residuals()
    {
        // A real estimation produces the leverage-corrected residuals the tool consumes; aggregating a few
        // seeds gives a small corpus. A consistent fit's classes should sit near unit spread (loosely).
        GridModel model = IeeeCases.Case14();
        PowerFlowResult truth = new NewtonRaphsonPowerFlow().Solve(model);
        var all = new List<MeasurementResidual>();
        for (int seed = 0; seed < 20; seed++)
        {
            MeasurementSet set = SyntheticMeasurements.Sample(model, truth, new SyntheticMeasurementOptions { Seed = seed });
            EstimationResult est = new WlsStateEstimator().Estimate(model, set);
            all.AddRange(est.Residuals);
        }

        NoiseFloorReport report = NoiseFloorAnalysis.Characterise(all);
        Assert.True(report.TotalSamples > 0);
        Assert.NotEmpty(report.Classes);
        // Every class carries a finite, positive suggested scale.
        Assert.All(report.Classes, c => Assert.True(c.SuggestedSigmaScale > 0 && double.IsFinite(c.SuggestedSigmaScale)));
    }

    private static NoiseFloorClass Find(NoiseFloorReport r, string source, MeasurementKind kind)
        => r.Classes.Single(c => c.Source == source && c.Kind == kind);
}
