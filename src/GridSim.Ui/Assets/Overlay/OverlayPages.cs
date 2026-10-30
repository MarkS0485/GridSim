using System.Globalization;
using System.Text.Encodings.Web;
using System.Text.Json;
using GridSim.Core.IO.Json.Overlay;
using GridSim.Core.IO.Json.Schema;
using GridSim.Core.Simulation;
using GridSim.Ui.Assets.Index;
using GridSim.Ui.Assets.Keys;
using GridSim.Ui.Assets.Navigation;
using GridSim.Ui.Assets.Overviews;

namespace GridSim.Ui.Assets.Overlay;

// UI programme ASSETS slice 5-view: what the overlay's pages and sub-tabs show, as plain immutable values the views bind
// and the tests compare exactly. Nothing here is formatted for a culture: literals are the overlay file's own JSON text,
// counts are invariant digits. Every page says plainly that the overlay is carried and not used by the solver.

/// <summary>The OVERLAY sub-tab of a model asset: the overlay records attached to it, each a link to its record.</summary>
/// <param name="Heading">What the list is ("4 overlay records attached to bus 3 'Load132'").</param>
/// <param name="Note">The solver note (<see cref="OverlayIndex.SolverNote"/>).</param>
/// <param name="Rows">One row per attached record: how it attaches, its id, its kind; a click selects its record.</param>
public sealed record AssetOverlayList(string Heading, string Note, IReadOnlyList<ConnectionRow> Rows)
{
    /// <summary>The section title of the rows.</summary>
    public const string Section = "OVERLAY RECORDS ATTACHED TO THIS ASSET";
}

/// <summary>The PROPERTIES sub-tab of an overlay record: the record as written, read-only.</summary>
/// <param name="Heading">"ov.cb  (assets/site.assets.json /records/3)".</param>
/// <param name="Kind">The kind and its title ("switchingDevice: Switching device").</param>
/// <param name="Note">The solver note.</param>
/// <param name="Rows">The members: id, kind, parent, each reference role, each parameter, prov, note.</param>
public sealed record OverlayRecordProperties(string Heading, string Kind, string Note, IReadOnlyList<PropertyRow> Rows);

/// <summary>One checker problem of an overlay record.</summary>
/// <param name="Pointer">The JSON pointer of the field in the overlay file.</param>
/// <param name="Message">The checker's message.</param>
public sealed record OverlayCheckRow(string Pointer, string Message);

/// <summary>The CHECK sub-tab of an overlay record: its problems from the overlay checker (V2AssetChecker).</summary>
/// <param name="Heading">"2 problems in assets/site.assets.json".</param>
/// <param name="Note">The solver note.</param>
/// <param name="Rows">The problems, in the order the checker reported them.</param>
public sealed record OverlayCheck(string Heading, string Note, IReadOnlyList<OverlayCheckRow> Rows)
{
    /// <summary>Phase 5c: the record's topology WARNINGS (a substation's implied bus split or coupling), each at the
    /// substation record's pointer; empty for every other record. A warning is never an error: the case still loads and
    /// solves exactly as written.</summary>
    public IReadOnlyList<OverlayCheckRow> Warnings { get; init; } = Array.Empty<OverlayCheckRow>();

    /// <summary>True when there is at least one problem (the problem list is shown).</summary>
    public bool HasRows => Rows.Count > 0;

    /// <summary>True when there is at least one warning (the warning list is shown).</summary>
    public bool HasWarnings => Warnings.Count > 0;

    /// <summary>The caption over the warnings.</summary>
    public const string WarningsSection = "TOPOLOGY WARNINGS  (THE CASE STILL LOADS AND SOLVES EXACTLY AS WRITTEN)";

    /// <summary>Phase 5f onwards: the record's kind-specific consistency notes (a three-winding transformer whose legs
    /// are not its nameplate's derivation, an HVDC link whose ends have the same MW sign), DISPLAY ONLY: never errors,
    /// the checker is unchanged and the case still loads and solves exactly as written. Empty when none.</summary>
    public IReadOnlyList<OverlayCheckRow> Notes { get; init; } = Array.Empty<OverlayCheckRow>();

    /// <summary>True when there is at least one consistency note.</summary>
    public bool HasNotes => Notes.Count > 0;

    /// <summary>The caption over the consistency notes.</summary>
    public const string NotesSection = "CONSISTENCY NOTES  (DISPLAY ONLY, NEVER ERRORS: THE CASE STILL LOADS AND SOLVES EXACTLY AS WRITTEN)";
}

/// <summary>
/// The content of the overlay's OVERVIEW pages (the OVERLAY class page, a kind's page, a record's page) and of the
/// overlay sub-tabs (OVERLAY on a model asset; PROPERTIES and CHECK on a record). Pure: the same key and index give the
/// same content. The overlay has no live value, so the frame never matters.
/// </summary>
public static class OverlayPages
{
    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    // The JSON text of a string the way a writer would write it (quotes, the minimal escapes).
    private static readonly JsonSerializerOptions Relaxed = new() { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    private static string Quote(string s) => JsonSerializer.Serialize(s, Relaxed);

    private static string Count(int n) => n.ToString("N0", Inv);

    private static string Capped(int total) =>
        total > OverviewBuilder.RowCap ? $"showing {OverviewBuilder.RowCap.ToString(Inv)} of {Count(total)}" : "";

    /// <summary>The OVERVIEW content of overlay page <paramref name="key"/> (OVERLAY, a kind, a record). Phase 5g:
    /// <paramref name="frame"/> is the last solved frame (null before any), read only by an HVDC link's page to show the
    /// solved output of the model generators it groups; every overlay value is the same with or without it.</summary>
    public static OverviewContent Build(AssetPageKey key, AssetIndex index, SimFrame? frame = null)
    {
        ArgumentNullException.ThrowIfNull(key);
        ArgumentNullException.ThrowIfNull(index);
        OverlayIndex? ov = index.Overlay;
        if (ov is null) return Missing("ASSET OVERLAY", key.Name ?? "OVERLAY", "the case carries no asset overlay");
        return key.Kind switch
        {
            AssetPageKind.OverlayFleet => Fleet(ov),
            AssetPageKind.OverlayKind => Kind(key.Name ?? "", ov),
            AssetPageKind.OverlayRecord => Record(key, ov, frame),
            _ => throw new ArgumentOutOfRangeException(nameof(key), key.Kind, "Not an overlay page."),
        };
    }

    // A page whose subject is gone still says what the overlay is.
    private static OverviewContent Missing(string eyebrow, string title, string why) => new(eyebrow, title,
        why + "  -  " + OverlayIndex.NotUsedBySolver, Array.Empty<OverviewTile>(), Array.Empty<OverviewFact>(), "",
        Array.Empty<OverviewRow>(), "", OverlayIndex.SolverNote, false);

    private static OverviewRow RecordRow(OverlayEntry e, string detail) =>
        new(e.Id, detail, e.Kind, e.Key);

    // ---- the OVERLAY class page: every record, by kind ----
    private static OverviewContent Fleet(OverlayIndex ov)
    {
        int attached = ov.Entries.Count(e => e.Attachments.Count > 0);
        int problems = ov.Entries.Sum(e => e.Problems.Count) + ov.UnplacedProblems.Count;
        var tiles = new List<OverviewTile>
        {
            new("RECORDS", Count(ov.Entries.Count), "overlay records the case carries"),
            new("KINDS", Count(ov.ByKind.Count), "overlay kinds in use"),
            new("FILES", Count(ov.Overlay.Documents.Count), "overlay files the manifest lists"),
            new("ATTACHED", Count(attached), "records tied to an asset of the model"),
        };
        if (problems > 0) tiles.Add(new OverviewTile("PROBLEMS", Count(problems), "from the overlay checker"));
        var rows = new List<OverviewRow>();
        int total = 0;
        foreach (OverlayKindGroup g in ov.ByKind)
            foreach (OverlayEntry e in g.Records)
            {
                total++;
                if (rows.Count < OverviewBuilder.RowCap) rows.Add(RecordRow(e, g.Title + "  in " + e.Record.Document));
            }
        var facts = ov.Overlay.Documents.Select((d, i) => new OverviewFact("File " + (i + 1).ToString(Inv), d)).ToList();
        return new OverviewContent("ASSET OVERLAY", AssetTree.OverlayLabel,
            "the asset overlay of the case  -  " + OverlayIndex.NotUsedBySolver, tiles, facts, "RECORDS BY KIND", rows, Capped(total),
            OverlayIndex.SolverNote, false);
    }

    // ---- one kind's page ----
    private static OverviewContent Kind(string kind, OverlayIndex ov)
    {
        OverlayKindGroup? g = ov.ByKind.FirstOrDefault(k => string.Equals(k.Kind, kind, StringComparison.Ordinal));
        if (g is null) return Missing("OVERLAY KIND", kind, "no record of this kind is in the overlay");
        var tiles = new[]
        {
            new OverviewTile("RECORDS", Count(g.Records.Count), "of this kind"),
            new OverviewTile("ATTACHED", Count(g.Records.Count(e => e.Attachments.Count > 0)), "tied to an asset of the model"),
        };
        var facts = new[] { new OverviewFact("Kind", g.Kind), new OverviewFact("Description", g.Description) };
        var rows = g.Records.Take(OverviewBuilder.RowCap)
            .Select(e => RecordRow(e, (e.Parent is null ? "top level" : "under " + e.Parent.Kind + " '" + e.Parent.Id + "'") + "  in " + e.Record.Document))
            .ToList();
        return new OverviewContent("OVERLAY KIND", g.Title, g.Kind + "  -  " + OverlayIndex.NotUsedBySolver, tiles, facts,
            "RECORDS", rows, Capped(g.Records.Count), OverlayIndex.SolverNote, false);
    }

    // ---- one record's page: kind, params with units, prov, note, parent, children, refs as links ----
    private static OverviewContent Record(AssetPageKey key, OverlayIndex ov, SimFrame? frame)
    {
        OverlayEntry? e = key.Name is null ? null : ov.Find(key.Name);
        if (e is null || e.Ordinal != key.Index) return Missing("OVERLAY RECORD", key.Name ?? "", "this overlay record is not in the overlay");
        AssetOverlayRecord r = e.Record;
        var facts = new List<OverviewFact>
        {
            new("Kind", r.Kind + "  (" + e.KindTitle + ")"),
            new("Written in", r.Document + "  " + r.Pointer),
            new("Parent", e.Parent is null ? (r.Parent is null ? "none (a top-level record)" : "'" + r.Parent + "' (not in the overlay)") : e.Parent.Kind + " '" + e.Parent.Id + "'"),
            new("Children", Count(e.Children.Count)),
            new("Provenance", r.Prov is JsonElement pv ? ProvText(pv) : "none given"),
            new("Note", r.Note ?? "none"),
        };
        foreach ((string name, string value) in Params(r))
            facts.Add(new OverviewFact("Parameter " + name, value));
        if (e.Problems.Count > 0)
            facts.Add(new OverviewFact("Checker", Count(e.Problems.Count) + (e.Problems.Count == 1 ? " problem (see CHECK)" : " problems (see CHECK)")));
        // Phase 5c: a substation's topology: what it holds, the nodes its sections form under the normal switch states,
        // and every implied split or coupling warning, in full.
        if (e.Topology is { } topo)
        {
            facts.Add(new OverviewFact("Topology", TopologySummary(topo)));
            if (topo.Sections.Count > 0) facts.Add(new OverviewFact("Normal-state nodes", OverlaySubstationDiagram.NodesText(topo)));
            foreach (SubstationTopologyWarning w in topo.Warnings) facts.Add(new OverviewFact(TopologyWarningLabel, w.Message));
        }

        var rows = new List<OverviewRow>();
        if (e.Parent is { } p) rows.Add(new OverviewRow(p.Id, "parent  " + p.KindTitle, p.Kind, p.Key));
        foreach (OverlayEntry c in e.Children) rows.Add(new OverviewRow(c.Id, "child  " + c.KindTitle, c.Kind, c.Key));
        foreach (OverlayRefLink link in e.Refs)
            rows.Add(new OverviewRow(link.Written, "reference '" + link.Role + "':  " + link.Resolved, link.IsModelAsset ? "model" : "record", link.Target));
        // A record with no reference of its own hangs under the buses its children point at: say so, with the link.
        if (!e.Refs.Any(l => l.IsModelAsset))
            foreach (OverlayAttachment a in e.Attachments)
                rows.Add(new OverviewRow(a.Asset.Display, "attached: " + a.How, "model", a.Asset));

        // Phase 5e onwards: the kind-specific part (tables with units, the records and assets it relates to), after the
        // generic part, so every fact and row above stays exactly where it was.
        OverlayKindContent kind = OverlayKindPages.For(e, ov, frame);
        facts.AddRange(kind.Facts);
        rows.AddRange(kind.Rows);
        return new OverviewContent("OVERLAY RECORD", r.Id, e.KindTitle + "  -  " + OverlayIndex.NotUsedBySolver,
            Array.Empty<OverviewTile>(), facts, "PARENT, CHILDREN AND REFERENCES", rows, "", OverlayIndex.SolverNote, false)
        {
            Tables = kind.Tables,
        };
    }

    /// <summary>
    /// Every parameter of a record as the OVERVIEW shows it: the value as written (a string without its quotes), the unit
    /// (the one written in the {v, unit} form, else the kind spec's; none for a dimensionless "1"), and the value's own
    /// provenance when it gives one. In the order the record writes them.
    /// </summary>
    public static IReadOnlyList<(string Name, string Value)> Params(AssetOverlayRecord record)
    {
        ArgumentNullException.ThrowIfNull(record);
        var list = new List<(string, string)>();
        if (record.Params.ValueKind != JsonValueKind.Object) return list;
        KindSpec? spec = OverlayIndex.Kinds.Parameters.Find(record.Kind);
        foreach (JsonProperty p in record.Params.EnumerateObject())
        {
            JsonElement v = p.Value;
            string? unit = spec?.Params.GetValueOrDefault(p.Name)?.Unit;
            string prov = "";
            if (v.ValueKind == JsonValueKind.Object && v.TryGetProperty("v", out JsonElement inner))
            {
                if (v.TryGetProperty("unit", out JsonElement u) && u.ValueKind == JsonValueKind.String) unit = u.GetString();
                if (v.TryGetProperty("prov", out JsonElement pv)) prov = "  (provenance " + ProvText(pv) + ")";
                v = inner;
            }
            string text = v.ValueKind == JsonValueKind.String ? v.GetString()! : v.GetRawText();
            string unitText = unit is null or "1" ? "" : " " + unit;
            list.Add((p.Name, text + unitText + prov));
        }
        return list;
    }

    // A provenance object as words: "Synthetic: pin fixture" (tag, then note), or its JSON when it is not that shape.
    private static string ProvText(JsonElement pv)
    {
        if (pv.ValueKind == JsonValueKind.Object && pv.TryGetProperty("tag", out JsonElement t) && t.ValueKind == JsonValueKind.String)
            return t.GetString() + (pv.TryGetProperty("note", out JsonElement n) && n.ValueKind == JsonValueKind.String ? ": " + n.GetString() : "");
        return pv.GetRawText();
    }

    // ===================================================================================================================
    // The sub-tabs
    // ===================================================================================================================

    /// <summary>
    /// The OVERLAY sub-tab of model asset <paramref name="asset"/>: every overlay record attached to it (by one of its
    /// own references, or through a child that points at it), in overlay order. Null when none is.
    /// </summary>
    public static AssetOverlayList? AttachedList(OverlayIndex ov, AssetKey asset)
    {
        ArgumentNullException.ThrowIfNull(ov);
        ArgumentNullException.ThrowIfNull(asset);
        IReadOnlyList<OverlayEntry> attached = ov.AttachedTo(asset);
        if (attached.Count == 0) return null;
        var rows = new List<ConnectionRow>(attached.Count);
        foreach (OverlayEntry e in attached)
        {
            string how = e.Attachments.First(a => a.Asset == asset).How;
            rows.Add(new ConnectionRow(AssetOverlayList.Section, how, e.Id, e.KindTitle, e.Key));
        }
        string heading = attached.Count == 1 ? "1 overlay record attached to " + asset.Display
            : Count(attached.Count) + " overlay records attached to " + asset.Display;
        return new AssetOverlayList(heading, OverlayIndex.SolverNote, rows);
    }

    /// <summary>
    /// The PROPERTIES sub-tab of an overlay record (any of its nodes): the record as written, read-only, one row per
    /// member: id, kind, parent; each reference role the kind declares (absent ones say whether the kind requires them)
    /// and any other written; each parameter of the kind spec (with its unit and help; absent ones say whether they are
    /// required) and any other written; prov; note. Null when the key is not an overlay record of this overlay.
    /// Literals are the file's JSON text; a reference object is shown in its one-member form (a bus id as a whole number).
    /// </summary>
    public static OverlayRecordProperties? Properties(OverlayIndex ov, AssetKey key)
    {
        ArgumentNullException.ThrowIfNull(ov);
        ArgumentNullException.ThrowIfNull(key);
        if (ov.EntryOf(key) is not OverlayEntry e) return null;
        AssetOverlayRecord r = e.Record;
        KindSpec? spec = OverlayIndex.Kinds.Parameters.Find(r.Kind);
        OverlayKindSpec? overlaySpec = OverlayIndex.Kinds.Find(r.Kind);
        var rows = new List<PropertyRow>
        {
            new("id", "Record id", "", "The record's id, unique across every overlay file of the case.", "String", true, true, Quote(r.Id), "required", "", ""),
            new("kind", "Kind", "", e.KindTitle + ": " + OverlayIndex.DescriptionOf(r.Kind), "String", true, true, Quote(r.Kind), "required", "", ""),
            new("parent", "Parent", "", ParentHelp(overlaySpec), "String", overlaySpec?.ParentRequired ?? false, r.Parent is not null,
                r.Parent is null ? "" : Quote(r.Parent), overlaySpec?.ParentRequired == true ? "required" : "none (a top-level record)", "", ""),
        };

        // References: every role the kind declares, in its order, then any other the record writes.
        // (First of a repeated role: a repeat is a checker problem, shown on CHECK; the rows still show every member.)
        var written = new Dictionary<string, AssetOverlayRef>(StringComparer.Ordinal);
        foreach (AssetOverlayRef x in r.Refs) written.TryAdd(x.Role, x);
        var shown = new HashSet<string>(StringComparer.Ordinal);
        foreach (OverlayRefSpec role in overlaySpec?.Refs.Values ?? Enumerable.Empty<OverlayRefSpec>())
        {
            shown.Add(role.Role);
            written.TryGetValue(role.Role, out AssetOverlayRef? w);
            rows.Add(new PropertyRow("refs/" + role.Role, "Reference " + role.Role, OverlayKindSet.TargetWord(role.Target), role.Description,
                "Reference", role.Required, w is not null, w is null ? "" : RefLiteral(w), role.Required ? "required" : "none", "", ""));
        }
        foreach (AssetOverlayRef w in r.Refs.Where(x => !shown.Contains(x.Role)))
            rows.Add(new PropertyRow("refs/" + w.Role, "Reference " + w.Role, OverlayKindSet.TargetWord(w.Target), "Not a role of this kind.",
                "Unknown", false, true, RefLiteral(w), "none", "", ""));

        // Parameters: every one the kind spec declares, in its order, then any other the record writes.
        var paramShown = new HashSet<string>(StringComparer.Ordinal);
        bool hasParams = r.Params.ValueKind == JsonValueKind.Object;
        foreach (KindParamSpec ps in spec?.Params.Values ?? Enumerable.Empty<KindParamSpec>())
        {
            paramShown.Add(ps.Name);
            bool present = hasParams && r.Params.TryGetProperty(ps.Name, out _);
            rows.Add(new PropertyRow("params/" + ps.Name, ps.Name, ps.Unit is null or "1" ? "" : ps.Unit, ps.Description, ps.Type.ToString(),
                ps.Required, present, present ? r.Params.GetProperty(ps.Name).GetRawText() : "", ps.Required ? "required" : "none", "", ""));
        }
        if (hasParams)
            foreach (JsonProperty p in r.Params.EnumerateObject().Where(p => !paramShown.Contains(p.Name)))
                rows.Add(new PropertyRow("params/" + p.Name, p.Name, "", "Not a parameter of this kind.", "Unknown", false, true, p.Value.GetRawText(), "none", "", ""));

        rows.Add(new PropertyRow("prov", "Provenance", "", "The record's own provenance (tag and note).", "Object", false, r.Prov is not null,
            r.Prov is JsonElement pv ? pv.GetRawText() : "", "none", "", ""));
        rows.Add(new PropertyRow("note", "Note", "", "A free-text note on the record.", "String", false, r.Note is not null,
            r.Note is null ? "" : Quote(r.Note), "none", "", ""));
        return new OverlayRecordProperties(r.Id + "  (" + r.Document + " " + r.Pointer + ")", r.Kind + ": " + e.KindTitle, OverlayIndex.SolverNote, rows);
    }

    private static string ParentHelp(OverlayKindSpec? spec) => spec is null || spec.ParentKinds.Count == 0
        ? "The record this one sits under. This kind sits at the top level."
        : "The record this one sits under (a " + string.Join(" or ", spec.ParentKinds) + " record).";

    // The one-member reference object: { "bus": 3 } or { "line": "L1-2" }.
    private static string RefLiteral(AssetOverlayRef r) => "{ " + Quote(OverlayKindSet.TargetWord(r.Target)) + ": "
        + (r.Target == AssetRefTarget.Bus ? (r.Bus ?? 0).ToString(Inv) : Quote(r.Key ?? "")) + " }";

    /// <summary>The fact label of a topology warning on a substation's OVERVIEW.</summary>
    public const string TopologyWarningLabel = "Topology warning";

    /// <summary>"2 busbar sections, 7 switching devices, 6 bays" (Phase 5c, a substation's OVERVIEW).</summary>
    public static string TopologySummary(SubstationTopology t)
    {
        ArgumentNullException.ThrowIfNull(t);
        static string N(int n, string one, string many) => n.ToString(Inv) + " " + (n == 1 ? one : many);
        return N(t.Sections.Count, "busbar section", "busbar sections") + ", " + N(t.Devices.Count, "switching device", "switching devices")
            + ", " + N(t.Bays.Count, "bay", "bays");
    }

    /// <summary>The CHECK sub-tab of an overlay record: its checker problems, (Phase 5c) its topology warnings and (Phase
    /// 5f onwards) its consistency notes; null when it has none of them (the sub-tab is then hidden).</summary>
    public static OverlayCheck? Check(OverlayIndex ov, AssetKey key)
    {
        ArgumentNullException.ThrowIfNull(ov);
        ArgumentNullException.ThrowIfNull(key);
        if (ov.EntryOf(key) is not OverlayEntry e || (e.Problems.Count == 0 && e.Warnings.Count == 0 && e.Notes.Count == 0)) return null;
        // A record with problems keeps the heading it always had; warnings add a clause (or are the heading on their own).
        string heading = e.Problems.Count == 0 ? ""
            : (e.Problems.Count == 1 ? "1 problem" : Count(e.Problems.Count) + " problems") + " in " + e.Record.Document
              + " (the overlay checker, V2AssetChecker)";
        if (e.Warnings.Count > 0)
            heading += (heading.Length > 0 ? "; " : "") + (e.Warnings.Count == 1 ? "1 topology warning" : Count(e.Warnings.Count) + " topology warnings")
                + " in " + e.Record.Document + " (the case still loads and solves exactly as written)";
        // Phase 5f onwards: the consistency notes add a clause of their own (or are the heading on their own).
        if (e.Notes.Count > 0)
            heading += (heading.Length > 0 ? "; " : "") + (e.Notes.Count == 1 ? "1 consistency note" : Count(e.Notes.Count) + " consistency notes")
                + " on " + e.Record.Kind + " '" + e.Id + "' (display only, never an error)";
        return new OverlayCheck(heading, OverlayIndex.SolverNote, e.Problems.Select(p => new OverlayCheckRow(p.Pointer, p.Message)).ToList())
        {
            Warnings = e.Warnings.Select(w => new OverlayCheckRow(w.Pointer, w.Message)).ToList(),
            Notes = e.Notes,
        };
    }
}
