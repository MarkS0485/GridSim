using System.Windows.Media;
using GridSim.Ui.Tabs.Experimental;
using GridSim.Wpf.Visuals;

namespace GridSim.Wpf.Views;

// UI programme Phase 1.6 (third tab batch): the EXPERIMENTAL tab's logic moved to the platform-neutral
// GridSim.Ui.Tabs.Experimental.ExperimentalModel (src/GridSim.Ui/Tabs/Experimental/ExperimentalModel.cs), line for line,
// with each WPF Brush replaced by a neutral colour token (UiColour) and the way back to the UI thread passed in as a
// poster (as the CYCLE port did). This file is now the thin WPF side of that boundary: it passes the poster over the
// SAME mechanism as before (the calling thread's SynchronizationContext, through
// TaskScheduler.FromCurrentSynchronizationContext(), and none at all when the thread has no context, so the frame is
// then computed inline as it was), and the view-model and its law row add, under the OLD property names the XAML
// binds, a frozen brush for every token (Palette.Frozen: the same colour bytes). The models still run on a worker
// (R020, WpfExperimentalOffUiThreadTests). What the user sees is unchanged.

/// <summary>One physical-law row on the EXPERIMENTAL tab's conformance table (the WPF row: adds
/// <see cref="StatusBrush"/>).</summary>
public sealed class LawRow : LawRowModel
{
    /// <summary>The WPF row for the neutral <paramref name="source"/>: every value copied.</summary>
    public LawRow(LawRowModel source) : base(source) { }

    /// <summary>The status text's brush: the frozen brush of <see cref="LawRowModel.StatusColour"/> (fixed at creation).</summary>
    public Brush StatusBrush => Palette.Frozen(StatusColour);
}

/// <summary>
/// EXPERIMENTAL tab - physical-law conformance and the opt-in experimental models (the WPF side of
/// <see cref="ExperimentalModel"/>: adds the brushes the XAML binds and the WPF UI-thread poster).
/// </summary>
public sealed class ExperimentalVm : ExperimentalModel
{
    /// <summary>An EXPERIMENTAL tab whose results are shown back on the calling thread's synchronisation context.</summary>
    public ExperimentalVm() : base(CurrentUiPoster) { }

    /// <summary>
    /// The poster for the calling (UI) thread: an action is run on the thread's SynchronizationContext through
    /// TaskScheduler.FromCurrentSynchronizationContext(), exactly where the old continuation ran. Null when the thread
    /// has no context (a test host, a console caller), so the frame is computed and shown inline, as before.
    /// </summary>
    private static Action<Action>? CurrentUiPoster()
    {
        if (SynchronizationContext.Current is null) return null;
        TaskScheduler ui = TaskScheduler.FromCurrentSynchronizationContext();
        return apply => Task.Factory.StartNew(apply, CancellationToken.None, TaskCreationOptions.None, ui);
    }

    /// <summary>The gate verdict's brush: the frozen brush of <see cref="ExperimentalModel.GateColour"/>.</summary>
    public Brush GateBrush => Palette.Frozen(GateColour);

    /// <summary>Announce the neutral property, then the WPF brush bound in its place.</summary>
    protected override void OnPropertyChanged(string? name)
    {
        base.OnPropertyChanged(name);
        if (name == nameof(GateColour)) base.OnPropertyChanged(nameof(GateBrush));
    }

    protected override LawRowModel CreateLawRow(LawRowModel row) => new LawRow(row);
}
