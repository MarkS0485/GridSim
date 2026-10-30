using System.Windows.Input;
using System.Windows.Media;
using GridSim.Ui.Assets.Pages;
using GridSim.Wpf.Visuals;

namespace GridSim.Wpf.Views.Assets;

// UI programme ASSETS A2: the transformer and tap-changer pages' logic moved to
// src/GridSim.Ui/Assets/Pages/TransformerPages.cs, line for line (the bus lookups, the per-refresh tables and the OLTC
// mapping went with it). These are the thin WPF sides (see AssetPages.cs): the brushes and commands the
// TransformerTemplates.xaml bindings name, over the neutral tokens and actions.

/// <summary>One transformer's row on the fleet page (the WPF row: adds <see cref="Open"/> and <see cref="Bar"/>).</summary>
public sealed class TransformerFleetRow : TransformerFleetRowModel
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

/// <summary>The transformer fleet page (the WPF side of <see cref="TransformerFleetModel{TRow}"/>, with WPF rows).</summary>
public sealed class TransformerFleetVm(AssetExplorerVm explorer) : TransformerFleetModel<TransformerFleetRow>(explorer);

/// <summary>A labelled measurement row on the unit page (the WPF row: adds the <see cref="Chip"/> brush).</summary>
public sealed class TransformerReading : TransformerReadingModel
{
    public Brush Chip => Palette.Frozen(ChipColour);
}

/// <summary>One line in the unit's event log (the WPF row: adds the <see cref="Chip"/> brush).</summary>
public sealed class TransformerEvent : TransformerEventModel
{
    public Brush Chip => Palette.Frozen(ChipColour);
}

/// <summary>One transformer's dashboard (the WPF side of <see cref="TransformerDetailModel{TReading, TEvent}"/>: adds
/// the <see cref="OpenHv"/>, <see cref="OpenLv"/> and <see cref="OpenOltcBus"/> commands and the
/// <see cref="LoadingBrush"/> and <see cref="OltcBrush"/>).</summary>
public sealed class TransformerDetailVm(int index, int fromBus, int toBus, string? branchName, AssetExplorerVm explorer)
    : TransformerDetailModel<TransformerReading, TransformerEvent>(index, fromBus, toBus, branchName, explorer)
{
    private ICommand? _openHv, _openLv, _openOltcBus;

    // Click-through to the HV/LV endpoint substations and the bus the OLTC regulates (parameters: HvBusId, LvBusId,
    // OltcBusId).
    public ICommand OpenHv => _openHv ??= new RelayCommand<int>(OpenHvSubstation);
    public ICommand OpenLv => _openLv ??= new RelayCommand<int>(OpenLvSubstation);
    public ICommand OpenOltcBus => _openOltcBus ??= new RelayCommand<int>(OpenOltcBusSubstation);
    public Brush LoadingBrush => Palette.Frozen(LoadingColour);
    public Brush OltcBrush => Palette.Frozen(OltcColour);

    protected override void OnPropertyChanged(string? name)
    {
        base.OnPropertyChanged(name);
        if (name == nameof(LoadingColour)) base.OnPropertyChanged(nameof(LoadingBrush));
        else if (name == nameof(OltcColour)) base.OnPropertyChanged(nameof(OltcBrush));
    }
}

/// <summary>One OLTC's row on the tap-changer fleet page (the WPF row: adds <see cref="Open"/> and <see cref="Chip"/>).</summary>
public sealed class TapChangerRow : TapChangerRowModel
{
    private ICommand? _open;
    public ICommand Open => _open ??= new RelayCommand(OnOpen);
    public Brush Chip => Palette.Frozen(ChipColour);

    protected override void OnPropertyChanged(string? name)
    {
        base.OnPropertyChanged(name);
        if (name == nameof(ChipColour)) base.OnPropertyChanged(nameof(Chip));
    }
}

/// <summary>The tap-changer fleet page (the WPF side of <see cref="TapChangerFleetModel{TRow}"/>, with WPF rows).</summary>
public sealed class TapChangerFleetVm(AssetExplorerVm explorer) : TapChangerFleetModel<TapChangerRow>(explorer);
