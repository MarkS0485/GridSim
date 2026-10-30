using System.IO;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using GridSim.Core.Analysis;
using GridSim.Core.Dynamics;
using GridSim.Core.Frequency;
using GridSim.Core.IO;
using GridSim.Core.IO.Json;
using GridSim.Core.IO.Matpower;
using GridSim.Core.Layout;
using GridSim.Core.Licensing;
using GridSim.Core.Model;
using GridSim.Core.Net;
using GridSim.Core.PowerFlow;
using GridSim.Core.Presentation;
using GridSim.Core.Simulation;
using GridSim.Ui.Dashboard;
using GridSim.Ui.Geography;
using GridSim.Ui.Hosting;
using GridSim.Ui.Models;
using GridSim.Ui.Schematic;
using GridSim.Ui.Settings;
using GridSim.Wpf.Visuals;

namespace GridSim.Wpf;

public partial class MainWindow : Window
{
    private readonly DashboardViewModel _vm = new();
    // UI programme Phase 0.2: the simulation host (load a model, run the LiveSim / PresolvedReplay loop, produce frames)
    // moved to the platform-neutral GridSim.Ui. This window drives it and shows each frame on its own dispatcher
    // (DeliverHostFrame). The host owns the live simulation, the replay cursor, the tick, the step length and the
    // playback rate that this window used to hold in _sim, _replay, _tick, _dtMinutes, _playback and a DispatcherTimer.
    private readonly SimHost _host = new();
    private FastSchematic _renderer = null!;
    private TimeSpan _lastAnimate;   // RenderingTime of the last Animate call (drives the ~15 fps throttle and its dt)
    private SimFrame? _last;
    private GridBridgeClient? _bridge;                   // non-null when streaming live from the GDA bridge
    private string _bridgeCase = "zone-nged";            // streamed network (a DNO zone / GB-full), chosen by the zone picker
    private string _bridgeSource = "presolved";          // reconstruction the stream replays (presolved|estimated|live), chosen by the source picker
    private bool _bridgeControlsInit;                    // guards the zone/start pickers from firing a restart during programmatic setup
    private bool _bridgeLive;                            // set once the first live frame lands, so the status pill flips CONNECTING->LIVE
    private bool _bridgeFaulted;                         // the stream faulted: nothing on screen is live; right-click offers reconnect
    private bool _restoreLivePill;                       // a finished query left a non-live pill: the next live frame restores LIVE
    private bool _bridgeRestartRunning, _bridgeRestartPending;   // serialise stream restarts (one Stop/Start pair at a time)
    private System.DateTime? _bridgeTickUtc;             // N144: the latest bridge tick's UTC instant (MARKET price trace per settlement period)
    private DateTime _bridgeQueryAt;                     // historical instant on-demand queries are evaluated at (the stream anchor)
    private SimFrame? _lastLive;                         // most recent live bridge frame, restored on "return to live"
    private BridgeProtocol.OverlaysBlock? _lastOverlays; // most recent live overlays (condition/stress), restored on "return to live"
    private bool _whatIf;                                // true while a query result (scenario/feasibility) is overlaid - live frames are held off
    // D-DOC-141: the newest HUD scalars and OPERATOR/MARKET blocks that arrived while the bridge display was PAUSED,
    // applied on PLAY (the newest frame and overlays are already kept in _lastLive / _lastOverlays).
    private BridgeProtocol.ScalarsBlock? _heldHud;
    private (BridgeProtocol.OperatorBlock? Op, BridgeProtocol.MarketBlock? Mk, DateTime? TickUtc)? _heldMarket;
    private bool _scrubbing;                             // guards the scrubber from re-triggering a seek on programmatic update
    private bool IsReplay => _host.IsReplay;
    private List<ModelSource> _sources = new();
    private ModelSource? _current;                       // current model, so a solver change can rebuild it
    private SolverKind _solverKind = SolverKind.Auto;    // chosen power-flow engine (Auto picks per network)
    private int? _selBusId;       // last map selection, for "open in explorer"
    private Branch? _selBranch;
    private readonly AppSettings _settings = AppSettings.Load();

    // The SOLVER picker's entries (Auto first, then the three explicit engines) now live in GridSim.Ui
    // (TransportChoices.Solvers), values and order unchanged.
    private static readonly IReadOnlyList<SolverChoice> SolverChoices = TransportChoices.Solvers;

    // GDA zones the bridge can stream (server discovers zone-* dirs + gb-full). Friendly DNO names for the picker.
    private sealed record BridgeZone(string Label, string Case);
    private static readonly BridgeZone[] BridgeZones =
    {
        new("NGED  -  Midlands/SW/S.Wales", "zone-nged"),
        new("UKPN  -  London/South East/East", "zone-ukpn"),
        new("SSEN  -  N.Scotland/Southern", "zone-ssen"),
        new("SPEN  -  Scotland/Merseyside", "zone-spen"),
        new("NPg  -  North East/Yorkshire", "zone-npg"),
        new("ENWL  -  North West", "zone-enwl"),
        new("GB full", "gb-full"),
        new("GB spine  -  GSP-level", "gb-spine"),
    };

    // Which reconstruction the stream replays. presolved = demand-scaled re-solve lake; estimated =
    // measurement-conditioned WLS lake (frames carry a per-tick estimation-quality block - the chi-square pill);
    // live = static-network solve with only system scalars moving.
    private sealed record BridgeSourceChoice(string Label, string Source);
    private static readonly BridgeSourceChoice[] BridgeSources =
    {
        new("PRESOLVED", "presolved"),
        new("ESTIMATED", "estimated"),
        new("LIVE", "live"),
    };

    // ---- named shell constants (R210: were literals; values unchanged) ----

    /// <summary>Frame rate (frames per second) the GDA bridge stream is started at.</summary>
    private const double BridgeStreamFps = 2;

    /// <summary>Extra load (MW) the bridge context menu's feasibility query adds at the chosen bus.</summary>
    private const double FeasibilityProbeMw = 500.0;

    /// <summary>Shortest interval (s) between animation steps: about 15 fps, enough for marching dashes.</summary>
    private const double AnimationMinIntervalS = 0.065;

    /// <summary>Longest animation step (s) honoured; the first frame and any longer stall are skipped, not jumped.</summary>
    private const double AnimationMaxDtS = 0.2;

    /// <summary>Look-back used when no bridge start preset is selected: the first (shortest) preset, 9 days, which
    /// clears both the 169 h no-forward-inference horizon and the ~8-day settlement lag.</summary>
    private static readonly TimeSpan DefaultBridgeBack = TimeSpan.FromDays(9);

    // Historical start presets for the stream anchor. Every option is comfortably behind the
    // 169 h (7 d + 1 h) no-forward-inference horizon AND the ~8-day settlement publication lag,
    // so the constrained control can never ask the server for a start it would reject.
    private sealed record BridgeStart(string Label, TimeSpan Back);
    private static readonly BridgeStart[] BridgeStarts =
    {
        new("9 days ago", TimeSpan.FromDays(9)),
        new("12 days ago", TimeSpan.FromDays(12)),
        new("2 weeks ago", TimeSpan.FromDays(14)),
        new("3 weeks ago", TimeSpan.FromDays(21)),
        new("30 days ago", TimeSpan.FromDays(30)),
        new("60 days ago", TimeSpan.FromDays(60)),
    };

    // Bridge status-pill colours (green live / amber transitional / red fault), reusing the map's status ramps.
    private static readonly SolidColorBrush BridgeOkBrush = Palette.Frozen(Palette.Voltage(1.0));
    private static readonly SolidColorBrush BridgeWarnBrush = Palette.Frozen(Palette.Loading(0.7));
    private static readonly SolidColorBrush BridgeBadBrush = Palette.Frozen(Palette.Loading(1.1));

    public MainWindow()
    {
        InitializeComponent();
        DataContext = _vm;
        Loaded += OnLoaded;
        KeyDown += OnKeyDown;
        Closing += OnClosing;
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        _renderer = Schematic;
        // Real DNO/GSP geography + geocoded assets (from gda-out/geo, if materialised locally).
        var geoProblems = new List<string>();
        _renderer.SetGeography(GeographyData.Load(AppContext.BaseDirectory, geoProblems));
        ApplySettingsToRenderer();
        _renderer.NodeClicked += OnNodeClicked;
        _renderer.BranchClicked += OnBranchClicked;
        _renderer.MouseRightButtonUp += Schematic_RightClick;
        _renderer.MouseMove += Schematic_MouseMove;
        _renderer.MouseLeave += (_, _) => HoverTip.Visibility = Visibility.Collapsed;

        _vm.Scada.Navigate = OnScadaNavigate;

        StartHostLoop();
        CompositionTarget.Rendering += OnRender;

        // Show the active licence tier up front (title bar + log), so the operator knows their bus limit.
        // F02: the release number goes in the title bar too, and the About tab carries the full build line.
        Title = $"{Title}  -  v{GridSim.Core.BuildInfo.Version}  -  {App.Licence.Entitlement.Tier.DisplayName()} licence";
        AboutText.Text = string.Join(Environment.NewLine,
            GridSim.Core.BuildInfo.DisplayLine,
            $"Build:   {GridSim.Core.BuildInfo.InformationalVersion}",
            $"Runtime: {System.Runtime.InteropServices.RuntimeInformation.FrameworkDescription} " +
                $"{System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture}",
            $"Licence: {App.Licence.Describe()}");
        _vm.Log("08:00", $"Licence: {App.Licence.Describe()}");
        // A settings file that could not be read is reported, not silently replaced: the defaults apply for this
        // session and the file is left untouched on exit, so a hand edit with a typo can be fixed rather than lost.
        // A geography file that could not be read leaves its layer empty; say which and why.
        foreach (string gp in geoProblems)
            _vm.Log("08:00", $"Geography layer not loaded: {gp}");
        if (_settings.LoadProblem is { } settingsProblem)
            _vm.Log("08:00", $"Settings ignored and not overwritten: {settingsProblem}. Fix the file and restart.");

        var skippedFolders = new List<string>();
        _sources = ModelCatalog.Discover(_settings, skippedFolders);
        // A gda-out/ or results/ folder that could not be read is skipped, not fatal - but say which and why.
        foreach (string skipped in skippedFolders)
            _vm.Log("08:00", $"Model scan skipped an unreadable folder: {skipped}");
        // An empty model list is reported AFTER the launch arguments are checked (below), so a bad `--zone` or an
        // unknown option is still refused with exit code 2 on an install with no data folder, as the manual promises.
        SourcePicker.ItemsSource = _sources;
        // Solver picker: Auto (default) + the three explicit engines. Populate before the source
        // picker fires SwitchTo, so the first load already knows the chosen engine. Selecting Auto
        // here is a no-op (no model loaded yet - the handler guards on _current).
        SolverPicker.ItemsSource = SolverChoices;
        SolverPicker.SelectedIndex = 0;
        // Time-step control: default 20 min (the last choice) to preserve the day-sweep behaviour.
        StepPicker.ItemsSource = StepChoices;
        StepPicker.SelectedIndex = StepChoices.Count - 1;
        // Bridge zone/start pickers (hidden until bridge mode). Populate now; the init guard keeps the
        // default selections from firing a stream restart before a bridge is even connected.
        _bridgeControlsInit = true;
        BridgeZonePicker.ItemsSource = BridgeZones;
        BridgeZonePicker.SelectedIndex = 0;
        BridgeStartPicker.ItemsSource = BridgeStarts;
        BridgeStartPicker.SelectedIndex = 0;
        BridgeSourcePicker.ItemsSource = BridgeSources;
        BridgeSourcePicker.SelectedIndex = 0;
        _bridgeControlsInit = false;

        // Launch arguments (D-DOC-074, D-DOC-142): read by Core's DesktopLaunchArgs and matched against the pickers
        // and tabs this window actually offers. A flag's value is never taken as the model text (`--back 30` used to
        // boot case30), and a value that matches nothing is refused with a message instead of being dropped. On any
        // refusal the app says what was wrong and closes with exit code 2 (a usage error, as in the CLI), rather than
        // open on a zone, start, source, tab or model the user did not ask for.
        string[] cmdArgs = Environment.GetCommandLineArgs();
        DesktopLaunchPlan plan = DesktopLaunchArgs.Parse(cmdArgs.Skip(1).ToArray()).Resolve(
            zoneCases: BridgeZones.Select(z => z.Case).ToArray(),
            backPresetDays: BridgeStarts.Select(b => (int)Math.Round(b.Back.TotalDays)).ToArray(),
            bridgeSources: BridgeSources.Select(x => x.Source).ToArray(),
            tabHeaders: TabHeaders(),
            modelLabels: _sources.Select(s => s.Label).ToArray());
        if (!plan.Ok)
        {
            StopHostLoop();
            MessageBox.Show(plan.RefusalText(), "GridSim - launch arguments", MessageBoxButton.OK, MessageBoxImage.Error);
            Application.Current.Shutdown(LaunchArgumentsRefusedExitCode);
            return;
        }

        if (_sources.Count == 0)
        {
            StopHostLoop();
            MessageBox.Show("No grid models found (expected data/uk next to the app).", "GridSim",
                MessageBoxButton.OK, MessageBoxImage.Error);
            return;
        }

        // `--zone`, `--back` and `--bridge-source` pre-select the bridge pickers before connecting (handy when the
        // lake's settled data lags "now": the pre-solved window is about 30 days back). Guarded like the defaults above.
        _bridgeControlsInit = true;
        if (plan.ZoneIndex is int zi) { BridgeZonePicker.SelectedIndex = zi; _bridgeCase = BridgeZones[zi].Case; }
        if (plan.BackIndex is int bi) BridgeStartPicker.SelectedIndex = bi;
        if (plan.BridgeSourceIndex is int si) { BridgeSourcePicker.SelectedIndex = si; _bridgeSource = BridgeSources[si].Source; }
        _bridgeControlsInit = false;

        // The model text (e.g. `-- case9`) selects the first model whose label contains it, so the app can boot
        // straight onto a chosen case; with no model text, the last-used model (by its path, which is unique; by its
        // label only for a settings file written before the path was saved), then the first.
        int wanted = plan.ModelIndex ?? LastUsedSourceIndex();
        SourcePicker.SelectedIndex = wanted >= 0 ? wanted : 0;   // fires SwitchTo via SelectionChanged

        // `--tab OPERATOR` opens a specific tab on boot (used for screenshots / demos).
        if (plan.TabIndex is int ti) Tabs.SelectedIndex = ti;
    }

    /// <summary>The index of the last-used source: matched by its saved path first (labels can repeat), then by its
    /// saved label for an older settings file; -1 when neither matches.</summary>
    private int LastUsedSourceIndex()
    {
        if (_settings.LastSourcePath is { Length: > 0 } key)
        {
            int byKey = _sources.FindIndex(s => string.Equals(s.Key, key, StringComparison.OrdinalIgnoreCase));
            if (byKey >= 0) return byKey;
        }
        return _settings.LastSource is null ? -1
            : _sources.FindIndex(s => string.Equals(s.Label, _settings.LastSource, StringComparison.Ordinal));
    }

    /// <summary>Exit code when the launch arguments are refused: 2, a usage error, the same code the CLI uses.</summary>
    private const int LaunchArgumentsRefusedExitCode = 2;

    /// <summary>The header of the ASSETS tab. Tab jumps select it BY NAME (D-DOC-073): they used to select the last
    /// tab, which became ABOUT when ABOUT was added after ASSETS.</summary>
    private const string AssetsTabHeader = "ASSETS";

    /// <summary>Every tab's header text, left to right (a tab without a text header gives an empty string).</summary>
    private string[] TabHeaders() =>
        Tabs.Items.Cast<object>()
            .Select(o => o is System.Windows.Controls.TabItem { Header: string h } ? h : "")
            .ToArray();

    /// <summary>
    /// Select the tab whose header is exactly <paramref name="header"/> (D-DOC-073). A missing header is a defect
    /// in this window, not in the user's input, so it throws rather than land on whichever tab happens to be last.
    /// </summary>
    private void SelectTab(string header)
    {
        for (int i = 0; i < Tabs.Items.Count; i++)
            if (Tabs.Items[i] is System.Windows.Controls.TabItem { Header: string h } && string.Equals(h, header, StringComparison.Ordinal))
            {
                Tabs.SelectedIndex = i;
                return;
            }
        throw new InvalidOperationException($"MainWindow has no tab with the header '{header}'.");
    }

    /// <summary>Apply persisted overlay toggles + colour mode to the renderer and the toolbar.</summary>
    private void ApplySettingsToRenderer()
    {
        _renderer.ColorMode = _settings.ColorVoltage
            ? MapColourMode.Voltage : MapColourMode.Loading;
        _renderer.ShowFleet = _settings.Fleet;
        _renderer.ShowMinimap = _settings.Minimap;
        _renderer.ShowCoast = _settings.Coast;
        _renderer.ShowBoundaries = _settings.Boundaries;
        _renderer.ShowGspAreas = _settings.GspAreas;
        _renderer.ShowDnoAreas = _settings.DnoAreas;
        _renderer.ShowGspRegions = _settings.GspRegions;
        _renderer.ShowAssets = _settings.Assets;

        ColorBtn.Content = _settings.ColorVoltage ? " VOLTAGE" : " LOADING";
        FleetBtn.Opacity = _settings.Fleet ? 1.0 : 0.55;
        // Geography toggles are disabled + dimmed when no local geography is present.
        DnoBtn.IsEnabled = GspRegionBtn.IsEnabled = AssetsBtn.IsEnabled = _renderer.HasGeography;
        DnoBtn.Opacity = _renderer.HasGeography && _settings.DnoAreas ? 1.0 : 0.55;
        GspRegionBtn.Opacity = _renderer.HasGeography && _settings.GspRegions ? 1.0 : 0.55;
        AssetsBtn.Opacity = _renderer.HasGeography && _settings.Assets ? 1.0 : 0.55;
        _vm.SetColorLegend(_renderer.ColorMode);   // legend swatch/labels track the initial colour mode
    }

    private void OnClosing(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        // With a live bridge, the first close is cancelled while the socket is closed asynchronously; the window then
        // closes itself and this handler runs again with the bridge gone (see CloseAfterBridgeDisposal).
        if (!_closeAfterBridgeDisposed && _bridge is not null) { CloseAfterBridgeDisposal(e); return; }

        // Tear down the per-frame drivers on the way out. Harmless for the sole top-level window (process exit
        // reclaims them), but the static CompositionTarget.Rendering handler keeps this window alive and both it
        // and the host loop keep ticking the moment the window is ever hosted/reused.
        StopHostLoop();
        CompositionTarget.Rendering -= OnRender;
        // R101: stop the resident analysis cycle's worker threads (CycleVm owns a ResidentCycleService that was never
        // disposed).
        _vm.Cycle.Dispose();

        if (_renderer is not null)
        {
            _settings.ColorVoltage = _renderer.ColorMode == MapColourMode.Voltage;
            _settings.Fleet = _renderer.ShowFleet;
            _settings.Minimap = _renderer.ShowMinimap;
            _settings.Coast = _renderer.ShowCoast;
            _settings.Boundaries = _renderer.ShowBoundaries;
            _settings.GspAreas = _renderer.ShowGspAreas;
            _settings.DnoAreas = _renderer.ShowDnoAreas;
            _settings.GspRegions = _renderer.ShowGspRegions;
            _settings.Assets = _renderer.ShowAssets;
        }
        // The source is remembered by its path (labels repeat); the label is kept as a fallback for older readers.
        if (SourcePicker.SelectedItem is ModelSource s) { _settings.LastSource = s.Label; _settings.LastSourcePath = s.Key; }
        _settings.Save();
    }

    // Set once the bridge has been disposed on the way out, so the second (real) close does not wait again.
    private bool _closeAfterBridgeDisposed;

    /// <summary>
    /// The window's closing handler, bridge part. Closing used to block the UI thread for up to a second
    /// (<c>DisposeBridgeAsync().Wait(1 s)</c>), and any client continuation that needed the UI thread could not run
    /// meanwhile, so every close with a live bridge stalled. Now the first close is cancelled, the bridge is disposed
    /// asynchronously (bounded at 1 s so a stuck socket cannot hold the app open), and the window then closes itself.
    /// A disposal fault is written to the debug trace, not swallowed silently.
    /// </summary>
    private async void CloseAfterBridgeDisposal(System.ComponentModel.CancelEventArgs e)
    {
        if (_closeAfterBridgeDisposed || _bridge is null) return;
        e.Cancel = true;
        _closeAfterBridgeDisposed = true;
        try
        {
            Task dispose = DisposeBridgeAsync();
            if (await Task.WhenAny(dispose, Task.Delay(TimeSpan.FromSeconds(1))) == dispose) await dispose;
        }
        catch (Exception ex)
        {
            // The window is closing: there is no event log left to show, so record it where a developer can see it.
            System.Diagnostics.Trace.WriteLine($"GridSim: bridge close failed on exit: {ex.Message}");
        }
        // Queued, not called inline: if disposal finished synchronously we are still inside the Closing event, where
        // WPF refuses a nested Close().
        _ = Dispatcher.BeginInvoke(new Action(Close));
    }

    private void SourcePicker_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        if (SourcePicker.SelectedItem is ModelSource s) SwitchTo(s);
    }

    private void SolverPicker_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        if (SolverPicker.SelectedItem is SolverChoice c) _solverKind = c.Kind;
        // Rebuild the running sim on the newly-chosen engine, but only for a LOCAL model: a replay is pre-solved and a
        // bridge is solved by the server, so the choice is irrelevant to both. Rebuilding used to reconnect the bridge
        // (losing any what-if) or reload a replay and park it on frame 0. The picker is disabled in those modes too.
        if (_current is { Bridge: null, ReplayDir: null } s) SwitchTo(s);
    }

    private void Tabs_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        // Only the room tabs, not inner selectors (combos/grids) bubbling up. Gate the ASSETS explorer's per-frame
        // home refresh so the O(n) work is skipped while another tab is shown. D-DOC-073: matched by header, not by
        // "the last tab": ABOUT now sits after ASSETS, so the old index test refreshed the explorer only while ABOUT
        // was showing and froze it while ASSETS was.
        if (sender is not System.Windows.Controls.TabControl tc || !ReferenceEquals(e.OriginalSource, tc)) return;
        _vm.Assets.Active = tc.SelectedItem is System.Windows.Controls.TabItem { Header: AssetsTabHeader };
        // R020: the EXPERIMENTAL tab's per-frame audit and wave model run only while that tab is showing. Matched by
        // header text, not index, so inserting a tab cannot silently point the gate at the wrong one.
        _vm.Experimental.Active = tc.SelectedItem is System.Windows.Controls.TabItem { Header: "EXPERIMENTAL" };
        // The SPEED DOMAINS decomposition likewise runs only while its tab is showing (it used to run every frame).
        _vm.Domains.Active = tc.SelectedItem is System.Windows.Controls.TabItem { Header: "SPEED DOMAINS" };
    }

    private void SwitchTo(ModelSource s)
    {
        if (s.Bridge is not null) { LoadBridge(s); return; }
        _ = DisposeBridgeAsync();   // leaving any live bridge behind for a local model
        _vm.HideHud(); _vm.ClearBridgeStatus(); _lastOverlays = null;   // the live HUD + status pill + condition/stress overlays are bridge-only
        if (s.ReplayDir is not null) { LoadReplay(s); return; }

        // The host reads the model (MATPOWER through the unit normaliser, else the JSON case folder, then a tube-map
        // layout for a case without coordinates), applies the software licence gate (LICENSE.md section 2: bus count is
        // the only licensed quantity; Bundled Networks are exempt) and builds the live simulation on the engine the
        // solver picker chose (MATPOWER/test cases held steady; an authored asset-dynamics or protections bundle drives
        // the asset-aware integrator, D-DOC-079). A refusal is logged and changes nothing, as before.
        LiveLoadResult loaded = _host.LoadLive(s, _solverKind, App.Licence);
        if (!loaded.Loaded) { _vm.Log("--:--", loaded.Refusal!); return; }
        GridModel model = loaded.Model!;
        string activeSolver = loaded.ActiveSolver!;

        _current = s;
        SetTransportMode(replay: false);
        _paused = false; PauseBtn.Content = " PAUSE";   // live models animate (the host plays from the load)
        _trippedUnits.Clear();   // a new run: no unit has been tripped in it yet
        _vm.MapHint = DashboardViewModel.MapHintLocal;
        ActiveSolverText.Text = _solverKind == SolverKind.Auto ? $"auto * {activeSolver}" : activeSolver;
        // Drop the previous model's frame: a solve still in flight for the old model arrives with a stale epoch and is
        // dropped (DeliverHostFrame), and until the new model's first frame lands _renderer holds the NEW model while
        // _last held the OLD one - a trip/tooltip would resolve new bus ids against old data. Better to no-op than to
        // serve a wrong frame.
        _last = null;
        _vm.ClearSelection();
        _vm.ModelChanged();            // new model and timeline: rebuild per-model caches, drop integrated MACHINE state
        _renderer.Load(model);
        _host.RequestStep();           // the first step now, not one interval later (the old immediate StepOnce)
        _renderer.FitToView();
        _vm.Log("08:00", $"Loaded {s.Label}  -  {model.BusCount} buses, {model.Branches.Count} branches  -  {activeSolver}");
    }

    /// <summary>Load a pre-solved run directory and enter replay mode: paused on frame 0, driven by the cursor.</summary>
    private void LoadReplay(ModelSource s)
    {
        ReplayLoadResult replay = _host.LoadReplay(s.ReplayDir!);
        if (!replay.Loaded) { _vm.Log("--:--", replay.Refusal!); return; }
        int count = replay.Cursor!.Count;

        _current = s;
        ActiveSolverText.Text = "replay";
        _vm.MapHint = DashboardViewModel.MapHintReplay;
        _vm.ClearSelection();
        _vm.ModelChanged();            // fresh replay model and timeline (the timeline also resets on Restart/backward seeks)
        _renderer.Load(replay.LayoutModel!);

        SetTransportMode(replay: true);
        // Lowering Scrubber.Maximum below the old Value (loading a SHORTER run after a longer one) makes WPF
        // coerce Value down to the new max, firing Scrubber_ValueChanged outside the guard -> it would seek run B
        // to its LAST frame and open parked at the end (PLAY then restarts instead of playing from 0). Guard the
        // whole reset and pin Value to 0 so run B opens on frame 0.
        _scrubbing = true;
        Scrubber.Maximum = Math.Max(0, count - 1);
        Scrubber.Value = 0;
        _scrubbing = false;
        _paused = true; PauseBtn.Content = " PLAY";   // the host opens a replay paused

        RenderReplayFrame(replay.First!);
        _renderer.FitToView();
        _vm.Log("00:00", $"Loaded replay {replay.Title}  -  {count} frames, {replay.LayoutModel!.BusCount} buses" +
                         (count == 1 ? "" : $"  (sampled - {replay.Cursor.CurrentLabel} ... {replay.LastLabel})"));
    }

    /// <summary>
    /// Connect to the GDA bridge and enter live-stream mode. Mirrors <see cref="LoadReplay"/> but the frames are
    /// pushed by the server (which owns the physics): topology arrives once -> build + fit the schematic; each
    /// horizon-guarded <c>state</c> tick is rendered through the same path a replay frame is. No local solver, no
    /// local timer, no scrubber - cadence is server-driven.
    /// </summary>
    private async void LoadBridge(ModelSource s)
    {
        GridBridgeClient? prev = _bridge;               // dispose the outgoing client after swapping in the new one
        var client = new GridBridgeClient();
        // The default limits, and ws:// only when the settings file opts in (the client refuses it otherwise).
        (ResilienceOptions.Default with { AllowUnencryptedEndpoint = _settings.BridgeAllowUnencrypted }).ApplyTo(client);
        _bridge = client;
        _current = s;
        _host.Detach();                                 // no local solver or replay, tick 0: the server streams solved frames
        ActiveSolverText.Text = "bridge";
        _vm.MapHint = DashboardViewModel.MapHintBridge;
        // Drop the previous model's frame alongside the live-frame cache: during CONNECTING the renderer is about
        // to hold the NEW topology while _last still held the OLD one, so hover/right-click/trip-nearest would
        // resolve new-topology ids against old cached data (wrong bus, wrong "trip nearest gen"). Mirror SwitchTo's
        // guard and null _last too. (fix #7)
        _whatIf = false; _last = null; _lastLive = null; _bridgeLive = false; _bridgeTickUtc = null;
        _bridgeFaulted = false; _restoreLivePill = false;
        _vm.ClearSelection();
        _vm.ClearQueryResult();
        _vm.ModelChanged();

        // Live push source: no local scrubber/step, no local step timer; show the bridge-only zone/start pickers.
        SetTransportMode(replay: false);
        StepLabel.Visibility = StepPicker.Visibility = Visibility.Collapsed;
        SetBridgeControlsVisible(true);
        // The server owns the physics and the pace: the local solver choice and the playback speed do nothing here, so
        // both are disabled rather than left looking live (a solver change used to reconnect the bridge).
        SolverPicker.IsEnabled = false;
        SpeedBtn.IsEnabled = false;
        // Sync the pickers to the current case without firing a restart (we're about to start the stream ourselves).
        _bridgeControlsInit = true;
        BridgeZonePicker.SelectedItem = BridgeZones.FirstOrDefault(z => z.Case == _bridgeCase) ?? BridgeZones[0];
        _bridgeControlsInit = false;
        // D-DOC-141: the stream is server-paced and starts at once, so the bar opens PLAYING (label PAUSE). It used to
        // open as PLAY while frames already rendered, and PLAY only started the dash animation. PAUSE now holds the
        // display (see Pause_Click). No local step driver runs in bridge mode: the host loop plays nothing (the host was
        // detached above; stopping it here states that the local driver is off, as the old timer stop did).
        _paused = false; _host.Playing = false; PauseBtn.Content = " PAUSE";
        _heldHud = null; _heldMarket = null;
        _vm.SetBridgeStatus($"CONNECTING  *  {_bridgeCase}", BridgeWarnBrush);

        // Topology (once): build + fit the map on the UI thread. Ignore if we've since switched away.
        // BeginInvoke (non-blocking) not Invoke: the receive loop must never block waiting on the UI thread -
        // during OnClosing's bounded DisposeBridge Wait the UI thread is parked, and a blocking Invoke here would
        // deadlock it (~1 s stall + unclean socket teardown). Fire-and-forget dispatch instead. (fix #9)
        client.TopologyReceived += model => Dispatcher.BeginInvoke(() =>
        {
            if (!ReferenceEquals(_bridge, client)) return;
            _vm.ModelChanged();   // a topology is a (possibly different) model: per-model caches rebuild on its frames
            _renderer.Load(model);
            _renderer.FitToView();
        });
        // Every accepted (forward-inference-guarded) tick renders through the shared replay path - unless a
        // what-if query result is currently overlaid, in which case the live frame is remembered but held off
        // the map so the solved scenario/feasibility state stays put until the user returns to the live stream.
        // StateReceived (not FrameReceived) also carries the raw scalars + overlays the SimFrame can't: they drive
        // the live HUD (always) and the Condition/Stress colour overlays (when not showing a what-if).
        client.StateReceived += (f, scalars, overlays) => Dispatcher.BeginInvoke(() =>
        {
            if (!ReferenceEquals(_bridge, client)) return;
            _lastLive = f;
            _lastOverlays = overlays;
            // D-DOC-141: while PAUSED the display is held: the frame, HUD and overlays are remembered (the newest
            // wins) and shown on PLAY. The stream itself keeps running on the server.
            if (_paused) { _heldHud = scalars; return; }
            _vm.UpdateHud(scalars?.InertiaMwS, scalars?.ConditionIndex, scalars?.RocofHeadroomHzS);
            if (!_bridgeLive) { _bridgeLive = true; _vm.SetBridgeStatus($"LIVE  *  streaming {_bridgeCase}", BridgeOkBrush); }
            // A finished scalars-only (or failed) query must not leave WHAT-IF / QUERY FAILED on the pill while live
            // frames render; the query's outcome stays in the results panel, the event log and (on failure) the alarms.
            else if (_restoreLivePill && !_whatIf) { _restoreLivePill = false; _vm.SetBridgeStatus($"LIVE  *  streaming {_bridgeCase}", BridgeOkBrush); }
            if (!_whatIf)
            {
                RenderReplayFrame(f);
                _renderer.SetOverlay(overlays?.ConditionByBus, overlays?.StressByBranch);
            }
        });
        // The per-tick operator (balancing/constraints/warnings) + market (prices/interconnectors/carbon) series
        // ride their own blocks; feed the OPERATOR/MARKET tabs, mirroring the HUD side-channel above.
        // N144: the tick's own UTC instant, for the MARKET price trace's one-sample-per-settlement-period rule.
        // RawDataReceived fires for every accepted state tick, just BEFORE MarketDataReceived in the same call, and both
        // handlers queue onto the UI thread in that order, so the market handler always reads its own tick's instant.
        client.RawDataReceived += (f, mix, wx, ics, ts) => Dispatcher.BeginInvoke(() =>
        {
            if (!ReferenceEquals(_bridge, client)) return;
            _bridgeTickUtc = ts;
        });
        client.MarketDataReceived += (f, op, mk) => Dispatcher.BeginInvoke(() =>
        {
            if (!ReferenceEquals(_bridge, client)) return;
            // D-DOC-141: held while PAUSED (the newest tick's blocks are applied on PLAY).
            if (_paused) { _heldMarket = (op, mk, _bridgeTickUtc); return; }
            _vm.UpdateMarketData(op, mk, _bridgeTickUtc);
        });
        // Estimated-source frames carry per-tick estimation quality: surface the chi-square verdict + fit stats on
        // the status pill so a measurement-fitted state is never shown without its honesty layer.
        client.EstimateReceived += (f, src, est) => Dispatcher.BeginInvoke(() =>
        {
            // While PAUSED (D-DOC-141) the pill keeps saying so; the next frame after PLAY brings the verdict back.
            if (!ReferenceEquals(_bridge, client) || est is null || _paused) return;
            // The text is EstimateBlock.QualitySummary, shared with the web console's quality pill.
            _vm.SetBridgeStatus($"ESTIMATED  *  {_bridgeCase}  *  {est.QualitySummary()}",
                est.QualityOk ? BridgeOkBrush : BridgeBadBrush);
        });
        client.Faulted += ex => Dispatcher.BeginInvoke(() =>
        {
            if (!ReferenceEquals(_bridge, client)) return;
            // A receive-loop fault includes a horizon rejection (a state tick newer than the 169 h horizon, ForwardInferenceGuard.HorizonHours).
            bool horizon = ex is GridSim.Core.Diagnostics.ForwardInferenceException;
            // Fail loud: no more frames will arrive, so nothing on screen is live any more. The pill alone used to change
            // while the header lamp kept its last verdict (SYSTEM NORMAL) and the HUD, OPERATOR and MARKET tiles kept
            // their last values indefinitely. Now the lamp reads STREAM LOST, the bridge-only HUD and lake blocks are
            // cleared, an alarm is raised, and right-click on the map offers to reconnect.
            _bridgeFaulted = true;
            _bridgeLive = false;
            _vm.SetBridgeStatus(horizon ? "HORIZON REJECTION  *  stream stopped  *  right-click the map to reconnect"
                                        : "BRIDGE FAULT  *  disconnected  *  right-click the map to reconnect", BridgeBadBrush);
            _vm.MarkStreamLost(horizon ? "HORIZON REJECTION" : "BRIDGE FAULT");
            _vm.Log(_vm.Clock, $"Bridge fault: {ex.Message}");
        });
        // One unreadable bridge message: logged (with the running count) while the stream carries on (R275).
        client.MessageRejected += ex => Dispatcher.BeginInvoke(() =>
        {
            if (!ReferenceEquals(_bridge, client)) return;
            _vm.Log(_vm.Clock, $"Bridge frame rejected ({client.RejectedMessages} so far): {ex.Message}");
        });
        // GR-228: a failure in this app's own handling of a good frame is a GridSim fault, logged as such (not as a bad
        // server message); the stream carries on.
        client.SubscriberFailed += ex => Dispatcher.BeginInvoke(() =>
        {
            if (!ReferenceEquals(_bridge, client)) return;
            _vm.Log(_vm.Clock, ex.Message);
        });

        if (prev is not null) { try { await prev.DisposeAsync(); } catch { /* best-effort */ } }

        try
        {
            await client.ConnectAsync(new Uri(s.Bridge!));
            // Stream the chosen DNO zone from the chosen historical start, comfortably behind the no-forward-inference
            // horizon. The same anchor instant is where on-demand what-if queries are evaluated (always historical).
            _bridgeQueryAt = DateTime.UtcNow - SelectedBridgeBack();
            await client.StartAsync(_bridgeCase, _bridgeQueryAt, fps: BridgeStreamFps, source: _bridgeSource);
            _vm.Log("00:00", $"Connected to bridge {BridgeProtocol.DisplayEndpoint(s.Bridge!)}  -  streaming {_bridgeCase} ({_bridgeSource}) from {_bridgeQueryAt:yyyy-MM-dd HH:mm}Z");
        }
        catch (Exception ex)
        {
            if (ReferenceEquals(_bridge, client))
            {
                _vm.SetBridgeStatus($"CONNECT FAILED  *  {ex.Message}", BridgeBadBrush);
                _vm.Log("--:--", $"Bridge connect failed: {ex.Message}");
                await DisposeBridgeAsync();
            }
        }
    }

    /// <summary>The historical look-back for the current start preset (defaults to <see cref="DefaultBridgeBack"/>,
    /// 9 days, if none is selected). R210: this summary used to say "2 days" while the code returned 9.</summary>
    private TimeSpan SelectedBridgeBack() =>
        BridgeStartPicker.SelectedItem is BridgeStart s ? s.Back : DefaultBridgeBack;

    private void BridgeZonePicker_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        if (_bridgeControlsInit || _bridge is null) return;   // programmatic setup, or not in bridge mode
        if (BridgeZonePicker.SelectedItem is BridgeZone z) _bridgeCase = z.Case;
        ApplyBridgeStream();
    }

    private void BridgeStartPicker_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        if (_bridgeControlsInit || _bridge is null) return;
        ApplyBridgeStream();
    }

    private void BridgeSourcePicker_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        if (BridgeSourcePicker.SelectedItem is BridgeSourceChoice c) _bridgeSource = c.Source;
        if (_bridgeControlsInit || _bridge is null) return;
        ApplyBridgeStream();
    }

    /// <summary>
    /// Re-anchor the live stream on the currently-selected zone + historical start, without reconnecting: stop the
    /// current stream and start the new one on the same socket. Any what-if overlay is cleared first. The chosen start
    /// is always behind the horizon (the presets guarantee it), so the server never rejects it.
    /// </summary>
    private async void ApplyBridgeStream()
    {
        if (_bridge is not GridBridgeClient b) return;
        _whatIf = false; _vm.ClearQueryResult();
        _bridgeLive = false;
        // A new stream plays (D-DOC-141): a held display of the old stream has nothing left to show.
        _paused = false; PauseBtn.Content = " PAUSE"; _heldHud = null; _heldMarket = null;
        // Mirror LoadBridge's fix #7: the restarted stream can carry a DIFFERENT case (zone picker),
        // and resolving new-topology ids against these cached frames gives "wrong bus, wrong trip
        // nearest gen". Drop them before the new stream's first frame arrives.
        _last = null; _lastLive = null; _lastOverlays = null;
        _vm.ModelChanged();   // the restarted stream may be a different zone (a different model)
        _bridgeQueryAt = DateTime.UtcNow - SelectedBridgeBack();
        _vm.SetBridgeStatus($"RESTARTING  *  {_bridgeCase}", BridgeWarnBrush);
        _vm.Log(_vm.Clock, $"Bridge: stream {_bridgeCase} from {_bridgeQueryAt:yyyy-MM-dd HH:mm}Z");

        // Serialise restarts on the one socket. Scrolling the ZONE picker fires several changes in quick succession, and
        // overlapping Stop/Start pairs could reach the server as Stop, Stop, Start, Start (two streams, or a rejected
        // second start). A restart already running just notes that another is wanted; when it finishes it runs once
        // more with the NEWEST selection, so a burst of changes coalesces into at most one extra restart.
        if (_bridgeRestartRunning) { _bridgeRestartPending = true; return; }
        _bridgeRestartRunning = true;
        try
        {
            do
            {
                _bridgeRestartPending = false;
                // Capture the selection BEFORE the first await, so this Stop/Start pair starts the case it was asked for.
                string caseName = _bridgeCase, source = _bridgeSource;
                DateTime at = _bridgeQueryAt;
                try
                {
                    await b.StopAsync();
                    await b.StartAsync(caseName, at, fps: BridgeStreamFps, source: source);
                }
                catch (Exception ex)
                {
                    if (ReferenceEquals(_bridge, b))
                    {
                        _vm.SetBridgeStatus($"RESTART FAILED  *  {ex.Message}", BridgeBadBrush);
                        _vm.Log(_vm.Clock, $"Bridge restart failed: {ex.Message}");
                    }
                }
            } while (_bridgeRestartPending && ReferenceEquals(_bridge, b));
        }
        finally { _bridgeRestartRunning = false; }
    }

    /// <summary>Detach and dispose the live bridge client, if any. Late frames are ignored once <c>_bridge</c> is cleared.</summary>
    private Task DisposeBridgeAsync()
    {
        GridBridgeClient? b = _bridge;
        _bridge = null;
        return b is null ? Task.CompletedTask : b.DisposeAsync().AsTask();
    }

    /// <summary>Render one replay/bridge frame through the same views the live solve feeds, and reflect the cursor.</summary>
    private bool RenderReplayFrame(SimFrame f)
    {
        if (!_host.IsReplay && _bridge is null) return false;
        // _last (which drives hover, the fly-out and trip selection) is set only AFTER the views accepted the frame: a
        // frame that fails to render must not become the current frame (it used to be assigned before the guard).
        // Guard the render exactly like the local-sim path (ShowHostFrame): a bad streamed/replayed frame
        // (e.g. an empty Vm making DashboardViewModel.Update's Vm.Min()/Max() throw) must land on the
        // SOLVE FAULT path, not crash the whole app out of the bridge's Dispatcher callback.
        try
        {
            // Compute boundary transfers from the streamed/replayed frame so the Operator + Network
            // boundary panels populate in bridge/replay mode (they carry ETYS cuts on the full model).
            // D-DOC-087: the analysis refuses a non-converged state; such a frame shows no boundary transfers.
            var boundaries = f.Result.Converged
                ? GridSim.Core.Analysis.BoundaryAnalysis.Evaluate(f.Model, f.Result)
                : Array.Empty<GridSim.Core.Analysis.BoundaryFlow>();
            _vm.Update(f, boundaries);
            _renderer.SetState(f.Result);
            // The map's ETYS cut labels only move via SetBoundaries (the local-sim path in ShowHostFrame
            // calls it every frame) - without this they sat at 0 MW / 0% for all of replay/bridge mode.
            _renderer.SetBoundaries(boundaries);
            _last = f;
        }
        catch (Exception ex)
        {
            SolidColorBrush fault = Palette.Frozen(Palette.Loading(1.2));
            _vm.Status = " SOLVE FAULT";
            _vm.StatusBrush = fault;
            _vm.Log(_vm.Clock, $"Render failed: {ex.Message}");
            _vm.AddAlarm($"SOLVE FAULT: {ex.Message}", fault);
            return false;
        }

        // Scrubber/readout only apply to a seekable pre-solved run. The cursor read here is the frame's own: a loop frame
        // is shown only while the host loop waits for it and only when no hand-driven move came after it (its epoch is
        // current), and a hand-driven move shows its frame at once, so the cursor cannot have moved on meanwhile.
        if (_host.ReplayCursor is ReplayCursor cursor)
        {
            _scrubbing = true;                          // move the slider without re-triggering a seek
            Scrubber.Value = cursor.Index;
            _scrubbing = false;
            FrameReadout.Text = $"frame {cursor.Index + 1}/{cursor.Count}  *  {cursor.CurrentLabel}";
        }
        return true;
    }

    /// <summary>Show live controls (step-size) or replay controls (scrubber + frame stepping + readout).</summary>
    private void SetTransportMode(bool replay)
    {
        Visibility live = replay ? Visibility.Collapsed : Visibility.Visible;
        Visibility rep = replay ? Visibility.Visible : Visibility.Collapsed;
        StepLabel.Visibility = StepPicker.Visibility = live;
        RestartBtn.Visibility = StepBackBtn.Visibility = StepFwdBtn.Visibility = rep;
        Scrubber.Visibility = FrameReadout.Visibility = rep;
        // A replay is pre-solved, so the SOLVER picker has nothing to rebuild (it used to reload the run and park it on
        // frame 0); the playback speed still paces the replay timer. LoadBridge disables both after this call.
        SolverPicker.IsEnabled = !replay;
        SpeedBtn.IsEnabled = true;
        SetBridgeControlsVisible(false);   // bridge-only zone/start pickers; LoadBridge re-shows them
    }

    /// <summary>Show or hide the bridge-only zone/start/source pickers on the transport bar.</summary>
    private void SetBridgeControlsVisible(bool show)
    {
        Visibility v = show ? Visibility.Visible : Visibility.Collapsed;
        BridgeZoneLabel.Visibility = BridgeZonePicker.Visibility = v;
        BridgeStartLabel.Visibility = BridgeStartPicker.Visibility = v;
        BridgeSourceLabel.Visibility = BridgeSourcePicker.Visibility = v;
    }

    private void Restart_Click(object sender, RoutedEventArgs e)
    {
        if (!_host.IsReplay) return;
        ReplayPosition at = _host.ReplayRestart();
        _vm.ResetTimeline();           // starting over from frame 0: integrate the MACHINE state afresh
        RenderReplayFrame(at.Frame);
    }

    private void StepBack_Click(object sender, RoutedEventArgs e)
    {
        if (!_host.IsReplay) return;
        ReplayPosition at = _host.ReplayRewind();
        // A step back breaks the timeline: playing forward again would integrate the same frames a second time into
        // the MACHINE accumulators (Hz.s banked, regen, BESS throughput), so they restart here as on a scrub to frame 0.
        _vm.ResetTimeline();
        RenderReplayFrame(at.Frame);
    }

    private void StepForward_Click(object sender, RoutedEventArgs e)
    {
        if (!_host.IsReplay) return;
        ReplayPosition at = _host.ReplayAdvance();
        RenderReplayFrame(at.Frame);
    }

    private void Scrubber_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (!_host.IsReplay || _scrubbing) return;
        ReplayPosition at = _host.ReplaySeek((int)Math.Round(e.NewValue));
        // Any BACKWARD scrub (and scrub-to-0) restarts the timeline exactly like the Restart button: the MACHINE
        // accumulators (Hz*s, regen GBP, MWh cycled) integrate forward, so playing on from an earlier frame without
        // resetting double-counts the frames between. It used to reset only on a scrub to frame 0.
        if (at.Cursor.Index == 0 || at.Cursor.Index < at.Before) _vm.ResetTimeline();
        RenderReplayFrame(at.Frame);
    }

    /// <summary>Open a folder-browse dialog and load the chosen directory as a replay run.</summary>
    private void LoadRun_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new Microsoft.Win32.OpenFolderDialog { Title = "Choose a GridSim run directory (with an exports/ folder)" };
        if (dlg.ShowDialog(this) != true) return;
        if (!PresolvedReplay.IsRunDirectory(dlg.FolderName))
        {
            _vm.Log("--:--", "Not a run directory (no exports/slice-*.json or state-*.json).");
            return;
        }
        ModelSource src = ModelCatalog.ReplaySource(dlg.FolderName);
        // A run already in the list (same folder, by full path) is selected, not added a second time.
        if (_sources.FirstOrDefault(x => x.SameSourceAs(src)) is ModelSource existing)
        {
            if (ReferenceEquals(SourcePicker.SelectedItem, existing)) SwitchTo(existing);   // reselecting fires nothing
            else SourcePicker.SelectedItem = existing;
            return;
        }
        // A new run whose folder name repeats another source's label gets its parent folder in the label.
        _sources = ModelCatalog.Disambiguate(new List<ModelSource>(_sources) { src });
        src = _sources[^1];
        SourcePicker.ItemsSource = null;
        SourcePicker.ItemsSource = _sources;
        SourcePicker.SelectedItem = src;   // fires SwitchTo -> LoadReplay
    }

    // N151: the audit-log viewer (Core AuditLogDisplay, verified logs only; a tampered log shows only its verdict).
    private void AuditLog_Click(object sender, RoutedEventArgs e) => new AuditLogWindow(this).Show();

    private bool _paused;
    private double _playback = 1;                       // playback rate shown on the SPEED button (the host paces the loop)
    // The playback rates, the STEP choices and the x1 loop interval now live in GridSim.Ui (TransportChoices,
    // SimHost.BasePlaybackMs), values and order unchanged; the host holds the chosen step length and rate.
    private static readonly double[] Playbacks = TransportChoices.Playbacks.ToArray();
    private static readonly IReadOnlyList<StepChoice> StepChoices = TransportChoices.Steps;

    private void Pause_Click(object sender, RoutedEventArgs e)
    {
        // D-DOC-141: on a bridge feed the stream is paced by the server, so there is no local timer to stop or start.
        // PAUSE holds the display (map, HUD, overlays, OPERATOR / MARKET) on the frame showing; PLAY shows the newest
        // frame that arrived meanwhile and follows the stream again. It used to start only the dash animation.
        if (_bridge is not null) { SetBridgePaused(!_paused); return; }

        _paused = !_paused;
        if (_paused) { _host.Playing = false; PauseBtn.Content = IsReplay ? " PLAY" : " RESUME"; }
        else
        {
            if (_host.ReplayCursor is { AtEnd: true }) { _host.ReplayRestart(); _vm.ResetTimeline(); }   // play again from the start, fresh integrators
            _host.Playing = true;
            PauseBtn.Content = " PAUSE";
        }
    }

    /// <summary>
    /// D-DOC-141: hold (<paramref name="pause"/> true) or release the bridge display. While held, incoming frames are
    /// remembered but not shown and the status pill says so; on release the newest held frame, HUD, overlays and
    /// market blocks are shown (unless a what-if result is on the map, which keeps the live frame off as before).
    /// </summary>
    private void SetBridgePaused(bool pause)
    {
        _paused = pause;
        PauseBtn.Content = pause ? " PLAY" : " PAUSE";
        if (pause)
        {
            _vm.SetBridgeStatus($"PAUSED  *  {_bridgeCase}  *  display held, stream still running", BridgeWarnBrush);
            return;
        }

        if (_heldHud is { } hud) _vm.UpdateHud(hud.InertiaMwS, hud.ConditionIndex, hud.RocofHeadroomHzS);
        if (_heldMarket is { } held) _vm.UpdateMarketData(held.Op, held.Mk, held.TickUtc);
        _heldHud = null; _heldMarket = null;
        if (_whatIf)
        {
            _vm.SetBridgeStatus("WHAT-IF  *  live frames held off (RETURN TO LIVE to resume)", BridgeWarnBrush);
            return;
        }
        if (_lastLive is SimFrame f)
        {
            RenderReplayFrame(f);
            _renderer.SetOverlay(_lastOverlays?.ConditionByBus, _lastOverlays?.StressByBranch);
            _bridgeLive = true;
            _vm.SetBridgeStatus($"LIVE  *  streaming {_bridgeCase}", BridgeOkBrush);
        }
        else _vm.SetBridgeStatus($"CONNECTING  *  {_bridgeCase}", BridgeWarnBrush);   // nothing has arrived yet
    }

    // Playback speed = how fast it plays in real time (the host loop's cadence), decoupled from the sim step.
    private void Speed_Click(object sender, RoutedEventArgs e)
    {
        int i = Array.IndexOf(Playbacks, _playback);
        _playback = Playbacks[(i + 1) % Playbacks.Length];
        SpeedBtn.Content = $"x{_playback:0}";
        _host.PlaybackRate = _playback;   // the loop now waits SimHost.BasePlaybackMs / rate
    }

    // Simulation time-step (dt) = how much sim-time each solve advances. Fine dt shows live frequency
    // dynamics (the swing equation is integrated at this dt); coarse dt sweeps the day/scenario.
    private void StepPicker_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        if (StepPicker.SelectedItem is StepChoice s) _host.StepMinutes = s.Minutes;
    }

    // The running host loop (null until OnLoaded starts it), the token that ends it and the action that cancels that
    // token. The loop's CancellationTokenSource itself is owned by the loop: it is disposed when the loop ends.
    private Task? _hostLoop;
    private CancellationToken _hostToken = new(canceled: false);
    private Action _stopHostLoop = () => { };

    /// <summary>
    /// Start the host's play loop on the thread pool (it replaces the DispatcherTimer that called StepOnce). Each frame
    /// the loop makes is shown on this window's dispatcher (DeliverHostFrame) and the loop waits for that before it
    /// makes the next, so solves never overlap and frames are shown in the order they were made, as before. A fault in
    /// the loop itself is rethrown on the UI thread (fail loud: the app's crash handler reports it), never left in an
    /// unobserved task.
    /// </summary>
    private void StartHostLoop()
    {
        var cts = new CancellationTokenSource();
        CancellationToken ct = cts.Token;
        _hostToken = ct;
        // Cancel and dispose under one lock, so a stop that races the loop's end never cancels a disposed source.
        var stopGate = new object();
        bool ended = false;
        _stopHostLoop = () => { lock (stopGate) if (!ended && !ct.IsCancellationRequested) cts.Cancel(); };
        _hostLoop = Task.Run(() => _host.RunAsync(DeliverHostFrame, ct));
        _hostLoop.ContinueWith(t =>
        {
            if (t.IsFaulted)
            {
                Exception ex = t.Exception!.GetBaseException();
                Dispatcher.BeginInvoke(() => System.Runtime.ExceptionServices.ExceptionDispatchInfo.Throw(ex));
            }
            lock (stopGate) { ended = true; cts.Dispose(); }   // the loop has ended: nothing reads its token any more
        }, CancellationToken.None, TaskContinuationOptions.None, TaskScheduler.Default);
    }

    /// <summary>End the host loop (window closing, or a launch that was refused). Safe to call more than once.</summary>
    private void StopHostLoop() => _stopHostLoop();

    /// <summary>The host loop's delivery: show the frame on the UI thread; the loop awaits the returned task.</summary>
    private Task DeliverHostFrame(HostFrame frame) => Dispatcher.InvokeAsync(() => ShowHostFrame(frame)).Task;

    /// <summary>
    /// Show one frame from the host loop: the old <c>StepOnce</c> display path, unchanged. A frame made before a model
    /// switch or a hand-driven replay move is stale and dropped (the old "discard if the model was switched").
    /// </summary>
    private void ShowHostFrame(HostFrame frame)
    {
        if (_renderer is null || _hostToken.IsCancellationRequested) return;
        if (frame.Epoch != _host.Epoch)
        {
            // Stale. One thing it says still holds: a replay that reached its last frame stopped playing. Before the move
            // the end frame was shown and then the bar was set to PLAY, and a later hand-driven move kept that, so the
            // bar follows the host when it has stopped.
            if (frame.StoppedAtEnd && !_host.Playing) { _paused = true; PauseBtn.Content = " PLAY"; }
            return;
        }

        if (frame.Kind == HostFrameKind.Replay)
        {
            RenderReplayFrame(frame.Frame!);
            if (frame.StoppedAtEnd) { _paused = true; PauseBtn.Content = " PLAY"; }  // the host stopped at the end
            return;
        }

        int tick = frame.Tick;
        if (frame.Kind == HostFrameKind.LiveFaulted)
        {
            // A solver exception must not silently freeze the dashboard on the last good
            // frame - surface it on the status lamp, alarms and event log.
            Exception ex = frame.Fault ?? new InvalidOperationException("unknown solve fault");
            SolidColorBrush fault = Palette.Frozen(Palette.Loading(1.2));
            _vm.Status = " SOLVE FAULT";
            _vm.StatusBrush = fault;
            _vm.Log(_vm.Clock, $"Solve failed at tick {tick}: {ex.Message}");
            _vm.AddAlarm($"SOLVE FAULT: {ex.Message}", fault);
            return;
        }
        // An exception while showing the frame (e.g. Vm.Min() on an empty result) must not end the host loop or freeze
        // the dashboard silently every tick. Route it through the same SOLVE FAULT path as a solve fault.
        try
        {
            SimFrame f = frame.Frame!;
            IReadOnlyList<BoundaryFlow> boundaries = frame.Boundaries;
            double solveMs = frame.SolveMs;
            _vm.Update(f, boundaries, solveMs);
            _renderer.SetState(f.Result);
            _renderer.SetBoundaries(boundaries);
            _last = f;   // only a frame the views accepted drives hover, the fly-out and trips
        }
        catch (Exception ex)
        {
            SolidColorBrush fault = Palette.Frozen(Palette.Loading(1.2));
            _vm.Status = " SOLVE FAULT";
            _vm.StatusBrush = fault;
            _vm.Log(_vm.Clock, $"Render failed at tick {tick}: {ex.Message}");
            _vm.AddAlarm($"SOLVE FAULT: {ex.Message}", fault);
        }
    }

    private void OnRender(object? sender, EventArgs e)
    {
        if (_renderer is null || e is not RenderingEventArgs args) return;
        // Throttle animation to ~15 fps - a schematic's marching dashes don't need 60 fps,
        // and the full OnRender (rings, labels, flow geometry) is expensive on large networks.
        double sinceLast = _lastAnimate == default ? 1.0 : (args.RenderingTime - _lastAnimate).TotalSeconds;
        if (sinceLast < AnimationMinIntervalS) return;
        _lastAnimate = args.RenderingTime;
        // Advance the animation by the time since the PREVIOUS ANIMATE, not since the previous rendering
        // callback. Rendering fires every ~16 ms but Animate only every >= 65 ms, so passing the ~16 ms
        // inter-callback time ran the marching dashes and the wave at about a quarter of the px/s that
        // FastSchematic.Animate documents. The < 0.2 s clamp still skips the first frame (sinceLast = 1.0)
        // and any long stall, so a resumed window does not jump.
        if (sinceLast > 0 && sinceLast < AnimationMaxDtS && !_paused) _renderer.Animate(sinceLast);
    }

    private void Schematic_MouseMove(object sender, MouseEventArgs e)
    {
        if (_last is null || e.LeftButton == MouseButtonState.Pressed) { HoverTip.Visibility = Visibility.Collapsed; return; }
        System.Windows.Point p = e.GetPosition(_renderer);
        int? id = _renderer.HitTestNode(p);
        Bus? bus = id is null ? null : _last.Model.Buses.FirstOrDefault(b => b.Id == id);
        if (bus is null) { HoverTip.Visibility = Visibility.Collapsed; return; }

        double vm = 1.0;
        for (int i = 0; i < _last.Result.BusIds.Count; i++) if (_last.Result.BusIds[i] == bus.Id) { vm = _last.Result.Vm[i]; break; }
        HoverText.Text = $"{bus.Name}\n{bus.BaseKv:F0} kV  {vm:F3} pu  {bus.Pd:F0} MW";
        System.Windows.Controls.Canvas.SetLeft(HoverTip, p.X + 14);
        System.Windows.Controls.Canvas.SetTop(HoverTip, p.Y + 14);
        HoverTip.Visibility = Visibility.Visible;
    }

    private void Schematic_RightClick(object sender, MouseButtonEventArgs e)
    {
        if (_renderer is null) return;
        System.Windows.Point p = e.GetPosition(_renderer);
        // In bridge mode, right-click offers the GDA on-demand physics (feasibility / scenario) as a context menu.
        // In live/replay mode it keeps the original behaviour: right-click a bus trips its largest infeed locally.
        if (_bridge is not null && _bridgeFaulted) { ShowBridgeReconnectMenu(); return; }
        if (_bridge is not null) { ShowBridgeContextMenu(p); return; }
        int? id = _renderer.HitTestNode(p);
        if (id is not null) TripAtBus(id.Value);
    }

    /// <summary>
    /// Build and pop the GDA-bridge context menu at <paramref name="screenPt"/>: a BUS offers a +500 MW feasibility
    /// study here and a scenario trip of its nearest generator; a BRANCH/generator corridor offers a scenario trip
    /// of that element. Each item fires a <see cref="GridBridgeClient.Query"/> and renders the returned what-if.
    /// </summary>
    /// <summary>After a bridge fault: a one-item menu that reconnects to the same bridge source (a fresh socket and
    /// stream, exactly as selecting the source does). Selecting the already-selected source fires nothing, so this is
    /// the way back without restarting the app.</summary>
    private void ShowBridgeReconnectMenu()
    {
        if (_current is not { Bridge: not null } source) return;
        var menu = new System.Windows.Controls.ContextMenu { PlacementTarget = _renderer };
        menu.Items.Add(MakeMenuItem("Reconnect to the bridge", () => LoadBridge(source)));
        menu.IsOpen = true;
    }

    private void ShowBridgeContextMenu(System.Windows.Point screenPt)
    {
        if (_last is null) return;
        GridModel m = _last.Model;
        var menu = new System.Windows.Controls.ContextMenu { PlacementTarget = _renderer };

        int? busId = _renderer.HitTestNode(screenPt);
        if (busId is int bid)
        {
            Bus? bus = m.Buses.FirstOrDefault(b => b.Id == bid);
            string bn = bus?.Name ?? $"bus {bid}";
            menu.Items.Add(MakeMenuItem($"Feasibility: +{FeasibilityProbeMw:F0} MW here  ({bn})", () => RunFeasibility(bid, bn)));
            // Say up front which unit would be tripped: the largest physical unit generating at this bus, or, when the
            // bus has none, the largest unit in the whole model (labelled as such; it is not "nearest" in any sense).
            bool hasLocal = InfeedSelection.LargestOnline(m, bid) is not null;
            menu.Items.Add(MakeMenuItem(hasLocal
                    ? $"Scenario: trip the largest generator at {bn}"
                    : $"Scenario: trip the largest unit in the model (none generating at {bn})",
                () => RunTripNearest(bid, bn)));
        }
        else if (_renderer.HitTestBranch(screenPt) is int idx && idx >= 0 && idx < m.Branches.Count)
        {
            Branch br = m.Branches[idx];
            // The server selects the element to trip BY NAME (a case-insensitive substring, generators first, then
            // lines, then transformers). An unnamed branch, or a name that also matches other elements, would trip
            // something else or nothing, so the item is shown disabled with the reason instead of being sent.
            string? refusal = BridgeTripRefusal(m, br.Name);
            string nm = br.Name ?? $"{br.From}-{br.To}";
            var item = MakeMenuItem(refusal is null ? $"Scenario: trip this  ({nm})" : $"Scenario: trip this  ({nm})  -  {refusal}",
                () => RunScenario(nm, $"trip {nm}"));
            item.IsEnabled = refusal is null;
            menu.Items.Add(item);
        }

        if (_whatIf)
        {
            if (menu.Items.Count > 0) menu.Items.Add(new System.Windows.Controls.Separator());
            menu.Items.Add(MakeMenuItem("Return to live stream", ReturnToLive));
        }
        if (menu.Items.Count == 0) return;
        menu.IsOpen = true;
    }

    private static System.Windows.Controls.MenuItem MakeMenuItem(string header, Action onClick)
    {
        var mi = new System.Windows.Controls.MenuItem { Header = header };
        mi.Click += (_, _) => onClick();
        return mi;
    }

    // ---- on-demand physics queries over the bridge (results rendered as a transient what-if) ----

    private void RunFeasibility(int busId, string busName) =>
        RunQuery("feasibility", new { kind = "new_load", bus = busId, mw = FeasibilityProbeMw },
                 $"Feasibility  *  +{FeasibilityProbeMw:F0} MW at {busName}");

    private void RunTripNearest(int busId, string busName)
    {
        if (_last is null) return;
        GridModel m = _last.Model;
        // The shared Core rule (InfeedSelection, as the local T key and right-click use): the largest PHYSICAL unit
        // generating at this bus, so a slack balancer or voltage-control device is never chosen. With none at the bus,
        // the largest unit in the model, and the label says so (it used to say "nearest" for a unit anywhere in GB).
        Generator? local = InfeedSelection.LargestOnline(m, busId)?.Unit;
        Generator? g = local ?? InfeedSelection.LargestOnline(m)?.Unit;
        if (g is null) { _vm.Log(_vm.Clock, $"No generator to trip near {busName}"); return; }
        // The server picks the unit BY NAME; refuse a name that is missing or not unique rather than trip a namesake.
        if (BridgeTripRefusal(m, g.Name) is string refusal)
        {
            _vm.Log(_vm.Clock, $"Scenario not sent for {g.Name ?? $"the unit at bus {g.Bus}"}: {refusal}");
            return;
        }
        string trip = g.Name!;
        RunScenario(trip, local is not null
            ? $"trip {trip} (largest generator at {busName})"
            : $"trip {trip} (largest unit in the model; none generating at {busName})");
    }

    /// <summary>
    /// Why a bridge scenario trip of the element named <paramref name="name"/> must not be sent, or null when it is
    /// safe. The bridge server resolves the trip selector BY NAME, as a case-insensitive substring matched against
    /// generators, then lines, then transformers (technical chapter 9, the <c>scenario</c> op). So an element with no
    /// name cannot be addressed at all, and a name that is a substring of more than one element's name in this model
    /// could make the server trip a different element (a namesake, or a unit whose name merely contains it). Both are
    /// refused with the reason, never guessed.
    /// </summary>
    internal static string? BridgeTripRefusal(GridModel m, string? name)
    {
        if (string.IsNullOrWhiteSpace(name)) return "it has no name, and the bridge selects the element to trip by name";
        int matches = 0;
        foreach (Generator g in m.Generators)
            if (g.Name is { } n && n.Contains(name, StringComparison.OrdinalIgnoreCase)) matches++;
        foreach (Branch b in m.Branches)
            if (b.Name is { } n && n.Contains(name, StringComparison.OrdinalIgnoreCase)) matches++;
        return matches == 1 ? null
            : $"its name matches {matches} elements in this model, and the bridge selects by name, so it could trip a different one";
    }

    private void RunScenario(string trip, string label) =>
        RunQuery("scenario", new { trip }, $"Scenario  *  {label}");

    /// <summary>
    /// Fire an on-demand bridge query at the historical stream anchor, marshal the result back to the UI thread,
    /// and render it: a <c>solveExport</c> becomes a transient what-if frame on the map (via <see cref="SolveImport"/>
    /// -> <see cref="FastSchematic.SetState"/>) with live frames held off; verdict + binding constraint + violations
    /// populate the results panel. Ops without a solve (inertia/condition/post_event) show the panel only.
    /// </summary>
    private async void RunQuery(string op, object @params, string title)
    {
        if (_bridge is not GridBridgeClient b) return;
        _vm.SetBridgeStatus($"QUERY  *  {op} ...", BridgeWarnBrush);
        _vm.Log(_vm.Clock, $"Bridge query: {title} ...");
        QueryResult qr;
        try { qr = await b.Query(op, _bridgeQueryAt, @params); }   // no ConfigureAwait => continuation resumes on the UI thread
        catch (Exception ex)
        {
            if (ReferenceEquals(_bridge, b))
            {
                _vm.SetBridgeStatus($"QUERY FAILED  *  {op}: {ex.Message}", BridgeBadBrush);
                // The pill returns to LIVE on the next frame, so the failure is also kept in the alarm list.
                _vm.AddAlarm($"QUERY FAILED: {op}: {ex.Message}", BridgeBadBrush);
                _restoreLivePill = true;
            }
            _vm.Log(_vm.Clock, $"Query '{op}' failed: {ex.Message}");
            return;
        }
        if (!ReferenceEquals(_bridge, b)) return;   // switched away while the query was in flight

        bool hasOverlay = false, notConverged = false;
        if (qr.Solve is SolveSnapshot snap)
        {
            if (!snap.Result.Converged)
            {
                // Fail loud: a solve that did not converge is not an operating point, so it is never painted as the
                // what-if state. The verdict and violations still show in the results panel, flagged as not converged.
                notConverged = true;
            }
            else
            {
                // The what-if frame goes through the SAME path as every frame (lamp, read-outs, boundaries, tabs and the
                // map), so the dashboard and the map describe one state. It used to reach the map only, while the lamp
                // and the tabs kept describing the live frame. The lamp is then marked WHAT-IF.
                _whatIf = true;                      // freeze live frames while the solved what-if is on the map
                hasOverlay = RenderReplayFrame(ToWhatIfFrame(snap));   // hover/click now read the what-if state
                if (hasOverlay) _vm.Status = $"WHAT-IF  *  {_vm.Status.Trim()}";
                else _whatIf = false;                // the frame was refused (SOLVE FAULT shown); stay on the live stream
            }
        }
        _vm.ShowQueryResult(qr, title, hasOverlay, notConverged);
        // WHAT-IF only when a what-if state is actually on the map; otherwise the stream is still showing, so the pill
        // goes back to LIVE (now, or on the next frame) and the outcome lives in the results panel.
        if (hasOverlay) _vm.SetBridgeStatus($"WHAT-IF  *  {qr.Verdict ?? op}", BridgeWarnBrush);
        else if (_bridgeLive && !_paused) _vm.SetBridgeStatus($"LIVE  *  streaming {_bridgeCase}", BridgeOkBrush);
        else _restoreLivePill = true;
        _vm.Log(_vm.Clock, $"{op} -> {qr.Verdict ?? "(no verdict)"}" +
                            (string.IsNullOrWhiteSpace(qr.BindingConstraint) ? "" : $"  binding {qr.BindingConstraint}") +
                            $"  *  {qr.Violations.Count} violation(s)");
    }

    /// <summary>Wrap a solved snapshot as a <see cref="SimFrame"/> so map hover/selection reflect the what-if state.</summary>
    private SimFrame ToWhatIfFrame(SolveSnapshot snap)
    {
        double gen = snap.Result.PInjMw.Where(p => p > 0).Sum();
        double demand = snap.Model.Buses.Sum(b => b.Pd);
        double hour = _last?.Hour ?? 0.0;
        // N273 (October 2026, stream B2): a steady what-if snapshot sits at the case's own nominal (50.0 for a GB case).
        return new SimFrame(snap.Model, snap.Result, hour, _host.Tick, snap.Model.FrequencyHz, gen, demand, 0.0);
    }

    private void ReturnToLive_Click(object sender, RoutedEventArgs e) => ReturnToLive();

    /// <summary>Clear the what-if overlay and resume rendering the live bridge stream from its most recent frame.</summary>
    private void ReturnToLive()
    {
        _whatIf = false;
        _vm.ClearQueryResult();
        // "Return to live" means following the stream again, so it also releases a PAUSE hold (D-DOC-141).
        if (_bridge is not null && _paused) { SetBridgePaused(false); return; }
        if (_bridge is not null && _lastLive is SimFrame f)
        {
            RenderReplayFrame(f);
            _renderer.SetOverlay(_lastOverlays?.ConditionByBus, _lastOverlays?.StressByBranch);
            _vm.SetBridgeStatus($"LIVE  *  streaming {_bridgeCase}", BridgeOkBrush);
        }
    }

    private void TripAtBus(int busId)
    {
        if (_last is null) return;
        // Replay and bridge feeds have no local solver (the host has no live simulation): say so instead of silently doing nothing.
        if (!_host.HasLiveSolver)
        {
            _vm.Log(_vm.Clock, "TRIP is not available on this feed (replay or bridge stream)");
            return;
        }
        GridModel m = _last.Model;
        // The shared Core rule (also used by the web map): the largest unit generating NOW at this bus, by live
        // Pg, skipping power-flow-only devices (slack balancer / voltage control) even when the solve gave them
        // output. Before this used every unit with Pg > 0, so the slack balancer could be "tripped".
        (Generator Unit, int Index)? pick = InfeedSelection.LargestOnline(m, busId);
        Bus? bus = m.Buses.FirstOrDefault(b => b.Id == busId);
        if (pick is null)
        {
            _vm.Log(_vm.Clock, $"No generation to trip at {bus?.Name ?? $"bus {busId}"}");
            return;
        }
        // A trip injects the unit's output as a deficit into the live frequency integrator; it does not take the unit
        // out of the dispatch, so the same unit stays the largest infeed on later frames. Without this guard every
        // further T press or right-click "lost" the same machine again (three presses = three times its output from
        // one unit). The same unit is refused, with the reason, for the life of this run.
        (Generator gen, int genIndex) = pick.Value;
        if (!_trippedUnits.Add(TripKey(genIndex, gen)))
        {
            _vm.Log(_vm.Clock, $"TRIP refused: {gen.Name ?? $"the unit at bus {gen.Bus}"} has already been tripped in this run " +
                               "(a trip is a frequency what-if: the unit stays in the dispatch, so it cannot be lost twice)");
            return;
        }

        // Inject the loss of infeed into the live frequency integrator - the transient then plays out
        // at whatever time-step the sim is running (fine dt = watch the RoCoF/nadir in slow motion).
        _host.TripInfeed(gen.Pg);

        // Also drive the Generation tab's analytical trace + a headline RoCoF/nadir for the log.
        // UI programme Phase 1.6: the swing study half of the trip moved, statement for statement, to the platform-neutral
        // GridSim.Ui.Tabs.Generation.TripSwingStudy (src/GridSim.Ui/Tabs/Generation/TripSwingStudy.cs) so the Avalonia
        // trip runs the same study. In its order: the inputs (N325: the N289 rotating-mass total less the tripped unit's
        // own share; the case's demand; the unit's output), the study ONCE inside AnalyseTrip (which also runs the TSGB
        // variant and the sizer), the log line with the baseline RoCoF and nadir, then the TRIP alarm in
        // Palette.Loading(1.1). The header lamp is NOT forced to FREQUENCY EVENT here: the next frame's lamp rule (the
        // shared deviation and RoCoF tests) decides, so a 5 MW trip that stays in band does not light the lamp.
        GridSim.Ui.Tabs.Generation.TripSwingStudy.Apply(_vm, m, gen, busId);
    }

    // Units already tripped in the running local simulation (cleared when a model is loaded), keyed by index, bus and
    // name so a namesake at another index is a different unit.
    private readonly HashSet<string> _trippedUnits = new(StringComparer.Ordinal);
    private static string TripKey(int index, Generator g) => $"{index}:{g.Bus}:{g.Name}";

    private void ClearAlarms_Click(object sender, RoutedEventArgs e) => _vm.ClearAlarms();

    private void Color_Click(object sender, RoutedEventArgs e)
    {
        // Cycle: LOADING -> VOLTAGE -> CONDITION -> STRESS -> LOADING. The last two are the GDA-bridge
        // overlays (fragility-by-bus / stress-by-branch), meaningful in bridge mode where SetOverlay is fed.
        _renderer.ColorMode = _renderer.ColorMode switch
        {
            MapColourMode.Loading => MapColourMode.Voltage,
            MapColourMode.Voltage => MapColourMode.Condition,
            MapColourMode.Condition => MapColourMode.Stress,
            _ => MapColourMode.Loading,
        };
        ColorBtn.Content = ColorLabel(_renderer.ColorMode);
        _vm.SetColorLegend(_renderer.ColorMode);   // keep the legend swatch/labels in step with the tint
    }

    private static string ColorLabel(MapColourMode mode) => mode switch
    {
        MapColourMode.Voltage => " VOLTAGE",
        MapColourMode.Condition => " CONDITION",
        MapColourMode.Stress => " STRESS",
        _ => " LOADING",
    };

    private void Fleet_Click(object sender, RoutedEventArgs e)
    {
        _renderer.ShowFleet = !_renderer.ShowFleet;
        FleetBtn.Opacity = _renderer.ShowFleet ? 1.0 : 0.55;
    }

    private void Dno_Click(object sender, RoutedEventArgs e)
    {
        _renderer.ShowDnoAreas = !_renderer.ShowDnoAreas;
        DnoBtn.Opacity = _renderer.ShowDnoAreas ? 1.0 : 0.55;
    }

    private void GspRegion_Click(object sender, RoutedEventArgs e)
    {
        _renderer.ShowGspRegions = !_renderer.ShowGspRegions;
        GspRegionBtn.Opacity = _renderer.ShowGspRegions ? 1.0 : 0.55;
    }

    private void Assets_Click(object sender, RoutedEventArgs e)
    {
        _renderer.ShowAssets = !_renderer.ShowAssets;
        AssetsBtn.Opacity = _renderer.ShowAssets ? 1.0 : 0.55;
    }

    private void Search_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter || _renderer is null) return;
        int? id = _renderer.FindNode(SearchBox.Text);
        if (id is int b) { _renderer.FocusNode(b); OnNodeClicked(b); }
        else _vm.Log(_vm.Clock, $"No substation matching '{SearchBox.Text}'");
    }

    private void OnNodeClicked(int busId)
    {
        if (_last is null) return;
        GridModel m = _last.Model;
        PowerFlowResult r = _last.Result;
        Bus? bus = m.Buses.FirstOrDefault(b => b.Id == busId);
        if (bus is null) return;
        _selBusId = busId; _selBranch = null;

        double vm = 1.0;
        for (int i = 0; i < r.BusIds.Count; i++) if (r.BusIds[i] == busId) { vm = r.Vm[i]; break; }
        double gen = m.Generators.Where(g => g.Bus == busId).Sum(g => g.Pg);

        var rows = new List<KeyVal>
        {
            new("Voltage", $"{vm:F3} p.u."),
            new("Demand", $"{bus.Pd:F0} MW"),
            new("Generation", $"{gen:F0} MW"),
        };

        var lines = new List<string>();
        for (int i = 0; i < m.Branches.Count; i++)
        {
            Branch br = m.Branches[i];
            if (br.From != busId && br.To != busId) continue;
            string load = i < r.LineFlows.Count && br.RateAMva > 0 ? $"  {r.LineFlows[i].LoadingPercent:F0}%" : "";
            lines.Add($"{br.Name}{load}");
        }

        _vm.Select(bus.BaseKv >= 200 ? "EHV SUBSTATION" : "SUBSTATION",
            bus.Name ?? $"Bus {busId}", $"{bus.BaseKv:F0} kV  {bus.Type}",
            rows, $"CONNECTIONS ({lines.Count})", string.Join("\n", lines.Take(12)));
    }

    private void OnBranchClicked(int index)
    {
        if (_last is null || index < 0 || index >= _last.Model.Branches.Count) return;
        GridModel m = _last.Model;
        PowerFlowResult r = _last.Result;
        Branch br = m.Branches[index];
        _selBranch = br; _selBusId = null;
        Bus? a = m.Buses.FirstOrDefault(b => b.Id == br.From);
        Bus? c = m.Buses.FirstOrDefault(b => b.Id == br.To);

        string flow = " - ", load = " - ";
        if (index < r.LineFlows.Count)
        {
            LineFlow lf = r.LineFlows[index];
            flow = $"{Math.Max(Math.Abs(lf.PFromMw), Math.Abs(lf.PToMw)):F0} MW";
            load = br.RateAMva > 0 ? $"{lf.LoadingPercent:F0}%" : " - ";
        }

        var rows = new List<KeyVal>
        {
            new("Type", br.IsTransformer ? "Transformer" : "Circuit"),
            new("Rating", br.RateAMva > 0 ? $"{br.RateAMva:F0} MVA" : " - "),
            new("Flow", flow),
            new("Loading", load),
        };
        _vm.Select("CORRIDOR", br.Name ?? $"{br.From}-{br.To}",
            $"{a?.Name} -> {c?.Name}", rows, "", "");
    }

    private void OnKeyDown(object sender, KeyEventArgs e)
    {
        // The map shortcuts are window-wide, but a TextBox takes its characters through TextInput and does NOT
        // mark the letter KeyDown handled, so the key still bubbles up here. Without this guard, typing
        // "Peterhead" into the substation search tripped the largest infeed on the 't', and typing in the SCADA
        // filter refitted the map or toggled overlays. Keys aimed at a text box, password box or combo box (or
        // its drop-down) belong to that control. Ctrl/Alt chords (Ctrl+C copy, Alt menu keys) are not map
        // shortcuts either.
        if (IsTextEntryTarget(Keyboard.FocusedElement as DependencyObject)) return;
        if ((Keyboard.Modifiers & (ModifierKeys.Control | ModifierKeys.Alt)) != 0) return;

        if (e.Key == Key.F) _renderer.FitToView();
        else if (e.Key == Key.M) _renderer.ShowMinimap = !_renderer.ShowMinimap;
        else if (e.Key == Key.C) _renderer.ShowCoast = !_renderer.ShowCoast;
        else if (e.Key == Key.B) _renderer.ShowBoundaries = !_renderer.ShowBoundaries;
        else if (e.Key == Key.G) _renderer.ShowGspAreas = !_renderer.ShowGspAreas;
        else if (e.Key == Key.D && _renderer.HasGeography) Dno_Click(this, e);
        else if (e.Key == Key.R && _renderer.HasGeography) GspRegion_Click(this, e);
        else if (e.Key == Key.A && _renderer.HasGeography) Assets_Click(this, e);
        else if (e.Key == Key.T) TripLargestInfeed();
        else if (e.Key == Key.OemPeriod) CycleStep(-1);   // '.' / '>' - finer time-step
        else if (e.Key == Key.OemComma) CycleStep(+1);    // ',' / '<' - coarser time-step
        else if (e.Key == Key.Escape) _vm.ClearSelection();
    }

    /// <summary>
    /// True when keyboard focus sits in (or inside) a control that consumes typed characters: any text box
    /// (TextBoxBase covers TextBox and RichTextBox, including an editable ComboBox's inner text box), a
    /// PasswordBox, or a ComboBox / its drop-down item (letters drive the combo's type-ahead selection). Walks up
    /// the visual tree, falling back to the logical tree for non-visual elements, so a key from a template part
    /// is attributed to the control that owns it.
    /// </summary>
    private static bool IsTextEntryTarget(DependencyObject? focused)
    {
        for (DependencyObject? d = focused; d is not null;
             d = d is Visual or System.Windows.Media.Media3D.Visual3D ? VisualTreeHelper.GetParent(d) : LogicalTreeHelper.GetParent(d))
        {
            if (d is System.Windows.Controls.Primitives.TextBoxBase
                  or System.Windows.Controls.PasswordBox
                  or System.Windows.Controls.ComboBox
                  or System.Windows.Controls.ComboBoxItem)
                return true;
        }
        return false;
    }

    /// <summary>Nudge the STEP picker (finer = smaller dt); the SelectionChanged handler applies it.</summary>
    private void CycleStep(int delta)
    {
        if (StepPicker.Items.Count == 0) return;
        StepPicker.SelectedIndex = Math.Clamp(StepPicker.SelectedIndex + delta, 0, StepPicker.Items.Count - 1);
    }

    /// <summary>Trip the largest online generator - the canonical worst-case N-1 loss of infeed.</summary>
    private void TripLargestInfeed()
    {
        if (_last is null) return;
        // Same shared Core rule as TripAtBus, over the whole system (live Pg, no power-flow-only devices).
        Generator? g = InfeedSelection.LargestOnline(_last.Model)?.Unit;
        if (g is null) { _vm.Log(_vm.Clock, "No generation to trip"); return; }
        // TripAtBus refuses a unit already tripped in this run (it stays in the dispatch, so it is still the largest).
        // Say so plainly here rather than appear to trip it again.
        TripAtBus(g.Bus);
    }

    private void ClearSelection_Click(object sender, RoutedEventArgs e) => _vm.ClearSelection();

    /// <summary>Jump from the map's quick-peek fly-out into the ASSETS explorer, opening the
    /// selected substation or branch as a full detail page you can navigate onward from.</summary>
    private void OpenInExplorer_Click(object sender, RoutedEventArgs e)
    {
        if (Tabs is null) return;
        SelectTab(AssetsTabHeader);   // D-DOC-073: by name (the last tab is ABOUT, not ASSETS)
        if (_selBusId is int id)
        {
            _vm.Assets.OpenSubstation(id);
            // In bridge mode, back the substation page with REAL per-asset GDA data (task T-J):
            // resolve the bus through the crosswalk + op="asset" and drop envelope/BESS/tap/consumer
            // rows onto the page. Live/replay mode leaves the card hidden (no per-asset feed).
            if (_bridge is not null && _vm.Assets.Current is Views.Assets.SubstationDetailVm sub)
                BridgeApplyAsset(sub);
        }
        else if (_selBranch is Branch br) _vm.Assets.OpenBranch(br);
    }

    /// <summary>
    /// Fetch this bus's real per-asset data over the bridge (<c>op="asset"</c> at the historical stream anchor)
    /// and populate the ASSETS explorer's substation page  -  generator envelope MSG/Pmax, BESS SoC ratings,
    /// tap-changer wear and connected consumers  -  replacing the UI-first placeholders. The bus is resolved to
    /// the Derived datasets by the pre-baked <c>asset_crosswalk.json</c> on the GDA side. Graceful on empty/failure.
    /// </summary>
    private async void BridgeApplyAsset(Views.Assets.SubstationDetailVm page)
    {
        if (_bridge is not GridBridgeClient b) return;
        int busId = page.BusId;
        page.ApplyAsset(null, "querying GDA per-asset data ...");
        QueryResult qr;
        try { qr = await b.Query("asset", _bridgeQueryAt, new { busId }); }   // continuation on UI thread
        catch (Exception ex) { page.ApplyAsset(null, $"asset query failed: {ex.Message}"); return; }
        if (!ReferenceEquals(_bridge, b)) return;                             // switched away mid-flight
        // Only apply if the same substation page is still open (the user may have navigated on).
        if (_vm.Assets.Current is Views.Assets.SubstationDetailVm cur && cur.BusId == busId)
        {
            bool has = qr.Asset is { IsEmpty: false };
            cur.ApplyAsset(qr.Asset,
                has ? $"GDA @ {_bridgeQueryAt:yyyy-MM-dd HH:mm}Z  *  bus {busId}"
                    : $"no GDA per-asset data resolved for bus {busId}");
        }
    }

    /// <summary>Click-through from a SCADA register point to its entity in the ASSETS explorer,
    /// resolving the point's Kind/Index against the live model. UI programme Phase 1.6: the point and its kind are the
    /// shared GridSim.Ui.Tabs.Scada types the SCADA view-model now derives from (the WPF ScadaPoint is one).</summary>
    private void OnScadaNavigate(GridSim.Ui.Tabs.Scada.ScadaPointModel pt)
    {
        if (_last is null || Tabs is null) return;
        GridModel m = _last.Model;
        switch (pt.Kind)
        {
            case GridSim.Ui.Tabs.Scada.PointKind.BusVoltage:
                if ((uint)pt.Index < (uint)_last.Result.BusIds.Count)
                    _vm.Assets.OpenSubstation(_last.Result.BusIds[pt.Index]);
                break;
            case GridSim.Ui.Tabs.Scada.PointKind.BranchP:
            case GridSim.Ui.Tabs.Scada.PointKind.BranchQ:
            case GridSim.Ui.Tabs.Scada.PointKind.OltcTap:
                if ((uint)pt.Index < (uint)m.Branches.Count) _vm.Assets.OpenBranch(m.Branches[pt.Index]);
                break;
            default:
                return;   // SystemFreq has no entity to open
        }
        SelectTab(AssetsTabHeader);   // D-DOC-073: by name (the last tab is ABOUT, not ASSETS)
    }
}
