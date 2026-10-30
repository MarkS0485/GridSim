using System.Globalization;
using GridSim.Ui.Assets.Overlay;
using GridSim.Ui.Assets.Overviews;
using GridSim.Ui.Assets.Pages;
using GridSim.Ui.Diagrams;
using GridSim.Ui.Visuals;

namespace GridSim.Ui.Assets.Navigation;

/// <summary>
/// The richer single-line views of the ASSETS unit pages (UI programme ASSETS A3, plan section 8 decision 2): the A1b
/// toolkit diagram of a substation page (<see cref="SubstationDiagramBuilder"/>, from the page's own
/// <c>MapInput</c>, the same data the A1 single-line picture draws) and of a transformer page
/// (<see cref="TransformerDiagramBuilder"/>, from the same page values the A1 schematic is bound to), and what a click on
/// one of their symbols opens. The A1 pictures stay on the pages for exact WPF parity; these are the second view.
/// </summary>
public static class AssetDiagrams
{
    /// <summary>The page property whose change means the diagram must be rebuilt: a substation page sets its
    /// <c>MapInput</c> last in each refresh; a transformer page sets its <c>Trace</c> last (a new array every refresh).
    /// Null for a page with no diagram.</summary>
    public static string? RebuildOn(AssetPageModel? page) => page switch
    {
        SubstationDetailModel<PlantRowModel, ConnectionRowModel> => nameof(SubstationDetailModel<PlantRowModel, ConnectionRowModel>.MapInput),
        TransformerDetailModel<TransformerReadingModel, TransformerEventModel> => nameof(TransformerDetailModel<TransformerReadingModel, TransformerEventModel>.Trace),
        // Phase 5c: a substation overlay record's diagram follows the solved bus voltages, so it is rebuilt when the page
        // raises Content, the last of its refresh notifications.
        AssetOverviewPage { HasDiagram: true } => nameof(AssetOverviewPage.Content),
        _ => null,
    };

    /// <summary>The toolkit diagram of <paramref name="page"/> now, or null (no diagram for this page, or a substation
    /// page that has not seen a frame).</summary>
    public static DiagramModel? For(AssetPageModel? page) => page switch
    {
        SubstationDetailModel<PlantRowModel, ConnectionRowModel> sub => sub.MapInput is { } input ? SubstationDiagramBuilder.Build(input) : null,
        TransformerDetailModel<TransformerReadingModel, TransformerEventModel> tx => TransformerDiagramBuilder.Build(InputOf(tx)),
        // Phase 5c (first drawn in slice 5-view): a substation overlay record's topology, switch states as written, its
        // sections coloured by the solved voltage of their bus in Run mode.
        AssetOverviewPage ov => OverlaySubstationDiagram.Build(ov.OverlayRecord, ov.SolvedVoltages),
        _ => null,
    };

    /// <summary>
    /// The transformer page's values as the schematic input, each the page property the WPF and Avalonia schematics are
    /// bound to (LoadingPct, HvKvNom, LvKvNom, HvKvLiveNum, LvKvLiveNum, FlowMw, RatingNum, TapNum, TopOilC, HotSpotC,
    /// HasOltc, TapMinNum, TapMaxNum).
    /// </summary>
    public static TransformerSchematicInput InputOf(TransformerDetailModel<TransformerReadingModel, TransformerEventModel> tx)
    {
        ArgumentNullException.ThrowIfNull(tx);
        return new TransformerSchematicInput
        {
            LoadingPct = tx.LoadingPct,
            HvKvNom = tx.HvKvNom,
            LvKvNom = tx.LvKvNom,
            HvKvLive = tx.HvKvLiveNum,
            LvKvLive = tx.LvKvLiveNum,
            FlowMw = tx.FlowMw,
            RatingMva = tx.RatingNum,
            TapRatio = tx.TapNum,
            TopOilC = tx.TopOilC,
            HotSpotC = tx.HotSpotC,
            HasOltc = tx.HasOltc,
            TapMin = tx.TapMinNum,
            TapMax = tx.TapMaxNum,
        };
    }

    /// <summary>
    /// Open what a click on symbol <paramref name="symbolId"/> of <paramref name="page"/>'s diagram stands for, by the
    /// page's own click routes: connection bay i is the page's connection row i (the diagram is built from the rows in
    /// order), generator bay k its plant row k; the transformer's HV and LV busbars are its HV and LV bus links. Returns
    /// false when the symbol stands for no other asset.
    /// </summary>
    public static bool Open(AssetPageModel? page, string symbolId)
    {
        ArgumentNullException.ThrowIfNull(symbolId);
        switch (page)
        {
            case SubstationDetailModel<PlantRowModel, ConnectionRowModel> sub:
                if (Bay(symbolId, 'C') is int c && c < sub.Connections.Count) { sub.Connections[c].OnOpen(); return true; }
                if (Bay(symbolId, 'G') is int g && g < sub.Plant.Count) { sub.Plant[g].OnOpen(); return true; }
                return false;
            case TransformerDetailModel<TransformerReadingModel, TransformerEventModel> tx:
                if (symbolId == TransformerDiagramBuilder.HvBusId) { tx.OpenHvSubstation(tx.HvBusId); return true; }
                if (symbolId == TransformerDiagramBuilder.LvBusId) { tx.OpenLvSubstation(tx.LvBusId); return true; }
                return false;
            default:
                return false;
        }
    }

    // "C12/CB" -> 12 for prefix 'C': the bay number of a symbol id the substation builder makes ("{bay}/DS", "/CB",
    // "/TX", "/END", the bay being "C{i}" or "G{k}"); null for any other id.
    private static int? Bay(string id, char prefix)
    {
        if (id.Length < 3 || id[0] != prefix) return null;
        int slash = id.IndexOf('/', StringComparison.Ordinal);
        if (slash < 2) return null;
        return int.TryParse(id.AsSpan(1, slash - 1), NumberStyles.None, CultureInfo.InvariantCulture, out int n) ? n : null;
    }
}
