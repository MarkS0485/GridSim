using System.Globalization;
using GridSim.Core.Model;

namespace GridSim.Core.IO.Cim.Profiles;

/// <summary>The bus positions a DL profile gives, on GridSim's layout scale (0..1, y growing downwards).</summary>
/// <param name="Positions">Bus id -> (X, Y), for the buses the documents place, in bus id order.</param>
/// <param name="Notes">What was chosen and why (normalisation, orientation, ties, objects left out).</param>
public sealed record CgmesLayout(IReadOnlyDictionary<int, (double X, double Y)> Positions, IReadOnlyList<string> Notes);

/// <summary>
/// RC5 WP10: the CGMES diagram layout (DL) profile. <b>Export</b>: one Diagram named after the case with
/// Diagram.orientation = OrientationKind.negative (y grows downwards, as GridSim's single-line diagram draws it), and per
/// bus one DiagramObject naming the bus's TopologicalNode (DiagramObject.IdentifiedObject) with one DiagramObjectPoint
/// (sequenceNumber 1, xPosition = Bus.X, yPosition = Bus.Y, written in round-trip form). <b>Import</b>
/// (<see cref="Read"/>): the DiagramObjects of the first Diagram that places a bus (through its TopologicalNode, or a
/// ConnectivityNode's TopologicalNode); a single point is the bus position, several are averaged (a busbar drawn as a
/// polyline); coordinates already in [0, 1] are kept, others are scaled by the bounding box keeping the aspect ratio
/// (s = max(xmax - xmin, ymax - ymin), X = (x - xmin) / s, Y = (y - ymin) / s); orientation positive flips Y = 1 - Y. A bus
/// placed twice keeps the first object in document order. <see cref="Apply"/> sets the positions on a model (a new model:
/// the import itself changes nothing).
/// </summary>
/// <remarks>Not carried: DiagramStyle, glue points, text annotations, visibility layers, rotation and switch symbols; one
/// point per bus. CGMES 2.4.15 defines OrientationKind.negative only; positive (CIM, CGMES 3.0) is read.</remarks>
public static class CgmesDiagramLayout
{
    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    internal static void WriteInto(ExtraDocument doc, GridModel model, List<string> refused, List<CgmesOmission> omissions, List<string> notes)
    {
        if (model.Buses.All(b => b.X == 0 && b.Y == 0))
        {
            refused.Add("--profiles dl: every bus is at (0, 0), so the model holds no layout to write.");
            return;
        }
        var bad = model.Buses.Where(b => !double.IsFinite(b.X) || !double.IsFinite(b.Y)).Select(b => b.Id).ToList();
        if (bad.Count > 0)
        {
            refused.Add($"--profiles dl: {bad.Count} bus(es) have a position that is not finite (first: {string.Join(", ", bad.Take(5))}).");
            return;
        }
        string diagram = doc.NewId("diagram");
        doc.New("Diagram", diagram, ("IdentifiedObject.name", model.Name ?? "case"), ("Diagram.orientation", doc.EnumOf("OrientationKind.negative")));
        foreach (Bus b in model.Buses)
        {
            string node = "N" + b.Id.ToString(Inv);
            string obj = doc.NewId(node);
            doc.New("DiagramObject", obj, ("IdentifiedObject.name", b.Name ?? node),
                ("DiagramObject.Diagram", ExtraDocument.Ref(diagram)), ("DiagramObject.IdentifiedObject", doc.RefObject(node)));
            doc.New("DiagramObjectPoint", doc.NewId(node + "/point/1"),
                ("DiagramObjectPoint.DiagramObject", ExtraDocument.Ref(obj)), ("DiagramObjectPoint.sequenceNumber", "1"),
                ("DiagramObjectPoint.xPosition", CgmesExtraProfiles.Num(b.X)), ("DiagramObjectPoint.yPosition", CgmesExtraProfiles.Num(b.Y)));
        }
        omissions.Add(new CgmesOmission(CgmesProfileKind.DL, "DiagramStyle, glue points, text annotations, branch and switch symbols",
            "GridSim holds one layout point per bus; branches are drawn between their buses."));
        notes.Add($"DL: {model.Buses.Count} bus position(s) written, orientation negative (y grows downwards).");
    }

    /// <summary>The bus positions the DL documents among <paramref name="documents"/> give, for the buses of
    /// <paramref name="busIdOfNode"/> (a bridge's <see cref="CimBridgeResult.BusIdOfNode"/>).</summary>
    public static CgmesLayout Read(IEnumerable<string> documents, IReadOnlyDictionary<string, int> busIdOfNode)
    {
        ArgumentNullException.ThrowIfNull(documents);
        ArgumentNullException.ThrowIfNull(busIdOfNode);
        CgmesObjectSet set = CgmesObjectSet.Parse(documents);
        var notes = new List<string>();

        int? BusOf(string? mrid)
        {
            if (mrid is null) return null;
            if (busIdOfNode.TryGetValue(mrid, out int id)) return id;
            CgmesObject? o = set.Get(mrid);
            string? tn = o?.Ref("ConnectivityNode.TopologicalNode");
            return tn is not null && busIdOfNode.TryGetValue(tn, out int id2) ? id2 : null;
        }

        var pointsOf = new Dictionary<string, List<(int? Seq, int Order, double X, double Y)>>(StringComparer.Ordinal);
        int order = 0;
        foreach (CgmesObject p in set.OfClass("DiagramObjectPoint"))
        {
            string? owner = p.Ref("DiagramObjectPoint.DiagramObject");
            if (owner is null) continue;
            double x = CgmesObjectSet.Double(p, "DiagramObjectPoint.xPosition") ?? throw new InvalidDataException($"DiagramObjectPoint '{p.Mrid}' has no xPosition.");
            double y = CgmesObjectSet.Double(p, "DiagramObjectPoint.yPosition") ?? throw new InvalidDataException($"DiagramObjectPoint '{p.Mrid}' has no yPosition.");
            string? seqText = p.Text("DiagramObjectPoint.sequenceNumber");
            int? seq = seqText is not null && int.TryParse(seqText, NumberStyles.Integer, Inv, out int s) ? s : null;
            if (!pointsOf.TryGetValue(owner, out var list)) pointsOf[owner] = list = new();
            list.Add((seq, order++, x, y));
        }

        string? chosenDiagram = null;
        var raw = new List<(int Bus, double X, double Y)>();
        var placed = new HashSet<int>();
        int twice = 0, otherDiagrams = 0;
        foreach (CgmesObject d in set.OfClass("DiagramObject"))
        {
            int? bus = BusOf(d.Ref("DiagramObject.IdentifiedObject"));
            if (bus is null || !pointsOf.TryGetValue(d.Mrid, out var pts) || pts.Count == 0) continue;
            string diagram = d.Ref("DiagramObject.Diagram") ?? "";
            chosenDiagram ??= diagram;
            if (diagram != chosenDiagram) { otherDiagrams++; continue; }
            if (!placed.Add(bus.Value)) { twice++; continue; }
            var sorted = pts.OrderBy(q => q.Seq ?? int.MaxValue).ThenBy(q => q.Order).ToList();
            double sx = 0, sy = 0;
            foreach (var q in sorted) { sx += q.X; sy += q.Y; }
            raw.Add((bus.Value, sorted.Count == 1 ? sorted[0].X : sx / sorted.Count, sorted.Count == 1 ? sorted[0].Y : sy / sorted.Count));
        }
        if (twice > 0) notes.Add($"{twice} diagram object(s) place a bus a second time; the first in document order is kept.");
        if (otherDiagrams > 0) notes.Add($"{otherDiagrams} diagram object(s) of other diagrams are left out; the first diagram that places a bus is read.");

        bool positive = false;
        if (chosenDiagram is not null && set.Get(chosenDiagram) is CgmesObject dg && dg.Ref("Diagram.orientation") is string orient)
        {
            string leaf = CgmesObjectSet.EnumLeaf(orient);
            if (leaf == "positive") positive = true;
            else if (leaf != "negative") throw new InvalidDataException($"Diagram '{dg.Mrid}': orientation '{orient}' is not OrientationKind.positive or negative.");
        }
        var result = new SortedDictionary<int, (double X, double Y)>();
        if (raw.Count > 0)
        {
            bool unit = raw.All(r => r.X >= 0 && r.X <= 1 && r.Y >= 0 && r.Y <= 1);
            double xmin = raw.Min(r => r.X), ymin = raw.Min(r => r.Y);
            double s = Math.Max(raw.Max(r => r.X) - xmin, raw.Max(r => r.Y) - ymin);
            if (!unit && !(s > 0)) throw new InvalidDataException("The diagram places every bus at one point outside [0, 1]; there is no layout to scale.");
            if (!unit) notes.Add($"positions scaled by the bounding box (side {s.ToString("R", Inv)}), keeping the aspect ratio.");
            if (positive) notes.Add("orientation positive (y grows upwards): Y flipped to 1 - Y.");
            foreach (var r in raw)
            {
                double x = unit ? r.X : (r.X - xmin) / s, y = unit ? r.Y : (r.Y - ymin) / s;
                result[r.Bus] = (x, positive ? 1.0 - y : y);
            }
        }
        notes.Add($"DL: {result.Count} bus position(s) read.");
        return new CgmesLayout(result, notes);
    }

    /// <summary>A copy of <paramref name="model"/> with the positions of <paramref name="layout"/> on its buses (buses the
    /// layout does not place keep theirs).</summary>
    public static GridModel Apply(GridModel model, CgmesLayout layout)
    {
        ArgumentNullException.ThrowIfNull(model);
        ArgumentNullException.ThrowIfNull(layout);
        var buses = model.Buses.Select(b => layout.Positions.TryGetValue(b.Id, out var p) ? b with { X = p.X, Y = p.Y } : b).ToList();
        return model.CopyWith(buses: buses);
    }
}
