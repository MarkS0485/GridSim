using System.Windows.Media;
using GridSim.Ui.Presentation;
using GridSim.Ui.Tabs.Machine;
using GridSim.Wpf.Visuals;

namespace GridSim.Wpf.Views;

// UI programme Phase 1.6 (second tab batch): THE MACHINE tab's logic moved to the platform-neutral
// GridSim.Ui.Tabs.Machine.MachineModel (src/GridSim.Ui/Tabs/Machine/MachineModel.cs), line for line, with each WPF Brush
// replaced by a neutral colour token (UiColour). This file is now the thin WPF side of that boundary: the view-model and
// its rows derive from the neutral ones and add, under the OLD property names the XAML binds (Color, DevBrush, DivBrush,
// AccumBrush, BessBrush, VoltBrush, QBrush), the frozen brush of each token (Palette.Frozen: the same colour bytes, the
// same cached brush instances). The dashboard still feeds and resets it through its hooks (Machine.Update(f),
// Machine.Reset()), at exactly the points it did. What the user sees is unchanged.

/// <summary>One energy stream in or out of the machine (the WPF row: adds the <see cref="Color"/> brush).</summary>
public sealed class StreamRow : StreamRowModel
{
    /// <summary>The row's brush: the frozen brush of <see cref="StreamRowModel.Colour"/> (set once, at creation).</summary>
    public Brush Color => Palette.Frozen(Colour);
}

/// <summary>One voltage level of the transmission "shaft" (the WPF row: adds the <see cref="Color"/> and
/// <see cref="DevBrush"/> brushes).</summary>
public sealed class VoltageLayer : VoltageLayerModel
{
    /// <summary>The MW bar's brush: the frozen brush of <see cref="VoltageLayerModel.Colour"/>.</summary>
    public Brush Color => Palette.Frozen(Colour);

    /// <summary>The deviation marker's brush: the frozen brush of <see cref="VoltageLayerModel.DevColour"/>.</summary>
    public Brush DevBrush => Palette.Frozen(DevColour);

    protected override void OnPropertyChanged(string? name)
    {
        base.OnPropertyChanged(name);
        if (name == nameof(Colour)) base.OnPropertyChanged(nameof(Color));
        else if (name == nameof(DevColour)) base.OnPropertyChanged(nameof(DevBrush));
    }
}

/// <summary>One on-load tap changer in the fleet (the WPF row: adds the <see cref="Color"/> brush).</summary>
public sealed class TapRow : TapRowModel
{
    /// <summary>The row's brush: the frozen brush of <see cref="TapRowModel.Colour"/>.</summary>
    public Brush Color => Palette.Frozen(Colour);

    protected override void OnPropertyChanged(string? name)
    {
        base.OnPropertyChanged(name);
        if (name == nameof(Colour)) base.OnPropertyChanged(nameof(Color));
    }
}

/// <summary>
/// THE MACHINE  -  the whole grid reduced to one rotating machine for the layman: the neutral
/// <see cref="MachineModel"/> plus its WPF brushes and rows.
/// </summary>
public sealed class MachineVm : MachineModel
{
    // ---- the WPF brushes: each the cached frozen brush of its neutral colour token ----

    public Brush DivBrush => Palette.Frozen(DivColour);
    public Brush AccumBrush => Palette.Frozen(AccumColour);
    public Brush BessBrush => Palette.Frozen(BessColour);
    public Brush VoltBrush => Palette.Frozen(VoltColour);
    public Brush QBrush => Palette.Frozen(QColour);

    /// <summary>Each neutral colour property and the WPF brush property the XAML binds in its place.</summary>
    private static readonly Dictionary<string, string> AdapterOf = new(StringComparer.Ordinal)
    {
        [nameof(DivColour)] = nameof(DivBrush),
        [nameof(AccumColour)] = nameof(AccumBrush),
        [nameof(BessColour)] = nameof(BessBrush),
        [nameof(VoltColour)] = nameof(VoltBrush),
        [nameof(QColour)] = nameof(QBrush),
    };

    /// <summary>Announce the neutral property, then (when it has one) the WPF adapter property bound in its place.</summary>
    protected override void OnPropertyChanged(string? name)
    {
        base.OnPropertyChanged(name);
        if (name is not null && AdapterOf.TryGetValue(name, out string? adapter)) base.OnPropertyChanged(adapter);
    }

    // ---- the WPF rows ----

    protected override StreamRowModel CreateStreamRow(string key, string name, UiColour colour) =>
        new StreamRow { Key = key, Name = name, Colour = colour };

    protected override VoltageLayerModel CreateVoltageLayer(string label, UiColour colour) =>
        new VoltageLayer { Label = label, Colour = colour };

    protected override TapRowModel CreateTapRow(int oltcIndex, string name) =>
        new TapRow { OltcIndex = oltcIndex, Name = name };
}
