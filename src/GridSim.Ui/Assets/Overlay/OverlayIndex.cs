using System.Globalization;
using GridSim.Core.IO.Json.CaseEditing;
using GridSim.Core.IO.Json.Overlay;
using GridSim.Core.IO.Json.Schema;
using GridSim.Core.Model;
using GridSim.Ui.Assets.Index;
using GridSim.Ui.Assets.Keys;
using GridSim.Ui.Assets.Navigation;

namespace GridSim.Ui.Assets.Overlay;

// =====================================================================================================================
// UI programme ASSETS slice "5-view" (plan sections 3.3 Assets/Overlay, 4.1 and question Q12; Run mode, read-only).
//
// The asset overlay of a v2 case (gridsim-assets/2, Phase 5a) is CARRIED BESIDE THE MODEL AND NOT USED BY THE SOLVER:
// the v2 loader checks it and hands it back on its load result, and nothing in the solver ever reads it (the 5a pin and
// guard tests). This file is the ASSETS tab's index over it: the parent / child tree of the records, every reference
// resolved to the AssetKey of the tree node it names (a bus, line, transformer, generator or demand of the running
// model, or another overlay record), the model assets each record attaches to (so the tree can nest the record under
// them, Q12), the records grouped by kind (the by-kind view, Q12), and the V2AssetChecker problems of each record.
//
// Read-only and one way: the index reads the overlay and the asset index; it never changes either, and no model value
// on any page depends on it (UiAssetOverlayTests pins every A2/B2 page identical with and without an overlay).
// =====================================================================================================================

/// <summary>
/// One reference of an overlay record, resolved for display: the role, the reference as the document wrote it, what it
/// is in the running case, and the tree node a click selects.
/// </summary>
/// <param name="Role">The role name (for example <c>bus</c>, <c>from</c>).</param>
/// <param name="Written">The reference as written, in the overlay's own words ("bus 3", "line 'L1-2'", "record 'ov.bb1'").</param>
/// <param name="Resolved">What it names in the running case ("bus 3 'Load132'", "not a line of the running model").</param>
/// <param name="Target">The node a click selects (the canonical key of the asset or record), or null when it names none.</param>
/// <param name="IsModelAsset">True when the reference points at an element of the model (bus, line, transformer,
/// generator or demand), false for a reference to another overlay record.</param>
public sealed record OverlayRefLink(string Role, string Written, string Resolved, AssetKey? Target, bool IsModelAsset);

/// <summary>A model asset an overlay record is attached to, and how ("role 'bus' points here").</summary>
/// <param name="Asset">The key of the model asset's tree node.</param>
/// <param name="How">Why the record belongs to it, in words.</param>
public sealed record OverlayAttachment(AssetKey Asset, string How);

/// <summary>
/// One overlay record as the ASSETS tab shows it: the record exactly as carried, its canonical key and page, its place in
/// the parent / child tree, its resolved references, the model assets it attaches to and its checker problems.
/// </summary>
public sealed class OverlayEntry
{
    private readonly List<OverlayEntry> _children = new();

    internal OverlayEntry(AssetOverlayRecord record, int ordinal, string kindTitle)
    {
        Record = record;
        Ordinal = ordinal;
        KindTitle = kindTitle;
        Key = OverlayIndex.KeyOf(record.Id);
        Page = OverlayIndex.PageOf(ordinal, record.Id);
    }

    /// <summary>The record, exactly as the overlay carries it.</summary>
    public AssetOverlayRecord Record { get; }

    /// <summary>The record's position in the overlay (file by file in manifest order, document order within a file).</summary>
    public int Ordinal { get; }

    /// <summary>The record id.</summary>
    public string Id => Record.Id;

    /// <summary>The overlay kind.</summary>
    public string Kind => Record.Kind;

    /// <summary>The kind spec's title without its "(asset overlay)" tag ("Substation", "Busbar section").</summary>
    public string KindTitle { get; }

    /// <summary>The record's canonical key (its node under OVERLAY, by kind).</summary>
    public AssetKey Key { get; }

    /// <summary>The page the record's nodes open.</summary>
    public AssetPageKey Page { get; }

    /// <summary>The parent record, or null for a top-level record (or a parent the overlay does not hold).</summary>
    public OverlayEntry? Parent { get; internal set; }

    /// <summary>The child records, in overlay order.</summary>
    public IReadOnlyList<OverlayEntry> Children => _children;

    /// <summary>The references, in the order the document wrote them, each resolved.</summary>
    public IReadOnlyList<OverlayRefLink> Refs { get; internal set; } = Array.Empty<OverlayRefLink>();

    /// <summary>The model assets the record attaches to (its own references first; a record with none of its own
    /// attaches to the buses its descendants point at), each once, in first-seen order.</summary>
    public IReadOnlyList<OverlayAttachment> Attachments { get; internal set; } = Array.Empty<OverlayAttachment>();

    /// <summary>The checker's problems for this record (V2AssetChecker), in the order it reported them.</summary>
    public IReadOnlyList<SchemaError> Problems { get; internal set; } = Array.Empty<SchemaError>();

    /// <summary>Phase 5c: the topology of a substation record (its sections, devices, bays and normal-state nodes), or
    /// null for a record of any other kind.</summary>
    public SubstationTopology? Topology { get; internal set; }

    /// <summary>Phase 5c: the topology warnings of a substation record (implied bus split or coupling; never errors:
    /// the case still loads and solves exactly as written), empty for any other record.</summary>
    public IReadOnlyList<SubstationTopologyWarning> Warnings => Topology?.Warnings ?? Array.Empty<SubstationTopologyWarning>();

    /// <summary>Phase 5f onwards: the record's kind-specific consistency notes (display only, never errors: the
    /// checker's problems are unchanged and the case still loads and solves exactly as written), empty when none.</summary>
    public IReadOnlyList<OverlayCheckRow> Notes { get; internal set; } = Array.Empty<OverlayCheckRow>();

    internal void AddChild(OverlayEntry child) => _children.Add(child);

    /// <inheritdoc />
    public override string ToString() => Kind + " '" + Id + "'";
}

/// <summary>The records of one overlay kind (the by-kind view).</summary>
/// <param name="Kind">The kind name.</param>
/// <param name="Title">The kind spec's title without its "(asset overlay)" tag.</param>
/// <param name="Description">The kind spec's description.</param>
/// <param name="Records">The records of that kind, in overlay order.</param>
public sealed record OverlayKindGroup(string Kind, string Title, string Description, IReadOnlyList<OverlayEntry> Records);

/// <summary>
/// The ASSETS tab's index over the case's asset overlay (slice 5-view, read-only). Built once per asset index (the model
/// and the case it was read from), from the <see cref="CaseAssetOverlay"/> the v2 loader carried and the checker
/// problems the caller has for it (none for an overlay the loader accepted: it refuses a case whose overlay fails).
/// </summary>
public sealed class OverlayIndex
{
    /// <summary>The words every overlay page carries, plainly.</summary>
    public const string NotUsedBySolver = "carried, not used by the solver";

    /// <summary>The note every overlay page shows (it starts with <see cref="NotUsedBySolver"/>, capitalised).</summary>
    public const string SolverNote =
        "Carried, not used by the solver: the asset overlay is read from the case's overlay files and shown here as written. "
        + "The solver never reads it, so no value on any model page depends on it; the authored buses, lines and "
        + "transformers stay the electrical truth.";

    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    // The overlay kind specs (titles, descriptions, parameter units and help, reference roles), read once from the copies
    // embedded in GridSim.Core: the same files the v2 loader the save gate runs checks overlays against.
    private static readonly Lazy<OverlayKindSet> EmbeddedKinds = new(() =>
        OverlayKindSet.FromSources(BundledSchemas.Sources(BundledSchemas.V2OverlayKindsPrefix), BundledSchemas.V2()));

    private readonly Dictionary<string, OverlayEntry> _byId = new(StringComparer.Ordinal);
    private readonly Dictionary<AssetKey, List<OverlayEntry>> _attached = new();

    private OverlayIndex(CaseAssetOverlay overlay, AssetIndex assets)
    {
        Overlay = overlay;
        Assets = assets;
    }

    /// <summary>The overlay kind specs (embedded in GridSim.Core).</summary>
    public static OverlayKindSet Kinds => EmbeddedKinds.Value;

    /// <summary>The overlay, as the loader carried it.</summary>
    public CaseAssetOverlay Overlay { get; }

    /// <summary>The asset index the references were resolved against.</summary>
    public AssetIndex Assets { get; }

    /// <summary>Every record, in overlay order.</summary>
    public IReadOnlyList<OverlayEntry> Entries { get; private set; } = Array.Empty<OverlayEntry>();

    /// <summary>The records grouped by kind, kinds in ordinal order of their names, records in overlay order.</summary>
    public IReadOnlyList<OverlayKindGroup> ByKind { get; private set; } = Array.Empty<OverlayKindGroup>();

    /// <summary>Every model asset some record attaches to, each once, in the order first seen.</summary>
    public IReadOnlyList<AssetKey> AttachedAssets { get; private set; } = Array.Empty<AssetKey>();

    /// <summary>Phase 5c: every substation topology warning, substation by substation in overlay order.</summary>
    public IReadOnlyList<SubstationTopologyWarning> Warnings { get; private set; } = Array.Empty<SubstationTopologyWarning>();

    /// <summary>Checker problems that name no record of the overlay (a problem of a file as a whole).</summary>
    public IReadOnlyList<SchemaError> UnplacedProblems { get; private set; } = Array.Empty<SchemaError>();

    /// <summary>True when the overlay holds at least one record.</summary>
    public bool HasRecords => Entries.Count > 0;

    /// <summary>The canonical key of overlay record <paramref name="id"/> (its node under OVERLAY).</summary>
    public static AssetKey KeyOf(string id) => AssetKey.ForOverlay(new OverlayRef(id));

    /// <summary>The page of overlay record <paramref name="id"/> at overlay position <paramref name="ordinal"/>.</summary>
    public static AssetPageKey PageOf(int ordinal, string id) => new(AssetPageKind.OverlayRecord, ordinal, 0, 0, id);

    /// <summary>The page of the OVERLAY class node (every record, by kind).</summary>
    public static AssetPageKey FleetPage { get; } = new(AssetPageKind.OverlayFleet);

    /// <summary>The page of one kind's node in the by-kind view.</summary>
    public static AssetPageKey KindPage(string kind) => new(AssetPageKind.OverlayKind, -1, 0, 0, kind);

    /// <summary>The record with this id, or null.</summary>
    public OverlayEntry? Find(string id) => _byId.GetValueOrDefault(id);

    /// <summary>The record a key names (any node of it: its canonical node or one of its attachment nodes), or null when
    /// the key is not an overlay record of this overlay.</summary>
    public OverlayEntry? EntryOf(AssetKey key)
    {
        ArgumentNullException.ThrowIfNull(key);
        return key.Kind == AssetKeyKind.OverlayRecord && key.Overlay is { } r ? Find(r.RecordId) : null;
    }

    /// <summary>Every record attached to model asset <paramref name="asset"/>, in overlay order (empty when none).</summary>
    public IReadOnlyList<OverlayEntry> AttachedTo(AssetKey asset)
    {
        ArgumentNullException.ThrowIfNull(asset);
        return _attached.TryGetValue(asset, out List<OverlayEntry>? list) ? list : Array.Empty<OverlayEntry>();
    }

    /// <summary>
    /// The records the tree nests directly under model asset <paramref name="asset"/>: those attached to it whose parent
    /// is not attached to it too (a record whose parent is attached shows under its parent, with the rest of the
    /// parent's subtree).
    /// </summary>
    public IReadOnlyList<OverlayEntry> RootsUnder(AssetKey asset)
    {
        IReadOnlyList<OverlayEntry> all = AttachedTo(asset);
        if (all.Count == 0) return all;
        var set = new HashSet<OverlayEntry>(all);
        return all.Where(e => !HasAncestorIn(e, set)).ToList();
    }

    private static bool HasAncestorIn(OverlayEntry e, HashSet<OverlayEntry> set)
    {
        var seen = new HashSet<OverlayEntry>();
        for (OverlayEntry? p = e.Parent; p is not null && seen.Add(p); p = p.Parent)
            if (set.Contains(p)) return true;
        return false;
    }

    /// <summary>
    /// Build the index of <paramref name="overlay"/> over <paramref name="assets"/> (the running model and its case).
    /// <paramref name="problems"/> are V2AssetChecker results for the same overlay files (empty or null for an overlay
    /// the loader accepted); each is placed on the record whose JSON pointer it falls under, in its file.
    /// Throws when a parent chain loops (the checker refuses that, so it is an internal error here).
    /// </summary>
    public static OverlayIndex Build(CaseAssetOverlay overlay, AssetIndex assets, IReadOnlyList<SchemaError>? problems = null)
    {
        ArgumentNullException.ThrowIfNull(overlay);
        ArgumentNullException.ThrowIfNull(assets);
        var ix = new OverlayIndex(overlay, assets);

        // ---- the entries, in overlay order ----
        var entries = new List<OverlayEntry>(overlay.Records.Count);
        for (int i = 0; i < overlay.Records.Count; i++)
        {
            AssetOverlayRecord r = overlay.Records[i];
            var e = new OverlayEntry(r, i, TitleOf(r.Kind));
            if (!ix._byId.TryAdd(r.Id, e))
                throw new InvalidOperationException($"overlay record '{r.Id}' appears twice (the checker refuses a repeated id; internal error).");
            entries.Add(e);
        }
        ix.Entries = entries;

        // ---- the parent / child tree (a parent the overlay does not hold leaves the record at the top) ----
        foreach (OverlayEntry e in entries)
            if (e.Record.Parent is string p && ix._byId.TryGetValue(p, out OverlayEntry? parent))
            {
                e.Parent = parent;
                parent.AddChild(e);
            }
        foreach (OverlayEntry e in entries)
        {
            var seen = new HashSet<OverlayEntry> { e };
            for (OverlayEntry? p = e.Parent; p is not null; p = p.Parent)
                if (!seen.Add(p))
                    throw new InvalidOperationException($"overlay record '{e.Id}': its parent chain loops (the checker refuses that; internal error).");
        }

        // ---- references, resolved against the running model, its case and the overlay itself ----
        foreach (OverlayEntry e in entries) e.Refs = e.Record.Refs.Select(r => ix.Resolve(r)).ToList();

        // ---- attachments: a record's own model references; a record with none attaches to its descendants' buses ----
        foreach (OverlayEntry e in entries)
        {
            var list = new List<OverlayAttachment>();
            foreach (OverlayRefLink link in e.Refs)
                if (link.IsModelAsset && link.Target is AssetKey k && list.All(a => a.Asset != k))
                    list.Add(new OverlayAttachment(k, "role '" + link.Role + "' points here"));
            if (list.Count == 0)
                foreach (OverlayEntry d in Descendants(e))
                    foreach (OverlayRefLink link in d.Refs)
                        if (link.IsModelAsset && link.Target is AssetKey k && k.Kind == AssetKeyKind.Substation && list.All(a => a.Asset != k))
                            list.Add(new OverlayAttachment(k, "holds " + d.Kind + " '" + d.Id + "', whose role '" + link.Role + "' points here"));
            e.Attachments = list;
        }
        var assetsSeen = new List<AssetKey>();
        foreach (OverlayEntry e in entries)
            foreach (OverlayAttachment a in e.Attachments)
            {
                if (!ix._attached.TryGetValue(a.Asset, out List<OverlayEntry>? list))
                {
                    ix._attached[a.Asset] = list = new List<OverlayEntry>();
                    assetsSeen.Add(a.Asset);
                }
                list.Add(e);
            }
        ix.AttachedAssets = assetsSeen;

        // ---- Phase 5c: the topology of each substation record, and its warnings ----
        foreach (OverlayEntry e in entries)
            if (e.Kind == "substation") e.Topology = SubstationTopology.Build(e.Record, overlay.Records);
        ix.Warnings = entries.SelectMany(e => e.Warnings).ToList();

        // ---- Phase 5f onwards: each record's kind-specific consistency notes (display only) ----
        foreach (OverlayEntry e in entries) e.Notes = OverlayKindPages.Notes(e, ix);

        // ---- the by-kind view ----
        ix.ByKind = entries.GroupBy(e => e.Kind, StringComparer.Ordinal)
            .OrderBy(g => g.Key, StringComparer.Ordinal)
            .Select(g => new OverlayKindGroup(g.Key, TitleOf(g.Key), DescriptionOf(g.Key), g.ToList()))
            .ToList();

        // ---- checker problems, each on the record whose pointer it falls under ----
        var perRecord = new Dictionary<OverlayEntry, List<SchemaError>>();
        var unplaced = new List<SchemaError>();
        foreach (SchemaError p in problems ?? Array.Empty<SchemaError>())
        {
            OverlayEntry? owner = entries.FirstOrDefault(e => SameFile(p.File, e.Record.Document, overlay.Documents.Count)
                && (p.Pointer == e.Record.Pointer || p.Pointer.StartsWith(e.Record.Pointer + "/", StringComparison.Ordinal)));
            if (owner is null) { unplaced.Add(p); continue; }
            if (!perRecord.TryGetValue(owner, out List<SchemaError>? list)) perRecord[owner] = list = new List<SchemaError>();
            list.Add(p);
        }
        foreach ((OverlayEntry e, List<SchemaError> list) in perRecord) e.Problems = list;
        ix.UnplacedProblems = unplaced;
        return ix;
    }

    // A problem belongs to a file when it names that file; a problem naming no file belongs to the only file there is.
    private static bool SameFile(string? problemFile, string document, int documentCount) =>
        problemFile is null ? documentCount == 1 : string.Equals(problemFile, document, StringComparison.Ordinal);

    // Every record under e, depth first in overlay order (a loop was refused above, so this ends).
    private static IEnumerable<OverlayEntry> Descendants(OverlayEntry e)
    {
        foreach (OverlayEntry c in e.Children)
        {
            yield return c;
            foreach (OverlayEntry d in Descendants(c)) yield return d;
        }
    }

    /// <summary>The kind spec's title without its "(asset overlay)" tag, or the kind name when the kind is unknown.</summary>
    public static string TitleOf(string kind)
    {
        ArgumentNullException.ThrowIfNull(kind);
        string? t = Kinds.Parameters.Find(kind)?.Title;
        if (t is null) return kind;
        const string tag = " (asset overlay)";
        return t.EndsWith(tag, StringComparison.Ordinal) ? t[..^tag.Length] : t;
    }

    /// <summary>The kind spec's description, or "" when the kind is unknown.</summary>
    public static string DescriptionOf(string kind)
    {
        ArgumentNullException.ThrowIfNull(kind);
        return Kinds.Parameters.Find(kind)?.Description ?? "";
    }

    // ===================================================================================================================
    // Reference resolution
    // ===================================================================================================================

    // One reference, resolved to the node of what it names. A model element is found by the rule the overlay checker
    // uses on the case's network files (a bus by id; a line, transformer, generator or demand by a name exactly one
    // element of that type carries), here against the running model and its case; a name the model carries more than
    // once, or not at all, gives no target and says why.
    private OverlayRefLink Resolve(AssetOverlayRef r)
    {
        GridModel m = Assets.Model;
        string written = r.Display;
        switch (r.Target)
        {
            case AssetRefTarget.Bus:
            {
                long id = r.Bus ?? long.MinValue;
                if (id is >= int.MinValue and <= int.MaxValue && Assets.BusIndexOf((int)id) is int bi and >= 0)
                {
                    string name = m.Buses[bi].Name is string n ? " '" + n + "'" : "";
                    return new OverlayRefLink(r.Role, written, "bus " + id.ToString(Inv) + name, AssetKey.Substation((int)id), true);
                }
                return new OverlayRefLink(r.Role, written, "not a bus of the running model", null, true);
            }
            case AssetRefTarget.Line:
            case AssetRefTarget.Transformer:
            {
                bool tx = r.Target == AssetRefTarget.Transformer;
                string word = tx ? "transformer" : "line";
                var hits = new List<int>();
                for (int i = 0; i < m.Branches.Count; i++)
                    if (m.Branches[i].IsTransformer == tx && string.Equals(m.Branches[i].Name, r.Key, StringComparison.Ordinal)) hits.Add(i);
                if (hits.Count == 1)
                {
                    Branch b = m.Branches[hits[0]];
                    return new OverlayRefLink(r.Role, written, string.Create(Inv, $"{word} #{hits[0]}  {b.From} -> {b.To}"), AssetKey.Branch(m, hits[0]), true);
                }
                return new OverlayRefLink(r.Role, written, Unresolved(word, hits.Count), null, true);
            }
            case AssetRefTarget.Generator:
            {
                var hits = new List<int>();
                for (int i = 0; i < m.Generators.Count; i++)
                    if (string.Equals(m.Generators[i].Name, r.Key, StringComparison.Ordinal)) hits.Add(i);
                var off = new List<int>();
                for (int k = 0; k < m.AllGenerators.Count; k++)
                    if (!m.AllGenerators[k].InService && string.Equals(m.AllGenerators[k].Name, r.Key, StringComparison.Ordinal)) off.Add(k);
                if (hits.Count + off.Count == 1)
                {
                    if (hits.Count == 1)
                    {
                        Generator g = m.Generators[hits[0]];
                        bool link = AssetIndex.IsLink(g);
                        return new OverlayRefLink(r.Role, written, string.Create(Inv, $"{(link ? "interconnector" : "generator")} #{hits[0]}  bus {g.Bus}"),
                            AssetKey.Unit(m, hits[0], link), true);
                    }
                    Generator o = m.AllGenerators[off[0]];
                    return new OverlayRefLink(r.Role, written, string.Create(Inv, $"out-of-service unit  bus {o.Bus}"), AssetKey.OutOfService(m, off[0]), true);
                }
                return new OverlayRefLink(r.Role, written, Unresolved("generator", hits.Count + off.Count), null, true);
            }
            case AssetRefTarget.Demand:
            {
                if (!Assets.HasCase)
                    return new OverlayRefLink(r.Role, written, "demand records are shown only when the case file is open (" + (Assets.CaseNote ?? "no case file") + ")", null, true);
                List<DemandRecord> hits = Assets.Demands.Where(d => string.Equals(d.Name, r.Key, StringComparison.Ordinal)).ToList();
                if (hits.Count == 1)
                    return new OverlayRefLink(r.Role, written, "demand record " + hits[0].Record.Display + "  bus " + hits[0].Bus.ToString(Inv),
                        AssetIndex.DemandKey(hits[0]), true);
                return new OverlayRefLink(r.Role, written, Unresolved("demand record", hits.Count), null, true);
            }
            default:
            {
                OverlayEntry? hit = r.Key is null ? null : Find(r.Key);
                return hit is null
                    ? new OverlayRefLink(r.Role, written, "not a record of the overlay", null, false)
                    : new OverlayRefLink(r.Role, written, hit.KindTitle + " '" + hit.Id + "'", hit.Key, false);
            }
        }
    }

    private static string Unresolved(string word, int count) => count == 0
        ? "not a " + word + " of the running model"
        : string.Create(Inv, $"ambiguous: {count} {word}s of the running model carry this name");
}
