using System.Windows.Media;
using GridSim.Ui.Tabs.Market;
using GridSim.Wpf.Visuals;

namespace GridSim.Wpf.Views;

// UI programme Phase 1.6 (first tab batch): the MARKET tab's logic moved to the platform-neutral
// GridSim.Ui.Tabs.Market.MarketModel (src/GridSim.Ui/Tabs/Market/MarketModel.cs), line for line, with each WPF Brush
// replaced by a neutral colour token (UiColour). This file is now the thin WPF side of that boundary: the view-model
// and its interconnector row derive from the neutral ones and add, under the OLD property names the XAML binds, a
// frozen brush for every token (Palette.Frozen: the same colour bytes). What the user sees is unchanged.

/// <summary>
/// THE MARKET TAB  -  the trading / market-operator view of the same streamed instant (the WPF side of
/// <see cref="MarketModel"/>: adds the brushes the XAML binds).
/// </summary>
public sealed class MarketVm : MarketModel
{
    public Brush ImbalanceBrush => Palette.Frozen(ImbalanceColour);
    public Brush NivBrush => Palette.Frozen(NivColour);
    public Brush CarbonBrush => Palette.Frozen(CarbonColour);

    /// <summary>Each neutral property and the WPF adapter property the XAML binds in its place.</summary>
    private static readonly Dictionary<string, string> AdapterOf = new(StringComparer.Ordinal)
    {
        [nameof(ImbalanceColour)] = nameof(ImbalanceBrush),
        [nameof(NivColour)] = nameof(NivBrush),
        [nameof(CarbonColour)] = nameof(CarbonBrush),
    };

    /// <summary>Announce the neutral property, then (when it has one) the WPF adapter property bound in its place.</summary>
    protected override void OnPropertyChanged(string? name)
    {
        base.OnPropertyChanged(name);
        if (name is not null && AdapterOf.TryGetValue(name, out string? adapter)) base.OnPropertyChanged(adapter);
    }

    protected override MktInterconnectorRowModel CreateInterconnectorRow() => new MktInterconnectorRow();
}

/// <summary>One interconnector (the WPF row: adds <see cref="FlowBrush"/>).</summary>
public sealed class MktInterconnectorRow : MktInterconnectorRowModel
{
    public Brush FlowBrush => Palette.Frozen(FlowColour);

    protected override void OnPropertyChanged(string? name)
    {
        base.OnPropertyChanged(name);
        if (name == nameof(FlowColour)) base.OnPropertyChanged(nameof(FlowBrush));
    }
}
