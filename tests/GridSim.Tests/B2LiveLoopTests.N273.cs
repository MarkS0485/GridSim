using GridSim.Core.Dynamics;
using GridSim.Core.Frequency;
using GridSim.Core.IO;
using GridSim.Core.Model;
using GridSim.Core.Presentation;
using GridSim.Core.Simulation;
using GridSim.Web.Projection;
using Xunit;

namespace GridSim.Tests;

/// <summary>
/// N273 (and the band part of N291): the live loop, web and desktop used the GB 50 Hz nominal and the absolute GB band
/// edges for every case, so case9 (60 Hz since D2) ran its live swing at 50 Hz and would have shown as a permanent
/// over-frequency against 50 Hz bands. The live loop now runs at the model's nominal, and every live display grades
/// against <see cref="FrequencyBands"/>: the GB bands as hertz offsets about the model's nominal (the form PSS/E,
/// PowerFactory, ENTSO-E SOGL and NERC use; see FrequencyBands). The shared assessment flags take a band set too. Every
/// 50 Hz case gets the GB constants themselves, so every 50 Hz output is unchanged; this region proves that bit for bit.
/// </summary>
public sealed partial class B2LiveLoopTests
{
    /// <summary>Every GB band edge the live surfaces use: the limits plus the presentation-only bands.</summary>
    private static readonly double[] GbEdges =
    {
        FrequencyLimits.OperationalLowHz, FrequencyLimits.OperationalHighHz, FrequencyLimits.StatutoryLowHz,
        FrequencyLimits.StatutoryHighHz, FrequencyLimits.LfddFirstStageHz,
        ScadaThresholds.FreqOperationalLowHz, ScadaThresholds.FreqOperationalHighHz, 49.7, 50.3,
    };

    [Fact]
    public void N273_the_gb_band_set_is_the_frequency_limits_constants_bit_for_bit()
    {
        FrequencyBands gb = FrequencyBands.Gb;
        AssertBits(FrequencyLimits.NominalHz, gb.NominalHz, "nominal");
        AssertBits(FrequencyLimits.OperationalLowHz, gb.OperationalLowHz, "operational low");
        AssertBits(FrequencyLimits.OperationalHighHz, gb.OperationalHighHz, "operational high");
        AssertBits(FrequencyLimits.StatutoryLowHz, gb.StatutoryLowHz, "statutory low");
        AssertBits(FrequencyLimits.StatutoryHighHz, gb.StatutoryHighHz, "statutory high");
        AssertBits(FrequencyLimits.LfddFirstStageHz, gb.LfddFirstStageHz, "LFDD");
        Assert.Same(gb, FrequencyBands.ForNominal(50.0));
        Assert.True(FrequencyBands.ForNominal(50.0).IsGb);
        Assert.False(FrequencyBands.ForNominal(60.0).IsGb);
        foreach (double e in GbEdges) AssertBits(e, FrequencyBands.GbEdgeAbout(e, 50.0), $"edge {e} at 50 Hz");
    }

    [Fact]
    public void N273_the_gb_bands_about_60_hz_are_the_same_hertz_offsets()
    {
        FrequencyBands b = FrequencyBands.ForNominal(60.0);
        Assert.Equal(60.0, b.NominalHz);
        // nominal + (edge - 50): the difference is exact (Sterbenz), so each edge is the one rounding of 60 + offset.
        AssertBits(60.0 + (FrequencyLimits.OperationalLowHz - 50.0), b.OperationalLowHz, "operational low");
        AssertBits(60.0 + (FrequencyLimits.OperationalHighHz - 50.0), b.OperationalHighHz, "operational high");
        AssertBits(60.0 + (FrequencyLimits.StatutoryLowHz - 50.0), b.StatutoryLowHz, "statutory low");
        AssertBits(60.0 + (FrequencyLimits.StatutoryHighHz - 50.0), b.StatutoryHighHz, "statutory high");
        AssertBits(60.0 + (FrequencyLimits.LfddFirstStageHz - 50.0), b.LfddFirstStageHz, "LFDD");
        Assert.Equal(59.8, b.OperationalLowHz, 12);
        Assert.Equal(60.2, b.OperationalHighHz, 12);
        Assert.Equal(59.5, b.StatutoryLowHz, 12);
        Assert.Equal(60.5, b.StatutoryHighHz, 12);
        Assert.Equal(58.8, b.LfddFirstStageHz, 12);
        Assert.Equal(60.0, FrequencyBands.ForModel(IeeeCases.Case9()).NominalHz);
        Assert.True(FrequencyBands.ForModel(IeeeCases.Case30()).IsGb);
    }

    [Fact]
    public void N273_band_sets_fail_loud()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => FrequencyBands.ForNominal(double.NaN));
        Assert.Throws<ArgumentOutOfRangeException>(() => FrequencyBands.ForNominal(double.PositiveInfinity));
        Assert.Throws<ArgumentOutOfRangeException>(() => FrequencyBands.ForNominal(0.0));
        Assert.Throws<ArgumentOutOfRangeException>(() => FrequencyBands.ForNominal(1.0));   // LFDD would be below 0 Hz
        Assert.Throws<ArgumentException>(() => new FrequencyBands(50, 50.2, 49.8, 49.5, 50.5, 48.8));      // low above high
        Assert.Throws<ArgumentException>(() => new FrequencyBands(50, 49.8, 50.2, 49.5, 50.5, 49.6));      // LFDD above statutory
        Assert.Throws<ArgumentException>(() => new FrequencyBands(50, 49.8, 50.2, double.NaN, 50.5, 48.8));
    }

    [Fact]
    public void N273_band_labels_print_the_gb_texts_exactly()
    {
        Assert.Equal("49.5", FrequencyBands.Label(FrequencyLimits.StatutoryLowHz));
        Assert.Equal("50.5", FrequencyBands.Label(FrequencyLimits.StatutoryHighHz));
        Assert.Equal("49.8", FrequencyBands.Label(FrequencyLimits.OperationalLowHz));
        Assert.Equal("50.2", FrequencyBands.Label(FrequencyLimits.OperationalHighHz));
        Assert.Equal("59.8", FrequencyBands.Label(FrequencyBands.ForNominal(60.0).OperationalLowHz));
        Assert.Equal("49.9-50.1", ScadaThresholds.FreqAlarmBandText(50.0));
        Assert.Equal("59.9-60.1", ScadaThresholds.FreqAlarmBandText(60.0));
    }

    /// <summary>A spread of responses reaching every summary branch: small and large losses, an over-frequency event
    /// (a loss of demand), and a low-inertia system for the RoCoF branches.</summary>
    private static IEnumerable<FrequencyResponse> SpreadOfResponses(double nominalHz)
    {
        var cfg = new DynamicsConfig { NominalHz = nominalHz };
        foreach (double inertia in new[] { 200_000.0, 20_000.0 })
            foreach (double dp in new[] { -300.0, -1_320.0, -3_000.0, -12_000.0, +1_000.0, +3_000.0 })
                yield return FrequencyDynamics.Simulate(inertia, 30_000, new[] { new FrequencyEvent(1.0, dp) }, cfg);
    }

    [Fact]
    public void N273_assessing_against_the_gb_bands_is_the_old_assessment_exactly()
    {
        foreach (FrequencyResponse r in SpreadOfResponses(50.0))
        {
            FrequencyAssessment old = FrequencyLimits.Assess(r);
            FrequencyAssessment banded = FrequencyLimits.Assess(r, FrequencyLimits.ActiveRocofLimitHzPerS, FrequencyBands.Gb);
            Assert.Equal(old, banded);   // record equality: every flag, number and the summary text
        }
        // The summaries name the GB edges exactly as before (the R081 / pre-B2 texts).
        var summaries = SpreadOfResponses(50.0).Select(r => FrequencyLimits.Assess(r).Summary).ToHashSet();
        Assert.Contains("BELOW STATUTORY (49.5 Hz)", summaries);
        Assert.Contains("ABOVE STATUTORY (50.5 Hz)", summaries);
    }

    [Fact]
    public void N291_a_60_hz_response_is_graded_against_its_own_bands()
    {
        FrequencyBands sixty = FrequencyBands.ForNominal(60.0);
        var cfg = new DynamicsConfig { NominalHz = 60.0 };
        // 100 MW on 30 GW: a few hundredths of a hertz at either nominal.
        FrequencyResponse small = FrequencyDynamics.Simulate(200_000, 30_000, new[] { new FrequencyEvent(1.0, -100.0) }, cfg);
        // Against the fixed GB edges a 60 Hz run is "above statutory" from its first point (the defect N291 names) ...
        FrequencyAssessment gbFlags = FrequencyLimits.Assess(small);
        Assert.True(gbFlags.AboveStatutory);
        Assert.Equal("ABOVE STATUTORY (50.5 Hz)", gbFlags.Summary);
        // ... against its own bands it is within limits.
        FrequencyAssessment own = FrequencyLimits.Assess(small, FrequencyLimits.ActiveRocofLimitHzPerS, sixty);
        Assert.False(own.AboveStatutory);
        Assert.False(own.AboveOperational);
        Assert.False(own.BelowOperational);
        Assert.Equal("within limits", own.Summary);

        // A loss deep enough to cross the statutory floor about 60 Hz names the 60 Hz edge.
        FrequencyResponse big = FrequencyDynamics.Simulate(200_000, 30_000, new[] { new FrequencyEvent(1.0, -12_000.0) }, cfg);
        FrequencyAssessment bigOwn = FrequencyLimits.Assess(big, FrequencyLimits.ActiveRocofLimitHzPerS, sixty);
        Assert.True(bigOwn.BelowStatutory);
        if (!bigOwn.LfddAtRisk) Assert.Equal("BELOW STATUTORY (59.5 Hz)", bigOwn.Summary);
        else Assert.Equal(big.NadirHz <= sixty.LfddFirstStageHz, bigOwn.LfddAtRisk);
    }

    [Fact]
    public void N273_trace_assessment_against_the_gb_bands_is_the_old_report_and_a_60_hz_trace_counts_its_own()
    {
        var t0 = new DateTime(2026, 10, 5, 12, 0, 0, DateTimeKind.Utc);
        double[] hz = { 50.0, 49.85, 49.75, 49.45, 48.7, 49.9, 50.25, 50.6, 50.05 };
        var samples = hz.Select((h, i) => new FrequencySample(t0.AddSeconds(i), h)).ToList();
        Assert.Equal(FrequencyLimits.AssessTrace(samples), FrequencyLimits.AssessTrace(samples, FrequencyLimits.ActiveRocofLimitHzPerS, FrequencyBands.Gb));

        var shifted = hz.Select((h, i) => new FrequencySample(t0.AddSeconds(i), h + 10.0)).ToList();
        FrequencyTraceReport own = FrequencyLimits.AssessTrace(shifted, FrequencyLimits.ActiveRocofLimitHzPerS, FrequencyBands.ForNominal(60.0));
        FrequencyTraceReport gb = FrequencyLimits.AssessTrace(samples);
        Assert.Equal(gb.SamplesBelowOperational, own.SamplesBelowOperational);
        Assert.Equal(gb.SamplesBelowStatutory, own.SamplesBelowStatutory);
        Assert.Equal(gb.SamplesAboveOperational, own.SamplesAboveOperational);
        Assert.Equal(gb.SamplesAboveStatutory, own.SamplesAboveStatutory);
        Assert.Equal(gb.LfddReached, own.LfddReached);
    }

    /// <summary>Frequencies around every GB lamp / alarm edge, and the doubles either side of each.</summary>
    private static IEnumerable<double> FrequenciesAroundTheEdges()
    {
        foreach (double e in GbEdges.Append(50.0))
        {
            yield return e;
            yield return Math.BitDecrement(e);
            yield return Math.BitIncrement(e);
            yield return e - 0.01;
            yield return e + 0.01;
        }
    }

    [Fact]
    public void N273_the_lamp_and_scada_rules_at_50_hz_are_the_old_rules_exactly()
    {
        foreach (double f in FrequenciesAroundTheEdges())
        {
            Assert.Equal(StatusLampRules.IsFrequencyDeviationEvent(f), StatusLampRules.IsFrequencyDeviationEvent(f, FrequencyBands.Gb));
            bool oldAlarm = f < ScadaThresholds.FreqOperationalLowHz || f > ScadaThresholds.FreqOperationalHighHz;
            Assert.Equal(oldAlarm, ScadaThresholds.IsFrequencyAlarm(f, 50.0));
            HeadlineDto h = new(FrequencyHz: f, RocofHzPerS: 0.0, GenerationMw: 1000, DemandMw: 990, LossMw: 10,
                InertiaGvaS: 100, AvgVoltagePu: 1.0, Converged: true, MinVoltagePu: 1.0, MaxVoltagePu: 1.0);
            Assert.Equal(StatusLamp.Evaluate(h, false), StatusLamp.Evaluate(h, false, FrequencyBands.Gb));
        }
    }

    [Fact]
    public void N273_a_60_hz_case_at_its_nominal_is_neither_a_frequency_event_nor_a_scada_alarm()
    {
        FrequencyBands sixty = FrequencyBands.ForNominal(60.0);
        Assert.False(StatusLampRules.IsFrequencyDeviationEvent(60.0, sixty));
        Assert.True(StatusLampRules.IsFrequencyDeviationEvent(60.0));             // the GB edges: the old defect
        Assert.True(StatusLampRules.IsFrequencyDeviationEvent(59.75, sixty));
        Assert.False(ScadaThresholds.IsFrequencyAlarm(60.05, 60.0));
        Assert.True(ScadaThresholds.IsFrequencyAlarm(59.85, 60.0));
        HeadlineDto h = new(FrequencyHz: 60.0, RocofHzPerS: 0.0, GenerationMw: 1000, DemandMw: 990, LossMw: 10,
            InertiaGvaS: 100, AvgVoltagePu: 1.0, Converged: true, MinVoltagePu: 1.0, MaxVoltagePu: 1.0);
        Assert.Equal("SYSTEM NORMAL", StatusLamp.Evaluate(h, false, sixty).Text);
        Assert.Equal("FREQUENCY EVENT", StatusLamp.Evaluate(h, false).Text);
    }

    [Fact]
    public void N273_the_two_pole_speed_follows_the_nominal_and_is_3000_rpm_at_50_hz_exactly()
    {
        AssertBits(PresentationConstants.TwoPoleSyncRpm, PresentationConstants.TwoPoleSyncRpmAt(50.0), "3000 rpm");
        Assert.Equal(3600.0, PresentationConstants.TwoPoleSyncRpmAt(60.0));
        Assert.Equal(2970.0, 60.0 * FrequencyBands.Gb.StatutoryLowHz);   // the GB GRID SPEED gauge ends, exactly
        Assert.Equal(3030.0, 60.0 * FrequencyBands.Gb.StatutoryHighHz);
    }

    [Fact]
    public void N273_LiveSim_runs_a_case_at_its_own_nominal()
    {
        var nine = new LiveSim(IeeeCases.Case9());
        Assert.Equal(60.0, nine.NominalHz);
        Assert.Equal(60.0, nine.Bands.NominalHz);
        SimFrame f = nine.Step(1, 1.0);
        Assert.InRange(f.FrequencyHz, 59.7, 60.3);   // the live swing settles about 60 Hz, not 50

        var thirty = new LiveSim(IeeeCases.Case30(), holdSteady: true);
        Assert.Equal(50.0, thirty.NominalHz);
        Assert.True(thirty.Bands.IsGb);
    }

    [Fact]
    public void N273_LiveSim_refuses_a_config_at_another_nominal_and_the_shell_config_carries_the_model_nominal()
    {
        var ex = Assert.Throws<ArgumentException>(() => new LiveSim(IeeeCases.Case9(), dynamicsConfig: new DynamicsConfig()));
        Assert.Equal("config", ex.ParamName);
        Assert.Contains("LiveSim", ex.Message);
        // A config at the model's nominal is used as given.
        Assert.Equal(60.0, new LiveSim(IeeeCases.Case9(), dynamicsConfig: new DynamicsConfig { NominalHz = 60.0 }).NominalHz);

        // The shells' config (asset bundle present) carries the model's nominal: 50.0 for every GB case.
        GridModel bundled = OneGflModel(AssetTestHelpers.GflClass("ffr"), pgMw: 100, ratedMw: 400);
        DynamicsConfig? cfg = LiveSim.AuthoredBundleConfig(bundled);
        Assert.NotNull(cfg);
        Assert.Equal(bundled.FrequencyHz, cfg!.NominalHz);
        Assert.True(cfg.EnableAssetDynamics);
    }

    [Fact]
    public void N273_a_50_hz_live_run_is_bit_identical_with_no_config_and_with_the_default_config()
    {
        // The resolution LiveSim now does (no config -> the defaults at the model's nominal) is, for a 50 Hz case, the
        // default config the integrators built from a null before: the two runs agree bit for bit.
        var a = new LiveSim(IeeeCases.Case30());
        var b = new LiveSim(IeeeCases.Case30(), dynamicsConfig: new DynamicsConfig());
        for (int t = 1; t <= 40; t++)
        {
            SimFrame fa = a.Step(t, t % 2 == 0 ? 1.0 : 1.0 / 60.0), fb = b.Step(t, t % 2 == 0 ? 1.0 : 1.0 / 60.0);
            AssertBits(fa.FrequencyHz, fb.FrequencyHz, $"tick {t} f");
            AssertBits(fa.RocofHzPerS, fb.RocofHzPerS, $"tick {t} RoCoF");
        }
    }

    [Fact]
    public void N273_the_shells_read_the_nominal_from_the_model()
    {
        // Source guards: the live surfaces grade against the case's own bands (behaviour is pinned above in Core).
        string scadaWeb = Src("src", "GridSim.Web", "Sessions", "ScadaService.cs");
        Assert.Contains("ScadaThresholds.IsFrequencyAlarm(f.FrequencyHz, f.Model.FrequencyHz)", scadaWeb);
        // Test retargeted 2026-10-20 (UI programme 1.6): src/GridSim.Wpf/Views/ScadaVm.cs moved to
        // src/GridSim.Ui/Tabs/Scada/ScadaModel.cs; assertions unchanged, and strengthened: the WPF view-model must derive
        // from that model and may neither run the alarm rule itself nor carry the literal 50 Hz band text.
        string scadaWpf = Src("src", "GridSim.Ui", "Tabs", "Scada", "ScadaModel.cs");
        Assert.Contains("ScadaThresholds.IsFrequencyAlarm(frame.FrequencyHz, nominalHz)", scadaWpf);
        Assert.DoesNotContain("OUTSIDE 49.9-50.1\"", scadaWpf);
        string scadaAdapter = Src("src", "GridSim.Wpf", "Views", "ScadaVm.cs");
        Assert.Contains("public sealed class ScadaVm : ScadaModel", scadaAdapter);
        Assert.DoesNotContain("IsFrequencyAlarm", scadaAdapter);
        Assert.DoesNotContain("OUTSIDE 49.9-50.1\"", scadaAdapter);
        string dash = Src("src", "GridSim.Web", "Components", "Pages", "Dashboard.razor");
        Assert.Contains("Min=\"@SparkMinHz\" Max=\"@SparkMaxHz\"", dash);
        Assert.Contains("_session?.Bands", dash);
        Assert.DoesNotContain("Minimum=\"2970\"", Src("src", "GridSim.Web", "Components", "Tabs", "MachineTab.razor"));
        Assert.DoesNotContain("Minimum=\"2970\"", Src("src", "GridSim.Wpf", "Views", "MachineView.xaml"));
        // Test retargeted 2026-10-20 (UI programme 1.6): Views/MachineView.xaml was ported to
        // src/GridSim.App/Tabs/MachineView.axaml (the WPF file stays until Phase 2 and is still read above); assertion
        // unchanged; strengthened: the port is checked too, and its GRID SPEED gauge must bind the model's band.
        string machineAxaml = Src("src", "GridSim.App", "Tabs", "MachineView.axaml");
        Assert.DoesNotContain("Minimum=\"2970\"", machineAxaml);
        Assert.Contains("Minimum=\"{Binding RpmGaugeMin}\" Maximum=\"{Binding RpmGaugeMax}\"", machineAxaml);
        Assert.DoesNotContain("Min=\"49.5\" Max=\"50.5\"", Src("src", "GridSim.Web", "Components", "Tabs", "ChartsTab.razor"));
    }
}
