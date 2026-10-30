using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using GridSim.Core.Dynamics;
using GridSim.Core.Model;
using GridSim.Core.Presentation;
using GridSim.Core.Simulation;
using GridSim.Gda;
using GridSim.Web.Projection;
using Xunit;

namespace GridSim.Tests;

/// <summary>
/// Q1 and Q10 (rulings of 2026-10-02 evening). Pins the ONE Core presenter for the NESO RP1 / OPERATOR credible loss
/// and grade ladder (<see cref="FrequencyRiskPresenter"/>), the shared status-lamp / idle-link rules
/// (<see cref="StatusLampRules"/>), that both shells call them, and R200's per-island GDA infeed sizing.
/// <para>Q1 grounding: NESO, Frequency Risk and Control Report 2025 (March 2025): secure all BMU-only events and do
/// not secure simultaneous events (Executive summary p.4; s.6 Table 1 p.15; s.6.2 p.18-19; s.7.1 p.27). So the
/// credible loss is the largest single unit and a correlated interconnector group is unsecured context.</para>
/// </summary>
public sealed class FrequencyRiskPresenterTests
{
    // ======================= grade ladder =======================

    [Theory]
    [InlineData(double.PositiveInfinity, "NORMAL", FrequencyRiskPresenter.GradeLevel.Green)]
    [InlineData(10.0, "NORMAL", FrequencyRiskPresenter.GradeLevel.Green)]
    [InlineData(5.0, "NORMAL", FrequencyRiskPresenter.GradeLevel.Green)]          // NORMAL cut inclusive
    [InlineData(4.999, "WATCH", FrequencyRiskPresenter.GradeLevel.Amber)]
    [InlineData(3.0, "WATCH", FrequencyRiskPresenter.GradeLevel.Amber)]           // WATCH cut inclusive
    [InlineData(2.999, "ADVISORY", FrequencyRiskPresenter.GradeLevel.Amber)]
    [InlineData(2.0, "ADVISORY", FrequencyRiskPresenter.GradeLevel.Amber)]        // ADVISORY cut inclusive
    [InlineData(1.999, "WARNING", FrequencyRiskPresenter.GradeLevel.Red)]
    [InlineData(0.0, "WARNING", FrequencyRiskPresenter.GradeLevel.Red)]
    public void Ladder_steps_and_levels_without_a_trend(double seconds, string name, FrequencyRiskPresenter.GradeLevel level)
    {
        FrequencyRiskPresenter.Grade g = FrequencyRiskPresenter.GradePurchasedTime(seconds, tightening: false);
        Assert.Equal(name, g.Name);
        Assert.Equal(name, g.Text);
        Assert.Equal(level, g.Level);
        Assert.False(g.Tightening);
    }

    [Theory]
    [InlineData(4.0, "WATCH down")]
    [InlineData(2.5, "ADVISORY down")]
    [InlineData(1.0, "WARNING down")]
    public void A_falling_trend_below_NORMAL_escalates_to_red_down(double seconds, string text)
    {
        FrequencyRiskPresenter.Grade g = FrequencyRiskPresenter.GradePurchasedTime(seconds, tightening: true);
        Assert.Equal(text, g.Text);
        Assert.Equal(FrequencyRiskPresenter.GradeLevel.Red, g.Level);
        Assert.True(g.Tightening);
    }

    [Fact]
    public void A_falling_trend_does_not_escalate_NORMAL()
    {
        FrequencyRiskPresenter.Grade g = FrequencyRiskPresenter.GradePurchasedTime(6.0, tightening: true);
        Assert.Equal("NORMAL", g.Text);
        Assert.Equal(FrequencyRiskPresenter.GradeLevel.Green, g.Level);
        Assert.False(g.Tightening);
    }

    [Theory]
    [InlineData(double.NaN)]
    [InlineData(-0.1)]
    [InlineData(double.NegativeInfinity)]
    public void Grading_fails_loud_on_an_invalid_purchased_time(double seconds)
        => Assert.Throws<ArgumentOutOfRangeException>(() => FrequencyRiskPresenter.GradePurchasedTime(seconds, false));

    [Fact]
    public void The_ladder_colours_keep_the_old_web_cuts_and_the_wpf_ladder_values()
    {
        // Web GREEN / AMBER cuts (5 s / 2 s) are the Core NORMAL / ADVISORY cuts; WPF's 5 / 3 / 2 s ladder is Core's.
        Assert.Equal(5.0, FrequencyRiskPresenter.GradeNormalS);
        Assert.Equal(3.0, FrequencyRiskPresenter.GradeWatchS);
        Assert.Equal(2.0, FrequencyRiskPresenter.GradeAdvisoryS);
        Assert.Equal(PresentationLimits.PurchasedTimeGreenS, FrequencyRiskPresenter.GradeNormalS);
        Assert.Equal(PresentationLimits.PurchasedTimeAmberS, FrequencyRiskPresenter.GradeAdvisoryS);
        Assert.Equal(10.0, FrequencyRiskPresenter.PurchasedTimeDisplayCapS);
        Assert.Equal(0.05, FrequencyRiskPresenter.TighteningDeltaS);
        Assert.Equal(4, FrequencyRiskPresenter.TrendSpanPoints);
        Assert.Equal(60, FrequencyRiskPresenter.TraceCapacity);
    }

    // ======================= trace =======================

    [Fact]
    public void Trace_stores_infinity_at_the_cap_and_keeps_the_last_sixty_points()
    {
        var t = new FrequencyRiskPresenter.PurchasedTimeTrace();
        t.Add(double.PositiveInfinity);
        Assert.Equal(10.0, t.Points[0]);
        for (int i = 0; i < 100; i++) t.Add(i);
        Assert.Equal(60, t.Points.Count);
        Assert.Equal(40.0, t.Points[0]);
        Assert.Equal(99.0, t.Points[^1]);
        t.Clear();
        Assert.Empty(t.Points);
        Assert.Throws<ArgumentOutOfRangeException>(() => t.Add(double.NaN));
    }

    [Fact]
    public void Trace_tightening_compares_the_newest_point_with_the_one_three_frames_earlier()
    {
        var t = new FrequencyRiskPresenter.PurchasedTimeTrace();
        t.Add(4.0); t.Add(4.0); t.Add(4.0);
        Assert.False(t.IsTightening);                 // three points: not enough history
        t.Add(3.96);                                  // fell 0.04 s: inside the 0.05 s delta
        Assert.False(t.IsTightening);
        t.Add(3.94);                                  // 3.94 vs 4.0 three frames back: fell 0.06 s
        Assert.True(t.IsTightening);
    }

    // ======================= credible loss =======================

    [Fact]
    public void The_credible_loss_is_the_largest_unit_even_when_a_correlated_group_is_bigger()
    {
        // FRCR 2025 s.7.1: simultaneous events are not secured, so a 2,000 MW Sellindge group does not replace a
        // 1,500 MW single unit as the credible loss; it is flagged as the bigger unsecured risk instead.
        var view = FrequencyRiskPresenter.CredibleLoss(1500.0, "Big CCGT", new StabilityMetrics.GroupLoss("Sellindge (IFA+ElecLink)", 2000.0));
        Assert.Equal(1500.0, view.SecuredLossMw);
        Assert.Equal("Big CCGT", view.SecuredLossName);
        Assert.Equal(2000.0, view.CorrelatedGroupMw);
        Assert.True(view.CorrelatedGroupExceedsSecured);

        var smaller = FrequencyRiskPresenter.CredibleLoss(1500.0, "Big CCGT", new StabilityMetrics.GroupLoss("x", 900.0));
        Assert.Equal(1500.0, smaller.SecuredLossMw);
        Assert.False(smaller.CorrelatedGroupExceedsSecured);
    }

    [Fact]
    public void Credible_loss_fails_loud_on_invalid_figures()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => FrequencyRiskPresenter.CredibleLoss(double.NaN, "u", new StabilityMetrics.GroupLoss("", 0)));
        Assert.Throws<ArgumentOutOfRangeException>(() => FrequencyRiskPresenter.CredibleLoss(-1.0, "u", new StabilityMetrics.GroupLoss("", 0)));
        Assert.Throws<ArgumentOutOfRangeException>(() => FrequencyRiskPresenter.CredibleLoss(1.0, "u", new StabilityMetrics.GroupLoss("", double.PositiveInfinity)));
    }

    [Fact]
    public void Largest_secured_unit_is_the_largest_single_infeed_first_wins_a_tie_name_falls_back_to_fuel()
    {
        // Moved 2026-10 (B4 N039, Ruling R1: Mark delegated EE calls 2026-10-04; source NETS SQSS v2.10 para 5.1.2 "a
        // single generating unit ..., a single power park module, or a single DC converter", and NESO FRCR Methodology v3
        // s.4.1.1 Table 2 "an event that disconnects one BMU", any technology): converter plant and interconnector imports
        // are secured single infeeds too. Before (test named "..._is_the_largest_synchronous_output_..."): the wind farm
        // and the interconnector were excluded and the answer was 1200 MW "First CCGT"; the solar-only fleet gave
        // (0, "-"). After: the 3000 MW interconnector "IFA" is the largest infeed; the solar unit is 500 MW "Solar".
        var gens = new[]
        {
            new Generator(1, 2000, Type: "wind-offshore", Name: "Wind farm"),   // converter: counts (power park module)
            new Generator(1, 1200, Type: "CCGT", Name: "First CCGT"),
            new Generator(1, 1200, Type: "CCGT", Name: "Second CCGT"),
            new Generator(1, 3000, Type: "interconnector", Name: "IFA"),         // DC converter import: counts
            new Generator(1, -50, Type: "Coal", Name: "Absorbing"),
        };
        (double mw, string name) = FrequencyRiskPresenter.LargestSecuredUnit(gens);
        Assert.Equal(3000.0, mw);
        Assert.Equal("IFA", name);

        // A tie still keeps the first unit met (the rule both shells always had).
        (double mwTie, string nameTie) = FrequencyRiskPresenter.LargestSecuredUnit(new[]
        {
            new Generator(1, 1200, Type: "CCGT", Name: "First CCGT"),
            new Generator(1, 1200, Type: "wind-offshore", Name: "Equal wind farm"),
        });
        Assert.Equal(1200.0, mwTie);
        Assert.Equal("First CCGT", nameTie);

        (double mw2, string name2) = FrequencyRiskPresenter.LargestSecuredUnit(new[] { new Generator(1, 700, Type: "nuclear") });
        Assert.Equal(700.0, mw2);
        Assert.Equal("Nuclear", name2);

        Assert.Equal((500.0, "Solar"), FrequencyRiskPresenter.LargestSecuredUnit(new[] { new Generator(1, 500, Type: "solar") }));
        Assert.Throws<ArgumentException>(() => FrequencyRiskPresenter.LargestSecuredUnit(new[] { new Generator(1, double.NaN, Type: "CCGT") }));
    }

    [Fact]
    public void Web_rp1_uses_the_core_credible_loss_and_grade_and_never_the_correlated_group()
    {
        SimFrame f = WebProjectionFixtures.Solve(WebProjectionFixtures.MixedFleet());
        var fb = new FuelBreakdown(f.Model);
        // A 2,500 MW Sellindge import, bigger than any single unit in the fleet.
        // Moved 2026-10 (B4 N039, Ruling R1: Mark delegated EE calls 2026-10-04; source NETS SQSS v2.10 para 5.1.2, NESO
        // FRCR Methodology v3 Table 2): the 2,000 MW wind farm now counts as a single infeed, so the credible loss is
        // 2,000 MW and the old 1,000 + 1,000 = 2,000 MW group would no longer be bigger. Before: IFA 1000 + ElecLink
        // 1000 (group 2000.0 MW against the 1,500 MW CCGT). After: 1250 + 1250 (group 2500.0 MW against the 2,000 MW
        // wind farm). The point of the test (the bigger group never becomes the credible loss) is unchanged.
        var ics = new List<(string Name, double Mw)> { ("IFA", 1250.0), ("ElecLink", 1250.0) };
        Rp1Dto rp1 = TabProjections.Rp1(f, fb, ics);

        (double unitMw, string unitName) = FrequencyRiskPresenter.LargestSecuredUnit(f.Model.Generators);
        Assert.Equal(unitMw, fb.LargestUnitMw);
        Assert.Equal(unitName, fb.LargestUnitName);
        Assert.Equal(unitMw, rp1.CredibleLossMw);
        Assert.Equal(2500.0, rp1.CorrelatedGroup.Mw);
        Assert.True(rp1.CorrelatedGroup.Mw > rp1.CredibleLossMw);   // bigger, yet not the credible loss

        FrequencyRiskPresenter.Grade g = FrequencyRiskPresenter.GradePurchasedTime(rp1.PurchasedTimeSeconds, false);
        Assert.Equal(g.Text, rp1.LfddGrade);
        Assert.Equal(g.Level.ToString().ToLowerInvariant(), rp1.LfddLevel);
    }

    [Fact]
    public void Web_rp1_with_a_session_trace_adds_a_point_and_escalates_on_a_falling_trend()
    {
        SimFrame f = WebProjectionFixtures.Solve(WebProjectionFixtures.MixedFleet());
        var fb = new FuelBreakdown(f.Model);
        double p = TabProjections.Rp1(f, fb).PurchasedTimeSeconds;
        Assert.True(double.IsFinite(p));

        var trace = new FrequencyRiskPresenter.PurchasedTimeTrace();
        // History well above this frame's figure, so this frame reads as a fall of more than 0.05 s.
        for (int i = 0; i < 3; i++) trace.Add(p + 1.0);
        Rp1Dto rp1 = TabProjections.Rp1(f, fb, trace: trace);
        Assert.Equal(4, trace.Points.Count);
        Assert.Equal(p, trace.Points[^1]);
        Assert.Equal(FrequencyRiskPresenter.GradePurchasedTime(p, tightening: true).Text, rp1.LfddGrade);
    }

    // ======================= Q10 status-lamp rules =======================

    [Theory]
    [InlineData(50.0, false)]
    [InlineData(49.8, false)]     // on the edge: inside the operational limit (the ruling's reason)
    [InlineData(50.2, false)]
    [InlineData(49.79, true)]
    [InlineData(50.21, true)]
    [InlineData(48.8, true)]
    public void Frequency_event_is_strictly_outside_the_operational_band(double hz, bool expected)
        => Assert.Equal(expected, StatusLampRules.IsFrequencyDeviationEvent(hz));

    [Fact]
    public void Frequency_event_band_is_the_shared_literal_and_fails_loud_on_nan()
    {
        Assert.Equal(0.2, StatusLampRules.FrequencyEventBandHz);
        Assert.Equal(StatusLampRules.FrequencyEventBandHz, StatusLamp.FrequencyEventBandHz);
        Assert.Throws<ArgumentOutOfRangeException>(() => StatusLampRules.IsFrequencyDeviationEvent(double.NaN));
    }

    [Theory]
    [InlineData(0.94, 1.06, false)]   // band edges inclusive
    [InlineData(0.939, 1.00, true)]   // one low bus is an alert...
    [InlineData(0.98, 1.061, true)]   // ...and so is one high bus
    public void Voltage_alert_is_judged_on_min_and_max(double vmin, double vmax, bool expected)
        => Assert.Equal(expected, StatusLampRules.IsVoltageAlert(vmin, vmax));

    [Fact]
    public void Voltage_alert_fails_loud_on_bad_input()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => StatusLampRules.IsVoltageAlert(double.NaN, 1.0));
        Assert.Throws<ArgumentException>(() => StatusLampRules.IsVoltageAlert(1.05, 0.95));
    }

    [Fact]
    public void Web_lamp_flags_one_bus_out_of_band_even_when_the_average_is_fine()
    {
        // The case the ruling is about: average 1.00 pu (in band) but one bus at 0.90 pu.
        var h = new HeadlineDto(FrequencyHz: 50.0, RocofHzPerS: 0, GenerationMw: 1000, DemandMw: 990, LossMw: 10,
            InertiaGvaS: 100, AvgVoltagePu: 1.00, Converged: true, MinVoltagePu: 0.90, MaxVoltagePu: 1.04);
        Assert.Equal("VOLTAGE ALERT", StatusLamp.Evaluate(h, anyBoundaryOverloaded: false).Text);
    }

    [Fact]
    public void Projected_headline_carries_the_real_min_and_max_bus_voltage()
    {
        SimFrame f = WebProjectionFixtures.Solve(WebProjectionFixtures.MixedFleet());
        FrameEnvelope env = FrameProjector.Project(f);
        Assert.Equal(f.Result.Vm.Min(), env.Headline.MinVoltagePu);
        Assert.Equal(f.Result.Vm.Max(), env.Headline.MaxVoltagePu);
    }

    [Theory]
    [InlineData(0.0, true)]
    [InlineData(0.49, true)]
    [InlineData(-0.49, true)]
    [InlineData(0.5, false)]    // at the cut-off: active (the web's ">= 0.5" kept)
    [InlineData(-0.5, false)]
    [InlineData(5.0, false)]    // was idle on the WPF's old 5 MW cut
    public void Idle_interconnector_is_below_half_a_megawatt(double mw, bool idle)
        => Assert.Equal(idle, StatusLampRules.IsIdleInterconnector(mw));

    [Fact]
    public void Idle_interconnector_cutoff_is_shared_by_the_web_limits()
    {
        Assert.Equal(0.5, StatusLampRules.IdleInterconnectorMw);
        Assert.Equal(StatusLampRules.IdleInterconnectorMw, PresentationLimits.IdleInterconnectorMw);
    }

    // ======================= the WPF twin calls the same Core rules (read from source) =======================

    private static string Wpf(string file) => PresentationConstantsAntiDriftTests.Source("src", "GridSim.Wpf", file);

    /// <summary>The platform-neutral dashboard the WPF view-model now derives from (UI programme 0.2).</summary>
    private static string UiDashboard() => PresentationConstantsAntiDriftTests.Source("src", "GridSim.Ui", "Dashboard", "DashboardModel.cs");

    private static double WpfLiteral(string file, string name) => Literal(Wpf(file), file, name);

    private static double Literal(string src, string file, string name)
    {
        Match m = Regex.Match(src, @"const\s+(?:double|int)\s+" + Regex.Escape(name) + @"\s*=\s*([0-9_.eE+-]+)\s*;");
        Assert.True(m.Success, $"{file}: no literal constant named {name}");
        return double.Parse(m.Groups[1].Value.Replace("_", ""), CultureInfo.InvariantCulture);
    }

    [Fact]
    public void Wpf_literal_mirrors_equal_the_core_values()
    {
        // Test retargeted 2026-10-20 (UI programme 1.6): src/GridSim.Wpf/Views/NesoRp1Vm.cs moved to
        // src/GridSim.Ui/Tabs/NesoRp1/NesoRp1Model.cs; the RP1 assertions are unchanged (still literals, still equal to
        // the Core values, and the ladder line below), and strengthened: the WPF view-model must not declare a second
        // copy of any of them.
        Assert.Equal(FrequencyRiskPresenter.GradeNormalS, Literal(Rp1Model(), Rp1ModelFile, "PurchasedTimeGreenS"));
        Assert.Equal(FrequencyRiskPresenter.GradeAdvisoryS, Literal(Rp1Model(), Rp1ModelFile, "PurchasedTimeAmberS"));
        Assert.Equal(FrequencyRiskPresenter.PurchasedTimeDisplayCapS, Literal(Rp1Model(), Rp1ModelFile, "PurchasedTimeDisplayCapS"));
        Assert.Equal(FrequencyRiskPresenter.TighteningDeltaS, Literal(Rp1Model(), Rp1ModelFile, "TighteningDeltaS"));
        foreach (string moved in new[] { "PurchasedTimeGreenS", "PurchasedTimeAmberS", "PurchasedTimeDisplayCapS", "TighteningDeltaS", "LfddNormalS" })
            Assert.DoesNotMatch(@"const\s+double\s+" + moved, Wpf("Views/NesoRp1Vm.cs"));
        // Test retargeted 2026-10-20 (UI programme 0.2): the dashboard's FrequencyEventBandHz mirror moved to
        // src/GridSim.Ui/Dashboard/DashboardModel.cs; assertion unchanged (still a literal, still equal to the Core
        // value), and strengthened: the WPF view-model must not declare a second copy.
        Assert.Equal(StatusLampRules.FrequencyEventBandHz, Literal(UiDashboard(), "Dashboard/DashboardModel.cs", "FrequencyEventBandHz"));
        Assert.DoesNotMatch(@"const\s+double\s+FrequencyEventBandHz", Wpf("DashboardViewModel.cs"));
        Assert.Contains("LfddNormalS = 5.0, LfddWatchS = 3.0, LfddAdvisoryS = 2.0;", Rp1Model());
    }

    /// <summary>UI programme 1.6: the NESO RP1 logic the WPF NesoRp1Vm now derives from.</summary>
    private const string Rp1ModelFile = "Tabs/NesoRp1/NesoRp1Model.cs";

    private static string Rp1Model() => PresentationConstantsAntiDriftTests.Source("src", "GridSim.Ui", "Tabs", "NesoRp1", "NesoRp1Model.cs");

    [Fact]
    public void Wpf_rp1_and_operator_call_the_core_presenter_and_no_longer_take_the_group_as_the_loss()
    {
        // Test retargeted 2026-10-20 (UI programme 1.6): src/GridSim.Wpf/Views/NesoRp1Vm.cs moved to
        // src/GridSim.Ui/Tabs/NesoRp1/NesoRp1Model.cs; the RP1 assertions are unchanged, and strengthened: the WPF
        // view-model must derive from that model and may not grade or take the credible loss itself.
        string rp1 = Rp1Model();
        string rp1Adapter = Wpf("Views/NesoRp1Vm.cs");
        Assert.Contains("public sealed class NesoRp1Vm : NesoRp1Model", rp1Adapter);
        Assert.DoesNotContain("FrequencyRiskPresenter.", rp1Adapter);
        Assert.DoesNotContain("Math.Max(_largestUnitMw, grp.Mw)", rp1Adapter);
        Assert.Contains("FrequencyRiskPresenter.LargestSecuredUnit(f.Model.Generators)", rp1);
        Assert.Contains("FrequencyRiskPresenter.CredibleLoss(_largestUnitMw, _largestUnitName, grp)", rp1);
        Assert.Contains("double liveLoss = loss.SecuredLossMw;", rp1);
        Assert.Contains("FrequencyRiskPresenter.GradePurchasedTime(purchasedSeconds, tightening)", rp1);
        Assert.Contains("SetLfdd(tLive, _trace.IsTightening);", rp1);
        Assert.DoesNotContain("Math.Max(_largestUnitMw, grp.Mw)", rp1);

        // Test retargeted 2026-10-20 (UI programme 1.6): src/GridSim.Wpf/Views/OperatorVm.cs moved to
        // src/GridSim.Ui/Tabs/Operator/OperatorModel.cs; assertion unchanged, and strengthened: the WPF view-model must
        // derive from that model and may not set the credible loss itself.
        Assert.Contains("FrequencyRiskPresenter.LargestSecuredUnit(m.Generators)",
            PresentationConstantsAntiDriftTests.Source("src", "GridSim.Ui", "Tabs", "Operator", "OperatorModel.cs"));
        Assert.Contains("public sealed class OperatorVm : OperatorModel", Wpf("Views/OperatorVm.cs"));
        Assert.DoesNotContain("LargestLoss =", Wpf("Views/OperatorVm.cs"));
    }

    [Fact]
    public void Wpf_dashboard_calls_the_shared_lamp_and_idle_rules()
    {
        // Test retargeted 2026-10-20 (UI programme 0.2): the dashboard lamp and import links moved to
        // src/GridSim.Ui/Dashboard/DashboardModel.cs; assertions unchanged, and strengthened: the IdleLinkMw comparison is
        // refused in the WPF view-model too.
        string dash = UiDashboard();
        // Moved 2026-10 (B2 N273, Ruling R1: Mark delegated EE calls 2026-10-04; source: NEW-FINDINGS N273, the live loop,
        // web and desktop take the nominal frequency and bands from the model): the WPF lamp still calls the shared rule, now
        // with the band set of the frame's own model. Before: "StatusLampRules.IsFrequencyDeviationEvent(f.FrequencyHz)";
        // after: the same call with the bands argument. The guard's purpose (the WPF lamp uses the one Core rule) is unchanged.
        Assert.Contains("StatusLampRules.IsFrequencyDeviationEvent(f.FrequencyHz, GridSim.Core.Dynamics.FrequencyBands.ForModel(f.Model))", dash);
        Assert.Contains("!StatusLampRules.IsVoltageAlert(vmin, vmax)", dash);
        Assert.Contains("StatusLampRules.IsIdleInterconnector(g.Pg)", dash);
        // No comparison against the removed WPF 5 MW constant survives (a comment may still name it).
        Assert.DoesNotMatch(@"[<>]=?\s*-?IdleLinkMw", dash);
        Assert.DoesNotMatch(@"[<>]=?\s*-?IdleLinkMw", Wpf("DashboardViewModel.cs"));
    }

    // ======================= R200: GDA infeed sized per island =======================

    /// <summary>Two islands: A = buses 1-2 (slack 1, 3,000 MW demand), B = buses 3-4 (slack 3, 200 MW demand).</summary>
    private static GridModel TwoIslands() => new()
    {
        BaseMva = 100,
        Buses = new[]
        {
            new Bus(1, BusType.Slack, BaseKv: 132, Name: "GSP A"),
            new Bus(2, BusType.Pq, Pd: 3000, BaseKv: 132),
            new Bus(3, BusType.Slack, BaseKv: 132, Name: "GSP B"),
            new Bus(4, BusType.Pq, Pd: 200, BaseKv: 132),
        },
        Branches = new[] { new Branch(1, 2, 0.01, 0.1), new Branch(3, 4, 0.01, 0.1) },
        Generators = Array.Empty<Generator>(),
    };

    private static Dictionary<string, double> WrittenInfeedCapacities(GridModel m)
    {
        string dir = Path.Combine(Path.GetTempPath(), $"gridsim-r200-{Guid.NewGuid():N}");
        try
        {
            GdaModelWriter.WriteDirectory(m, dir);
            using JsonDocument doc = JsonDocument.Parse(File.ReadAllText(Path.Combine(dir, "generation.json")));
            var caps = new Dictionary<string, double>();
            foreach (JsonElement g in doc.RootElement.EnumerateArray())
                if (g.GetProperty("type").GetString() == "grid")
                {
                    double cap = g.GetProperty("capacityMva").GetDouble();
                    Assert.Equal(cap, g.GetProperty("qMaxMvar").GetDouble());
                    Assert.Equal(-cap, g.GetProperty("qMinMvar").GetDouble());
                    caps[g.GetProperty("name").GetString()!] = cap;
                }
            return caps;
        }
        finally { try { Directory.Delete(dir, recursive: true); } catch { /* best effort */ } }
    }

    [Fact]
    public void R200_each_infeed_is_sized_on_its_own_island_demand()
    {
        // Before R200 both infeeds were 5 x max(1,000, 3,200) = 16,000 MVA. Now: A = 5 x 3,000 = 15,000 MVA and
        // B = 5 x max(1,000, 200) = 5,000 MVA (the floor still applies per island).
        Dictionary<string, double> caps = WrittenInfeedCapacities(TwoIslands());
        Assert.Equal(2, caps.Count);
        Assert.Equal(15000.0, caps["Grid infeed @ GSP A"]);
        Assert.Equal(5000.0, caps["Grid infeed @ GSP B"]);

        IReadOnlyDictionary<int, double> isl = GdaModelWriter.IslandDemandBySlack(TwoIslands());
        Assert.Equal(3000.0, isl[1]);
        Assert.Equal(200.0, isl[3]);
    }

    [Fact]
    public void R200_a_single_island_model_is_bit_identical_to_the_old_whole_network_sizing()
    {
        // Two GSP slacks in ONE island: each gets the whole island's demand, which is the whole network's, so the
        // written figure is exactly the old 5 x max(1,000, sum Pd).
        var m = new GridModel
        {
            BaseMva = 100,
            Buses = new[]
            {
                new Bus(1, BusType.Slack, BaseKv: 132, Name: "GSP A"),
                new Bus(2, BusType.Pq, Pd: 1234.5678, BaseKv: 132),
                new Bus(3, BusType.Slack, BaseKv: 132, Name: "GSP B"),
                new Bus(4, BusType.Pq, Pd: 987.654321, BaseKv: 132),
            },
            Branches = new[] { new Branch(1, 2, 0.01, 0.1), new Branch(2, 3, 0.01, 0.1), new Branch(3, 4, 0.01, 0.1) },
            Generators = Array.Empty<Generator>(),
        };
        double old = Math.Max(GdaBuildDefaults.GridInfeedFloorMw, m.Buses.Sum(b => b.Pd)) * GdaBuildDefaults.GridInfeedHeadroomFactor;
        Dictionary<string, double> caps = WrittenInfeedCapacities(m);
        Assert.Equal(old, caps["Grid infeed @ GSP A"]);
        Assert.Equal(old, caps["Grid infeed @ GSP B"]);
    }

    [Fact]
    public void R200_fails_loud_on_a_branch_to_a_missing_bus_and_on_non_finite_demand()
    {
        var bad = new GridModel
        {
            BaseMva = 100,
            Buses = new[] { new Bus(1, BusType.Slack) },
            Branches = new[] { new Branch(1, 99, 0.01, 0.1) },
            Generators = Array.Empty<Generator>(),
        };
        Assert.Throws<InvalidOperationException>(() => GdaModelWriter.IslandDemandBySlack(bad));
        Assert.Throws<ArgumentOutOfRangeException>(() => GdaModelWriter.GridInfeedSizingMw(double.NaN));
    }
}
