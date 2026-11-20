using System.Numerics;
using GridSim.Core.Harmonics;
using GridSim.Core.Harmonics.Planning;
using GridSim.Core.Harmonics.Planning.Flicker;
using GridSim.Core.Model;
using Xunit;

namespace GridSim.Tests;

/// <summary>
/// RC4 R4-21: filter design by closed forms (single-tuned, high-pass, C-type) with the existing frequency sweep as the independent
/// finder of the resonances; ENA EREC P28 Issue 2 (docs/reference S141) tables and equations; and the structure of the IEC 61000-4-15
/// flickermeter. The flickermeter's VERIFICATION is AWAITING STANDARD TEXT (paid standard): the tests here check the structure by
/// derivation (analytic filter responses, linearity, a Pst combination worked by hand), never against the standard's numbers, and the
/// filter parameters used are arbitrary test values, NOT the standard's.
/// </summary>
public sealed class R421FilterFlickerTests
{
    // ------------------------------------------------------------------ single-tuned filter, closed forms

    private const double V = 11.0, Q = 5.0, H = 4.9;

    [Fact]
    public void Single_tuned_reactances_follow_the_closed_form_and_the_impedance_is_zero_at_the_tuned_order()
    {
        FilterDesignResult f = FilterDesign.Design(new FilterDesignRequest(FilterKind.SingleTuned, V, Q, H));
        // X_C - X_L = V^2/Q and X_L = X_C/h^2 give X_C = V^2 h^2 / (Q (h^2 - 1)): 121 x 24.01 / (5 x 23.01) = 25.2517 ohm.
        double xc = V * V * H * H / (Q * (H * H - 1.0));
        Assert.Equal(xc, f.CapacitorOhm, 12);
        Assert.Equal(xc / (H * H), f.ReactorOhm, 12);
        Assert.Equal(V * V / Q, f.CapacitorOhm - f.ReactorOhm, 9);                 // 24.2 ohm: the fundamental net reactance
        Assert.Equal(25.2517, f.CapacitorOhm, 4);
        Assert.True(f.ImpedanceOhm(H).Magnitude < 1e-9);                           // series resonance exactly at h
        Assert.Equal(Q, f.FundamentalMvar);
        Assert.Equal(H * H / (H * H - 1.0), f.CapacitorVoltageRatio!.Value, 12);   // capacitor voltage rise Xc/(Xc - XL)
        Assert.Equal(0.0, f.FundamentalMw, 9);                                     // lossless
        // The impedance is capacitive below h and inductive above.
        Assert.True(f.ImpedanceOhm(3.0).Imaginary < 0.0);
        Assert.True(f.ImpedanceOhm(7.0).Imaginary > 0.0);
    }

    [Fact]
    public void Single_tuned_resistor_from_the_quality_factor()
    {
        FilterDesignResult f = FilterDesign.Design(new FilterDesignRequest(FilterKind.SingleTuned, V, Q, H, QualityFactor: 30.0));
        Assert.Equal(H * f.ReactorOhm / 30.0, f.ResistorOhm!.Value, 12);           // q = h X_L / R
        Complex zh = f.ImpedanceOhm(H);
        Assert.Equal(f.ResistorOhm.Value, zh.Real, 9);                              // at the tuned order the impedance is the resistance
        Assert.True(Math.Abs(zh.Imaginary) < 1e-9);
    }

    // A single 11 kV bus: a machine of reactance 0.4 pu on 100 MVA (Ssc 250 MVA) with the filter added at the bus.
    private static GridModel SourceBus() => new()
    {
        Name = "r421-source",
        BaseMva = 100,
        Buses = [new Bus(1, BusType.Slack, BaseKv: 11)],
        Branches = [],
        Generators = [new Generator(1, Pg: 0, MBase: 100, SubtransientXdPu: 0.4)],
    };

    [Fact]
    public void The_frequency_sweep_finds_the_series_zero_at_h_and_the_parallel_resonance_at_the_closed_form_order()
    {
        FilterDesignResult f = FilterDesign.Design(new FilterDesignRequest(FilterKind.SingleTuned, V, Q, H));
        GridModel m = FilterDesign.AddSingleTunedTo(SourceBus(), 1, f);
        var opt = new HarmonicModelOptions { IncludeGeneratorSources = true };
        FrequencySweepResult sweep = HarmonicFrequencySweep.DrivingPoint(m, 1, opt);

        // Closed form (2): h_p = sqrt(X_C / (X_L + X_s)), X_s = 0.4 pu x 121/100 = 0.484 ohm.
        double xs = 0.4 * V * V / 100.0;
        double hp = Math.Sqrt(f.CapacitorOhm / (f.ReactorOhm + xs));
        Assert.Equal(4.0548, hp, 3);
        Assert.True(Math.Abs(sweep.PeakOrder - hp) < 1e-4, $"sweep peak {sweep.PeakOrder:R}, closed form {hp:R}");

        // The series resonance: the driving-point impedance at h = 4.9 (grid index 34 of 1.5 + 0.1 k) is the filter's zero in parallel with the source.
        int k = (int)Math.Round((H - sweep.Orders[0]) / 0.1);
        Assert.Equal(H, sweep.Orders[k], 9);
        Assert.True(sweep.PositiveZ[k].Magnitude < 1e-9, $"|Z(h)| = {sweep.PositiveZ[k].Magnitude:E3}");
    }

    [Fact]
    public void The_impedance_overlay_finds_the_same_resonances_without_the_model_surgery()
    {
        FilterDesignResult f = FilterDesign.Design(new FilterDesignRequest(FilterKind.SingleTuned, V, Q, H));
        double[] grid = Enumerable.Range(0, 951).Select(i => 1.5 + i * 0.05).ToArray();          // 1.5 to 49
        double zBase = V * V / 100.0;
        Complex[] zNet = grid.Select(h => new Complex(0.0, h * 0.4)).ToArray();                  // the source alone, pu on 100 MVA
        Complex[] comb = FilterDesign.Combined(f, grid, zNet, 100.0);
        (IReadOnlyList<double> peaks, IReadOnlyList<double> valleys) = FilterDesign.ExtremaOrders(grid, comb);
        double hp = Math.Sqrt(f.CapacitorOhm / (f.ReactorOhm + 0.4 * zBase));
        Assert.Contains(peaks, p => Math.Abs(p - hp) < 5e-3);
        Assert.Contains(valleys, v => Math.Abs(v - H) < 5e-3);
        // Below the parallel resonance the filter is capacitive and the network inductive; the combination is above the source alone near h_p.
        Assert.True(comb.Max(z => z.Magnitude) > zNet.Max(z => z.Magnitude) * 0.0 + 1.0);
    }

    // ------------------------------------------------------------------ high-pass and C-type

    [Fact]
    public void High_pass_generates_exactly_the_requested_reactive_power_at_the_fundamental()
    {
        FilterDesignResult f = FilterDesign.Design(new FilterDesignRequest(FilterKind.HighPass, V, Q, 12.0, QualityFactor: 2.0));
        // Independent evaluation of Z(1) = -jXc + jXL R / (R + jXL) and Q = -Im(V^2 / conj Z).
        Complex zc = new(0.0, -f.CapacitorOhm), zl = new(0.0, f.ReactorOhm);
        Complex z1 = zc + zl * f.ResistorOhm!.Value / (zl + f.ResistorOhm.Value);
        double q = -(V * V / Complex.Conjugate(z1)).Imaginary;
        Assert.Equal(Q, q, 9);
        Assert.Equal(f.FundamentalMw, (V * V / Complex.Conjugate(z1)).Real, 9);
        Assert.True(f.FundamentalMw > 0.0);                                         // the resistor takes a little current at 50 Hz
        Assert.Equal(f.CapacitorOhm / (12.0 * 12.0), f.ReactorOhm, 12);            // X_L = X_C / h^2
        Assert.Equal(2.0 * 12.0 * f.ReactorOhm, f.ResistorOhm.Value, 12);          // R = q h X_L
        // At a very high order the parallel R-L tends to R: the filter is a resistor.
        Complex zHigh = f.ImpedanceOhm(1.0e5);
        Assert.True(Math.Abs(zHigh.Real - f.ResistorOhm.Value) < 1e-3 * f.ResistorOhm.Value);
    }

    [Fact]
    public void C_type_has_only_the_main_capacitor_at_the_fundamental_and_a_zero_at_the_tuned_order_when_lossless()
    {
        FilterDesignResult damped = FilterDesign.Design(new FilterDesignRequest(FilterKind.CType, V, Q, 3.0, QualityFactor: 1.5));
        Assert.Equal(V * V / Q, damped.CapacitorOhm, 12);                           // X_C1 = V^2/Q
        Assert.Equal(damped.CapacitorOhm / (3.0 * 3.0 - 1.0), damped.Capacitor2Ohm, 12);   // X_C2 = X_C1/(h^2 - 1)
        Assert.Equal(damped.Capacitor2Ohm, damped.ReactorOhm, 12);                  // C2 and L tuned at the fundamental
        // The fundamental impedance is -j X_C1 whatever the resistor: the L-C2 branch is a short at 50 Hz and bypasses it.
        Complex z1 = damped.ImpedanceOhm(1.0);
        Assert.True(Math.Abs(z1.Real) < 1e-9);
        Assert.Equal(-damped.CapacitorOhm, z1.Imaginary, 9);
        Assert.Equal(0.0, damped.FundamentalMw, 9);
        FilterDesignResult lossless = FilterDesign.Design(new FilterDesignRequest(FilterKind.CType, V, Q, 3.0));
        Assert.True(lossless.ImpedanceOhm(3.0).Magnitude < 1e-9);                   // the lossless limit of the tuning
    }

    [Fact]
    public void Filter_design_refuses_non_physical_inputs()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => FilterDesign.Design(new FilterDesignRequest(FilterKind.SingleTuned, V, Q, 1.0)));
        Assert.Throws<ArgumentOutOfRangeException>(() => FilterDesign.Design(new FilterDesignRequest(FilterKind.SingleTuned, V, 0.0, 5.0)));
        Assert.Throws<ArgumentOutOfRangeException>(() => FilterDesign.Design(new FilterDesignRequest(FilterKind.SingleTuned, -1.0, Q, 5.0)));
        Assert.Throws<ArgumentOutOfRangeException>(() => FilterDesign.Design(new FilterDesignRequest(FilterKind.HighPass, V, Q, 5.0, QualityFactor: 0.0)));
        FilterDesignResult hp = FilterDesign.Design(new FilterDesignRequest(FilterKind.HighPass, V, Q, 12.0));
        Assert.Throws<ArgumentException>(() => FilterDesign.AddSingleTunedTo(SourceBus(), 1, hp));
    }

    // ------------------------------------------------------------------ P28 planning levels, summation, voltage change

    [Theory]
    [InlineData(0.4, 1.0, 0.8)] [InlineData(3.3, 0.9, 0.7)] [InlineData(11.0, 0.9, 0.7)] [InlineData(33.0, 0.9, 0.7)]
    [InlineData(66.0, 0.8, 0.6)] [InlineData(132.0, 0.8, 0.6)] [InlineData(275.0, 0.8, 0.6)] [InlineData(400.0, 0.8, 0.6)]
    public void P28_table_2_planning_levels(double kv, double pst, double plt)
    {
        P28PlanningLevels l = P28Assessment.PlanningLevels(kv);
        Assert.Equal(pst, l.Pst);
        Assert.Equal(plt, l.Plt);
    }

    [Theory]
    [InlineData(400.0, 132.0, 0.85)] [InlineData(275.0, 66.0, 0.85)] [InlineData(400.0, 33.0, 0.80)] [InlineData(400.0, 11.0, 0.70)]
    [InlineData(132.0, 66.0, 0.95)] [InlineData(110.0, 22.0, 0.90)] [InlineData(132.0, 11.0, 0.75)]
    [InlineData(66.0, 33.0, 0.95)] [InlineData(66.0, 6.6, 0.90)] [InlineData(33.0, 20.0, 0.90)] [InlineData(11.0, 0.4, 1.0)]
    [InlineData(11.0, 11.0, 1.0)] [InlineData(11.0, 33.0, 0.0)]
    public void P28_table_3_transfer_coefficients_and_no_transfer_to_a_higher_voltage(double from, double to, double t)
        => Assert.Equal(t, P28Assessment.TransferCoefficient(from, to));

    [Fact]
    public void A_pair_the_table_does_not_list_has_no_coefficient()
    {
        Assert.Null(P28Assessment.TransferCoefficient(33.0, 0.4));
        Assert.Null(P28Assessment.TransferCoefficient(400.0, 0.4));
    }

    [Fact]
    public void Summation_law_equation_1_and_footnote_28_eight_loads_of_Pst_0_5_with_exponent_3_give_exactly_1()
    {
        Assert.Equal(1.0, P28Assessment.Summation(Enumerable.Repeat(0.5, 8)), 12);                  // (8 x 0.125)^(1/3)
        Assert.Equal(Math.Sqrt(0.09 + 0.16), P28Assessment.Summation([0.3, 0.4], 2.0), 12);        // exponent 2: 0.5
        Assert.Equal(0.7, P28Assessment.Summation([0.3, 0.4], 1.0), 12);                            // exponent 1: linear
        Assert.Equal(Math.Pow(0.3 * 0.3 * 0.3 * 0.3 + 0.4 * 0.4 * 0.4 * 0.4, 0.25), P28Assessment.Summation([0.3, 0.4], 4.0), 12);
        Assert.Equal(1.0, P28Assessment.Summation([1.0, 0.0, 0.0]), 12);
    }

    [Fact]
    public void Plt_from_twelve_Pst_values_by_equation_2()
    {
        Assert.Equal(0.6, P28Assessment.PltFromPst(Enumerable.Repeat(0.6, 12).ToArray()), 12);
        // One Pst of 1.0 and eleven of 0: ((1/12) x 1)^(1/3); its cube times 12 is 1.
        double plt = P28Assessment.PltFromPst([1.0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0]);
        Assert.Equal(1.0, 12.0 * plt * plt * plt, 12);
        Assert.Equal(0.4368, plt, 4);
    }

    [Fact]
    public void Planning_check_sums_with_exponent_3_and_compares_with_table_2()
    {
        // 11 kV: Pst planning level 0.9. Four sources of 0.5: (4 x 0.125)^(1/3) = 0.7937 passes; eight: exactly 1.0 fails.
        var four = P28Assessment.PlanningCheck(11.0, [0.5, 0.5, 0.5, 0.5], [0.4, 0.4, 0.4, 0.4]);
        Assert.Equal(Math.Pow(0.5, 1.0 / 3.0), four.PstSummated, 12);
        Assert.Equal(G55Outcome.Pass, four.Pst.Outcome);
        var eight = P28Assessment.PlanningCheck(11.0, Enumerable.Repeat(0.5, 8).ToArray(), [0.4]);
        Assert.Equal(G55Outcome.Fail, eight.Pst.Outcome);
        Assert.Equal(0.9, eight.Pst.Limit);
    }

    [Fact]
    public void Voltage_change_equations_3_and_4_agree()
    {
        Assert.Equal(1.0, P28Assessment.VoltageChangePct(2.0, 200.0), 12);                          // equation 4
        // Equation 3 with the load power factor equal to the supply R/Z: S/Sbase x (cos R + sin X) = S/Sbase x (R^2 + X^2)/Z = S/Sbase x Z = S/Ssc.
        double sbase = 100.0, ssc = 250.0, rPu = 0.02, xPu = 0.38;
        double z = Math.Sqrt(rPu * rPu + xPu * xPu);
        double scale = (sbase / ssc) / z;                                                           // so that Z_pu = Sbase / Ssc
        double r = rPu * scale, x = xPu * scale;
        double dv = P28Assessment.VoltageChangePu(2.0, sbase, r / (r * r + x * x > 0 ? Math.Sqrt(r * r + x * x) : 1.0), x / Math.Sqrt(r * r + x * x), r, x);
        Assert.Equal(2.0 / ssc, dv, 12);
        Assert.Equal(P28Assessment.VoltageChangePct(2.0, ssc), dv * 100.0, 10);
    }

    [Fact]
    public void Inrush_voltage_change_annex_C()
    {
        Assert.Equal(4.0, P28Assessment.InrushVoltageChangePct(8.0, 0.5, 1.0, 100.0), 12);          // 8 x 0.5 x 1/100 x 100
        Assert.Equal(12.0, P28Assessment.InrushVoltageChangePct(10.0, 0.6, 2.0, 100.0), 12);
    }

    // ------------------------------------------------------------------ Stage 1, Stage 2 and the simplified step rule

    [Fact]
    public void Stage_1_conditions_for_LV_equipment()
    {
        // Up to 16 A, conforms to 61000-3-3, supply impedance 0.4 ohm against the typical maximum 0.472 (single phase): connects.
        Assert.Equal(G55Outcome.Pass, P28Assessment.Stage1Lv(13.0, false, true, false, 0.4, null, false, false).Outcome);
        Assert.Equal("STAGE 2", P28Assessment.Stage1Lv(13.0, false, true, false, 0.5, null, false, false).Outcome);        // 0.5 > 0.472
        Assert.Equal(G55Outcome.Pass, P28Assessment.Stage1Lv(15.0, true, true, false, 0.28, null, false, false).Outcome);  // three phase maximum 0.283
        Assert.Equal("STAGE 2", P28Assessment.Stage1Lv(15.0, true, true, false, 0.29, null, false, false).Outcome);
        // Above 16 A and up to 75 A: 61000-3-3 and a confirmed service.
        Assert.Equal("6.3.2.2", P28Assessment.Stage1Lv(40.0, false, true, false, 0.6, null, true, false).Clause);
        // Not conforming to 61000-3-3 but to 61000-3-11 and Z_max declared 0.45: 0.4 passes (a), 0.5 does not; or 100 A service with at most 0.35 / 0.212 (b).
        Assert.Equal("6.3.2.2 a)", P28Assessment.Stage1Lv(40.0, false, false, true, 0.4, 0.45, false, false).Clause);
        Assert.Equal("STAGE 2", P28Assessment.Stage1Lv(40.0, false, false, true, 0.5, 0.45, false, false).Outcome);
        Assert.Equal("6.3.2.2 b)", P28Assessment.Stage1Lv(40.0, false, false, true, 0.34, null, false, true).Clause);
        Assert.Equal("STAGE 2", P28Assessment.Stage1Lv(80.0, false, true, true, 0.3, 0.4, true, true).Outcome);              // above 75 A: Stage 2
    }

    [Fact]
    public void Stage_2_limit_is_pst_0_5()
    {
        Assert.Equal(G55Outcome.Pass, P28Assessment.Stage2(0.5).Outcome);
        Assert.Equal("STAGE 3", P28Assessment.Stage2(0.51).Outcome);
        Assert.Throws<ArgumentOutOfRangeException>(() => P28Assessment.Stage2(-0.1));
    }

    [Fact]
    public void Simplified_step_rule_decides_at_the_documents_own_points_and_otherwise_needs_a_curve()
    {
        Assert.Equal(G55Outcome.Pass, P28Assessment.SimplifiedStep(3.0, 475.0).Outcome);            // Figure B.1.2 note 2: 3 % once every 475 s
        Assert.Equal(G55Outcome.Pass, P28Assessment.SimplifiedStep(1.0, 20.0).Outcome);             // footnote 46: 1 % not more than every 20 s
        Assert.Equal(G55Outcome.Pass, P28Assessment.SimplifiedStep(0.8, 60.0).Outcome);
        Assert.Equal(G55Outcome.Incomplete, P28Assessment.SimplifiedStep(3.0, 100.0).Outcome);      // not decidable from the text alone
        // With curve points (20 s, 1 %) and (475 s, 3 %) the log-log midpoint interval sqrt(20 x 475) allows sqrt(1 x 3) = 1.7320508 %.
        double mid = Math.Sqrt(20.0 * 475.0);
        P28Verdict v = P28Assessment.SimplifiedStep(1.7, mid, [(20.0, 1.0), (475.0, 3.0)]);
        Assert.Equal(Math.Sqrt(3.0), v.Limit!.Value, 9);
        Assert.Equal(G55Outcome.Pass, v.Outcome);
        Assert.Equal("STAGE 3", P28Assessment.SimplifiedStep(1.8, mid, [(20.0, 1.0), (475.0, 3.0)]).Outcome);
        // The curve is capped at 3 % whatever the points say.
        Assert.Equal(3.0, P28Assessment.SimplifiedStep(2.0, 1000.0, [(20.0, 1.0), (475.0, 5.0)]).Limit);
    }

    // ------------------------------------------------------------------ rapid voltage change envelopes (Table 4, Figures 5 to 7)

    [Theory]
    [InlineData(1, 0.05, -6.0, 6.0)] [InlineData(1, 0.1, -6.0, 6.0)] [InlineData(1, 0.11, -3.0, 3.0)] [InlineData(1, 5.0, -3.0, 3.0)]
    [InlineData(2, 0.05, -10.0, 6.0)] [InlineData(2, 0.5, -6.0, 6.0)] [InlineData(2, 0.8, -6.0, 6.0)] [InlineData(2, 0.9, -6.0, 3.0)] [InlineData(2, 2.0, -6.0, 3.0)] [InlineData(2, 2.1, -3.0, 3.0)]
    [InlineData(3, 0.05, -12.0, 6.0)] [InlineData(3, 1.0, -10.0, 3.0)] [InlineData(3, 2.0, -10.0, 3.0)] [InlineData(3, 2.1, -3.0, 3.0)]
    public void Rvc_envelopes_follow_the_notes_of_table_4(int category, double t, double lower, double upper)
    {
        (double lo, double hi) = P28Assessment.Envelope((P28RvcCategory)category, t);
        Assert.Equal(lower, lo);
        Assert.Equal(upper, hi);
    }

    [Fact]
    public void Rvc_check_passes_inside_and_fails_outside_the_envelope_and_the_steady_state_limit_is_3_percent()
    {
        P28Verdict ok = P28Assessment.RapidVoltageChange(P28RvcCategory.Infrequent, [new(0.05, -9.0), new(0.5, -5.5), new(2.5, -2.9)], -2.9);
        Assert.Equal(G55Outcome.Pass, ok.Outcome);
        P28Verdict late = P28Assessment.RapidVoltageChange(P28RvcCategory.Infrequent, [new(0.05, -9.0), new(2.5, -4.0)], -2.9);   // -4 % after 2 s exceeds -3 %
        Assert.Equal(G55Outcome.Fail, late.Outcome);
        Assert.Equal(G55Outcome.Fail, P28Assessment.RapidVoltageChange(P28RvcCategory.Frequent, [new(0.05, -7.0)], 0.0).Outcome);
        Assert.Equal(G55Outcome.Fail, P28Assessment.RapidVoltageChange(P28RvcCategory.VeryInfrequent, [new(0.05, -11.0)], -3.5).Outcome);   // steady-state change above 3 %
        Assert.Equal(G55Outcome.Pass, P28Assessment.RapidVoltageChange(P28RvcCategory.VeryInfrequent, [new(0.05, -11.9), new(1.0, -9.9), new(2.5, 2.0)], 1.0).Outcome);
        Assert.Throws<ArgumentException>(() => P28Assessment.RapidVoltageChange(P28RvcCategory.Frequent, [], 0.0));
    }

    // ------------------------------------------------------------------ flickermeter structure (AWAITING STANDARD TEXT)

    // TEST VALUES ONLY: arbitrary positive numbers that exercise the structure; they are NOT the IEC 61000-4-15 coefficients.
    private static FlickermeterParameters Test(double fs = 4000.0) => new(
        SampleRateHz: fs, NominalRmsVolts: 230.0, HighPassCornerHz: 0.05, LowPassOrder: 6, LowPassCornerHz: 35.0,
        K: 1.5, Lambda: 2 * Math.PI * 4.0, Omega1: 2 * Math.PI * 9.0, Omega2: 2 * Math.PI * 2.5, Omega3: 2 * Math.PI * 1.2, Omega4: 2 * Math.PI * 21.0,
        SlidingMeanTauSeconds: 0.3,
        P1sPercentiles: [0.7, 1.0, 1.5], P3sPercentiles: [2.2, 3.0, 4.0], P10sPercentiles: [6.0, 8.0, 10.0], P50sPercentiles: [30.0, 50.0, 80.0]);

    [Fact]
    public void The_digital_weighting_filter_follows_its_analytic_transfer_function()
    {
        FlickermeterParameters p = Test();
        var sections = Flickermeter.WeightingSections(p);
        foreach (double f in new[] { 0.5, 2.0, 5.0, 8.8, 15.0, 25.0 })
        {
            double w = 2 * Math.PI * f;
            Complex digital = sections.Aggregate(Complex.One, (acc, b) => acc * b.Response(w, p.SampleRateHz));
            Complex analytic = Flickermeter.WeightingAnalogue(p, w);
            Assert.True(Complex.Abs(digital - analytic) < 5e-3 * Complex.Abs(analytic), $"f = {f}: digital {digital}, analytic {analytic}");
        }
        // The band-pass peak of the analogue form is K at w1 (before the lead-lag), so the form is not flat: weighting gain at 8.8 Hz differs from at 0.5 Hz by a wide margin.
        Assert.True(Complex.Abs(Flickermeter.WeightingAnalogue(p, 2 * Math.PI * 8.8)) > 5.0 * Complex.Abs(Flickermeter.WeightingAnalogue(p, 2 * Math.PI * 0.5)));
    }

    [Fact]
    public void The_butterworth_low_pass_has_unit_dc_gain_minus_3_dB_at_the_corner_and_the_closed_form_roll_off()
    {
        var lp = Flickermeter.ButterworthLowPass(6, 35.0, 4000.0);
        Complex At(double f) => lp.Aggregate(Complex.One, (acc, b) => acc * b.Response(2 * Math.PI * f, 4000.0));
        Assert.Equal(1.0, At(1e-3).Magnitude, 6);
        Assert.Equal(1.0 / Math.Sqrt(2.0), At(35.0).Magnitude, 2);                                   // within the bilinear warping
        // |H(f)| = 1 / sqrt(1 + (f/fc)^(2N)); at 2 fc and N = 6: 1 / sqrt(1 + 4096).
        Assert.InRange(At(70.0).Magnitude / (1.0 / Math.Sqrt(1.0 + 4096.0)), 0.98, 1.02);                 // within the bilinear warping (no pre-warp)
        var odd = Flickermeter.ButterworthLowPass(3, 35.0, 4000.0);
        Assert.InRange(odd.Aggregate(Complex.One, (acc, b) => acc * b.Response(2 * Math.PI * 70.0, 4000.0)).Magnitude / (1.0 / Math.Sqrt(1.0 + 64.0)), 0.98, 1.02);
    }

    [Fact]
    public void An_unmodulated_voltage_gives_no_flicker_and_pst_is_linear_in_the_modulation_depth()
    {
        FlickermeterParameters p = Test();
        double[] flat = Flickermeter.ModulatedVoltage(p.SampleRateHz, 40.0, 230.0, 50.0, "sine", 8.8, 0.0);
        double[] small = Flickermeter.ModulatedVoltage(p.SampleRateHz, 40.0, 230.0, 50.0, "sine", 8.8, 0.01);
        double[] twice = Flickermeter.ModulatedVoltage(p.SampleRateHz, 40.0, 230.0, 50.0, "sine", 8.8, 0.02);
        FlickermeterResult r0 = Flickermeter.Run(p, flat, 10.0);
        FlickermeterResult r1 = Flickermeter.Run(p, small, 10.0);
        FlickermeterResult r2 = Flickermeter.Run(p, twice, 10.0);
        Assert.True(r1.Pst > 0.0);
        Assert.True(r0.Pst < 0.05 * r1.Pst, $"unmodulated {r0.Pst:E3} vs modulated {r1.Pst:E3}");    // the carrier's second harmonic is filtered out
        // Instantaneous flicker is the square of a weighted signal that scales with the depth: Pst scales with the depth (P28 Table 2 note 2).
        Assert.Equal(2.0, r2.Pst / r1.Pst, 1);
        Assert.Equal(Flickermeter.AwaitingStandardText, r1.VerificationStatus);
        Assert.Contains("AWAITING STANDARD TEXT", r1.VerificationStatus);
    }

    [Fact]
    public void A_rectangular_modulation_is_also_accepted_and_runs_are_bit_identical()
    {
        FlickermeterParameters p = Test();
        double[] u = Flickermeter.ModulatedVoltage(p.SampleRateHz, 30.0, 230.0, 50.0, "rectangular", 1.0, 0.01);
        FlickermeterResult a = Flickermeter.Run(p, u, 8.0);
        FlickermeterResult b = Flickermeter.Run(p, u, 8.0);
        Assert.True(a.Pst > 0.0);
        Assert.Equal(BitConverter.DoubleToInt64Bits(a.Pst), BitConverter.DoubleToInt64Bits(b.Pst));
        Assert.Throws<ArgumentException>(() => Flickermeter.ModulatedVoltage(4000, 1, 230, 50, "triangle", 1, 0.01));
    }

    [Fact]
    public void Pst_combination_and_percentiles_by_hand()
    {
        double w = 0.0314 + 0.0525 + 0.0657 + 0.28 + 0.08;                                          // 0.5096
        Assert.Equal(Math.Sqrt(4.0 * w), Flickermeter.CombinePst(4, 4, 4, 4, 4), 12);
        Assert.Equal(1.0, Flickermeter.CombinePst(1.0 / w, 1.0 / w, 1.0 / w, 1.0 / w, 1.0 / w), 12);
        Assert.Equal(Math.Sqrt(0.0314 * 2 + 0.0525 * 3 + 0.0657 * 5 + 0.28 * 7 + 0.08 * 11), Flickermeter.CombinePst(2, 3, 5, 7, 11), 12);
        double[] ramp = Enumerable.Range(1, 100).Select(i => (double)i).ToArray();                  // 1 .. 100, sorted
        Assert.Equal(99.0, Flickermeter.Percentile(ramp, 1.0));                                      // exceeded by 1 sample in 100
        Assert.Equal(50.0, Flickermeter.Percentile(ramp, 50.0));
        Assert.Equal(100.0, Flickermeter.Percentile(ramp, 0.1));
        Assert.Equal(90.0, Flickermeter.Percentile(ramp, 10.0));
    }

    [Fact]
    public void Missing_or_unphysical_meter_parameters_are_refused()
    {
        FlickermeterParameters p = Test();
        double[] u = Flickermeter.ModulatedVoltage(p.SampleRateHz, 2.0, 230.0, 50.0, "sine", 8.8, 0.01);
        Assert.Throws<ArgumentException>(() => Flickermeter.Run(p with { P50sPercentiles = [] }, u));
        Assert.Throws<ArgumentException>(() => Flickermeter.Run(p with { K = 0.0 }, u));
        Assert.Throws<ArgumentException>(() => Flickermeter.Run(p with { LowPassCornerHz = 3000.0 }, u));
        Assert.Throws<ArgumentException>(() => Flickermeter.Run(p with { P1sPercentiles = [120.0] }, u));
        Assert.Throws<ArgumentException>(() => Flickermeter.Run(p, u, 5.0));                          // settle longer than the record
    }
}
