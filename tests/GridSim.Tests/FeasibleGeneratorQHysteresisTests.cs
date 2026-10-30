using GridSim.Core.Estimation;
using GridSim.Core.IO;
using GridSim.Core.Model;
using GridSim.Core.PowerFlow;
using Xunit;

namespace GridSim.Tests;

/// <summary>
/// N043: the Feasible estimator's generator reactive-capability rows carry a hysteresis band (like the angle rows of
/// the R257 follow-up) and are released on the sign of their multiplier, not by a bare 1e-6 pu trigger. These tests
/// pin:
///   - the band's size and its fail-loud gates (QBandPu);
///   - case118 with noise 1 converges (it stopped at MaxIterations: the Q active set cycled), with exact numbers;
///   - the case14 Ring B angle-cap scenario at 0.7 x the WLS spread converges and holds the cap exactly (it stopped
///     at MaxIterations for the same reason);
///   - noise-free case57 and case118 still fit their data exactly with nothing removed. This guards the reason the
///     angle rows' "hold anywhere in the band" clamp was NOT copied to the Q rows: a generator sitting at its limit
///     is approached from inside, and that clamp froze Qg where it entered the band (case57 noise 0 went from
///     J 1.7e-23 to J 224.8 with 4 good measurements removed).
/// </summary>
public class FeasibleGeneratorQHysteresisTests
{
    [Fact]
    public void The_band_is_a_fraction_of_the_capability_range_with_a_floor()
    {
        // Both bounds finite: 1e-3 x (Qmax - Qmin).
        Assert.Equal(1e-3 * (1.0 - -0.5), FeasibleWlsStep.QBandPu(-0.5, 1.0));
        // One bound finite: 1e-3 x |that bound|.
        Assert.Equal(1e-3 * 2.0, FeasibleWlsStep.QBandPu(double.NegativeInfinity, 2.0));
        Assert.Equal(1e-3 * 0.3, FeasibleWlsStep.QBandPu(-0.3, double.PositiveInfinity));
        // A zero range keeps the old 1e-6 pu tolerance as its band.
        Assert.Equal(FeasibleWlsStep.GeneratorQBandFloorPu, FeasibleWlsStep.QBandPu(0.25, 0.25));
        Assert.Equal(1e-6, FeasibleWlsStep.GeneratorQBandFloorPu);
        Assert.Equal(1e-3, FeasibleWlsStep.GeneratorQHysteresisFraction);
    }

    [Fact]
    public void Inverted_or_missing_reactive_limits_fail_loud()
    {
        Assert.Throws<ArgumentException>(() => FeasibleWlsStep.QBandPu(1.0, 0.5));
        Assert.Throws<ArgumentException>(() => FeasibleWlsStep.QBandPu(double.NaN, 0.5));
        Assert.Throws<ArgumentException>(() => FeasibleWlsStep.QBandPu(-0.5, double.NaN));
    }

    private static EstimationResult Synthetic(string name, double noise)
    {
        GridModel m = name == "57" ? IeeeCases.Case57() : IeeeCases.Case118();
        PowerFlowResult truth = new NewtonRaphsonPowerFlow().Solve(m, new PowerFlowOptions { EnforceQLimits = true });
        Assert.True(truth.Converged);
        MeasurementSet set = SyntheticMeasurements.Sample(m, truth, new SyntheticMeasurementOptions { NoiseScale = noise });
        return new WlsStateEstimator().Estimate(m, set, new EstimationOptions { Method = EstimationMethod.Feasible });
    }

    [Fact]
    public void Case118_with_noise_converges_instead_of_cycling()
    {
        // Before N043: MaxIterations after 30 iterations, J 877.8811728901087.
        EstimationResult est = Synthetic("118", 1.0);
        Assert.Equal(EstimationStatus.Converged, est.Status);
        Assert.Equal(8, est.Iterations);
        Assert.Empty(est.Removed);
        // Moved 2026-10 (D1, Mark's determinism rule 2026-10-04: identical bits on every CPU tier and OS): truth solve,
        // noise and measurement functions on GridSim's deterministic fdlibm maths. Before -> after: 877.8811650453016 -> 877.881165045295.
        Assert.Equal(877.881165045295, est.ObjectiveJ);
    }

    [Theory]
    [InlineData("57")]
    [InlineData("118")]
    public void Noise_free_data_is_still_fitted_exactly_with_nothing_removed(string name)
    {
        EstimationResult est = Synthetic(name, 0.0);
        Assert.Equal(EstimationStatus.Converged, est.Status);
        Assert.Empty(est.Removed);
        Assert.True(est.ObjectiveJ < 1e-15, $"case{name}: noise-free J {est.ObjectiveJ:R} is not a perfect fit");
    }

    [Fact]
    public void Ring_B_angle_cap_at_seventy_percent_converges_and_holds_the_cap()
    {
        GridModel m = IeeeCases.Case14();
        PowerFlowResult truth = new NewtonRaphsonPowerFlow().Solve(m, new PowerFlowOptions { EnforceQLimits = true });
        MeasurementSet set = WlsEstimatorTests.ExactFullSet(m, truth);

        double MaxAngleDiffRad(EstimationResult est)
        {
            var vaDeg = new Dictionary<int, double>();
            for (int i = 0; i < est.BusIds.Count; i++) vaDeg[est.BusIds[i]] = est.VaDeg[i];
            double max = 0;
            foreach (Branch br in m.Branches)
                max = Math.Max(max, Math.Abs((vaDeg[br.From] - vaDeg[br.To]) * Math.PI / 180.0));
            return max;
        }

        EstimationResult wls = new WlsStateEstimator().Estimate(m, set, new EstimationOptions { Method = EstimationMethod.Wls });
        double cap = MaxAngleDiffRad(wls) * 0.7;
        // Test changed 2026-10-19: 1.95.299 (Mark's ruling 2026-10-15, GR-066/383/418) solves the plain WLS estimate this
        // cap is taken from through the canonical sparse step, not the dense LU; its changelog says small-case WLS pins
        // move in their last bits. Before -> after: 0.10719284702809638 -> 0.10719284702809641 (2 ulps, 2026-10-19 run).
        Assert.Equal(0.10719284702809641, cap);

        EstimationResult fea = new WlsStateEstimator().Estimate(m, set, new EstimationOptions
        {
            Method = EstimationMethod.Feasible,
            Feasibility = new FeasibilityConstraints { AngleDiffMaxRad = cap },
        });
        // Before N043: MaxIterations, 30 iterations, J 4401.644730223793 (max difference 0.10719284702809635).
        Assert.Equal(EstimationStatus.Converged, fea.Status);
        Assert.Equal(68, fea.Iterations);
        // Moved 2026-10 (D1, Mark's determinism rule 2026-10-04: identical bits on every CPU tier and OS): truth solve,
        // noise and measurement functions on GridSim's deterministic fdlibm maths. Before -> after: 72.38263564898884 -> 72.38263564899046.
        // Test changed 2026-10-19 (re-pin, first run since 1.95.299): plain WLS now runs the canonical sparse step at every
        // size (the 2026-10-15 ruling), which moved the angle cap above by 2 ulps; 68 constrained iterations carry that to
        // J = 72.382635648986437 (was 72.382635648990458: 4.0e-12 absolute, about 283 ulps). The iteration count is unchanged.
        Assert.Equal(72.382635648986437, fea.ObjectiveJ);
        // Test changed 2026-10-19: the estimator holds the cap on its radian angles, but this test reads VaDeg (radians
        // times 180/pi) and converts back times pi/180, which rounds each of the two angles twice. The check allows that
        // round trip (4 ulps at the cap) and nothing more; the 2026-10-19 run landed 1 ulp over (0.10719284702809642).
        double capWithConversion = cap + 4 * (Math.BitIncrement(cap) - cap);
        Assert.True(MaxAngleDiffRad(fea) <= capWithConversion, $"cap {cap:R} not held: {MaxAngleDiffRad(fea):R}");
    }
}
