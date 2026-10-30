using GridSim.Core.IO;
using GridSim.Ui.Presentation;

namespace GridSim.Ui.Dashboard;

// The row view-models of the dashboard's lists, without a user-interface framework (UI programme Phase 0.2). Each was a
// WPF class in src/GridSim.Wpf/DashboardViewModel.cs that held a WPF Brush; here it holds a neutral colour token
// (UiColour) instead, with every other member, default and formatting unchanged. The classes are not sealed: a front
// end derives its own row from each one to add its brush property (the WPF rows add Bar, Brush, Color, BadgeBrush with
// the names the XAML already binds), and the dashboard creates its rows through overridable factory methods so the
// front end's own row type is what lands in the bound collections.

/// <summary>One transmission-boundary corridor row in the side panel.</summary>
public class BoundaryRowModel : ObservableModel
{
    public string Name { get; init; } = "";
    private string _flow = ""; public string Flow { get => _flow; set => Set(ref _flow, value); }
    private string _pct = ""; public string Pct { get => _pct; set => Set(ref _pct, value); }
    private double _barWidth; public double BarWidth { get => _barWidth; set => Set(ref _barWidth, value); }
    /// <summary>The bar's colour (was the WPF <c>Bar</c> brush, default <c>Brushes.Gray</c>).</summary>
    private UiColour _barColour = DashboardColours.Gray; public UiColour BarColour { get => _barColour; set => Set(ref _barColour, value); }
}

/// <summary>One voltage-band dial on the NETWORK telemetry column: live average pu for that kV
/// level, shown against a nominal-centred dial (1.0 pu in the middle).</summary>
public class VoltageBandDialModel : ObservableModel
{
    /// <summary>The level's exact base voltage: the dial's identity (two levels may round to the same label).</summary>
    public double BaseKv { get; init; }
    public string Label { get; init; } = "";                                                    // "400 kV", "0.415 kV"
    private double _vpu = 1.0; public double Vpu { get => _vpu; set => Set(ref _vpu, value); }   // dial value (pu)
    private string _dev = " - "; public string Dev { get => _dev; set => Set(ref _dev, value); }   // "+0.4%"
    private string _kv = " - "; public string Kv { get => _kv; set => Set(ref _kv, value); }       // "417 kV" live
    /// <summary>The dial's colour (was the WPF <c>Brush</c>, default <c>Brushes.Gray</c>).</summary>
    private UiColour _colour = DashboardColours.Gray; public UiColour Colour { get => _colour; set => Set(ref _colour, value); }
}

/// <summary>One interconnector's row in the import-links panel: signed flow against capacity.</summary>
public class ImportLinkRowModel : ObservableModel
{
    /// <summary>The link's index in the model's generator list: the row's identity (link names repeat).</summary>
    public int Index { get; init; }
    public string Name { get; init; } = "";
    public string Route { get; init; } = "";
    public string Capacity { get; init; } = "";
    private string _flow = ""; public string Flow { get => _flow; set => Set(ref _flow, value); }
    private string _badge = ""; public string Badge { get => _badge; set => Set(ref _badge, value); }
    private double _barWidth; public double BarWidth { get => _barWidth; set => Set(ref _barWidth, value); }
    private double _barOffset; public double BarOffset { get => _barOffset; set => Set(ref _barOffset, value); }   // px from track left to bar start (centred diverging bar)
    /// <summary>The bar's colour (was the WPF <c>Bar</c> brush, default <c>Brushes.Gray</c>).</summary>
    private UiColour _barColour = DashboardColours.Gray; public UiColour BarColour { get => _barColour; set => Set(ref _barColour, value); }
    /// <summary>The badge's colour (was the WPF <c>BadgeBrush</c>, default <c>Brushes.Gray</c>).</summary>
    private UiColour _badgeColour = DashboardColours.Gray; public UiColour BadgeColour { get => _badgeColour; set => Set(ref _badgeColour, value); }
}

/// <summary>One fuel's contribution in the generation-mix panel  -  its MW share and the inertia
/// it spins (so the gen-mix and inertia-by-class boxes share one ordered list).</summary>
public class FuelRowModel : ObservableModel
{
    public string Name { get; init; } = "";
    /// <summary>The fuel's colour from the shared taxonomy (was the WPF <c>Color</c> brush, default <c>Brushes.Gray</c>).</summary>
    public UiColour Colour { get; init; } = DashboardColours.Gray;
    private string _mw = ""; public string Mw { get => _mw; set => Set(ref _mw, value); }
    private string _pct = ""; public string Pct { get => _pct; set => Set(ref _pct, value); }
    private double _barWidth; public double BarWidth { get => _barWidth; set => Set(ref _barWidth, value); }
    private string _inertiaGva = " - "; public string InertiaGva { get => _inertiaGva; set => Set(ref _inertiaGva, value); }
    private double _inertiaBar; public double InertiaBar { get => _inertiaBar; set => Set(ref _inertiaBar, value); }
}

/// <summary>A label/value pair for the detail fly-out.</summary>
public sealed record KeyVal(string Key, string Value);

/// <summary>One row in the alarms panel (was the WPF <c>AlarmRow(Time, Text, Brush)</c>).</summary>
/// <param name="Time">The dashboard clock when the alarm was raised.</param>
/// <param name="Text">The alarm text.</param>
/// <param name="Colour">The alarm's colour token.</param>
public record AlarmEntry(string Time, string Text, UiColour Colour);

/// <summary>One constraint violation from an on-demand GDA-bridge query, projected for the results panel.</summary>
public class ViolationEntry
{
    public string Kind { get; }
    public string Element { get; }
    public string Detail { get; }
    public string Message { get; }
    /// <summary>The row's colour: always the loading ramp's red (was the WPF <c>Brush</c>).</summary>
    public UiColour Colour { get; }

    /// <summary>Project one violation: the old WPF <c>ViolationRow.From</c>, field for field and format for format.</summary>
    public ViolationEntry(Violation v)
    {
        string detail =
            v.Value is double val && v.Limit is double lim ? $"{val:0.###} / {lim:0.###}"
            : v.Value is double only ? $"{only:0.###}"
            : v.Limit is double lo ? $"limit {lo:0.###}"
            : "";
        Kind = v.Kind ?? "";
        Element = string.IsNullOrWhiteSpace(v.Element) ? (v.Kind ?? "violation") : v.Element!;
        Detail = detail;
        Message = v.Message ?? "";
        Colour = ColourRamps.Loading(1.1);
    }
}
