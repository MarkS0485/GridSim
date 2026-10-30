using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using GridSim.Core.IO.Json.Overlay;
using GridSim.Core.IO.Json.Schema;
using GridSim.Core.Licensing;
using GridSim.Core.Model;
using GridSim.Core.Presentation;
using GridSim.Core.Simulation;
using GridSim.Ui.Assets.Editing;
using GridSim.Ui.Assets.Index;
using GridSim.Ui.Assets.Keys;
using GridSim.Ui.Assets.Overlay;
using GridSim.Ui.Assets.Overviews;
using GridSim.Ui.Assets.Pages;
using GridSim.Ui.Dashboard;

namespace GridSim.Ui.Assets.Navigation;

/// <summary>
/// The ASSETS tab's navigator (UI programme ASSETS A3, plan section 3.2; UI-neutral, no Avalonia): the explorer tree of
/// the solved model, the selection, search, back / forward history, the crumb, the sub-tabs over the page and the
/// Active gate. It drives the A2 explorer (<see cref="NavigableAssetExplorer"/>): selecting a node opens that node's page
/// through the explorer's own Open* route (the call the WPF explorer makes for the same click), so every value a page
/// shows is the A2 page's, made by the same code.
///
/// <para>How it keeps in step with the pages. A page can open another page itself (a fleet row, a substation's plant or
/// connection row, a unit page's bus link): the A2 pages call the explorer directly. The navigator watches the explorer's
/// current page; when it changes without the navigator having asked, the new page is looked up by the key the explorer
/// recorded when it made it, the tree node for that key is selected (none when the tree holds no node for it) and the
/// page joins the history. Before each navigation of its own the navigator returns the explorer to its landing page
/// (GoHome), so the explorer's stack never grows with tree clicks; the history holds the page objects themselves, so going
/// back shows the same page with its trace, as the WPF BACK button did.</para>
///
/// <para>Fed once per frame by the dashboard's tab hook (<see cref="Update"/>, the WPF <c>Assets.Update(f, Clock)</c>,
/// first of the hooks). A new model rebuilds the tree only when its structure differs (<see cref="AssetTree.SameStructure"/>);
/// the selection, expansion state and search are kept across the rebuild by node id.</para>
/// </summary>
public sealed class AssetNavigatorModel : ObservableModel
{
    /// <summary>The most pages the back / forward history keeps (the oldest is dropped beyond this).</summary>
    public const int HistoryLimit = 100;

    /// <summary>The most search matches listed (the note says how many there were in all).</summary>
    public const int SearchHitLimit = 500;

    /// <summary>The OVERVIEW sub-tab: the A2 page (today's WPF page).</summary>
    public const string OverviewTab = "OVERVIEW";

    /// <summary>The PROPERTIES sub-tab (UI programme ASSETS B2): the case record's fields, read-only in Run mode.</summary>
    public const string PropertiesTab = "PROPERTIES";

    /// <summary>The CONNECTIONS sub-tab (UI programme ASSETS B2): the model adjacency and the case's where-used list.</summary>
    public const string ConnectionsTab = "CONNECTIONS";

    /// <summary>The OVERLAY sub-tab (UI programme ASSETS slice 5-view): the overlay records attached to a model asset.</summary>
    public const string OverlayTab = "OVERLAY";

    /// <summary>The CHECK sub-tab (slice 5-view: an overlay record's problems from the overlay checker; Author mode adds
    /// the case records' own in Phase 4).</summary>
    public const string CheckTab = "CHECK";

    /// <summary>
    /// Every sub-tab the plan names, in order (plan section 3.2). OVERVIEW always has content; PROPERTIES and CONNECTIONS
    /// have content since B2 (once the shell has declared the model's source, <see cref="SetSource"/>); OVERLAY and CHECK
    /// since slice 5-view, when the shell hands the case's asset overlay with the source (OVERLAY on a model asset that
    /// overlay records attach to; PROPERTIES and CHECK on an overlay record, CHECK only when the checker found problems).
    /// A sub-tab with nothing to show is hidden, not shown empty.
    /// </summary>
    public static readonly IReadOnlyList<string> AllSubTabs = new[] { OverviewTab, PropertiesTab, ConnectionsTab, OverlayTab, CheckTab };

    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    // ---- UI programme ASSETS B1/B2: the source, the index and the sub-tab content ----
    private AssetCaseSource _source = AssetCaseSource.Undeclared;
    private LicenceGate? _licence;
    private AssetIndex? _index;        // the index of the current structure, once built
    private bool _indexWanted;         // a source is declared and the index of the current model is still to be built
    private bool _awaitingFrame;       // a source was declared and its model's first frame has not arrived yet
    private SimFrame? _frame;          // the last frame (the PROPERTIES solved column reads it)
    private IReadOnlyList<string> _subTabs = new[] { OverviewTab };
    private AssetProperties? _properties;
    private AssetConnections _connections = AssetConnections.None;
    private string _subTabNote = "";
    // ---- slice 5-view: the overlay handed with the source, and the overlay sub-tab content of the selected node ----
    private CaseAssetOverlay? _overlay;
    private IReadOnlyList<SchemaError>? _overlayProblems;
    private AssetOverlayList? _overlayList;
    private OverlayRecordProperties? _overlayProperties;
    private OverlayCheck? _overlayCheck;

    private readonly List<HistoryEntry> _history = new();
    private int _at = -1;            // the history entry on screen (-1 before the first)
    private int _suppress;           // > 0 while the navigator itself is moving the explorer
    private bool _currentChanged;    // the explorer's page changed during a navigation of the navigator's own
    private AssetTree _tree = AssetTree.Empty();
    private AssetTreeNode? _selected;
    private GridModel? _model;
    private string _searchText = "";
    private string _searchNote = "";

    /// <summary>A navigator over an explorer of the neutral pages.</summary>
    public AssetNavigatorModel() : this(new NavigableAssetExplorer()) { }

    /// <summary>A navigator over <paramref name="explorer"/> (a front end passes one that makes its own page types).</summary>
    public AssetNavigatorModel(NavigableAssetExplorer explorer)
    {
        Explorer = explorer ?? throw new ArgumentNullException(nameof(explorer));
        Explorer.PropertyChanged += OnExplorerChanged;
        _selected = _tree.Home;
        Record(_tree.Home, Explorer.Home);
    }

    /// <summary>The A2 explorer this navigator drives (its pages, its stack, its frame feed).</summary>
    public NavigableAssetExplorer Explorer { get; }

    /// <summary>The page on screen (the explorer's current page): what the OVERVIEW sub-tab shows.</summary>
    public AssetPageModel Current => Explorer.Current;

    /// <summary>The tree for the model of the last frame (before any frame: the landing page and the empty classes).</summary>
    public AssetTree Tree => _tree;

    /// <summary>The tree's top-level nodes (what the view's tree shows).</summary>
    public IReadOnlyList<AssetTreeNode> Roots => _tree.Roots;

    /// <summary>The model of the last frame, or null before the first.</summary>
    public GridModel? Model => _model;

    /// <summary>The selected tree node: the node of the page on screen, or null when no node stands for it.</summary>
    public AssetTreeNode? Selected => _selected;

    /// <summary>The crumb: the selected node's tree path ("SUBSTATIONS > 400 kV > DRAX"), or the page's own title when
    /// no tree node stands for the page.</summary>
    public string Crumb => _selected?.Path ?? Current.Title;

    /// <summary>The sim-time strip every page is aligned to (the explorer's: "SIM hh:mm  TICK n").</summary>
    public string TimeLabel => Explorer.TimeLabel;

    /// <summary>The session mode the tab is in: "RUN", or (UI programme Phase 4.1) "AUTHOR", with " *" while the case
    /// being edited has unsaved changes.</summary>
    public string ModeLabel => _author is null ? "RUN" : _author.ModeLabel;

    // ======================================== Author mode (UI programme Phase 4.1) ========================================

    private Editing.AssetAuthorModel? _author;
    private string _modeNote = "";
    private bool _activeAsked;   // the shell's Active gate, kept while AUTHOR holds the explorer inactive

    /// <summary>The Author mode's model while the tab is in AUTHOR, else null (Run mode).</summary>
    public Editing.AssetAuthorModel? Author => _author;

    /// <summary>True while the tab is in AUTHOR mode: the Run pages (every live value) are not shown.</summary>
    public bool IsAuthoring => _author is not null;

    /// <summary>True while the tab is in RUN mode (the A3 layout and its live pages are shown).</summary>
    public bool IsRunMode => _author is null;

    /// <summary>
    /// Whether this head offers Author mode at all (default true, the desktop; a mobile head sets it false: mobile is Run
    /// mode only, plan Phase 3).
    /// </summary>
    public bool AuthorSupported { get; set; } = true;

    /// <summary>
    /// Why AUTHOR cannot be entered now, in words the header shows (plan Q9: a MATPOWER file, a replay and the bridge are
    /// not case files; nothing loaded yet; a head without Author mode), or null when it can.
    /// </summary>
    public string? AuthorUnavailableReason => !AuthorSupported
        ? "AUTHOR is desktop only: this app is Run mode only (the mobile sandbox solves a case; the case editor is on the desktop)."
        : Editing.CaseAuthoring.UnavailableReason(_source);

    /// <summary>True when AUTHOR can be entered now.</summary>
    public bool CanAuthor => AuthorUnavailableReason is null;

    /// <summary>The toggle's second text: what a click does ("switch to AUTHOR", "switch to RUN", or "AUTHOR unavailable"
    /// when the source has no case file to edit).</summary>
    public string ModeSwitchLabel => _author is not null ? "switch to RUN" : CanAuthor ? "switch to AUTHOR" : "AUTHOR unavailable";

    /// <summary>A note the header shows beside the mode toggle (why AUTHOR was not entered, why RUN was not resumed), or "".</summary>
    public string ModeNote
    {
        get => _modeNote;
        set
        {
            string v = value ?? "";
            if (string.Equals(v, _modeNote, StringComparison.Ordinal)) return;
            _modeNote = v;
            Raise(nameof(ModeNote));
            Raise(nameof(HasModeNote));
        }
    }

    /// <summary>True when <see cref="ModeNote"/> has something to say.</summary>
    public bool HasModeNote => _modeNote.Length > 0;

    /// <summary>
    /// Open the running model's case for authoring, under the licence gate its load passed. Throws
    /// <see cref="Editing.CaseAuthoringException"/> with the reason when AUTHOR is not available (<see cref="AuthorUnavailableReason"/>)
    /// or the case cannot be opened. <paramref name="bundledRoots"/> and <paramref name="userCasesFolder"/> as
    /// <see cref="Editing.CaseAuthoring.Open"/> (null: the real ones).
    /// </summary>
    public Editing.CaseAuthoring OpenAuthoring(IReadOnlyList<string>? bundledRoots = null, string? userCasesFolder = null)
    {
        if (AuthorUnavailableReason is string why) throw new Editing.CaseAuthoringException(why);
        if (_licence is null) throw new Editing.CaseAuthoringException("AUTHOR is not available: the case's licence gate is not known yet (no load has declared it).");
        return Editing.CaseAuthoring.Open(_source, _licence, bundledRoots, userCasesFolder);
    }

    /// <summary>
    /// Enter AUTHOR with <paramref name="author"/> (the shell opened the case and paused the run). From now on the tab
    /// shows the case file's records only: no frame is read and no live value is shown (plan Q5). The explorer stops
    /// refreshing its pages.
    /// </summary>
    public void EnterAuthor(Editing.AssetAuthorModel author)
    {
        ArgumentNullException.ThrowIfNull(author);
        if (_author is not null) throw new InvalidOperationException("The ASSETS tab is already in AUTHOR mode.");
        _author = author;
        _author.PropertyChanged += OnAuthorChanged;
        if (Explorer.Active)
        {
            Explorer.Active = false;   // no per-frame page refresh while authoring (nothing live is on screen)
            Raise(nameof(Active));
        }
        ModeNote = "";
        RaiseMode();
    }

    /// <summary>
    /// Leave AUTHOR (the shell has saved or discarded and will reload the case if it was saved). The Run layout comes
    /// back; the Active gate is the shell's again (it re-applies it).
    /// </summary>
    public void LeaveAuthor()
    {
        if (_author is null) throw new InvalidOperationException("The ASSETS tab is not in AUTHOR mode.");
        _author.PropertyChanged -= OnAuthorChanged;
        _author = null;
        RaiseMode();
        Active = _activeAsked;   // the shell's gate again (the ASSETS tab is still shown: catch up at once)
    }

    private void OnAuthorChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(Editing.AssetAuthorModel.ModeLabel)) Raise(nameof(ModeLabel));
    }

    private void RaiseMode()
    {
        Raise(nameof(Author));
        Raise(nameof(IsAuthoring));
        Raise(nameof(IsRunMode));
        Raise(nameof(ModeLabel));
        Raise(nameof(CanAuthor));
        Raise(nameof(AuthorUnavailableReason));
        Raise(nameof(ModeSwitchLabel));
        Raise(nameof(CanShowOnMap));
    }

    /// <summary>
    /// The sub-tabs over the page that have something to show, in the plan's order: OVERVIEW always; PROPERTIES when the
    /// selected node stands for a record of the case file; CONNECTIONS when it has a connection in the model or a
    /// where-used entry in the case. Until a source is declared (<see cref="SetSource"/>) only OVERVIEW, as in slice A3.
    /// </summary>
    public IReadOnlyList<string> SubTabs => _subTabs;

    private string _selectedSubTab = OverviewTab;

    /// <summary>The sub-tab shown. Only one of <see cref="SubTabs"/>; anything else is refused.</summary>
    public string SelectedSubTab
    {
        get => _selectedSubTab;
        set
        {
            if (!SubTabs.Contains(value, StringComparer.Ordinal))
                throw new ArgumentException($"'{value}' is not a sub-tab of this page (shown: {string.Join(", ", SubTabs)})", nameof(value));
            if (string.Equals(value, _selectedSubTab, StringComparison.Ordinal)) return;
            Set(ref _selectedSubTab, value);
            RaiseShown();
            if (value == PropertiesTab) RefreshProperties();
        }
    }

    /// <summary>True while OVERLAY is the sub-tab shown (slice 5-view).</summary>
    public bool IsOverlayShown => _selectedSubTab == OverlayTab;

    /// <summary>True while CHECK is the sub-tab shown (slice 5-view).</summary>
    public bool IsCheckShown => _selectedSubTab == CheckTab;

    /// <summary>The OVERLAY content of the selected model asset (its attached overlay records), or null.</summary>
    public AssetOverlayList? OverlayList => _overlayList;

    /// <summary>The PROPERTIES content of a selected overlay record (the record as written), or null.</summary>
    public OverlayRecordProperties? OverlayProperties => _overlayProperties;

    /// <summary>The CHECK content of a selected overlay record (its checker problems), or null.</summary>
    public OverlayCheck? OverlayCheck => _overlayCheck;

    /// <summary>The asset overlay handed with the source (slice 5-view), or null.</summary>
    public CaseAssetOverlay? SourceOverlay => _overlay;

    /// <summary>True while OVERVIEW is the sub-tab shown (the page host is visible).</summary>
    public bool IsOverviewShown => _selectedSubTab == OverviewTab;

    /// <summary>True while PROPERTIES is the sub-tab shown.</summary>
    public bool IsPropertiesShown => _selectedSubTab == PropertiesTab;

    /// <summary>True while CONNECTIONS is the sub-tab shown.</summary>
    public bool IsConnectionsShown => _selectedSubTab == ConnectionsTab;

    private void RaiseShown()
    {
        Raise(nameof(IsOverviewShown));
        Raise(nameof(IsPropertiesShown));
        Raise(nameof(IsConnectionsShown));
        Raise(nameof(IsOverlayShown));
        Raise(nameof(IsCheckShown));
    }

    /// <summary>The PROPERTIES content of the selected node (its case record's fields), or null when it has none.</summary>
    public AssetProperties? Properties => _properties;

    /// <summary>The CONNECTIONS content of the selected node (empty when it has none).</summary>
    public AssetConnections Connections => _connections;

    /// <summary>
    /// Why PROPERTIES is not offered at all (plan Q9): the model's source is not a case file (MATPOWER, a replay, the GDA
    /// bridge), or the case could not be opened or does not build the running model. "" otherwise, and before a source
    /// is declared.
    /// </summary>
    public string SubTabNote => _subTabNote;

    /// <summary>True when <see cref="SubTabNote"/> has something to say.</summary>
    public bool HasSubTabNote => _subTabNote.Length > 0;

    /// <summary>The model's declared source (<see cref="AssetCaseSource.Undeclared"/> until the shell declares it).</summary>
    public AssetCaseSource Source => _source;

    /// <summary>The asset index of the current model, or null (no source declared, no frame yet, or not yet built because
    /// the tab has not been shown since the model arrived).</summary>
    public AssetIndex? Index => _index;

    /// <summary>
    /// The Active gate (the WPF <c>Assets.Active</c>): set by the shell when the ASSETS tab is the selected tab. While
    /// false the explorer skips the per-frame page refresh; turning it on refreshes the page at once (catch up on show).
    /// </summary>
    public bool Active
    {
        get => Explorer.Active;
        set
        {
            // Phase 4.1: while the tab is in AUTHOR the explorer stays inactive (nothing live is on screen); the shell's
            // gate is remembered and applied when the tab goes back to RUN.
            _activeAsked = value;
            if (_author is not null) return;
            if (Explorer.Active == value) return;
            Explorer.Active = value;
            Raise(nameof(Active));
            // ASSETS B2: an index left to build while the tab was hidden is built now; the PROPERTIES solved column
            // catches up with the last frame.
            if (value)
            {
                EnsureIndex();
                RefreshProperties();
            }
        }
    }

    // ======================================== the source (ASSETS B1/B2) ========================================

    /// <summary>
    /// Declare what the running model was read from (the shell calls it on every load, before the first frame of the new
    /// model), with the licence gate the load passed. From then on the tab builds an <see cref="AssetIndex"/> for each
    /// model structure, the first time the tab is shown with that model: for a case file the case is opened READ-ONLY
    /// (records, schema field specs, where-used), and the tree gains the B2 class nodes and the PROPERTIES / CONNECTIONS
    /// sub-tabs. Building is deferred while the tab is hidden, so a load costs nothing until ASSETS is opened.
    /// <para>UI programme ASSETS slice 5-view: <paramref name="overlay"/> is the case's asset overlay as the v2 loader
    /// carried it beside the model (null for none: carried, not used by the solver) and <paramref name="overlayProblems"/>
    /// the overlay checker's problems for it (none for an overlay the loader accepted). With an overlay the tree gains the
    /// OVERLAY class node and the records nested under the model assets they attach to, and the OVERLAY / CHECK
    /// sub-tabs; nothing else on the tab changes.</para>
    /// </summary>
    public void SetSource(AssetCaseSource source, LicenceGate licence, CaseAssetOverlay? overlay = null, IReadOnlyList<SchemaError>? overlayProblems = null)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(licence);
        _source = source;
        _licence = licence;
        _overlay = overlay;
        _overlayProblems = overlayProblems;
        bool had = _index is not null;
        _index = null;
        _indexWanted = source.Kind != AssetSourceKind.Undeclared;
        if (had && _model is not null) Rebuild(AssetTree.Build(_model));   // the old index's nodes must go
        // The index is built on the next frame (the shell declares the source before the new model's first frame, so
        // the model held now may still be the previous case's).
        _awaitingFrame = true;
        RefreshSubTabs();
        Raise(nameof(Source));
        Raise(nameof(Index));
        // Phase 4.1: whether AUTHOR is available follows the source (plan Q9).
        Raise(nameof(CanAuthor));
        Raise(nameof(AuthorUnavailableReason));
        Raise(nameof(ModeSwitchLabel));
        Raise(nameof(CanShowOnMap));
    }

    // Build the index of the current model when one is wanted and the tab is shown, then the tree over it.
    private void EnsureIndex()
    {
        if (!_indexWanted || _awaitingFrame || _model is null || !Explorer.Active) return;
        _indexWanted = false;
        _index = AssetIndex.Build(_model, _source, _licence!, _overlay, _overlayProblems);
        Rebuild(_index.BuildTree());
        Raise(nameof(Index));
    }

    // ======================================== the frame feed ========================================

    /// <summary>
    /// One solved frame, with the dashboard's clock (the WPF <c>Assets.Update(f, Clock)</c>): a model of a new structure
    /// rebuilds the tree first, then the explorer takes the frame (only the page on screen refreshes, and only while
    /// <see cref="Active"/>).
    /// </summary>
    public void Update(SimFrame frame, string clock)
    {
        ArgumentNullException.ThrowIfNull(frame);
        ArgumentNullException.ThrowIfNull(clock);
        _frame = frame;
        _awaitingFrame = false;
        if (!ReferenceEquals(frame.Model, _model))
        {
            bool same = AssetTree.SameStructure(_model, frame.Model);
            _model = frame.Model;
            if (!same)
            {
                // ASSETS B2: a new structure needs a new index (when a source is declared); built at once while the tab
                // shows (one rebuild, over the index), else when it is next shown.
                bool hadIndex = _index is not null;
                _index = null;
                _indexWanted = _source.Kind != AssetSourceKind.Undeclared;
                if (_indexWanted && Explorer.Active) EnsureIndex();
                else Rebuild(AssetTree.Build(frame.Model));
                if (hadIndex) Raise(nameof(Index));
            }
        }
        EnsureIndex();   // a source declared since the last frame (or a tab shown again) builds its index now
        Explorer.Update(frame, clock);
        if (Explorer.Active && IsPropertiesShown) RefreshProperties();   // the solved column follows the frame
    }

    // A new tree: keep the selection (by id, if the node still opens the same page; else by the page on screen), the
    // expansion of every node that still exists, and the search.
    private void Rebuild(AssetTree tree)
    {
        var expanded = new HashSet<string>(_tree.PreOrder().Where(n => n.IsExpanded).Select(n => n.Id), StringComparer.Ordinal);
        string? selectedId = _selected?.Id;
        _tree = tree;
        foreach (AssetTreeNode n in _tree.PreOrder())
            if (expanded.Contains(n.Id)) n.IsExpanded = true;
        AssetTreeNode? keep = selectedId is null ? null : _tree.ById(selectedId);
        AssetPageKey? onScreen = Explorer.KeyOf(Current);
        if (keep is null || keep.Page != onScreen) keep = _tree.NodeFor(onScreen);
        Raise(nameof(Tree));
        Raise(nameof(Roots));
        SetSelected(keep);
        if (_searchText.Length > 0) RunSearch();
    }

    // ======================================== selection and navigation ========================================

    /// <summary>
    /// Select <paramref name="node"/> (a node of the current <see cref="Tree"/>) and open its page by its A2 route:
    /// the landing page by GoHome, a class or group by the landing card's route (Open(Create...Fleet())), a unit by its
    /// Open* call (OpenSubstation, OpenGenerator, OpenInterconnector, OpenCircuit, OpenTransformer; a tap changer by its
    /// TAP CHANGERS row's route). Returns false when the node is already selected with its page on screen.
    /// </summary>
    public bool Select(AssetTreeNode node)
    {
        ArgumentNullException.ThrowIfNull(node);
        if (!_tree.Contains(node))
            throw new ArgumentException($"{node} is not a node of the current asset tree (the tree was rebuilt for a new model)", nameof(node));
        if (ReferenceEquals(node, _selected) && Explorer.KeyOf(Current) == node.Page) return false;
        Navigate(node.Route);
        // ASSETS A3 follow-up (2026-10-24): a voltage-level group opens the SUBSTATIONS page filtered to its level, and the
        // class node opens it unfiltered (so coming back to the class after a group is never left filtered).
        if (Current is IKvFilteredFleet kvFleet) kvFleet.ApplyKvFilter(node.GroupKv ?? double.NaN);
        if (Current is IBandFilteredFleet bandFleet) bandFleet.ApplyBandFilter(node.GroupBand ?? 0);   // a band group likewise
        Record(node, Current);
        SetSelected(node);
        return true;
    }

    /// <summary>
    /// Open the substation page of bus <paramref name="busId"/> by the WPF route (<c>Assets.OpenSubstation(id)</c>: the
    /// map's OPEN IN EXPLORER on a bus, a SCADA bus-voltage point) and select its node.
    /// </summary>
    public void OpenSubstation(int busId) => OpenBy(x => x.OpenSubstation(busId));

    /// <summary>
    /// Open the page of <paramref name="branch"/> by the WPF route (<c>Assets.OpenBranch(br)</c>: a transformer page for a
    /// transformer, else a circuit page; the map's OPEN IN EXPLORER on a branch, a SCADA branch or tap point) and select
    /// its node.
    /// </summary>
    public void OpenBranch(Branch branch)
    {
        ArgumentNullException.ThrowIfNull(branch);
        OpenBy(x => x.OpenBranch(branch));
    }

    private void OpenBy(Action<AssetExplorerModel> route)
    {
        Navigate(route);
        Record(_tree.NodeFor(Explorer.KeyOf(Current)), Current);
        SetSelected(_tree.NodeFor(Explorer.KeyOf(Current)));
    }

    /// <summary>Select the substation node of bus <paramref name="busId"/> ("go to bus id"). False when the tree holds
    /// no such bus.</summary>
    public bool GoToBus(int busId)
    {
        AssetTreeNode? node = _tree.Substation(busId);
        if (node is null) return false;
        Select(node);
        return true;
    }

    // Move the explorer by the navigator's own hand: back to its landing page, then the route (if any). The explorer's
    // page changes are not treated as page-initiated while this runs; one Current notification is raised at the end.
    private void Navigate(Action<AssetExplorerModel>? route)
    {
        _suppress++;
        try
        {
            Explorer.GoHome();
            route?.Invoke(Explorer);
        }
        finally
        {
            _suppress--;
        }
        if (_currentChanged)
        {
            _currentChanged = false;
            Raise(nameof(Current));
        }
    }

    // The explorer moved. A page opened from inside another page (a row click, a cross-link) is the user navigating:
    // select its node and add it to the history.
    private void OnExplorerChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(AssetExplorerModel.TimeLabel)) { Raise(nameof(TimeLabel)); return; }
        if (e.PropertyName != nameof(AssetExplorerModel.Current)) return;
        if (_suppress > 0) { _currentChanged = true; return; }
        Raise(nameof(Current));
        AssetTreeNode? node = _tree.NodeFor(Explorer.KeyOf(Current));
        Record(node, Current);
        SetSelected(node);
    }

    private void SetSelected(AssetTreeNode? node)
    {
        // Open the path to the node so the view's tree can show it.
        for (AssetTreeNode? p = node?.Parent; p is not null; p = p.Parent) p.IsExpanded = true;
        Set(ref _selected, node, nameof(Selected));
        Raise(nameof(Crumb));
        Raise(nameof(MapTarget));
        Raise(nameof(CanShowOnMap));
        RefreshSubTabs();
    }

    // ======================================== the sub-tabs (ASSETS B2) ========================================

    // Work out which sub-tabs the selected node has content for, and their content. Called on every selection, source
    // and tree change. Without an index (no source declared, or not built yet) only OVERVIEW, exactly as slice A3.
    private void RefreshSubTabs()
    {
        var tabs = new List<string> { OverviewTab };
        AssetConnections connections = AssetConnections.None;
        string note = "";
        AssetOverlayList? overlayList = null;
        OverlayRecordProperties? overlayProperties = null;
        OverlayCheck? overlayCheck = null;
        if (_index is not null)
        {
            if (_selected is not null)
            {
                // Slice 5-view: an overlay record's PROPERTIES is the record as written (it has no case record).
                if (_index.Overlay is { } ov) overlayProperties = OverlayPages.Properties(ov, _selected.Key);
                if (_index.RecordOf(_selected.Key) is not null || overlayProperties is not null) tabs.Add(PropertiesTab);
                connections = _index.Connections(_selected.Key);
                if (connections.Any) tabs.Add(ConnectionsTab);
                if (_index.Overlay is { } ov2)
                {
                    overlayList = OverlayPages.AttachedList(ov2, _selected.Key);
                    if (overlayList is not null) tabs.Add(OverlayTab);
                    overlayCheck = OverlayPages.Check(ov2, _selected.Key);
                    if (overlayCheck is not null) tabs.Add(CheckTab);
                }
            }
            if (!_index.HasCase && _index.CaseNote is string why) note = why;
        }
        // The overlay content follows the selection (a new object only when the content changed).
        if (!SameList(overlayList, _overlayList)) { _overlayList = overlayList; Raise(nameof(OverlayList)); }
        if (!SameProperties(overlayProperties, _overlayProperties)) { _overlayProperties = overlayProperties; Raise(nameof(OverlayProperties)); }
        if (!SameCheck(overlayCheck, _overlayCheck)) { _overlayCheck = overlayCheck; Raise(nameof(OverlayCheck)); }
        if (!tabs.SequenceEqual(_subTabs, StringComparer.Ordinal))
        {
            _subTabs = tabs;
            Raise(nameof(SubTabs));
        }
        if (!tabs.Contains(_selectedSubTab, StringComparer.Ordinal))
        {
            _selectedSubTab = OverviewTab;   // the page shown has no such sub-tab: back to its overview
            Raise(nameof(SelectedSubTab));
            RaiseShown();
        }
        _connections = connections;
        Raise(nameof(Connections));
        if (!string.Equals(note, _subTabNote, StringComparison.Ordinal))
        {
            _subTabNote = note;
            Raise(nameof(SubTabNote));
            Raise(nameof(HasSubTabNote));
        }
        RefreshProperties();
    }

    // Records holding lists compare their lists by reference; these compare the rows (slice 5-view).
    private static bool SameList(AssetOverlayList? a, AssetOverlayList? b) =>
        a is null || b is null ? a is null && b is null : a.Heading == b.Heading && a.Rows.SequenceEqual(b.Rows);

    private static bool SameProperties(OverlayRecordProperties? a, OverlayRecordProperties? b) =>
        a is null || b is null ? a is null && b is null : a.Heading == b.Heading && a.Kind == b.Kind && a.Rows.SequenceEqual(b.Rows);

    private static bool SameCheck(OverlayCheck? a, OverlayCheck? b) =>
        a is null || b is null ? a is null && b is null : a.Heading == b.Heading && a.Rows.SequenceEqual(b.Rows) && a.Warnings.SequenceEqual(b.Warnings)
            && a.Notes.SequenceEqual(b.Notes);

    // The PROPERTIES content of the selected node, with the solved column from the last frame. Made only while the tab
    // is shown (or about to be), so a hidden tab costs nothing per frame.
    private void RefreshProperties()
    {
        AssetProperties? p = null;
        if (_index is not null && _selected is not null && _subTabs.Contains(PropertiesTab, StringComparer.Ordinal))
            p = _index.Properties(_selected.Key, _frame);
        if (ReferenceEquals(p, _properties)) return;
        _properties = p;
        Raise(nameof(Properties));
    }

    /// <summary>
    /// Select the node whose key is <paramref name="key"/> and open its page (a CONNECTIONS link, a B2 page's row). False,
    /// and nothing changes, when the tree holds no node for it.
    /// </summary>
    public bool OpenKey(AssetKey key)
    {
        ArgumentNullException.ThrowIfNull(key);
        AssetTreeNode? node = _tree.NodeFor(key);
        if (node is null) return false;
        Select(node);
        return true;
    }

    /// <summary>A click on a CONNECTIONS link: select the node it names. False when it names none.</summary>
    public bool OpenConnection(ConnectionRow row)
    {
        ArgumentNullException.ThrowIfNull(row);
        return row.Target is AssetKey k && OpenKey(k);
    }

    /// <summary>Raised by <see cref="OpenInverterFleet"/>: the shell switches to the INVERTER FLEET tab.</summary>
    public event EventHandler? InverterFleetRequested;

    /// <summary>OPEN INVERTER FLEET on an asset class or binding page: ask the shell to show the INVERTER FLEET tab (the
    /// live roll-up of the authored classes). False when nothing listens.</summary>
    public bool OpenInverterFleet()
    {
        if (InverterFleetRequested is not { } h) return false;
        h(this, EventArgs.Empty);
        return true;
    }

    // ======================================== back / forward ========================================

    private sealed record HistoryEntry(string? NodeId, AssetPageKey? Key, AssetPageModel Page);

    /// <summary>True when there is a page to go back to.</summary>
    public bool CanGoBack => _at > 0;

    /// <summary>True when there is a page to go forward to.</summary>
    public bool CanGoForward => _at >= 0 && _at < _history.Count - 1;

    /// <summary>How many pages the history holds (for tests and the view's tool tip).</summary>
    public int HistoryCount => _history.Count;

    // Add the page on screen to the history (after the entry on screen; anything forward of it is dropped), unless it
    // is already the entry on screen.
    private void Record(AssetTreeNode? node, AssetPageModel page)
    {
        if (_at >= 0 && ReferenceEquals(_history[_at].Page, page)) return;
        if (_at < _history.Count - 1) _history.RemoveRange(_at + 1, _history.Count - _at - 1);
        _history.Add(new HistoryEntry(node?.Id, Explorer.KeyOf(page), page));
        if (_history.Count > HistoryLimit) _history.RemoveAt(0);
        _at = _history.Count - 1;
        RaiseHistory();
    }

    /// <summary>Show the previous page of the history (the same page object, so its trace is kept). False at the start.</summary>
    public bool GoBack()
    {
        if (!CanGoBack) return false;
        Show(_at - 1);
        return true;
    }

    /// <summary>Show the next page of the history. False at the end.</summary>
    public bool GoForward()
    {
        if (!CanGoForward) return false;
        Show(_at + 1);
        return true;
    }

    private void Show(int at)
    {
        HistoryEntry e = _history[at];
        _at = at;
        AssetPageModel page = e.Page;
        Navigate(x => { if (!ReferenceEquals(page, x.Home)) x.Open(page); });
        AssetTreeNode? node = e.NodeId is null ? null : _tree.ById(e.NodeId);
        if (node is null || node.Page != e.Key) node = _tree.NodeFor(e.Key);
        SetSelected(node);
        RaiseHistory();
    }

    private void RaiseHistory()
    {
        Raise(nameof(CanGoBack));
        Raise(nameof(CanGoForward));
        Raise(nameof(HistoryCount));
    }

    // ======================================== search ========================================

    /// <summary>The asset matches of <see cref="SearchText"/>, in tree order (at most <see cref="SearchHitLimit"/>).</summary>
    public ObservableCollection<AssetTreeNode> SearchHits { get; } = new();

    /// <summary>True while a search is typed: the view shows <see cref="SearchHits"/> in place of the tree.</summary>
    public bool IsSearching => _searchText.Trim().Length > 0;

    /// <summary>What the search found, in words ("12 matches", "showing 500 of 1,234 matches", "no asset matches 'x'").</summary>
    public string SearchNote => _searchNote;

    /// <summary>
    /// The search: every node (not the landing page) whose label, kind words or bus ids contain the text, ignoring case
    /// (ordinal; the SchemaEditor's "/" search rule). A number also matches the bus ids a node touches (a bus, a unit's
    /// bus, a branch's ends).
    /// </summary>
    public string SearchText
    {
        get => _searchText;
        set
        {
            string v = value ?? "";
            if (string.Equals(v, _searchText, StringComparison.Ordinal)) return;
            _searchText = v;
            Raise(nameof(SearchText));
            RunSearch();
        }
    }

    private void RunSearch()
    {
        string q = _searchText.Trim();
        SearchHits.Clear();
        if (q.Length > 0)
        {
            int total = 0;
            foreach (AssetTreeNode n in _tree.PreOrder())
            {
                // Slice 5-view: an overlay record is listed once (its canonical node), not again for each asset it
                // attaches to.
                if (n.Role == AssetNodeRole.Home || n.IsAttachment || !Matches(n, q)) continue;
                total++;
                if (SearchHits.Count < SearchHitLimit) SearchHits.Add(n);
            }
            _searchNote = total == 0 ? $"no asset matches '{q}'"
                : total > SearchHitLimit ? string.Create(Inv, $"showing {SearchHitLimit} of {total:N0} matches")
                : total == 1 ? "1 match" : string.Create(Inv, $"{total:N0} matches");
        }
        else _searchNote = "";
        Raise(nameof(SearchNote));
        Raise(nameof(IsSearching));
    }

    private static bool Matches(AssetTreeNode n, string q)
    {
        if (n.Label.Contains(q, StringComparison.OrdinalIgnoreCase)) return true;
        if (n.Kind.Contains(q, StringComparison.OrdinalIgnoreCase)) return true;
        foreach (int id in n.BusIds)
            if (id.ToString(Inv).Contains(q, StringComparison.Ordinal)) return true;
        return false;
    }

    /// <summary>
    /// The search box's Enter: a whole number that is a bus id goes straight to that bus ("go to bus id"); otherwise the
    /// first match is selected. Either way the search is cleared so the tree shows the selection. False (and nothing
    /// changes) when nothing matches.
    /// </summary>
    public bool SubmitSearch()
    {
        string q = _searchText.Trim();
        if (q.Length == 0) return false;
        bool done = int.TryParse(q, NumberStyles.Integer, Inv, out int id) && GoToBus(id);
        if (!done && SearchHits.Count > 0) { Select(SearchHits[0]); done = true; }
        if (done) SearchText = "";
        return done;
    }

    // ======================================== show on map ========================================

    /// <summary>
    /// Where the page on screen is on the network map: its bus (a substation; a generator's or link's bus) or its branch
    /// (a circuit or transformer, by its index in the current model, confirmed by ends and name), or null (home, fleets,
    /// a unit that is not in the current model).
    /// </summary>
    public AssetMapTarget? MapTarget
    {
        get
        {
            AssetPageKey? key = Explorer.KeyOf(Current);
            if (key is null || _model is null) return null;
            switch (key.Kind)
            {
                case AssetPageKind.Substation:
                    return _model.Buses.Any(b => b.Id == key.BusA) ? new AssetMapTarget(key.BusA, null) : null;
                case AssetPageKind.Generator:
                case AssetPageKind.Interconnector:
                    return AssetLookup.FindGenerator(_model, key.Index, key.BusA, key.Name) is { } g
                        ? new AssetMapTarget(g.Unit.Bus, null) : null;
                case AssetPageKind.Circuit:
                case AssetPageKind.Transformer:
                    if (key.Index < 0) return null;
                    return AssetLookup.FindBranch(_model, key.Index, key.BusA, key.BusB, key.Name) is { } b
                        ? new AssetMapTarget(null, b.Index) : null;
                // ASSETS B2: a demand record (the bus it folds into), a protection (its monitored bus) and an
                // out-of-service unit (the bus it would connect to) have a place on the map: their bus.
                case AssetPageKind.Demand:
                case AssetPageKind.Protection:
                case AssetPageKind.OutOfServiceUnit:
                    return _model.Buses.Any(b => b.Id == key.BusA) ? new AssetMapTarget(key.BusA, null) : null;
                // Phase 5d: a line route's page has a place on the map: its polyline on the Line routes layer (framed
                // there), with the line it carries as the branch (selected when the route is not drawn).
                case AssetPageKind.OverlayRecord:
                    return LineRouteTarget(key);
                default:
                    return null;
            }
        }
    }

    // Phase 5d: the map target of a line route record's page (the route id, and the carried line's branch index when it
    // resolves in the current model); null for any other overlay record.
    private AssetMapTarget? LineRouteTarget(AssetPageKey key)
    {
        if (key.Name is null || _index?.Overlay?.Find(key.Name) is not { Kind: LineRoutes.RouteKind } route) return null;
        int? branch = route.Refs.FirstOrDefault(l => l.Role == "line")?.Target?.Model?.FindBranch(_model!)?.Index;
        return new AssetMapTarget(null, branch) { LineRouteId = route.Id };
    }

    /// <summary>True when the page on screen has a place on the map (<see cref="MapTarget"/>).</summary>
    public bool CanShowOnMap => MapTarget is not null && _author is null;   // Phase 4.1: not in AUTHOR (no model selection there)

    /// <summary>Raised by <see cref="ShowOnMap"/> with the bus or branch to select on the NETWORK map.</summary>
    public event EventHandler<AssetMapTarget>? ShowOnMapRequested;

    /// <summary>Ask the shell to switch to NETWORK and select the page's bus or branch. False when it has none.</summary>
    public bool ShowOnMap()
    {
        if (_author is not null || MapTarget is not AssetMapTarget t) return false;
        ShowOnMapRequested?.Invoke(this, t);
        return true;
    }

    // ======================================== the toolkit diagrams ========================================

    /// <summary>
    /// A click on a symbol of the page's toolkit diagram (<see cref="AssetDiagrams"/>): a substation's connection bay
    /// ("C{i}/...") or generator bay ("G{k}/...") opens what that connection's or plant row's click opens; a
    /// transformer's HV or LV busbar opens what the HV or LV bus link opens. The new page's node is then selected as for
    /// any page opened from inside a page. False when the symbol stands for no asset (the busbar of the substation itself,
    /// the demand, a breaker of the transformer bay).
    /// </summary>
    public bool OpenFromDiagram(string symbolId)
    {
        ArgumentNullException.ThrowIfNull(symbolId);
        // Slice 5-view: a substation overlay record's diagram: a section or device opens its record; a bay's far end opens
        // the model asset the device switches.
        if (Current is AssetOverviewPage { OverlayRecord: { } rec } page && page.Index.Overlay is { } ov)
            return OverlaySubstationDiagram.TargetOf(ov, rec, symbolId) is AssetKey k && OpenKey(k);
        return AssetDiagrams.Open(Current, symbolId);
    }
}
