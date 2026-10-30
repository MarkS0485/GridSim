using System.Windows.Input;
using System.Windows.Media;
using GridSim.Ui.Assets.Pages;
using GridSim.Wpf.Visuals;

namespace GridSim.Wpf.Views.Assets;

// UI programme ASSETS A2: the cables & lines pages' logic moved to src/GridSim.Ui/Assets/Pages/CircuitPages.cs, line for
// line. These are the thin WPF sides (see AssetPages.cs): the brushes and commands the CircuitTemplates.xaml bindings
// name, over the neutral tokens and actions.

/// <summary>A selectable voltage-band chip (the WPF chip: adds the <see cref="Select"/> command).</summary>
public sealed class BandChip : BandChipModel
{
    private ICommand? _select;
    public ICommand Select => _select ??= new RelayCommand(OnSelect);
}

/// <summary>One circuit's row on the cables &amp; lines fleet page (the WPF row: adds <see cref="Open"/> and
/// <see cref="Bar"/>).</summary>
public sealed class CircuitFleetRow : CircuitFleetRowModel
{
    private ICommand? _open;
    public ICommand Open => _open ??= new RelayCommand(OnOpen);
    public Brush Bar => Palette.Frozen(BarColour);

    protected override void OnPropertyChanged(string? name)
    {
        base.OnPropertyChanged(name);
        if (name == nameof(BarColour)) base.OnPropertyChanged(nameof(Bar));
    }
}

/// <summary>The cables &amp; lines fleet page (the WPF side of <see cref="CircuitFleetModel{TChip, TRow}"/>, with WPF
/// chips and rows).</summary>
public sealed class CircuitFleetVm(AssetExplorerVm explorer) : CircuitFleetModel<BandChip, CircuitFleetRow>(explorer);

/// <summary>A status row in the corridor dashboard's static panels (the WPF row: adds the <see cref="Dot"/> brush).</summary>
public sealed class CircuitStatusRow : CircuitStatusRowModel
{
    public Brush Dot => Palette.Frozen(DotColour);
}

/// <summary>One corridor's dashboard (the WPF side of <see cref="CircuitDetailModel{TStatus}"/>: adds the
/// <see cref="OpenIn"/> and <see cref="OpenOut"/> commands, the <see cref="LoadingBrush"/> and the
/// <see cref="QverdictBrush"/>).</summary>
public sealed class CircuitDetailVm(int index, int fromBus, int toBus, string? branchName, AssetExplorerVm explorer)
    : CircuitDetailModel<CircuitStatusRow>(index, fromBus, toBus, branchName, explorer)
{
    private ICommand? _openIn, _openOut;

    // Click-through to the corridor's two endpoint substations (parameters: VInBusId, VOutBusId, read at click).
    public ICommand OpenIn => _openIn ??= new RelayCommand<int>(OpenInSubstation);
    public ICommand OpenOut => _openOut ??= new RelayCommand<int>(OpenOutSubstation);
    public Brush LoadingBrush => Palette.Frozen(LoadingColour);
    public Brush QverdictBrush => Palette.Frozen(QverdictColour);

    protected override void OnPropertyChanged(string? name)
    {
        base.OnPropertyChanged(name);
        if (name == nameof(LoadingColour)) base.OnPropertyChanged(nameof(LoadingBrush));
        else if (name == nameof(QverdictColour)) base.OnPropertyChanged(nameof(QverdictBrush));
    }
}
