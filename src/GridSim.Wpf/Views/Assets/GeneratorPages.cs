using System.Windows.Input;
using System.Windows.Media;
using GridSim.Ui.Assets.Pages;
using GridSim.Wpf.Visuals;

namespace GridSim.Wpf.Views.Assets;

// UI programme ASSETS A2: the generator pages' logic moved to src/GridSim.Ui/Assets/Pages/GeneratorPages.cs, line for
// line. These are the thin WPF sides (see AssetPages.cs): the brushes and commands the GeneratorTemplates.xaml bindings
// name, over the neutral tokens and actions.

/// <summary>One generator's row on the fleet page (the WPF row: adds <see cref="Open"/>, <see cref="FuelBrush"/> and
/// <see cref="Bar"/>).</summary>
public sealed class GenFleetRow : GenFleetRowModel
{
    private ICommand? _open;
    public ICommand Open => _open ??= new RelayCommand(OnOpen);
    public Brush FuelBrush => Palette.Frozen(FuelColour);
    public Brush Bar => Palette.Frozen(BarColour);

    protected override void OnPropertyChanged(string? name)
    {
        base.OnPropertyChanged(name);
        if (name == nameof(BarColour)) base.OnPropertyChanged(nameof(Bar));
    }
}

/// <summary>The generator fleet page (the WPF side of <see cref="GeneratorFleetModel{TRow}"/>, with WPF rows).</summary>
public sealed class GeneratorFleetVm(AssetExplorerVm explorer) : GeneratorFleetModel<GenFleetRow>(explorer);

/// <summary>One generator's dashboard (the WPF side of <see cref="GeneratorDetailModel"/>: adds the
/// <see cref="OpenBus"/> command, the <see cref="FuelBrush"/> and the <see cref="LeadLagBrush"/>).</summary>
public sealed class GeneratorDetailVm(int index, int unitBus, string? unitName, AssetExplorerVm explorer)
    : GeneratorDetailModel(index, unitBus, unitName, explorer)
{
    private ICommand? _openBus;

    /// <summary>Click-through to the substation this unit connects to (parameter: <see cref="GeneratorDetailModel.BusId"/>).</summary>
    public ICommand OpenBus => _openBus ??= new RelayCommand<int>(OpenBusSubstation);
    public Brush FuelBrush => Palette.Frozen(FuelColour);
    public Brush LeadLagBrush => Palette.Frozen(LeadLagColour);

    protected override void OnPropertyChanged(string? name)
    {
        base.OnPropertyChanged(name);
        if (name == nameof(FuelColour)) base.OnPropertyChanged(nameof(FuelBrush));
        else if (name == nameof(LeadLagColour)) base.OnPropertyChanged(nameof(LeadLagBrush));
    }
}

/// <summary>One generator tile on the phasor fleet page (the WPF tile: adds <see cref="Open"/>,
/// <see cref="FuelBrush"/> and <see cref="LeadLagBrush"/>).</summary>
public sealed class PhasorTile : PhasorTileModel
{
    private ICommand? _open;
    public ICommand Open => _open ??= new RelayCommand(OnOpen);
    public Brush FuelBrush => Palette.Frozen(FuelColour);
    public Brush LeadLagBrush => Palette.Frozen(LeadLagColour);

    protected override void OnPropertyChanged(string? name)
    {
        base.OnPropertyChanged(name);
        if (name == nameof(LeadLagColour)) base.OnPropertyChanged(nameof(LeadLagBrush));
    }
}

/// <summary>The phasor fleet page (the WPF side of <see cref="GeneratorPhasorFleetModel{TTile}"/>, with WPF tiles).</summary>
public sealed class GeneratorPhasorFleetVm(AssetExplorerVm explorer) : GeneratorPhasorFleetModel<PhasorTile>(explorer);
