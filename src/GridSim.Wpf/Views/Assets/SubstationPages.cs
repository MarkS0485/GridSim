using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using GridSim.Ui.Assets.Pages;
using GridSim.Ui.Visuals;
using GridSim.Wpf.Visuals;

namespace GridSim.Wpf.Views.Assets;

// UI programme ASSETS A2: the substation pages' logic moved to src/GridSim.Ui/Assets/Pages/SubstationPages.cs, line for
// line (the GDA per-asset card still reads the shared Core AssetInsightFormatter there). These are the thin WPF sides
// (see AssetPages.cs): the brushes, visibility, commands and single-line diagram data the SubstationTemplates.xaml
// bindings name, over the neutral tokens, bool, actions and diagram input. MainWindow's bridge path still calls
// SubstationDetailVm.ApplyAsset and reads BusId, which this page inherits.

/// <summary>One bus's row on the substations fleet page (the WPF row: adds <see cref="Open"/> and <see cref="VmBrush"/>).</summary>
public sealed class SubstationFleetRow : SubstationFleetRowModel
{
    private ICommand? _open;
    public ICommand Open => _open ??= new RelayCommand(OnOpen);
    public Brush VmBrush => Palette.Frozen(VmColour);

    protected override void OnPropertyChanged(string? name)
    {
        base.OnPropertyChanged(name);
        if (name == nameof(VmColour)) base.OnPropertyChanged(nameof(VmBrush));
    }
}

/// <summary>One toggle chip in the voltage-level filter row (the WPF chip: adds the <see cref="Toggle"/> command).</summary>
public sealed class KvFilterChip : KvFilterChipModel
{
    private ICommand? _toggle;
    public ICommand Toggle => _toggle ??= new RelayCommand(OnToggle);
}

/// <summary>The substations fleet page (the WPF side of <see cref="SubstationFleetModel{TChip, TRow}"/>, with WPF chips
/// and rows).</summary>
public sealed class SubstationFleetVm(AssetExplorerVm explorer) : SubstationFleetModel<KvFilterChip, SubstationFleetRow>(explorer);

/// <summary>One generator's row in a substation's connected-plant list (the WPF row: adds <see cref="Open"/> and
/// <see cref="FuelBrush"/>).</summary>
public sealed class PlantRow : PlantRowModel
{
    private ICommand? _open;
    public ICommand Open => _open ??= new RelayCommand(OnOpen);
    public Brush FuelBrush => Palette.Frozen(FuelColour);

    protected override void OnPropertyChanged(string? name)
    {
        base.OnPropertyChanged(name);
        if (name == nameof(FuelColour)) base.OnPropertyChanged(nameof(FuelBrush));
    }
}

/// <summary>One branch's row in a substation's connections list (the WPF row: adds <see cref="Open"/> and
/// <see cref="Bar"/>).</summary>
public sealed class ConnectionRow : ConnectionRowModel
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

/// <summary>One substation's dashboard (the WPF side of <see cref="SubstationDetailModel{TPlant, TConn}"/>: adds the
/// <see cref="VoltageBrush"/>, the GDA card's <see cref="AssetVis"/> and the single-line diagram's <see cref="MapData"/>).</summary>
public sealed class SubstationDetailVm(int busId, AssetExplorerVm explorer)
    : SubstationDetailModel<PlantRow, ConnectionRow>(busId, explorer)
{
    public Brush VoltageBrush => Palette.Frozen(VoltageColour);

    /// <summary>The GDA per-asset card's visibility: <see cref="SubstationDetailModel{TPlant, TConn}.AssetVisible"/> as
    /// Visible / Collapsed.</summary>
    public Visibility AssetVis => AssetVisible ? Visibility.Visible : Visibility.Collapsed;

    /// <summary>The single-line diagram's data: the neutral <see cref="SubstationDetailModel{TPlant, TConn}.MapInput"/>
    /// field for field, each fuel colour as the WPF colour with the same four bytes. Made once per new input.</summary>
    public SubstationMapData? MapData { get; private set; }

    protected override void OnPropertyChanged(string? name)
    {
        // The diagram data is converted before anyone hears that it changed, so a listener reads the new one.
        if (name == nameof(MapInput)) MapData = MapInput is { } input ? ToMapData(input) : null;
        base.OnPropertyChanged(name);
        if (name == nameof(VoltageColour)) base.OnPropertyChanged(nameof(VoltageBrush));
        else if (name == nameof(AssetVisible)) base.OnPropertyChanged(nameof(AssetVis));
        else if (name == nameof(MapInput)) base.OnPropertyChanged(nameof(MapData));
    }

    /// <summary>The WPF diagram data for a neutral input: every field copied, every colour token as its WPF colour.</summary>
    private static SubstationMapData ToMapData(SubstationMapInput i) => new()
    {
        VoltagePu = i.VoltagePu,
        AngleDeg = i.AngleDeg,
        BaseKv = i.BaseKv,
        DemandMw = i.DemandMw,
        ReactiveMvar = i.ReactiveMvar,
        GenerationMw = i.GenerationMw,
        BusType = i.BusType,
        NetInjectionMw = i.NetInjectionMw,
        Connections = i.Connections.Select(c => new MapConnection(c.Name, c.OtherEnd, c.Kind, c.FlowMw, c.Exporting, c.LoadingFrac)).ToList(),
        Generators = i.Generators.Select(g => new MapGenerator(g.Name, g.Fuel, Palette.ToColor(g.FuelColour), g.OutputMw)).ToList(),
    };
}
