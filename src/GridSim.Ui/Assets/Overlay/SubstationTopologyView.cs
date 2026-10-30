using System.Globalization;
using System.Text.Json;
using GridSim.Core.IO.Json.Overlay;
using GridSim.Ui.Diagrams;

namespace GridSim.Ui.Assets.Overlay;

// =====================================================================================================================
// Phase 5c (2026-10-20): the substation topology as the ASSETS views show it, for an overlay that may be MID-EDIT.
//
// The projection itself (sections, devices, bays, the normal-state nodes and the implied split / coupling warnings) is
// GridSim.Core's SubstationTopology, so the checker (V2AssetChecker.Warnings), the Run-mode ASSETS tab (OverlayIndex)
// and Author mode all give the same answer from the same code. This file adds what Author mode needs on top: reading
// the overlay records straight from the session's overlay documents, leniently (an edit in progress may leave a record
// the checker would refuse: such a record is skipped or read as far as it goes, never thrown on), and finding the
// substation a selected record belongs to.
// =====================================================================================================================

/// <summary>
/// One substation's topology view: the projection, its single-line diagram (switch states as written, nothing
/// energised: Author mode has no live values) and its warning texts.
/// </summary>
/// <param name="Topology">The projection.</param>
/// <param name="Diagram">The single-line diagram, or null when the substation has no busbar section.</param>
/// <param name="Warnings">The warning texts, in order (empty for a consistent topology).</param>
public sealed record SubstationTopologyView(SubstationTopology Topology, DiagramModel? Diagram, IReadOnlyList<string> Warnings)
{
    /// <summary>The heading over the view ("SUBSTATION 'ov.sub'  (CARRIED, NOT USED BY THE SOLVER)").</summary>
    public string Heading => "SUBSTATION '" + Topology.Substation + "'  -  SINGLE-LINE VIEW OF THE OVERLAY  ("
        + OverlayIndex.NotUsedBySolver.ToUpperInvariant() + ")";

    /// <summary>The normal-state nodes in words.</summary>
    public string Nodes => "Normal-state nodes: " + (Topology.Sections.Count == 0 ? "none (no busbar section)" : OverlaySubstationDiagram.NodesText(Topology));

    /// <summary>True when there is a diagram to draw.</summary>
    public bool HasDiagram => Diagram is not null;

    /// <summary>True when there is at least one warning.</summary>
    public bool HasWarnings => Warnings.Count > 0;

    /// <summary>
    /// The view of the substation that record <paramref name="recordId"/> is or belongs to (the substation itself, or a
    /// direct child of one: a busbar section, a switching device or any other child), or null when it is neither.
    /// </summary>
    public static SubstationTopologyView? For(string recordId, IReadOnlyList<AssetOverlayRecord> records)
    {
        ArgumentNullException.ThrowIfNull(recordId);
        ArgumentNullException.ThrowIfNull(records);
        AssetOverlayRecord? r = records.FirstOrDefault(x => string.Equals(x.Id, recordId, StringComparison.Ordinal));
        if (r is null) return null;
        AssetOverlayRecord? sub = r.Kind == "substation" ? r
            : r.Parent is string p ? records.FirstOrDefault(x => string.Equals(x.Id, p, StringComparison.Ordinal) && x.Kind == "substation") : null;
        if (sub is null) return null;
        SubstationTopology t = SubstationTopology.Build(sub, records);
        return new SubstationTopologyView(t, t.Sections.Count > 0 ? OverlaySubstationDiagram.Build(t) : null, t.Warnings.Select(w => w.Message).ToList());
    }

    /// <summary>
    /// The records of overlay documents as written, read leniently: a record without a string id and kind is skipped; a
    /// reference that is not a one-member object of a known target type (bus by a whole number, the rest by a string) is
    /// left out; params are kept as written (the projection reads them leniently too). <paramref name="documents"/> are
    /// (file name, root) pairs, in manifest order.
    /// </summary>
    public static IReadOnlyList<AssetOverlayRecord> ReadRecords(IReadOnlyList<(string Name, JsonElement Root)> documents)
    {
        ArgumentNullException.ThrowIfNull(documents);
        var list = new List<AssetOverlayRecord>();
        foreach ((string name, JsonElement root) in documents)
        {
            if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("records", out JsonElement recs) || recs.ValueKind != JsonValueKind.Array) continue;
            int i = -1;
            foreach (JsonElement r in recs.EnumerateArray())
            {
                i++;
                if (r.ValueKind != JsonValueKind.Object) continue;
                if (Str(r, "id") is not string id || Str(r, "kind") is not string kind) continue;
                var refs = new List<AssetOverlayRef>();
                if (r.TryGetProperty("refs", out JsonElement rs) && rs.ValueKind == JsonValueKind.Object)
                    foreach (JsonProperty role in rs.EnumerateObject())
                        if (ReadRef(role) is AssetOverlayRef x) refs.Add(x);
                list.Add(new AssetOverlayRecord
                {
                    Id = id,
                    Kind = kind,
                    Parent = Str(r, "parent"),
                    Refs = refs,
                    Params = r.TryGetProperty("params", out JsonElement ps) ? ps.Clone() : default,
                    Document = name,
                    Pointer = "/records/" + i.ToString(CultureInfo.InvariantCulture),
                });
            }
        }
        return list;
    }

    private static string? Str(JsonElement o, string member) =>
        o.TryGetProperty(member, out JsonElement v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    // One reference, or null when it is not a one-member object naming a known target type with a value of its type.
    private static AssetOverlayRef? ReadRef(JsonProperty role)
    {
        if (role.Value.ValueKind != JsonValueKind.Object) return null;
        List<JsonProperty> members = role.Value.EnumerateObject().ToList();
        if (members.Count != 1) return null;
        JsonProperty only = members[0];
        AssetRefTarget? target = only.Name switch
        {
            "bus" => AssetRefTarget.Bus,
            "line" => AssetRefTarget.Line,
            "transformer" => AssetRefTarget.Transformer,
            "generator" => AssetRefTarget.Generator,
            "demand" => AssetRefTarget.Demand,
            "record" => AssetRefTarget.Record,
            _ => null,
        };
        if (target is not AssetRefTarget t) return null;
        if (t == AssetRefTarget.Bus)
            return AssetOverlayTargets.BusIdOf(only.Value) is long bus ? new AssetOverlayRef(role.Name, t, bus, null) : null;
        return only.Value.ValueKind == JsonValueKind.String ? new AssetOverlayRef(role.Name, t, null, only.Value.GetString()) : null;
    }
}
