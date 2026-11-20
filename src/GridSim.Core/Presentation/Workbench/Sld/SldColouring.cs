using GridSim.Core.Analysis.StudyCases;
using GridSim.Core.PowerFlow;

namespace GridSim.Core.Presentation.Workbench.Sld;

/// <summary>How the diagram is coloured (R4-18): not at all, by voltage level, by loading, by area (an R4-01 group of kind Area) or by bus voltage.</summary>
public enum SldColourMode { Plain, VoltageLevel, Loading, Area, Voltage }

/// <summary>
/// What a colour mode needs besides the model: a power-flow result (bus voltages by node, branch loading by element) and the area of
/// each node. A missing value is NaN (voltages, loading) or -1 (area): it colours grey, it is never taken as zero.
/// </summary>
public sealed class SldColourInput
{
    /// <summary>Voltage magnitude (pu) by node index, or null before a study has run.</summary>
    public double[]? NodeVm { get; init; }

    /// <summary>Loading (percent of the continuous rating) by element index of <see cref="SldModel.Elements"/>; NaN where unrated or not a branch.</summary>
    public double[]? ElementLoadingPercent { get; init; }

    /// <summary>The area (an index into <see cref="AreaNames"/>) of each node, -1 for none.</summary>
    public int[]? NodeArea { get; init; }

    public IReadOnlyList<string> AreaNames { get; init; } = [];

    /// <summary>The input a converged power flow gives (a non-converged result gives no voltages and no loading).</summary>
    public static SldColourInput FromPowerFlow(SldModel model, PowerFlowResult r, SldColourInput? withAreas = null)
    {
        ArgumentNullException.ThrowIfNull(model);
        ArgumentNullException.ThrowIfNull(r);
        double[]? vm = null, loading = null;
        if (r.Converged && model.Grid is not null)
        {
            vm = new double[model.Nodes.Count];
            Array.Fill(vm, double.NaN);
            var byId = new Dictionary<int, int>();
            for (int i = 0; i < r.BusIds.Count; i++) byId[r.BusIds[i]] = i;
            for (int i = 0; i < model.Nodes.Count; i++)
                if (byId.TryGetValue(model.Nodes[i].Key, out int bi)) vm[i] = r.Vm[bi];
            loading = new double[model.Elements.Count];
            Array.Fill(loading, double.NaN);
            for (int e = 0; e < model.Elements.Count; e++)
            {
                SldElement el = model.Elements[e];
                if (el.Kind is SldKind.Line or SldKind.Transformer && el.Source < r.LineFlows.Count && r.LineFlows[el.Source].RatingMva > 0)
                    loading[e] = r.LineFlows[el.Source].LoadingPercent;
            }
        }
        return new SldColourInput { NodeVm = vm, ElementLoadingPercent = loading, NodeArea = withAreas?.NodeArea, AreaNames = withAreas?.AreaNames ?? [] };
    }

    /// <summary>The areas of a groups document bound to the model: each node takes the first group of kind Area that lists its bus.</summary>
    public static SldColourInput FromGroups(SldModel model, BoundGroups groups)
    {
        ArgumentNullException.ThrowIfNull(model);
        ArgumentNullException.ThrowIfNull(groups);
        var areas = groups.Groups.Where(g => g.Group.Kind == GroupKind.Area).ToList();
        var area = new int[model.Nodes.Count];
        Array.Fill(area, -1);
        for (int i = 0; i < model.Nodes.Count; i++)
            for (int a = 0; a < areas.Count; a++)
                if (areas[a].BusIds.Contains(model.Nodes[i].Key)) { area[i] = a; break; }
        return new SldColourInput { NodeArea = area, AreaNames = areas.Select(a => a.Id).ToList() };
    }
}

/// <summary>The colours of one diagram, aligned with <see cref="SldGeometry.Symbols"/> and <see cref="SldGeometry.Connections"/>, and a legend.</summary>
public sealed record SldColourSet(SldColourMode Mode, IReadOnlyList<string> Symbols, IReadOnlyList<string> Connections, IReadOnlyList<(string Label, string Colour)> Legend);

/// <summary>
/// Colouring of the diagram by voltage level, loading, area or result (R4-18). Colours are literal hex strings from the Okabe-Ito set
/// (kept apart for the common colour-vision types); a value the model or study does not give is grey, never a colour for zero, and an
/// open or out-of-service element is grey in every mode.
/// </summary>
public static class SldColouring
{
    public const string Ink = "#333333", Unknown = "#9e9e9e";
    private static readonly string[] Categorical = ["#0072B2", "#D55E00", "#009E73", "#CC79A7", "#E69F00", "#56B4E9", "#F0E442", "#000000"];

    public static SldColourSet Colour(SldModel model, SldGeometry geometry, SldColourMode mode, SldColourInput? input = null)
    {
        ArgumentNullException.ThrowIfNull(model);
        ArgumentNullException.ThrowIfNull(geometry);
        input ??= new SldColourInput();
        var nodeColour = new Dictionary<string, string>(StringComparer.Ordinal);
        var elementColour = new Dictionary<string, string>(StringComparer.Ordinal);
        var legend = new List<(string, string)>();

        var levels = model.Nodes.Select(n => n.BaseKv).Distinct().OrderByDescending(v => v).ToList();
        string ByLevel(double kv) => Categorical[Math.Max(0, levels.IndexOf(kv)) % Categorical.Length];
        string ByVoltage(double vm) => double.IsNaN(vm) ? Unknown : vm < 0.95 ? "#0072B2" : vm <= 1.05 ? "#009E73" : "#D55E00";
        string ByLoading(double p) => double.IsNaN(p) ? Unknown : p < 50 ? "#0072B2" : p < 80 ? "#56B4E9" : p <= 100 ? "#E69F00" : "#D55E00";

        switch (mode)
        {
            case SldColourMode.VoltageLevel:
                foreach (double kv in levels) legend.Add((kv > 0 ? WorkbenchText.Canon(kv) + " kV" : "voltage not given", ByLevel(kv)));
                break;
            case SldColourMode.Loading:
                legend.AddRange([("below 50 %", "#0072B2"), ("50 to 80 %", "#56B4E9"), ("80 to 100 %", "#E69F00"), ("above 100 %", "#D55E00"), ("no rating or no result", Unknown)]);
                break;
            case SldColourMode.Voltage:
                legend.AddRange([("below 0.95 pu", "#0072B2"), ("0.95 to 1.05 pu", "#009E73"), ("above 1.05 pu", "#D55E00"), ("no result", Unknown)]);
                break;
            case SldColourMode.Area:
                for (int a = 0; a < input.AreaNames.Count; a++) legend.Add((input.AreaNames[a], Categorical[a % Categorical.Length]));
                legend.Add(("no area", Unknown));
                break;
        }

        foreach (SldNode n in model.Nodes)
            nodeColour[n.Id] = mode switch
            {
                SldColourMode.VoltageLevel => ByLevel(n.BaseKv),
                SldColourMode.Voltage => ByVoltage(input.NodeVm is { } v ? v[n.Index] : double.NaN),
                SldColourMode.Area => input.NodeArea is { } ar && ar[n.Index] >= 0 ? Categorical[ar[n.Index] % Categorical.Length] : Unknown,
                SldColourMode.Loading => Ink,
                _ => Ink,
            };
        for (int e = 0; e < model.Elements.Count; e++)
        {
            SldElement el = model.Elements[e];
            string c = mode switch
            {
                SldColourMode.VoltageLevel => ByLevel(el.BaseKv),
                SldColourMode.Loading => el.IsEdge ? ByLoading(input.ElementLoadingPercent is { } l ? l[e] : double.NaN) : Ink,
                SldColourMode.Voltage => el.IsEdge ? Ink : ByVoltage(input.NodeVm is { } v ? v[el.Node] : double.NaN),
                SldColourMode.Area => input.NodeArea is { } ar && ar[el.Node] >= 0 ? Categorical[ar[el.Node] % Categorical.Length] : Unknown,
                _ => Ink,
            };
            elementColour[el.Id] = el.InService ? c : Unknown;
        }

        string Of(string id) => nodeColour.TryGetValue(id, out string? c) ? c : elementColour.GetValueOrDefault(id, Ink);
        var symbols = geometry.Symbols.Select(s => Of(s.Id)).ToList();
        var connections = geometry.Connections.Select(c => Of(c.ElementId)).ToList();
        return new SldColourSet(mode, symbols, connections, legend);
    }
}
