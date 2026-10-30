using System.Globalization;
using System.Reflection;
using System.Text.RegularExpressions;
using GridSim.Core.Dynamics.Distributed;
using GridSim.Core.PowerFlow;
using GridSim.Core.Presentation;
using GridSim.Gda;
using GridSim.Web.Api;
using GridSim.Web.Components.Visuals;
using GridSim.Web.Projection;
using Xunit;

namespace GridSim.Tests;

/// <summary>
/// R199-R213 (October 2026 review, WS13): bare literals in the presentation layer and the GDA build were named. These
/// tests (1) pin every new constant to the literal it replaced, so the naming provably changed no value, and (2) keep
/// the WPF twin's copies equal to the web ones. The test project does not reference GridSim.Wpf, so the WPF side is
/// read from source: <c>const double Name = value;</c>.
/// </summary>
public class PresentationConstantsAntiDriftTests
{
    internal static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "GridSim.slnx"))) dir = dir.Parent;
        Assert.True(dir is not null, $"GridSim.slnx not found above {AppContext.BaseDirectory}");
        return dir!.FullName;
    }

    internal static string Source(params string[] relative)
    {
        string path = Path.Combine(new[] { RepoRoot() }.Concat(relative).ToArray());
        Assert.True(File.Exists(path), $"source not found: {path}");
        return File.ReadAllText(path).Replace("\r\n", "\n");
    }

    /// <summary>The value of <c>const double|int Name = literal;</c> in a source file (fails if absent or not a literal),
    /// or of <c>const double|int Name = GridSim.Core.Presentation.Type.Member;</c>, resolved to that Core constant's
    /// value.</summary>
    private static double WpfConst(string file, string name) => ProjectConst("GridSim.Wpf", file, name);

    /// <summary>UI programme 0.2: the same read, in src/GridSim.Ui (the platform-neutral half of the desktop app that
    /// the WPF code is moving into). The rule (a literal, or a reference to a Core presentation constant) is
    /// unchanged.</summary>
    private static double UiConst(string file, string name) => ProjectConst("GridSim.Ui", file, name);

    private static double ProjectConst(string project, string file, string name)
    {
        string src = Source("src", project, file);
        Match m = Regex.Match(src, @"const\s+(?:double|int)\s+" + Regex.Escape(name) + @"\s*=\s*([0-9_.eE+-]+)\s*;");
        if (m.Success) return double.Parse(m.Groups[1].Value.Replace("_", ""), CultureInfo.InvariantCulture);
        // Moved 2026-10 (N023, authorised by Mark's 2026-10-02 best-judgement ruling): before, only a numeric literal
        // was accepted, which REQUIRED the WPF app to keep its own copy of every shared value (the duplication N023
        // removes). The shared constants now live once in GridSim.Core.Presentation and the WPF twins refer to them,
        // so a reference to a Core presentation constant is resolved to its value here; every expected value in this
        // file is unchanged, so the WPF numbers are still pinned exactly as before.
        Match r = Regex.Match(src, @"const\s+(?:double|int)\s+" + Regex.Escape(name) + @"\s*=\s*GridSim\.Core\.Presentation\.(\w+)\.(\w+)\s*;");
        Assert.True(r.Success, $"{file}: no literal constant named {name} and no reference to a GridSim.Core.Presentation constant");
        return CorePresentationConst(r.Groups[1].Value, r.Groups[2].Value);
    }

    /// <summary>The value of a public constant in GridSim.Core.Presentation (fails loud if there is none).</summary>
    internal static double CorePresentationConst(string type, string member)
    {
        Type? t = typeof(PresentationConstants).Assembly.GetType("GridSim.Core.Presentation." + type);
        Assert.True(t is not null, $"no type GridSim.Core.Presentation.{type}");
        FieldInfo? f = t!.GetField(member, BindingFlags.Public | BindingFlags.Static);
        Assert.True(f is not null && f.IsLiteral, $"GridSim.Core.Presentation.{type}.{member} is not a public constant");
        return Convert.ToDouble(f!.GetRawConstantValue(), CultureInfo.InvariantCulture);
    }

    [Fact]
    public void Web_presentation_limits_equal_the_literals_they_replaced()
    {
        Assert.Equal(2000.0, PresentationLimits.UnratedBranchNominalMw);
        Assert.Equal(1500, PresentationLimits.HeavyModelBusLimit);
        Assert.Equal(0.05, PresentationLimits.ModeShapeSplitAmplitude);
        Assert.Equal(3000.0, PresentationLimits.TwoPoleSyncRpm);
        Assert.Equal(200.0, PresentationLimits.TorqueNarrativeBandMw);
        Assert.Equal(1e-6, PresentationLimits.VoltageWeightEpsilonMw);
        Assert.Equal(0.01, PresentationLimits.DefaultOltcDeadbandPu);
        Assert.Equal(0.5, PresentationLimits.TapAtLimitStepFraction);
        Assert.Equal(new[] { 400, 275, 132, 66, 33, 25, 22, 11 }, PresentationLimits.KvTiers);
        Assert.Equal(0.2, PresentationLimits.KvTierSnapTolerance);
        Assert.Equal(50.0, PresentationLimits.BessInitialSocPct);
        Assert.Equal(0.9, PresentationLimits.LpBladeTipRadiusM);
        Assert.Equal(343.0, PresentationLimits.SpeedOfSoundMs);
        Assert.Equal(1e-4, PresentationLimits.RegenDeadbandHzPerS);
        Assert.Equal(0.55, PresentationLimits.SyncShareHealthyFraction);
        Assert.Equal(0.40, PresentationLimits.SyncShareModerateFraction);
        Assert.Equal(0.5, PresentationLimits.IdleInterconnectorMw);
        Assert.Equal(5.0, PresentationLimits.PurchasedTimeGreenS);
        Assert.Equal(2.0, PresentationLimits.PurchasedTimeAmberS);
        Assert.Equal(150.0, PresentationLimits.ImbalancePriceHighGbpPerMwh);
        Assert.Equal(80.0, PresentationLimits.ImbalancePriceWatchGbpPerMwh);
        Assert.Equal(100.0, PresentationLimits.CarbonLowGPerKwh);
        Assert.Equal(250.0, PresentationLimits.CarbonWatchGPerKwh);
        Assert.Equal(1_000_000.0, PresentationLimits.BalancingSpendHighGbp);
        Assert.Equal(250_000.0, PresentationLimits.BalancingSpendWatchGbp);
        Assert.Equal(0.5, PresentationLimits.RampDeadbandMwPerMin);
        Assert.Equal(95.0, PresentationLimits.StressLimitPct);
        Assert.Equal(85.0, PresentationLimits.StressNearPct);
        Assert.Equal(8.0, PresentationLimits.RawDefaultFps);
        Assert.Equal(0.1, PresentationLimits.MinFps);
        Assert.Equal(60.0, PresentationLimits.MaxFps);
        Assert.Equal(TimeSpan.FromSeconds(100), PresentationLimits.MetaFetchTimeout);
        Assert.Equal(2, PresentationLimits.SettlementLagMarginDays);
        Assert.Equal(1e-9, PresentationLimits.MinOmegaRadPerS);
        Assert.Equal(4, PresentationLimits.DefaultCycleWorkerCap);
        // Core constants the web now references instead of its own literals.
        Assert.Equal(10.0, MechanicalThresholds.MinSyncMachineMw);
        Assert.Equal(8.0, EnergyEconomicsConstants.HomeKwhPerDay);
        Assert.Equal(1800.0, CredibleLossBasis.FictionLossMw);
    }

    [Fact]
    public void Rest_oltc_pass_cap_is_the_solvers_own_default()
    {
        MethodInfo solve = typeof(SteadyStateSolver).GetMethod(nameof(SteadyStateSolver.Solve))!;
        ParameterInfo cap = solve.GetParameters().Single(p => p.Name == "maxOltcPasses");
        Assert.Equal(cap.DefaultValue, RestApi.RestOltcPassCap);
        Assert.Equal(25, RestApi.RestOltcPassCap);
    }

    [Fact]
    public void Dominance_text_is_formatted_from_the_configured_threshold()
    {
        // R206: was a hard-coded "10%" beside a configurable threshold.
        Assert.Equal(0.10, new DistributedConfig().DominanceThreshold);
        Assert.Equal("10%", TabProjections.DominanceThresholdText());
    }

    [Fact]
    // Test retargeted 2026-10-20 (UI programme 0.2): the three DashboardViewModel.cs rows (TwoPoleSyncRpm,
    // LowInertiaSyncFraction, FrequencyEventBandHz) moved to src/GridSim.Ui/Dashboard/DashboardModel.cs with the
    // dashboard, and the Visuals/FastSchematic.cs row (UnratedBranchNominalMw) to src/GridSim.Ui/Schematic/SchematicView.cs
    // with the map state; they are the next theory, with the same web values; strengthened: the WPF file each left may
    // not declare its own copy.
    // Test retargeted 2026-10-20 (UI programme 1.6): the six Views/MachineVm.cs rows moved to
    // src/GridSim.Ui/Tabs/Machine/MachineModel.cs and the Views/MachineEngineerVm.cs row (RampDeadbandMwPerMin) to
    // src/GridSim.Ui/Tabs/MachineEngineer/MachineEngineerModel.cs with the tabs' logic; they are the next theory, with the
    // same web values; strengthened: the WPF file each left may not declare its own copy.
    // Test changed 2026-10-20 (UI programme 1.6, merging the second and third tab batches): every row of this theory
    // moved with its tab (MACHINE and MECHANICAL in one batch; EXPERIMENTAL, HUMAN SCALE and NESO RP1 in the other) to
    // the GridSim.Ui theories, which pin the same values and that the WPF file each left holds no copy. With no row left
    // the theory would fail for want of data, so it is now one fact that is stronger: no file anywhere under
    // src/GridSim.Wpf may declare any of these constants again.
    public void Wpf_holds_no_twin_of_a_web_presentation_constant()
    {
        string[] moved =
        {
            "HeavyModelBusLimit", "ModeShapeSplitAmplitude", "TwoPoleSyncRpm", "LpBladeTipRadiusM", "SpeedOfSoundMs",
            "RegenDeadbandHzPerS", "PurchasedTimeGreenS", "PurchasedTimeAmberS", "TorqueNarrativeBandMw",
            "VoltageWeightEpsilonMw", "DefaultOltcDeadbandPu", "TapAtLimitStepFraction", "BessInitialSocPct",
            "RampDeadbandMwPerMin", "LowInertiaSyncFraction", "FrequencyEventBandHz", "UnratedBranchNominalMw",
        };
        string wpf = Path.Combine(RepoRoot(), "src", "GridSim.Wpf");
        Assert.True(Directory.Exists(wpf), wpf);
        foreach (string file in Directory.EnumerateFiles(wpf, "*.cs", SearchOption.AllDirectories))
        {
            if (file.Contains(Path.DirectorySeparatorChar + "obj" + Path.DirectorySeparatorChar, StringComparison.Ordinal)
                || file.Contains(Path.DirectorySeparatorChar + "bin" + Path.DirectorySeparatorChar, StringComparison.Ordinal)) continue;
            string src = File.ReadAllText(file);
            foreach (string name in moved)
                Assert.False(Regex.IsMatch(src, @"const\s+(?:double|int)\s+" + Regex.Escape(name) + @"\s*="),
                    $"{file} declares {name} again; it lives in GridSim.Ui (see the GridSim.Ui theories)");
        }
    }

    [Theory]
    // GridSim.Ui file, name, web value
    [InlineData("Dashboard/DashboardModel.cs", "TwoPoleSyncRpm", PresentationLimits.TwoPoleSyncRpm)]
    [InlineData("Dashboard/DashboardModel.cs", "LowInertiaSyncFraction", PresentationLimits.SyncShareModerateFraction)]
    [InlineData("Dashboard/DashboardModel.cs", "FrequencyEventBandHz", StatusLamp.FrequencyEventBandHz)]
    [InlineData("Schematic/SchematicView.cs", "UnratedBranchNominalMw", PresentationLimits.UnratedBranchNominalMw)]
    [InlineData("Tabs/Machine/MachineModel.cs", "TwoPoleSyncRpm", PresentationLimits.TwoPoleSyncRpm)]
    [InlineData("Tabs/Machine/MachineModel.cs", "TorqueNarrativeBandMw", PresentationLimits.TorqueNarrativeBandMw)]
    [InlineData("Tabs/Machine/MachineModel.cs", "VoltageWeightEpsilonMw", PresentationLimits.VoltageWeightEpsilonMw)]
    [InlineData("Tabs/Machine/MachineModel.cs", "DefaultOltcDeadbandPu", PresentationLimits.DefaultOltcDeadbandPu)]
    [InlineData("Tabs/Machine/MachineModel.cs", "TapAtLimitStepFraction", PresentationLimits.TapAtLimitStepFraction)]
    [InlineData("Tabs/Machine/MachineModel.cs", "BessInitialSocPct", PresentationLimits.BessInitialSocPct)]
    [InlineData("Tabs/MachineEngineer/MachineEngineerModel.cs", "RampDeadbandMwPerMin", PresentationLimits.RampDeadbandMwPerMin)]
    public void Ui_dashboard_constants_equal_the_web_ones(string file, string name, double webValue)
    {
        Assert.Equal(webValue, UiConst(file, name));
        Assert.DoesNotMatch(@"const\s+(?:double|int)\s+" + Regex.Escape(name) + @"\s*=", Source("src", "GridSim.Wpf", WpfFileLeft(file)));
    }

    /// <summary>The WPF file a GridSim.Ui constant moved out of (UI programme 0.2 and 1.6): the map state from
    /// Visuals/FastSchematic.cs, THE MACHINE and MECHANICAL tabs from their Views view-models, the dashboard from
    /// DashboardViewModel.cs.</summary>
    internal static string WpfFileLeft(string uiFile) =>
        uiFile.StartsWith("Schematic/", StringComparison.Ordinal) ? "Visuals/FastSchematic.cs"
        : uiFile.StartsWith("Tabs/Machine/", StringComparison.Ordinal) ? "Views/MachineVm.cs"
        : uiFile.StartsWith("Tabs/MachineEngineer/", StringComparison.Ordinal) ? "Views/MachineEngineerVm.cs"
        : "DashboardViewModel.cs";

    [Theory]
    // GridSim.Ui file, name, web value, the WPF file it left
    // Test retargeted 2026-10-20 (UI programme 1.6): the Views/ExperimentalVm.cs, Views/HumanScaleVm.cs and
    // Views/NesoRp1Vm.cs rows of Wpf_twin_constants_equal_the_web_ones moved here with the tab logic (to
    // src/GridSim.Ui/Tabs/Experimental/ExperimentalModel.cs, Tabs/HumanScale/HumanScaleModel.cs and
    // Tabs/NesoRp1/NesoRp1Model.cs); the same eight rows with the same web values; strengthened: the WPF file each left
    // may not declare its own copy.
    [InlineData("Tabs/Experimental/ExperimentalModel.cs", "HeavyModelBusLimit", (double)PresentationLimits.HeavyModelBusLimit, "Views/ExperimentalVm.cs")]
    [InlineData("Tabs/Experimental/ExperimentalModel.cs", "ModeShapeSplitAmplitude", PresentationLimits.ModeShapeSplitAmplitude, "Views/ExperimentalVm.cs")]
    [InlineData("Tabs/HumanScale/HumanScaleModel.cs", "TwoPoleSyncRpm", PresentationLimits.TwoPoleSyncRpm, "Views/HumanScaleVm.cs")]
    [InlineData("Tabs/HumanScale/HumanScaleModel.cs", "LpBladeTipRadiusM", PresentationLimits.LpBladeTipRadiusM, "Views/HumanScaleVm.cs")]
    [InlineData("Tabs/HumanScale/HumanScaleModel.cs", "SpeedOfSoundMs", PresentationLimits.SpeedOfSoundMs, "Views/HumanScaleVm.cs")]
    [InlineData("Tabs/HumanScale/HumanScaleModel.cs", "RegenDeadbandHzPerS", PresentationLimits.RegenDeadbandHzPerS, "Views/HumanScaleVm.cs")]
    [InlineData("Tabs/NesoRp1/NesoRp1Model.cs", "PurchasedTimeGreenS", PresentationLimits.PurchasedTimeGreenS, "Views/NesoRp1Vm.cs")]
    [InlineData("Tabs/NesoRp1/NesoRp1Model.cs", "PurchasedTimeAmberS", PresentationLimits.PurchasedTimeAmberS, "Views/NesoRp1Vm.cs")]
    public void Ui_tab_constants_equal_the_web_ones(string file, string name, double webValue, string left)
    {
        Assert.Equal(webValue, UiConst(file, name));
        Assert.DoesNotMatch(@"const\s+(?:double|int)\s+" + Regex.Escape(name) + @"\s*=", Source("src", "GridSim.Wpf", left));
    }

    [Fact]
    public void Wpf_stress_bands_are_the_web_percentages_as_fractions()
    {
        // Test retargeted 2026-10-20 (UI programme 1.6): Views/MachineEngineerVm.cs moved to
        // src/GridSim.Ui/Tabs/MachineEngineer/MachineEngineerModel.cs; assertions unchanged; strengthened: the WPF file may
        // not declare its own copy of either band.
        Assert.Equal(PresentationLimits.StressLimitPct / 100.0, UiConst("Tabs/MachineEngineer/MachineEngineerModel.cs", "StressLimitFraction"));
        Assert.Equal(PresentationLimits.StressNearPct / 100.0, UiConst("Tabs/MachineEngineer/MachineEngineerModel.cs", "StressNearFraction"));
        foreach (string moved in new[] { "StressLimitFraction", "StressNearFraction" })
            Assert.DoesNotMatch(@"const\s+(?:double|int)\s+" + moved + @"\s*=", Source("src", "GridSim.Wpf", "Views", "MachineEngineerVm.cs"));
    }

    [Fact]
    public void Wpf_constants_equal_the_literals_they_replaced()
    {
        // Test retargeted 2026-10-20 (UI programme 0.2): the dashboard constants moved from DashboardViewModel.cs to
        // src/GridSim.Ui/Dashboard/DashboardModel.cs; the values asserted are unchanged; strengthened: the WPF view-model
        // may not declare its own copy of any of them (nor IdleLinkMw).
        string wpfDash = Source("src", "GridSim.Wpf", "DashboardViewModel.cs");
        foreach (string moved in new[] { "FreqDialFullScaleHz", "FreqDialSweepDeg", "FreqNominalColourBandHz", "EventLogCapacity", "AlarmListCapacity" })
            Assert.DoesNotMatch(@"const\s+(?:double|int)\s+" + moved + @"\s*=", wpfDash);
        Assert.Equal(0.5, UiConst("Dashboard/DashboardModel.cs", "FreqDialFullScaleHz"));
        Assert.Equal(120.0, UiConst("Dashboard/DashboardModel.cs", "FreqDialSweepDeg"));
        Assert.Equal(0.1, UiConst("Dashboard/DashboardModel.cs", "FreqNominalColourBandHz"));
        // Moved 2026-10 (Q10, authorised by Mark's best-judgement ruling): was Assert.Equal(5.0, WpfConst(
        // "DashboardViewModel.cs", "IdleLinkMw")). The idle-interconnector cut-off is now ONE shared value, 0.5 MW (the
        // web's), in Core StatusLampRules.IdleInterconnectorMw, and the WPF private 5 MW constant is removed; so this
        // pins the shared value and that the WPF file no longer declares its own.
        Assert.Equal(0.5, StatusLampRules.IdleInterconnectorMw);
        Assert.DoesNotMatch(@"const\s+double\s+IdleLinkMw", Source("src", "GridSim.Wpf", "DashboardViewModel.cs"));
        Assert.DoesNotMatch(@"const\s+double\s+IdleLinkMw", Source("src", "GridSim.Ui", "Dashboard", "DashboardModel.cs"));
        Assert.Equal(14, UiConst("Dashboard/DashboardModel.cs", "EventLogCapacity"));
        Assert.Equal(12, UiConst("Dashboard/DashboardModel.cs", "AlarmListCapacity"));
        Assert.Equal(2, WpfConst("MainWindow.xaml.cs", "BridgeStreamFps"));
        Assert.Equal(500.0, WpfConst("MainWindow.xaml.cs", "FeasibilityProbeMw"));
        Assert.Equal(0.065, WpfConst("MainWindow.xaml.cs", "AnimationMinIntervalS"));
        Assert.Equal(0.2, WpfConst("MainWindow.xaml.cs", "AnimationMaxDtS"));
        // Test retargeted 2026-10-20 (UI programme 1.6): Views/MachineEngineerVm.cs moved to
        // src/GridSim.Ui/Tabs/MachineEngineer/MachineEngineerModel.cs; values unchanged; strengthened: the WPF file may not
        // declare its own copy of any of the three.
        Assert.Equal(30.0, UiConst("Tabs/MachineEngineer/MachineEngineerModel.cs", "DefaultPeriodMin"));
        Assert.Equal(40.0, UiConst("Tabs/MachineEngineer/MachineEngineerModel.cs", "TorsionalPulseStepMw"));
        Assert.Equal(15.0, UiConst("Tabs/MachineEngineer/MachineEngineerModel.cs", "InertiaProjectionMinutes"));
        foreach (string moved in new[] { "DefaultPeriodMin", "TorsionalPulseStepMw", "InertiaProjectionMinutes" })
            Assert.DoesNotMatch(@"const\s+(?:double|int)\s+" + moved + @"\s*=", Source("src", "GridSim.Wpf", "Views", "MachineEngineerVm.cs"));
        // Test retargeted 2026-10-20 (UI programme 1.6): the NESO RP1 constants moved from Views/NesoRp1Vm.cs to
        // src/GridSim.Ui/Tabs/NesoRp1/NesoRp1Model.cs; the values asserted are unchanged; strengthened: the WPF
        // view-model may not declare its own copy of any of them.
        Assert.Equal(10.0, UiConst("Tabs/NesoRp1/NesoRp1Model.cs", "PurchasedTimeDisplayCapS"));
        Assert.Equal(0.05, UiConst("Tabs/NesoRp1/NesoRp1Model.cs", "TighteningDeltaS"));
        Assert.Equal(0.5, UiConst("Tabs/NesoRp1/NesoRp1Model.cs", "BoundaryTrendBandPct"));
        Assert.Equal(48, UiConst("Tabs/NesoRp1/NesoRp1Model.cs", "PriceTraceLen"));
        foreach (string moved in new[] { "PurchasedTimeDisplayCapS", "TighteningDeltaS", "BoundaryTrendBandPct", "PriceTraceLen" })
            Assert.DoesNotMatch(@"const\s+(?:double|int)\s+" + moved + @"\s*=", Source("src", "GridSim.Wpf", "Views", "NesoRp1Vm.cs"));
        Assert.Equal(220, WpfConst("Visuals/FastSchematic.cs", "LabelBudget"));
        Assert.Equal(200.0, WpfConst("Visuals/FastSchematic.cs", "EhvDisplayKv"));
        Assert.Equal(120.0, WpfConst("Visuals/FastSchematic.cs", "DashMwPerUnit"));
        Assert.Equal(0.4, WpfConst("Visuals/FastSchematic.cs", "DashSpeedMin"));
        Assert.Equal(8.0, WpfConst("Visuals/FastSchematic.cs", "DashSpeedMax"));
        Assert.Equal(9.0, WpfConst("Visuals/FastSchematic.cs", "DashPxScale"));
        Assert.Equal(0.016, WpfConst("Visuals/FastSchematic.cs", "AnimDefaultDtS"));
        Assert.Equal(0.25, WpfConst("Visuals/FastSchematic.cs", "AnimMaxDtS"));
        // Test retargeted 2026-10-20 (UI programme 0.2): the two pick tolerances moved with the hit tests from
        // Visuals/FastSchematic.cs to src/GridSim.Ui/Schematic/SchematicView.cs; values unchanged; strengthened:
        // FastSchematic may not declare its own copy.
        Assert.Equal(14.0, UiConst("Schematic/SchematicView.cs", "NodePickRadiusPx"));
        Assert.Equal(8.0, UiConst("Schematic/SchematicView.cs", "BranchPickTolerancePx"));
        foreach (string moved in new[] { "NodePickRadiusPx", "BranchPickTolerancePx" })
            Assert.DoesNotMatch(@"const\s+(?:double|int)\s+" + moved + @"\s*=", Source("src", "GridSim.Wpf", "Visuals", "FastSchematic.cs"));
        // Test retargeted 2026-10-20 (UI programme 1.6): HeavyModelBusLimit moved from Views/ExperimentalVm.cs to
        // src/GridSim.Ui/Tabs/Experimental/ExperimentalModel.cs; the value asserted is unchanged; strengthened: the WPF
        // view-model may not declare its own copy.
        Assert.Equal(1500, UiConst("Tabs/Experimental/ExperimentalModel.cs", "HeavyModelBusLimit"));
        Assert.DoesNotMatch(@"const\s+(?:double|int)\s+HeavyModelBusLimit\s*=", Source("src", "GridSim.Wpf", "Views", "ExperimentalVm.cs"));
    }

    [Fact]
    public void Map_js_mirrors_the_wpf_renderer_constants()
    {
        // map-interop.js carries the same label budget, EHV cut and pick tolerances as FastSchematic (R213).
        string js = Source("src", "GridSim.Web", "wwwroot", "js", "map-interop.js");
        double Js(string name)
        {
            Match m = Regex.Match(js, @"const\s+" + name + @"\s*=\s*([0-9.]+)\s*;");
            Assert.True(m.Success, $"map-interop.js: no constant {name}");
            return double.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture);
        }
        Assert.Equal(WpfConst("Visuals/FastSchematic.cs", "LabelBudget"), Js("LABEL_BUDGET"));
        Assert.Equal(WpfConst("Visuals/FastSchematic.cs", "EhvDisplayKv"), Js("EHV_DISPLAY_KV"));
        // Test retargeted 2026-10-20 (UI programme 0.2): the desktop pick tolerances moved to
        // src/GridSim.Ui/Schematic/SchematicView.cs; the web mirrors are compared with them there, unchanged.
        Assert.Equal(UiConst("Schematic/SchematicView.cs", "NodePickRadiusPx"), Js("NODE_PICK_RADIUS_PX"));
        Assert.Equal(UiConst("Schematic/SchematicView.cs", "BranchPickTolerancePx"), Js("BRANCH_PICK_TOL_PX"));
        Assert.Equal(WpfConst("MainWindow.xaml.cs", "AnimationMinIntervalS") * 1000.0, Js("ANIM_MIN_INTERVAL_MS"), 9);
    }

    [Fact]
    public void Gda_build_defaults_equal_the_literals_they_replaced()
    {
        Assert.Equal(0.98, GdaBuildDefaults.DemandPowerFactor);
        Assert.Equal(0.4, GdaBuildDefaults.EmbeddedGenQRatio);
        Assert.Equal(0.0125, GdaBuildDefaults.OltcStepPu);
        Assert.Equal(0.01, GdaBuildDefaults.OltcDeadbandPu);
        Assert.Equal(1e-6, GdaBuildDefaults.DegenerateTransformerXPu);
        Assert.Equal(1e-4, GdaBuildDefaults.MinTransformerXPu);
        Assert.Equal(0.01, GdaBuildDefaults.StarLegFloorFraction);
        Assert.Equal(1e-6, GdaBuildDefaults.StarLegFloorAbs);
        Assert.Equal(100.0, GdaBuildDefaults.TransformerRatingFallbackMva);
        Assert.Equal(132.0, GdaBuildDefaults.DefaultGspKv);
        Assert.Equal(50.0, GdaBuildDefaults.SystemFrequencyHz);
        Assert.Equal(1e-9, GdaBuildDefaults.TieImpedanceThresholdOhm);
        Assert.Equal(1000.0, GdaBuildDefaults.GridInfeedFloorMw);
        Assert.Equal(5.0, GdaBuildDefaults.GridInfeedHeadroomFactor);
        Assert.Equal(GdaBuildDefaults.DefaultGspKv, GdaModelBuilder.ParseGspKv("No voltage here"));
        Assert.Equal(275.0, GdaModelBuilder.ParseGspKv("Somewhere 275kV"));
    }

    [Theory]
    [InlineData("wave", false, true, 7, 6, true)]          // WAVE mode, new tick: push
    [InlineData("wave", false, true, 7, 7, false)]         // already pushed for this tick
    [InlineData("loading", false, true, 7, 6, false)]      // R132: not in WAVE mode: never push
    [InlineData("voltage", false, true, 7, int.MinValue, false)]
    [InlineData("wave", true, true, 7, 6, false)]          // raw backdrop: no solved state
    [InlineData("wave", false, false, 7, 6, false)]        // no field on the frame
    public void Wave_field_is_pushed_only_in_wave_mode_once_per_tick(string mode, bool backdrop, bool hasField, int tick, int pushed, bool expected)
    {
        Assert.Equal(expected, NetworkMap.ShouldPushWaveField(mode, backdrop, hasField, tick, pushed));
    }
}
