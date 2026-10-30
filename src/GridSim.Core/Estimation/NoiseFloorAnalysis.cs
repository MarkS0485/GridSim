namespace GridSim.Core.Estimation;

/// <summary>The noise-floor characterisation of one measurement class (a source tag x measurement kind).</summary>
/// <param name="Source">The measurement source tag (SCADA/PMU/pseudo/virtual, etc.).</param>
/// <param name="Kind">The measurement kind.</param>
/// <param name="Count">Finite normalized residuals aggregated for this class.</param>
/// <param name="Bias">Discrepancy between the mean- and median-based scale factors (kMean - kRobust). ~0 for
/// a clean half-normal class; grows with skew, gross errors, or a bias the |residual| folds in.</param>
/// <param name="SpreadStd">Std-based scale factor: std(|r|) / std|N(0,1)|. For a correctly-declared sigma
/// this is ~1. A value k means the class's true noise is about k x its declared sigma.</param>
/// <param name="SpreadRobust">Outlier-resistant scale factor: median(|r|) / median|N(0,1)| - the same ~1
/// target, but not skewed by the handful of gross errors a real feed carries.</param>
/// <param name="SuggestedSigmaScale">Multiply the class's declared sigma by this (the robust factor) to
/// bring its residuals to unit spread - the recalibration a noise-floor study proposes.</param>
/// <param name="Miscalibrated">True when the robust scale factor or the shape discrepancy is outside tolerance.</param>
public sealed record NoiseFloorClass(
    string Source,
    MeasurementKind Kind,
    int Count,
    double Bias,
    double SpreadStd,
    double SpreadRobust,
    double SuggestedSigmaScale,
    bool Miscalibrated);

/// <summary>A noise-floor study over a corpus of estimation residuals.</summary>
public sealed record NoiseFloorReport(
    IReadOnlyList<NoiseFloorClass> Classes,
    int TotalSamples,
    int SkippedNonFinite)
{
    /// <summary>The miscalibrated classes, worst (furthest robust spread from 1) first.</summary>
    public IEnumerable<NoiseFloorClass> Miscalibrated => Classes.Where(c => c.Miscalibrated);
}

/// <summary>
/// Characterises the measurement-noise floor per class (source tag x kind) from estimation residuals - the
/// study that decides whether the declared sigmas match the noise the feeds actually carry, and by how much
/// each sigma class should be rescaled. It reads the LEVERAGE-CORRECTED normalized residuals the estimator
/// already produces (<see cref="MeasurementResidual.NormalizedResidual"/>). Those are the ABSOLUTE values
/// |r|/sqrt(omega), so a correctly-declared class is HALF-normal (|N(0,1)|); the tool recovers a per-class
/// scale factor of k (true noise ~ k x declared sigma) from the half-normal statistics.
///
/// <para>This is the automated form of the "broad shelf slightly above 1 across one source tag =
/// miscalibrated sigma class" reading. Run it over a CORPUS spanning many timestamps (aggregate residuals
/// from many estimations) so each class has enough samples; a single snapshot rarely does. The tool is
/// data-agnostic - point it at synthetic or real GB residuals; the GB sigma-class re-fit is then just its
/// output on a real residual corpus.</para>
/// </summary>
public static class NoiseFloorAnalysis
{
    /// <summary>Default miscalibration tolerance: a class is flagged when its robust spread leaves [1 - tol, 1 + tol]
    /// or its |bias| exceeds tol. Named (R062) so the <c>estimate-noise-floor</c> verb uses the same value.</summary>
    public const double DefaultMiscalibrationTolerance = 0.25;

    /// <summary>Default minimum finite-sample count before a class may be flagged.</summary>
    public const int DefaultMinSamplesPerClass = 8;

    /// <summary>Characterise residuals grouped by (source, kind).</summary>
    /// <param name="residuals">Residuals from one or many estimations (aggregate a corpus for stability).</param>
    /// <param name="miscalibrationTolerance">Flag a class whose robust spread falls outside
    /// [1 - tol, 1 + tol] or whose |bias| exceeds tol. Default 0.25.</param>
    /// <param name="minSamplesPerClass">Classes below this finite-sample count are still reported but never
    /// flagged (too few samples to trust the spread). Default 8.</param>
    public static NoiseFloorReport Characterise(
        IEnumerable<MeasurementResidual> residuals,
        double miscalibrationTolerance = DefaultMiscalibrationTolerance,
        int minSamplesPerClass = DefaultMinSamplesPerClass)
    {
        var byClass = new Dictionary<(string Source, MeasurementKind Kind), List<double>>();
        int total = 0, skipped = 0;
        foreach (MeasurementResidual r in residuals)
        {
            total++;
            double nr = r.NormalizedResidual;
            if (!double.IsFinite(nr)) { skipped++; continue; }        // NaN = critical / no redundancy
            var key = (r.Measurement.Source ?? "", r.Measurement.Kind);
            if (!byClass.TryGetValue(key, out List<double>? list)) byClass[key] = list = new List<double>();
            list.Add(nr);
        }

        // The estimator emits the ABSOLUTE leverage-corrected residual |r|/sqrt(omega) (see
        // EstimationResult.NormalizedResidual / BadDataAnalysis), so a correctly-declared class is
        // HALF-normal (|N(0,1)|), not signed N(0,1). Recover the calibration factor k (true noise =
        // k x declared sigma) by dividing each robust statistic by its half-normal reference - a
        // clean class then reads k ~ 1 on all three, instead of the ~0.8/0.59 the old signed-N(0,1)
        // assumption produced (which flagged every well-calibrated class).
        const double hnMean = 0.7978845608028654;    // E|N(0,1)| = sqrt(2/pi)
        const double hnMedian = 0.6744897501960817;  // median|N(0,1)|
        const double hnStd = 0.6028102749890869;     // std|N(0,1)| = sqrt(1 - 2/pi)

        var classes = new List<NoiseFloorClass>(byClass.Count);
        foreach (((string source, MeasurementKind kind), List<double> vals) in byClass)
        {
            int n = vals.Count;
            double meanAbs = Mean(vals);
            double kMean = meanAbs / hnMean;
            double kStd = SampleStd(vals, meanAbs) / hnStd;
            double kRobust = Median(vals) / hnMedian;                      // robust scale factor, target 1
            double scale = kRobust > 0 ? kRobust : (kStd > 0 ? kStd : 1.0);
            // Shape check: for a clean half-normal the mean- and median-based factors agree, so their
            // difference is ~0. A gross skew, heavy outliers, or a folded-in bias pushes them apart.
            double bias = kMean - kRobust;
            bool enough = n >= minSamplesPerClass;
            bool miscal = enough && (System.Math.Abs(kRobust - 1.0) > miscalibrationTolerance
                                     || System.Math.Abs(bias) > miscalibrationTolerance);
            classes.Add(new NoiseFloorClass(source, kind, n, bias, kStd, kRobust, scale, miscal));
        }

        // Worst (furthest robust spread from 1) first, then by class size.
        classes.Sort((a, b) =>
        {
            int c = System.Math.Abs(b.SpreadRobust - 1.0).CompareTo(System.Math.Abs(a.SpreadRobust - 1.0));
            return c != 0 ? c : b.Count.CompareTo(a.Count);
        });

        return new NoiseFloorReport(classes, total, skipped);
    }

    /// <summary>Convenience: characterise a single estimation's residuals (aggregate many for a real study).</summary>
    public static NoiseFloorReport Characterise(
        EstimationResult result, double miscalibrationTolerance = DefaultMiscalibrationTolerance, int minSamplesPerClass = DefaultMinSamplesPerClass)
        => Characterise(result.Residuals, miscalibrationTolerance, minSamplesPerClass);

    private static double Mean(List<double> v)
    {
        double s = 0;
        for (int i = 0; i < v.Count; i++) s += v[i];
        return v.Count == 0 ? 0 : s / v.Count;
    }

    private static double SampleStd(List<double> v, double mean)
    {
        if (v.Count < 2) return 0;
        double s = 0;
        for (int i = 0; i < v.Count; i++) { double e = v[i] - mean; s += e * e; }
        return System.Math.Sqrt(s / (v.Count - 1));
    }

    private static double Median(List<double> v)
    {
        var a = v.ToArray();
        System.Array.Sort(a);
        return MedianSorted(a);
    }

    private static double MedianSorted(double[] a)
    {
        int n = a.Length;
        if (n == 0) return 0;
        return (n & 1) == 1 ? a[n / 2] : 0.5 * (a[n / 2 - 1] + a[n / 2]);
    }
}
