using System.Windows.Media;
using GridSim.Ui.Presentation;
using GridSim.Ui.Tabs.InverterFleet;
using GridSim.Wpf.Visuals;

namespace GridSim.Wpf.Views;

// UI programme Phase 1.6 (second tab batch): the INVERTER FLEET tab's logic moved to the platform-neutral
// GridSim.Ui.Tabs.InverterFleet.InverterFleetModel (src/GridSim.Ui/Tabs/InverterFleet/InverterFleetModel.cs), line for
// line, with each WPF Brush replaced by a neutral colour token (UiColour). This file is now the thin WPF side of that
// boundary: under the OLD property names the XAML binds (TrippedBrush, a row's ModeBrush and TrippedBrush) it adds the
// frozen brush of each token (the same colour bytes). The dashboard still feeds it through its hook
// (InverterFleet.Update(f)), at exactly the point it did. What the user sees is unchanged.

/// <summary>One authored asset class shown on the INVERTER FLEET tab (the WPF row: adds its two brushes).</summary>
public sealed class InverterFleetRow : InverterFleetRowModel
{
    public Brush ModeBrush => Palette.Frozen(ModeColour);
    public Brush TrippedBrush => Palette.Frozen(TrippedColour);
}

/// <summary>INVERTER FLEET tab: the neutral <see cref="InverterFleetModel"/> plus its WPF brushes and rows.</summary>
public sealed class InverterFleetVm : InverterFleetModel
{
    /// <summary>The trip total's brush: the frozen brush of <see cref="InverterFleetModel.TrippedColour"/>.</summary>
    public Brush TrippedBrush => Palette.Frozen(TrippedColour);

    /// <summary>Announce the neutral property, then the WPF brush bound in its place.</summary>
    protected override void OnPropertyChanged(string? name)
    {
        base.OnPropertyChanged(name);
        if (name == nameof(TrippedColour)) base.OnPropertyChanged(nameof(TrippedBrush));
    }

    protected override InverterFleetRowModel CreateRow(string className, string mode, string count, string onlineMw,
        string tripped, string soc, UiColour modeColour, UiColour trippedColour) =>
        new InverterFleetRow
        {
            ClassName = className, Mode = mode, Count = count, OnlineMw = onlineMw, Tripped = tripped, Soc = soc,
            ModeColour = modeColour, TrippedColour = trippedColour,
        };
}
