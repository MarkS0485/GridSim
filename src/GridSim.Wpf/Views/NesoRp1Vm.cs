using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using GridSim.Ui.Tabs.NesoRp1;
using GridSim.Wpf.Views.Assets;   // RelayCommand
using GridSim.Wpf.Visuals;

namespace GridSim.Wpf.Views;

// UI programme Phase 1.6 (third tab batch): the NESO RP1 tab's logic moved to the platform-neutral
// GridSim.Ui.Tabs.NesoRp1.NesoRp1Model (src/GridSim.Ui/Tabs/NesoRp1/NesoRp1Model.cs), line for line, with each WPF
// Brush replaced by a neutral colour token (UiColour), the two page Visibility values by bools, the three commands by
// plain methods (InspectFuel, InspectBm, InspectIc), and the drill-down pages built as neutral pages. This file is now
// the thin WPF side of that boundary: the view-model and its rows derive from the neutral ones and add, under the OLD
// names the XAML binds, a frozen brush for every token (Palette.Frozen: the same colour bytes), a Visibility for each
// bool (true = Visible, false = Collapsed), the WPF commands over the neutral methods, and the WPF InspectorVm that
// shows each neutral page exactly as the old code built it. What the user sees and clicks is unchanged.

/// <summary>
/// THE NESO RP1 TAB - the situational-awareness control-room page (the WPF side of <see cref="NesoRp1Model"/>: adds
/// the brushes, the page visibilities, the commands and the drill-down inspector the XAML binds, and makes the WPF rows).
/// </summary>
public sealed class NesoRp1Vm : NesoRp1Model
{
    /// <summary>A NESO RP1 tab whose row clicks drill into <see cref="Inspector"/>.</summary>
    public NesoRp1Vm()
    {
        InspectFuelCommand = new RelayCommand<string>(InspectFuel);
        InspectBmCommand = new RelayCommand<Rp1BmRowModel>(InspectBm);
        InspectIcCommand = new RelayCommand<Rp1IcRowModel>(InspectIc);
    }

    // ---- drill-down inspector (click a fuel / BM unit / interconnector to see how it is made up) ----
    public InspectorVm Inspector { get; } = new();
    public ICommand InspectFuelCommand { get; }
    public ICommand InspectBmCommand { get; }
    public ICommand InspectIcCommand { get; }

    /// <summary>
    /// Show a neutral drill-down page in the WPF inspector: each stat and row as the WPF InspectStat / InspectRow the
    /// old code built (the same texts, the frozen brush of each colour token, and a RelayCommand over a row's drill).
    /// </summary>
    protected override void ShowInspector(Rp1InspectPage page) =>
        Inspector.Show(page.Title, page.Subtitle,
            page.Stats.Select(s => new InspectStat { Label = s.Label, Value = s.Value, ValueBrush = Palette.Frozen(s.ValueColour) }).ToList(),
            page.Rows.Select(r => new InspectRow
            {
                Primary = r.Primary,
                Secondary = r.Secondary,
                Value = r.Value,
                Sub = r.Sub,
                Swatch = Palette.Frozen(r.Swatch),
                ValueBrush = Palette.Frozen(r.ValueColour),
                Drill = r.Drill is { } drill ? new RelayCommand(drill) : null,
            }).ToList());

    // ---- the brushes and visibilities the XAML binds ----

    public Brush PurchasedBrush => Palette.Frozen(PurchasedColour);
    public Brush UnitBrush => Palette.Frozen(UnitColour);
    public Brush GroupBrush => Palette.Frozen(GroupColour);
    public Brush LfddBrush => Palette.Frozen(LfddColour);

    /// <summary>N301: <see cref="NesoRp1Model.IsPageVisible"/> as Visible or Collapsed.</summary>
    public Visibility PageVisibility => IsPageVisible ? Visibility.Visible : Visibility.Collapsed;

    /// <summary>N301: <see cref="NesoRp1Model.IsNotApplicableVisible"/> as Visible or Collapsed.</summary>
    public Visibility NotApplicableVisibility => IsNotApplicableVisible ? Visibility.Visible : Visibility.Collapsed;

    /// <summary>Each neutral property and the WPF adapter property the XAML binds in its place.</summary>
    private static readonly Dictionary<string, string> AdapterOf = new(StringComparer.Ordinal)
    {
        [nameof(PurchasedColour)] = nameof(PurchasedBrush),
        [nameof(UnitColour)] = nameof(UnitBrush),
        [nameof(GroupColour)] = nameof(GroupBrush),
        [nameof(LfddColour)] = nameof(LfddBrush),
        [nameof(IsPageVisible)] = nameof(PageVisibility),
        [nameof(IsNotApplicableVisible)] = nameof(NotApplicableVisibility),
    };

    /// <summary>Announce the neutral property, then (when it has one) the WPF adapter property bound in its place.</summary>
    protected override void OnPropertyChanged(string? name)
    {
        base.OnPropertyChanged(name);
        if (name is not null && AdapterOf.TryGetValue(name, out string? adapter)) base.OnPropertyChanged(adapter);
    }

    // ---- the WPF rows ----

    protected override Rp1InertiaRowModel CreateInertiaRow() => new Rp1InertiaRow();
    protected override Rp1GenRowModel CreateGenRow() => new Rp1GenRow();
    protected override Rp1BoundaryRowModel CreateBoundaryRow() => new Rp1BoundaryRow();
    protected override Rp1ConstraintRowModel CreateConstraintRow() => new Rp1ConstraintRow();
    protected override Rp1BmRowModel CreateBmRow() => new Rp1BmRow();
    protected override Rp1IcRowModel CreateIcRow() => new Rp1IcRow();
}

/// <summary>One fuel's share of intrinsic inertia (the WPF row: adds <see cref="Swatch"/>).</summary>
public sealed class Rp1InertiaRow : Rp1InertiaRowModel
{
    public Brush Swatch => Palette.Frozen(SwatchColour);

    protected override void OnPropertyChanged(string? name)
    {
        base.OnPropertyChanged(name);
        if (name == nameof(SwatchColour)) base.OnPropertyChanged(nameof(Swatch));
    }
}

/// <summary>One fuel of the dispatch stack (the WPF row: adds <see cref="Swatch"/> and <see cref="KindBrush"/>).</summary>
public sealed class Rp1GenRow : Rp1GenRowModel
{
    public Brush Swatch => Palette.Frozen(SwatchColour);
    public Brush KindBrush => Palette.Frozen(KindColour);

    protected override void OnPropertyChanged(string? name)
    {
        base.OnPropertyChanged(name);
        if (name == nameof(SwatchColour)) base.OnPropertyChanged(nameof(Swatch));
        else if (name == nameof(KindColour)) base.OnPropertyChanged(nameof(KindBrush));
    }
}

/// <summary>One boundary transfer (the WPF row: adds <see cref="LoadingBrush"/> and <see cref="TrendBrush"/>).</summary>
public sealed class Rp1BoundaryRow : Rp1BoundaryRowModel
{
    public Brush LoadingBrush => Palette.Frozen(LoadingColour);
    public Brush TrendBrush => Palette.Frozen(TrendColour);

    protected override void OnPropertyChanged(string? name)
    {
        base.OnPropertyChanged(name);
        if (name == nameof(LoadingColour)) base.OnPropertyChanged(nameof(LoadingBrush));
        else if (name == nameof(TrendColour)) base.OnPropertyChanged(nameof(TrendBrush));
    }
}

/// <summary>One lake-streamed constraint (the WPF row: adds <see cref="BindingBrush"/>).</summary>
public sealed class Rp1ConstraintRow : Rp1ConstraintRowModel
{
    public Brush BindingBrush => Palette.Frozen(BindingColour);

    protected override void OnPropertyChanged(string? name)
    {
        base.OnPropertyChanged(name);
        if (name == nameof(BindingColour)) base.OnPropertyChanged(nameof(BindingBrush));
    }
}

/// <summary>One balancing-mechanism acceptance (the WPF row: adds <see cref="DirBrush"/>).</summary>
public sealed class Rp1BmRow : Rp1BmRowModel
{
    public Brush DirBrush => Palette.Frozen(DirColour);

    protected override void OnPropertyChanged(string? name)
    {
        base.OnPropertyChanged(name);
        if (name == nameof(DirColour)) base.OnPropertyChanged(nameof(DirBrush));
    }
}

/// <summary>One interconnector (the WPF row: adds <see cref="FlowBrush"/> and <see cref="FlagBrush"/>).</summary>
public sealed class Rp1IcRow : Rp1IcRowModel
{
    public Brush FlowBrush => Palette.Frozen(FlowColour);
    public Brush FlagBrush => Palette.Frozen(FlagColour);

    protected override void OnPropertyChanged(string? name)
    {
        base.OnPropertyChanged(name);
        if (name == nameof(FlowColour)) base.OnPropertyChanged(nameof(FlowBrush));
        else if (name == nameof(FlagColour)) base.OnPropertyChanged(nameof(FlagBrush));
    }
}
