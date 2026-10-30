using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using GridSim.Ui.Assets.Pages;
using GridSim.Wpf.Visuals;

namespace GridSim.Wpf.Views.Assets;

// UI programme ASSETS A2: the ASSETS tab's page logic moved to the platform-neutral GridSim.Ui.Assets.Pages
// (src/GridSim.Ui/Assets/Pages/), line for line, with each WPF Brush replaced by a neutral colour token (UiColour), each
// Visibility by a bool and each command by an action or method. This folder is now the thin WPF side of that boundary:
// every page and row derives from its neutral model and adds, under the OLD names the XAML binds, a frozen brush for
// every token (Palette.Frozen: the same colour bytes), a Visibility for every bool (true = Visible, false = Collapsed)
// and an ICommand over every action. The explorer makes these WPF pages (its Create... overrides), so the DataTemplates
// in AssetExplorerView.xaml and the *Templates.xaml dictionaries match them by type exactly as before, and what the user
// sees and clicks is unchanged. The ASSETS tab is still fed by DashboardViewModel.UpdateTabs (Assets.Update, first of
// the tab hooks) and gated by MainWindow (Assets.Active); both call the members this explorer inherits.

/// <summary>Minimal ICommand for asset-page navigation.</summary>
public sealed class RelayCommand(Action exec) : ICommand
{
    public event EventHandler? CanExecuteChanged { add { } remove { } }
    public bool CanExecute(object? p) => true;
    public void Execute(object? p) => exec();
}

/// <summary>ICommand that passes its <c>CommandParameter</c> through to the handler  -  used for
/// cross-links whose target id changes per frame (the parameter binds to a live VM property).</summary>
public sealed class RelayCommand<T>(Action<T> exec) : ICommand
{
    public event EventHandler? CanExecuteChanged { add { } remove { } }
    public bool CanExecute(object? p) => true;
    public void Execute(object? p) { if (p is T t) exec(t); }
}

/// <summary>
/// The explorer shell (the WPF side of <see cref="AssetExplorerModel"/>): adds the BACK / ASSETS commands and the BACK
/// button's visibility the header binds, and makes the WPF page types so the DataTemplates match them.
/// </summary>
public sealed class AssetExplorerVm : AssetExplorerModel
{
    public ICommand BackCmd { get; }
    public ICommand HomeCmd { get; }

    /// <summary>The BACK button's visibility: <see cref="AssetExplorerModel.BackVisible"/> as Visible / Collapsed.</summary>
    public Visibility BackVis => BackVisible ? Visibility.Visible : Visibility.Collapsed;

    public AssetExplorerVm() : base(e => new AssetHomeVm((AssetExplorerVm)e))
    {
        BackCmd = new RelayCommand(Back);
        HomeCmd = new RelayCommand(GoHome);
    }

    /// <summary>Announce the neutral property, then the WPF visibility bound in its place.</summary>
    protected override void OnPropertyChanged(string? name)
    {
        base.OnPropertyChanged(name);
        if (name == nameof(BackVisible)) base.OnPropertyChanged(nameof(BackVis));
    }

    // ---- the WPF pages (each derives from its neutral page and adds the brushes and commands the templates bind) ----
    public override AssetPageModel CreateGeneratorFleet() => new GeneratorFleetVm(this);
    public override AssetPageModel CreatePhasorFleet() => new GeneratorPhasorFleetVm(this);
    public override AssetPageModel CreateInterconnectorFleet() => new InterconnectorFleetVm(this);
    public override AssetPageModel CreateTransformerFleet() => new TransformerFleetVm(this);
    public override AssetPageModel CreateCircuitFleet() => new CircuitFleetVm(this);
    public override AssetPageModel CreateSubstationFleet() => new SubstationFleetVm(this);
    public override AssetPageModel CreateTapChangerFleet() => new TapChangerFleetVm(this);
    public override AssetPageModel CreateSubstationDetail(int busId) => new SubstationDetailVm(busId, this);
    public override AssetPageModel CreateGeneratorDetail(int index, int bus, string? name) => new GeneratorDetailVm(index, bus, name, this);
    public override AssetPageModel CreateInterconnectorDetail(int index, int bus, string? name) => new InterconnectorDetailVm(index, bus, name, this);
    public override AssetPageModel CreateCircuitDetail(int index, int fromBus, int toBus, string? name) =>
        new CircuitDetailVm(index, fromBus, toBus, name, this);
    public override AssetPageModel CreateTransformerDetail(int index, int fromBus, int toBus, string? name) =>
        new TransformerDetailVm(index, fromBus, toBus, name, this);
}

/// <summary>One asset-class summary card (the WPF card: adds the <see cref="Open"/> command and the
/// <see cref="MetricBrush"/>).</summary>
public sealed class AssetClassCard : AssetClassCardModel
{
    private ICommand? _open;
    public ICommand Open => _open ??= new RelayCommand(OnOpen);
    public Brush MetricBrush => Palette.Frozen(MetricColour);

    protected override void OnPropertyChanged(string? name)
    {
        base.OnPropertyChanged(name);
        if (name == nameof(MetricColour)) base.OnPropertyChanged(nameof(MetricBrush));
    }
}

/// <summary>The landing page (the WPF side of <see cref="AssetHomeModel{TCard}"/>, with WPF cards).</summary>
public sealed class AssetHomeVm(AssetExplorerVm explorer) : AssetHomeModel<AssetClassCard>(explorer);

/// <summary>One link's row on the interconnector fleet page (the WPF row: adds <see cref="Open"/> and <see cref="Bar"/>).</summary>
public sealed class LinkFleetRow : LinkFleetRowModel
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

/// <summary>The interconnector fleet page (the WPF side of <see cref="InterconnectorFleetModel{TRow}"/>, with WPF rows).</summary>
public sealed class InterconnectorFleetVm(AssetExplorerVm explorer) : InterconnectorFleetModel<LinkFleetRow>(explorer);

/// <summary>One interconnector's dashboard (the WPF side of <see cref="InterconnectorDetailModel"/>: adds the
/// <see cref="OpenLanding"/> command and the <see cref="DirectionBrush"/>).</summary>
public sealed class InterconnectorDetailVm(int index, int unitBus, string? unitName, AssetExplorerVm explorer)
    : InterconnectorDetailModel(index, unitBus, unitName, explorer)
{
    private ICommand? _openLanding;

    /// <summary>Click-through to the substation the link lands on (parameter: <see cref="InterconnectorDetailModel.LandingBusId"/>).</summary>
    public ICommand OpenLanding => _openLanding ??= new RelayCommand<int>(OpenLandingSubstation);
    public Brush DirectionBrush => Palette.Frozen(DirectionColour);

    protected override void OnPropertyChanged(string? name)
    {
        base.OnPropertyChanged(name);
        if (name == nameof(DirectionColour)) base.OnPropertyChanged(nameof(DirectionBrush));
    }
}
