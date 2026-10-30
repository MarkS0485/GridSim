using GridSim.Core.Frequency;
using GridSim.Core.Numerics;

namespace GridSim.Core.Dynamics;

/// <summary>One (time, frequency) sample of a measured frequency trace, seconds from the event window start.</summary>
public sealed record FrequencyTraceSample(double Seconds, double Hz);

/// <summary>Which nadir/recovery constant(s) to fit. The initial RoCoF depends only on inertia and the loss,
/// so it carries no information about these - only the nadir depth and the recovery shape do.</summary>
public enum FrequencyFitParam
{
    /// <summary><see cref="DynamicsConfig.DampingPctPerHz"/> - load self-damping (the primary, best-identified target).</summary>
    Damping,
    /// <summary><see cref="DynamicsConfig.ResponsiveFraction"/> - droop-responsive headroom.</summary>
    ResponsiveFraction,
    /// <summary><see cref="DynamicsConfig.GovernorTimeConstantS"/> - primary-response lag.</summary>
    GovernorTimeConstant,
}

/// <summary>The outcome of fitting the swing model's nadir/recovery constants to a measured trace.</summary>
public sealed record FrequencyFitResult(
    DynamicsConfig FittedConfig,
    double DampingPctPerHz,
    double ResponsiveFraction,
    double GovernorTimeConstantS,
    double RmsErrorHz,
    double NadirErrorHz,
    int Evaluations,
    bool Converged);

/// <summary>
/// Fits the swing model's nadir/recovery constants (<see cref="DynamicsConfig.DampingPctPerHz"/> and,
/// optionally, <see cref="DynamicsConfig.ResponsiveFraction"/> / <see cref="DynamicsConfig.GovernorTimeConstantS"/>)
/// to a measured loss-of-infeed frequency trace, by least-squares over the trajectory that
/// <see cref="FrequencyDynamics.Simulate"/> produces for a known lost infeed at known inertia and demand.
///
/// <para><b>This is the tool, not a fitted value.</b> The default <c>DampingPctPerHz = 1.0</c> stays
/// UNFITTED until a real GB trip trace (a clean dip to a nadir and recovery, with the concurrent lost
/// infeed and the system inertia both known) is supplied here. The bundled NESO sample is ambient normal
/// operation - it contains no trip to fit against. Fitting <c>Damping</c> alone (the default) is
/// well-identified from one trace; jointly fitting all three from a single trace is weakly identified (they
/// trade off against each other), so supply the responsive headroom and governor lag from the operational
/// record and fit only <c>Damping</c> unless multiple events are available.</para>
/// </summary>
public static class FrequencyConstantsFitter
{
    // Physical bounds for the fitted parameters; the objective penalises anything outside.
    private const double DampingMin = 0.0, DampingMax = 10.0;        // %/Hz
    private const double RespMin = 0.0, RespMax = 1.0;               // fraction
    private const double TauMin = 0.5, TauMax = 30.0;               // s

    // ---- Named literals (October 2026 review, R162, H hygiene stream). Values unchanged. ----

    /// <summary>Objective value returned for a point outside the physical box: <c>base + slope * distance outside</c>.
    /// The base is far above any realistic sum of squared Hz errors, so the simplex always retreats into the box; the
    /// slope gives it a direction to retreat in. The model is not simulated at such a point.</summary>
    public const double OutOfBoundsPenaltyBase = 1e6;

    /// <summary>See <see cref="OutOfBoundsPenaltyBase"/>: penalty added per unit of distance outside the box.</summary>
    public const double OutOfBoundsPenaltySlope = 1e6;

    /// <summary>The simulation runs at least this long (s) past the last measured sample, so the interpolation in
    /// the objective never reads past the end of the simulated trajectory.</summary>
    public const double TracePaddingSeconds = 2.0;

    public static FrequencyFitResult Fit(
        double systemInertiaMwS,
        double baseDemandMw,
        FrequencyEvent lostInfeed,
        IReadOnlyList<FrequencyTraceSample> measured,
        DynamicsConfig? seed = null,
        IReadOnlyList<FrequencyFitParam>? fit = null,
        NelderMead.Options? optimizer = null)
    {
        if (measured.Count < 3)
            throw new ArgumentException("need at least three trace samples to fit the nadir/recovery shape.", nameof(measured));
        // GR-558: the duration below is taken from the last sample and the objective walks the trace in order, so the
        // samples must be finite with strictly increasing times (an unsorted trace was clamped to its last point).
        for (int k = 0; k < measured.Count; k++)
        {
            if (!double.IsFinite(measured[k].Seconds) || !double.IsFinite(measured[k].Hz))
                throw new ArgumentException(System.FormattableString.Invariant($"trace sample {k} is not finite (t = {measured[k].Seconds:R} s, f = {measured[k].Hz:R} Hz)."), nameof(measured));
            if (k > 0 && !(measured[k].Seconds > measured[k - 1].Seconds))
                throw new ArgumentException(System.FormattableString.Invariant($"trace sample {k} at t = {measured[k].Seconds:R} s does not come after sample {k - 1} at {measured[k - 1].Seconds:R} s; times must strictly increase."), nameof(measured));
        }

        DynamicsConfig baseCfg = seed ?? new DynamicsConfig();
        FrequencyFitParam[] which = (fit is { Count: > 0 } ? fit : new[] { FrequencyFitParam.Damping }).Distinct().ToArray();

        // A duration that comfortably spans the trace, at the model's native step. (R162: the old initialiser also
        // copied the step size onto itself, which a with-expression already does: a no-op, removed.)
        double lastT = measured[^1].Seconds;
        baseCfg = baseCfg with
        {
            DurationSeconds = Math.Max(baseCfg.DurationSeconds, lastT + TracePaddingSeconds),
        };

        // Pack the selected parameters into the optimiser vector, seeded from baseCfg.
        double[] seedVec = which.Select(p => Get(baseCfg, p)).ToArray();
        int evals = 0;

        double Objective(double[] x)
        {
            evals++;
            DynamicsConfig cfg = Apply(baseCfg, which, x, out double penalty);
            if (penalty > 0) return penalty; // outside the feasible box - large finite penalty, no simulate
            FrequencyResponse resp = FrequencyDynamics.Simulate(systemInertiaMwS, baseDemandMw, new[] { lostInfeed }, cfg);
            return SumSq(resp, measured, cfg.StepSeconds);
        }

        NelderMead.Result r = NelderMead.Minimize(Objective, seedVec, optimizer);
        DynamicsConfig fitted = Apply(baseCfg, which, r.X, out double finalPenalty);
        // GR-558: a simplex that ends outside the physical box (a seed outside it, or a short budget) is not a fitted
        // value; it used to be returned as one, flagged only by Converged = false.
        if (finalPenalty > 0)
            throw new InvalidOperationException(
                "the frequency-constant fit ended outside the physical parameter box (seed outside it, or too few iterations); "
                + "it is not a fitted value. Seed inside the box or allow more iterations.");

        // Report RMS + a nadir-depth error for a quick read on fit quality.
        FrequencyResponse best = FrequencyDynamics.Simulate(systemInertiaMwS, baseDemandMw, new[] { lostInfeed }, fitted);
        double rms = Math.Sqrt(SumSq(best, measured, fitted.StepSeconds) / measured.Count);
        double measuredNadir = measured.Min(s => s.Hz);
        double nadirErr = best.NadirHz - measuredNadir;

        return new FrequencyFitResult(
            fitted, fitted.DampingPctPerHz, fitted.ResponsiveFraction, fitted.GovernorTimeConstantS,
            rms, nadirErr, evals, r.Converged);
    }

    // Sum of squared frequency errors between the simulated trajectory (sampled at dt) and the trace,
    // linearly interpolating the trajectory to each measured sample time.
    private static double SumSq(FrequencyResponse resp, IReadOnlyList<FrequencyTraceSample> measured, double dt)
    {
        IReadOnlyList<FrequencyPoint> pts = resp.Points;
        double sum = 0;
        foreach (FrequencyTraceSample s in measured)
        {
            double idx = s.Seconds / dt;
            int k = (int)Math.Floor(idx);
            double hz;
            if (k < 0) hz = pts[0].Hz;
            else if (k >= pts.Count - 1) hz = pts[^1].Hz;
            else { double frac = idx - k; hz = pts[k].Hz + frac * (pts[k + 1].Hz - pts[k].Hz); }
            double e = hz - s.Hz;
            sum += e * e;
        }
        return sum;
    }

    private static double Get(DynamicsConfig c, FrequencyFitParam p) => p switch
    {
        FrequencyFitParam.Damping => c.DampingPctPerHz,
        FrequencyFitParam.ResponsiveFraction => c.ResponsiveFraction,
        FrequencyFitParam.GovernorTimeConstant => c.GovernorTimeConstantS,
        _ => throw new ArgumentOutOfRangeException(nameof(p)),
    };

    // Apply the optimiser vector to the config, returning a penalty (> 0) if any value is out of bounds.
    private static DynamicsConfig Apply(DynamicsConfig baseCfg, FrequencyFitParam[] which, double[] x, out double penalty)
    {
        penalty = 0;
        DynamicsConfig c = baseCfg;
        for (int i = 0; i < which.Length; i++)
        {
            double v = x[i];
            (double lo, double hi) = which[i] switch
            {
                FrequencyFitParam.Damping => (DampingMin, DampingMax),
                FrequencyFitParam.ResponsiveFraction => (RespMin, RespMax),
                FrequencyFitParam.GovernorTimeConstant => (TauMin, TauMax),
                _ => (0.0, 0.0),
            };
            if (v < lo || v > hi) penalty += OutOfBoundsPenaltyBase + OutOfBoundsPenaltySlope * (v < lo ? lo - v : v - hi);
            c = which[i] switch
            {
                FrequencyFitParam.Damping => c with { DampingPctPerHz = v },
                FrequencyFitParam.ResponsiveFraction => c with { ResponsiveFraction = v },
                FrequencyFitParam.GovernorTimeConstant => c with { GovernorTimeConstantS = v },
                _ => c,
            };
        }
        return c;
    }
}
