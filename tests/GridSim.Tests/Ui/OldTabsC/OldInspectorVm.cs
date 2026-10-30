// Test oracle for UiTabsThirdBatchParityTests (UI programme Phase 1.6, third tab batch). NOT production code.
//
// Below is the WPF InspectorVm exactly as it was before its logic moved to src/GridSim.Ui (src/GridSim.Wpf/Views/InspectorVm.cs at 2.0.28), copied
// mechanically from git. The only edits are:
//   - the lines "using System.Windows;", "using System.Windows.Media;" and "using GridSim.Wpf.Views.Assets;   // RelayCommand"
//     removed (the shims in OldTabsCShims.cs stand in) (3x);
//   - the namespace GridSim.Wpf.Views became GridSim.Tests.Ui.OldTabsC (1x).
// "using System.Windows.Input;" stays: ICommand is the base class library one. The RP1 drill-down pages are built
// into it, so it is the oracle for every inspector page the NESO RP1 tab shows.

using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Windows.Input;

namespace GridSim.Tests.Ui.OldTabsC;

/// <summary>
/// A reusable drill-down overlay for the RP1 / MECHANICAL tabs: click an aggregate (a fuel, a
/// boundary, an interconnector, a machine) and this shows how it is made up - the constituent items,
/// plus combined totals and averages - for the currently-displayed frame. Rows can themselves drill
/// further (a back-stack keeps the trail), so a paused frame becomes fully explorable.
/// </summary>
public sealed class InspectorVm : Notify
{
    public ObservableCollection<InspectStat> Stats { get; } = new();
    public ObservableCollection<InspectRow> Rows { get; } = new();

    private bool _isOpen;
    public bool IsOpen { get => _isOpen; private set { Set(ref _isOpen, value); Raise(nameof(Visibility)); } }
    public Visibility Visibility => _isOpen ? Visibility.Visible : Visibility.Collapsed;

    private string _title = ""; public string Title { get => _title; set => Set(ref _title, value); }
    private string _subtitle = ""; public string Subtitle { get => _subtitle; set => Set(ref _subtitle, value); }

    private bool _canBack; public bool CanBack { get => _canBack; private set { Set(ref _canBack, value); Raise(nameof(BackVisibility)); } }
    public Visibility BackVisibility => _canBack ? Visibility.Visible : Visibility.Collapsed;

    private readonly Stack<Snapshot> _back = new();
    private sealed record Snapshot(string Title, string Subtitle, IReadOnlyList<InspectStat> Stats, IReadOnlyList<InspectRow> Rows);

    public ICommand CloseCommand { get; }
    public ICommand BackCommand { get; }

    public InspectorVm()
    {
        CloseCommand = new RelayCommand(Close);
        BackCommand = new RelayCommand(Back);
    }

    /// <summary>Open (or push a new page onto) the inspector with a fresh title, stat tiles and rows.</summary>
    public void Show(string title, string subtitle, IEnumerable<InspectStat> stats, IEnumerable<InspectRow> rows)
    {
        if (_isOpen)
            _back.Push(new Snapshot(Title, Subtitle, new List<InspectStat>(Stats), new List<InspectRow>(Rows)));
        CanBack = _back.Count > 0;
        LoadPage(title, subtitle, stats, rows);
        IsOpen = true;
    }

    private void LoadPage(string title, string subtitle, IEnumerable<InspectStat> stats, IEnumerable<InspectRow> rows)
    {
        Title = title; Subtitle = subtitle;
        Stats.Clear(); foreach (InspectStat s in stats) Stats.Add(s);
        Rows.Clear(); foreach (InspectRow r in rows) Rows.Add(r);
    }

    private void Back()
    {
        if (_back.Count == 0) { Close(); return; }
        Snapshot s = _back.Pop();
        CanBack = _back.Count > 0;
        LoadPage(s.Title, s.Subtitle, s.Stats, s.Rows);
    }

    public void Close()
    {
        _back.Clear();
        CanBack = false;
        IsOpen = false;
    }
}

/// <summary>A headline combined/averaged figure shown as a tile at the top of an inspector page.</summary>
public sealed class InspectStat : Notify
{
    public string Label { get; init; } = "";
    public string Value { get; init; } = "";
    public Brush ValueBrush { get; init; } = Brushes.White;
}

/// <summary>One constituent row in an inspector page; may carry a command to drill further.</summary>
public sealed class InspectRow : Notify
{
    public string Primary { get; init; } = "";
    public string Secondary { get; init; } = "";
    public string Value { get; init; } = "";
    public string Sub { get; init; } = "";
    public Brush Swatch { get; init; } = Brushes.Transparent;
    public Brush ValueBrush { get; init; } = Brushes.White;
    public ICommand? Drill { get; init; }
    public Visibility Chevron => Drill is null ? Visibility.Collapsed : Visibility.Visible;
}
