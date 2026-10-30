using System.Windows.Media;
using GridSim.Ui.Tabs.Domains;
using GridSim.Wpf.Visuals;

namespace GridSim.Wpf.Views;

// UI programme Phase 1.6 (first tab batch): the SPEED DOMAINS tab's logic moved to the platform-neutral
// GridSim.Ui.Tabs.Domains.DomainsModel (src/GridSim.Ui/Tabs/Domains/DomainsModel.cs), line for line, with each WPF
// Brush replaced by a neutral colour token (UiColour). This file is now the thin WPF side of that boundary: the
// view-model and its row derive from the neutral ones and add, under the OLD property names the XAML binds, a frozen
// brush for every token (the same colour bytes). What the user sees is unchanged.

/// <summary>One speed domain shown on the SPEED DOMAINS tab (the WPF row: adds the <see cref="Accent"/> brush).</summary>
public sealed class SpeedDomainRow : SpeedDomainRowModel
{
    /// <summary>The WPF row for the neutral <paramref name="source"/>: every value copied.</summary>
    public SpeedDomainRow(SpeedDomainRowModel source) : base(source) { }

    /// <summary>The accent brush: the frozen brush of <see cref="SpeedDomainRowModel.AccentColour"/> (fixed at creation).</summary>
    public Brush Accent => Palette.Frozen(AccentColour);
}

/// <summary>
/// SPEED DOMAINS tab - the experimental distributed-inertia view (the WPF side of <see cref="DomainsModel"/>: adds the
/// brushes the XAML binds).
/// </summary>
public sealed class DomainsVm : DomainsModel
{
    /// <summary>The dominance read-out's brush: the frozen brush of <see cref="DomainsModel.DominantColour"/>.</summary>
    public Brush DominantBrush => Palette.Frozen(DominantColour);

    /// <summary>Announce the neutral property, then the WPF brush bound in its place.</summary>
    protected override void OnPropertyChanged(string? name)
    {
        base.OnPropertyChanged(name);
        if (name == nameof(DominantColour)) base.OnPropertyChanged(nameof(DominantBrush));
    }

    protected override SpeedDomainRowModel CreateRow(SpeedDomainRowModel row) => new SpeedDomainRow(row);
}
