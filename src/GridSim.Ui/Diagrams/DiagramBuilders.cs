using System.Globalization;
using GridSim.Ui.Schematic;
using GridSim.Ui.Visuals;

namespace GridSim.Ui.Diagrams;

/// <summary>
/// The substation single-line diagram on the toolkit (UI programme ASSETS A1b), for the A3 ASSETS views: the same frame
/// data as <see cref="SubstationMapGeometry"/> (<see cref="SubstationMapInput"/>) drawn as a standard IEC-style
/// single-line diagram: one busbar, a bay per connection above it (disconnector, breaker, then a transformer and the
/// far end for a transformer connection, or a line end), a bay per generator and one for the demand below it.
///
/// <para>This is a separate builder, not a re-expression of the A1 drawing: the A1 diagram's own pictures (a 6 px bar
/// with end caps, fixed text boxes, flow arrows, fuel-coloured generator rings) are not toolkit symbols, so drawing them
/// on the toolkit would not be pixel-identical. The A1 geometry stays the parity path for the WPF control.</para>
///
/// <para>Text is formatted with the invariant culture (a decimal point on every machine).</para>
/// </summary>
public static class SubstationDiagramBuilder
{
    /// <summary>The busbar's left end.</summary>
    public static readonly ScreenPoint BusbarOrigin = new(40, 260);

    /// <summary>The busbar's id.</summary>
    public const string BusbarId = "BUS";

    /// <summary>The diagram for one frame's data.</summary>
    /// <summary>The width one character of a bay-end label takes, generously (labels are <see cref="DiagramStyle.LabelSize"/>
    /// px semibold, about 6 px a character).</summary>
    public const double LabelCharWidth = 6.2;

    /// <summary>The room a bay needs beside its end label: the end symbol's half-width and gap before the label, and the
    /// neighbouring bay's symbol half-width and a margin after it.</summary>
    public const double LabelClearance = 32;

    /// <summary>
    /// ASSETS A3 follow-up (2026-10-24): the distance between neighbouring bays. A bay-end label (the far end of a line,
    /// a generator's name and output, the demand) is drawn to the right of its end symbol, at the height of the
    /// neighbouring bay's symbols, so a label wider than the default pitch ran into the next bay (a far-end name over a
    /// transformer symbol). The pitch is the default, or the room the longest end label needs, whichever is larger. Pure
    /// arithmetic on the input: the same input always gives the same layout.
    /// </summary>
    public static double PitchFor(SubstationMapInput d)
    {
        ArgumentNullException.ThrowIfNull(d);
        CultureInfo inv = CultureInfo.InvariantCulture;
        IEnumerable<string> labels = d.Connections.Select(c => c.OtherEnd)
            .Concat(d.Generators.Select(g => string.Create(inv, $"{g.Name} {g.OutputMw:F0} MW")))
            .Concat(Math.Abs(d.DemandMw) > 0.1 ? new[] { string.Create(inv, $"{d.DemandMw:F0} MW {d.ReactiveMvar:F0} MVAr") } : Array.Empty<string>());
        int longest = labels.Select(l => l.Length).DefaultIfEmpty(0).Max();
        return Math.Max(DiagramLayout.DefaultBayPitch, longest * LabelCharWidth + LabelClearance);
    }

    public static DiagramModel Build(SubstationMapInput d)
    {
        ArgumentNullException.ThrowIfNull(d);
        CultureInfo inv = CultureInfo.InvariantCulture;
        var m = new DiagramModel();
        int below = d.Generators.Count + (Math.Abs(d.DemandMw) > 0.1 ? 1 : 0);
        int bays = Math.Max(1, Math.Max(d.Connections.Count, below));
        bool live = double.IsFinite(d.VoltagePu) && d.VoltagePu > 0;
        var busState = new DiagramState(Energised: live);
        DiagramLayout.BusbarRow(m, BusbarId, BusbarOrigin, bays, pitch: PitchFor(d),
            label: string.Create(inv, $"{d.BusType} {d.BaseKv:F0} kV"), state: busState);
        m.AddLabel(new DiagramLabel(string.Create(inv, $"{d.VoltagePu:F3} pu   {d.VoltagePu * d.BaseKv:F1} kV   {d.AngleDeg:+0.0;-0.0;0.0} deg"),
            new ScreenPoint(BusbarOrigin.X, BusbarOrigin.Y + 10), Muted: true));

        // Connections above the bar.
        for (int i = 0; i < d.Connections.Count; i++)
        {
            SubstationMapConnection c = d.Connections[i];
            string bay = "C" + i.ToString(inv);
            var state = new DiagramState(live, DiagramSwitchState.Closed, c.LoadingFrac > 0 ? c.LoadingFrac : null);
            bool tx = c.Kind == "TRANSFORMER";
            FeederBayResult r = DiagramLayout.FeederBay(m, BusbarId, i, bay, tx ? FeederEnd.None : FeederEnd.Line,
                BayDirection.Up, endLabel: c.OtherEnd);
            var conns = new List<string>(r.ConnectionIds);
            if (tx)
            {
                // A transformer connection: the transformer above the breaker, then the far end.
                ScreenPoint cb = m.Symbol(r.SymbolIds[^1]).Position;
                string txId = bay + "/TX", endId = bay + "/END";
                m.Add(new DiagramSymbol(txId, DiagramSymbolKind.Transformer2W, new ScreenPoint(cb.X, cb.Y - DiagramLayout.BayPitch), DiagramRotation.Half) { State = state });
                m.Add(new DiagramSymbol(endId, DiagramSymbolKind.LineEnd, new ScreenPoint(cb.X, cb.Y - 2 * DiagramLayout.BayPitch), DiagramRotation.Half, c.OtherEnd));
                conns.Add(m.Connect(r.SymbolIds[^1], "B", txId, "HV").Id);
                conns.Add(m.Connect(txId, "LV", endId, "T").Id);
            }
            foreach (string cid in conns) m.SetConnectionState(cid, state);
            ScreenPoint breaker = m.Symbol(bay + "/CB").Position;
            m.AddLabel(new DiagramLabel(string.Create(inv, $"{Math.Abs(c.FlowMw):F0} MW"), new ScreenPoint(breaker.X - 34, breaker.Y - 6), 9, Muted: true));
        }

        // Generators, then the demand, below the bar.
        int tap = 0;
        foreach (SubstationMapGenerator g in d.Generators)
        {
            FeederBayResult r = DiagramLayout.FeederBay(m, BusbarId, tap, "G" + tap.ToString(inv), FeederEnd.Generator,
                endLabel: string.Create(inv, $"{g.Name} {g.OutputMw:F0} MW"));
            foreach (string cid in r.ConnectionIds) m.SetConnectionState(cid, busState);
            tap++;
        }
        if (Math.Abs(d.DemandMw) > 0.1)
        {
            FeederBayResult r = DiagramLayout.FeederBay(m, BusbarId, tap, "D", FeederEnd.Load,
                endLabel: string.Create(inv, $"{d.DemandMw:F0} MW {d.ReactiveMvar:F0} MVAr"));
            foreach (string cid in r.ConnectionIds) m.SetConnectionState(cid, busState);
        }
        return m;
    }
}

/// <summary>
/// The transformer as a single-line diagram on the toolkit (UI programme ASSETS A1b), for the A3 ASSETS views: the HV
/// busbar, a breaker, the two-winding transformer coloured by its loading, a breaker and the LV busbar, with the live
/// readings as labels (the same <see cref="TransformerSchematicInput"/> the A1 front elevation draws).
///
/// <para>A separate builder, not a re-expression of the A1 front elevation (a tank, core, bushings and coolers are not
/// single-line symbols), which stays the parity path for the WPF control. Invariant-culture text.</para>
/// </summary>
public static class TransformerDiagramBuilder
{
    /// <summary>The HV busbar's id.</summary>
    public const string HvBusId = "HV";

    /// <summary>The LV busbar's id.</summary>
    public const string LvBusId = "LV";

    /// <summary>The transformer bay's id (the transformer is "TX/TX").</summary>
    public const string BayId = "TX";

    /// <summary>The diagram for the given readings.</summary>
    public static DiagramModel Build(TransformerSchematicInput v)
    {
        CultureInfo inv = CultureInfo.InvariantCulture;
        var m = new DiagramModel();
        var hvState = new DiagramState(Energised: v.HvKvLive > 0);
        var lvState = new DiagramState(Energised: v.LvKvLive > 0);
        DiagramLayout.BusbarRow(m, HvBusId, new ScreenPoint(40, 40), 1, 160, string.Create(inv, $"HV {v.HvKvNom:F0} kV"), state: hvState);
        DiagramLayout.BusbarRow(m, LvBusId, new ScreenPoint(40, 280), 1, 160, string.Create(inv, $"LV {v.LvKvNom:F0} kV"), state: lvState);
        TransformerBayResult bay = DiagramLayout.TransformerBetween(m, HvBusId, 0, LvBusId, 0, BayId, string.Create(inv, $"{v.RatingMva:F0} MVA"));

        // The transformer is live when either side is, and carries its loading; the connections take their side's state.
        bool live = hvState.Energised || lvState.Energised;
        m.SetState(bay.TransformerId, new DiagramState(live, DiagramSwitchState.Closed, v.LoadingPct / 100));
        m.SetConnectionState(bay.ConnectionIds[0], hvState);
        m.SetConnectionState(bay.ConnectionIds[1], hvState);
        m.SetConnectionState(bay.ConnectionIds[2], lvState);
        m.SetConnectionState(bay.ConnectionIds[3], lvState);

        ScreenPoint tx = m.Symbol(bay.TransformerId).Position;
        double x = tx.X + 70;
        m.AddLabel(new DiagramLabel(string.Create(inv, $"LOAD {v.LoadingPct:F1} %"), new ScreenPoint(x, tx.Y - 30)));
        m.AddLabel(new DiagramLabel(string.Create(inv, $"{v.FlowMw:F1} MW"), new ScreenPoint(x, tx.Y - 16)));
        m.AddLabel(new DiagramLabel(string.Create(inv, $"HV {v.HvKvLive:F1} kV  LV {v.LvKvLive:F1} kV"), new ScreenPoint(x, tx.Y - 2), 9, Muted: true));
        if (v.HasOltc)
            m.AddLabel(new DiagramLabel(string.Create(inv, $"OLTC tap {v.TapRatio:F3} ({v.TapMin:F3} to {v.TapMax:F3})"), new ScreenPoint(x, tx.Y + 12), 9, Muted: true));
        return m;
    }
}
