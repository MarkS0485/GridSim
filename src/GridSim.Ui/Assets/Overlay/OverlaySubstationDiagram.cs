using System.Globalization;
using GridSim.Core.IO.Json.Overlay;
using GridSim.Ui.Assets.Keys;
using GridSim.Ui.Diagrams;
using GridSim.Ui.Schematic;

namespace GridSim.Ui.Assets.Overlay;

/// <summary>
/// Phase 5c (2026-10-20; first drawn as a simple view in slice 5-view, 2.0.72): a substation overlay record drawn as a
/// single-line diagram of its TOPOLOGY (<see cref="SubstationTopology"/>) on the A1b toolkit (GridSim.Ui/Diagrams), on
/// the record's OVERVIEW. Symbols follow IEC 60617 single-line conventions (the toolkit's library): a circuit breaker,
/// a disconnector and an earth switch each have their own symbol.
/// <list type="bullet">
/// <item>Each busbar section is a bus section bar, left to right in overlay order, labelled with its id and the bus it
/// collapses to.</item>
/// <item>A coupler between two NEIGHBOURING sections is drawn in line in the gap between them (the bus section breaker
/// position); any other coupler hangs below the bays and is routed to a tap on each of its two sections.</item>
/// <item>Each bay hangs from a tap of its section: its devices in series, in overlay order, then what it switches (a
/// line end, a transformer, a generator or a load) when it names a circuit. An earth switch is a bay of its own, ending
/// at earth.</item>
/// <item>Switches show their NORMAL state as written (DiagramStyle: red closed, green open, amber unknown).</item>
/// <item>In Run mode (<paramref name="solvedVm"/> given) every section, and every bay and coupler side hanging from it,
/// takes the energised colour when the solved voltage of the case bus the section collapses to is above zero. This is
/// read-only and a picture of the authored topology: no switching is simulated (the overlay is carried, not used by
/// the solver), so an open device does not de-energise anything beyond it.</item>
/// </list>
/// Symbol ids are the record ids (a bay's far end is "{last device id}/END"), so a click opens the record or the model
/// asset it names (<see cref="TargetOf"/>). Everything is plain arithmetic on the topology: the same topology and
/// voltages always give the same diagram.
/// </summary>
public static class OverlaySubstationDiagram
{
    /// <summary>The distance between neighbouring taps on a section.</summary>
    public const double Pitch = 80;

    /// <summary>The gap left between two sections (room for an in-line coupler: a device is 40 long).</summary>
    public const double SectionGap = 60;

    /// <summary>The y of every section.</summary>
    public const double BarY = 120;

    /// <summary>The x of the first section's left end.</summary>
    public const double LeftX = 40;

    /// <summary>The suffix of a bay's far-end symbol id.</summary>
    public const string EndSuffix = "/END";

    /// <summary>How far below the bars an in-line coupler's caption is centred.</summary>
    public const double InlineCaptionDrop = 18;

    /// <summary>The caption of a coupler's label after its id.</summary>
    public const string CouplerCaption = "  coupler";

    /// <summary>True when <paramref name="e"/> is a substation record with at least one busbar section to draw.</summary>
    public static bool CanDraw(OverlayEntry? e) => e?.Topology is { Sections.Count: > 0 };

    /// <summary>The diagram of substation record <paramref name="e"/>, or null when it has no busbar section.
    /// <paramref name="solvedVm"/> is the last solved voltage (pu) by bus id (Run mode), or null (nothing energised).</summary>
    public static DiagramModel? Build(OverlayEntry? e, IReadOnlyDictionary<long, double>? solvedVm = null) =>
        CanDraw(e) ? Build(e!.Topology!, solvedVm) : null;

    /// <summary>The words under the title, by mode.</summary>
    public static string ModeNote(bool solved) => solved
        ? "switch states as written; colours: the solved voltage of the bus each section collapses to (no switching is simulated)"
        : "switch states as written; no solved values, so nothing is drawn energised";

    /// <summary>The normal-state nodes in words: "['a', 'b'] and ['c']" (each node's sections, in overlay order).</summary>
    public static string NodesText(SubstationTopology t)
    {
        ArgumentNullException.ThrowIfNull(t);
        List<string> nodes = t.Groups.Select(g => "[" + string.Join(", ", g.Sections.Select(s => "'" + s + "'")) + "]").ToList();
        return nodes.Count <= 1 ? string.Concat(nodes) : string.Join(", ", nodes.Take(nodes.Count - 1)) + " and " + nodes[^1];
    }

    /// <summary>
    /// The diagram of topology <paramref name="t"/> (it must have at least one section). A record id the diagram has
    /// already used (possible only in an overlay mid-edit, which the checker would refuse) is drawn once, at its first
    /// record.
    /// </summary>
    public static DiagramModel Build(SubstationTopology t, IReadOnlyDictionary<long, double>? solvedVm = null)
    {
        ArgumentNullException.ThrowIfNull(t);
        if (t.Sections.Count == 0) throw new ArgumentException($"substation '{t.Substation}' has no busbar section to draw.", nameof(t));
        CultureInfo inv = CultureInfo.InvariantCulture;
        var used = new HashSet<string>(t.Sections.Select(s => s.Id), StringComparer.Ordinal);
        var sectionAt = t.Sections.Select((s, i) => (s.Id, i)).ToDictionary(x => x.Id, x => x.i, StringComparer.Ordinal);

        // ---- which bays are drawn (a repeated device id is drawn once) ----
        var bays = new List<TopologyBay>();
        foreach (TopologyBay b in t.Bays)
        {
            List<string> devs = b.Devices.Where(used.Add).ToList();
            if (devs.Count > 0) bays.Add(b with { Devices = devs });
        }

        // ---- couplers: the first one between each pair of neighbouring sections in line, the rest routed below ----
        var inline = new Dictionary<int, TopologyDevice>();   // left section index -> the coupler drawn in its right gap
        var routed = new List<TopologyDevice>();
        foreach (TopologyDevice d in t.Devices.Where(d => d.Role == TopologyDeviceRole.Coupler))
        {
            if (!used.Add(d.Id)) continue;
            int a = sectionAt[d.From!], b = sectionAt[d.To!];
            int left = Math.Min(a, b);
            if (Math.Abs(a - b) == 1 && !inline.ContainsKey(left)) inline[left] = d;
            else routed.Add(d);
        }

        // ---- taps per section: its bays first, then one per routed coupler end on it ----
        var tapCount = t.Sections.ToDictionary(s => s.Id, s => bays.Count(b => b.Section == s.Id), StringComparer.Ordinal);
        var routedTaps = new List<(int FromTap, int ToTap)>();
        foreach (TopologyDevice d in routed) routedTaps.Add((tapCount[d.From!]++, tapCount[d.To!]++));

        // ---- the states: a section is live when its bus has a solved voltage above zero ----
        bool solved = solvedVm is not null;
        DiagramState SectionState(string sectionId)
        {
            TopologySection s = t.Section(sectionId)!;
            bool live = solvedVm is not null && s.Bus is long bus && solvedVm.TryGetValue(bus, out double vm) && double.IsFinite(vm) && vm > 0;
            return new DiagramState(Energised: live);
        }
        static DiagramSwitchState Blade(TopologyDevice d) => d.NormalOpen switch
        {
            true => DiagramSwitchState.Open,
            false => DiagramSwitchState.Closed,
            null => DiagramSwitchState.Unknown,
        };
        DiagramState DeviceState(TopologyDevice d, string sectionId) => SectionState(sectionId) with { Switch = Blade(d) };

        // ---- the sections ----
        var m = new DiagramModel();
        double x = LeftX;
        foreach (TopologySection s in t.Sections)
        {
            int taps = Math.Max(1, tapCount[s.Id]);
            DiagramLayout.BusbarRow(m, s.Id, new ScreenPoint(x, BarY), taps, Pitch, s.Id + "  (" + s.BusText + ")", DiagramSymbolKind.BusSection, SectionState(s.Id));
            x += taps * Pitch + SectionGap;
        }

        // ---- the bays: the devices in series from the tap, then what the bay switches ----
        double deepest = 0;
        var nextTap = t.Sections.ToDictionary(s => s.Id, _ => 0, StringComparer.Ordinal);
        foreach (TopologyBay b in bays)
        {
            DiagramState live = SectionState(b.Section);
            DiagramPort tap = m.Port(b.Section, "T" + (nextTap[b.Section]++).ToString(inv));
            string prevId = b.Section, prevPort = tap.Name;
            double y = tap.Position.Y + DiagramLayout.BayStart;
            foreach (string id in b.Devices)
            {
                TopologyDevice d = t.Device(id)!;
                DiagramSymbolKind kind = SymbolOf(d.Device);
                m.Add(new DiagramSymbol(id, kind, new ScreenPoint(tap.Position.X, y), DiagramRotation.None, id) { State = DeviceState(d, b.Section) });
                m.SetConnectionState(m.Connect(prevId, prevPort, id, kind == DiagramSymbolKind.EarthSwitch ? "T" : "A").Id, live);
                prevId = id;
                prevPort = "B";
                y += DiagramLayout.BayPitch;
            }
            if (b.Feeder is TopologyFeeder f && FarEnd(f.Target) is (DiagramSymbolKind endKind, string endPort))
            {
                string endId = b.Devices[^1] + EndSuffix;
                if (used.Add(endId))
                {
                    m.Add(new DiagramSymbol(endId, endKind, new ScreenPoint(tap.Position.X, y), DiagramRotation.None, f.Display) { State = live });
                    m.SetConnectionState(m.Connect(prevId, prevPort, endId, endPort).Id, live);
                    y += DiagramLayout.BayPitch;
                }
            }
            deepest = Math.Max(deepest, y - DiagramLayout.BayPitch - BarY);
        }

        // ---- in-line couplers: in the gap to the right of their left section, turned to lie along the bar. A symbol's
        // own label is drawn to its right, which here would sit over the next section, so the caption is a free label
        // centred under the gap instead (the gap's centre is 70 from the nearest tap on either side: room to spare). ----
        foreach ((int left, TopologyDevice d) in inline.OrderBy(p => p.Key))
        {
            string l = t.Sections[left].Id, r = t.Sections[left + 1].Id;
            DiagramPort end = m.Port(l, "R");
            double cx = end.Position.X + SectionGap / 2;
            m.Add(new DiagramSymbol(d.Id, SymbolOf(d.Device), new ScreenPoint(cx, BarY), DiagramRotation.ThreeQuarter) { State = DeviceState(d, d.From!) });
            m.AddLabel(new DiagramLabel(d.Id + CouplerCaption, new ScreenPoint(cx, BarY + InlineCaptionDrop), 9, DiagramLabelAlign.Centre));
            m.SetConnectionState(m.Connect(l, "R", d.Id, "A").Id, SectionState(l));
            m.SetConnectionState(m.Connect(d.Id, "B", r, "L").Id, SectionState(r));
        }

        // ---- routed couplers (any other coupler): each lying along its own row below the bays, half way between its
        // two taps, with a lead dropping from each tap to its nearer end. The router turns half way down a lead, so the
        // first row is set low enough that every turn falls below the deepest bay (a bay's last symbol reaches 20 below
        // its centre): row 0 at BarY + 2 * (deepest + 30), so the turn at (BarY + row) / 2 is 10 below that. ----
        double rowY = BarY + 2 * (Math.Max(deepest, 0) + 30);
        for (int k = 0; k < routed.Count; k++)
        {
            TopologyDevice d = routed[k];
            DiagramPort a = m.Port(d.From!, "T" + routedTaps[k].FromTap.ToString(inv));
            DiagramPort b = m.Port(d.To!, "T" + routedTaps[k].ToTap.ToString(inv));
            bool fromLeft = a.Position.X <= b.Position.X;
            (string leftSec, DiagramPort leftTap, string rightSec, DiagramPort rightTap) = fromLeft ? (d.From!, a, d.To!, b) : (d.To!, b, d.From!, a);
            var at = new ScreenPoint((a.Position.X + b.Position.X) / 2, rowY + k * DiagramLayout.BayPitch);
            m.Add(new DiagramSymbol(d.Id, SymbolOf(d.Device), at, DiagramRotation.ThreeQuarter, d.Id + CouplerCaption) { State = DeviceState(d, d.From!) });
            m.SetConnectionState(m.Connect(leftSec, leftTap.Name, d.Id, "A").Id, SectionState(leftSec));
            m.SetConnectionState(m.Connect(rightSec, rightTap.Name, d.Id, "B").Id, SectionState(rightSec));
        }

        // ---- the words: what the picture is, the mode, the nodes and the warnings ----
        m.AddLabel(new DiagramLabel(t.Substation + "  as written in the asset overlay (" + OverlayIndex.NotUsedBySolver + ")",
            new ScreenPoint(LeftX, BarY - 70), 11));
        m.AddLabel(new DiagramLabel(ModeNote(solved), new ScreenPoint(LeftX, BarY - 56), 9, Muted: true));
        string warn = t.Warnings.Count switch
        {
            0 => "",
            1 => "  -  1 topology warning (see CHECK)",
            _ => "  -  " + t.Warnings.Count.ToString(inv) + " topology warnings (see CHECK)",
        };
        m.AddLabel(new DiagramLabel("normal-state nodes: " + NodesText(t) + warn, new ScreenPoint(LeftX, BarY - 42), 9, Muted: true));
        return m;
    }

    /// <summary>The IEC 60617 symbol of a device type.</summary>
    public static DiagramSymbolKind SymbolOf(TopologyDeviceType device) => device switch
    {
        TopologyDeviceType.Disconnector => DiagramSymbolKind.Disconnector,
        TopologyDeviceType.EarthSwitch => DiagramSymbolKind.EarthSwitch,
        _ => DiagramSymbolKind.CircuitBreaker,
    };

    // What a bay's far end is drawn as, by the circuit's type, and the port that faces the bay.
    private static (DiagramSymbolKind, string)? FarEnd(AssetRefTarget target) => target switch
    {
        AssetRefTarget.Line => (DiagramSymbolKind.LineEnd, "T"),
        AssetRefTarget.Transformer => (DiagramSymbolKind.Transformer2W, "HV"),
        AssetRefTarget.Generator => (DiagramSymbolKind.Generator, "T"),
        AssetRefTarget.Demand => (DiagramSymbolKind.Load, "T"),
        _ => null,
    };

    /// <summary>
    /// What a click on symbol <paramref name="symbolId"/> of <paramref name="e"/>'s diagram opens: a section or device
    /// symbol opens its record; a bay's far end opens the model asset the bay switches (the device's record when that
    /// does not resolve). Null for an id the diagram does not draw.
    /// </summary>
    public static AssetKey? TargetOf(OverlayIndex ov, OverlayEntry e, string symbolId)
    {
        ArgumentNullException.ThrowIfNull(ov);
        ArgumentNullException.ThrowIfNull(e);
        ArgumentNullException.ThrowIfNull(symbolId);
        bool end = symbolId.EndsWith(EndSuffix, StringComparison.Ordinal);
        string id = end ? symbolId[..^EndSuffix.Length] : symbolId;
        if (ov.Find(id) is not OverlayEntry hit || !ReferenceEquals(hit.Parent, e)) return null;
        if (!end) return hit.Key;
        return hit.Refs.FirstOrDefault(r => SubstationTopology.FeederRoles.Contains(r.Role, StringComparer.Ordinal))?.Target ?? hit.Key;
    }

    /// <summary>The record id a click on <paramref name="symbolId"/> stands for (the device for a bay's far end).</summary>
    public static string RecordIdOf(string symbolId)
    {
        ArgumentNullException.ThrowIfNull(symbolId);
        return symbolId.EndsWith(EndSuffix, StringComparison.Ordinal) ? symbolId[..^EndSuffix.Length] : symbolId;
    }
}
