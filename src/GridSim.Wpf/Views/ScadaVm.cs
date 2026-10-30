using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using GridSim.Ui.Presentation;
using GridSim.Ui.Tabs.Scada;
using GridSim.Wpf.Visuals;

namespace GridSim.Wpf.Views;

// UI programme Phase 1.6 (first tab batch): the SCADA tab's logic moved to the platform-neutral
// GridSim.Ui.Tabs.Scada.ScadaModel (src/GridSim.Ui/Tabs/Scada/ScadaModel.cs), line for line, with each WPF Brush
// replaced by a neutral colour token (UiColour), the filter's Visibility by a bool, and the three commands by plain
// methods (AckAll, Ack, OpenPoint). This file is now the thin WPF side of that boundary: the view-model and its rows
// derive from the neutral ones and add, under the OLD names the XAML binds, a frozen brush for every token
// (Palette.Frozen: the same colour bytes), a Visibility for the bool (true = Visible, false = Collapsed) and the WPF
// commands over the neutral methods. What the user sees and clicks is unchanged.

/// <summary>A command that takes its bound row as the parameter  -  used by the per-row ACK button.</summary>
public sealed class RowCommand(Action<object?> exec) : ICommand
{
    public event EventHandler? CanExecuteChanged { add { } remove { } }
    public bool CanExecute(object? p) => true;
    public void Execute(object? p) => exec(p);
}

/// <summary>One telemetry point in the SCADA register (the WPF row: adds <see cref="QualityBrush"/>,
/// <see cref="LoadBrush"/> and the filter's <see cref="Vis"/>).</summary>
public sealed class ScadaPoint : ScadaPointModel
{
    public Brush QualityBrush => Palette.Frozen(QualityColour);
    public Brush LoadBrush => Palette.Frozen(LoadColour);
    public Visibility Vis => Visible ? Visibility.Visible : Visibility.Collapsed;

    protected override void OnPropertyChanged(string? name)
    {
        base.OnPropertyChanged(name);
        if (name == nameof(QualityColour)) base.OnPropertyChanged(nameof(QualityBrush));
        else if (name == nameof(LoadColour)) base.OnPropertyChanged(nameof(LoadBrush));
        else if (name == nameof(Visible)) base.OnPropertyChanged(nameof(Vis));
    }
}

/// <summary>One row in the event/alarm log (the WPF row: adds <see cref="PriorityBrush"/>).</summary>
public sealed class ScadaEvent : ScadaEventModel
{
    /// <summary>The priority chip's brush: the frozen brush of <see cref="ScadaEventModel.PriorityColour"/> (fixed at creation).</summary>
    public Brush PriorityBrush => Palette.Frozen(PriorityColour);
}

/// <summary>
/// THE SCADA TAB  -  a simulated supervisory console over the live grid (the WPF side of <see cref="ScadaModel"/>:
/// adds the commands the XAML binds and makes the WPF rows).
/// </summary>
public sealed class ScadaVm : ScadaModel
{
    public ICommand AckAllCmd { get; }
    public ICommand AckCmd { get; }

    /// <summary>The register click-through (see <see cref="ScadaModel.Navigate"/>, which the host wires).</summary>
    public ICommand OpenPointCmd { get; }

    public ScadaVm()
    {
        AckAllCmd = new RowCommand(_ => AckAll());
        AckCmd = new RowCommand(o => { if (o is ScadaEventModel e) Ack(e); });
        OpenPointCmd = new RowCommand(o => { if (o is ScadaPointModel p) OpenPoint(p); });
    }

    protected override ScadaPointModel CreatePoint(string tag, string description, string units, PointKind kind, int index) =>
        new ScadaPoint { Tag = tag, Description = description, Units = units, Kind = kind, Index = index };

    protected override ScadaEventModel CreateEvent(string stamp, string priority, UiColour priorityColour, string source, string message) =>
        new ScadaEvent { Stamp = stamp, Priority = priority, PriorityColour = priorityColour, Source = source, Message = message };
}
