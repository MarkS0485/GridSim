using System.Diagnostics;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using GridSim.App.Assets;
using GridSim.App.Dashboard;
using GridSim.App.Map;
using GridSim.App.Run;
using GridSim.App.Shell;
using GridSim.App.Tabs;
using GridSim.Core.Analysis;
using GridSim.Core.Model;
using GridSim.Core.PowerFlow;
using GridSim.Core.Presentation;
using GridSim.Core.Simulation;
using GridSim.Ui.Assets.Editing;
using GridSim.Ui.Assets.Navigation;
using GridSim.Ui.Assets.Overlay;
using GridSim.Ui.Commands;
using GridSim.Ui.Hosting;
using GridSim.Ui.Shell;
using GridSim.Ui.Tabs.Scada;

namespace GridSim.App;

/// <summary>
/// The main window (UI programme Phase 1.1, the shell). On start it reads the settings, registers the shell's commands
/// into the view model's registry, and builds the menu bar and the keyboard shortcuts from that registry; on close it
/// saves the settings when anything changed. Everything the window shows comes from <see cref="MainWindowViewModel"/>.
/// <para>
/// Phase 1.2, the run pipeline: once the first case or run has loaded, the Run commands
/// (<see cref="RunCommands"/>) join the registry and the menus and shortcuts are rebuilt with them, and the transport
/// bar appears. Closing the window stops the play loop (it is cancelled and ends on its own; the window does not wait
/// for it).
/// </para>
/// <para>
/// Integration (2026-10-20): the window is laid out as the WPF MainWindow.xaml (header, room tabs, transport bar,
/// footer; see MainWindow.axaml). The NETWORK tab is the <see cref="DashboardView"/> showing the view model's one
/// dashboard, with the map in its MapSlot; every tab is fed only through that dashboard's tab hooks.
/// </para>
/// <para>
/// Phase 1.5, the map: the network map (<see cref="SchematicControl"/>) fills the dashboard's map card. It is driven as the WPF
/// window drives FastSchematic: the loaded model is laid out (Load, then FitToView), each accepted frame's state and
/// boundary transfers are pushed in (SetState, SetBoundaries), and the marching dashes advance at the WPF ~15 fps
/// cadence while the run plays. A click selects a bus or a branch (the fly-out), the pointer shows the hover tip, a
/// right-click on a bus opens its context menu from the registry, and the WPF map keys are registry commands
/// (<see cref="MapCommands"/>), registered with the Run commands.
/// </para>
/// </summary>
public sealed partial class MainWindow : Window
{
    private readonly MainWindowViewModel _vm;

    /// <summary>The real app's window: the shared settings file, the real model catalogue and dialogs.</summary>
    public MainWindow() : this(new ShellOptions()) { }

    /// <summary>A window using <paramref name="options"/> for everything external (tests pass their own).</summary>
    public MainWindow(ShellOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        InitializeComponent();
        Options = options;
        // Slice 1.8: the GDA bridge is registered only where ShellOptions allows it (every desktop; not a mobile head).
        _vm = new MainWindowViewModel(options.SettingsStore.Load(), options.Licence(), options.BridgeEnabled);
        DataContext = _vm;

        Actions = options.Actions?.Invoke(this) ?? new ShellActions(this, _vm, options);
        // The shell's commands, with the audit-log viewer in the File menu after Open run folder... (WPF: its AUDIT
        // LOG... button beside LOAD RUN...).
        // Shell slice (v2 + pickers, 2026-10-20): Open case folder... and Open v2 case... (this app's own, through the OS
        // pickers) go between Open run folder... and the audit log. They always run the real shell actions: a test that
        // records the shell's own actions still gets working open commands (their pickers come from ShellOptions.Pickers).
        CaseFiles = Actions as ShellActions ?? new ShellActions(this, _vm, options);
        ShellCommands.Register(_vm.Registry, Actions,
            CaseFileCommands.Commands(CaseFiles).Append(AuditLogWindow.Command(() => this)).ToArray());
        // UI programme Phase 4.1: Author mode (desktop only). Its commands (Edit, Case and the ASSETS tree's context
        // menus) are Author mode only, so the Run-mode menu bar and shortcuts are exactly as before; the window rebuilds
        // its menus and shortcuts whenever the mode changes, and a load while authoring asks Save / Discard / Cancel first.
        AuthorMode = new AuthorModeController(_vm, options.AuthorPrompts?.Invoke(this) ?? new AuthorDialogs(this))
        {
            BundledRoots = options.AuthorBundledRoots,
            UserCasesFolder = options.AuthorUserCasesFolder,
        };
        AuthorMode.Commands = AuthorCommands.Register(_vm.Registry, AuthorMode);
        // UI programme Phase 4.2: map editing on the NETWORK map in AUTHOR mode (the Map menu, Author mode only, after
        // Case on the menu bar). The controller owns the map while authoring; the window routes the map's gestures to it.
        MapAuthor = new MapAuthorController(_vm, Schematic, () => AuthorMode.Prompts, () => SelectTab(AssetsTabHeader), RestoreRunMap, ContextMenuFor);
        MapAuthor.Commands = MapAuthorCommands.Register(_vm.Registry, MapAuthor);
        _vm.LeaveAuthorBeforeLoad = AuthorMode.LeaveForLoadAsync;
        _vm.ModeChanged += (_, _) => OnModeChanged();
        AssetsTab.AuthorMode = AuthorMode;
        AssetsTab.ContextMenuProvider = ContextMenuFor;
        BuildMenusAndShortcuts();

        // Integration (2026-10-20): the NETWORK tab shows the window's one dashboard, fed by the host the view model
        // made and attached; the lamp, the clock and the STATE NOT VALID banner are the window's (over every tab, WPF),
        // so the tab does not repeat them. The map panel in the dashboard's MapSlot reads the window's view model (its
        // hover tip), and the GENERATION & INERTIA tab shows the dashboard model itself, as the WPF tab does.
        NetworkDashboard.Host = _vm.DashboardHost;
        NetworkDashboard.ShowsWindowParts = false;
        MapPanel.DataContext = _vm;
        GenerationTab.Model = _vm.Dashboard;
        // The fly-out's close button clears the dashboard's selection; the map's own record of it follows (the WPF
        // ClearSelection_Click), so a closed fly-out never leaves a selection behind.
        _vm.Dashboard.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(_vm.Dashboard.SelectionVisible) && !_vm.Dashboard.SelectionVisible && _vm.Map.Selection is not null)
                _vm.Map.ClearSelection();
        };
        // UI programme ASSETS A3: the routes into the ASSETS tab, as WPF has them: the fly-out's OPEN IN EXPLORER button
        // (WPF OpenInExplorer_Click) and the SCADA register's row click (WPF OnScadaNavigate); and the way out, the
        // ASSETS tab's SHOW ON MAP (switch to NETWORK and select the bus or branch on the map).
        NetworkDashboard.OpenInExplorerRequested += (_, _) => OpenInExplorer();
        _vm.Tabs.Scada.Navigate = OnScadaNavigate;
        _vm.Tabs.Assets.ShowOnMapRequested += (_, target) => ShowOnMap(target);
        // UI programme ASSETS B2: OPEN INVERTER FLEET on an asset class or binding page shows the INVERTER FLEET tab.
        _vm.Tabs.Assets.InverterFleetRequested += (_, _) => SelectTab(TabModels.InverterFleetHeader);
        // Slice 1.8: the what-if panel's RETURN TO LIVE button is the bridge session's ReturnToLive (WPF ReturnToLive_Click).
        NetworkDashboard.ReturnToLiveRequested += (_, _) => _vm.Run.Bridge?.ReturnToLive();

        MapActions = new MapActions(Schematic, _vm.Map, _vm.Run, _vm.Settings, _vm.NoteSettingsChanged, line => _vm.StatusText = line);
        WireMap(options);
        WireHeader(options);

        // The Run commands appear with the first loaded case (the registry shows no command without its page), and
        // their enabled state follows the feed (a replay has no SOLVER or STEP choice, as in WPF).
        _vm.Run.FeedChanged += (_, _) =>
        {
            EnsureRunCommands();
            foreach (AppCommand c in _runCommands!) c.RaiseCanExecuteChanged();
        };

        // The tab gates (ASSETS, EXPERIMENTAL, SPEED DOMAINS) start from the tab shown now (none of them).
        ApplyTabGates();

        Closing += OnClosing;
        // Slice 1.8: as the WPF window does in OnLoaded, the window loads a source by itself when it first opens.
        Opened += OnFirstOpened;
    }

    // ======================================== start-up (slice 1.8, the WPF OnLoaded launch plan) ========================================

    /// <summary>The WPF message when the model catalogue is empty (MainWindow.OnLoaded).</summary>
    public const string NoModelsText = "No grid models found (expected data/uk next to the app).";

    /// <summary>Exit code when the launch arguments are refused: 2, a usage error, the same code the CLI uses (WPF).</summary>
    public const int LaunchArgumentsRefusedExitCode = 2;

    private bool _opened;

    /// <summary>
    /// The start-up load the window began when it first opened (null before it opened, when
    /// <see cref="ShellOptions.AutoLoadAtStart"/> is off, or when nothing was loaded), so a test can wait for it.
    /// </summary>
    public Task? StartupLoad { get; private set; }

    /// <summary>The launch plan the window resolved when it first opened (null before then or with auto-load off).</summary>
    public DesktopLaunchPlan? LaunchPlan { get; private set; }

    private void OnFirstOpened(object? sender, EventArgs e)
    {
        if (_opened) return;
        _opened = true;
        if (Options.AutoLoadAtStart) RunStartupPlan();
    }

    /// <summary>
    /// The WPF window's start-up rules (MainWindow.OnLoaded), in its order:
    /// <list type="number">
    /// <item><description>the launch arguments are read by Core's <see cref="DesktopLaunchArgs"/> and matched against the
    /// bridge pickers, the tabs and the NETWORK picker this window offers; on any refusal nothing is loaded and
    /// <see cref="ShellOptions.LaunchRefused"/> runs (by default: the refusal is shown, then the app closes with exit
    /// code 2);</description></item>
    /// <item><description>an empty catalogue is reported in plain words (in red in the status bar) and nothing is loaded;</description></item>
    /// <item><description>`--zone`, `--back` and `--bridge-source` pre-select the bridge pickers, without a restart;</description></item>
    /// <item><description>the model text picks the first source whose label contains it; with none, the last-used source
    /// (by its saved path, else its saved label, <see cref="CaseChoice.LastUsedIndex"/>; a pre-solved run is passed over for
    /// the first live case, <see cref="CaseChoice.StartupIndex"/>); with no match, the first entry.
    /// It is chosen in the NETWORK picker, which loads it (a source that is found but refused shows its refusal, as WPF:
    /// there is no second fallback);</description></item>
    /// <item><description>`--tab` opens that tab.</description></item>
    /// </list>
    /// </summary>
    public void RunStartupPlan()
    {
        Dispatcher.UIThread.VerifyAccess();
        DesktopLaunchPlan plan = DesktopLaunchArgs.Parse(Options.LaunchArguments()).Resolve(
            zoneCases: BridgeChoices.Zones.Select(z => z.Case).ToArray(),
            backPresetDays: BridgeChoices.Starts.Select(b => (int)Math.Round(b.Back.TotalDays)).ToArray(),
            bridgeSources: BridgeChoices.Sources.Select(x => x.Source).ToArray(),
            tabHeaders: TabHeaders(),
            modelLabels: _vm.Sources.Select(s => s.Label).ToArray());
        LaunchPlan = plan;
        if (!plan.Ok)
        {
            (Options.LaunchRefused ?? ShowLaunchRefusalAndExit)(this, plan.RefusalText());
            return;
        }

        if (_vm.Sources.Count == 0)
        {
            _vm.ProblemText = NoModelsText;
            return;
        }

        // The bridge pickers before connecting (handy when the lake's settled data lags "now"), guarded so they never
        // fire a restart. A head without the bridge still checks the values (above) but has no pickers to set.
        _vm.Run.Bridge?.Preselect(plan.ZoneIndex, plan.BackIndex, plan.BridgeSourceIndex);

        // 2026-10-24: a last-used pre-solved run is not reopened at start-up (it opens paused and solves nothing); the
        // first live case is, so the app opens solving (CaseChoice.StartupIndex). A named model always wins.
        int wanted = plan.ModelIndex ?? CaseChoice.StartupIndex(_vm.Sources, _vm.Settings);
        _vm.SelectedSource = _vm.Sources[wanted >= 0 ? wanted : 0];   // loads it, as the WPF SelectionChanged runs SwitchTo
        StartupLoad = _vm.PendingSourceLoad;

        if (plan.TabIndex is int ti) TabHost.SelectedIndex = ti;
    }

    // Every tab's header text, left to right (a tab without a text header gives an empty string), as WPF TabHeaders.
    private string[] TabHeaders() =>
        TabHost.Items.Cast<object>().Select(o => o is TabItem { Header: string h } ? h : "").ToArray();

    // The default refusal (WPF: a message box, then Shutdown with exit code 2): a small window with the refusal, and the
    // app closes with code 2 once it is dismissed. Off a desktop lifetime (no process to end), the main window closes.
    private static void ShowLaunchRefusalAndExit(MainWindow owner, string text)
    {
        var ok = new Button { Content = "OK", HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Right, MinWidth = 80 };
        var dialog = new Window
        {
            Title = "GridSim - launch arguments",
            Width = 640,
            SizeToContent = SizeToContent.Height,
            CanResize = false,
            Content = new StackPanel
            {
                Margin = new Thickness(16),
                Spacing = 12,
                Children = { new TextBlock { Text = text, TextWrapping = Avalonia.Media.TextWrapping.Wrap }, ok },
            },
        };
        ok.Click += (_, _) => dialog.Close();
        dialog.Closed += (_, _) =>
        {
            if (Application.Current?.ApplicationLifetime is Avalonia.Controls.ApplicationLifetimes.IClassicDesktopStyleApplicationLifetime desktop)
                desktop.Shutdown(LaunchArgumentsRefusedExitCode);
            else owner.Close();
        };
        _ = dialog.ShowDialog(owner);
    }

    // The Run, map and ASSETS commands once registered (null until the first case has loaded).
    private IReadOnlyList<AppCommand>? _runCommands;
    private IReadOnlyList<AppCommand>? _mapCommands;
    private IReadOnlyList<AppCommand>? _assetCommands;

    // The key bindings this window added from the registry, so a rebuild replaces exactly those.
    private readonly List<KeyBinding> _registryKeys = new();

    private void EnsureRunCommands()
    {
        if (_runCommands is not null) return;
        _runCommands = RunCommands.Register(_vm.Registry, _vm.Run);
        // The map's commands join with the Run ones: before a case has loaded the map has nothing on it.
        _mapCommands = MapCommands.Register(_vm.Registry, MapActions, MapActions);   // Phase 5d: with View / Line routes
        // UI programme ASSETS A3: "Open in Assets" on the map's context menu for a bus or a branch (Run mode).
        _assetCommands = AssetCommands.Register(_vm.Registry,
            new AssetCommandActions(() => MapActions.ContextBusId, () => MapActions.ContextBranchIndex, OpenContextInAssetsAsync));
        BuildMenusAndShortcuts();

        // Slice 1.7: the transport bar's map buttons show the state their commands change, whichever way the command
        // ran (the button, the menu, a key): every map toggle raises CheckedChanged after it ran.
        foreach (AppCommand c in _mapCommands.Where(c => c.IsToggle))
            c.CheckedChanged += (_, _) => UpdateMapButtons();
        UpdateMapButtons();
        // Slice 1.7: the Run menu's SOLVER, STEP and SPEED ticks follow a choice made anywhere (the header's SOLVER
        // picker, the STEP picker, the SPEED button), not only from the menu itself.
        _vm.Run.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName is nameof(RunViewModel.SelectedSolver) or nameof(RunViewModel.SelectedStep) or nameof(RunViewModel.Playback))
                foreach (AppCommand c in _runCommands.Where(c => c.IsToggle)) c.RaiseCheckedChanged();
        };
    }

    // ---- slice 1.7: the transport bar's map buttons (WPF ColorBtn, FleetBtn, DnoBtn, GspRegionBtn) ----

    /// <summary>
    /// Show on the map buttons what the map shows now, by the WPF rules (ApplySettingsToRenderer and the click handlers):
    /// COLOR names the colour mode; FLEET, DNO and GSP are at full opacity while their layer is drawn and dimmed while it
    /// is not; DNO and GSP are disabled (and dimmed) when no local geography was found.
    /// </summary>
    private void UpdateMapButtons()
    {
        SchematicControl map = Schematic;
        ColorButton.Content = MapCommands.ColourButtonLabel(map.ColorMode);
        FleetButton.Opacity = MapCommands.ToggleButtonOpacity(map.ShowFleet);
        DnoButton.IsEnabled = GspRegionButton.IsEnabled = map.HasGeography;
        DnoButton.Opacity = MapCommands.ToggleButtonOpacity(map.ShowDnoAreas, map.HasGeography);
        GspRegionButton.Opacity = MapCommands.ToggleButtonOpacity(map.ShowGspRegions, map.HasGeography);
    }

    // COLOR: the View menu's Colour by item for the next mode in the WPF cycle.
    private void Color_Click(object? sender, RoutedEventArgs e) =>
        RunRegistryCommand(MapCommands.ColourById(MapCommands.NextColourMode(Schematic.ColorMode)));

    // FLEET, DNO and GSP: the View menu's Fleet, DNO areas and GSP regions toggles.
    private void Fleet_Click(object? sender, RoutedEventArgs e) => RunRegistryCommand(MapCommands.Fleet);

    private void Dno_Click(object? sender, RoutedEventArgs e) => RunRegistryCommand(MapCommands.DnoAreas);

    private void GspRegion_Click(object? sender, RoutedEventArgs e) => RunRegistryCommand(MapCommands.GspRegions);

    /// <summary>The stop of the play loop that closing the window began (null before the window closes).</summary>
    public Task? StopLoop { get; private set; }

    /// <summary>The view model (for tests and later pages).</summary>
    public MainWindowViewModel ViewModel => _vm;

    /// <summary>What this window was built with.</summary>
    public ShellOptions Options { get; }

    /// <summary>The actions the shell's commands run.</summary>
    public IShellActions Actions { get; }

    /// <summary>Shell slice (v2 + pickers, 2026-10-20): the shell actions File > Open case folder... and Open v2 case...
    /// run (<see cref="Actions"/> when that is the real <see cref="ShellActions"/>, else a real one of the window's own).</summary>
    public ShellActions CaseFiles { get; }

    /// <summary>The actions the map's commands run.</summary>
    public MapActions MapActions { get; }

    /// <summary>The network map.</summary>
    public SchematicControl MapControl => Schematic;

    /// <summary>
    /// True when the menu bar is in the macOS system menu (the in-window menu bar is then hidden, and the native menu
    /// items carry the shortcuts).
    /// </summary>
    public bool UsesNativeMenu => Options.UseNativeMenu;

    /// <summary>The modifier "Ctrl" in a registry gesture stands for here: Command on macOS, Control elsewhere.</summary>
    public KeyModifiers CommandModifier
        => PlatformSettings?.HotkeyConfiguration.CommandModifiers ?? (OperatingSystem.IsMacOS() ? KeyModifiers.Meta : KeyModifiers.Control);

    /// <summary>
    /// The context menu for <paramref name="target"/> in the current mode, or null when no command acts on it. The
    /// hook the map and the tables call when they gain right-click targets.
    /// </summary>
    public ContextMenu? ContextMenuFor(CommandTargets target)
        => MenuBuilder.ContextMenuFor(_vm.Registry, target, _vm.Mode, CommandPlatforms.Desktop, CommandModifier, _vm.ReportFailure);

    // Build (or rebuild, after commands were added) the menu bar and the shortcuts from the registry.
    private void BuildMenusAndShortcuts()
    {
        foreach (KeyBinding k in _registryKeys) KeyBindings.Remove(k);
        _registryKeys.Clear();
        IReadOnlyList<MenuNode> tree = _vm.Registry.MenuTree(_vm.Mode, CommandPlatforms.Desktop);
        KeyModifiers command = CommandModifier;
        if (UsesNativeMenu)
        {
            // macOS: the system menu bar holds the menus, and its items handle their own shortcuts.
            NativeMenu.SetMenu(this, MenuBuilder.NativeMenuBar(tree, command, _vm.ReportFailure));
            MenuBar.IsVisible = false;
            return;
        }

        MenuBar.ItemsSource = MenuBuilder.MenuBarItems(tree, command, _vm.ReportFailure);
        // Every shortcut is bound once, on the window, from the registry, so it works wherever the focus is inside the
        // window. The menu items only display them. A single-key shortcut (no Ctrl, no Alt, not a function key) stands
        // aside while the focus is in a text box or a combo box, as the WPF window's map keys do (TypingGuard).
        foreach ((string gesture, AppCommand c) in _vm.Registry.Gestures(_vm.Mode, CommandPlatforms.Desktop))
        {
            Func<bool>? blocked = TypingGuard.NeedsGuard(gesture) ? IsTyping : null;
            var binding = new KeyBinding
            {
                Gesture = GestureMapper.ToKeyGesture(gesture, command),
                Command = new RegistryCommand(c, _vm.ReportFailure, blocked),
            };
            KeyBindings.Add(binding);
            _registryKeys.Add(binding);
        }
    }

    /// <summary>True while the keyboard focus is in a control that takes typed characters (the typing guard).</summary>
    public bool IsTyping() => TypingGuard.IsTextEntryTarget(FocusManager?.GetFocusedElement());

    // ======================================== the map ========================================

    // The WPF window advances the marching dashes from CompositionTarget.Rendering, throttled to about 15 fps
    // (AnimationMinIntervalS = 0.065 s), by the time since the previous advance, skipping any gap of 0.2 s or more
    // (AnimationMaxDtS) and the whole while paused. A dispatcher timer at the same interval does the same here.
    private const double AnimationMinIntervalS = 0.065;
    private const double AnimationMaxDtS = 0.2;
    private readonly DispatcherTimer _animation = new() { Interval = TimeSpan.FromSeconds(AnimationMinIntervalS) };
    private readonly Stopwatch _animationClock = new();
    private TimeSpan _lastAnimate;

    private void WireMap(ShellOptions options)
    {
        // Real DNO/GSP geography and geocoded assets (from gda-out/geo, if present locally). A layer that could not
        // be read is reported, never silent (WPF writes it to the event log; here it goes in the status bar in red).
        var geoProblems = new List<string>();
        Schematic.SetGeography(options.LoadGeography(geoProblems));
        if (geoProblems.Count > 0)
            _vm.ProblemText = "Geography layer not loaded: " + string.Join("; ", geoProblems);
        MapActions.ApplySettings();

        _vm.Run.ModelLoaded += (_, model) =>
        {
            // The WPF SwitchTo / LoadReplay: drop the selection, set the hint for the feed, lay out the new model and
            // fit it. The window is laid out first, so the fit is to the map's final size: the transport bar has just
            // appeared above it with the first case (the fit waits for the first render if the map has no size yet).
            // Slice 1.8: a bridge topology (WPF TopologyReceived) only lays the map out and fits it: the bridge has
            // already set its own hint and closed the fly-out when it connected (WPF LoadBridge).
            // UI programme Phase 4.2: the run's map is remembered, so leaving AUTHOR without a reload can show it again.
            _runMapModel = model;
            _runMapResult = null;
            _runMapBoundaries = null;
            if (MapAuthor.IsActive) return;   // never while authoring (a load leaves AUTHOR first); defensive
            if (!_vm.Run.IsBridge) _vm.Map.ModelChanged(_vm.Run.IsReplay);
            Schematic.Load(model);
            // Phase 5d: the loaded case's line routes (none for a case without an asset overlay, a replay or a bridge).
            Schematic.SetLineRoutes(LineRouteMap.Build(_vm.Run.LoadedOverlay, model));
            UpdateLayout();
            Schematic.FitToView();
        };
        _vm.Run.FrameArrived += (_, f) =>
        {
            _runMapResult = f.Frame.Result;
            _runMapBoundaries = f.Boundaries;
            // Phase 4.2: the AUTHOR map is the case being edited and shows no live value (plan Q5).
            if (MapAuthor.IsActive) return;
            // The WPF ShowHostFrame / RenderReplayFrame: the frame's state and boundary transfers into the map.
            Schematic.SetState(f.Frame.Result);
            Schematic.SetBoundaries(f.Boundaries);
        };
        // Slice 1.8: the GDA bridge's condition and stress overlays (WPF _renderer.SetOverlay after each live frame).
        _vm.Run.OverlayArrived += (_, o) =>
        {
            if (MapAuthor.IsActive) return;   // Phase 4.2: no live overlay on the AUTHOR map (a bridge has no AUTHOR anyway)
            Schematic.SetOverlay(o.ConditionByBus, o.StressByBranch);
        };

        Schematic.NodeClicked += id => _vm.Map.SelectBus(id);
        Schematic.BranchClicked += index => _vm.Map.SelectBranch(index);
        // Phase 5d: a click on a line route polyline selects its record in ASSETS (Show in Assets).
        Schematic.LineRouteClicked += ShowLineRouteInAssets;
        // Phase 4.2: the AUTHOR gestures (the map raises these only in its edit mode) go to the map editor.
        Schematic.EditClicked += (p, bus, branch) => RunMapEdit(() => MapAuthor.ClickAsync(p, bus, branch));
        Schematic.BusDragged += (bus, p) => RunMapEdit(() =>
        {
            MapAuthor.Drag(bus, p);
            return Task.CompletedTask;
        });
        Schematic.RightClicked += p => MapContextMenuAt(p)?.Open(Schematic);
        Schematic.PointerMoved += (_, e) =>
        {
            Point p = e.GetPosition(Schematic);
            if (MapAuthor.IsActive)
                _vm.Map.ShowAuthorTip(e.GetCurrentPoint(Schematic).Properties.IsLeftButtonPressed ? null : MapAuthor.TipAt(p));
            else
                _vm.Map.Hover(Schematic.HitTestNode(p), e.GetCurrentPoint(Schematic).Properties.IsLeftButtonPressed);
            Canvas.SetLeft(HoverTip, p.X + 14);
            Canvas.SetTop(HoverTip, p.Y + 14);
        };
        Schematic.PointerExited += (_, _) => _vm.Map.HideHover();

        _animationClock.Start();
        _animation.Tick += (_, _) => AnimateMap();
        _animation.Start();
    }

    // One animation step, by the time since the previous one (WPF OnRender).
    private void AnimateMap()
    {
        TimeSpan now = _animationClock.Elapsed;
        double sinceLast = _lastAnimate == default ? 1.0 : (now - _lastAnimate).TotalSeconds;
        if (sinceLast < AnimationMinIntervalS) return;
        _lastAnimate = now;
        if (sinceLast > 0 && sinceLast < AnimationMaxDtS && _vm.Run.HasRun && !_vm.Run.IsPaused) Schematic.Animate(sinceLast);
    }

    /// <summary>
    /// The context menu for a right-click at <paramref name="p"/> on the map: the registry's bus menu when the click is
    /// on a bus (WPF trips that bus's largest infeed on a right-click; here the menu names the trip first), the
    /// registry's branch menu when it is on a branch (UI programme ASSETS A3: "Open in Assets"; WPF offers nothing
    /// there), else null (no menu on the empty map, as WPF). The bus or branch is remembered for the command
    /// (<see cref="IMapActions.ContextBusId"/>, <see cref="MapActions.ContextBranchIndex"/>).
    /// </summary>
    public ContextMenu? MapContextMenuAt(Point p)
    {
        // Slice 1.8, WPF Schematic_RightClick: on a bridge feed, right-click offers the GDA on-demand physics (or, after a
        // fault, only the reconnect) instead of the local trip.
        if (_vm.Run.Bridge is { IsActive: true } bridge) return BridgeContextMenuAt(bridge, p);
        // UI programme Phase 4.2: in AUTHOR mode a bus or a branch has the map element menu (Delete, Show in Assets).
        if (MapAuthor.IsActive) return MapAuthor.ContextMenuAt(p);
        int? busId = Schematic.HitTestNode(p);
        int? branch = busId is null ? Schematic.HitTestBranch(p) : null;
        MapActions.ContextBusId = busId;
        MapActions.ContextBranchIndex = branch;
        foreach (AppCommand c in _mapCommands ?? Array.Empty<AppCommand>()) c.RaiseCanExecuteChanged();
        foreach (AppCommand c in _assetCommands ?? Array.Empty<AppCommand>()) c.RaiseCanExecuteChanged();
        if (busId is not null) return ContextMenuFor(CommandTargets.Bus);
        return branch is null ? null : ContextMenuFor(CommandTargets.Branch);
    }

    /// <summary>
    /// Slice 1.8: the bridge's map context menu at <paramref name="p"/> (WPF ShowBridgeReconnectMenu and
    /// ShowBridgeContextMenu): the items <see cref="BridgeSession.ContextMenu"/> gives for the bus there or, on no bus, the
    /// branch there, in its order and with its texts, a disabled item staying disabled; null when there is nothing to
    /// offer (WPF then opens no menu). An item's failure is shown in the status bar, never lost.
    /// </summary>
    public ContextMenu? BridgeContextMenuAt(BridgeSession bridge, Point p)
    {
        ArgumentNullException.ThrowIfNull(bridge);
        int? busId = Schematic.HitTestNode(p);
        int? branch = busId is null ? Schematic.HitTestBranch(p) : null;
        IReadOnlyList<BridgeMenuItem> items = bridge.ContextMenu(busId, branch);
        if (items.Count == 0) return null;
        var menu = new ContextMenu();
        var entries = new List<Control>();
        foreach (BridgeMenuItem item in items)
        {
            if (item.IsSeparator) { entries.Add(new Separator()); continue; }
            var mi = new MenuItem { Header = item.Header, IsEnabled = item.Enabled };
            Func<Task> run = item.Run!;
            mi.Click += async (_, _) =>
            {
                try
                {
                    await run().ConfigureAwait(true);
                }
#pragma warning disable CA1031 // Not swallowed: shown in red in the status bar.
                catch (Exception ex)
#pragma warning restore CA1031
                {
                    _vm.ProblemText = $"{item.Header} failed: {ex.Message}";
                }
            };
            entries.Add(mi);
        }
        menu.ItemsSource = entries;
        return menu;
    }

    // ======================================== the header (slice 1.7) ========================================

    /// <summary>The WPF event-log line for a search that matched no substation (MainWindow.Search_KeyDown).</summary>
    public static string NoSubstationText(string query) => $"No substation matching '{query}'";

    // The WPF header's NETWORK picker, search box and buttons (see MainWindow.axaml).
    private void WireHeader(ShellOptions options)
    {
        // The NETWORK picker lists the model catalogue, found once at start-up as the WPF OnLoaded does. A folder the
        // scan could not read is skipped, not fatal, and said in the event log with the WPF words at "08:00".
        var skipped = new List<string>();
        _vm.SetSources(options.DiscoverCases(_vm.Settings, skipped));
        foreach (string folder in skipped)
            _vm.Dashboard.Log("08:00", $"Model scan skipped an unreadable folder: {folder}");

        // Enter in the search box searches. The handler is on the tunnelling phase, so it sees Enter before the text
        // box itself does (whatever the text box does with Enter, the search still runs, as WPF's KeyDown handler does).
        SearchBox.AddHandler(KeyDownEvent, (_, e) =>
        {
            if (e.Key != Key.Enter) return;
            SearchSubstation(SearchBox.Text);
            e.Handled = true;
        }, RoutingStrategies.Tunnel);
    }

    /// <summary>
    /// The WPF search (MainWindow.Search_KeyDown, on Enter): find the first substation on the map whose name contains
    /// <paramref name="query"/> (ignoring case; <see cref="GridSim.Ui.Schematic.SchematicView.FindNode"/>), centre the
    /// map on it at the focus zoom and select it (its fly-out opens, as a click on it does). When nothing matches, the
    /// WPF line goes to the event log at the dashboard's clock and nothing else changes. Returns the bus found, or null.
    /// </summary>
    public int? SearchSubstation(string? query)
    {
        string text = query ?? "";
        int? id = Schematic.FindNode(text);
        if (id is int bus)
        {
            Schematic.FocusNode(bus);
            _vm.Map.SelectBus(bus);   // the WPF OnNodeClicked
        }
        else
        {
            _vm.Dashboard.Log(_vm.Dashboard.Clock, NoSubstationText(text));
        }
        return id;
    }

    // LOAD RUN... is File > Open run folder... (the same command, so the same folder picker and run-directory check).
    private void LoadRun_Click(object? sender, RoutedEventArgs e) => RunRegistryCommand(ShellCommands.OpenRunFolder);

    // AUDIT LOG... is File > Audit log... (the same command, so the same viewer).
    private void AuditLog_Click(object? sender, RoutedEventArgs e) => RunRegistryCommand(AuditLogWindow.CommandId);

    // A button that runs a registry command: exactly what its menu item runs, and a failure is reported the way a
    // failed menu item's is (the command's title and the reason, in red in the status bar), never lost.
    private async void RunRegistryCommand(string id)
    {
        AppCommand command = _vm.Registry.Get(id);
        try
        {
            await command.RunAsync().ConfigureAwait(true);
        }
#pragma warning disable CA1031 // Not swallowed: shown in the status bar.
        catch (Exception ex)
#pragma warning restore CA1031
        {
            _vm.ReportFailure(command, ex);
        }
    }

    // ======================================== the tabs (Phase 1.6) ========================================

    /// <summary>The header of the SPEED DOMAINS tab, whose decomposition runs only while it is showing (WPF).</summary>
    public const string DomainsTabHeader = TabModels.DomainsHeader;

    /// <summary>The header of the EXPERIMENTAL tab, whose models run only while it is showing (WPF R020).</summary>
    public const string ExperimentalTabHeader = TabModels.ExperimentalHeader;

    /// <summary>The header of the ASSETS tab, whose explorer refreshes only while it is showing (WPF D-DOC-073).</summary>
    public const string AssetsTabHeader = TabModels.AssetsHeader;

    /// <summary>The header of the NETWORK tab (the map), where the ASSETS tab's SHOW ON MAP goes.</summary>
    public const string NetworkTabHeader = "NETWORK";

    /// <summary>The ASSETS tab's view.</summary>
    public AssetsView AssetsView => AssetsTab;

    /// <summary>The room's tab strip (the map is in its NETWORK tab).</summary>
    public TabControl TabStrip => TabHost;

    /// <summary>The NETWORK tab: the dashboard view, with the network map in its MapSlot.</summary>
    public DashboardView DashboardTab => NetworkDashboard;

    // WPF Tabs_SelectionChanged: only the room tabs, not an inner selector's change bubbling up. Matched by header, not
    // by index, so inserting a tab cannot point a gate at the wrong one. The EXPERIMENTAL models (R020) and the SPEED
    // DOMAINS decomposition run only while their tab is showing (showing one catches up on the newest frame at once).
    // UI programme ASSETS A3: the ASSETS explorer too, as WPF gates it here (D-DOC-073: matched by its header).
    private void Tabs_SelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (!ReferenceEquals(e.Source, TabHost)) return;
        ApplyTabGates();
    }

    private void ApplyTabGates()
    {
        _vm.Tabs.Assets.Active = TabHost.SelectedItem is TabItem { Header: AssetsTabHeader };
        _vm.Tabs.Experimental.Active = TabHost.SelectedItem is TabItem { Header: ExperimentalTabHeader };
        _vm.Tabs.Domains.Active = TabHost.SelectedItem is TabItem { Header: DomainsTabHeader };
    }

    // ======================================== Author mode (UI programme Phase 4.1) ========================================

    /// <summary>The window's Author mode: entering and leaving AUTHOR on the ASSETS tab, and the Author-mode commands.</summary>
    public AuthorModeController AuthorMode { get; }

    /// <summary>UI programme Phase 4.2: the NETWORK map in AUTHOR mode (the case being edited, its tools, the shared
    /// selection).</summary>
    public MapAuthorController MapAuthor { get; }

    // The run's own map (its model, last state and boundary transfers), kept while authoring so that leaving AUTHOR
    // without a reload shows exactly what the run showed. Null until the first load and the first frame.
    private GridModel? _runMapModel;
    private PowerFlowResult? _runMapResult;
    private IReadOnlyList<BoundaryFlow>? _runMapBoundaries;

    // Leaving AUTHOR: the run's own map again (the case reload after a save, if any, loads the saved case after this).
    private void RestoreRunMap()
    {
        if (_runMapModel is null) return;
        Schematic.Load(_runMapModel, keepView: true);
        if (_runMapResult is not null) Schematic.SetState(_runMapResult);
        if (_runMapBoundaries is not null) Schematic.SetBoundaries(_runMapBoundaries);
    }

    // A map edit from a gesture: an event handler cannot await, so run it and show anything it throws in red in the
    // status bar (never lost).
    private async void RunMapEdit(Func<Task> edit)
    {
        try
        {
            await edit().ConfigureAwait(true);
        }
#pragma warning disable CA1031 // Not swallowed: shown in red in the status bar.
        catch (Exception ex)
#pragma warning restore CA1031
        {
            _vm.ProblemText = "Map edit failed: " + ex.Message;
        }
    }

    // The mode changed: the menus and shortcuts are rebuilt for it, the transport bar is disabled while authoring (the run
    // is paused and stays paused until RUN), and the tab gates are applied again (the ASSETS explorer catches up in RUN).
    private void OnModeChanged()
    {
        // UI programme Phase 4.2: the map shows the case being edited while authoring, and the run's map again after.
        if (_vm.Mode == CommandModes.Author && _vm.Tabs.Assets.Author is AssetAuthorModel author && !MapAuthor.IsActive) MapAuthor.Enter(author);
        else if (_vm.Mode == CommandModes.Run && MapAuthor.IsActive) MapAuthor.Leave();
        BuildMenusAndShortcuts();
        TransportBar.IsEnabled = _vm.Mode == CommandModes.Run;
        ApplyTabGates();
    }

    /// <summary>Show the tab whose header is <paramref name="header"/> (WPF SelectTab, by name: D-DOC-073).</summary>
    public void SelectTab(string header) =>
        TabHost.SelectedItem = TabHost.Items.OfType<TabItem>().Single(t => Equals(t.Header, header));

    // ======================================== the ASSETS routes (UI programme ASSETS A3) ========================================

    /// <summary>The bridge's per-asset query the last substation page shown in bridge mode started, on whichever route
    /// (null when none did), so a test can wait for it. UI programme ASSETS fix S6: this was the OPEN IN EXPLORER route's
    /// own query; every route now asks through <see cref="MainWindowViewModel.AssetInsights"/>.</summary>
    public Task? PendingAssetQuery => _vm.AssetInsights.Pending;

    /// <summary>
    /// The map fly-out's OPEN IN EXPLORER (WPF OpenInExplorer_Click): show the ASSETS tab and open the selected bus's
    /// substation page or the selected branch's page, by the WPF routes. In bridge mode a substation page is then backed
    /// by the bridge's real per-asset data (WPF BridgeApplyAsset). UI programme ASSETS fix S6: the query is no longer made
    /// here but by the AssetInsightSource, which asks for every substation page shown in bridge mode, on every route (this
    /// one included, with the same query and the same card).
    /// </summary>
    public void OpenInExplorer()
    {
        SelectTab(AssetsTabHeader);   // D-DOC-073: by name
        AssetNavigatorModel assets = _vm.Tabs.Assets;
        switch (_vm.Map.Selection)
        {
            case { Kind: MapSelectionKind.Bus } bus:
                assets.OpenSubstation(bus.Id);   // S6: the page change itself asks the bridge (AssetInsightSource)
                break;
            case { Kind: MapSelectionKind.Branch } branch when BranchAt(branch.Id) is Branch br:
                assets.OpenBranch(br);
                break;
        }
    }

    // The branch at index i of the model on screen (the map's selection and right-click name branches by index).
    private Branch? BranchAt(int i) =>
        _vm.Run.LastFrame is SimFrame f && (uint)i < (uint)f.Model.Branches.Count ? f.Model.Branches[i] : null;

    /// <summary>
    /// The SCADA register's row click (WPF OnScadaNavigate): a bus-voltage point opens its substation, a branch flow or
    /// tap point its branch, each resolved against the newest frame; then the ASSETS tab is shown. The system
    /// frequency point has no asset and does nothing.
    /// </summary>
    public void OnScadaNavigate(ScadaPointModel pt)
    {
        ArgumentNullException.ThrowIfNull(pt);
        if (_vm.Run.LastFrame is not SimFrame last) return;
        GridModel m = last.Model;
        switch (pt.Kind)
        {
            case PointKind.BusVoltage:
                if ((uint)pt.Index < (uint)last.Result.BusIds.Count) _vm.Tabs.Assets.OpenSubstation(last.Result.BusIds[pt.Index]);
                break;
            case PointKind.BranchP:
            case PointKind.BranchQ:
            case PointKind.OltcTap:
                if ((uint)pt.Index < (uint)m.Branches.Count) _vm.Tabs.Assets.OpenBranch(m.Branches[pt.Index]);
                break;
            default:
                return;   // SystemFreq has no entity to open
        }
        SelectTab(AssetsTabHeader);   // D-DOC-073: by name
    }

    /// <summary>
    /// Phase 5d: a click on a line route polyline on the map: switch to ASSETS and select the route's record there. A
    /// route the ASSETS tree does not hold is said in red in the status bar, never ignored.
    /// </summary>
    public void ShowLineRouteInAssets(string routeId)
    {
        ArgumentNullException.ThrowIfNull(routeId);
        SelectTab(AssetsTabHeader);
        if (!_vm.Tabs.Assets.OpenKey(OverlayIndex.KeyOf(routeId)))
            _vm.ProblemText = "The line route '" + routeId + "' is not in the ASSETS tree of the case on screen.";
    }

    // The map context menu's "Open in Assets": the bus or branch the menu was opened on, as OPEN IN EXPLORER opens it.
    private Task OpenContextInAssetsAsync()
    {
        if (MapActions.ContextBusId is int bus)
        {
            SelectTab(AssetsTabHeader);
            _vm.Tabs.Assets.OpenSubstation(bus);
        }
        else if (MapActions.ContextBranchIndex is int index && BranchAt(index) is Branch br)
        {
            SelectTab(AssetsTabHeader);
            _vm.Tabs.Assets.OpenBranch(br);
        }
        else throw new InvalidOperationException("The map's context menu was not opened on a bus or a branch of the model on screen.");
        return Task.CompletedTask;
    }

    /// <summary>
    /// The ASSETS tab's SHOW ON MAP: switch to NETWORK and select the bus (centred at the focus zoom, as the header
    /// search does) or the branch (the map centred on its From end) there, with its fly-out, as a click on it does.
    /// </summary>
    public void ShowOnMap(AssetMapTarget target)
    {
        SelectTab(NetworkTabHeader);
        // Phase 5d: a line route drawn on the map is framed there (the layer is turned on first if it was off), with the
        // line it carries selected; a route that is not drawn (chainage only) selects its line as any branch.
        if (target.LineRouteId is string routeId && Schematic.LineRoutes.Find(routeId) is not null)
        {
            if (MapActions.ShowLineRoutesLayer())
                foreach (AppCommand c in _mapCommands?.Where(c => c.Id == MapCommands.LineRoutes) ?? Enumerable.Empty<AppCommand>()) c.RaiseCheckedChanged();
            UpdateLayout();   // the NETWORK tab has just been selected: the map has its size before the frame
            // Never silent: a map with no size yet (it cannot be framed) is said in red in the status bar.
            if (!Schematic.FrameLineRoute(routeId))
                _vm.ProblemText = "The line route '" + routeId + "' could not be framed: the map has no size yet.";
            if (target.BranchIndex is int line && BranchAt(line) is not null) _vm.Map.SelectBranch(line);
            return;
        }
        if (target.BusId is int bus)
        {
            Schematic.FocusNode(bus);
            _vm.Map.SelectBus(bus);
        }
        else if (target.BranchIndex is int index && BranchAt(index) is Branch br)
        {
            Schematic.FocusNode(br.From);
            _vm.Map.SelectBranch(index);
        }
    }

    // ---- the transport bar: each button runs the same action as its Run menu item ----

    private void PlayPause_Click(object? sender, RoutedEventArgs e) => RunAction(_vm.Run.TogglePlayAsync);

    private void StepOnce_Click(object? sender, RoutedEventArgs e) => RunAction(_vm.Run.StepOnceAsync);

    private void Speed_Click(object? sender, RoutedEventArgs e) => RunAction(_vm.Run.CycleSpeedAsync);

    // Slice 1.7: the WPF replay buttons (the scrubber is bound to the run view model and seeks there).
    private void Restart_Click(object? sender, RoutedEventArgs e) => RunAction(_vm.Run.RestartReplayAsync);

    private void StepBack_Click(object? sender, RoutedEventArgs e) => RunAction(_vm.Run.StepBackAsync);

    // A button handler cannot await: run the action and show anything it throws in the run's error area.
    private async void RunAction(Func<Task> action)
    {
        try
        {
            await action().ConfigureAwait(true);
        }
#pragma warning disable CA1031 // Not swallowed: shown in the error area.
        catch (Exception ex)
#pragma warning restore CA1031
        {
            _vm.Run.LoadError = ex.Message;
        }
    }

    // Save on the way out, as the WPF window does, but only when something changed this session: an unchanged file is
    // left exactly as the user (or the WPF app) wrote it. A save that could not write is reported to the trace log;
    // the window is closing, so there is nowhere left on screen to show it.
    private void OnClosing(object? sender, WindowClosingEventArgs e)
    {
        // Slice 1.8, WPF OnClosing: with a live bridge the first close is cancelled while the socket is closed
        // asynchronously; the window then closes itself and this handler runs again with the bridge gone.
        if (!_closeAfterBridgeDisposed && _vm.Run.Bridge is { IsActive: true }) { CloseAfterBridgeDisposal(e); return; }

        // Stop the play loop: it is cancelled now and ends by itself (its task completes; it never throws).
        StopLoop = _vm.Run.StopLoopAsync();
        _animation.Stop();
        _vm.DashboardHost.Detach();   // the dashboard (and every tab) takes nothing more from the stopping loop
        _vm.Tabs.Dispose();   // the CYCLE tab's background worker (WPF: _vm.Cycle.Dispose() on close)
        if (!_vm.SettingsChanged) return;
        _vm.Settings.Save();
        if (_vm.Settings.SaveProblem is { } problem) Trace.WriteLine($"GridSim: settings not saved: {problem}");
    }

    // Set once the bridge has been disposed on the way out, so the second (real) close does not wait again.
    private bool _closeAfterBridgeDisposed;

    /// <summary>The bridge disposal a close began (null unless the window was closed with a live bridge).</summary>
    public Task? BridgeClosing { get; private set; }

    /// <summary>
    /// The closing handler's bridge part (WPF CloseAfterBridgeDisposal): the first close is cancelled, the bridge is
    /// disposed asynchronously (bounded at 1 s so a stuck socket cannot hold the app open), and the window then closes
    /// itself. A disposal fault is written to the trace log (the window is closing: there is no event log left to show).
    /// </summary>
    private void CloseAfterBridgeDisposal(WindowClosingEventArgs e)
    {
        e.Cancel = true;
        _closeAfterBridgeDisposed = true;
        BridgeClosing = DisposeBridgeThenClose(_vm.Run.Bridge!);
    }

    private async Task DisposeBridgeThenClose(BridgeSession bridge)
    {
        try
        {
            Task dispose = bridge.DisposeBridgeAsync();
            if (await Task.WhenAny(dispose, Task.Delay(TimeSpan.FromSeconds(1))).ConfigureAwait(true) == dispose)
                await dispose.ConfigureAwait(true);
        }
#pragma warning disable CA1031 // Not swallowed: written to the trace log, as WPF writes it.
        catch (Exception ex)
#pragma warning restore CA1031
        {
            Trace.WriteLine($"GridSim: bridge close failed on exit: {ex.Message}");
        }
        // Queued, not called inline: a disposal that finished at once would still be inside the Closing event.
        Dispatcher.UIThread.Post(Close);
    }
}
