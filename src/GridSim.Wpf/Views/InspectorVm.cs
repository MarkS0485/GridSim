using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using GridSim.Ui.Presentation;
using GridSim.Ui.Tabs.Inspector;
using GridSim.Wpf.Views.Assets;   // RelayCommand
using GridSim.Wpf.Visuals;

namespace GridSim.Wpf.Views;

// UI programme Phase 1.6 (second tab batch): the drill-down inspector's logic moved to the platform-neutral
// GridSim.Ui.Tabs.Inspector.InspectorModel (src/GridSim.Ui/Tabs/Inspector/InspectorModel.cs), line for line. This file is
// now the thin WPF side of that boundary: under the OLD names the XAML binds it adds the Visibility of each bool
// (Visibility, BackVisibility, a row's Chevron), a command for each action (CloseCommand, BackCommand, a row's Drill) and
// a brush for each colour token (ValueBrush, Swatch). The NESO RP1 and MECHANICAL tabs build their pages exactly as
// before (NESO RP1 still writes "new InspectStat { ..., ValueBrush = b }"), so what the user sees is unchanged.

/// <summary>
/// A reusable drill-down overlay for the RP1 / MECHANICAL tabs: the neutral <see cref="InspectorModel"/> plus its WPF
/// visibilities, commands and row types.
/// </summary>
public sealed class InspectorVm : InspectorModel
{
    /// <summary>The overlay's visibility: <see cref="InspectorModel.IsOpen"/> as Visible / Collapsed.</summary>
    public Visibility Visibility => IsOpen ? Visibility.Visible : Visibility.Collapsed;

    /// <summary>The "&lt; back" button's visibility: <see cref="InspectorModel.CanBack"/> as Visible / Collapsed.</summary>
    public Visibility BackVisibility => CanBack ? Visibility.Visible : Visibility.Collapsed;

    public ICommand CloseCommand { get; }
    public ICommand BackCommand { get; }

    public InspectorVm()
    {
        CloseCommand = new RelayCommand(Close);
        BackCommand = new RelayCommand(Back);
    }

    /// <summary>Announce the neutral property, then the WPF visibility bound in its place.</summary>
    protected override void OnPropertyChanged(string? name)
    {
        base.OnPropertyChanged(name);
        if (name == nameof(IsOpen)) base.OnPropertyChanged(nameof(Visibility));
        else if (name == nameof(CanBack)) base.OnPropertyChanged(nameof(BackVisibility));
    }

    // ---- the WPF tiles and rows (with their brushes), for the pages the neutral tabs build ----

    public override InspectStatModel CreateStat(string label, string value, UiColour? valueColour = null) =>
        valueColour is UiColour c
            ? new InspectStat { Label = label, Value = value, ValueColour = c }
            : new InspectStat { Label = label, Value = value };

    public override InspectRowModel CreateRow(string primary, string value, UiColour swatchColour, UiColour valueColour,
        string secondary = "", string sub = "", Action? onDrill = null) =>
        new InspectRow
        {
            Primary = primary, Secondary = secondary, Value = value, Sub = sub,
            SwatchColour = swatchColour, ValueColour = valueColour, OnDrill = onDrill,
        };

    /// <summary>
    /// The neutral colour token of a brush a WPF tab hands a tile or row: the four bytes of a plain solid brush at full
    /// brush opacity (every caller passes a <see cref="Palette.Frozen(Color)"/> brush or a WPF named solid brush).
    /// Anything else has no single token, so it is refused loudly rather than reported as a different colour.
    /// </summary>
    internal static UiColour TokenOf(Brush brush) =>
        brush is SolidColorBrush { Opacity: 1.0 } s
            ? new UiColour(s.Color.A, s.Color.R, s.Color.G, s.Color.B)
            : throw new ArgumentException($"The inspector takes a plain solid brush; got {brush}.", nameof(brush));
}

/// <summary>A headline combined/averaged figure shown as a tile at the top of an inspector page (the WPF tile: adds
/// the <see cref="ValueBrush"/>).</summary>
public sealed class InspectStat : InspectStatModel
{
    private readonly Brush? _valueBrush;

    /// <summary>The value's brush. Set by a WPF tab (NESO RP1), it is that very brush, and the neutral token is its
    /// colour; left unset, it is the frozen brush of <see cref="InspectStatModel.ValueColour"/> (white by default).</summary>
    public Brush ValueBrush
    {
        get => _valueBrush ?? Palette.Frozen(ValueColour);
        init { _valueBrush = value; ValueColour = InspectorVm.TokenOf(value); }
    }
}

/// <summary>One constituent row in an inspector page (the WPF row: adds the <see cref="Swatch"/> and
/// <see cref="ValueBrush"/> brushes, the <see cref="Drill"/> command and the <see cref="Chevron"/>).</summary>
public sealed class InspectRow : InspectRowModel
{
    private readonly Brush? _swatch, _valueBrush;
    private readonly ICommand? _drill;
    private ICommand? _drillFromAction;   // made once, on first read, for a row built by a neutral tab

    /// <summary>The swatch's brush (a WPF tab's own brush when it set one, else the frozen brush of the token).</summary>
    public Brush Swatch
    {
        get => _swatch ?? Palette.Frozen(SwatchColour);
        init { _swatch = value; SwatchColour = InspectorVm.TokenOf(value); }
    }

    /// <summary>The value's brush (a WPF tab's own brush when it set one, else the frozen brush of the token).</summary>
    public Brush ValueBrush
    {
        get => _valueBrush ?? Palette.Frozen(ValueColour);
        init { _valueBrush = value; ValueColour = InspectorVm.TokenOf(value); }
    }

    /// <summary>The row's drill command: a WPF tab's own command when it set one (the neutral action then runs it), else
    /// a command running <see cref="InspectRowModel.OnDrill"/>; null when the row does not drill.</summary>
    public ICommand? Drill
    {
        get => _drill ?? (OnDrill is { } act ? _drillFromAction ??= new RelayCommand(act) : null);
        init { _drill = value; OnDrill = value is null ? null : () => value.Execute(null); }
    }

    /// <summary>The chevron's visibility: shown when the row drills.</summary>
    public Visibility Chevron => HasDrill ? Visibility.Visible : Visibility.Collapsed;
}
