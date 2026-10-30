using System;
using GridSim.Core.Diagnostics;
using GridSim.Core.Estimation.Dse;
using Xunit;

namespace GridSim.Tests;

/// <summary>The frequency/RoCoF Kalman filter: fed a ramping frequency, it tracks the frequency and converges
/// its RoCoF state to the true ramp rate - the Blue-Cut-relevant capability of estimating RoCoF as a state
/// rather than differentiating a PLL. It is a gated recursive filter.</summary>
public class FrequencyRocofFilterTests
{
    [Fact]
    public void Tracks_a_frequency_ramp_and_recovers_rocof()
    {
        // True frequency ramps down at 0.2 Hz/s from 50 Hz.
        const double trueRocof = -0.2;
        var filter = new FrequencyRocofFilter(initialFreqHz: 50.0);

        FrequencyRocofEstimate est = null!;
        const double dt = 0.02; // 50 Hz sampling
        for (int k = 1; k <= 200; k++) // 4 seconds
        {
            double t = k * dt;
            double trueFreq = 50.0 + trueRocof * t;
            est = filter.Step(trueFreq, sigmaHz: 0.001, dtSeconds: dt);
        }

        Assert.True(Math.Abs(est.RocofHzPerS - trueRocof) < 0.01, $"RoCoF {est.RocofHzPerS} vs {trueRocof}");
        double finalTrue = 50.0 + trueRocof * 4.0;
        Assert.True(Math.Abs(est.FreqHz - finalTrue) < 0.01, $"freq {est.FreqHz} vs {finalTrue}");
    }

    [Fact]
    public void Is_registered_in_the_review_gate()
    {
        Assert.True(RestrictionReview.IsReviewed(FrequencyRocofFilter.MethodName));
        // Construction asserts the gate (would throw if unregistered).
        _ = new FrequencyRocofFilter();
    }

    [Fact]
    public void Kalman_recovers_rocof_where_naive_differentiation_diverges()
    {
        // The Blue-Cut point, made concrete: on a NOISY frequency trace, differentiating adjacent samples
        // (the PLL-style finite-difference RoCoF that mis-tripped ~6,200 MW in 2016) is swamped - the noise
        // is divided by dt and blows up - while the Kalman filter, which treats RoCoF as a smoothed state,
        // tracks the true rate through the same noise. This pins the "estimate, don't differentiate" claim.
        const double trueRocof = -0.2, dt = 0.02, sigma = 0.01; // 50 Hz sampling, 0.01 Hz measurement noise
        const int steps = 400, warmup = 200;                    // measure over the settled second half
        var rng = new Random(20260713);
        double NextGaussian()
        {
            double u1 = 1.0 - rng.NextDouble(), u2 = 1.0 - rng.NextDouble();
            return Math.Sqrt(-2.0 * Math.Log(u1)) * Math.Cos(2.0 * Math.PI * u2);
        }

        var filter = new FrequencyRocofFilter(initialFreqHz: 50.0);
        double prevMeas = 50.0;
        double kfSse = 0, fdSse = 0;
        int n = 0;
        for (int k = 1; k <= steps; k++)
        {
            double t = k * dt;
            double meas = 50.0 + trueRocof * t + sigma * NextGaussian();
            double fdRocof = (meas - prevMeas) / dt;                     // naive finite-difference RoCoF
            prevMeas = meas;
            double kfRocof = filter.Step(meas, sigma, dt).RocofHzPerS;   // Kalman RoCoF state
            if (k > warmup)
            {
                kfSse += (kfRocof - trueRocof) * (kfRocof - trueRocof);
                fdSse += (fdRocof - trueRocof) * (fdRocof - trueRocof);
                n++;
            }
        }
        double kfRms = Math.Sqrt(kfSse / n);
        double fdRms = Math.Sqrt(fdSse / n);

        Assert.True(fdRms > 0.3, $"finite-difference RoCoF should be swamped by the noise (RMS {fdRms:F3} Hz/s)");
        Assert.True(kfRms < 0.1, $"the Kalman RoCoF state should track through the noise (RMS {kfRms:F3} Hz/s)");
        Assert.True(kfRms * 4 < fdRms, $"the filter must beat naive differentiation by >4x: KF {kfRms:F3} vs FD {fdRms:F3}");
    }
}
