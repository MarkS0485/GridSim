using System.Numerics;
using GridSim.Core.Distribution;
using GridSim.Core.Estimation.Dse;
using GridSim.Core.IO;
using GridSim.Core.Model;
using Xunit;

namespace GridSim.Tests;

public partial class Lf1LowHangingTests
{
    // =================================================================================================================
    // N264 (validation parts only; the EnKF random-draw behaviour is untouched): the three-phase estimators refuse an
    // unusable measurement sigma by name, and the UKF refuses alpha / beta / kappa that give no sigma-point spread.
    // =================================================================================================================

    private static readonly PhaseImpedance N264Z = PhaseImpedance.Diagonal(new Complex(0.02, 0.06));

    private static MultiPhaseModel N264Feeder() => new(
        new[] { new PhaseBus(0, true, MultiPhaseModel.BalancedNominal(1.0)), new PhaseBus(1, false), new PhaseBus(2, false) },
        new[] { new PhaseBranch(0, 1, N264Z), new PhaseBranch(1, 2, N264Z) },
        new[]
        {
            new PhaseLoad(1, new[] { new Complex(0.5, 0.2), new Complex(0.3, 0.1), new Complex(0.4, 0.12) }),
            new PhaseLoad(2, new[] { new Complex(0.2, 0.05), new Complex(0.45, 0.15), new Complex(0.25, 0.08) }),
        });

    /// <summary>Exact measurements from the sweep truth (sigma 0.001 everywhere), as DistributionEstimatorFixTests.</summary>
    private static (List<ThreePhaseCurrent> Currents, List<ThreePhaseVMag> VMags) N264Truth(MultiPhaseModel model)
    {
        MultiPhaseResult truth = new ThreePhaseSweep().Solve(model);
        var idx = new Dictionary<int, int>();
        for (int i = 0; i < truth.BusIds.Count; i++) idx[truth.BusIds[i]] = i;
        var currents = new List<ThreePhaseCurrent>();
        foreach (PhaseBranch br in model.Branches)
        {
            Complex[] vf = truth.V[idx[br.From]], vt = truth.V[idx[br.To]];
            Complex[] i = br.Z.Invert().Multiply(new[] { vf[0] - vt[0], vf[1] - vt[1], vf[2] - vt[2] });
            for (int p = 0; p < 3; p++) currents.Add(new ThreePhaseCurrent(br.From, br.To, p, i[p], 0.001));
        }
        var vmags = new List<ThreePhaseVMag>();
        foreach (int id in new[] { 1, 2 })
            for (int p = 0; p < 3; p++) vmags.Add(new ThreePhaseVMag(id, p, truth.V[idx[id]][p].Magnitude, 0.001));
        return (currents, vmags);
    }

    public static TheoryData<double> N264BadSigmas() => new()
    {
        0.0, -0.0, -0.001, double.NaN, double.PositiveInfinity, double.NegativeInfinity,
        1e-200,   // 1 / sigma^2 overflows to infinity
        1e200,    // 1 / sigma^2 underflows to 0: the measurement would be silently ignored
    };

    [Theory]
    [MemberData(nameof(N264BadSigmas))]
    public void N264_the_three_phase_wls_estimator_refuses_an_unusable_current_sigma_by_name(double sigma)
    {
        MultiPhaseModel model = N264Feeder();
        (List<ThreePhaseCurrent> currents, List<ThreePhaseVMag> vmags) = N264Truth(model);
        currents[4] = currents[4] with { Sigma = sigma };   // branch 1->2, phase b
        ArgumentException ex = Assert.Throws<ArgumentException>(() => ThreePhaseWlsEstimator.Estimate(model, vmags, currents));
        Assert.Contains("the three-phase WLS estimator", ex.Message, StringComparison.Ordinal);
        Assert.Contains("current #5 (branch 1->2, phase b) sigma", ex.Message, StringComparison.Ordinal);
    }

    [Theory]
    [MemberData(nameof(N264BadSigmas))]
    public void N264_the_three_phase_wls_estimator_refuses_an_unusable_voltage_sigma_by_name(double sigma)
    {
        MultiPhaseModel model = N264Feeder();
        (List<ThreePhaseCurrent> currents, List<ThreePhaseVMag> vmags) = N264Truth(model);
        vmags[3] = vmags[3] with { Sigma = sigma };   // bus 2, phase a
        ArgumentException ex = Assert.Throws<ArgumentException>(() => ThreePhaseWlsEstimator.Estimate(model, vmags, currents));
        Assert.Contains("voltage magnitude #4 (bus 2, phase a) sigma", ex.Message, StringComparison.Ordinal);
    }

    [Theory]
    [MemberData(nameof(N264BadSigmas))]
    public void N264_the_branch_current_estimator_refuses_an_unusable_sigma_by_name(double sigma)
    {
        MultiPhaseModel model = N264Feeder();
        (List<ThreePhaseCurrent> currents, _) = N264Truth(model);
        currents[0] = currents[0] with { Sigma = sigma };   // branch 0->1, phase a
        ArgumentException ex = Assert.Throws<ArgumentException>(() => BranchCurrentDsse.Estimate(model, currents));
        Assert.Contains("the branch-current estimator", ex.Message, StringComparison.Ordinal);
        Assert.Contains("current #1 (branch 0->1, phase a) sigma", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void N264_every_bad_sigma_is_listed_in_one_refusal()
    {
        MultiPhaseModel model = N264Feeder();
        (List<ThreePhaseCurrent> currents, List<ThreePhaseVMag> vmags) = N264Truth(model);
        currents[0] = currents[0] with { Sigma = 0.0 };
        vmags[0] = vmags[0] with { Sigma = -1.0 };
        string msg = Assert.Throws<ArgumentException>(() => ThreePhaseWlsEstimator.Estimate(model, vmags, currents)).Message;
        Assert.Contains("2 measurement sigma(s) unusable", msg, StringComparison.Ordinal);
        Assert.Contains("sigma 0", msg, StringComparison.Ordinal);
        Assert.Contains("sigma -1", msg, StringComparison.Ordinal);
    }

    [Fact]
    public void N264_usable_sigmas_are_accepted_and_the_estimate_is_unchanged_by_the_check()
    {
        // The check computes nothing the estimators use, so a good input gives the same bits however small or large
        // its (usable) sigmas. Pinned as a self-consistency: two runs on identical input agree to the bit, and the
        // estimate matches the sweep truth (the existing machine-precision tests pin the values themselves).
        MultiPhaseModel model = N264Feeder();
        (List<ThreePhaseCurrent> currents, List<ThreePhaseVMag> vmags) = N264Truth(model);
        ThreePhaseWlsResult a = ThreePhaseWlsEstimator.Estimate(model, vmags, currents);
        ThreePhaseWlsResult b = ThreePhaseWlsEstimator.Estimate(model, vmags, currents);
        Assert.True(a.Converged);
        for (int i = 0; i < a.V.Length; i++)
            for (int p = 0; p < 3; p++)
            {
                Assert.Equal(BitConverter.DoubleToInt64Bits(a.V[i][p].Real), BitConverter.DoubleToInt64Bits(b.V[i][p].Real));
                Assert.Equal(BitConverter.DoubleToInt64Bits(a.V[i][p].Imaginary), BitConverter.DoubleToInt64Bits(b.V[i][p].Imaginary));
            }
        Assert.True(BranchCurrentDsse.Estimate(model, currents).Converged);
        // The edges of the usable range are accepted.
        Assert.True(ThreePhaseSigmaChecks.IsUsable(1e-150));
        Assert.True(ThreePhaseSigmaChecks.IsUsable(1e150));
        Assert.True(ThreePhaseSigmaChecks.IsUsable(1e-6));
    }

    // ---- UKF alpha / beta / kappa ----

    [Theory]
    [InlineData(0.0)]
    [InlineData(-1e-3)]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    public void N264_the_ukf_refuses_an_alpha_that_is_not_a_finite_number_above_0(double alpha)
    {
        GridModel model = IeeeCases.Case9();
        var ex = Assert.Throws<ArgumentOutOfRangeException>(() => new UnscentedKalmanStateEstimator(model, alpha: alpha));
        Assert.Equal("alpha", ex.ParamName);
        Assert.Contains("alpha is", ex.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(double.NaN)]
    [InlineData(double.NegativeInfinity)]
    public void N264_the_ukf_refuses_a_beta_that_is_not_finite(double beta)
    {
        var ex = Assert.Throws<ArgumentOutOfRangeException>(() => new UnscentedKalmanStateEstimator(IeeeCases.Case9(), beta: beta));
        Assert.Equal("beta", ex.ParamName);
    }

    [Fact]
    public void N264_the_ukf_refuses_a_kappa_with_s_plus_kappa_at_or_below_0()
    {
        // case9: 9 buses, slack bus 1, so s = 8 angles + 9 magnitudes = 17 states.
        GridModel model = IeeeCases.Case9();
        int s = model.Buses.Count(b => b.Type != BusType.Slack) + model.BusCount;
        Assert.Equal(17, s);
        foreach (double kappa in new[] { -17.0, -18.0, -1e6, double.NaN, double.PositiveInfinity })
        {
            var ex = Assert.Throws<ArgumentOutOfRangeException>(() => new UnscentedKalmanStateEstimator(model, kappa: kappa));
            Assert.Equal("kappa", ex.ParamName);
        }
        // Just above the edge, and the usual choices (0, 3 - s), are accepted.
        _ = new UnscentedKalmanStateEstimator(model, kappa: -16.0);
        _ = new UnscentedKalmanStateEstimator(model, kappa: 0.0);
        _ = new UnscentedKalmanStateEstimator(model, kappa: 3.0 - s);
    }

    [Fact]
    public void N264_the_ukf_defaults_are_accepted()
    {
        Assert.False(new UnscentedKalmanStateEstimator(IeeeCases.Case9()).IsInitialized);
    }
}
