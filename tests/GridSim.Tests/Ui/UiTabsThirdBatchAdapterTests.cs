using System.Text.RegularExpressions;
using Xunit;

namespace GridSim.Tests.Ui;

/// <summary>
/// UI programme Phase 1.6 (third tab batch): the WPF side of the move. THE HUMAN SCALE, NESO RP1 and EXPERIMENTAL
/// view-models are now thin adapters over the shared GridSim.Ui models (UiTabsThirdBatchParityTests compares the models
/// with the old WPF code). The test host does not load the WPF project, so these read the adapter sources: each derives
/// from its model, maps every colour token to the brush its XAML binds under the old name, turns each bool back into the
/// old Visibility, keeps the old commands over the model methods, and holds no frame logic of its own.
/// </summary>
public sealed class UiTabsThirdBatchAdapterTests
{
    /// <summary>Each WPF adapter file, the neutral model its view-model derives from, and the old names it maps.</summary>
    public static TheoryData<string, string, string> Adapters => new()
    {
        { "Views/HumanScaleVm.cs", "public sealed class HumanScaleVm : HumanScaleModel", "RegenColour=RegenBrush;TwistColour=TwistBrush" },
        { "Views/NesoRp1Vm.cs", "public sealed class NesoRp1Vm : NesoRp1Model", "PurchasedColour=PurchasedBrush;UnitColour=UnitBrush;GroupColour=GroupBrush;LfddColour=LfddBrush" },
        { "Views/ExperimentalVm.cs", "public sealed class ExperimentalVm : ExperimentalModel", "" },
    };

    [Theory]
    [MemberData(nameof(Adapters))]
    public void Each_wpf_tab_derives_from_its_model_and_maps_every_token_to_its_old_brush(string file, string derives, string mapped)
    {
        string wpf = PresentationConstantsAntiDriftTests.Source("src", "GridSim.Wpf", file);
        Assert.Contains(derives, wpf, StringComparison.Ordinal);
        foreach (string pair in mapped.Split(';', StringSplitOptions.RemoveEmptyEntries))
        {
            string[] p = pair.Split('=');
            Assert.Contains($"public Brush {p[1]} => Palette.Frozen({p[0]});", wpf, StringComparison.Ordinal);
            Assert.Contains($"[nameof({p[0]})] = nameof({p[1]}),", wpf, StringComparison.Ordinal);
        }
        // The adapter is thin: no frame logic of its own.
        Assert.DoesNotContain("SimFrame", wpf, StringComparison.Ordinal);
        Assert.DoesNotContain("ToString(\"F", wpf, StringComparison.Ordinal);
        Assert.DoesNotMatch(new Regex(@"\$""\{"), wpf);
    }

    [Fact]
    public void The_wpf_adapters_turn_each_bool_back_into_the_old_visibility_and_each_row_token_into_its_old_brush()
    {
        string Wpf(string f) => PresentationConstantsAntiDriftTests.Source("src", "GridSim.Wpf", "Views", f);
        foreach ((string f, string line) in new[]
                 {
                     ("NesoRp1Vm.cs", "public Visibility PageVisibility => IsPageVisible ? Visibility.Visible : Visibility.Collapsed;"),
                     ("NesoRp1Vm.cs", "public Visibility NotApplicableVisibility => IsNotApplicableVisible ? Visibility.Visible : Visibility.Collapsed;"),
                     ("NesoRp1Vm.cs", "InspectFuelCommand = new RelayCommand<string>(InspectFuel);"),
                     ("NesoRp1Vm.cs", "InspectBmCommand = new RelayCommand<Rp1BmRowModel>(InspectBm);"),
                     ("NesoRp1Vm.cs", "InspectIcCommand = new RelayCommand<Rp1IcRowModel>(InspectIc);"),
                     ("NesoRp1Vm.cs", "Drill = r.Drill is { } drill ? new RelayCommand(drill) : null,"),
                     ("NesoRp1Vm.cs", "public Brush Swatch => Palette.Frozen(SwatchColour);"),
                     ("NesoRp1Vm.cs", "public Brush KindBrush => Palette.Frozen(KindColour);"),
                     ("NesoRp1Vm.cs", "public Brush LoadingBrush => Palette.Frozen(LoadingColour);"),
                     ("NesoRp1Vm.cs", "public Brush TrendBrush => Palette.Frozen(TrendColour);"),
                     ("NesoRp1Vm.cs", "public Brush BindingBrush => Palette.Frozen(BindingColour);"),
                     ("NesoRp1Vm.cs", "public Brush DirBrush => Palette.Frozen(DirColour);"),
                     ("NesoRp1Vm.cs", "public Brush FlowBrush => Palette.Frozen(FlowColour);"),
                     ("NesoRp1Vm.cs", "public Brush FlagBrush => Palette.Frozen(FlagColour);"),
                     ("HumanScaleVm.cs", "public Brush Bar => Palette.Frozen(BarColour);"),
                     ("ExperimentalVm.cs", "public Brush GateBrush => Palette.Frozen(GateColour);"),
                     ("ExperimentalVm.cs", "public Brush StatusBrush => Palette.Frozen(StatusColour);"),
                 })
            Assert.Contains(line, Wpf(f), StringComparison.Ordinal);
    }
}
