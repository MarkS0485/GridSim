using GridSim.Core.Assets;
using GridSim.Core.Assets.Protection;
using GridSim.Core.Diagnostics;
using GridSim.Core.Dynamics;
using GridSim.Core.IO;
using GridSim.Core.Model;
using GridSim.Core.Presentation;
using GridSim.Core.Simulation;
using Xunit;

namespace GridSim.Tests;

/// <summary>
/// N297 (B2 NF-2, NF-3, NF-4): (a) the `screen` / RocofScreening grade against the bands of the screened system
/// (FrequencyBands, B2's convention: GB bands as hertz offsets about the nominal), not the absolute GB edges; (b) a placed
/// LFDD block sheds its share of the demand now running (Grid Code OC6.6.2: blocks sized in % of demand), not the base-case
/// MW; (c) a protection class's frequency settings move with the system's nominal (settingsNominalHz, default the GB
/// 50 Hz); (d) the web and desktop trip / TSGB what-if surfaces run at the case's nominal (TripWhatIf), with R148's one pair
/// of sizing targets. Every 50 Hz path is bit-identical; each region says how.
/// </summary>
public sealed partial class B4GbLossThresholdsTests
{
    private static AssetClass N297Prot(params (string Key, double Value)[] p) => new("p", AssetKind.Protection, "protection",
        p.ToDictionary(x => x.Key, x => x.Value),
        new Dictionary<string, AssetClassProvenance> { [AssetClass.DefaultKey] = new(ProvenanceTag.CuratedPlaceholder, "test [VERIFY]") });

    private static AssetStepContext N297Ctx(double hz, double clockS) =>
        new(LocalHz: hz, LocalRocofHzPerS: 0, LocalPhaseDeg: 0, DtSeconds: 0.02, ClockSeconds: clockS);

    // ---------------------------------------------------------------- (a) screening

    [Fact]
    public void N297_a_50_hz_screen_is_bit_identical_through_the_band_overload()
    {
        var conts = new[] { new Contingency("a", 1800, 20_000), new Contingency("b", 1320, 10_000), new Contingency("c", 600, 0) };
        IReadOnlyList<RocofScreeningResult> old = RocofScreening.Screen(150_000, 25_000, conts, FrequencyLimits.ActiveRocofLimitHzPerS, null);
        IReadOnlyList<RocofScreeningResult> gb = RocofScreening.Screen(150_000, 25_000, conts, FrequencyLimits.ActiveRocofLimitHzPerS,
            new DynamicsConfig(), FrequencyBands.Gb);
        Assert.Equal(old.Count, gb.Count);
        for (int i = 0; i < old.Count; i++) Assert.Equal(old[i], gb[i]);
    }

    [Fact]
    public void N297_a_60_hz_screen_grades_against_the_bands_about_60_hz_and_mismatched_bands_are_refused()
    {
        GridModel case9 = IeeeCases.Case9();
        FrequencyBands b60 = FrequencyBands.ForNominal(60.0);
        foreach (RocofScreeningResult r in RocofScreening.Screen(case9))
        {
            Assert.Equal(r.NadirHz < b60.StatutoryLowHz, r.StatutoryBreach);
            Assert.Equal(r.NadirHz <= b60.LfddFirstStageHz, r.LfddRisk);
        }
        var conts = new[] { new Contingency("a", 100, 0) };
        Assert.Throws<ArgumentException>(() => RocofScreening.Screen(10_000, 300, conts, 1.0, new DynamicsConfig { NominalHz = 60.0 }, FrequencyBands.Gb));
    }

    // ---------------------------------------------------------------- (b) LFDD blocks follow the demand

    private static ProtectionController N297Lfdd(double nominalHz = 50.0) => new(
        N297Prot((AssetParams.LfddStageHz, 48.8), (AssetParams.LfddDwellS, 0.0), (AssetParams.LfddBlockMw, 400.0)), 1, 400,
        systemNominalHz: nominalHz);

    [Fact]
    public void N297_an_lfdd_block_sheds_its_share_of_the_demand_now_running()
    {
        ProtectionController at1 = N297Lfdd();
        Assert.Equal(1.0, at1.DemandScale);
        AssetActuation a1 = at1.Update(N297Ctx(48.7, 0.0));
        AssertBits(400.0, a1.ActivePowerMw, "scale 1.0: exactly the block, as before");

        ProtectionController at06 = N297Lfdd();
        at06.SetDemandScale(0.6);
        AssetActuation a06 = at06.Update(N297Ctx(48.7, 0.0));
        AssertBits(400.0 * 0.6, a06.ActivePowerMw, "scale 0.6: the block's share of the 04:00 demand");

        // A stage that already operated keeps what it shed when the demand moves on.
        at06.SetDemandScale(1.0);
        AssertBits(400.0 * 0.6, at06.Update(N297Ctx(48.7, 0.02)).ActivePowerMw, "kept after the scale moved");

        Assert.Throws<ArgumentOutOfRangeException>(() => at1.SetDemandScale(double.NaN));
        Assert.Throws<ArgumentOutOfRangeException>(() => at1.SetDemandScale(-0.1));
    }

    [Fact]
    public void N297_the_fleet_hands_placed_devices_the_operating_points_demand_over_the_base()
    {
        var lfdd = N297Prot((AssetParams.LfddStageHz, 48.8), (AssetParams.LfddDwellS, 0.0), (AssetParams.LfddBlockMw, 400.0));
        var model = new GridModel
        {
            BaseMva = 100,
            Buses = new List<Bus> { new(1, BusType.Slack, Pd: 20_000, BaseKv: 400, Name: "A"), new(2, BusType.Pq, Pd: 10_000, BaseKv: 400, Name: "B") },
            Generators = new List<Generator> { new(1, Pg: 30_000, MBase: 40_000, H: 4.0, Name: "sync", Type: "ccgt") },
            Branches = new List<Branch> { new(1, 2, 0.001, 0.01) },
            Protections = new ProtectionModel(new AssetClassLibrary(new[] { lfdd }), new[] { new ProtectionInstance("d1", "p", MonitorBus: 2, CapacityMw: 400) }),
        };
        AssetDynamicsFleet fleet = AssetDynamicsFleet.Build(model, 50.0);
        var pc = (ProtectionController)fleet.Assets.Single();

        fleet.SetDispatch(model);   // the base itself (the hold-steady path): exactly 1.0
        AssertBits(1.0, pc.DemandScale, "base demand");

        GridModel night = GridScenario.AtLoad(model, 0.6);
        fleet.SetDispatch(night);
        double expected = (20_000 * 0.6 + 10_000 * 0.6) / 30_000.0;
        AssertBits(expected, pc.DemandScale, "load-scaled demand");
    }

    // ---------------------------------------------------------------- (c) frequency stages about the nominal

    [Fact]
    public void N297_a_gb_under_frequency_stage_moves_with_a_60_hz_nominal_and_is_unchanged_at_50_hz()
    {
        AssetClass uf = N297Prot((AssetParams.UfTripHz, 47.5), (AssetParams.UfTripDwellS, 0.0));

        // 50 Hz: the setting as written (47.5 Hz).
        var c50 = new ProtectionController(uf, 1, 100);
        Assert.False(c50.Update(N297Ctx(47.6, 0.0)).TrippedNow);
        Assert.True(c50.Update(N297Ctx(47.4, 0.02)).TrippedNow);

        // 60 Hz: the same GB setting about 60 Hz (57.5 Hz). Before N297 it compared with 47.5 Hz and never operated.
        var c60 = new ProtectionController(uf, 1, 100, systemNominalHz: 60.0);
        Assert.False(c60.Update(N297Ctx(60.0, 0.0)).TrippedNow);
        Assert.False(c60.Update(N297Ctx(57.6, 0.02)).TrippedNow);
        Assert.True(c60.Update(N297Ctx(57.4, 0.04)).TrippedNow);

        // A class that says its settings are for 60 Hz is used as written on a 60 Hz system.
        AssetClass native = N297Prot((AssetParams.UfTripHz, 59.3), (AssetParams.UfTripDwellS, 0.0), (AssetParams.SettingsNominalHz, 60.0));
        var n60 = new ProtectionController(native, 1, 100, systemNominalHz: 60.0);
        Assert.False(n60.Update(N297Ctx(59.4, 0.0)).TrippedNow);
        Assert.True(n60.Update(N297Ctx(59.2, 0.02)).TrippedNow);

        Assert.Throws<InvalidDataException>(() => new ProtectionController(
            N297Prot((AssetParams.UfTripHz, 47.5), (AssetParams.UfTripDwellS, 0.0), (AssetParams.SettingsNominalHz, 0.0)), 1, 100));
        Assert.Equal(50.0, AssetParamDefaults.ProtectionSettingsNominalHz);
    }

    [Fact]
    public void N297_the_empty_measurement_history_reads_the_systems_nominal()
    {
        // With a detection latency the first reads come from the empty history. At 60 Hz they must read 60 Hz, or a
        // GB under-frequency stage moved to 57.5 Hz would operate on the first step.
        AssetClass uf = N297Prot((AssetParams.UfTripHz, 47.5), (AssetParams.UfTripDwellS, 0.0), (AssetParams.DetectionLatencyS, 0.1));
        var c60 = new ProtectionController(uf, 1, 100, systemNominalHz: 60.0);
        Assert.False(c60.Update(N297Ctx(60.0, 0.0)).TrippedNow);
        Assert.False(c60.Tripped);
    }

    // ---------------------------------------------------------------- (d) the what-if surfaces (and R148)

    [Fact]
    public void N297_R148_the_what_if_rules_at_50_hz_are_the_old_ones()
    {
        // Field for field the default config (the list member compared by content, not by reference).
        DynamicsConfig at50 = TripWhatIf.StudyConfig(50.0);
        Assert.Empty(at50.SystemResponseServices);
        Assert.Equal(new DynamicsConfig() with { SystemResponseServices = at50.SystemResponseServices }, at50);
        Assert.Equal(60.0, TripWhatIf.StudyConfig(60.0).NominalHz);
        Assert.Throws<ArgumentOutOfRangeException>(() => TripWhatIf.StudyConfig(double.NaN));

        Assert.Equal(0.125, TripWhatIf.SizingMaxRocofHzPerS);
        Assert.Equal(49.5, TripWhatIf.SizingMinNadirHz(FrequencyBands.Gb));
        Assert.Equal(59.5, TripWhatIf.SizingMinNadirHz(FrequencyBands.ForNominal(60.0)));
        Assert.Equal("<=0.125 Hz/s, >=49.5 Hz", TripWhatIf.SizingTargetsText(FrequencyBands.Gb));   // the WPF compare's old text

        Assert.Equal(new[] { "50.0", "49.8", "49.5", "48.8" }, TripWhatIf.ReferenceLines(FrequencyBands.Gb).Select(r => r.Label));
        Assert.Equal(new[] { 50.0, 49.8, 49.5, 48.8 }, TripWhatIf.ReferenceLines(FrequencyBands.Gb).Select(r => r.Hz));
        Assert.Equal(new[] { "60.0", "59.8", "59.5", "58.8" }, TripWhatIf.ReferenceLines(FrequencyBands.ForNominal(60.0)).Select(r => r.Label));

        Assert.Equal(TripWhatIf.Level.Alarm, TripWhatIf.GradeNadir(48.7, FrequencyBands.Gb));
        Assert.Equal(TripWhatIf.Level.Warning, TripWhatIf.GradeNadir(49.4, FrequencyBands.Gb));
        Assert.Equal(TripWhatIf.Level.Normal, TripWhatIf.GradeNadir(49.5, FrequencyBands.Gb));
        Assert.Equal(TripWhatIf.Level.Normal, TripWhatIf.GradeNadir(59.6, FrequencyBands.ForNominal(60.0)));
        Assert.Equal(TripWhatIf.Level.Alarm, TripWhatIf.GradeRocof(-1.2));
        Assert.Equal(TripWhatIf.Level.Warning, TripWhatIf.GradeRocof(0.6));
        Assert.Equal(TripWhatIf.Level.Normal, TripWhatIf.GradeRocof(0.5));
        Assert.Equal("alarm", TripWhatIf.CssClass(TripWhatIf.Level.Alarm));
        Assert.Equal("warnv", TripWhatIf.CssClass(TripWhatIf.Level.Warning));
        Assert.Equal("", TripWhatIf.CssClass(TripWhatIf.Level.Normal));
    }

    [Fact]
    public void N297_R148_every_what_if_surface_uses_the_shared_rules()
    {
        string trip = Src("src", "GridSim.Web", "Components", "Tabs", "TripTab.razor");
        Assert.DoesNotContain("targetMaxRocofHzPerS: 0.5", trip);
        Assert.Contains("TripWhatIf.SizingMaxRocofHzPerS, TripWhatIf.SizingMinNadirHz(Bands), cfg", trip);
        Assert.Contains("TripWhatIf.StudyConfig(NominalHz)", trip);
        string card = Src("src", "GridSim.Web", "Components", "Tabs", "TsgbWhatIf.razor");
        Assert.Contains("TripWhatIf.SizingMaxRocofHzPerS, TripWhatIf.SizingMinNadirHz(Bands), studyCfg", card);
        Assert.Contains("TripWhatIf.StudyConfig(NominalHz)", card);
        // Test retargeted 2026-10-20 (UI programme 0.2): the desktop TSGB compare moved to
        // src/GridSim.Ui/Dashboard/DashboardModel.cs; assertion unchanged, and strengthened: the WPF view-model must
        // derive from that model and run no sizing of its own.
        string wpf = Src("src", "GridSim.Ui", "Dashboard", "DashboardModel.cs");
        Assert.Contains("TripWhatIf.SizingMaxRocofHzPerS, TripWhatIf.SizingMinNadirHz(bands), studyCfg", wpf);
        Assert.Contains("public sealed class DashboardViewModel : DashboardModel", Src("src", "GridSim.Wpf", "DashboardViewModel.cs"));
        Assert.DoesNotContain("TsgbSizer.SizeForTargets", Src("src", "GridSim.Wpf", "DashboardViewModel.cs"));
        Assert.Contains("<TripTab Frame=\"_frame\" NominalHz=\"_session.NominalHz\" />", Src("src", "GridSim.Web", "Components", "Pages", "Dashboard.razor"));
    }
}
