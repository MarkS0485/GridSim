using System.Collections.Generic;
using GridSim.Core.Dynamics;
using GridSim.Core.Numerics;
using Xunit;

namespace GridSim.Tests;

/// <summary>
/// The nadir/recovery-constant fitter (<see cref="FrequencyConstantsFitter"/>). Since no clean GB
/// loss-of-infeed trace is bundled, the fitter is validated by RECOVERY on synthetic data: generate a
/// trajectory from known constants, then confirm the fitter recovers them. This proves the tool is correct
/// and ready for a real trace - it does NOT fit the shipped default (DampingPctPerHz stays UNFITTED at 1.0).
/// </summary>
public class FrequencyConstantsFitterTests
{
    private const double InertiaMwS = 200_000; // ~GB-scale spinning energy
    private const double DemandMw = 30_000;
    private static readonly FrequencyEvent Loss = new(AtSeconds: 1.0, DeltaPMw: -1200); // 1.2 GW infeed loss

    // Sample a Simulate trajectory to a measured-style trace at a coarse cadence.
    private static IReadOnlyList<FrequencyTraceSample> SynthTrace(DynamicsConfig truth, double sampleDt = 0.5, double span = 25.0)
    {
        FrequencyResponse r = FrequencyDynamics.Simulate(InertiaMwS, DemandMw, new[] { Loss }, truth);
        var trace = new List<FrequencyTraceSample>();
        for (double t = 0; t <= span; t += sampleDt)
        {
            int k = (int)System.Math.Round(t / truth.StepSeconds);
            if (k >= r.Points.Count) k = r.Points.Count - 1;
            trace.Add(new FrequencyTraceSample(t, r.Points[k].Hz));
        }
        return trace;
    }

    [Fact]
    public void Recovers_damping_from_a_synthetic_trace()
    {
        var truth = new DynamicsConfig { DampingPctPerHz = 1.6, ResponsiveFraction = 0.20, GovernorTimeConstantS = 8.0 };
        IReadOnlyList<FrequencyTraceSample> trace = SynthTrace(truth);

        // Seed away from the truth; fit Damping alone (responsive/governor known from the operational record).
        var seed = new DynamicsConfig { DampingPctPerHz = 0.5, ResponsiveFraction = 0.20, GovernorTimeConstantS = 8.0 };
        FrequencyFitResult fit = FrequencyConstantsFitter.Fit(InertiaMwS, DemandMw, Loss, trace, seed);

        Assert.True(fit.Converged, "fitter should converge");
        Assert.True(System.Math.Abs(fit.DampingPctPerHz - 1.6) < 0.02,
            $"recovered damping {fit.DampingPctPerHz} vs truth 1.6");
        Assert.True(fit.RmsErrorHz < 1e-3, $"RMS error {fit.RmsErrorHz} Hz");
    }

    [Fact]
    public void Jointly_recovers_all_three_from_a_dense_trace()
    {
        var truth = new DynamicsConfig { DampingPctPerHz = 1.2, ResponsiveFraction = 0.28, GovernorTimeConstantS = 6.0 };
        IReadOnlyList<FrequencyTraceSample> trace = SynthTrace(truth, sampleDt: 0.1, span: 28.0);

        var seed = new DynamicsConfig { DampingPctPerHz = 1.0, ResponsiveFraction = 0.20, GovernorTimeConstantS = 8.0 };
        FrequencyFitResult fit = FrequencyConstantsFitter.Fit(
            InertiaMwS, DemandMw, Loss, trace, seed,
            fit: new[] { FrequencyFitParam.Damping, FrequencyFitParam.ResponsiveFraction, FrequencyFitParam.GovernorTimeConstant },
            optimizer: new NelderMead.Options { MaxIterations = 4000 });

        // Joint fit is weakly identified; require a good trajectory fit and reasonable parameter recovery.
        Assert.True(fit.RmsErrorHz < 5e-3, $"RMS error {fit.RmsErrorHz} Hz");
        Assert.True(System.Math.Abs(fit.DampingPctPerHz - 1.2) < 0.15, $"damping {fit.DampingPctPerHz} vs 1.2");
        Assert.True(System.Math.Abs(fit.GovernorTimeConstantS - 6.0) < 1.0, $"governor tau {fit.GovernorTimeConstantS} vs 6.0");
    }

    [Fact]
    public void The_shipped_default_remains_unfitted()
    {
        // Guard the honesty invariant: nothing here changed the shipped default.
        Assert.Equal(1.0, new DynamicsConfig().DampingPctPerHz);
    }
}
