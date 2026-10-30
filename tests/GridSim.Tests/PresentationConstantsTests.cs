using System.Text.RegularExpressions;
using GridSim.Core.Presentation;
using GridSim.Web.Projection;
using Xunit;

namespace GridSim.Tests;

/// <summary>
/// N023: the presentation constants the web and desktop apps share live once, in Core
/// <see cref="PresentationConstants"/>. Pins each value to the literal both old copies carried (moved, not changed),
/// and checks both surfaces refer to the Core copy instead of holding their own.
/// </summary>
public sealed class PresentationConstantsTests
{
    [Fact]
    public void Core_values_are_the_literals_both_copies_carried()
    {
        Assert.Equal(2000.0, PresentationConstants.UnratedBranchNominalMw);
        Assert.Equal(1500, PresentationConstants.HeavyModelBusLimit);
        Assert.Equal(0.05, PresentationConstants.ModeShapeSplitAmplitude);
        Assert.Equal(3000.0, PresentationConstants.TwoPoleSyncRpm);
        Assert.Equal(200.0, PresentationConstants.TorqueNarrativeBandMw);
        Assert.Equal(1e-6, PresentationConstants.VoltageWeightEpsilonMw);
        Assert.Equal(0.01, PresentationConstants.DefaultOltcDeadbandPu);
        Assert.Equal(0.5, PresentationConstants.TapAtLimitStepFraction);
        Assert.Equal(50.0, PresentationConstants.BessInitialSocPct);
        Assert.Equal(0.9, PresentationConstants.LpBladeTipRadiusM);
        Assert.Equal(343.0, PresentationConstants.SpeedOfSoundMs);
        Assert.Equal(1e-4, PresentationConstants.RegenDeadbandHzPerS);
        Assert.Equal(0.40, PresentationConstants.SyncShareModerateFraction);
        Assert.Equal(0.5, PresentationConstants.RampDeadbandMwPerMin);
        Assert.Equal(95.0, PresentationConstants.StressLimitPct);
        Assert.Equal(85.0, PresentationConstants.StressNearPct);
        Assert.Equal(0.95, PresentationConstants.StressLimitFraction);
        Assert.Equal(0.85, PresentationConstants.StressNearFraction);
    }

    [Fact]
    public void Stress_fractions_are_exactly_the_percentages_over_100()
    {
        // Bit-exact: the desktop's fractions and the web's percentages must classify the same stress the same way.
        Assert.Equal(BitConverter.DoubleToInt64Bits(PresentationConstants.StressLimitPct / 100.0),
                     BitConverter.DoubleToInt64Bits(PresentationConstants.StressLimitFraction));
        Assert.Equal(BitConverter.DoubleToInt64Bits(PresentationConstants.StressNearPct / 100.0),
                     BitConverter.DoubleToInt64Bits(PresentationConstants.StressNearFraction));
    }

    [Fact]
    public void Web_names_read_the_core_copy()
    {
        Assert.Equal(PresentationConstants.UnratedBranchNominalMw, PresentationLimits.UnratedBranchNominalMw);
        Assert.Equal(PresentationConstants.HeavyModelBusLimit, PresentationLimits.HeavyModelBusLimit);
        Assert.Equal(PresentationConstants.TwoPoleSyncRpm, PresentationLimits.TwoPoleSyncRpm);
        Assert.Equal(PresentationConstants.StressLimitPct, PresentationLimits.StressLimitPct);
        string src = PresentationConstantsAntiDriftTests.Source("src", "GridSim.Web", "Projection", "PresentationLimits.cs");
        foreach (string n in Shared)
            Assert.Matches(@"public const (?:double|int) " + n + @" = GridSim\.Core\.Presentation\.PresentationConstants\." + n + ";", src);
    }

    private static readonly string[] Shared =
    {
        "UnratedBranchNominalMw", "HeavyModelBusLimit", "ModeShapeSplitAmplitude", "TwoPoleSyncRpm", "TorqueNarrativeBandMw",
        "VoltageWeightEpsilonMw", "DefaultOltcDeadbandPu", "TapAtLimitStepFraction", "BessInitialSocPct", "LpBladeTipRadiusM",
        "SpeedOfSoundMs", "RegenDeadbandHzPerS", "SyncShareModerateFraction", "RampDeadbandMwPerMin", "StressLimitPct", "StressNearPct",
    };

    [Fact]
    // Not listed: NesoRp1Vm PurchasedTimeGreenS / AmberS and DashboardViewModel FrequencyEventBandHz. Their values
    // already live in Core (FrequencyRiskPresenter, StatusLampRules) and FrequencyRiskPresenterTests deliberately pins
    // the WPF lines as literal mirrors of them; they are left as they are (see the S4 web notes).
    // Test retargeted 2026-10-20 (UI programme 0.2): the two DashboardViewModel.cs rows (TwoPoleSyncRpm,
    // LowInertiaSyncFraction) moved to src/GridSim.Ui/Dashboard/DashboardModel.cs with the dashboard, and the
    // Visuals/FastSchematic.cs row (UnratedBranchNominalMw) to src/GridSim.Ui/Schematic/SchematicView.cs with the map
    // state; they are the next theory, with the same rule; strengthened: the WPF file each left may not hold a copy.
    // Test retargeted 2026-10-20 (UI programme 1.6): the six Views/MachineVm.cs rows moved to
    // src/GridSim.Ui/Tabs/Machine/MachineModel.cs and the three Views/MachineEngineerVm.cs rows to
    // src/GridSim.Ui/Tabs/MachineEngineer/MachineEngineerModel.cs with the tabs' logic; they are the next theory, with the
    // same rule; strengthened: the WPF file each left may not hold a copy.
    // Test changed 2026-10-20 (UI programme 1.6, merging the second and third tab batches): every row of this theory
    // moved with its tab (MACHINE and MECHANICAL in one batch; EXPERIMENTAL, HUMAN SCALE and NESO RP1 in the other) to
    // the GridSim.Ui theories, which pin the same values and that the WPF file each left holds no copy. With no row left
    // the theory would fail for want of data, so it is now one fact that is stronger: no file anywhere under
    // src/GridSim.Wpf may declare any of these constants again.
    public void Wpf_holds_no_literal_twin_of_a_core_presentation_constant()
    {
        string[] moved =
        {
            "HeavyModelBusLimit", "ModeShapeSplitAmplitude", "TwoPoleSyncRpm", "LpBladeTipRadiusM", "SpeedOfSoundMs",
            "RegenDeadbandHzPerS", "TorqueNarrativeBandMw", "VoltageWeightEpsilonMw", "DefaultOltcDeadbandPu",
            "TapAtLimitStepFraction", "BessInitialSocPct", "RampDeadbandMwPerMin", "StressLimitFraction",
            "StressNearFraction", "LowInertiaSyncFraction", "UnratedBranchNominalMw",
        };
        string wpf = PresentationConstantsAntiDriftTests.RepoRoot();
        wpf = Path.Combine(wpf, "src", "GridSim.Wpf");
        Assert.True(Directory.Exists(wpf), wpf);
        foreach (string file in Directory.EnumerateFiles(wpf, "*.cs", SearchOption.AllDirectories))
        {
            if (file.Contains(Path.DirectorySeparatorChar + "obj" + Path.DirectorySeparatorChar, StringComparison.Ordinal)
                || file.Contains(Path.DirectorySeparatorChar + "bin" + Path.DirectorySeparatorChar, StringComparison.Ordinal)) continue;
            string src = File.ReadAllText(file);
            foreach (string name in moved)
                Assert.False(Regex.IsMatch(src, @"const\s+(?:double|int)\s+" + Regex.Escape(name) + @"\s*="),
                    $"{file} declares {name} again; it lives in GridSim.Ui (see Ui_twins_refer_to_core_instead_of_holding_a_literal)");
        }
    }

    [Theory]
    [InlineData("Dashboard/DashboardModel.cs", "TwoPoleSyncRpm")]
    [InlineData("Dashboard/DashboardModel.cs", "LowInertiaSyncFraction")]
    [InlineData("Schematic/SchematicView.cs", "UnratedBranchNominalMw")]
    [InlineData("Tabs/Machine/MachineModel.cs", "TwoPoleSyncRpm")]
    [InlineData("Tabs/Machine/MachineModel.cs", "TorqueNarrativeBandMw")]
    [InlineData("Tabs/Machine/MachineModel.cs", "VoltageWeightEpsilonMw")]
    [InlineData("Tabs/Machine/MachineModel.cs", "DefaultOltcDeadbandPu")]
    [InlineData("Tabs/Machine/MachineModel.cs", "TapAtLimitStepFraction")]
    [InlineData("Tabs/Machine/MachineModel.cs", "BessInitialSocPct")]
    [InlineData("Tabs/MachineEngineer/MachineEngineerModel.cs", "RampDeadbandMwPerMin")]
    [InlineData("Tabs/MachineEngineer/MachineEngineerModel.cs", "StressLimitFraction")]
    [InlineData("Tabs/MachineEngineer/MachineEngineerModel.cs", "StressNearFraction")]
    public void Ui_twins_refer_to_core_instead_of_holding_a_literal(string file, string name)
    {
        string src = PresentationConstantsAntiDriftTests.Source("src", "GridSim.Ui", file);
        Match m = Regex.Match(src, @"const\s+(?:double|int)\s+" + Regex.Escape(name) + @"\s*=\s*GridSim\.Core\.Presentation\.(\w+)\.(\w+)\s*;");
        Assert.True(m.Success, $"{file}: {name} does not refer to a GridSim.Core.Presentation constant");
        // Test retargeted 2026-10-20 (UI programme 1.6): the WPF file each constant left now also covers the two tab
        // view-models (Views/MachineVm.cs, Views/MachineEngineerVm.cs); the rule is unchanged.
        Assert.DoesNotMatch(@"const\s+(?:double|int)\s+" + Regex.Escape(name) + @"\s*=",
            PresentationConstantsAntiDriftTests.Source("src", "GridSim.Wpf", PresentationConstantsAntiDriftTests.WpfFileLeft(file)));
    }

    [Theory]
    // GridSim.Ui file, name, the WPF file it left
    // Test retargeted 2026-10-20 (UI programme 1.6): the Views/ExperimentalVm.cs and Views/HumanScaleVm.cs rows of
    // Wpf_twins_refer_to_core_instead_of_holding_a_literal moved here with the tab logic (to
    // src/GridSim.Ui/Tabs/Experimental/ExperimentalModel.cs and Tabs/HumanScale/HumanScaleModel.cs); the same six rows
    // with the same rule; strengthened: the WPF file each left may not hold a copy.
    [InlineData("Tabs/Experimental/ExperimentalModel.cs", "HeavyModelBusLimit", "Views/ExperimentalVm.cs")]
    [InlineData("Tabs/Experimental/ExperimentalModel.cs", "ModeShapeSplitAmplitude", "Views/ExperimentalVm.cs")]
    [InlineData("Tabs/HumanScale/HumanScaleModel.cs", "TwoPoleSyncRpm", "Views/HumanScaleVm.cs")]
    [InlineData("Tabs/HumanScale/HumanScaleModel.cs", "LpBladeTipRadiusM", "Views/HumanScaleVm.cs")]
    [InlineData("Tabs/HumanScale/HumanScaleModel.cs", "SpeedOfSoundMs", "Views/HumanScaleVm.cs")]
    [InlineData("Tabs/HumanScale/HumanScaleModel.cs", "RegenDeadbandHzPerS", "Views/HumanScaleVm.cs")]
    public void Ui_tab_twins_refer_to_core_instead_of_holding_a_literal(string file, string name, string left)
    {
        string src = PresentationConstantsAntiDriftTests.Source("src", "GridSim.Ui", file);
        Match m = Regex.Match(src, @"const\s+(?:double|int)\s+" + Regex.Escape(name) + @"\s*=\s*GridSim\.Core\.Presentation\.(\w+)\.(\w+)\s*;");
        Assert.True(m.Success, $"{file}: {name} does not refer to a GridSim.Core.Presentation constant");
        Assert.DoesNotMatch(@"const\s+(?:double|int)\s+" + Regex.Escape(name) + @"\s*=",
            PresentationConstantsAntiDriftTests.Source("src", "GridSim.Wpf", left));
    }
}
