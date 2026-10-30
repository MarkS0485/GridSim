using System.Windows;
using System.Windows.Media;
using GridSim.Ui.Tabs.Operator;
using GridSim.Wpf.Visuals;

namespace GridSim.Wpf.Views;

// UI programme Phase 1.6 (first tab batch): the OPERATOR tab's logic moved to the platform-neutral
// GridSim.Ui.Tabs.Operator.OperatorModel (src/GridSim.Ui/Tabs/Operator/OperatorModel.cs), line for line, with each WPF
// Brush replaced by a neutral colour token (UiColour) and the warning banner's Visibility by a bool. This file is now
// the thin WPF side of that boundary: the view-model and its rows derive from the neutral ones and add, under the OLD
// property names the XAML binds, a frozen brush for every token (Palette.Frozen: the same colour bytes) and a
// Visibility for the bool (true = Visible, false = Collapsed). What the user sees is unchanged.

/// <summary>
/// THE OPERATOR TAB  -  the control-room / system-operator view of the streamed grid state (the WPF side of
/// <see cref="OperatorModel"/>: adds the brushes and the banner's Visibility the XAML binds).
/// </summary>
public sealed class OperatorVm : OperatorModel
{
    /// <summary>The frequency read-out's brush: the frozen brush of <see cref="OperatorModel.FrequencyColour"/>.</summary>
    public Brush FrequencyBrush => Palette.Frozen(FrequencyColour);

    /// <summary>The warning banner's brush: the frozen brush of <see cref="OperatorModel.WarningColour"/>.</summary>
    public Brush WarningBrush => Palette.Frozen(WarningColour);

    /// <summary>The warning banner's Visibility: <see cref="OperatorModel.IsWarningVisible"/> as Visible or Collapsed.</summary>
    public Visibility WarningVisible => IsWarningVisible ? Visibility.Visible : Visibility.Collapsed;

    /// <summary>Each neutral property and the WPF adapter property the XAML binds in its place.</summary>
    private static readonly Dictionary<string, string> AdapterOf = new(StringComparer.Ordinal)
    {
        [nameof(FrequencyColour)] = nameof(FrequencyBrush),
        [nameof(WarningColour)] = nameof(WarningBrush),
        [nameof(IsWarningVisible)] = nameof(WarningVisible),
    };

    /// <summary>Announce the neutral property, then (when it has one) the WPF adapter property bound in its place.</summary>
    protected override void OnPropertyChanged(string? name)
    {
        base.OnPropertyChanged(name);
        if (name is not null && AdapterOf.TryGetValue(name, out string? adapter)) base.OnPropertyChanged(adapter);
    }

    // ---- the WPF rows ----

    protected override OpBoundaryRowModel CreateBoundaryRow() => new OpBoundaryRow();
    protected override OpFuelRowModel CreateFuelRow() => new OpFuelRow();
    protected override OpActionRowModel CreateActionRow() => new OpActionRow();
    protected override OpConstraintRowModel CreateConstraintRow() => new OpConstraintRow();
    protected override OpWarningRowModel CreateWarningRow() => new OpWarningRow();
}

/// <summary>One transmission boundary (the WPF row: adds <see cref="LoadingBrush"/>).</summary>
public sealed class OpBoundaryRow : OpBoundaryRowModel
{
    public Brush LoadingBrush => Palette.Frozen(LoadingColour);

    protected override void OnPropertyChanged(string? name)
    {
        base.OnPropertyChanged(name);
        if (name == nameof(LoadingColour)) base.OnPropertyChanged(nameof(LoadingBrush));
    }
}

/// <summary>One fuel of the generation stack (the WPF row: adds <see cref="Swatch"/>).</summary>
public sealed class OpFuelRow : OpFuelRowModel
{
    public Brush Swatch => Palette.Frozen(SwatchColour);

    protected override void OnPropertyChanged(string? name)
    {
        base.OnPropertyChanged(name);
        if (name == nameof(SwatchColour)) base.OnPropertyChanged(nameof(Swatch));
    }
}

/// <summary>One balancing-mechanism acceptance (the WPF row: adds <see cref="KindBrush"/>).</summary>
public sealed class OpActionRow : OpActionRowModel
{
    public Brush KindBrush => Palette.Frozen(KindColour);

    protected override void OnPropertyChanged(string? name)
    {
        base.OnPropertyChanged(name);
        if (name == nameof(KindColour)) base.OnPropertyChanged(nameof(KindBrush));
    }
}

/// <summary>One active network constraint (the WPF row: adds <see cref="BindingBrush"/>).</summary>
public sealed class OpConstraintRow : OpConstraintRowModel
{
    public Brush BindingBrush => Palette.Frozen(BindingColour);

    protected override void OnPropertyChanged(string? name)
    {
        base.OnPropertyChanged(name);
        if (name == nameof(BindingColour)) base.OnPropertyChanged(nameof(BindingBrush));
    }
}

/// <summary>One system warning (the WPF row: adds <see cref="Brush"/>).</summary>
public sealed class OpWarningRow : OpWarningRowModel
{
    public Brush Brush => Palette.Frozen(Colour);

    protected override void OnPropertyChanged(string? name)
    {
        base.OnPropertyChanged(name);
        if (name == nameof(Colour)) base.OnPropertyChanged(nameof(Brush));
    }
}
