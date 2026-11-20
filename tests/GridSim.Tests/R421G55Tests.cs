using System.Numerics;
using GridSim.Core.Harmonics;
using GridSim.Core.Harmonics.Planning;
using GridSim.Core.Model;
using Xunit;

namespace GridSim.Tests;

/// <summary>
/// RC4 R4-21 (UK harmonic connection assessment): ENA EREC G5 Issue 5 (2020, docs/reference S139) tables and equations as code, each
/// pinned to the printed table value or to a summation, scaling or network result worked by hand in the test. The existing harmonic
/// code and tests are untouched (<see cref="Table_1_agrees_with_the_incumbent_headline_levels"/> pins the two to agree).
/// </summary>
public sealed class R421G55Tests
{
    // ------------------------------------------------------------------ planning levels (Tables 1 to 6 and 16)

    [Theory]
    [InlineData(0.4, 5, 4.0)] [InlineData(0.4, 13, 2.5)] [InlineData(0.4, 3, 4.0)] [InlineData(0.4, 15, 0.5)] [InlineData(0.4, 2, 1.6)] [InlineData(0.4, 10, 0.4)]
    [InlineData(11.0, 5, 3.0)] [InlineData(11.0, 7, 3.0)] [InlineData(11.0, 11, 2.0)] [InlineData(11.0, 13, 2.0)] [InlineData(11.0, 17, 1.6)] [InlineData(11.0, 19, 1.5)] [InlineData(11.0, 23, 1.2)]
    [InlineData(11.0, 3, 3.0)] [InlineData(11.0, 9, 1.2)] [InlineData(11.0, 15, 0.4)] [InlineData(11.0, 21, 0.2)] [InlineData(11.0, 27, 0.2)]
    [InlineData(11.0, 2, 1.5)] [InlineData(11.0, 4, 1.0)] [InlineData(11.0, 6, 0.5)] [InlineData(11.0, 8, 0.4)] [InlineData(11.0, 10, 0.4)] [InlineData(11.0, 12, 0.2)] [InlineData(11.0, 14, 0.2)]
    [InlineData(33.0, 5, 2.8)] [InlineData(33.0, 23, 1.0)] [InlineData(33.0, 3, 2.6)] [InlineData(33.0, 9, 1.1)] [InlineData(33.0, 2, 1.3)]
    [InlineData(132.0, 5, 2.5)] [InlineData(132.0, 7, 2.0)] [InlineData(132.0, 11, 1.8)] [InlineData(132.0, 4, 0.8)]
    [InlineData(400.0, 5, 2.0)] [InlineData(400.0, 9, 0.5)] [InlineData(400.0, 10, 0.4)] [InlineData(400.0, 3, 1.5)] [InlineData(400.0, 2, 1.0)]
    public void Planning_levels_equal_the_printed_table_values(double kv, int h, double expected)
        => Assert.Equal(expected, G55Limits.PlanningLevelPct(kv, h), 12);

    [Fact]
    public void Orders_from_25_use_the_formulae_of_each_table()
    {
        Assert.Equal(1.0, G55Limits.PlanningLevelPct(11.0, 25), 12);                 // Table 3: 25/h
        Assert.Equal(25.0 / 29.0, G55Limits.PlanningLevelPct(0.4, 29), 12);         // Table 2: 25/h
        Assert.Equal(0.6 * 1.0 + 0.2, G55Limits.PlanningLevelPct(33.0, 25), 12);    // Table 4: 0.6 (25/h) + 0.2
        Assert.Equal(0.6 * (25.0 / 37.0) + 0.2, G55Limits.PlanningLevelPct(132.0, 37), 12);   // Table 5
        Assert.Equal(0.6 * (25.0 / 49.0) + 0.2, G55Limits.PlanningLevelPct(400.0, 49), 12);   // Table 6
    }

    [Theory]
    [InlineData(0.4, 5.0)] [InlineData(0.23, 5.0)] [InlineData(0.41, 4.5)] [InlineData(11.0, 4.5)] [InlineData(25.0, 4.5)] [InlineData(25.1, 3.7)]
    [InlineData(66.0, 3.7)] [InlineData(66.1, 3.0)] [InlineData(132.0, 3.0)] [InlineData(230.0, 3.0)] [InlineData(400.0, 3.0)]
    public void Table_1_agrees_with_the_incumbent_headline_levels(double kv, double expected)
    {
        Assert.Equal(expected, G55Limits.ThdPlanningLevelPct(kv));
        Assert.Equal(expected, HarmonicCompliance.G55ThdPlanningLevelPct(kv));   // the existing encoding, not edited
    }

    [Fact]
    public void Invalid_voltages_and_orders_are_refused()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => G55Limits.Band(0.0));
        Assert.Throws<ArgumentOutOfRangeException>(() => G55Limits.Band(double.NaN));
        Assert.Throws<ArgumentOutOfRangeException>(() => G55Limits.PlanningLevelPct(11.0, 1));
    }

    [Theory]
    [InlineData(2, 1.0)] [InlineData(4, 1.0)] [InlineData(5, 1.4)] [InlineData(10, 1.4)] [InlineData(11, 2.0)] [InlineData(49, 2.0)]
    public void Table_16_exponents(int h, double alpha) => Assert.Equal(alpha, G55Limits.SummationExponent(h));

    // ------------------------------------------------------------------ summation laws, worked by hand

    [Fact]
    public void Summation_is_linear_without_declared_diversity_and_uses_the_table_exponent_with_it()
    {
        double[] v = [1.0, 2.0];
        Assert.Equal(3.0, G55Limits.Aggregate(v, 5, diversityDeclared: false), 12);               // "without which linear addition shall be employed"
        // 5th order, alpha = 1.4: (1 + 2^1.4)^(1/1.4). 2^1.4 = 2.639016; 3.639016^(0.714286) = 2.51596 (by hand: ln 3.639016 = 1.291714, / 1.4 = 0.922653, exp = 2.51596).
        Assert.Equal(2.51596, G55Limits.Aggregate(v, 5, diversityDeclared: true), 4);
        Assert.Equal(3.0, G55Limits.Aggregate(v, 3, diversityDeclared: true), 12);                 // h < 5: exponent 1
        // Order 13, alpha = 2: the root of the sum of squares, 3 and 4 give exactly 5.
        Assert.Equal(5.0, G55Limits.Aggregate([3.0, 4.0], 13, diversityDeclared: true), 9);
    }

    [Fact]
    public void Headroom_by_equations_51_and_52()
    {
        Assert.Equal(0.75, G55Limits.Headroom(3.0, 2.25, 1.0), 12);                  // 3.0 - 2.25
        Assert.Equal(4.0, G55Limits.Headroom(5.0, 3.0, 2.0), 9);                     // sqrt(25 - 9), a 3-4-5 triangle
        Assert.Equal(0.0, G55Limits.Headroom(3.0, 3.5, 1.0));                        // background above the level: no headroom, not negative
        Assert.Equal(0.0, G55Limits.Headroom(3.0, 3.0, 2.0));
    }

    // ------------------------------------------------------------------ worst-case impedance, equations (30) and (31)

    [Fact]
    public void Worst_case_impedance_for_a_resistive_source_is_the_square_root_of_h_times_the_short_circuit_impedance()
    {
        // X/R = 0: X1 = 0, so Z_h = R1 sqrt(h) with R1 = Z1 = V^2/S = 121/250.
        Assert.Equal(121.0 / 250.0 * Math.Sqrt(5.0), G55Limits.WorstCaseImpedanceOhm(250.0, 11.0, 0.0, 2.0, 5), 12);
    }

    [Fact]
    public void Worst_case_impedance_equals_the_equation_31_form()
    {
        // Equation (31): Z_h = Z1 sqrt(h + h^2 k^2 (X/R)^2) / sqrt(1 + (X/R)^2) written independently of the R1, X1 route of the code.
        double z1 = 11.0 * 11.0 / 250.0, xr = 10.0;
        foreach (int h in new[] { 2, 5, 8, 9, 20 })
        {
            double k = G55Limits.WorstCaseReactanceFactor(11.0, h);
            double expected = z1 * Math.Sqrt(h + h * h * k * k * xr * xr) / Math.Sqrt(1.0 + xr * xr);
            Assert.Equal(expected, G55Limits.WorstCaseImpedanceOhm(250.0, 11.0, xr, k, h), 10);
        }
    }

    [Theory]
    [InlineData(0.4, 7, 1.0)] [InlineData(0.4, 8, 0.5)] [InlineData(11.0, 8, 2.0)] [InlineData(11.0, 9, 1.0)] [InlineData(6.6, 2, 2.0)] [InlineData(22.0, 50, 1.0)]
    public void Table_22_reactance_factor(double kv, int h, double k) => Assert.Equal(k, G55Limits.WorstCaseReactanceFactor(kv, h));

    // ------------------------------------------------------------------ Stages 1C, 1D, 2A, 2B

    [Fact]
    public void The_reference_table_values_agree_with_the_minimum_short_circuit_constants()
    {
        // Equations (12) and (23) state the same limits as Tables 19 and 21: reference power / constant, to the table's rounding.
        Assert.InRange(10000.0 / G55StagedAssessment.Eq12SixPulse - G55StagedAssessment.Table19SixPulseKva, -0.5, 0.5);        // 21.74 vs 22
        Assert.InRange(10000.0 / G55StagedAssessment.Eq12ActiveFrontEnd - G55StagedAssessment.Table19ActiveFrontEndKva, -0.5, 0.5);   // 191.69 vs 192
        Assert.InRange(60000.0 / G55StagedAssessment.Eq23SixPulse - G55StagedAssessment.Table21SixPulseKva, -0.5, 0.5);        // 76.34 vs 76
        Assert.InRange(60000.0 / G55StagedAssessment.Eq23ActiveFrontEnd - G55StagedAssessment.Table21ActiveFrontEndKva, -0.5, 0.5);   // 673.1 vs 673
        // Equation (28) is equation (23) at 75 % background of the 5th planning level 3.0 % (headroom 0.75 %).
        Assert.Equal(G55StagedAssessment.Eq23SixPulse, G55StagedAssessment.Eq28SixPulse / 0.75, 2);   // the printed constants are rounded to three decimals
        Assert.Equal(G55StagedAssessment.Eq23ActiveFrontEnd, G55StagedAssessment.Eq28ActiveFrontEnd / 0.75, 2);
        Assert.Equal(0.75, 0.25 * G55Limits.PlanningLevelPct(11.0, 5), 12);
    }

    private static G55Equipment E(G55Technology t, double kva, string id = "e") => new(id, t, kva);

    [Fact]
    public void Stage_1C_1_scales_table_19_by_the_short_circuit_power()
    {
        G55Equipment[] six = [E(G55Technology.SixPulse, 15.0, "a"), E(G55Technology.SixPulse, 5.0, "b")];   // 20 kVA
        G55Substage at10 = G55StagedAssessment.Stage1C(six, 10.0);                                          // permitted 22 kVA
        Assert.Equal("1C-1", at10.Stage);
        Assert.Equal(G55Outcome.Pass, at10.Outcome);
        Assert.Equal(20.0, at10.Required);
        Assert.Equal(22.0, at10.Permitted);
        Assert.Equal(G55Outcome.Fail, G55StagedAssessment.Stage1C(six, 5.0).Outcome);                       // permitted 11 kVA
        Assert.Equal(11.0, G55StagedAssessment.Stage1C(six, 5.0).Permitted);
        Assert.Equal(7.9 * 3.0 / 2.0, G55StagedAssessment.Stage1C([E(G55Technology.SinglePhaseRectifier, 10.0)], 3.0).Permitted);   // single phase: 2 MVA reference
    }

    [Fact]
    public void Stage_1C_2_minimum_short_circuit_power_for_a_mix_is_equation_12()
    {
        G55Equipment[] mix = [E(G55Technology.SixPulse, 10.0), E(G55Technology.ActiveFrontEnd, 100.0)];
        double min = 459.977 * 10.0 + 52.170 * 100.0;   // 4599.77 + 5217 = 9816.77 kVA
        G55Substage ok = G55StagedAssessment.Stage1C(mix, 10.0);
        Assert.Equal("1C-2", ok.Stage);
        Assert.Equal(9816.77, ok.Required!.Value, 9);
        Assert.Equal(G55Outcome.Pass, ok.Outcome);
        Assert.Equal(G55Outcome.Fail, G55StagedAssessment.Stage1C(mix, 9.5).Outcome);
        Assert.Equal(min, G55StagedAssessment.Stage1C(mix, 9.5).Required!.Value, 9);
        // A mix with twelve-pulse plant is outside Stage 1C.
        Assert.Equal(G55Outcome.NotApplicable, G55StagedAssessment.Stage1C([E(G55Technology.SixPulse, 5), E(G55Technology.TwelvePulse, 5)], 10.0).Outcome);
    }

    [Fact]
    public void Stage_1D_scales_by_the_measured_headroom_at_the_limiting_order()
    {
        G55Equipment[] afe = [E(G55Technology.ActiveFrontEnd, 300.0)];
        // LV 5th planning level 4.0 %: 75 % background (3.0) is the Table 19 basis (ratio 1); 2.0 % measured doubles the permitted power.
        G55Substage atBasis = G55StagedAssessment.Stage1D(afe, 10.0, new Dictionary<int, double> { [5] = 3.0 });
        Assert.Equal(192.0, atBasis.Permitted!.Value, 9);
        G55Substage better = G55StagedAssessment.Stage1D(afe, 10.0, new Dictionary<int, double> { [5] = 2.0 });
        Assert.Equal(384.0, better.Permitted!.Value, 9);
        Assert.Equal(G55Outcome.Pass, better.Outcome);                          // 300 <= 384
        Assert.Equal(G55Outcome.Fail, G55StagedAssessment.Stage1D(afe, 10.0, new Dictionary<int, double> { [5] = 4.0 }).Outcome);   // no headroom
        Assert.Equal(G55Outcome.Incomplete, G55StagedAssessment.Stage1D(afe, 10.0, null).Outcome);                                 // evidence missing is never a pass
        // Twelve-pulse is limited at the 37th (Table 2: 25/37); single-phase at the 21st (0.2).
        G55Substage tw = G55StagedAssessment.Stage1D([E(G55Technology.TwelvePulse, 50.0)], 10.0, new Dictionary<int, double> { [37] = 0.75 * 25.0 / 37.0 });
        Assert.Equal(77.0, tw.Permitted!.Value, 9);
    }

    [Fact]
    public void Stage_2A_scales_table_21_from_60_MVA_and_equation_23_for_a_mix()
    {
        G55Equipment[] afe = [E(G55Technology.ActiveFrontEnd, 600.0)];
        Assert.Equal(G55Outcome.Pass, G55StagedAssessment.Stage2A(afe, 60.0).Outcome);                      // 600 <= 673
        Assert.Equal(G55Outcome.Fail, G55StagedAssessment.Stage2A([E(G55Technology.ActiveFrontEnd, 700.0)], 60.0).Outcome);
        Assert.Equal(673.0 / 2.0, G55StagedAssessment.Stage2A(afe, 30.0).Permitted);
        G55Substage mix = G55StagedAssessment.Stage2A([E(G55Technology.SixPulse, 50.0), E(G55Technology.ActiveFrontEnd, 100.0)], 60.0);
        Assert.Equal(785.962 * 50.0 + 89.143 * 100.0, mix.Required!.Value, 9);                              // 39298.1 + 8914.3 = 48212.4 kVA
        Assert.Equal(G55Outcome.Pass, mix.Outcome);                                                         // 48212.4 <= 60000
    }

    [Fact]
    public void Stage_2B_reduces_to_2A_at_75_percent_background_and_scales_with_the_headroom()
    {
        G55Equipment[] six = [E(G55Technology.SixPulse, 70.0)];
        Assert.Equal(76.0, G55StagedAssessment.Stage2B(six, 60.0, 11.0, new Dictionary<int, double> { [5] = 2.25 }).Permitted!.Value, 9);     // headroom 0.75 = 0.25 x 3.0
        Assert.Equal(152.0, G55StagedAssessment.Stage2B(six, 60.0, 11.0, new Dictionary<int, double> { [5] = 1.5 }).Permitted!.Value, 9);       // headroom 1.5: twice the permitted power
        Assert.Equal(G55Outcome.Fail, G55StagedAssessment.Stage2B(six, 60.0, 11.0, new Dictionary<int, double> { [5] = 3.0 }).Outcome);
        Assert.Equal(G55Outcome.Incomplete, G55StagedAssessment.Stage2B(six, 60.0, 11.0, null).Outcome);
        // 2B-2 at 75 % background is exactly 2A-2.
        G55Equipment[] mix = [E(G55Technology.SixPulse, 50.0), E(G55Technology.ActiveFrontEnd, 100.0)];
        double b = G55StagedAssessment.Stage2B(mix, 60.0, 11.0, new Dictionary<int, double> { [5] = 2.25 }).Required!.Value;
        double a = G55StagedAssessment.Stage2A(mix, 60.0).Required!.Value;
        Assert.Equal(a, b, 2);
    }

    // ------------------------------------------------------------------ Stage 2C, hand computed from the amps and ohms

    [Fact]
    public void Stage_2C_incremental_voltage_matches_the_derivation_from_amps_and_ohms()
    {
        // 11 kV, Ssc 100 MVA, X/R 10, 1000 kVA of plant emitting 10 % at the 5th and 5 % at the 7th (of the fundamental current).
        const double vl = 11.0, ssc = 100.0, xr = 10.0, sequ = 1000.0;
        var emission = new Dictionary<int, double> { [5] = 10.0, [7] = 5.0 };
        var bg = new Dictionary<int, double> { [5] = 1.0, [7] = 0.5 };
        G55Stage2CResult r = G55StagedAssessment.Stage2C(vl, ssc, xr, sequ, emission, true, G55Background.Measured(bg), missingBackgroundIsZero: true);

        // The long route of equation (35): rated current I = S / (sqrt3 V); I_1 = I / sqrt(1 + THDI^2); I_h = pct I_1; Z_h by equation (30); V% = 100 sqrt3 I_h Z_h / V.
        double thdI = Math.Sqrt(0.10 * 0.10 + 0.05 * 0.05);
        double iRated = sequ / (Math.Sqrt(3.0) * vl);      // A (kVA / kV)
        double i1 = iRated / Math.Sqrt(1.0 + thdI * thdI);
        double z1 = vl * vl / ssc, r1 = z1 / Math.Sqrt(1.0 + xr * xr), x1 = r1 * xr;
        double Vc(int h, double pct)
        {
            double k = 2.0;   // Table 22, 11 kV, h <= 8
            double zh = Math.Sqrt(r1 * r1 * h + k * k * h * h * x1 * x1);
            return 100.0 * Math.Sqrt(3.0) * (pct / 100.0 * i1) * zh / (vl * 1000.0);   // amps x ohm over the line voltage in volts
        }
        G55OrderRow o5 = r.Orders.Single(o => o.Order == 5);
        G55OrderRow o7 = r.Orders.Single(o => o.Order == 7);
        Assert.Equal(Vc(5, 10.0), o5.IncrementalPct, 9);
        Assert.Equal(Vc(7, 5.0), o7.IncrementalPct, 9);
        Assert.Equal(0.98910, o5.IncrementalPct, 4);                          // hand: 0.1 x 100.025 / 10.1125
        // Linear addition (no declared diversity): V_p = V_m + V_c; THDVp by equation (43) from those two orders and the zero background elsewhere.
        Assert.Equal(1.0 + Vc(5, 10.0), o5.PredictedPct, 9);
        Assert.Equal(0.5 + Vc(7, 5.0), o7.PredictedPct, 9);
        double thdP = Math.Sqrt(Math.Pow(1.0 + Vc(5, 10.0), 2) + Math.Pow(0.5 + Vc(7, 5.0), 2));
        Assert.Equal(thdP, r.ThdPredictedPct, 9);
        Assert.Equal(thdI * 100.0, r.ThdCurrentPct, 9);
        Assert.Equal(G55Outcome.Pass, r.Outcome);                              // 2.0 % <= 3.0 % at the 5th, THDV 2.2 % <= 4.5 %
        Assert.Equal(4.5, r.ThdPlanningLevelPct);

        // Five times the plant: V_c5 is five times larger (the THDI and the impedance do not change): 1.0 + 4.9455 = 5.9455 % > 3.0 %.
        G55Stage2CResult big = G55StagedAssessment.Stage2C(vl, ssc, xr, 5.0 * sequ, emission, true, G55Background.Measured(bg), missingBackgroundIsZero: true);
        Assert.Equal(5.0 * Vc(5, 10.0), big.Orders.Single(o => o.Order == 5).IncrementalPct, 9);
        Assert.Equal(G55Outcome.Fail, big.Outcome);
        Assert.False(big.Orders.Single(o => o.Order == 5).Within);
    }

    [Fact]
    public void Stage_2C_uses_the_table_16_exponent_only_when_diversity_is_declared()
    {
        var emission = new Dictionary<int, double> { [13] = 5.0 };
        var bg = new Dictionary<int, double> { [13] = 0.9 };
        G55Stage2CResult lin = G55StagedAssessment.Stage2C(11.0, 100.0, 10.0, 1000.0, emission, true, G55Background.Measured(bg), missingBackgroundIsZero: true);
        G55Stage2CResult div = G55StagedAssessment.Stage2C(11.0, 100.0, 10.0, 1000.0, emission, true, G55Background.Measured(bg), diversityDeclared: true, missingBackgroundIsZero: true);
        double vc = lin.Orders.Single(o => o.Order == 13).IncrementalPct;
        Assert.Equal(0.9 + vc, lin.Orders.Single(o => o.Order == 13).PredictedPct, 12);
        Assert.Equal(Math.Sqrt(0.9 * 0.9 + vc * vc), div.Orders.Single(o => o.Order == 13).PredictedPct, 12);   // order 13: alpha 2
        Assert.Equal(2.0, div.Orders.Single(o => o.Order == 13).AlphaUsed);
    }

    [Fact]
    public void Stage_2C_measured_background_missing_an_order_is_incomplete_and_names_it()
    {
        G55Stage2CResult r = G55StagedAssessment.Stage2C(11.0, 100.0, 10.0, 1000.0, new Dictionary<int, double> { [5] = 10.0 }, true,
            G55Background.Measured(new Dictionary<int, double> { [5] = 1.0 }), maxOrder: 8);
        Assert.Equal(G55Outcome.Incomplete, r.Outcome);
        Assert.Contains("background order 2", r.Missing);
        Assert.Contains("background order 8", r.Missing);
        Assert.DoesNotContain("background order 5", r.Missing);
    }

    [Fact]
    public void The_75_percent_assumption_applied_to_every_order_alone_exceeds_the_THDV_level_so_it_is_applied_to_THDV_as_one_level()
    {
        // Hand sum: the root of the sum of squares of 0.75 x Table 3 over orders 2 to 100 is 5.593 %, above the 4.5 % THDV planning level.
        double sum = 0.0;
        for (int h = 2; h <= 100; h++) sum += Math.Pow(0.75 * G55Limits.PlanningLevelPct(11.0, h), 2);
        Assert.InRange(Math.Sqrt(sum), 5.5934, 5.5936);
        Assert.True(Math.Sqrt(sum) > 4.5);

        // A tiny emission: THDVp^2 = (0.75 x 4.5)^2 + sum of (Vp^2 - Vm^2) with linear addition, so a 5th-order Vc adds 2 Vm Vc + Vc^2 at order 5.
        var emission = new Dictionary<int, double> { [5] = 1.0 };
        G55Stage2CResult r = G55StagedAssessment.Stage2C(11.0, 1000.0, 10.0, 100.0, emission, true, G55Background.Assumed);
        double vm5 = 0.75 * 3.0;
        double vc5 = r.Orders.Single(o => o.Order == 5).IncrementalPct;
        double expected = Math.Sqrt(Math.Pow(0.75 * 4.5, 2) + (Math.Pow(vm5 + vc5, 2) - vm5 * vm5));
        Assert.Equal(expected, r.ThdPredictedPct, 9);
        Assert.True(r.BackgroundAssumed);
        Assert.Contains("75 %", r.Note);
        Assert.Equal(G55Outcome.Pass, r.Outcome);
    }

    // ------------------------------------------------------------------ resonant plant, clause 9

    [Fact]
    public void Resonant_plant_equations_44_to_49()
    {
        // LV: Ssc_X 10 MVA, Q 0.1 MVAr: h_r = sqrt(10 / 0.1) = 10; G = sqrt(10 x 0.1) / Pd = 1 / Pd.
        G55StagedAssessment.ResonantPlantResult weak = G55StagedAssessment.ResonantPlant(0.4, 10.0, 0.1, 0.5);   // G = 2 > 1.33
        Assert.Equal(10.0, weak.ResonantOrder, 12);
        Assert.Equal(2.0, weak.MagnificationFactor, 12);
        Assert.Equal("STAGE 3", weak.Outcome);
        G55StagedAssessment.ResonantPlantResult strong = G55StagedAssessment.ResonantPlant(0.4, 10.0, 0.1, 1.0);  // G = 1
        Assert.Equal(G55Outcome.Pass, strong.Outcome);
        Assert.Equal(1.33, strong.LimitFactor);
        // Above h_r = 15 the allowed factor is 2: Ssc_X 100, Q 0.4 -> h_r = 15.81; G = sqrt(40)/Pd = 6.3246/3.5 = 1.807 <= 2.
        G55StagedAssessment.ResonantPlantResult high = G55StagedAssessment.ResonantPlant(0.4, 100.0, 0.4, 3.5);
        Assert.Equal(Math.Sqrt(250.0), high.ResonantOrder, 12);
        Assert.Equal(2.0, high.LimitFactor);
        Assert.Equal(G55Outcome.Pass, high.Outcome);
        // 11 kV: k = 2 at the resonance below 8: h_r = sqrt(100 / (1 x 2)) = 7.07; G = sqrt(100 x 1) / (Pd sqrt 2) = 10 / (10 x 1.41421) = 0.7071.
        G55StagedAssessment.ResonantPlantResult mv = G55StagedAssessment.ResonantPlant(11.0, 100.0, 1.0, 10.0);
        Assert.Equal(Math.Sqrt(50.0), mv.ResonantOrder, 12);
        Assert.Equal(1.0 / Math.Sqrt(2.0), mv.MagnificationFactor, 12);
        Assert.Equal("STAGE 3", G55StagedAssessment.ResonantPlant(33.0, 100.0, 1.0, 10.0).Outcome);   // 33 kV and above is Stage 3 outright
    }

    // ------------------------------------------------------------------ Stage 3 limits, hand worked

    [Theory]
    [InlineData(0.03, 0.1)] [InlineData(0.05, 0.1)] [InlineData(0.1, 0.2)] [InlineData(0.25, 0.5)] [InlineData(1.0, 0.66)] [InlineData(2.0, 0.66)]
    public void Apportionment_multiplier_curve_equations_57_to_60(double kM, double m) => Assert.Equal(m, G55Stage3.ApportionmentAbove132(kM), 12);

    [Fact]
    public void Apportionment_multiplier_in_the_sloping_section_and_beta_by_voltage()
    {
        Assert.Equal(16.0 / 75.0 * 0.5 + 67.0 / 150.0, G55Stage3.ApportionmentAbove132(0.5), 12);   // 0.55333
        Assert.Equal(1000.0, G55Stage3.Beta(220.0));
        Assert.Equal(1500.0, G55Stage3.Beta(275.0));
        Assert.Equal(2000.0, G55Stage3.Beta(400.0));
        Assert.Throws<ArgumentOutOfRangeException>(() => G55Stage3.Beta(330.0));
        Assert.Equal(0.5, G55Stage3.Multiplier(132.0, null));                                       // 0.5 up to 132 kV with no capacity needed
        Assert.Equal(16.0 / 75.0 * 0.5 + 67.0 / 150.0, G55Stage3.Multiplier(400.0, 1000.0), 12);     // kM = 1000 / 2000
        Assert.Throws<ArgumentException>(() => G55Stage3.Multiplier(400.0, null));
    }

    // Two 11 kV buses, a source reactance 0.2 pu at the PCC (bus 1), 0.1 pu to ground at the remote bus 2 and a 0.1 pu line between them
    // (base 100 MVA). A unit current at the PCC splits so that V2/V1 = Xg2 / (Xg2 + Xb) = 0.5: the transfer coefficient is exactly 0.5.
    private static GridModel TwoBus() => new()
    {
        Name = "r421-two-bus",
        BaseMva = 100,
        Buses = [new Bus(1, BusType.Slack, BaseKv: 11), new Bus(2, BusType.Pq, BaseKv: 11)],
        Branches = [new Branch(1, 2, 0.0, 0.1)],
        Generators = [new Generator(1, Pg: 0, MBase: 100, SubtransientXdPu: 0.2), new Generator(2, Pg: 0, MBase: 100, SubtransientXdPu: 0.1)],
    };

    private static G55Stage3Request Stage3(double i1Pu, double? assumedRemote = null, bool diversity = false, int[]? orders = null,
        IReadOnlyDictionary<int, IReadOnlyDictionary<int, double>>? remote = null)
        => new(TwoBus(), 1, [new HarmonicSource(1, "test", i1Pu, [new HarmonicComponent(5, 10.0)])],
            new HarmonicModelOptions { Orders = orders ?? [5], IncludeGeneratorSources = true },
            new Dictionary<int, double> { [5] = 1.0 }, remote, assumedRemote, diversity);

    [Fact]
    public void Stage_3_limits_transfer_the_remote_headroom_to_the_PCC_by_the_transfer_coefficient()
    {
        // Remote background 2.7 % against the 3.0 % planning level: headroom 0.3 %, transferred to the PCC by T = 0.5: 0.6 %.
        // PCC background 1.0 %: headroom 2.0 %. Limiting headroom min(0.6, 2.0) = 0.6; incremental limit M x 0.6 = 0.3 % with M = 0.5.
        var remote = new Dictionary<int, IReadOnlyDictionary<int, double>> { [2] = new Dictionary<int, double> { [5] = 2.7 } };
        G55Stage3Result r = G55Stage3.Assess(Stage3(0.01, remote: remote));
        G55Stage3Row row = Assert.Single(r.Rows);
        Assert.Equal(3.0, row.PlanningLevelPct);
        Assert.Equal(2.0, row.HeadroomPccPct, 12);
        Assert.Equal(0.6, row.HeadroomTransferredMinPct, 9);
        Assert.Equal(2, row.LimitingBus);
        Assert.Equal(0.5, row.Multiplier);
        Assert.Equal(0.3, row.IncrementalLimitPct, 9);
        Assert.Equal(1.0 + 0.5 * 2.0, row.TotalLimitPct, 12);                  // equation (63) with alpha 1: V_bg + M H_PCC = 2.0 %

        // Incremental voltage at the PCC by hand: I_h = 0.1 x 0.01 = 0.001 pu; Z = j h (0.2 || (0.1 + 0.1)) = j 5 x 0.1 = 0.5 pu; V = 0.0005 pu = 0.05 %.
        Assert.Equal(0.05, row.IncrementalPct, 9);
        Assert.True(row.IncrementalWithin);
        Assert.Equal(1.05, row.TotalPct, 9);                                   // equation (69) with alpha 1 and no change of impedance: 0.05 + 1.0
        Assert.True(row.TotalWithin);
        Assert.Equal(G55Outcome.Pass, r.Outcome);
        Assert.Equal(0.5 * 2.0 - 0.05, row.ResonantAllowancePct, 9);          // equation (65): M H_PCC - V_inc = 1.0 - 0.05 = 0.95 %
    }

    [Fact]
    public void Stage_3_fails_an_incremental_voltage_above_the_limit_and_the_total_follows()
    {
        var remote = new Dictionary<int, IReadOnlyDictionary<int, double>> { [2] = new Dictionary<int, double> { [5] = 2.7 } };
        // Ten times the current: V_inc = 0.5 % > 0.3 % limit.
        G55Stage3Result r = G55Stage3.Assess(Stage3(0.1, remote: remote));
        G55Stage3Row row = Assert.Single(r.Rows);
        Assert.Equal(0.5, row.IncrementalPct, 9);
        Assert.False(row.IncrementalWithin);
        Assert.Equal(G55Outcome.Fail, r.Outcome);
    }

    [Fact]
    public void Stage_3_without_remote_background_is_incomplete_unless_an_assumption_is_declared()
    {
        G55Stage3Result none = G55Stage3.Assess(Stage3(0.01));
        Assert.Equal(G55Outcome.Incomplete, none.Outcome);
        Assert.Contains("remote background bus 2 order 5", none.Missing);
        // Declared at 0.5 of the 3.0 % level (1.5 %): remote headroom 1.5 / 0.5 = 3.0, above the PCC's 2.0, so the PCC limits.
        G55Stage3Result assumed = G55Stage3.Assess(Stage3(0.01, assumedRemote: 0.5));
        Assert.Equal(G55Outcome.Pass, assumed.Outcome);
        Assert.Equal(1, assumed.Rows[0].LimitingBus);
        Assert.Equal(0.5 * 2.0, assumed.Rows[0].IncrementalLimitPct, 9);
        Assert.Contains(assumed.Assumptions, a => a.Contains("declared by the caller"));
    }

    [Fact]
    public void Stage_3_total_limit_with_declared_diversity_is_equation_64()
    {
        // alpha = 1.4 at the 5th: (M PL)^a + (1 - M^a) bg^a, rooted. M = 0.5, PL = 3.0, bg = 1.0.
        var remote = new Dictionary<int, IReadOnlyDictionary<int, double>> { [2] = new Dictionary<int, double> { [5] = 0.0 } };
        G55Stage3Result r = G55Stage3.Assess(Stage3(0.01, diversity: true, remote: remote));
        double a = 1.4, m = 0.5;
        double eq64 = Math.Pow(Math.Pow(m * 3.0, a) + (1.0 - Math.Pow(m, a)) * Math.Pow(1.0, a), 1.0 / a);
        Assert.Equal(eq64, r.Rows[0].TotalLimitPct, 9);
        Assert.Equal(1.4, r.Rows[0].Alpha);
    }

    [Fact]
    public void Stage_3_scales_the_incremental_voltage_by_the_impedance_ratio_of_equation_69()
    {
        // A shunt filter of impedance j 0.05 pu x h (here at order 5: j 0.25 pu) on the PCC: Z_pre = j 0.5, Z_post = j (0.5 x 0.25 / 0.75) = j 0.16667.
        var remote = new Dictionary<int, IReadOnlyDictionary<int, double>> { [2] = new Dictionary<int, double> { [5] = 0.0 } };
        G55Stage3Request req = Stage3(0.01, remote: remote) with { UserShuntImpedancePu = new Dictionary<int, Complex> { [5] = new Complex(0.0, 0.25) } };
        G55Stage3Row row = G55Stage3.Assess(req).Rows[0];
        double ratio = (0.5 * 0.25 / 0.75) / 0.5;                             // 1/3
        Assert.Equal(0.05 * ratio, row.IncrementalPct, 9);
        Assert.Equal(0.05 * ratio + ratio * 1.0, row.TotalPct, 9);            // alpha 1: V_inc + ratio x V_bg
    }

    [Fact]
    public void Stage_3_is_bit_repeatable()
    {
        G55Stage3Result a = G55Stage3.Assess(Stage3(0.01, assumedRemote: 0.5, orders: [5, 7, 11, 13]));
        G55Stage3Result b = G55Stage3.Assess(Stage3(0.01, assumedRemote: 0.5, orders: [5, 7, 11, 13]));
        Assert.Equal(a.Rows.Count, b.Rows.Count);
        for (int i = 0; i < a.Rows.Count; i++)
        {
            Assert.Equal(BitConverter.DoubleToInt64Bits(a.Rows[i].IncrementalPct), BitConverter.DoubleToInt64Bits(b.Rows[i].IncrementalPct));
            Assert.Equal(BitConverter.DoubleToInt64Bits(a.Rows[i].IncrementalLimitPct), BitConverter.DoubleToInt64Bits(b.Rows[i].IncrementalLimitPct));
        }
    }

    // ------------------------------------------------------------------ the staged route

    private static ConnectionAssessmentRequest Req(double kv, double ssc, IReadOnlyList<G55Equipment> eq, bool declared = false,
        IReadOnlyDictionary<int, double>? bg = null, bool assume = false, IReadOnlyDictionary<int, double>? emission = null)
        => new(kv, ssc, null, 10.0, eq, declared, bg, assume, emission);

    [Fact]
    public void The_route_stops_at_the_first_stage_that_passes_and_names_it()
    {
        ConnectionAssessmentResult a = ConnectionAssessmentRunner.Run(Req(0.4, 10.0, [E(G55Technology.SixPulse, 5)], declared: true));
        Assert.Equal(ConnectionAssessmentRunner.Accepted, a.Verdict);
        Assert.Equal("1A", a.Route);

        ConnectionAssessmentResult c = ConnectionAssessmentRunner.Run(Req(0.4, 10.0, [E(G55Technology.SixPulse, 20)]));
        Assert.Equal(ConnectionAssessmentRunner.Accepted, c.Verdict);
        Assert.Equal("1C-1", c.Route);
        Assert.Contains(c.Substages, s => s.Stage == "1B" && s.Outcome == G55Outcome.NotEvaluated);

        ConnectionAssessmentResult m = ConnectionAssessmentRunner.Run(Req(11.0, 60.0, [E(G55Technology.ActiveFrontEnd, 600)]));
        Assert.Equal(ConnectionAssessmentRunner.Accepted, m.Verdict);
        Assert.Equal("2A-1", m.Route);
        Assert.Equal("Table 3", m.PlanningTable);
    }

    [Fact]
    public void Thirty_three_kV_and_above_is_stage_3_outright_and_an_LV_2C_failure_is_no_connection()
    {
        Assert.Equal(ConnectionAssessmentRunner.Stage3Required, ConnectionAssessmentRunner.Run(Req(33.0, 500.0, [E(G55Technology.ActiveFrontEnd, 1000)])).Verdict);
        // LV, big six-pulse plant on a weak supply: 1C and 1D fail, 2C with the 75 % assumption fails at the 5th (3 x 0.75 + Vc >> 4.0).
        ConnectionAssessmentResult lv = ConnectionAssessmentRunner.Run(Req(0.4, 1.0, [E(G55Technology.SixPulse, 200)], assume: true));
        Assert.Equal(ConnectionAssessmentRunner.NoConnection, lv.Verdict);
        // The same at 11 kV with the same failing 2C goes on to Stage 3.
        ConnectionAssessmentResult mv = ConnectionAssessmentRunner.Run(Req(11.0, 5.0, [E(G55Technology.SixPulse, 2000)], assume: true));
        Assert.Equal(ConnectionAssessmentRunner.Stage3Required, mv.Verdict);
    }

    [Fact]
    public void Missing_background_evidence_is_incomplete_not_a_pass()
    {
        ConnectionAssessmentResult r = ConnectionAssessmentRunner.Run(Req(11.0, 5.0, [E(G55Technology.SixPulse, 2000)]));
        Assert.Equal(ConnectionAssessmentRunner.Incomplete, r.Verdict);
        Assert.Contains(r.Substages, s => s.Stage == "2B-1" && s.Outcome == G55Outcome.Incomplete);
        Assert.Contains(r.Substages, s => s.Stage == "2C" && s.Outcome == G55Outcome.Incomplete);
    }

    [Fact]
    public void A_pcc_voltage_with_no_Table_22_factor_goes_to_stage_3()
    {
        ConnectionAssessmentResult r = ConnectionAssessmentRunner.Run(Req(3.3, 50.0, [E(G55Technology.SixPulse, 2000)], assume: true));
        Assert.Equal(ConnectionAssessmentRunner.Stage3Required, r.Verdict);
        Assert.Contains(r.Notes, n => n.Contains("no worst-case reactance factor"));
    }

    [Fact]
    public void The_profile_emission_is_the_rating_weighted_sum_of_the_G5_Annex_B_profiles()
    {
        IReadOnlyDictionary<int, double> e = ConnectionAssessmentRunner.ProfileEmission([E(G55Technology.SixPulse, 100), E(G55Technology.ActiveFrontEnd, 300)]);
        Assert.Equal((31.4 * 100 + 3.4 * 300) / 400.0, e[5], 9);        // Table B.3 5th: six-pulse 31.4 %, active front end 3.4 %
        Assert.Equal((10.9 * 100 + 1.5 * 300) / 400.0, e[7], 9);
        Assert.Equal((0.0 * 100 + 0.5 * 300) / 400.0, e[3], 9);         // the six-pulse profile has no 3rd
        Assert.Throws<ArgumentException>(() => ConnectionAssessmentRunner.ProfileEmission([E(G55Technology.SinglePhaseRectifier, 10)]));
    }
}
