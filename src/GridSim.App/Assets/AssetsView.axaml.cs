// HOW TO FEED (UI programme ASSETS A3; integrated 2026-10-20).
// The view shows the window's AssetNavigatorModel (TabModels.Assets), bound in MainWindow.axaml:
//     <assets:AssetsView x:Name="AssetsTab" DataContext="{Binding Tabs.Assets}" />
// It never feeds itself: the navigator is fed by the dashboard's tab hook (TabModels.Update, ASSETS first, as the WPF
// UpdateTabs), and the window sets its Active gate when the ASSETS tab is selected.
using System.ComponentModel;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;
using GridSim.App.Controls.Diagram;
using GridSim.Ui.Assets.Index;
using GridSim.Ui.Assets.Navigation;
using GridSim.Ui.Assets.Overviews;
using GridSim.Ui.Assets.Pages;
using GridSim.Ui.Diagrams;

namespace GridSim.App.Assets;

/// <summary>
/// The ASSETS tab (UI programme ASSETS A3): the split layout of the ASSETS plan (search, crumb, back / forward and the
/// time strip over the explorer tree and the page), every value bound to the A2 page models through the navigator. The
/// WPF commands are click handlers here, each running the A2 action or method behind the WPF command of the same place:
/// a row or card runs its <c>OnOpen</c>, a filter chip its <c>OnSelect</c> / <c>OnToggle</c>, a unit page's bus link
/// its Open...Substation method with the id bound to the button's CommandParameter (read at click time, as in WPF).
/// The tree, the search box, the header buttons and the toolkit diagrams talk to the navigator.
/// </summary>
public sealed partial class AssetsView : UserControl
{
    private bool _syncingTree;   // true while the tree's selection is moved to the navigator's (not a user's click)
    private AssetNavigatorModel? _watched;

    public AssetsView()
    {
        InitializeComponent();
        // Enter in the search box: go to the bus with that id, else the first match. Tunnelling, so the search runs
        // whatever the text box itself does with Enter.
        SearchBox.AddHandler(KeyDownEvent, (_, e) =>
        {
            if (e.Key != Key.Enter) return;
            Navigator.SubmitSearch();
            e.Handled = true;
        }, RoutingStrategies.Tunnel);
        DataContextChanged += (_, _) => WatchNavigator();
    }

    /// <summary>The navigator behind the view: a click with no navigator behind it is a wiring mistake and fails loud.</summary>
    public AssetNavigatorModel Navigator => DataContext as AssetNavigatorModel
        ?? throw new InvalidOperationException("The ASSETS view has no AssetNavigatorModel as its DataContext.");

    // ---- the tree follows the navigator's selection (a page opened from inside a page selects its node) ----

    private void WatchNavigator()
    {
        if (_watched is not null) _watched.PropertyChanged -= OnNavigatorChanged;
        _watched = DataContext as AssetNavigatorModel;
        if (_watched is not null) _watched.PropertyChanged += OnNavigatorChanged;
        SyncTree();
    }

    private void OnNavigatorChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(AssetNavigatorModel.Selected) or nameof(AssetNavigatorModel.Roots)) SyncTree();
    }

    private void SyncTree()
    {
        // ASSETS A3 follow-up (2026-10-24): the rows are virtualised, so the selected node's row may not exist yet, and a
        // tree cannot select a row it has not made. Its path is made first (scrolled into view level by level; the
        // navigator has already expanded its parents), then it is selected, and once the tree has laid out its header row
        // (not its whole expanded subtree) is brought into view again.
        if (this.IsAttachedToVisualTree() && _watched?.Selected is AssetTreeNode toShow) ScrollIntoView(toShow);
        _syncingTree = true;
        try { Tree.SelectedItem = _watched?.Selected; }
        finally { _syncingTree = false; }
        if (_watched?.Selected is AssetTreeNode selected)
            Dispatcher.UIThread.Post(() => ScrollIntoView(selected), DispatcherPriority.Background);
    }

    /// <summary>
    /// Bring the header row of <paramref name="node"/>'s tree item into view, if it is still the selected node. The rows
    /// are virtualised, so the item may not exist yet: the path is walked from the root, each level asked to scroll its
    /// child into view (which makes that child's item) before the child's own item is looked in.
    /// </summary>
    internal void ScrollIntoView(AssetTreeNode node)
    {
        if (!ReferenceEquals(_watched?.Selected, node)) return;
        var path = new List<AssetTreeNode>();
        for (AssetTreeNode? n = node; n is not null; n = n.Parent) path.Insert(0, n);
        ItemsControl level = Tree;
        Control? item = null;
        foreach (AssetTreeNode step in path)
        {
            level.ScrollIntoView(step);
            level.UpdateLayout();
            if (level.ContainerFromItem(step) is not TreeViewItem tvi) return;   // not shown (a collapsed or filtered path)
            item = tvi;
            level = tvi;
        }
        item?.BringIntoView(new Rect(0, 0, item.Bounds.Width, Math.Min(item.Bounds.Height, TreeRowHeight)));
    }

    // The height of one tree row's header (the item's own line, without its children): what is brought into view.
    private const double TreeRowHeight = 32;

    // A user's click on a tree node opens its page (the navigator's Select).
    private void Tree_SelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (_syncingTree || !ReferenceEquals(e.Source, Tree)) return;
        if (Tree.SelectedItem is AssetTreeNode node && _watched is { } nav && nav.Tree.Contains(node)) nav.Select(node);
    }

    // A search match: open it, as a click on its tree node.
    private void Hit_Click(object? sender, RoutedEventArgs e)
    {
        if ((sender as Control)?.DataContext is AssetTreeNode node) Navigator.Select(node);
    }

    // ---- the header ----

    private void Home_Click(object? sender, RoutedEventArgs e) => Navigator.Select(Navigator.Tree.Home);

    private void Back_Click(object? sender, RoutedEventArgs e) => Navigator.GoBack();

    private void Forward_Click(object? sender, RoutedEventArgs e) => Navigator.GoForward();

    private void ShowOnMap_Click(object? sender, RoutedEventArgs e) => Navigator.ShowOnMap();

    // ---- UI programme Phase 4.1: the RUN / AUTHOR toggle and the AUTHOR pane ----

    private AuthorModeController? _authorMode;

    /// <summary>The window's Author mode (the toggle and the AUTHOR pane run it). Set by the window when it wires the tab.</summary>
    public AuthorModeController? AuthorMode
    {
        get => _authorMode;
        set
        {
            _authorMode = value;
            AuthorPane.AuthorMode = value;
        }
    }

    /// <summary>The window's context menu builder (the AUTHOR tree's right-click menus come from the registry).</summary>
    public Func<GridSim.Ui.Commands.CommandTargets, ContextMenu?>? ContextMenuProvider
    {
        get => AuthorPane.ContextMenuProvider;
        set => AuthorPane.ContextMenuProvider = value;
    }

    /// <summary>The last mode switch the toggle started (null before the first), so a test can wait for it.</summary>
    public Task<bool>? PendingModeSwitch { get; private set; }

    // The toggle: AUTHOR from RUN (refused with the reason shown beside it for a source that is not a case file), RUN from
    // AUTHOR (Save / Discard / Cancel for unsaved changes, then the saved case is reloaded). With no Author mode wired the
    // click is a wiring mistake and fails loud.
    private void ModeToggle_Click(object? sender, RoutedEventArgs e)
    {
        AuthorModeController mode = _authorMode
            ?? throw new InvalidOperationException("The ASSETS tab's RUN / AUTHOR toggle has no Author mode wired (the window sets AssetsView.AuthorMode).");
        PendingModeSwitch = mode.ToggleAsync();
    }

    // ---- the pages: each WPF command's action ----

    // A card or row (the WPF Open command): its OnOpen action. Every row type that opens a page is listed; any other
    // data context under an Open_Click button is a wiring mistake in the AXAML and fails loud.
    private void Open_Click(object? sender, RoutedEventArgs e)
    {
        object? row = (sender as Control)?.DataContext;
        Action open = row switch
        {
            AssetClassCardModel c => c.OnOpen,
            LinkFleetRowModel r => r.OnOpen,
            GenFleetRowModel r => r.OnOpen,
            PhasorTileModel r => r.OnOpen,
            TransformerFleetRowModel r => r.OnOpen,
            TapChangerRowModel r => r.OnOpen,
            CircuitFleetRowModel r => r.OnOpen,
            SubstationFleetRowModel r => r.OnOpen,
            PlantRowModel r => r.OnOpen,
            ConnectionRowModel r => r.OnOpen,
            _ => throw new InvalidOperationException($"An ASSETS open button sits on a {row?.GetType().Name ?? "null"}, which opens nothing."),
        };
        open();
    }

    // A filter chip (the WPF Select / Toggle commands): the band chip's OnSelect, the kV chip's OnToggle.
    private void Chip_Click(object? sender, RoutedEventArgs e)
    {
        object? chip = (sender as Control)?.DataContext;
        Action pick = chip switch
        {
            BandChipModel b => b.OnSelect,
            KvFilterChipModel k => k.OnToggle,
            _ => throw new InvalidOperationException($"An ASSETS chip sits on a {chip?.GetType().Name ?? "null"}."),
        };
        pick();
    }

    // The bus id a link button carries (its CommandParameter, bound as in WPF to the page's live id property).
    private static int LinkId(object? sender) => (sender as Button)?.CommandParameter is int id
        ? id
        : throw new InvalidOperationException("An ASSETS link button carries no bus id.");

    private static T PageOf<T>(object? sender) where T : class => (sender as Control)?.DataContext as T
        ?? throw new InvalidOperationException($"An ASSETS link button is not on a {typeof(T).Name}.");

    // The WPF OpenLanding command (interconnector page): the landing substation.
    private void Landing_Click(object? sender, RoutedEventArgs e) => PageOf<InterconnectorDetailModel>(sender).OpenLandingSubstation(LinkId(sender));

    // The WPF OpenBus command (generator page): the connected bus.
    private void GeneratorBus_Click(object? sender, RoutedEventArgs e) => PageOf<GeneratorDetailModel>(sender).OpenBusSubstation(LinkId(sender));

    // The WPF OpenHv, OpenLv and OpenOltcBus commands (transformer page).
    private void Hv_Click(object? sender, RoutedEventArgs e) => PageOf<TransformerDetailPage>(sender).OpenHvSubstation(LinkId(sender));

    private void Lv_Click(object? sender, RoutedEventArgs e) => PageOf<TransformerDetailPage>(sender).OpenLvSubstation(LinkId(sender));

    private void OltcBus_Click(object? sender, RoutedEventArgs e) => PageOf<TransformerDetailPage>(sender).OpenOltcBusSubstation(LinkId(sender));

    // The WPF OpenIn and OpenOut commands (circuit page).
    private void In_Click(object? sender, RoutedEventArgs e) => PageOf<CircuitDetailPage>(sender).OpenInSubstation(LinkId(sender));

    private void Out_Click(object? sender, RoutedEventArgs e) => PageOf<CircuitDetailPage>(sender).OpenOutSubstation(LinkId(sender));

    // A click on a symbol of a toolkit diagram (substation or transformer page): open what it stands for.
    private void Diagram_SymbolClicked(object? sender, DiagramHitEventArgs e)
    {
        if (e.Hit is { Kind: DiagramHitKind.Symbol } hit) Navigator.OpenFromDiagram(hit.Id);
    }

    // ---- UI programme ASSETS B2: the new class pages and the CONNECTIONS sub-tab ----

    // A row of a B2 class or member page: select the node it names (a row that names none is disabled in the AXAML).
    private void OverviewRow_Click(object? sender, RoutedEventArgs e)
    {
        OverviewRow row = (sender as Control)?.DataContext as OverviewRow
            ?? throw new InvalidOperationException("An ASSETS overview row button is not on an OverviewRow.");
        if (row.Target is { } key) Navigator.OpenKey(key);
    }

    // OPEN INVERTER FLEET on an asset class or binding page: the shell shows the INVERTER FLEET tab.
    private void InverterFleet_Click(object? sender, RoutedEventArgs e) => Navigator.OpenInverterFleet();

    // A CONNECTIONS link: select the node it names.
    private void Connection_Click(object? sender, RoutedEventArgs e)
    {
        ConnectionRow row = (sender as Control)?.DataContext as ConnectionRow
            ?? throw new InvalidOperationException("An ASSETS connection button is not on a ConnectionRow.");
        Navigator.OpenConnection(row);
    }
}
