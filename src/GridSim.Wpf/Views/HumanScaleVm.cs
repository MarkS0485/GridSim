using System.Windows.Media;
using GridSim.Ui.Tabs.HumanScale;
using GridSim.Wpf.Visuals;

namespace GridSim.Wpf.Views;

// UI programme Phase 1.6 (third tab batch): THE HUMAN SCALE tab's logic moved to the platform-neutral
// GridSim.Ui.Tabs.HumanScale.HumanScaleModel (src/GridSim.Ui/Tabs/HumanScale/HumanScaleModel.cs), line for line, with
// each WPF Brush replaced by a neutral colour token (UiColour). This file is now the thin WPF side of that boundary: the
// view-model and its rows derive from the neutral ones and add, under the OLD property names the XAML binds, a frozen
// brush for every token (Palette.Frozen: the same colour bytes). What the user sees is unchanged.

/// <summary>
/// THE HUMAN SCALE TAB - the same grid physics re-expressed in units a person can feel (the WPF side of
/// <see cref="HumanScaleModel"/>: adds the brushes the XAML binds and makes the WPF rows).
/// </summary>
public sealed class HumanScaleVm : HumanScaleModel
{
    /// <summary>The live braking power's brush: the frozen brush of <see cref="HumanScaleModel.RegenColour"/>.</summary>
    public Brush RegenBrush => Palette.Frozen(RegenColour);

    /// <summary>The wind-up headline and bar brush: the frozen brush of <see cref="HumanScaleModel.TwistColour"/>.</summary>
    public Brush TwistBrush => Palette.Frozen(TwistColour);

    /// <summary>Each neutral property and the WPF adapter property the XAML binds in its place.</summary>
    private static readonly Dictionary<string, string> AdapterOf = new(StringComparer.Ordinal)
    {
        [nameof(RegenColour)] = nameof(RegenBrush),
        [nameof(TwistColour)] = nameof(TwistBrush),
    };

    /// <summary>Announce the neutral property, then (when it has one) the WPF adapter property bound in its place.</summary>
    protected override void OnPropertyChanged(string? name)
    {
        base.OnPropertyChanged(name);
        if (name is not null && AdapterOf.TryGetValue(name, out string? adapter)) base.OnPropertyChanged(adapter);
    }

    // ---- the WPF rows ----

    protected override TwistRegionRowModel CreateRegionRow() => new TwistRegionRow();
    protected override TwistMachineRowModel CreateMachineRow(string key, string name) => new TwistMachineRow { Key = key, Name = name };
}

/// <summary>One latitude band's mean rotor lead (the WPF row: adds <see cref="Bar"/>).</summary>
public sealed class TwistRegionRow : TwistRegionRowModel
{
    public Brush Bar => Palette.Frozen(BarColour);

    protected override void OnPropertyChanged(string? name)
    {
        base.OnPropertyChanged(name);
        if (name == nameof(BarColour)) base.OnPropertyChanged(nameof(Bar));
    }
}

/// <summary>One synchronous machine's rotor lead (the WPF row: adds <see cref="Bar"/>).</summary>
public sealed class TwistMachineRow : TwistMachineRowModel
{
    public Brush Bar => Palette.Frozen(BarColour);

    protected override void OnPropertyChanged(string? name)
    {
        base.OnPropertyChanged(name);
        if (name == nameof(BarColour)) base.OnPropertyChanged(nameof(Bar));
    }
}
