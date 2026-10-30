using System.ComponentModel;
using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using GridSim.App.Assets;
using GridSim.Ui.Assets.Editing;
using GridSim.Ui.Assets.Keys;
using GridSim.Ui.Commands;
using GridSim.Ui.Schematic;

namespace GridSim.App.Map;

// UI programme Phase 4.2: map editing on the NETWORK map in AUTHOR mode (desktop only). While the ASSETS tab is in
// AUTHOR, this controller owns the map:
//   - the map shows the CASE BEING EDITED (MapEditing.Build: the session's bus, line and transformer records), never a
//     frame, so nothing on it is a live value (plan Q5); corridors are drawn uncoloured (no solved state) and the hover
//     tip names the record, its name and base kV only;
//   - the selection is ONE selection with the ASSETS tree: a click on a bus or a branch selects its record in the tree,
//     and a record selected in the tree is ringed (a bus) or highlighted (a line or transformer) on the map;
//   - the tools (Map menu): Select (click to select, drag a bus to move it), Place bus (click a point: the bus's
//     required fields are asked for, as Add record asks), Connect (click bus A then bus B: line or transformer is asked
//     for, then the required fields the two clicks do not give);
//   - Delete (the Delete key, Map > Delete, the map's context menu) deletes the selected bus, line or transformer; a
//     refused delete changes nothing and shows the engine's refusal, with its referrers, in the status bar and in the
//     Author status line.
// Every change goes through the authoring session (MapEditing / RecordActions: one engine transaction, one undo step),
// so undo and redo, the dirty state, Check, Save, Save As and the RUN reload work exactly as for any other Author edit;
// after every change the map is rebuilt from the session, keeping the view.
// Back in RUN the window restores the run's own map (its model, last state and boundary flows), or reloads the saved case
// through the run pipeline as AuthorModeController decides; the Run map's behaviour is unchanged.

/// <summary>
/// The NETWORK map in AUTHOR mode (UI programme Phase 4.2): the map of the case being edited, its tools and the
/// selection shared with the ASSETS tree. The window routes the map's AUTHOR gestures here while <see cref="IsActive"/>.
/// </summary>
public sealed class MapAuthorController : IMapAuthorActions
{
    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    private readonly MainWindowViewModel _vm;
    private readonly SchematicControl _map;
    private readonly Func<IAuthorPrompts> _prompts;
    private readonly Action _showAssets;
    private readonly Action _restoreRunMap;
    private readonly Func<CommandTargets, ContextMenu?> _contextMenuFor;
    private AssetAuthorModel? _author;
    private MapEditTool _tool = MapEditTool.Select;

    /// <summary>
    /// The Author map over the window's view model <paramref name="vm"/> and map control <paramref name="map"/>, asking
    /// its questions through <paramref name="prompts"/> (Author mode's own prompts), showing the ASSETS tab with
    /// <paramref name="showAssets"/>, giving the map back to the run with <paramref name="restoreRunMap"/>, and building
    /// the map element's context menu with <paramref name="contextMenuFor"/> (the registry's, in the current mode).
    /// </summary>
    public MapAuthorController(MainWindowViewModel vm, SchematicControl map, Func<IAuthorPrompts> prompts, Action showAssets,
        Action restoreRunMap, Func<CommandTargets, ContextMenu?> contextMenuFor)
    {
        _vm = vm ?? throw new ArgumentNullException(nameof(vm));
        _map = map ?? throw new ArgumentNullException(nameof(map));
        _prompts = prompts ?? throw new ArgumentNullException(nameof(prompts));
        _showAssets = showAssets ?? throw new ArgumentNullException(nameof(showAssets));
        _restoreRunMap = restoreRunMap ?? throw new ArgumentNullException(nameof(restoreRunMap));
        _contextMenuFor = contextMenuFor ?? throw new ArgumentNullException(nameof(contextMenuFor));
    }

    /// <summary>The map-editing commands registered for this controller (their enabled state and ticks are refreshed
    /// after every change of tool, selection or case).</summary>
    public IReadOnlyList<AppCommand> Commands { get; set; } = Array.Empty<AppCommand>();

    /// <summary>True while the map shows the case being edited (the window is in AUTHOR).</summary>
    public bool IsActive => _author is not null;

    /// <inheritdoc />
    public AssetAuthorModel? Author => _author;

    /// <inheritdoc />
    public MapEditTool Tool => _tool;

    /// <summary>The map of the case as it is now (null outside AUTHOR).</summary>
    public AuthorMap? Map { get; private set; }

    /// <summary>Connect's first bus once it has been clicked (null otherwise).</summary>
    public int? ConnectFrom { get; private set; }

    /// <inheritdoc />
    public bool HasMapSelection => SelectedOnMap is not null;

    // The selected record when it is drawn on the map (a bus, a line or a transformer), else null.
    private CaseRef? SelectedOnMap
        => _author?.Selected?.Record is CaseRef r && Map is AuthorMap m && (m.BusOf(r) is not null || m.BranchIndexOf(r) is not null) ? r : null;

    // ======================================== entering and leaving ========================================

    /// <summary>AUTHOR was entered with <paramref name="author"/>: the map shows the case being edited, editable.</summary>
    public void Enter(AssetAuthorModel author)
    {
        ArgumentNullException.ThrowIfNull(author);
        if (_author is not null) throw new InvalidOperationException("The map is already in AUTHOR mode.");
        _author = author;
        author.Authoring.Changed += OnCaseChanged;
        author.PropertyChanged += OnAuthorChanged;
        // The Run selection's fly-out holds live values: it is closed (AUTHOR shows none), as is the live hover tip.
        _vm.Map.ClearSelection();
        _vm.Map.HideHover();
        _tool = MapEditTool.Select;
        ConnectFrom = null;
        _map.EditConnectFrom = null;
        _map.EditMode = true;
        // The tiles around the map still hold the paused run's last values: veiled and labelled as such while AUTHOR lasts.
        _vm.Dashboard.AuthorPaused = true;
        Rebuild();
        RefreshCommands();
    }

    /// <summary>AUTHOR was left: the map goes back to the run (the window restores the run's map), unchanged.</summary>
    public void Leave()
    {
        if (_author is null) return;
        _author.Authoring.Changed -= OnCaseChanged;
        _author.PropertyChanged -= OnAuthorChanged;
        _author = null;
        Map = null;
        ConnectFrom = null;
        _tool = MapEditTool.Select;
        _map.EditMode = false;   // clears the marks and any bus drag
        _vm.Dashboard.AuthorPaused = false;
        _vm.Map.HideHover();
        _restoreRunMap();
        RefreshCommands();
    }

    // The case changed (an edit anywhere, an undo, a redo, a Save As): draw the case as it is now.
    private void OnCaseChanged(object? sender, EventArgs e) => Rebuild();

    // The tree's selection changed: ring or highlight it on the map.
    private void OnAuthorChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(AssetAuthorModel.Selected)) SyncFromTree();
    }

    // Rebuild the Author map from the session and load it into the map control, keeping the view.
    private void Rebuild()
    {
        AssetAuthorModel author = _author ?? throw new InvalidOperationException("The map is not in AUTHOR mode.");
        AuthorMap map = MapEditing.Build(author.Authoring);
        Map = map;
        _map.Load(map.Model, keepView: true);
        if (ConnectFrom is int a && map.Bus(a) is null) SetConnectFrom(null);   // the first bus went (an undo)
        SyncFromTree();
    }

    // Show the tree's selected record on the map (a bus ringed, a branch highlighted, anything else nothing).
    private void SyncFromTree()
    {
        CaseRef? r = _author?.Selected?.Record;
        AuthorMap? m = Map;
        _map.EditSelectedBus = r is not null && m?.BusOf(r) is AuthorMapBus b ? b.Id : null;
        _map.EditSelectedBranch = r is not null && m is not null ? m.BranchIndexOf(r) : null;
        RefreshCommands();
    }

    private void RefreshCommands()
    {
        foreach (AppCommand c in Commands)
        {
            c.RaiseCanExecuteChanged();
            c.RaiseCheckedChanged();
        }
    }

    private AssetAuthorModel Need() => _author ?? throw new InvalidOperationException("The map is not in AUTHOR mode.");

    // Show an edit's result: the Author status line (and the selection follows a made or moved record) and the window's
    // status bar (the map is on another tab than the Author status line).
    private AuthoringResult Show(AuthoringResult r)
    {
        Need().Report(r);
        _vm.StatusText = r.Message;
        return r;
    }

    // A note (a cancelled prompt, what a tool expects next): the Author status line and the status bar.
    private void Note(string message, bool isError)
    {
        Need().Note(message, isError);
        _vm.StatusText = message;
    }

    // ======================================== the gestures ========================================

    /// <summary>The map WORLD point under map point <paramref name="p"/> (the map's own inverse projection).</summary>
    public ScreenPoint WorldAt(Point p) => _map.View.ScreenToWorld(new ScreenPoint(p.X, p.Y));

    /// <summary>
    /// A left click on the map at <paramref name="p"/>, on bus <paramref name="bus"/> or (no bus) branch
    /// <paramref name="branch"/>, as the current tool means it: Select selects the record; Place bus adds a bus at the
    /// point (after its prompt); Connect takes the first bus, then joins the second to it (after its prompts).
    /// </summary>
    public async Task ClickAsync(Point p, int? bus, int? branch)
    {
        AssetAuthorModel author = Need();
        switch (_tool)
        {
            case MapEditTool.Select:
                SelectOnMap(bus, branch);
                break;
            case MapEditTool.PlaceBus:
                await PlaceBusAsync(author, WorldAt(p)).ConfigureAwait(true);
                break;
            case MapEditTool.Connect:
                await ConnectClickAsync(author, bus).ConfigureAwait(true);
                break;
            default:
                throw new InvalidOperationException("Unknown map tool " + _tool + ".");
        }
    }

    // Select the record of the clicked bus or branch in the ASSETS tree (the map follows the tree's selection).
    private void SelectOnMap(int? bus, int? branch)
    {
        AssetAuthorModel author = Need();
        AuthorMap map = Map!;
        CaseRef? r = bus is int b ? map.Bus(b)?.Record
            : branch is int i && (uint)i < (uint)map.Branches.Count ? map.Branches[i].Record : null;
        if (r is null) return;   // empty map: the selection stays as it is
        author.SelectRecord(r);
        _vm.StatusText = "Selected " + author.Authoring.Describe(r) + ".";
    }

    private async Task PlaceBusAsync(AssetAuthorModel author, ScreenPoint world)
    {
        (double x, double y) = MapEditing.CaseCoordinates(world.X, world.Y);
        IReadOnlyList<NewRecordField> fields = MapEditing.PlaceBusFields(author.Authoring);
        IReadOnlyList<KeyValuePair<string, string>>? values = await _prompts().AskFieldsAsync("Place bus",
            "A new bus at x " + MapEditing.Format(x) + ", y " + MapEditing.Format(y) + " (the clicked point). Its required fields "
            + "(every other field is absent, so it takes its default):", fields).ConfigureAwait(true);
        if (values is null)
        {
            Note("Place bus cancelled; nothing changed.", isError: false);
            return;
        }
        Show(MapEditing.PlaceBus(author.Authoring, values, world.X, world.Y));
    }

    private async Task ConnectClickAsync(AssetAuthorModel author, int? bus)
    {
        if (bus is not int b)
        {
            Note(ConnectFrom is null ? "Connect: click the first bus." : "Connect: click the second bus (Escape cancels).", isError: false);
            return;
        }
        if (ConnectFrom is not int a)
        {
            SetConnectFrom(b);
            SelectOnMap(b, null);
            Note("Connect: from bus " + b.ToString(Inv) + ". Now click the second bus (Escape cancels).", isError: false);
            return;
        }
        if (a == b)
        {
            Show(MapEditing.Connect(author.Authoring, MapBranchKind.Line, a, b, Array.Empty<KeyValuePair<string, string>>()));
            return;   // refused (the same bus twice); still waiting for the second bus
        }

        IAuthorPrompts prompts = _prompts();
        IReadOnlyList<KeyValuePair<string, string>>? kindAnswer = await prompts.AskFieldsAsync("Connect",
            "Join bus " + a.ToString(Inv) + " to bus " + b.ToString(Inv) + " with a line or a transformer:",
            new[] { new NewRecordField("kind", "kind", "Enum", new[] { "line", "transformer" }) }).ConfigureAwait(true);
        string? kindText = kindAnswer?.FirstOrDefault(kv => kv.Key == "kind").Value?.Trim();
        if (string.IsNullOrEmpty(kindText))
        {
            SetConnectFrom(null);
            Note("Connect cancelled; nothing changed.", isError: false);
            return;
        }
        MapBranchKind kind;
        if (string.Equals(kindText, "line", StringComparison.OrdinalIgnoreCase)) kind = MapBranchKind.Line;
        else if (string.Equals(kindText, "transformer", StringComparison.OrdinalIgnoreCase)) kind = MapBranchKind.Transformer;
        else
        {
            SetConnectFrom(null);
            Note("Connect refused: '" + kindText + "' is not line or transformer; nothing changed.", isError: true);
            return;
        }

        IReadOnlyList<NewRecordField> fields = MapEditing.ConnectFields(author.Authoring, kind, a);
        IReadOnlyList<KeyValuePair<string, string>> values = Array.Empty<KeyValuePair<string, string>>();
        if (fields.Count > 0)
        {
            IReadOnlyList<KeyValuePair<string, string>>? typed = await prompts.AskFieldsAsync("Connect: " + MapEditing.KindText(kind),
                "A new " + MapEditing.KindText(kind) + " from bus " + a.ToString(Inv) + " to bus " + b.ToString(Inv)
                + ". Its required fields (from and to are the clicked buses; every other field is absent, so it takes its default):",
                fields).ConfigureAwait(true);
            if (typed is null)
            {
                SetConnectFrom(null);
                Note("Connect cancelled; nothing changed.", isError: false);
                return;
            }
            values = typed;
        }
        SetConnectFrom(null);
        Show(MapEditing.Connect(author.Authoring, kind, a, b, values));
    }

    private void SetConnectFrom(int? bus)
    {
        ConnectFrom = bus;
        _map.EditConnectFrom = bus;
    }

    /// <summary>A bus was dragged and dropped at map point <paramref name="p"/>: move it there (one undo step).</summary>
    public AuthoringResult Drag(int busId, Point p)
    {
        AssetAuthorModel author = Need();
        ScreenPoint world = WorldAt(p);
        return Show(MapEditing.MoveBus(author.Authoring, busId, world.X, world.Y));
    }

    /// <summary>The hover tip for map point <paramref name="p"/>: the bus under it as the case writes it (no live value),
    /// or null (no bus there).</summary>
    public string? TipAt(Point p)
    {
        if (Map is not AuthorMap map || _map.HitTestNode(p) is not int id || map.Bus(id) is not AuthorMapBus b) return null;
        return "bus " + b.Id.ToString(Inv) + (b.Name is null ? "" : " '" + b.Name + "'") + "\n"
            + b.BaseKv.ToString("0.###", Inv) + " kV  (AUTHOR: the case as written, no live value)";
    }

    /// <summary>
    /// The map's context menu at <paramref name="p"/> in AUTHOR mode: on a bus or a branch, its record is selected (in
    /// the tree too) and the registry's map element menu is returned (Delete, Show in Assets); null on the empty map.
    /// </summary>
    public ContextMenu? ContextMenuAt(Point p)
    {
        Need();
        int? bus = _map.HitTestNode(p);
        int? branch = bus is null ? _map.HitTestBranch(p) : null;
        if (bus is null && branch is null) return null;
        SelectOnMap(bus, branch);
        RefreshCommands();
        return _contextMenuFor(CommandTargets.MapElement);
    }

    // ======================================== the commands (IMapAuthorActions) ========================================

    /// <inheritdoc />
    public Task ChooseToolAsync(MapEditTool tool)
    {
        if (!Enum.IsDefined(tool)) throw new ArgumentOutOfRangeException(nameof(tool), tool, "Not a map tool.");
        Need();
        _tool = tool;
        SetConnectFrom(null);
        Note(tool switch
        {
            MapEditTool.PlaceBus => "Place bus: click the map where the new bus goes (Escape returns to the select tool).",
            MapEditTool.Connect => "Connect: click the first bus, then the second (Escape returns to the select tool).",
            _ => "Select: click a bus or a branch to select it; drag a bus to move it.",
        }, isError: false);
        RefreshCommands();
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task DeleteSelectedAsync()
    {
        AssetAuthorModel author = Need();
        if (SelectedOnMap is null)
        {
            Note("Select a bus, a line or a transformer to delete.", isError: true);
            return Task.CompletedTask;
        }
        AuthoringResult r = author.DeleteSelected();   // the engine's delete: refused, listing the referrers, while anything refers to it
        _vm.StatusText = r.Message;
        RefreshCommands();
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task ShowInAssetsAsync()
    {
        Need();
        _showAssets();
        return Task.CompletedTask;
    }
}
