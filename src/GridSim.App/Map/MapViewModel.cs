using Avalonia;
using Avalonia.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using GridSim.App.Run;
using GridSim.Core.Model;
using GridSim.Core.PowerFlow;
using GridSim.Core.Simulation;
using GridSim.Ui.Dashboard;
using GridSim.Ui.Presentation;
using GridSim.Ui.Schematic;

namespace GridSim.App.Map;

/// <summary>What is selected on the map: a bus or a branch.</summary>
public enum MapSelectionKind
{
    /// <summary>A bus (a roundel).</summary>
    Bus,

    /// <summary>A branch (a corridor or a transformer tie).</summary>
    Branch,
}

/// <summary>The map's selection: a bus by its Id, or a branch by its index in the model's Branches.</summary>
/// <param name="Kind">Bus or branch.</param>
/// <param name="Id">The bus Id (for a bus) or the branch index (for a branch).</param>
public sealed record MapSelection(MapSelectionKind Kind, int Id);

/// <summary>
/// What the map shows besides the drawing (UI programme Phase 1.5): the selection and its detail fly-out, the hover
/// tip, the colour-mode legend and the hint line. The texts are the WPF window's (its <c>OnNodeClicked</c>,
/// <c>OnBranchClicked</c>, <c>Schematic_MouseMove</c> and the dashboard's legend and hint), produced by the same
/// format strings from the same frame; the fly-out, the legend and the hint live on the shared
/// <see cref="DashboardModel"/> exactly as the WPF dashboard holds them, so the Phase 1.4 tiles find them there.
/// </summary>
public sealed class MapViewModel : ObservableObject
{
    private readonly RunViewModel _run;
    private MapSelection? _selection;
    private string _hoverText = "";
    private bool _hoverVisible;
    private IBrush _legendBrush = LegendRamp(MapColourMode.Loading);

    /// <summary>The map's view model over <paramref name="run"/>, writing the fly-out, legend and hint into <paramref name="dashboard"/>.</summary>
    public MapViewModel(RunViewModel run, DashboardModel dashboard)
    {
        _run = run ?? throw new ArgumentNullException(nameof(run));
        Dashboard = dashboard ?? throw new ArgumentNullException(nameof(dashboard));
    }

    /// <summary>The shared dashboard model: the fly-out (SelKind, SelName ...), the legend and the map hint.</summary>
    public DashboardModel Dashboard { get; }

    /// <summary>The selected bus or branch, or null when nothing is selected.</summary>
    public MapSelection? Selection
    {
        get => _selection;
        private set => SetProperty(ref _selection, value);
    }

    /// <summary>The hover tip's text (WPF HoverText).</summary>
    public string HoverText
    {
        get => _hoverText;
        private set => SetProperty(ref _hoverText, value);
    }

    /// <summary>True while the hover tip is shown (WPF HoverTip.Visibility).</summary>
    public bool HoverVisible
    {
        get => _hoverVisible;
        private set => SetProperty(ref _hoverVisible, value);
    }

    /// <summary>The legend swatch: the WPF dashboard's ramp for the current colour mode.</summary>
    public IBrush LegendBrush
    {
        get => _legendBrush;
        private set => SetProperty(ref _legendBrush, value);
    }

    /// <summary>A new case or run has loaded (WPF SwitchTo / LoadReplay): clear the selection and set the hint for the feed.</summary>
    public void ModelChanged(bool replay)
    {
        ClearSelection();
        HideHover();
        Dashboard.MapHint = replay ? DashboardModel.MapHintReplay : DashboardModel.MapHintLocal;
    }

    /// <summary>Point the legend at <paramref name="mode"/> (the WPF <c>_vm.SetColorLegend</c>).</summary>
    public void SetColourLegend(MapColourMode mode)
    {
        Dashboard.SetColorLegend(mode);
        LegendBrush = LegendRamp(Dashboard.Legend);
    }

    /// <summary>
    /// A roundel was clicked (the WPF window's <c>OnNodeClicked</c>): select the bus and fill the fly-out. Does nothing
    /// before the first accepted frame or for an Id that is not a bus of the model, as in WPF.
    /// </summary>
    public void SelectBus(int busId)
    {
        if (_run.LastFrame is not SimFrame last) return;
        GridModel m = last.Model;
        PowerFlowResult r = last.Result;
        Bus? bus = m.Buses.FirstOrDefault(b => b.Id == busId);
        if (bus is null) return;
        Selection = new MapSelection(MapSelectionKind.Bus, busId);

        double vm = 1.0;
        for (int i = 0; i < r.BusIds.Count; i++) if (r.BusIds[i] == busId) { vm = r.Vm[i]; break; }
        double gen = m.Generators.Where(g => g.Bus == busId).Sum(g => g.Pg);

        var rows = new List<KeyVal>
        {
            new("Voltage", $"{vm:F3} p.u."),
            new("Demand", $"{bus.Pd:F0} MW"),
            new("Generation", $"{gen:F0} MW"),
        };

        var lines = new List<string>();
        for (int i = 0; i < m.Branches.Count; i++)
        {
            Branch br = m.Branches[i];
            if (br.From != busId && br.To != busId) continue;
            string load = i < r.LineFlows.Count && br.RateAMva > 0 ? $"  {r.LineFlows[i].LoadingPercent:F0}%" : "";
            lines.Add($"{br.Name}{load}");
        }

        Dashboard.Select(bus.BaseKv >= 200 ? "EHV SUBSTATION" : "SUBSTATION",
            bus.Name ?? $"Bus {busId}", $"{bus.BaseKv:F0} kV  {bus.Type}",
            rows, $"CONNECTIONS ({lines.Count})", string.Join("\n", lines.Take(12)));
    }

    /// <summary>
    /// A corridor was clicked (the WPF window's <c>OnBranchClicked</c>): select the branch and fill the fly-out. Does
    /// nothing before the first accepted frame or for an index outside the model's branches, as in WPF.
    /// </summary>
    public void SelectBranch(int index)
    {
        if (_run.LastFrame is not SimFrame last || index < 0 || index >= last.Model.Branches.Count) return;
        GridModel m = last.Model;
        PowerFlowResult r = last.Result;
        Branch br = m.Branches[index];
        Selection = new MapSelection(MapSelectionKind.Branch, index);
        Bus? a = m.Buses.FirstOrDefault(b => b.Id == br.From);
        Bus? c = m.Buses.FirstOrDefault(b => b.Id == br.To);

        string flow = " - ", load = " - ";
        if (index < r.LineFlows.Count)
        {
            LineFlow lf = r.LineFlows[index];
            flow = $"{Math.Max(Math.Abs(lf.PFromMw), Math.Abs(lf.PToMw)):F0} MW";
            load = br.RateAMva > 0 ? $"{lf.LoadingPercent:F0}%" : " - ";
        }

        var rows = new List<KeyVal>
        {
            new("Type", br.IsTransformer ? "Transformer" : "Circuit"),
            new("Rating", br.RateAMva > 0 ? $"{br.RateAMva:F0} MVA" : " - "),
            new("Flow", flow),
            new("Loading", load),
        };
        Dashboard.Select("CORRIDOR", br.Name ?? $"{br.From}-{br.To}", $"{a?.Name} -> {c?.Name}", rows, "", "");
    }

    /// <summary>Drop the selection and close the fly-out (the WPF Escape key and close button).</summary>
    public void ClearSelection()
    {
        Selection = null;
        Dashboard.ClearSelection();
    }

    /// <summary>
    /// The pointer is over the map at no bus or at <paramref name="busId"/> (the WPF <c>Schematic_MouseMove</c>): show
    /// the bus's name, kV, voltage and demand, or hide the tip. Hidden before the first accepted frame and while a
    /// button is held (a pan), as in WPF.
    /// </summary>
    public void Hover(int? busId, bool buttonDown)
    {
        if (_run.LastFrame is not SimFrame last || buttonDown || busId is null) { HideHover(); return; }
        Bus? bus = last.Model.Buses.FirstOrDefault(b => b.Id == busId);
        if (bus is null) { HideHover(); return; }

        double vm = 1.0;
        for (int i = 0; i < last.Result.BusIds.Count; i++) if (last.Result.BusIds[i] == bus.Id) { vm = last.Result.Vm[i]; break; }
        HoverText = $"{bus.Name}\n{bus.BaseKv:F0} kV  {vm:F3} pu  {bus.Pd:F0} MW";
        HoverVisible = true;
    }

    /// <summary>
    /// UI programme Phase 4.2: the hover tip in AUTHOR mode, where the map is the case being edited and has no frame (plan
    /// Q5: no live value). <paramref name="text"/> is what the case says about the bus under the pointer (its record,
    /// name and base kV, from the Author map), or null to hide the tip.
    /// </summary>
    public void ShowAuthorTip(string? text)
    {
        if (text is null) { HideHover(); return; }
        HoverText = text;
        HoverVisible = true;
    }

    /// <summary>Hide the hover tip (the pointer left the map).</summary>
    public void HideHover() => HoverVisible = false;

    /// <summary>
    /// The legend swatch for <paramref name="mode"/>: a left-to-right gradient through the shared stops
    /// (<see cref="DashboardModel.LegendRampStops"/>), as the WPF dashboard's BuildRamp makes it.
    /// </summary>
    public static IBrush LegendRamp(MapColourMode mode)
    {
        var stops = new GradientStops();
        foreach ((UiColour colour, double offset) in DashboardModel.LegendRampStops(mode))
            stops.Add(new GradientStop(MapPaint.ToColor(colour), offset));
        return new LinearGradientBrush
        {
            GradientStops = stops,
            StartPoint = new RelativePoint(0, 0, RelativeUnit.Relative),
            EndPoint = new RelativePoint(1, 0, RelativeUnit.Relative),
        };
    }
}
