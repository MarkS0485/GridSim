using GridSim.Core.Dynamics;
using GridSim.Core.Frequency;
using GridSim.Core.Numerics;
using Xunit;

namespace GridSim.Tests;

/// <summary>R162: FrequencyConstantsFitter's penalty and padding literals are named and a no-op initialiser is gone.
/// The fitter must give the same fit, evaluation for evaluation: compared here with a verbatim copy of the old code.</summary>
public sealed partial class HHygieneTests
{
    [Fact]
    public void R162_fitter_constants_equal_the_literals_they_replaced()
    {
        Assert.Equal(1e6, FrequencyConstantsFitter.OutOfBoundsPenaltyBase);
        Assert.Equal(1e6, FrequencyConstantsFitter.OutOfBoundsPenaltySlope);
        Assert.Equal(2.0, FrequencyConstantsFitter.TracePaddingSeconds);
        string src = CoreSrc("Dynamics", "FrequencyConstantsFitter.cs");
        Assert.DoesNotContain("1e6 + 1e6", src);
        Assert.DoesNotContain("lastT + 2.0", src);
        Assert.DoesNotContain("StepSeconds = baseCfg.StepSeconds", src);
    }

    // ---- The pre-R162 fitter, verbatim (only renamed so it can live in a test). ----
    private static FrequencyFitResult OldFit(double systemInertiaMwS, double baseDemandMw, FrequencyEvent lostInfeed,
        IReadOnlyList<FrequencyTraceSample> measured, DynamicsConfig? seed, IReadOnlyList<FrequencyFitParam>? fit,
        NelderMead.Options? optimizer)
    {
        DynamicsConfig baseCfg = seed ?? new DynamicsConfig();
        FrequencyFitParam[] which = (fit is { Count: > 0 } ? fit : new[] { FrequencyFitParam.Damping }).Distinct().ToArray();
        double lastT = measured[^1].Seconds;
        baseCfg = baseCfg with
        {
            DurationSeconds = Math.Max(baseCfg.DurationSeconds, lastT + 2.0),
            StepSeconds = baseCfg.StepSeconds,
        };
        double[] seedVec = which.Select(p => OldGet(baseCfg, p)).ToArray();
        int evals = 0;
        double Objective(double[] x)
        {
            evals++;
            DynamicsConfig cfg = OldApply(baseCfg, which, x, out double penalty);
            if (penalty > 0) return penalty;
            FrequencyResponse resp = FrequencyDynamics.Simulate(systemInertiaMwS, baseDemandMw, new[] { lostInfeed }, cfg);
            return OldSumSq(resp, measured, cfg.StepSeconds);
        }
        NelderMead.Result r = NelderMead.Minimize(Objective, seedVec, optimizer);
        DynamicsConfig fitted = OldApply(baseCfg, which, r.X, out _);
        FrequencyResponse best = FrequencyDynamics.Simulate(systemInertiaMwS, baseDemandMw, new[] { lostInfeed }, fitted);
        double rms = Math.Sqrt(OldSumSq(best, measured, fitted.StepSeconds) / measured.Count);
        double measuredNadir = measured.Min(s => s.Hz);
        double nadirErr = best.NadirHz - measuredNadir;
        return new FrequencyFitResult(fitted, fitted.DampingPctPerHz, fitted.ResponsiveFraction, fitted.GovernorTimeConstantS,
            rms, nadirErr, evals, r.Converged);
    }

    private static double OldSumSq(FrequencyResponse resp, IReadOnlyList<FrequencyTraceSample> measured, double dt)
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

    private static double OldGet(DynamicsConfig c, FrequencyFitParam p) => p switch
    {
        FrequencyFitParam.Damping => c.DampingPctPerHz,
        FrequencyFitParam.ResponsiveFraction => c.ResponsiveFraction,
        FrequencyFitParam.GovernorTimeConstant => c.GovernorTimeConstantS,
        _ => throw new ArgumentOutOfRangeException(nameof(p)),
    };

    private static DynamicsConfig OldApply(DynamicsConfig baseCfg, FrequencyFitParam[] which, double[] x, out double penalty)
    {
        penalty = 0;
        DynamicsConfig c = baseCfg;
        for (int i = 0; i < which.Length; i++)
        {
            double v = x[i];
            (double lo, double hi) = which[i] switch
            {
                FrequencyFitParam.Damping => (0.0, 10.0),
                FrequencyFitParam.ResponsiveFraction => (0.0, 1.0),
                FrequencyFitParam.GovernorTimeConstant => (0.5, 30.0),
                _ => (0.0, 0.0),
            };
            if (v < lo || v > hi) penalty += 1e6 + 1e6 * (v < lo ? lo - v : v - hi);
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

    [Theory]
    [InlineData(false, 0.5)]    // damping alone, seeded inside the box
    [InlineData(false, 0.01)]   // damping alone, seeded at the box edge: the simplex steps outside and meets the penalty
    [InlineData(true, 1.0)]     // all three jointly
    public void R162_fitter_is_bit_identical_to_the_old_code(bool joint, double seedDamping)
    {
        var loss = new FrequencyEvent(1.0, -1200);
        var truth = new DynamicsConfig { DampingPctPerHz = 1.4, ResponsiveFraction = 0.25, GovernorTimeConstantS = 7.0 };
        FrequencyResponse tr = FrequencyDynamics.Simulate(200_000, 30_000, new[] { loss }, truth);
        var trace = new List<FrequencyTraceSample>();
        for (int i = 0; i <= 50; i++)
        {
            double t = i * 0.5;
            int k = Math.Min((int)Math.Round(t / truth.StepSeconds), tr.Points.Count - 1);
            trace.Add(new FrequencyTraceSample(t, tr.Points[k].Hz));
        }
        var seed = new DynamicsConfig { DampingPctPerHz = seedDamping, ResponsiveFraction = 0.2, GovernorTimeConstantS = 8.0 };
        FrequencyFitParam[]? which = joint
            ? new[] { FrequencyFitParam.Damping, FrequencyFitParam.ResponsiveFraction, FrequencyFitParam.GovernorTimeConstant }
            : null;
        var opts = new NelderMead.Options { MaxIterations = 600 };

        FrequencyFitResult old = OldFit(200_000, 30_000, loss, trace, seed, which, opts);
        FrequencyFitResult now = FrequencyConstantsFitter.Fit(200_000, 30_000, loss, trace, seed, which, opts);
        AssertBits(old.DampingPctPerHz, now.DampingPctPerHz, "damping");
        AssertBits(old.ResponsiveFraction, now.ResponsiveFraction, "responsive fraction");
        AssertBits(old.GovernorTimeConstantS, now.GovernorTimeConstantS, "governor tau");
        AssertBits(old.RmsErrorHz, now.RmsErrorHz, "RMS error");
        AssertBits(old.NadirErrorHz, now.NadirErrorHz, "nadir error");
        Assert.Equal(old.Evaluations, now.Evaluations);
        Assert.Equal(old.Converged, now.Converged);
        Assert.Equal(old.FittedConfig, now.FittedConfig);
    }
}
