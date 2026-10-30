using System.Globalization;
using GridSim.Core.IO.Json.CaseEditing;
using GridSim.Ui.Assets.Editing.Elements;
using GridSim.Ui.Assets.Editing.Templates;
using GridSim.Ui.Assets.Keys;
using GridSim.Ui.Assets.Navigation;
using GridSim.Ui.Assets.Overlay;
using GridSim.Ui.Commands;
using GridSim.Ui.Dashboard;

namespace GridSim.Ui.Assets.Editing;

// UI programme Phase 4.1: the ASSETS tab in AUTHOR mode (ASSETS plan section 3.5, "Author: no live values (no frame;
// nothing stale is shown as live, fail loud). OVERVIEW is replaced by the record summary; PROPERTIES is editable;
// Check = SaveGate.Run(CheckOnly:true); Save = SaveGate -> CaseCommitter; bundled cases are Save As only."). The same
// split layout as Run mode (Mark: "Really really like it": extended, not replaced): a tree on the left, sub-tabs over
// the page on the right. Here the tree is the CASE FILE's records (the authoring session's collections), not the solved
// model: Author mode reads no frame at all, so nothing on these pages can be a live value (plan Q5, answer (a)).
//   - Home: OVERVIEW is the case summary (counts per role, the files and which are dirty, the last Check); CHECK lists
//     every problem of the last Check.
//   - A class node (Buses, Generation, ...): OVERVIEW lists its records.
//   - A record: OVERVIEW is the record summary (where it is, its key, its text as written); PROPERTIES is the field grid
//     (editable); CONNECTIONS is the where-used list (shown only when something refers to it); CHECK is this record's
//     problems from the last Check (shown once a Check has run).
// A sub-tab with nothing to show is hidden, as in Run mode.
// Phase 5b (2026-10-20): a case with an asset overlay has an "Asset overlay" class node holding its records as a tree by
// parent. An overlay record's page is a record's page: PROPERTIES is the field grid from its kind spec, CONNECTIONS its
// children and the records pointing at it, CHECK its problems from the overlay checker. A model asset's CONNECTIONS also
// lists the overlay records that point at it. "Add overlay record..." adds a child of an overlay record, a record attached
// to a model asset, or (on the class node) a record of any kind; Duplicate, Delete and Rename act on overlay records too.

/// <summary>What a node of the Author tree stands for.</summary>
public enum AuthorNodeKind
{
    /// <summary>The case summary.</summary>
    Home,

    /// <summary>A document whose root is one record (a v1 system.json, a v2 case manifest).</summary>
    CaseFile,

    /// <summary>A collection of records (one role: Buses, Generation, ...).</summary>
    Class,

    /// <summary>One record.</summary>
    Record,

    // UI programme Phase 4.4 (2026-10-21): the templates of a v2 case (additive kinds; a v1 case and a lone network file
    // have none of these nodes, so their trees are exactly as before).

    /// <summary>The TEMPLATES class node: the case's own templates and the library templates it imports.</summary>
    TemplateClass,

    /// <summary>One template (a local template of the manifest, editable; or an imported library template, read only).</summary>
    Template,

    /// <summary>One override of a template parameter (under its template).</summary>
    TemplateOverride,
}

/// <summary>A node of the Author tree.</summary>
public sealed class AuthorNode : ObservableModel
{
    private bool _isExpanded;

    internal AuthorNode(string id, string label, string detail, AuthorNodeKind kind, string? role, CaseRef? record,
        IReadOnlyList<AuthoringRecordSet> collections, AuthorNode? parent, bool overlayAnchor = false, bool starTransformer = false)
    {
        OverlayAnchor = overlayAnchor;
        StarTransformer = starTransformer;
        Id = id;
        Label = label;
        Detail = detail;
        Kind = kind;
        Role = role;
        Record = record;
        Collections = collections;
        Parent = parent;
    }

    /// <summary>A stable id ("home", "class:buses", "rec:2:/buses:7"): the selection and the expansion are kept by it when
    /// the tree is rebuilt after a change.</summary>
    public string Id { get; }

    /// <summary>What the tree shows.</summary>
    public string Label { get; }

    /// <summary>A muted second text (a count, the file and position).</summary>
    public string Detail { get; }

    /// <summary>What the node stands for.</summary>
    public AuthorNodeKind Kind { get; }

    /// <summary>The engine role of a class or record node ("buses", ...), else null.</summary>
    public string? Role { get; }

    /// <summary>The record of a record or case-file node, else null.</summary>
    public CaseRef? Record { get; }

    /// <summary>The collections a class node stands for (v2: one per network file holding that role), else empty.</summary>
    public IReadOnlyList<AuthoringRecordSet> Collections { get; }

    /// <summary>The parent node (null for a top-level node).</summary>
    public AuthorNode? Parent { get; }

    /// <summary>The children (records under a class node).</summary>
    public List<AuthorNode> Children { get; } = new();

    /// <summary>Whether the node is open in the tree.</summary>
    public bool IsExpanded
    {
        get => _isExpanded;
        set => Set(ref _isExpanded, value);
    }

    /// <summary>The tree path ("Buses > bus 12 'Drax'").</summary>
    public string Path => Parent is null ? Label : Parent.Path + " > " + Label;

    /// <summary>The context-menu target kind of the node (plan section 3.5): a class node is
    /// <see cref="CommandTargets.AssetClassNode"/>, a record its role's kind, Home and a case file none.</summary>
    public CommandTargets Target => Kind switch
    {
        // Phase 5b: the "Asset overlay" class node is where an overlay record of any kind is added (not a model Add).
        AuthorNodeKind.Class => Role == CaseRoles.Overlay ? CommandTargets.OverlayAnchor : CommandTargets.AssetClassNode,
        AuthorNodeKind.Record => TargetOf(Role),
        // Phase 4.4: the template nodes have context menus of their own (Add template..., Duplicate, Delete, Bind to
        // template..., Reset override), apart from the record kinds', so no record's menu changes.
        AuthorNodeKind.TemplateClass => CommandTargets.TemplateClassNode,
        AuthorNodeKind.Template => CommandTargets.Template,
        AuthorNodeKind.TemplateOverride => CommandTargets.TemplateOverride,
        _ => CommandTargets.None,
    };

    /// <summary>Phase 4.4: the template reference of a template or override node (id@rev), else null.</summary>
    public string? TemplateRef { get; init; }

    /// <summary>Phase 4.4: the parameter of an override node, else null.</summary>
    public string? OverrideParam { get; init; }

    /// <summary>Phase 5b: true for a model asset an overlay record can be attached to (a bus, line, transformer, generator
    /// or demand record of a case with an overlay file).</summary>
    public bool OverlayAnchor { get; }

    /// <summary>Phase 5f follow-up: true for a transformer3W overlay record (its menu adds Derive star legs...).</summary>
    public bool StarTransformer { get; }

    /// <summary>
    /// The context-menu target kinds of the node, in order: its own <see cref="Target"/>, then (Phase 5b) the overlay
    /// anchor for a model asset an overlay record can be attached to, or (Phase 5f follow-up) the star-legs target for a
    /// transformer3W overlay record; the view shows one menu with the commands of each. Empty when the node has no
    /// context menu.
    /// </summary>
    public IReadOnlyList<CommandTargets> MenuTargets
        => Target == CommandTargets.None ? Array.Empty<CommandTargets>()
           : OverlayAnchor && Target != CommandTargets.OverlayAnchor ? new[] { Target, CommandTargets.OverlayAnchor }
           : StarTransformer && Target == CommandTargets.OverlayRecord ? new[] { Target, CommandTargets.StarTransformer }
           : new[] { Target };

    /// <summary>The context-menu target kind of a record of <paramref name="role"/>.</summary>
    public static CommandTargets TargetOf(string? role) => role switch
    {
        CaseRoles.Buses => CommandTargets.Bus,
        CaseRoles.Generators => CommandTargets.Generator,
        CaseRoles.Demands => CommandTargets.Demand,
        CaseRoles.Lines => CommandTargets.Branch,
        CaseRoles.Transformers => CommandTargets.Transformer,
        CaseRoles.Oltcs => CommandTargets.Oltc,
        CaseRoles.Boundaries => CommandTargets.Boundary,
        CaseRoles.Protections => CommandTargets.Protection,
        CaseRoles.AssetClasses => CommandTargets.AssetClass,
        CaseRoles.AssetBindings => CommandTargets.AssetBinding,
        CaseRoles.Overlay => CommandTargets.OverlayRecord,   // Phase 5b
        _ => CommandTargets.None,
    };

    /// <inheritdoc />
    public override string ToString() => Path;
}

/// <summary>A count of records of one role (the case summary).</summary>
public sealed record AuthorCount(string Title, int Count);

/// <summary>The Home page in Author mode: the case summary.</summary>
public sealed record AuthorSummary(
    string CaseName, string CasePath, string FormatText, string BundledNote, string OverlayNote, IReadOnlyList<AuthorCount> Counts,
    IReadOnlyList<AuthoringFile> Files, string DirtyText, string LastCheckText, string UndoText, string PreflightText)
{
    /// <summary>True when the bundled note has something to say.</summary>
    public bool HasBundledNote => BundledNote.Length > 0;

    /// <summary>True when the overlay note has something to say.</summary>
    public bool HasOverlayNote => OverlayNote.Length > 0;
}

/// <summary>A class node's OVERVIEW: its records (each row selects its node).</summary>
public sealed record AuthorClassPage(string Title, string Note, IReadOnlyList<AuthorNode> Rows);

/// <summary>A record's OVERVIEW in Author mode: the record summary (no live value).</summary>
public sealed record AuthorRecordPage(string Heading, string ShapeTitle, string Where, string KeyText, string StateText, string WrittenText)
{
    /// <summary>
    /// Phase 5c: for an overlay record that is a substation, or sits directly under one, that substation's topology view
    /// (its single-line diagram with the switch states as the session holds them now, and its implied split / coupling
    /// warnings); null for every other record. Rebuilt on every change, so toggling a device's normalOpen in PROPERTIES
    /// redraws the diagram and re-evaluates the warnings.
    /// </summary>
    public SubstationTopologyView? Topology { get; init; }

    /// <summary>True when the page shows a substation topology view.</summary>
    public bool HasTopology => Topology is not null;

    /// <summary>Phase 5f: true when the record is a transformer3W overlay record (the page offers Derive star legs).</summary>
    public bool CanDeriveStarLegs { get; init; }

    /// <summary>Phase 5f: the star-legs proposal made by Derive star legs for this record (the six leg edits old -> new,
    /// or why there are none), shown until it is applied or cancelled or anything changes; null before Derive.</summary>
    public StarLegsProposal? StarLegs { get; init; }

    /// <summary>True when a proposal is shown.</summary>
    public bool HasStarLegs => StarLegs is not null;

    /// <summary>True when the proposal shown can be applied (it has edits and was not refused).</summary>
    public bool CanApplyStarLegs => StarLegs?.CanApply == true;

    /// <summary>The proposal's refusal, or "".</summary>
    public string StarLegsRefusal => StarLegs?.Refusal ?? "";

    /// <summary>True when the proposal shown was refused.</summary>
    public bool HasStarLegsRefusal => StarLegs?.Refusal is not null;
}

/// <summary>The CHECK sub-tab: the last Check's problems for the node (Home: all of them).</summary>
public sealed record AuthorCheckPage(string Heading, string Note, IReadOnlyList<AuthoringProblem> Problems)
{
    /// <summary>True when there are problems to list.</summary>
    public bool HasProblems => Problems.Count > 0;

    /// <summary>Phase 5c follow-up (2026-10-24): the substation topology warnings (implied splits and couplings) of the
    /// overlay as the session holds it NOW, beside the last Check's problems: Home lists every substation's, an overlay
    /// record its own substation's. Display only: they never stop a Check or a save. Empty when there are none.</summary>
    public IReadOnlyList<AuthoringProblem> TopologyWarnings { get; init; } = Array.Empty<AuthoringProblem>();

    /// <summary>True when there are topology warnings to list.</summary>
    public bool HasTopologyWarnings => TopologyWarnings.Count > 0;
}

/// <summary>
/// The ASSETS tab in Author mode (UI programme Phase 4.1): the tree of the case file's records, the selection, the
/// sub-tabs and their content, and the editing actions (each one engine operation), with the last result as a status
/// line. UI-neutral (no Avalonia). It never reads a frame, so it can show no live value.
/// </summary>
public sealed class AssetAuthorModel : ObservableModel
{
    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    private IReadOnlyList<AuthorNode> _roots = Array.Empty<AuthorNode>();
    private Dictionary<string, AuthorNode> _byId = new(StringComparer.Ordinal);
    private AuthorNode _home = null!;
    private AuthorNode? _selected;
    private IReadOnlyList<string> _subTabs = new[] { AssetNavigatorModel.OverviewTab };
    private string _selectedSubTab = AssetNavigatorModel.OverviewTab;
    private AuthorSummary? _summary;
    private AuthorClassPage? _classPage;
    private AuthorRecordPage? _recordPage;
    private FieldGridModel? _fields;
    private ElementEditorModel? _editor;   // Phase 4.3
    private bool _showFieldGrid;           // Phase 4.3
    private IReadOnlyList<WhereUsedRow> _whereUsed = Array.Empty<WhereUsedRow>();
    private AuthorCheckPage? _checkPage;
    private StarLegsProposal? _starLegs;   // Phase 5f: the shown star-legs proposal (cleared on every selection and change)
    private TemplatesPage? _templatesPage;            // Phase 4.4
    private TemplateEditorModel? _templateEditor;     // Phase 4.4
    private TemplateOverridePage? _overridePage;      // Phase 4.4
    private GeneratorTemplateModel? _generatorTemplate; // Phase 4.4
    private TemplateResolution? _resolution;          // Phase 4.4: the last effective-parameter load (kept while current)
    private string _status = "";
    private bool _statusIsError;

    /// <summary>The Author mode over <paramref name="authoring"/> (opened by the shell when the user enters AUTHOR).</summary>
    public AssetAuthorModel(CaseAuthoring authoring) : this(authoring, null) { }

    /// <summary>Phase 4.4: the Author mode over <paramref name="authoring"/>, reading library imports from
    /// <paramref name="templateDataRoot"/> (null: the run pipeline's data root, <see cref="CaseTemplates.DefaultDataRoot"/>).</summary>
    public AssetAuthorModel(CaseAuthoring authoring, string? templateDataRoot)
    {
        TemplateDataRoot = templateDataRoot;
        Authoring = authoring ?? throw new ArgumentNullException(nameof(authoring));
        Authoring.Changed += (_, _) => Rebuild();
        Rebuild();
        Status = "AUTHOR: editing " + Authoring.CaseName + ". The run is paused and nothing here is a live value. "
            + (Authoring.IsBundled ? "This is a bundled case: Save As... saves your edits as your own copy." : "Save writes the case in place after it passes the save gate.");
    }

    /// <summary>The authoring session (the case being edited).</summary>
    public CaseAuthoring Authoring { get; }

    /// <summary>The badge text: "AUTHOR", with " *" while there are unsaved changes.</summary>
    public string ModeLabel => Authoring.IsDirty ? "AUTHOR *" : "AUTHOR";

    /// <summary>True while there are unsaved changes.</summary>
    public bool IsDirty => Authoring.IsDirty;

    /// <summary>The tree's top-level nodes: Home, the case-file records, then one class node per role that the case has.</summary>
    public IReadOnlyList<AuthorNode> Roots => _roots;

    /// <summary>The Home node (the case summary).</summary>
    public AuthorNode Home => _home;

    /// <summary>The selected node (Home at first).</summary>
    public AuthorNode? Selected => _selected;

    /// <summary>The crumb: the selected node's path.</summary>
    public string Crumb => _selected?.Path ?? "";

    /// <summary>Every node, depth first (for tests and the search).</summary>
    public IEnumerable<AuthorNode> PreOrder()
    {
        var stack = new Stack<AuthorNode>(_roots.Reverse());
        while (stack.Count > 0)
        {
            AuthorNode n = stack.Pop();
            yield return n;
            for (int i = n.Children.Count - 1; i >= 0; i--) stack.Push(n.Children[i]);
        }
    }

    /// <summary>The node with id <paramref name="id"/>, or null.</summary>
    public AuthorNode? ById(string id) => _byId.GetValueOrDefault(id);

    /// <summary>The node of record <paramref name="record"/>, or null.</summary>
    public AuthorNode? NodeOf(CaseRef record) => ById(IdOf(record));

    /// <summary>The class node of <paramref name="role"/>, or null.</summary>
    public AuthorNode? ClassNode(string role) => ById("class:" + role);

    private static string IdOf(CaseRef r) => "rec:" + r.Document.ToString(Inv) + ":" + r.ArrayPointer + ":" + r.Index.ToString(Inv);

    // ======================================== the tree ========================================

    // Build the tree from the session as it is now, keeping the selection and the expansion by id, then refresh the page.
    private void Rebuild()
    {
        var expanded = new HashSet<string>(PreOrder().Where(n => n.IsExpanded).Select(n => n.Id), StringComparer.Ordinal);
        string? selectedId = _selected?.Id;

        var roots = new List<AuthorNode>();
        var byId = new Dictionary<string, AuthorNode>(StringComparer.Ordinal);
        void Add(AuthorNode n, List<AuthorNode> into) { into.Add(n); byId[n.Id] = n; }

        AuthorNode home = new("home", "Case", Authoring.CaseName, AuthorNodeKind.Home, null, null, Array.Empty<AuthoringRecordSet>(), null);
        Add(home, roots);
        foreach (AuthoringRecord r in Authoring.RootRecords())
            Add(new AuthorNode(IdOf(r.Ref), r.Label, r.Detail, AuthorNodeKind.CaseFile, null, r.Ref, Array.Empty<AuthoringRecordSet>(), null), roots);

        // One class node per role, in the engine's order of first appearance; v2 joins the role's arrays of every
        // network file under one node (the detail names the file when there are several).
        // Phase 5b: the asset overlay's collections are not model roles; they get their own subtree below.
        IReadOnlyList<AuthoringRecordSet> all = Authoring.Collections().Where(c => c.Role != CaseRoles.Overlay).ToList();
        bool manyFiles = all.Select(c => c.Document).Distinct().Count() > 1 && Authoring.Format != AuthoringFormat.V1Folder;
        bool canAttach = OverlayActions.HasOverlayFile(Authoring);
        foreach (IGrouping<string, AuthoringRecordSet> role in all.GroupBy(c => c.Role))
        {
            List<AuthoringRecordSet> colls = role.ToList();
            int count = colls.Sum(c => c.Count);
            string detail = count.ToString(Inv) + (colls.All(c => !c.Exists) ? " (file absent)" : "");
            var cls = new AuthorNode("class:" + role.Key, colls[0].Title, detail, AuthorNodeKind.Class, role.Key, null, colls, null);
            Add(cls, roots);
            bool anchor = canAttach && OverlayActions.IsAttachableRole(role.Key);
            foreach (AuthoringRecordSet c in colls)
                foreach (AuthoringRecord r in Authoring.Records(c))
                {
                    string d = manyFiles ? r.Detail : r.Ref.Display;
                    var node = new AuthorNode(IdOf(r.Ref), r.Label, d, AuthorNodeKind.Record, role.Key, r.Ref, Array.Empty<AuthoringRecordSet>(), cls, anchor);
                    cls.Children.Add(node);
                    byId[node.Id] = node;
                }
        }
        AddOverlayTree(roots, byId);
        AddTemplateTree(roots, byId);   // Phase 4.4: a v2 case's templates (nothing for any other case)

        foreach (AuthorNode n in byId.Values) if (expanded.Contains(n.Id)) n.IsExpanded = true;
        _roots = roots;
        _byId = byId;
        _home = home;
        Raise(nameof(Roots));
        Raise(nameof(Home));
        Raise(nameof(ModeLabel));
        Raise(nameof(IsDirty));
        AuthorNode? keep = selectedId is null ? null : byId.GetValueOrDefault(selectedId);
        SetSelected(keep ?? home);
    }

    // Phase 5b: the "Asset overlay" class node (only for a case that lists overlay files), with the overlay records as a
    // tree by parent (ASSETS plan Q12's nesting): a record whose parent is not a record of the overlay sits at the top,
    // and a record caught in a parent loop (which Check reports) is shown at the top too, so every record has a node.
    // Each node's label is the record id, its detail the kind (and the file, when the case has several overlay files).
    private void AddOverlayTree(List<AuthorNode> roots, Dictionary<string, AuthorNode> byId)
    {
        List<AuthoringRecordSet> colls = Authoring.Collections().Where(c => c.Role == CaseRoles.Overlay).ToList();
        if (colls.Count == 0) return;
        int count = colls.Sum(c => c.Count);
        var cls = new AuthorNode("class:" + CaseRoles.Overlay, colls[0].Title, count.ToString(Inv) + (colls.All(c => !c.Exists) ? " (file absent)" : ""),
            AuthorNodeKind.Class, CaseRoles.Overlay, null, colls, null);
        roots.Add(cls);
        byId[cls.Id] = cls;

        bool manyFiles = colls.Count > 1;
        var records = new List<(AuthoringRecord Rec, string? Id, string Kind, string? Parent)>();
        foreach (AuthoringRecordSet c in colls)
            foreach (AuthoringRecord r in Authoring.Records(c))
            {
                (string? id, string kind, string? parent) = Authoring.OverlayIdentity(r.Ref);
                records.Add((r, id, kind, parent));
            }
        var known = new HashSet<string>(records.Where(x => x.Id is not null).Select(x => x.Id!), StringComparer.Ordinal);
        var placed = new HashSet<int>();
        var childrenOf = new Dictionary<string, List<int>>(StringComparer.Ordinal);
        for (int k = 0; k < records.Count; k++)
            if (records[k].Parent is string p)
            {
                if (!childrenOf.TryGetValue(p, out List<int>? l)) childrenOf[p] = l = new List<int>();
                l.Add(k);
            }

        void Place(int i, AuthorNode parentNode)
        {
            if (!placed.Add(i)) return;
            (AuthoringRecord r, string? id, string kind, _) = records[i];
            string detail = kind + (manyFiles ? "  " + r.Ref.FileName : "");
            var node = new AuthorNode(IdOf(r.Ref), id ?? r.Label, detail, AuthorNodeKind.Record, CaseRoles.Overlay, r.Ref, Array.Empty<AuthoringRecordSet>(), parentNode,
                starTransformer: kind == StarTransformerKind);
            parentNode.Children.Add(node);
            byId[node.Id] = node;
            if (id is not null && childrenOf.TryGetValue(id, out List<int>? kids))
                foreach (int k in kids) Place(k, node);
        }
        for (int i = 0; i < records.Count; i++)
            if (records[i].Parent is null || !known.Contains(records[i].Parent!)) Place(i, cls);
        for (int i = 0; i < records.Count; i++) Place(i, cls);   // anything left is in a parent loop
    }

    // Phase 4.4: the TEMPLATES class node of a v2 case manifest (only that format has templates of this kind): one node
    // per local template (its record's id, so a template an operation adds or moves is selected like any record), then
    // one per imported library template (read only), each with its overrides as child nodes. The label is the reference
    // (id@rev), the detail the kind and where it comes from.
    private void AddTemplateTree(List<AuthorNode> roots, Dictionary<string, AuthorNode> byId)
    {
        if (Authoring.Format != AuthoringFormat.V2Case) return;
        CaseTemplates t = Templates();
        var cls = new AuthorNode(TemplatePages.ClassNodeId, TemplatePages.ClassNodeLabel,
            t.Local.Count.ToString(Inv) + " local, " + t.Library.Count.ToString(Inv) + " library",
            AuthorNodeKind.TemplateClass, TemplateRole, null, Array.Empty<AuthoringRecordSet>(), null);
        roots.Add(cls);
        byId[cls.Id] = cls;
        foreach (TemplateInfo info in t.All)
        {
            string id = info.Record is CaseRef r ? IdOf(r) : TemplatePages.LibraryNodeId(info.Ref);
            if (byId.ContainsKey(id)) continue;   // a library template imported twice: one node (the loader imports it once)
            var node = new AuthorNode(id, info.Ref, info.Kind + (info.IsLocal ? "  local" : "  library, read only") + (info.IsPlaceholder ? "  placeholder" : ""),
                AuthorNodeKind.Template, TemplateRole, info.Record, Array.Empty<AuthoringRecordSet>(), cls) { TemplateRef = info.Ref };
            cls.Children.Add(node);
            byId[node.Id] = node;
            foreach (TemplateOverrideRow o in TemplatePages.OverridesOf(t, info.Ref))
            {
                var on = new AuthorNode(o.NodeId, "override " + o.Param, o.Text, AuthorNodeKind.TemplateOverride, TemplateRole, info.Record,
                    Array.Empty<AuthoringRecordSet>(), node) { TemplateRef = info.Ref, OverrideParam = o.Param };
                if (byId.TryAdd(on.Id, on)) node.Children.Add(on);
            }
        }
    }

    /// <summary>Phase 4.4: the role name the template nodes carry (not an engine role: templates live in the manifest).</summary>
    public const string TemplateRole = "templates";

    /// <summary>Phase 4.4: the folder library imports are read from (the run pipeline's data root unless a test gives another).</summary>
    public string? TemplateDataRoot { get; }

    /// <summary>Phase 4.4: the templates, bindings and overrides of the case as the session holds them now.</summary>
    public CaseTemplates Templates() => CaseTemplates.Read(Authoring, TemplateDataRoot);

    /// <summary>Select <paramref name="node"/> (a node of the current tree) and show its page. False when it is already
    /// selected.</summary>
    public bool Select(AuthorNode node)
    {
        ArgumentNullException.ThrowIfNull(node);
        if (!ReferenceEquals(_byId.GetValueOrDefault(node.Id), node))
            throw new ArgumentException($"{node} is not a node of the current Author tree (it was rebuilt after a change).", nameof(node));
        if (ReferenceEquals(node, _selected)) return false;
        SetSelected(node);
        return true;
    }

    /// <summary>Select the node of <paramref name="record"/>. False when the tree holds none.</summary>
    public bool SelectRecord(CaseRef record)
    {
        ArgumentNullException.ThrowIfNull(record);
        if (NodeOf(record) is not AuthorNode n) return false;
        Select(n);
        return true;
    }

    private void SetSelected(AuthorNode node)
    {
        for (AuthorNode? p = node.Parent; p is not null; p = p.Parent) p.IsExpanded = true;
        _selected = node;
        // Phase 5f: a proposal belongs to the session revision and the record it was made for; a new selection (and every
        // change, which rebuilds the tree and lands here) drops it, so a stale proposal is never shown or applied.
        _starLegs = null;
        Raise(nameof(Selected));
        Raise(nameof(Crumb));
        RefreshContent();
    }

    // ======================================== sub-tabs and content ========================================

    /// <summary>The sub-tabs the selected node has content for, in the plan's order.</summary>
    public IReadOnlyList<string> SubTabs => _subTabs;

    /// <summary>The sub-tab shown (only one of <see cref="SubTabs"/>; anything else is refused).</summary>
    public string SelectedSubTab
    {
        get => _selectedSubTab;
        set
        {
            if (!_subTabs.Contains(value, StringComparer.Ordinal))
                throw new ArgumentException($"'{value}' is not a sub-tab of this page (shown: {string.Join(", ", _subTabs)})", nameof(value));
            if (string.Equals(value, _selectedSubTab, StringComparison.Ordinal)) return;
            _selectedSubTab = value;
            Raise(nameof(SelectedSubTab));
            RaiseShown();
        }
    }

    /// <summary>True while OVERVIEW is shown.</summary>
    public bool IsOverviewShown => _selectedSubTab == AssetNavigatorModel.OverviewTab;

    /// <summary>True while PROPERTIES is shown.</summary>
    public bool IsPropertiesShown => _selectedSubTab == AssetNavigatorModel.PropertiesTab;

    /// <summary>True while CONNECTIONS is shown.</summary>
    public bool IsConnectionsShown => _selectedSubTab == AssetNavigatorModel.ConnectionsTab;

    /// <summary>True while CHECK is shown.</summary>
    public bool IsCheckShown => _selectedSubTab == AssetNavigatorModel.CheckTab;

    private void RaiseShown()
    {
        Raise(nameof(IsOverviewShown));
        Raise(nameof(IsPropertiesShown));
        Raise(nameof(IsEditorShown));      // Phase 4.3
        Raise(nameof(IsFieldGridShown));   // Phase 4.3
        Raise(nameof(IsViewToggleShown));  // Phase 4.3
        Raise(nameof(IsConnectionsShown));
        Raise(nameof(IsCheckShown));
        Raise(nameof(IsTemplateEditorShown));   // Phase 4.4
    }

    /// <summary>Home's OVERVIEW (the case summary), or null when another node is selected.</summary>
    public AuthorSummary? Summary => _summary;

    /// <summary>A class node's OVERVIEW, or null.</summary>
    public AuthorClassPage? ClassPage => _classPage;

    /// <summary>A record's OVERVIEW (the record summary), or null.</summary>
    public AuthorRecordPage? RecordPage => _recordPage;

    /// <summary>A record's PROPERTIES (the editable field grid), or null.</summary>
    public FieldGridModel? Fields => _fields;

    // ---- UI programme Phase 4.3: the per-element editor, a specialisation of PROPERTIES over the same field grid ----

    /// <summary>The selected record's per-element editor (bus, line, transformer, generator, demand, OLTC, boundary,
    /// protection), arranged over <see cref="Fields"/>' own rows; null for every other record (the generic grid shows).</summary>
    public ElementEditorModel? Editor => _editor;

    /// <summary>True when the selected record has a per-element editor.</summary>
    public bool HasEditor => _editor is not null;

    /// <summary>True while PROPERTIES shows the per-element editor (the record has one and the field grid is not asked for).</summary>
    public bool IsEditorShown => IsPropertiesShown && _editor is not null && !_showFieldGrid;

    /// <summary>True while PROPERTIES shows the generic field grid (a record with no editor, or the grid asked for).</summary>
    public bool IsFieldGridShown => IsPropertiesShown && (_editor is null || _showFieldGrid) && _templateEditor is null;

    /// <summary>True when the generic field grid is shown in place of a per-element editor (kept across selections).</summary>
    public bool ShowFieldGrid
    {
        get => _showFieldGrid;
        set
        {
            if (_showFieldGrid == value) return;
            _showFieldGrid = value;
            Raise(nameof(ShowFieldGrid));
            Raise(nameof(IsEditorShown));
            Raise(nameof(IsFieldGridShown));
            Raise(nameof(ViewToggleText));
        }
    }

    /// <summary>True while PROPERTIES shows a record that has a per-element editor (the view offers the switch between
    /// the editor and the generic field grid).</summary>
    public bool IsViewToggleShown => IsPropertiesShown && _editor is not null;

    /// <summary>The switch's text: what a click shows.</summary>
    public string ViewToggleText => _showFieldGrid ? "SHOW THE " + (_editor?.KindTitle.ToUpperInvariant() ?? "ELEMENT") + " EDITOR" : "SHOW ALL FIELDS AS A GRID";

    /// <summary>Commit an editor field's typed text (its grid row's own Commit: one undo step, or refused with the reason
    /// shown inline on the field and in the status line).</summary>
    public AuthoringResult CommitEditorField(EditorField field)
    {
        ArgumentNullException.ThrowIfNull(field);
        return Show(field.Commit());
    }

    /// <summary>Pick a choice of an editor field's picker and commit it (one undo step, or refused inline).</summary>
    public AuthoringResult PickEditorChoice(EditorField field, EditorChoice choice)
    {
        ArgumentNullException.ThrowIfNull(field);
        ArgumentNullException.ThrowIfNull(choice);
        return Show(field.Pick(choice));
    }

    /// <summary>Reset an editor field to absent (its grid row's own Reset; refused inline with the reason).</summary>
    public AuthoringResult ResetEditorField(EditorField field)
    {
        ArgumentNullException.ThrowIfNull(field);
        return Show(field.Reset());
    }

    /// <summary>A record's CONNECTIONS (who refers to it), empty when nothing does.</summary>
    public IReadOnlyList<WhereUsedRow> WhereUsed => _whereUsed;

    /// <summary>CHECK: the last Check's problems for the node, or null before the first Check.</summary>
    public AuthorCheckPage? CheckPage => _checkPage;

    // Work out the sub-tabs and their content for the selected node.
    private void RefreshContent()
    {
        AuthorNode n = _selected ?? _home;
        var tabs = new List<string> { AssetNavigatorModel.OverviewTab };
        AuthorSummary? summary = null;
        AuthorClassPage? cls = null;
        AuthorRecordPage? rec = null;
        FieldGridModel? fields = null;
        ElementEditorModel? editor = null;
        IReadOnlyList<WhereUsedRow> used = Array.Empty<WhereUsedRow>();
        AuthorCheckPage? check = null;
        TemplatesPage? templatesPage = null;             // Phase 4.4
        TemplateEditorModel? templateEditor = null;      // Phase 4.4
        TemplateOverridePage? overridePage = null;       // Phase 4.4
        GeneratorTemplateModel? generatorTemplate = null; // Phase 4.4
        AuthoringReport? last = Authoring.LastCheck;
        string checkNote = last is null ? "" : Authoring.LastCheckRevision == Authoring.Revision
            ? "From the last Check, made on the case as it is now: " + last.Headline
            : "From the last Check, made before the latest change (Check again to see the case as it is now): " + last.Headline;

        switch (n.Kind)
        {
            case AuthorNodeKind.Home:
                summary = MakeSummary();
                if (last is not null) check = new AuthorCheckPage("Every problem of the last Check", checkNote, last.Errors) { TopologyWarnings = TopologyWarningsNow(null) };
                break;
            case AuthorNodeKind.Class:
                cls = new AuthorClassPage(n.Label, n.Children.Count.ToString(Inv) + " record(s) in "
                    + string.Join(", ", n.Collections.Select(c => c.FileName + (c.ArrayPointer.Length > 0 ? " " + c.ArrayPointer : "")).Distinct())
                    + (n.Role == CaseRoles.Overlay
                        ? ". Top-level records (children sit under their parent). Select one to edit it; right-click the class or a record to add an overlay record."
                        : ". Select one to edit it; right-click the class to add a record."), n.Children);
                break;
            case AuthorNodeKind.CaseFile:
            case AuthorNodeKind.Record:
            {
                CaseRef r = n.Record!;
                fields = Authoring.FieldGrid(r);
                // Phase 4.3: the per-element editor of the record's kind, over the same grid (null: the generic grid).
                if (n.Kind == AuthorNodeKind.Record) editor = ElementEditors.For(Authoring, n.Role, fields);
                string? key = RecordActions.KeyField(Authoring, r);
                string keyText = key is null ? "These records have no key (nothing refers to them by name or id)."
                    : "Key: " + key + " (Rename changes it and every reference to it in one step).";
                bool fileDirty = Authoring.Files[r.Document].IsDirty;
                rec = new AuthorRecordPage(fields.Heading, fields.ShapeTitle, r.Display + (r.Pointer.Length > 0 ? "  at " + r.Pointer : ""), keyText,
                    fileDirty ? r.FileName + " has unsaved changes." : r.FileName + " is as saved on disk.", Authoring.RawText(r));
                // Phase 5c: an overlay record of a substation shows that substation's topology, from the session as it is now.
                if (n.Role == CaseRoles.Overlay && Authoring.OverlayIdentity(r).Id is string overlayId)
                    rec = rec with { Topology = SubstationTopologyView.For(overlayId, Authoring.OverlayRecordsNow()) };
                // Phase 5f: a three-winding transformer record offers Derive star legs, and shows the proposal once made.
                if (n.Role == CaseRoles.Overlay && Authoring.OverlayIdentity(r).Kind == "transformer3W")
                    rec = rec with { CanDeriveStarLegs = true, StarLegs = _starLegs };
                tabs.Add(AssetNavigatorModel.PropertiesTab);
                used = RecordActions.WhereUsed(Authoring, r);
                if (used.Count > 0) tabs.Add(AssetNavigatorModel.ConnectionsTab);
                if (last is not null)
                    check = new AuthorCheckPage("Problems of " + fields.Heading, checkNote, Authoring.CheckProblemsFor(r))
                    {
                        // Phase 5c follow-up: an overlay record of a substation also lists that substation's topology warnings.
                        TopologyWarnings = n.Role == CaseRoles.Overlay && Authoring.OverlayIdentity(r).Id is string ownId
                            ? TopologyWarningsNow(ownId) : Array.Empty<AuthoringProblem>(),
                    };
                // Phase 4.4: a generator of a v2 case shows, on its editor, the template it is bound to and that
                // template's overrides (and the effective parameters, once asked for).
                if (n.Kind == AuthorNodeKind.Record && n.Role == CaseRoles.Generators && Authoring.Format == AuthoringFormat.V2Case
                    && Templates() is CaseTemplates gt && gt.Generators.FirstOrDefault(g => g.Record == r) is GeneratorBindingInfo gb)
                {
                    generatorTemplate = new GeneratorTemplateModel(gt, gb);
                    if (_resolution is { } res && res.Revision == Authoring.Revision)
                    {
                        generatorTemplate.Effective = res.For(r);
                        generatorTemplate.EffectiveError = res.Error;
                    }
                    if (editor is not null) editor.TemplateLink = generatorTemplate;
                }
                break;
            }
            case AuthorNodeKind.TemplateClass:
            {
                // Phase 4.4: the TEMPLATES node: every template (a row selects it), rule, override and generator binding.
                CaseTemplates t = Templates();
                templatesPage = new TemplatesPage(
                    t.Local.Count.ToString(Inv) + " local template(s) in " + Authoring.CaseName + " (editable) and " + t.Library.Count.ToString(Inv)
                    + " library template(s) it imports (read only). Select one to edit it; right-click the class to add a template, a template to duplicate, delete or bind it.",
                    n.Children, t.Bindings, t.All.SelectMany(x => TemplatePages.OverridesOf(t, x.Ref)).ToList(), t.Generators, t.Notes);
                if (last is not null) check = new AuthorCheckPage("Problems of the templates", checkNote,
                    last.Errors.Where(e => IsTemplateProblem(e, null)).ToList());
                break;
            }
            case AuthorNodeKind.Template:
            case AuthorNodeKind.TemplateOverride:
            {
                CaseTemplates t = Templates();
                TemplateInfo? info = n.TemplateRef is string tr ? t.Find(tr) : null;
                if (info is null) break;   // the tree is rebuilt from the same text, so this does not happen; nothing to show
                if (n.Kind == AuthorNodeKind.TemplateOverride)
                {
                    if (TemplatePages.OverridesOf(t, info.Ref).FirstOrDefault(o => o.Param == n.OverrideParam) is TemplateOverrideRow row)
                        overridePage = new TemplateOverridePage(row, info.Source);
                    break;
                }
                templateEditor = new TemplateEditorModel(Authoring, t, info, TemplateDataRoot);
                string written = info.Record is CaseRef tr2 ? Authoring.RawText(tr2)
                    : info.LibraryJson is System.Text.Json.JsonElement lj ? lj.GetRawText() : "";
                rec = new AuthorRecordPage(templateEditor.Heading, templateEditor.KindText, info.Source,
                    "Key: " + info.Ref + " (binding rules, overrides and elements name a template by id@rev).",
                    info.IsLocal ? (Authoring.Files[0].IsDirty ? Authoring.Files[0].FileName + " has unsaved changes." : Authoring.Files[0].FileName + " is as saved on disk.")
                        : "Read only: " + TemplateEditorModel.LibraryReadOnlyReason, written);
                tabs.Add(AssetNavigatorModel.PropertiesTab);
                if (last is not null) check = new AuthorCheckPage("Problems of " + info.Ref, checkNote, last.Errors.Where(e => IsTemplateProblem(e, info)).ToList());
                break;
            }
        }
        if (check is not null) tabs.Add(AssetNavigatorModel.CheckTab);

        _summary = summary;
        _classPage = cls;
        _recordPage = rec;
        _fields = fields;
        _editor = editor;
        _whereUsed = used;
        _checkPage = check;
        _templatesPage = templatesPage;
        _templateEditor = templateEditor;
        _overridePage = overridePage;
        _generatorTemplate = generatorTemplate;
        if (!tabs.SequenceEqual(_subTabs, StringComparer.Ordinal))
        {
            _subTabs = tabs;
            Raise(nameof(SubTabs));
        }
        if (!tabs.Contains(_selectedSubTab, StringComparer.Ordinal))
        {
            _selectedSubTab = AssetNavigatorModel.OverviewTab;
            Raise(nameof(SelectedSubTab));
            RaiseShown();
        }
        Raise(nameof(Summary));
        Raise(nameof(ClassPage));
        Raise(nameof(RecordPage));
        Raise(nameof(Fields));
        Raise(nameof(Editor));             // Phase 4.3
        Raise(nameof(HasEditor));
        Raise(nameof(IsEditorShown));
        Raise(nameof(IsFieldGridShown));
        Raise(nameof(IsViewToggleShown));
        Raise(nameof(ViewToggleText));
        Raise(nameof(WhereUsed));
        Raise(nameof(CheckPage));
        Raise(nameof(TemplatesPage));          // Phase 4.4
        Raise(nameof(TemplateEditor));
        Raise(nameof(IsTemplateEditorShown));
        Raise(nameof(OverridePage));
        Raise(nameof(GeneratorTemplate));
    }

    // Phase 4.4: whether a Check problem belongs to the templates (template null: any of them) or to one template: its
    // pointer is in the manifest's localTemplates, bindings or overrides (or in that template's record), or its message
    // names the template's reference or pointer (the v2 loader's refusal is one problem whose message says where).
    private bool IsTemplateProblem(AuthoringProblem e, TemplateInfo? template)
    {
        string manifest = Authoring.Files[0].FileName;
        if (template is null)
            return (e.File == manifest && (e.Pointer.StartsWith("/localTemplates", StringComparison.Ordinal) || e.Pointer.StartsWith("/bindings", StringComparison.Ordinal)
                    || e.Pointer.StartsWith("/overrides", StringComparison.Ordinal)))
                || e.Message.Contains("/localTemplates/", StringComparison.Ordinal) || e.Message.Contains("/bindings/", StringComparison.Ordinal)
                || e.Message.Contains("/overrides/", StringComparison.Ordinal);
        string at = "/localTemplates/" + template.LocalIndex.ToString(Inv);
        return (template.IsLocal && e.File == manifest && (e.Pointer == at || e.Pointer.StartsWith(at + "/", StringComparison.Ordinal)))
            || e.Message.Contains(template.Ref, StringComparison.Ordinal)
            || (template.IsLocal && e.Message.Contains(at + "/", StringComparison.Ordinal));
    }

    private AuthorSummary MakeSummary()
    {
        CaseAuthoring a = Authoring;
        var counts = a.Collections().GroupBy(c => c.Role).Select(g => new AuthorCount(g.First().Title, g.Sum(c => c.Count))).ToList();
        string format = a.Format switch
        {
            AuthoringFormat.V1Folder => "v1 case folder",
            AuthoringFormat.V2Case => "v2 case manifest and its network files",
            _ => "v2 network file",
        };
        string bundled = a.IsBundled ? "Bundled case: it is never written in place. Save As... saves your edits as your own copy (suggested: " + a.SuggestSaveAsFolder() + ")." : "";
        // Phase 5b: a case with an overlay is edited, checked and saved with it; the note says so (and the one refusal left).
        string overlay = a.OverlayRefusal is string o ? "Check, Save and Save As are refused: " + o
            : a.HasOverlay ? "Asset overlay (carried beside the model, never used by the solver): " + string.Join(", ", a.OpenedOverlayFiles)
              + ". Its records are under Asset overlay, edited by their kind specs; Check runs the overlay checker and the v2 loader with it, and Save writes it with the rest."
            : "";
        string dirty = a.IsDirty ? "Unsaved changes in " + string.Join(", ", a.DirtyFiles) + "." : "No unsaved changes.";
        string check = a.LastCheck is null ? "No Check yet (Case > Check)." : a.LastCheck.Headline;
        string undo = (a.UndoDescription is null ? "Nothing to undo" : "Undo: " + a.UndoDescription)
            + "; " + (a.RedoDescription is null ? "nothing to redo" : "Redo: " + a.RedoDescription)
            + string.Create(Inv, $" ({a.UndoDepth} of at most {CaseAuthoring.UndoLimit} undo levels held).");
        return new AuthorSummary(a.CaseName, a.CasePath, format, bundled, overlay, counts, a.Files, dirty, check, undo, a.PreflightText);
    }

    // ======================================== templates (Phase 4.4) ========================================

    /// <summary>The TEMPLATES class node's OVERVIEW, or null when another node is selected.</summary>
    public TemplatesPage? TemplatesPage => _templatesPage;

    /// <summary>The selected template's editor (PROPERTIES of a template node), or null.</summary>
    public TemplateEditorModel? TemplateEditor => _templateEditor;

    /// <summary>True while PROPERTIES shows a template's editor.</summary>
    public bool IsTemplateEditorShown => IsPropertiesShown && _templateEditor is not null;

    /// <summary>The selected override node's page, or null.</summary>
    public TemplateOverridePage? OverridePage => _overridePage;

    /// <summary>The selected generator's template link (a generator record of a v2 case), or null.</summary>
    public GeneratorTemplateModel? GeneratorTemplate => _generatorTemplate;

    /// <summary>The TEMPLATES class node of a v2 case, or null.</summary>
    public AuthorNode? TemplatesNode => ById(TemplatePages.ClassNodeId);

    /// <summary>The node of template <paramref name="reference"/>, or null.</summary>
    public AuthorNode? TemplateNode(string reference)
    {
        ArgumentNullException.ThrowIfNull(reference);
        return TemplatesNode?.Children.FirstOrDefault(c => c.TemplateRef == reference);
    }

    // The selected template node's template (a template or override node), or null.
    private TemplateInfo? SelectedTemplate(CaseTemplates t)
        => _selected is { Kind: AuthorNodeKind.Template or AuthorNodeKind.TemplateOverride, TemplateRef: string r } ? t.Find(r) : null;

    /// <summary>True when Add template... can act (the TEMPLATES node or a template is selected).</summary>
    public bool CanAddTemplate => _selected is { Kind: AuthorNodeKind.TemplateClass or AuthorNodeKind.Template };

    /// <summary>True when a template node is selected (Duplicate and Bind to template... act on it).</summary>
    public bool HasSelectedTemplate => _selected is { Kind: AuthorNodeKind.Template };

    /// <summary>True when a LOCAL template node is selected (Delete acts on it; a library template cannot be deleted).</summary>
    public bool HasSelectedLocalTemplate => _selected is { Kind: AuthorNodeKind.Template, Record: not null };

    /// <summary>True when an override node is selected (Reset override acts on it).</summary>
    public bool HasSelectedOverride => _selected is { Kind: AuthorNodeKind.TemplateOverride };

    // Run one template operation over the case's templates as they are now (one engine transaction or a refusal).
    private AuthoringResult TemplateOp(Func<CaseSession, CaseTemplates, OpResult> op)
    {
        CaseTemplates t = Templates();
        return Show(Authoring.Apply(s => op(s, t)));
    }

    /// <summary>Add a local inverter template of <paramref name="kind"/> named <paramref name="name"/> (id gridsim.local.NAME,
    /// revision 1, no parameter set yet); the new template is selected. Refused with the reason.</summary>
    public AuthoringResult AddTemplate(string kind, string name, string? title = null)
    {
        ArgumentNullException.ThrowIfNull(kind);
        ArgumentNullException.ThrowIfNull(name);
        if (Authoring.Format != AuthoringFormat.V2Case) return Show(AuthoringResult.Refused("Add template refused: templates belong to a v2 case manifest; this case is not one."));
        return TemplateOp((s, t) => TemplateOps.AddTemplate(s, t, kind, name, title));
    }

    /// <summary>Duplicate the selected template as a new local template (a library template is copied into the case).</summary>
    public AuthoringResult DuplicateSelectedTemplate()
    {
        TemplateInfo? info = SelectedTemplate(Templates());
        if (info is null || _selected?.Kind != AuthorNodeKind.Template) return Show(AuthoringResult.Refused("Select a template to duplicate."));
        return TemplateOp((s, t) => TemplateOps.Duplicate(s, t, t.Find(info.Ref)!));
    }

    /// <summary>Delete the selected local template (refused, listing them, while anything in the case uses it).</summary>
    public AuthoringResult DeleteSelectedTemplate()
    {
        TemplateInfo? info = SelectedTemplate(Templates());
        if (info is null || _selected?.Kind != AuthorNodeKind.Template) return Show(AuthoringResult.Refused("Select a template to delete."));
        if (!info.IsLocal) return Show(AuthoringResult.Refused("Delete refused: " + info.Ref + " is a library template the case imports; it is not written in this case (remove the import from the manifest to stop importing it)."));
        return TemplateOp((s, t) => TemplateOps.Delete(s, t, info.LocalIndex));
    }

    /// <summary>
    /// Bind to the selected template: the generator named <paramref name="generatorName"/> (by its own template member,
    /// which wins over every rule), or every generator of type <paramref name="matchType"/> (a new binding rule; a
    /// trailing * makes it a prefix). Exactly one of the two must be given. Refused with the reason.
    /// </summary>
    public AuthoringResult BindToSelectedTemplate(string? generatorName, string? matchType)
    {
        CaseTemplates now = Templates();
        TemplateInfo? info = SelectedTemplate(now);
        if (info is null || _selected?.Kind != AuthorNodeKind.Template) return Show(AuthoringResult.Refused("Select a template to bind generators to."));
        bool byName = !string.IsNullOrWhiteSpace(generatorName), byType = !string.IsNullOrWhiteSpace(matchType);
        if (byName == byType) return Show(AuthoringResult.Refused("Bind refused: give either a generator (to bind that one generator) or a type (to bind every generator of that type by a rule), not both and not neither."));
        if (byType) return AddBindingRule(info.Ref, matchType!);
        List<GeneratorBindingInfo> named = now.Generators.Where(g => string.Equals(g.Name, generatorName!.Trim(), StringComparison.Ordinal)).ToList();
        if (named.Count != 1)
            return Show(AuthoringResult.Refused("Bind refused: " + (named.Count == 0 ? "no generator is named '" + generatorName!.Trim() + "'."
                : named.Count.ToString(Inv) + " generators are named '" + generatorName!.Trim() + "' (" + string.Join(", ", named.Select(g => g.Record.Display)) + "); bind the one you mean from its own editor.")));
        AuthoringResult bound = BindGenerator(named[0].Record, info.Ref);
        // The bind was asked for on the template, so the template stays selected (the engine's SetField would select
        // the generator it changed).
        if (bound.Ok && TemplateNode(info.Ref) is AuthorNode back) Select(back);
        return bound;
    }

    /// <summary>The generator names Bind to template... offers (each generator's name, in file order; a name is listed once).</summary>
    public IReadOnlyList<string> GeneratorNames() => Templates().Generators.Where(g => g.Name is not null).Select(g => g.Name!).Distinct(StringComparer.Ordinal).ToList();

    /// <summary>Add a binding rule: generators of type <paramref name="matchType"/> are bound to <paramref name="reference"/>.</summary>
    public AuthoringResult AddBindingRule(string reference, string matchType)
    {
        ArgumentNullException.ThrowIfNull(reference);
        ArgumentNullException.ThrowIfNull(matchType);
        return TemplateOp((s, t) => TemplateOps.AddBinding(s, t, matchType, reference));
    }

    /// <summary>Point binding rule <paramref name="ruleIndex"/> at <paramref name="reference"/>.</summary>
    public AuthoringResult RebindRule(int ruleIndex, string reference)
    {
        ArgumentNullException.ThrowIfNull(reference);
        return TemplateOp((s, t) => TemplateOps.Rebind(s, t, ruleIndex, reference));
    }

    /// <summary>Remove binding rule <paramref name="ruleIndex"/>.</summary>
    public AuthoringResult RemoveRule(int ruleIndex) => TemplateOp((s, t) => TemplateOps.RemoveBinding(s, t, ruleIndex));

    /// <summary>Bind the generator record <paramref name="generator"/> to <paramref name="reference"/> by its own template member.</summary>
    public AuthoringResult BindGenerator(CaseRef generator, string reference)
    {
        ArgumentNullException.ThrowIfNull(generator);
        ArgumentNullException.ThrowIfNull(reference);
        CaseTemplates now = Templates();
        if (now.Generators.FirstOrDefault(g => g.Record == generator) is not GeneratorBindingInfo g)
            return Show(AuthoringResult.Refused(generator.Display + " is not a generator of this v2 case."));
        return TemplateOp((s, t) => TemplateOps.BindGenerator(s, t, g, reference));
    }

    /// <summary>Remove the generator's own template member (it is then bound by the rules, if any match it).</summary>
    public AuthoringResult UnbindGenerator(CaseRef generator)
    {
        ArgumentNullException.ThrowIfNull(generator);
        if (Templates().Generators.FirstOrDefault(g => g.Record == generator) is not GeneratorBindingInfo g)
            return Show(AuthoringResult.Refused(generator.Display + " is not a generator of this v2 case."));
        return TemplateOp((s, _) => TemplateOps.UnbindGenerator(s, g));
    }

    /// <summary>Override <paramref name="param"/> of <paramref name="reference"/> for this case (a new override needs
    /// <paramref name="reason"/>). Refused with the reason, the kind spec's message for a value it refuses.</summary>
    public AuthoringResult SetOverride(string reference, string param, string typed, string? reason)
    {
        ArgumentNullException.ThrowIfNull(reference);
        ArgumentNullException.ThrowIfNull(param);
        ArgumentNullException.ThrowIfNull(typed);
        return TemplateOp((s, t) => TemplateOps.SetOverride(s, t, reference, param, typed, reason));
    }

    /// <summary>Reset an override to the template's own value (the override is removed).</summary>
    public AuthoringResult ResetOverride(string reference, string param)
    {
        ArgumentNullException.ThrowIfNull(reference);
        ArgumentNullException.ThrowIfNull(param);
        return TemplateOp((s, t) => TemplateOps.ResetOverride(s, t, reference, param));
    }

    /// <summary>Reset the selected override node's override (the registry's Reset override).</summary>
    public AuthoringResult ResetSelectedOverride()
        => _selected is { Kind: AuthorNodeKind.TemplateOverride, TemplateRef: string r, OverrideParam: string p }
            ? ResetOverride(r, p)
            : Show(AuthoringResult.Refused("Select an override (under its template in TEMPLATES) to reset it to the template's value."));

    /// <summary>Set a local template's title or description.</summary>
    public AuthoringResult SetTemplateText(string reference, string field, string typed)
    {
        ArgumentNullException.ThrowIfNull(reference);
        ArgumentNullException.ThrowIfNull(field);
        ArgumentNullException.ThrowIfNull(typed);
        TemplateInfo? info = Templates().Find(reference);
        if (info is not { IsLocal: true }) return Show(AuthoringResult.Refused(reference + " is not a local template of the case; only those are edited here."));
        return TemplateOp((s, t) => TemplateOps.SetText(s, t, info.LocalIndex, field, typed));
    }

    /// <summary>Select the node of template <paramref name="reference"/> (the generator editor's link). False when the tree has none.</summary>
    public bool OpenTemplate(string reference)
    {
        ArgumentNullException.ThrowIfNull(reference);
        if (TemplateNode(reference) is not AuthorNode node) return false;
        Select(node);
        return true;
    }

    /// <summary>
    /// Load the case as it is now with the v2 loader (a staged copy; nothing is written) and show the effective
    /// parameters on the selected generator's editor. Kept until the next change. Returns the load.
    /// </summary>
    public TemplateResolution ShowEffectiveParameters()
    {
        TemplateResolution r = TemplateResolution.Load(Authoring, TemplateDataRoot);
        _resolution = r;
        RefreshContent();
        if (r.Error is string e) Note("Effective parameters: the v2 loader refuses the case as it is now: " + e, isError: true);
        else Note("Effective parameters loaded with the v2 loader (case as it is now, nothing written).", isError: false);
        return r;
    }

    // ======================================== status ========================================

    /// <summary>The last action's result, in words (refusals start with what did not happen).</summary>
    public string Status
    {
        get => _status;
        private set => Set(ref _status, value);
    }

    /// <summary>True when <see cref="Status"/> reports a refusal or a failed check.</summary>
    public bool StatusIsError
    {
        get => _statusIsError;
        private set => Set(ref _statusIsError, value);
    }

    private AuthoringResult Show(AuthoringResult r)
    {
        StatusIsError = !r.Ok;
        Status = r.Message;
        if (r.Ok && r.Select is CaseRef s) SelectRecord(s);
        return r;
    }

    private AuthoringReport Show(AuthoringReport r)
    {
        StatusIsError = r.Outcome is not (AuthoringOutcome.Saved or AuthoringOutcome.CheckPassed or AuthoringOutcome.NothingToSave);
        Status = string.Join(Environment.NewLine, r.Lines());
        RefreshContent();   // the summary and the CHECK sub-tab follow the report
        Raise(nameof(ModeLabel));
        Raise(nameof(IsDirty));
        return r;
    }

    // ======================================== actions ========================================

    /// <summary>Undo the last change.</summary>
    public AuthoringResult Undo() => Show(Authoring.Undo());

    /// <summary>Redo the last undone change.</summary>
    public AuthoringResult Redo() => Show(Authoring.Redo());

    /// <summary>Check the case (no write, no solve); the result shows on Home and on each record's CHECK sub-tab.</summary>
    public AuthoringReport Check() => Show(Authoring.Check());

    /// <summary>Save in place (refused for a bundled case and a case with an overlay).</summary>
    public AuthoringReport Save(bool confirmNewWarnings = false) => Show(Authoring.Save(confirmNewWarnings));

    /// <summary>Save As into <paramref name="folder"/> (a new or empty folder of the user's own).</summary>
    public AuthoringReport SaveAs(string folder, bool confirmNewWarnings = false) => Show(Authoring.SaveAs(folder, confirmNewWarnings));

    /// <summary>Commit a field grid row's typed text (one undo step, or refused with the reason).</summary>
    public AuthoringResult CommitField(FieldRow row)
    {
        ArgumentNullException.ThrowIfNull(row);
        return Show(row.Commit());
    }

    /// <summary>Reset a field grid row to absent.</summary>
    public AuthoringResult ResetField(FieldRow row)
    {
        ArgumentNullException.ThrowIfNull(row);
        return Show(row.Reset());
    }

    /// <summary>Edit a field of the selected record by name (as the grid's row would).</summary>
    public AuthoringResult EditField(string field, string typed)
    {
        FieldGridModel grid = _fields ?? throw new InvalidOperationException("No record is selected, so there is no field to edit.");
        return Show(grid.Edit(field, typed));
    }

    // The record of the selected node, or a refusal naming what is selected.
    private CaseRef? SelectedRecord => _selected is { Kind: AuthorNodeKind.Record, Record: CaseRef r } ? r : null;

    // ======================================== Phase 5f: Derive star legs ========================================

    /// <summary>True when the selected node is a transformer3W overlay record (Derive star legs is offered).</summary>
    public bool CanDeriveStarLegs => SelectedRecord is CaseRef r && _selected!.Role == CaseRoles.Overlay && Authoring.OverlayIdentity(r).Kind == StarTransformerKind;

    // Phase 5c follow-up: the topology warnings of the overlay as the session holds it now, for the CHECK tab (the same
    // projection the OVERVIEW, the checker and Run mode use): every substation's for a null id, else the warnings of the
    // substation record <paramref name="overlayId"/> is or belongs to (none when it is neither). Each names the substation
    // record's file and pointer, as a Check problem does.
    private IReadOnlyList<AuthoringProblem> TopologyWarningsNow(string? overlayId)
    {
        if (!Authoring.HasOverlay) return Array.Empty<AuthoringProblem>();
        IReadOnlyList<global::GridSim.Core.IO.Json.Overlay.AssetOverlayRecord> records = Authoring.OverlayRecordsNow();
        IEnumerable<global::GridSim.Core.IO.Json.Overlay.SubstationTopologyWarning> warnings = overlayId is null
            ? global::GridSim.Core.IO.Json.Overlay.SubstationTopology.All(records).SelectMany(t => t.Warnings)
            : SubstationTopologyView.For(overlayId, records)?.Topology.Warnings
              ?? (IEnumerable<global::GridSim.Core.IO.Json.Overlay.SubstationTopologyWarning>)Array.Empty<global::GridSim.Core.IO.Json.Overlay.SubstationTopologyWarning>();
        return warnings.Select(w => new AuthoringProblem(w.Document, w.Pointer, w.Message)).ToList();
    }

    // The overlay kind Derive star legs acts on (the OVERVIEW panel, the tree's context menu and the command agree on it).
    private const string StarTransformerKind = "transformer3W";

    /// <summary>The star-legs proposal shown on the selected record's OVERVIEW, or null.</summary>
    public StarLegsProposal? StarLegs => _starLegs;

    /// <summary>
    /// Derive star legs (Phase 5f): PROPOSE the six leg edits of the selected transformer3W record from the session as it
    /// is now, old -> new, and show them on its OVERVIEW. Nothing is changed: <see cref="ApplyStarLegs"/> writes them,
    /// on the user's confirmation, as one undo step. A refused proposal shows why.
    /// </summary>
    public StarLegsProposal DeriveStarLegs()
    {
        if (!CanDeriveStarLegs || SelectedRecord is not CaseRef r)
            throw new InvalidOperationException("Derive star legs acts on a selected transformer3W overlay record.");
        _starLegs = StarLegsHelper.Propose(Authoring, r);
        StatusIsError = !_starLegs.Derivation.Ok;
        Status = _starLegs.Refusal ?? "Derive star legs: " + _starLegs.Edits.Count.ToString(Inv) + " leg edits proposed for '" + _starLegs.OverlayId
            + "' (not applied: Apply writes them as one change, one undo step; the solver keeps the authored values until then).";
        RefreshContent();
        return _starLegs;
    }

    /// <summary>Apply the shown star-legs proposal (the user's confirmation): the six leg values as ONE undo step.
    /// Refused, nothing changed, when there is no proposal or the case changed since it was made.</summary>
    public AuthoringResult ApplyStarLegs()
    {
        if (_starLegs is not StarLegsProposal p) return Show(AuthoringResult.Refused("Derive star legs first: there is no proposal to apply."));
        AuthoringResult result = StarLegsHelper.Apply(Authoring, p);
        _starLegs = null;   // applied or refused, the proposal is spent (a change rebuilds the page anyway)
        RefreshContent();
        return Show(result);
    }

    /// <summary>Drop the shown star-legs proposal without applying it.</summary>
    public void CancelStarLegs()
    {
        if (_starLegs is null) return;
        _starLegs = null;
        Status = "Derive star legs: the proposal was dropped; nothing was changed.";
        StatusIsError = false;
        RefreshContent();
    }

    /// <summary>True when the selected node is a record (Duplicate and Delete act on it).</summary>
    public bool HasSelectedRecord => SelectedRecord is not null;

    /// <summary>True when the selected record has a key to rename (bus, line, transformer, asset class).</summary>
    public bool SelectedHasKey => SelectedRecord is CaseRef r && RecordActions.KeyField(Authoring, r) is not null;

    /// <summary>Duplicate the selected record.</summary>
    public AuthoringResult DuplicateSelected() => SelectedRecord is CaseRef r
        ? Show(RecordActions.Duplicate(Authoring, r))
        : Show(AuthoringResult.Refused("Select a record to duplicate."));

    /// <summary>Delete the selected record (refused, listing the referrers, while anything refers to it).</summary>
    public AuthoringResult DeleteSelected() => SelectedRecord is CaseRef r
        ? Show(RecordActions.Delete(Authoring, r))
        : Show(AuthoringResult.Refused("Select a record to delete."));

    /// <summary>Rename the selected record's key (with <paramref name="dryRun"/>, say what would follow and change nothing).</summary>
    public AuthoringResult RenameSelected(string typed, bool dryRun = false)
    {
        ArgumentNullException.ThrowIfNull(typed);
        return SelectedRecord is CaseRef r
            ? Show(RecordActions.Rename(Authoring, r, typed, dryRun))
            : Show(AuthoringResult.Refused("Select a bus, line, transformer or asset class to rename."));
    }

    /// <summary>The current key text of the selected record ("12", "L1-2"), or "" (for the rename prompt).</summary>
    public string SelectedKeyText
    {
        get
        {
            if (SelectedRecord is not CaseRef r || RecordActions.KeyField(Authoring, r) is not string key || _fields is null) return "";
            FieldRow row = _fields.Rows.First(x => x.Field == key);
            string lit = row.Literal;
            return lit.Length >= 2 && lit[0] == '"' && lit[^1] == '"' ? System.Text.Json.JsonSerializer.Deserialize<string>(lit) ?? "" : lit;
        }
    }

    /// <summary>The collection a class node adds to (its first: v2 adds to the first network file holding the role).</summary>
    public static AuthoringRecordSet CollectionOf(AuthorNode classNode)
    {
        ArgumentNullException.ThrowIfNull(classNode);
        if (classNode.Kind != AuthorNodeKind.Class || classNode.Collections.Count == 0)
            throw new ArgumentException($"{classNode} is not a class node.", nameof(classNode));
        return classNode.Collections[0];
    }

    /// <summary>The class node an Add acts on: the selected class node, or the class of the selected record; null otherwise.</summary>
    public AuthorNode? AddTarget => _selected switch
    {
        // Phase 5b: the overlay has its own add (AddOverlayRecord), by kind; the model Add never acts on it.
        { Role: CaseRoles.Overlay } => null,
        { Kind: AuthorNodeKind.Class } c => c,
        { Kind: AuthorNodeKind.Record, Parent: AuthorNode p } => p,
        _ => null,
    };

    // ======================================== the asset overlay (Phase 5b) ========================================

    /// <summary>
    /// Where "Add overlay record..." acts for the selected node: the selected overlay record (a child of it), the selected
    /// model asset (a record attached to it), or null for the "Asset overlay" class node (a record of any kind). Only
    /// meaningful while <see cref="CanAddOverlay"/>.
    /// </summary>
    public CaseRef? OverlayAddAt => _selected is { Kind: AuthorNodeKind.Record, Record: CaseRef r } ? r : null;

    /// <summary>True when "Add overlay record..." can act on the selected node: the case has an overlay file, and the
    /// node is the overlay class node, an overlay record or a model asset an overlay record attaches to.</summary>
    public bool CanAddOverlay => OverlayActions.HasOverlayFile(Authoring) && _selected switch
    {
        { Kind: AuthorNodeKind.Class, Role: CaseRoles.Overlay } => true,
        { Kind: AuthorNodeKind.Record, Role: CaseRoles.Overlay } => true,
        { Kind: AuthorNodeKind.Record } n => n.OverlayAnchor,
        _ => false,
    };

    /// <summary>The kinds an overlay record may be added as at the selected node (empty when none).</summary>
    public IReadOnlyList<OverlayKindChoice> OverlayKindChoices() => CanAddOverlay ? OverlayActions.KindsFor(Authoring, OverlayAddAt) : Array.Empty<OverlayKindChoice>();

    /// <summary>What the Add prompt asks for a new overlay record of <paramref name="kind"/> at the selected node.</summary>
    public IReadOnlyList<NewRecordField> OverlayFieldsFor(string kind) => OverlayActions.FieldsFor(Authoring, OverlayAddAt, kind);

    /// <summary>Add an overlay record of <paramref name="kind"/> at the selected node from the prompt's values (one undo
    /// step; the new record is selected), or refuse with the reason.</summary>
    public AuthoringResult AddOverlayRecord(string kind, IReadOnlyList<KeyValuePair<string, string>> values)
    {
        ArgumentNullException.ThrowIfNull(kind);
        ArgumentNullException.ThrowIfNull(values);
        if (!CanAddOverlay) return Show(AuthoringResult.Refused("Select the Asset overlay class, an overlay record, or a bus, line, transformer, generator or demand to add an overlay record (the case must list an overlay file)."));
        return Show(OverlayActions.Add(Authoring, OverlayAddAt, kind, values));
    }

    /// <summary>Add a record to <paramref name="classNode"/>'s collection from typed values.</summary>
    public AuthoringResult AddRecord(AuthorNode classNode, IReadOnlyList<KeyValuePair<string, string>> values)
    {
        ArgumentNullException.ThrowIfNull(classNode);
        ArgumentNullException.ThrowIfNull(values);
        return Show(RecordActions.Add(Authoring, CollectionOf(classNode), values));
    }

    /// <summary>Select the record a where-used row names. False when the tree holds none (a nested record's holder is
    /// selected instead when it has a node).</summary>
    public bool OpenWhereUsed(WhereUsedRow row)
    {
        ArgumentNullException.ThrowIfNull(row);
        return SelectRecord(row.Record);
    }

    /// <summary>
    /// Phase 5c: open what a click on symbol <paramref name="symbolId"/> of the substation topology diagram stands for:
    /// a section or device selects its overlay record; a bay's far end selects the model record the bay switches (the
    /// line, transformer, generator or demand of that name, when exactly one record of that role carries it), else the
    /// device's own record. False when the symbol stands for no record of the tree.
    /// </summary>
    public bool OpenFromDiagram(string symbolId)
    {
        ArgumentNullException.ThrowIfNull(symbolId);
        string id = OverlaySubstationDiagram.RecordIdOf(symbolId);
        AuthorNode? node = PreOrder().FirstOrDefault(n => n.Kind == AuthorNodeKind.Record && n.Role == CaseRoles.Overlay
            && n.Record is CaseRef r && string.Equals(Authoring.OverlayIdentity(r).Id, id, StringComparison.Ordinal));
        if (node is null) return false;
        if (!string.Equals(id, symbolId, StringComparison.Ordinal) && FeederRecord(id) is CaseRef model && NodeOf(model) is AuthorNode target)
        {
            Select(target);
            return true;
        }
        Select(node);
        return true;
    }

    // The model record a feeder device switches: the record of the role its circuit reference names, by its name.
    private CaseRef? FeederRecord(string deviceId)
    {
        Core.IO.Json.Overlay.AssetOverlayRecord? dev = Authoring.OverlayRecordsNow().FirstOrDefault(r => string.Equals(r.Id, deviceId, StringComparison.Ordinal));
        Core.IO.Json.Overlay.AssetOverlayRef? feeder = dev?.Refs.FirstOrDefault(x => Core.IO.Json.Overlay.SubstationTopology.FeederRoles.Contains(x.Role, StringComparer.Ordinal) && x.Key is not null);
        if (feeder is null) return null;
        string role = feeder.Target switch
        {
            Core.IO.Json.Overlay.AssetRefTarget.Line => CaseRoles.Lines,
            Core.IO.Json.Overlay.AssetRefTarget.Transformer => CaseRoles.Transformers,
            Core.IO.Json.Overlay.AssetRefTarget.Generator => CaseRoles.Generators,
            Core.IO.Json.Overlay.AssetRefTarget.Demand => CaseRoles.Demands,
            _ => "",
        };
        List<AuthoringRecord> hits = Authoring.Collections().Where(c => c.Role == role).SelectMany(Authoring.Records)
            .Where(r => r.Label == "'" + feeder.Key + "'").ToList();
        return hits.Count == 1 ? hits[0].Ref : null;
    }

    /// <summary>Show CONNECTIONS for the selected record when it has any (the "Where used" command); false otherwise.</summary>
    public bool ShowWhereUsed()
    {
        if (!_subTabs.Contains(AssetNavigatorModel.ConnectionsTab, StringComparer.Ordinal))
        {
            Show(AuthoringResult.Refused(SelectedRecord is null ? "Select a record to see where it is used." : "Nothing refers to " + Authoring.Describe(SelectedRecord) + "."));
            return false;
        }
        SelectedSubTab = AssetNavigatorModel.ConnectionsTab;
        return true;
    }

    /// <summary>
    /// UI programme Phase 4.2: show the result of an edit made elsewhere (a map edit on the NETWORK tab, MapEditing) as
    /// this model shows its own: the message in the status line (a refusal as an error) and, on success, the selection
    /// follows the record the edit made or moved. Returns <paramref name="result"/>.
    /// </summary>
    public AuthoringResult Report(AuthoringResult result)
    {
        ArgumentNullException.ThrowIfNull(result);
        return Show(result);
    }

    /// <summary>Report a refusal or a note from the shell (a cancelled prompt, a reason) in the status line.</summary>
    public void Note(string message, bool isError)
    {
        ArgumentNullException.ThrowIfNull(message);
        StatusIsError = isError;
        Status = message;
    }
}
