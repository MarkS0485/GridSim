using GridSim.Core.Dynamics.Models;
using Xunit;

namespace GridSim.Tests;

/// <summary>
/// R083 (IEEEG1 part; the PSS1A part was done at 1.95.129): IEEEG1 lacked the valve opening / closing rate limits Uo / Uc
/// and the LP-shaft fractions K2 / K4 / K6 / K8 of the standard block diagram (IEEE Committee Report 1973, Fig. 1 and 5;
/// PSS/E IEEEG1). Both are in; with them at their defaults every number is the old model's, bit for bit (proved here
/// against the pre-R083 Step written out verbatim).
/// </summary>
public sealed partial class B3GovernorsDynamicDataTests
{
    /// <summary>The pre-R083 IEEEG1 (1.95.221), VERBATIM apart from the name and the R252 check: the bit-identity oracle.</summary>
    private sealed class Ieeeg1Pre083
    {
        private readonly double _rDroop;
        private readonly LeadLag _governor;
        private readonly FirstOrderLag _valve;
        private readonly FirstOrderLag _chest, _reheat, _crossover, _lp;
        private readonly double _k1, _k3, _k5, _k7;
        private double _pref;

        public Ieeeg1Pre083(double droopPu, double governorLeadSeconds, double governorLagSeconds, double valveTimeSeconds,
            double chestSeconds, double reheatSeconds, double crossoverSeconds, double lpSeconds,
            double k1 = 1.0, double k3 = 0.0, double k5 = 0.0, double k7 = 0.0,
            double valveMinPu = 0.0, double valveMaxPu = double.PositiveInfinity)
        {
            _rDroop = droopPu;
            _governor = new LeadLag(governorLeadSeconds, governorLagSeconds);
            _valve = new FirstOrderLag(gain: 1.0, timeConstantSeconds: valveTimeSeconds, min: valveMinPu, max: valveMaxPu);
            _chest = new FirstOrderLag(1.0, chestSeconds);
            _reheat = new FirstOrderLag(1.0, reheatSeconds);
            _crossover = new FirstOrderLag(1.0, crossoverSeconds);
            _lp = new FirstOrderLag(1.0, lpSeconds);
            _k1 = k1; _k3 = k3; _k5 = k5; _k7 = k7;
        }

        public void Initialize(double mechanicalPowerPu)
        {
            _pref = mechanicalPowerPu;
            _governor.Initialize(0.0);
            _valve.Initialize(mechanicalPowerPu);
            _chest.Initialize(mechanicalPowerPu);
            _reheat.Initialize(mechanicalPowerPu);
            _crossover.Initialize(mechanicalPowerPu);
            _lp.Initialize(mechanicalPowerPu);
        }

        public double Step(double speedDeviationPu, double dtSeconds)
        {
            double s1 = _governor.Step(-speedDeviationPu / _rDroop, dtSeconds);
            double pgv = _valve.Step(_pref + s1, dtSeconds);
            double y4 = _chest.Step(pgv, dtSeconds);
            double y5 = _reheat.Step(y4, dtSeconds);
            double y6 = _crossover.Step(y5, dtSeconds);
            double y7 = _lp.Step(y6, dtSeconds);
            return _k1 * y4 + _k3 * y5 + _k5 * y6 + _k7 * y7;
        }
    }

    [Fact]
    public void R083_ieeeg1_with_default_new_arguments_is_bit_for_bit_the_pre_R083_model()
    {
        var cases = new (Ieeeg1 New, Ieeeg1Pre083 Old, double Pm0)[]
        {
            (new Ieeeg1(0.05, 0.0, 0.2, 0.3, 0.2, 7.0, 0.4, 0.4, 0.3, 0.3, 0.2, 0.2),
             new Ieeeg1Pre083(0.05, 0.0, 0.2, 0.3, 0.2, 7.0, 0.4, 0.4, 0.3, 0.3, 0.2, 0.2), 0.8),
            (new Ieeeg1(0.04, 1.0, 0.5, 0.1, 0.3, 5.0, 0.5, 0.0, k1: 1.0, valveMinPu: 0.0, valveMaxPu: 1.0),
             new Ieeeg1Pre083(0.04, 1.0, 0.5, 0.1, 0.3, 5.0, 0.5, 0.0, k1: 1.0, valveMinPu: 0.0, valveMaxPu: 1.0), 0.9),
            (new Ieeeg1(0.05, 0.0, 0.2, 0.0, 0.25, 7.0, 0.4, 0.4, k1: 1.0),
             new Ieeeg1Pre083(0.05, 0.0, 0.2, 0.0, 0.25, 7.0, 0.4, 0.4, k1: 1.0), 0.6),
        };
        foreach ((Ieeeg1 nw, Ieeeg1Pre083 old, double pm0) in cases)
        {
            nw.Initialize(pm0);
            old.Initialize(pm0);
            for (int k = 0; k < 3000; k++)
            {
                // A speed excursion large enough to drive the second case into its valve ceiling, then back.
                double dw = k < 100 ? 0.0 : k < 1500 ? -0.004 : 0.003;
                AssertBits(old.Step(dw, 0.005), nw.Step(dw, 0.005), $"IEEEG1 pm0 {pm0} step {k}");
                Assert.Equal(0.0, nw.LowPressureShaftPowerPu);
            }
        }
    }

    [Fact]
    public void R083_valve_opening_rate_limit_gives_the_exact_ramp_then_the_exact_lag()
    {
        // Reference step 0.5 -> 0.9 with T3 = 0.1 s and Uo = 0.1 pu/s: the servo's own rate (0.4 / 0.1 = 4 pu/s) is far
        // above Uo, so the valve ramps at exactly 0.1 pu/s until the lag's rate falls to Uo, at x1 = u - Uo*T3 = 0.89,
        // reached after t1 = 0.39 / 0.1 = 3.9 s; from there it is the first-order lag 0.9 - 0.01 * exp(-(t - 3.9) / 0.1).
        const double uo = 0.1, t3 = 0.1, dt = 0.013;
        var gov = new Ieeeg1(0.05, 0.0, 0.2, t3, 0.2, 7.0, 0.4, 0.4, valveMinPu: 0.0, valveMaxPu: 1.0,
            valveOpeningRatePuPerS: uo, valveClosingRatePuPerS: -1.0);
        gov.Initialize(0.5);
        gov.SetReference(0.9);
        for (int k = 1; k <= 600; k++)
        {
            gov.Step(0.0, dt);
            double t = k * dt;
            double expected = t <= 3.9 ? 0.5 + uo * t : 0.9 - 0.01 * Math.Exp(-(t - 3.9) / t3);
            Assert.True(Math.Abs(gov.ValvePosition - expected) < 1e-12, $"t = {t:R}: valve {gov.ValvePosition:R} vs {expected:R}");
        }
    }

    [Fact]
    public void R083_valve_closing_rate_limit_and_position_floor()
    {
        // Reference step 0.8 -> 0.0 with Uc = -0.5 pu/s and T3 = 0.2 s: ramp down at 0.5 pu/s until x1 = 0 + 0.5 * 0.2 =
        // 0.1 (t1 = 1.4 s), then the lag toward 0. A reference below the floor is clamped at Pmin = 0.
        const double uc = -0.5, t3 = 0.2, dt = 0.01;
        var gov = new Ieeeg1(0.05, 0.0, 0.2, t3, 0.2, 7.0, 0.4, 0.4, valveMinPu: 0.0, valveMaxPu: 1.0,
            valveOpeningRatePuPerS: 0.1, valveClosingRatePuPerS: uc);
        gov.Initialize(0.8);
        gov.SetReference(0.0);
        for (int k = 1; k <= 200; k++)
        {
            gov.Step(0.0, dt);
            double t = k * dt;
            double expected = t <= 1.4 ? 0.8 + uc * t : 0.1 * Math.Exp(-(t - 1.4) / t3);
            Assert.True(Math.Abs(gov.ValvePosition - expected) < 1e-12, $"t = {t:R}: valve {gov.ValvePosition:R} vs {expected:R}");
        }
        gov.SetReference(-0.5);
        for (int k = 0; k < 2000; k++) gov.Step(0.0, dt);
        Assert.Equal(0.0, gov.ValvePosition);   // the position floor holds (non-windup)
    }

    [Fact]
    public void R083_lp_shaft_fractions_split_the_power_and_a_tandem_unit_holds_its_total()
    {
        // K1 + K3 on the HP shaft (0.3 + 0.3), K2 + K4 on the LP shaft (0.2 + 0.2): at equilibrium the HP shaft carries
        // 0.6 * Pmech0, the LP shaft 0.4 * Pmech0, together Pmech0.
        var gov = new Ieeeg1(0.05, 0.0, 0.2, 0.3, 0.2, 7.0, 0.4, 0.4, k1: 0.3, k3: 0.3, k2: 0.2, k4: 0.2);
        gov.Initialize(0.8);
        Assert.Equal(0.6, gov.HighPressureShaftFraction, 15);
        double hp = 0.0;
        for (int k = 0; k < 2000; k++) hp = gov.Step(0.0, 0.005);
        Assert.Equal(0.6 * 0.8, hp, 12);
        Assert.Equal(0.4 * 0.8, gov.LowPressureShaftPowerPu, 12);
        Assert.Equal(0.8, hp + gov.LowPressureShaftPowerPu, 12);
        // The total droop is still -1/R: a sustained speed rise of 0.01 pu takes 0.2 pu off the total.
        double hp2 = 0.0;
        for (int k = 0; k < 40000; k++) hp2 = gov.Step(0.01, 0.005);
        Assert.Equal(0.8 - 0.01 / 0.05, hp2 + gov.LowPressureShaftPowerPu, 4);
    }

    [Fact]
    public void R083_bad_ieeeg1_data_is_refused()
    {
        Assert.Throws<ArgumentException>(() => new Ieeeg1(0.05, 0.0, 0.2, 0.3, 0.2, 7.0, 0.4, 0.4, k1: 0.3, k3: 0.3));   // sum 0.6
        Assert.Throws<ArgumentOutOfRangeException>(() => new Ieeeg1(0.05, 0.0, 0.2, 0.3, 0.2, 7.0, 0.4, 0.4, k1: 1.2, k3: -0.2));
        Assert.Throws<ArgumentOutOfRangeException>(() => new Ieeeg1(0.05, 0.0, 0.2, 0.3, 0.2, 7.0, 0.4, 0.4, valveOpeningRatePuPerS: 0.0));
        Assert.Throws<ArgumentOutOfRangeException>(() => new Ieeeg1(0.05, 0.0, 0.2, 0.3, 0.2, 7.0, 0.4, 0.4, valveClosingRatePuPerS: 0.1));
        Assert.Throws<ArgumentOutOfRangeException>(() => new Ieeeg1(0.05, 0.0, 0.2, 0.3, 0.2, 7.0, 0.4, 0.4, valveOpeningRatePuPerS: double.NaN));
        // A lead with no lag is improper and stays refused.
        Assert.Throws<ArgumentOutOfRangeException>(() => new Ieeeg1(0.05, 0.5, 0.0, 0.3, 0.2, 7.0, 0.4, 0.4));
    }

    [Fact]
    public void R083_zero_governor_lead_and_lag_is_the_pure_droop_gain()
    {
        // T1 = T2 = 0 (common in PSS/E IEEEG1 data) used to throw; it is the pure gain K = 1/R. With T3 = 0 and all
        // steam lags 0 the whole governor is algebraic: Pmech = Pref - dw / R on the very first step.
        var gov = new Ieeeg1(0.05, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, valveMinPu: -10.0, valveMaxPu: 10.0);
        gov.Initialize(0.7);
        Assert.Equal(0.7 - 0.002 / 0.05, gov.Step(0.002, 0.01), 15);
    }
}
