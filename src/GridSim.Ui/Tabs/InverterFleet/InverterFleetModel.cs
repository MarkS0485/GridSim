using System.Collections.ObjectModel;
using GridSim.Core.Simulation;
using GridSim.Ui.Dashboard;
using GridSim.Ui.Presentation;

namespace GridSim.Ui.Tabs.InverterFleet;

// UI programme Phase 1.6 (second tab batch): the INVERTER FLEET tab's logic, moved line for line from the WPF
// src/GridSim.Wpf/Views/InverterFleetVm.cs so the Avalonia app shows the same tab from the same code. Every displayed
// string is produced by the same formatting call, in the same culture, in the same order as before. The only changes
// are at the framework boundary: each WPF Brush is a neutral colour token (UiColour, the same four bytes; for example
// TrippedBrush is now TrippedColour), and the rows are made through an overridable factory so the WPF adapter (which
// derives from this class and keeps the old brush property names) puts its own rows, with their brushes, in the list.
// UiTabsBatch2ParityTests compares every display property, frame for frame, with a verbatim copy of the old code.

/// <summary>One authored asset class shown on the INVERTER FLEET tab.</summary>
public class InverterFleetRowModel : ObservableModel
{
    public string ClassName { get; init; } = "";
    public string Mode { get; init; } = "";
    public string Count { get; init; } = "";
    public string OnlineMw { get; init; } = "";
    public string Tripped { get; init; } = "";
    public string Soc { get; init; } = "";
    /// <summary>The mode's colour (was the WPF <c>ModeBrush</c>, default <c>Brushes.Gray</c>).</summary>
    public UiColour ModeColour { get; init; } = DashboardColours.Gray;
    /// <summary>The trip count's colour (was the WPF <c>TrippedBrush</c>, default <c>Brushes.Gray</c>).</summary>
    public UiColour TrippedColour { get; init; } = DashboardColours.Gray;
}

/// <summary>
/// INVERTER FLEET tab - the authored per-asset dynamics view. Shows the fleet's net response, per-class
/// online MW / SoC / trips, and the Loss-of-Mains deficit when embedded generation trips. What the
/// synchronous fleet cannot account for: sample->compute->actuate inverter behaviour. Every control
/// parameter is literature-approximated [VERIFY]; the response is UNVALIDATED AT SCALE. Populated only
/// when the session runs asset dynamics (a case that ships asset-classes.json); otherwise shows OFF.
/// <para>Fed by <see cref="Update"/> once per solved frame (WPF: from the dashboard's tab hook); it holds no
/// time-integrated state, so a timeline break needs no reset (WPF never reset it).</para>
/// </summary>
public class InverterFleetModel : ObservableModel
{
    /// <summary>The tab's fixed colours (the WPF Following / Forming / Alarm / Muted brushes, byte for byte, opaque).</summary>
    public static readonly UiColour Following = new(0xFF, 0x3F, 0xA9, 0xF5);
    public static readonly UiColour Forming = new(0xFF, 0x2F, 0xD2, 0x7F);
    public static readonly UiColour Alarm = new(0xFF, 0xF5, 0x61, 0x5C);
    public static readonly UiColour Muted = new(0xFF, 0x6A, 0x76, 0x83);

    public ObservableCollection<InverterFleetRowModel> Classes { get; } = new();

    private bool _active;
    public bool Active { get => _active; set => Set(ref _active, value); }

    private string _status = "Inverter-fleet dynamics are OFF for this session. Load a case with an authored asset-class library to model the inverter fleet.";
    public string Status { get => _status; set => Set(ref _status, value); }

    private string _netFleetMw = " - "; public string NetFleetMw { get => _netFleetMw; set => Set(ref _netFleetMw, value); }
    private string _classCount = "0"; public string ClassCount { get => _classCount; set => Set(ref _classCount, value); }
    private string _totalUnits = "0"; public string TotalUnits { get => _totalUnits; set => Set(ref _totalUnits, value); }
    private string _totalTripped = "0"; public string TotalTripped { get => _totalTripped; set => Set(ref _totalTripped, value); }
    /// <summary>The trip total's colour (was the WPF <c>TrippedBrush</c>, default the muted slate).</summary>
    private UiColour _trippedColour = Muted; public UiColour TrippedColour { get => _trippedColour; set => Set(ref _trippedColour, value); }

    private string _lomWarning = ""; public string LomWarning { get => _lomWarning; set => Set(ref _lomWarning, value); }

    public string Provenance { get; } =
        "Every control parameter (droop/df-dt gains, detection/actuate timings, relay settings) is vendor-proprietary and literature-approximated [VERIFY]; the fleet response is UNVALIDATED AT SCALE. Capacity & location are grounded; the curve shape is assumption-driven.";

    /// <summary>Show one solved frame's asset-fleet roll-up (or, without one, switch the tab off once).</summary>
    public void Update(SimFrame f)
    {
        ArgumentNullException.ThrowIfNull(f);
        if (f.AssetFleet is not { Count: > 0 } rollups)
        {
            if (Active) { Active = false; Classes.Clear(); }
            return;
        }

        Active = true;
        Status = "Authored sample->compute->actuate inverter dynamics on the live network.";
        NetFleetMw = f.AssetFleetNetMw.ToString("+0;-0;0") + " MW";

        int units = 0, tripped = 0;
        Classes.Clear();
        foreach (var r in rollups)
        {
            units += r.Count;
            tripped += r.TrippedCount;
            Classes.Add(CreateRow(
                r.ClassName,
                r.Mode,
                r.Count.ToString(),
                r.OnlineMw.ToString("+0.0;-0.0;0"),
                r.TrippedCount.ToString(),
                r.SocPercent > 0 ? r.SocPercent.ToString("0") + "%" : "-",
                r.Mode == "grid-forming" ? Forming : Following,
                r.TrippedCount > 0 ? Alarm : Muted));
        }

        ClassCount = rollups.Count.ToString();
        TotalUnits = units.ToString();
        TotalTripped = tripped.ToString();
        TrippedColour = tripped > 0 ? Alarm : Muted;
        LomWarning = f.AssetTrippedMw > 0
            ? $"! {f.AssetTrippedMw:0} MW of embedded generation has tripped on RoCoF/vector-shift protection and been added to the deficit - the 2019-08-09 feedback loop."
            : "";
    }

    /// <summary>A new per-class row; a front end overrides this to put its own row type (with brushes) in the list.</summary>
    protected virtual InverterFleetRowModel CreateRow(string className, string mode, string count, string onlineMw,
        string tripped, string soc, UiColour modeColour, UiColour trippedColour) =>
        new()
        {
            ClassName = className, Mode = mode, Count = count, OnlineMw = onlineMw, Tripped = tripped, Soc = soc,
            ModeColour = modeColour, TrippedColour = trippedColour,
        };
}
