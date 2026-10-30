using System.Collections.ObjectModel;
using GridSim.Ui.Dashboard;
using GridSim.Ui.Presentation;

namespace GridSim.Ui.Tabs.Inspector;

/// <summary>
/// The drill-down inspector overlay of the NESO RP1 and MECHANICAL tabs (UI programme Phase 1.6): click an aggregate
/// (a fuel, a boundary, an interconnector, a machine) and it shows how that aggregate is made up, as a title, a row of
/// stat tiles and a list of constituent rows, for the frame currently shown. A row may drill further; a back-stack
/// keeps the trail, so a paused frame becomes fully explorable.
///
/// <para>This is the logic of the WPF <c>InspectorVm</c> (src/GridSim.Wpf/Views/InspectorVm.cs), moved here line for
/// line. The only changes are at the framework boundary:</para>
/// <list type="bullet">
/// <item>the WPF <c>Visibility</c> / <c>BackVisibility</c> are the bools <see cref="IsOpen"/> / <see cref="CanBack"/>
/// (true = visible); the WPF adapter maps them back under the old names;</item>
/// <item>the WPF <c>CloseCommand</c> / <c>BackCommand</c> are the methods <see cref="Close"/> / <see cref="Back"/>
/// (the WPF adapter wraps each in a command; GridSim.Ui names no user-interface command type);</item>
/// <item>a row's or tile's WPF brush is a neutral colour token, and a row's drill command is an <see cref="Action"/>
/// (<see cref="InspectRowModel.OnDrill"/>).</item>
/// </list>
/// <para>The tabs that open the inspector build its tiles and rows through <see cref="CreateStat"/> and
/// <see cref="CreateRow"/>, so a front end that overrides them (the WPF <c>InspectorVm</c>) puts its own row types, with
/// their brushes, in the lists.</para>
/// </summary>
public class InspectorModel : ObservableModel
{
    /// <summary>The combined / averaged headline tiles of the page shown.</summary>
    public ObservableCollection<InspectStatModel> Stats { get; } = new();

    /// <summary>The constituent rows of the page shown.</summary>
    public ObservableCollection<InspectRowModel> Rows { get; } = new();

    /// <summary>True while the overlay is open (was the WPF <c>Visibility</c>: true = Visible, false = Collapsed).</summary>
    private bool _isOpen;
    public bool IsOpen { get => _isOpen; private set => Set(ref _isOpen, value); }

    private string _title = ""; public string Title { get => _title; set => Set(ref _title, value); }
    private string _subtitle = ""; public string Subtitle { get => _subtitle; set => Set(ref _subtitle, value); }

    /// <summary>True while there is a page to go back to (was the WPF <c>BackVisibility</c>: true = Visible).</summary>
    private bool _canBack;
    public bool CanBack { get => _canBack; private set => Set(ref _canBack, value); }

    // The trail of pages behind the one shown: each is the page as it was when the next one was pushed over it.
    private readonly Stack<Snapshot> _back = new();
    private sealed record Snapshot(string Title, string Subtitle, IReadOnlyList<InspectStatModel> Stats, IReadOnlyList<InspectRowModel> Rows);

    /// <summary>Open (or push a new page onto) the inspector with a fresh title, stat tiles and rows.</summary>
    public void Show(string title, string subtitle, IEnumerable<InspectStatModel> stats, IEnumerable<InspectRowModel> rows)
    {
        // Already open: the page being covered goes on the back-stack first, so BACK returns to it.
        if (_isOpen)
            _back.Push(new Snapshot(Title, Subtitle, new List<InspectStatModel>(Stats), new List<InspectRowModel>(Rows)));
        CanBack = _back.Count > 0;
        LoadPage(title, subtitle, stats, rows);
        IsOpen = true;
    }

    private void LoadPage(string title, string subtitle, IEnumerable<InspectStatModel> stats, IEnumerable<InspectRowModel> rows)
    {
        Title = title; Subtitle = subtitle;
        Stats.Clear(); foreach (InspectStatModel s in stats) Stats.Add(s);
        Rows.Clear(); foreach (InspectRowModel r in rows) Rows.Add(r);
    }

    /// <summary>The "&lt; back" button: show the page before this one; with none left, close the overlay.</summary>
    public void Back()
    {
        if (_back.Count == 0) { Close(); return; }
        Snapshot s = _back.Pop();
        CanBack = _back.Count > 0;
        LoadPage(s.Title, s.Subtitle, s.Stats, s.Rows);
    }

    /// <summary>The "x" button: close the overlay and forget the trail.</summary>
    public void Close()
    {
        _back.Clear();
        CanBack = false;
        IsOpen = false;
    }

    // ---- tile and row factories: a front end overrides these to put its own types (with brushes) in the lists ----

    /// <summary>
    /// A new headline tile. <paramref name="valueColour"/> null keeps the tile's default value colour (white, the WPF
    /// <c>Brushes.White</c>), exactly as the WPF tabs left <c>ValueBrush</c> unset.
    /// </summary>
    public virtual InspectStatModel CreateStat(string label, string value, UiColour? valueColour = null) =>
        valueColour is UiColour c
            ? new InspectStatModel { Label = label, Value = value, ValueColour = c }
            : new InspectStatModel { Label = label, Value = value };

    /// <summary>A new constituent row (every colour given; the tabs pass their own defaults, as the WPF tabs did).</summary>
    public virtual InspectRowModel CreateRow(string primary, string value, UiColour swatchColour, UiColour valueColour,
        string secondary = "", string sub = "", Action? onDrill = null) =>
        new() { Primary = primary, Secondary = secondary, Value = value, Sub = sub, SwatchColour = swatchColour, ValueColour = valueColour, OnDrill = onDrill };
}

/// <summary>A headline combined / averaged figure shown as a tile at the top of an inspector page.</summary>
public class InspectStatModel : ObservableModel
{
    /// <summary>The WPF <c>Brushes.White</c>: #FFFFFFFF, the default value colour of a tile and a row.</summary>
    public static readonly UiColour White = new(0xFF, 0xFF, 0xFF, 0xFF);

    public string Label { get; init; } = "";
    public string Value { get; init; } = "";

    /// <summary>The value's colour (was the WPF <c>ValueBrush</c>, default <c>Brushes.White</c>).</summary>
    public UiColour ValueColour { get; init; } = White;
}

/// <summary>One constituent row in an inspector page; may carry an action to drill further.</summary>
public class InspectRowModel : ObservableModel
{
    public string Primary { get; init; } = "";
    public string Secondary { get; init; } = "";
    public string Value { get; init; } = "";
    public string Sub { get; init; } = "";

    /// <summary>The swatch's colour (was the WPF <c>Swatch</c> brush, default <c>Brushes.Transparent</c>).</summary>
    public UiColour SwatchColour { get; init; } = DashboardColours.Transparent;

    /// <summary>The value's colour (was the WPF <c>ValueBrush</c>, default <c>Brushes.White</c>).</summary>
    public UiColour ValueColour { get; init; } = InspectStatModel.White;

    /// <summary>What a click on the row does (was the WPF <c>Drill</c> command; the WPF row keeps a <c>Drill</c>
    /// command under its old name, which runs this); null = the row does not drill.</summary>
    public Action? OnDrill { get; init; }

    /// <summary>True when the row drills further: the chevron shows (was the WPF <c>Chevron</c>: Visible when
    /// <c>Drill</c> is set).</summary>
    public bool HasDrill => OnDrill is not null;

    /// <summary>Drill into this row (a click on it). A row that does not drill does nothing, as the WPF button with no
    /// command did.</summary>
    public void DrillIn() => OnDrill?.Invoke();
}
