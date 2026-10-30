using System.Windows.Media;
using GridSim.Ui.Tabs.Cycle;
using GridSim.Wpf.Visuals;

namespace GridSim.Wpf.Views;

// UI programme Phase 1.6 (first tab batch): the CYCLE tab's logic moved to the platform-neutral
// GridSim.Ui.Tabs.Cycle.CycleModel (src/GridSim.Ui/Tabs/Cycle/CycleModel.cs), line for line, with each WPF Brush
// replaced by a neutral colour token (UiColour) and the UI-thread marshalling passed in as a poster. This file is now
// the thin WPF side of that boundary: it passes the WPF dispatcher (the same BeginInvoke on the same application
// dispatcher as before), and the view-model and its job row add, under the OLD property names the XAML binds, a
// frozen brush for every token (Palette.Frozen: the same colour bytes). What the user sees is unchanged.

/// <summary>One job's outcome in the CYCLE tab's job table (the WPF row: adds <see cref="OutcomeBrush"/> and
/// <see cref="PriorityBrush"/>).</summary>
public sealed class CycleJobRow : CycleJobRowModel
{
    /// <summary>The WPF row for the neutral <paramref name="source"/>: every value copied.</summary>
    public CycleJobRow(CycleJobRowModel source) : base(source) { }

    public Brush OutcomeBrush => Palette.Frozen(OutcomeColour);
    public Brush PriorityBrush => Palette.Frozen(PriorityColour);
}

/// <summary>
/// The CYCLE tab (the WPF side of <see cref="CycleModel"/>). Because the sim ticks on the UI thread,
/// <see cref="CycleModel.Update"/> runs there; a completed cycle is raised on a worker thread and projected back on
/// the UI thread through the application's dispatcher, as before.
/// </summary>
public sealed class CycleVm : CycleModel
{
    /// <summary>A CYCLE tab whose completed cycles are projected on the WPF UI thread (nothing happens while no
    /// application is running, as before).</summary>
    public CycleVm() : base(apply => System.Windows.Application.Current?.Dispatcher.BeginInvoke(apply)) { }

    public Brush HeroBrush => Palette.Frozen(HeroColour);
    public Brush HighBrush => Palette.Frozen(HighColour);
    public Brush CoverageBrush => Palette.Frozen(CoverageColour);
    public Brush WorstBrush => Palette.Frozen(WorstColour);

    /// <summary>Each neutral property and the WPF adapter property the XAML binds in its place.</summary>
    private static readonly Dictionary<string, string> AdapterOf = new(StringComparer.Ordinal)
    {
        [nameof(HeroColour)] = nameof(HeroBrush),
        [nameof(HighColour)] = nameof(HighBrush),
        [nameof(CoverageColour)] = nameof(CoverageBrush),
        [nameof(WorstColour)] = nameof(WorstBrush),
    };

    /// <summary>Announce the neutral property, then (when it has one) the WPF adapter property bound in its place.</summary>
    protected override void OnPropertyChanged(string? name)
    {
        base.OnPropertyChanged(name);
        if (name is not null && AdapterOf.TryGetValue(name, out string? adapter)) base.OnPropertyChanged(adapter);
    }

    protected override CycleJobRowModel CreateJobRow(CycleJobRowModel row) => new CycleJobRow(row);
}
