using CommunityToolkit.Mvvm.ComponentModel;
using GridSim.App.Dashboard;
using GridSim.App.Map;
using GridSim.App.Run;
using GridSim.App.Tabs;
using GridSim.Core.Licensing;
using GridSim.Ui.Assets.Bridge;
using GridSim.Ui.Assets.Index;
using GridSim.Ui.Commands;
using GridSim.Ui.Dashboard;
using GridSim.Ui.Models;
using GridSim.Ui.Settings;
using GridSim.Ui.Shell;

namespace GridSim.App;

/// <summary>
/// What the main window shows (UI programme Phase 1.1, the shell): its title, the build line, the status bar, the
/// command registry the menus and shortcuts are built from, the settings, and the case chosen so far.
/// <para>
/// Phase 1.2, the run pipeline: <see cref="LoadCaseAsync"/> records the choice (<see cref="ChooseCase"/>) and then
/// loads it through <see cref="Run"/>, which drives the shared simulation host. The status bar says in plain words
/// whether the chosen case was loaded, so nobody mistakes a chosen case for a running one.
/// </para>
/// </summary>
public sealed class MainWindowViewModel : ObservableObject
{
    /// <summary>The status line before any case is chosen.</summary>
    public const string NoCaseText = "No case loaded yet. Choose one with File > Open case...";

    private string _statusText = NoCaseText;
    private string? _problemText;
    private ModelSource? _chosenCase;

    /// <summary>
    /// A view model over <paramref name="settings"/> (read by the caller from a <see cref="ShellSettingsStore"/>), whose
    /// live cases are checked against <paramref name="licence"/> (the window passes the gate it resolved at start-up,
    /// as the WPF app resolves App.Licence once).
    /// <para>UI programme slice 1.8: with <paramref name="bridgeEnabled"/> (the default, every desktop) the run pipeline
    /// carries a GDA bridge session over these settings; without it a bridge source is refused (a mobile head).</para>
    /// </summary>
    public MainWindowViewModel(AppSettings settings, LicenceGate licence, bool bridgeEnabled = true)
    {
        Settings = settings ?? throw new ArgumentNullException(nameof(settings));
        ArgumentNullException.ThrowIfNull(licence);
        // UI programme integration (2026-10-20): ONE dashboard, fed ONE way. The dashboard model owns the tabs' feed
        // (its four hooks run TabModels, in the WPF DashboardViewModel order); the run pipeline trips on it (the swing
        // study, its log line and its alarm); the map writes its fly-out, legend and hint into it; and the dashboard
        // host is the only thing that feeds it, from the run pipeline's feed events (RunFeedEvents.cs), making the same
        // calls in the same order as the WPF window makes on its DashboardViewModel. The host is attached here, before
        // the window adds its map handlers and before any case can be opened, so the dashboard and every tab see every
        // load and every frame, each exactly once.
        Dashboard = new AppDashboardModel(Tabs);
        Run = new RunViewModel(licence, Dashboard, bridgeEnabled ? settings : null);
        // The WPF title: its XAML title, then the run pipeline's tier line ("v<version>  -  <tier> licence").
        Title = $"{TitleStem}  -  {Run.LicenceTierText}";
        DashboardHost = new DashboardHost(Dashboard);
        DashboardHost.Attach(Run);   // also writes the WPF start-up line ("Licence: ...") to the event log
        Map = new MapViewModel(Run, Dashboard);
        // UI programme ASSETS B2: every model the run pipeline loads (a case, a run, the bridge's topology) declares its
        // source to the ASSETS tab before its first frame, with the gate the load passed: a case file is then opened
        // read-only for PROPERTIES and the where-used list, any other source says why there are no case records.
        // UI programme ASSETS slice 5-view: with the asset overlay a v2 case carried (null for every other source), so the
        // tab shows it read-only (carried, not used by the solver). The loader refuses a case whose overlay fails its
        // checker, so there are no checker problems to hand on in Run mode.
        Run.ModelLoaded += (_, _) => Tabs.Assets.SetSource(AssetCaseSource.From(Run.CurrentSource), licence, Run.LoadedOverlay);
        // UI programme ASSETS fix S6: in bridge mode the substation page's GDA per-asset card is asked for on EVERY route
        // to a substation page (tree, search, back / forward, SCADA row, Open in Assets, OPEN IN EXPLORER, cross-links),
        // through the one AssetInsightSource; outside bridge mode (and on a head without the bridge) it stays hidden.
        AssetInsights = new AssetInsightSource(Tabs.Assets, () => Run.Bridge);
        // A replay that played its last recorded frame says so in the status bar (it stops by design, as in WPF).
        Run.ReplayFinished += (_, text) => StatusText = text;
        // A settings file that could not be read is reported, not silently replaced, as in WPF: the defaults apply for
        // this session and the file is left untouched on exit.
        if (settings.LoadProblem is { } problem)
            _problemText = $"Settings ignored and not overwritten: {problem}. Fix the file and restart.";
    }

    /// <summary>The start of the WPF window's title (its XAML Title), before the version and the licence tier.</summary>
    public const string TitleStem = "GridSim  -  GB Transmission Control";

    /// <summary>
    /// The window title, exactly as the WPF window writes it once it has loaded (MainWindow.OnLoaded:
    /// <c>Title = $"{Title}  -  v{BuildInfo.Version}  -  {tier} licence"</c> over the XAML title
    /// <see cref="TitleStem"/>): for example "GridSim  -  GB Transmission Control  -  v2.0.38  -  Personal licence".
    /// Slice 1.7: the tier line used to sit in the header instead; WPF has it only here.
    /// </summary>
    public string Title { get; }

    /// <summary>"GridSim &lt;version&gt; (commit &lt;sha&gt;)", read from the assembly at run time by Core.</summary>
    public string VersionText { get; } = GridSim.Core.BuildInfo.DisplayLine;

    /// <summary>The run pipeline: the loaded case, its play loop and the live panel's text.</summary>
    public RunViewModel Run { get; }

    /// <summary>
    /// The tab view models (Phase 1.6; every ported tab since the integration of 2026-10-20), fed only through the
    /// dashboard's tab hooks. Declared before <see cref="Dashboard"/>, which is built over it.
    /// </summary>
    public TabModels Tabs { get; } = new();

    /// <summary>
    /// The window's one dashboard model (the shared GridSim.Ui model, as the WPF dashboard holds it): the NETWORK
    /// tab's tiles, the window's lamp, clock and banner, the map's fly-out, legend and hint, and the GENERATION &amp;
    /// INERTIA tab all show it, and its tab hooks feed <see cref="Tabs"/> at the points the WPF dashboard feeds its
    /// tabs. Fed only by <see cref="DashboardHost"/>.
    /// </summary>
    public AppDashboardModel Dashboard { get; }

    /// <summary>The host that feeds <see cref="Dashboard"/> from <see cref="Run"/> (attached for the window's life).</summary>
    public DashboardHost DashboardHost { get; }

    /// <summary>The map's selection, hover tip and legend (Phase 1.5).</summary>
    public MapViewModel Map { get; }

    /// <summary>
    /// UI programme ASSETS fix S6: the GDA per-asset data of the substation page on the ASSETS tab, asked of the bridge
    /// on every route to a substation page while the bridge streams (hidden outside bridge mode, as before).
    /// </summary>
    public AssetInsightSource AssetInsights { get; }

    /// <summary>Every command the shell offers; the menu bar, the shortcuts and the context menus come from it.</summary>
    public CommandRegistry Registry { get; } = new();

    private CommandModes _mode = CommandModes.Run;

    /// <summary>
    /// The session mode the menus, the shortcuts and the context menus are built for: Run, or (UI programme Phase 4.1)
    /// Author while the ASSETS tab edits the case. Only <see cref="CommandModes.Run"/> or <see cref="CommandModes.Author"/>;
    /// the window rebuilds its menus and shortcuts when it changes (<see cref="ModeChanged"/>).
    /// </summary>
    public CommandModes Mode
    {
        get => _mode;
        set
        {
            if (value is not (CommandModes.Run or CommandModes.Author))
                throw new ArgumentException($"The session mode is Run or Author, not {value}.", nameof(value));
            if (value == _mode) return;
            _mode = value;
            OnPropertyChanged(nameof(Mode));
            ModeChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    /// <summary>Raised when <see cref="Mode"/> changes (the window rebuilds its menu bar and shortcuts).</summary>
    public event EventHandler? ModeChanged;

    /// <summary>
    /// UI programme Phase 4.1: asked before any case is loaded while the ASSETS tab is in Author mode (File > Open case,
    /// the NETWORK picker): it must leave Author mode first (Save / Discard / Cancel for unsaved changes). Returns false
    /// to cancel the load (the user chose Cancel, or a save was refused). Null (no Author mode wired) allows every load.
    /// </summary>
    public Func<Task<bool>>? LeaveAuthorBeforeLoad { get; set; }

    /// <summary>The settings this session read, and writes back on exit when something changed.</summary>
    public AppSettings Settings { get; }

    /// <summary>True once anything in <see cref="Settings"/> changed this session, so exit knows to save.</summary>
    public bool SettingsChanged { get; private set; }

    /// <summary>Record that something in <see cref="Settings"/> was changed this session (a map layer, the colour mode).</summary>
    public void NoteSettingsChanged() => SettingsChanged = true;

    /// <summary>Run folders the user opened this session; the Open case list offers them alongside the catalogue.</summary>
    public List<string> OpenedRunFolders { get; } = new();

    /// <summary>Shell slice (v2 + pickers, 2026-10-20): the cases the user opened this session by hand (File > Open case
    /// folder..., Open v2 case...); the Open case list offers them alongside the catalogue, as it does the run folders.</summary>
    public List<ModelSource> OpenedCases { get; } = new();

    /// <summary>Shell slice (v2 + pickers, 2026-10-20): the folders those cases came from (full paths, each once); the Open
    /// case list also offers the JSON v2 case manifests found in them.</summary>
    public List<string> OpenedFolders { get; } = new();

    /// <summary>The status bar's message.</summary>
    public string StatusText
    {
        get => _statusText;
        set => SetProperty(ref _statusText, value);
    }

    /// <summary>A problem to show in red in the status bar (an unreadable settings file, a failed command), or null.</summary>
    public string? ProblemText
    {
        get => _problemText;
        set
        {
            if (SetProperty(ref _problemText, value)) OnPropertyChanged(nameof(HasProblem));
        }
    }

    /// <summary>True while <see cref="ProblemText"/> holds a message.</summary>
    public bool HasProblem => _problemText is not null;

    /// <summary>The case chosen with File > Open case... or Open run folder..., or null before one is chosen.</summary>
    public ModelSource? ChosenCase
    {
        get => _chosenCase;
        private set => SetProperty(ref _chosenCase, value);
    }

    /// <summary>
    /// Record <paramref name="source"/> as the chosen case, without loading it: it is shown in the status bar and
    /// remembered in the settings as the last-used source, by label and by path, as WPF does.
    /// <see cref="LoadCaseAsync"/> calls this and then loads it. Slice 1.7: the header's NETWORK picker is moved onto
    /// the chosen source (<see cref="SelectedSource"/>) without loading it a second time.
    /// </summary>
    public void ChooseCase(ModelSource source)
    {
        ArgumentNullException.ThrowIfNull(source);
        ChosenCase = source;
        CaseChoice.Remember(Settings, source);
        SettingsChanged = true;
        StatusText = $"Chosen: {source.Label}. Not loaded yet.";
        ShowInPicker(source);
    }

    // ======================================== the NETWORK picker (slice 1.7) ========================================

    private IReadOnlyList<ModelSource> _sources = Array.Empty<ModelSource>();
    private ModelSource? _selectedSource;
    private bool _syncingPicker;   // true while the picker is moved by code: a choice made by code loads nothing

    /// <summary>
    /// The header's NETWORK picker list (the WPF SourcePicker's ItemsSource): the model catalogue the window found at
    /// start-up (<see cref="SetSources"/>), plus any case or run folder loaded since that was not in it (a run folder
    /// opened with LOAD RUN... joins the list once, as in WPF).
    /// </summary>
    public IReadOnlyList<ModelSource> Sources
    {
        get => _sources;
        private set => SetProperty(ref _sources, value);
    }

    /// <summary>
    /// The NETWORK picker's selection (the WPF SourcePicker.SelectedItem). Choosing a source here loads it, as the WPF
    /// picker's SelectionChanged runs SwitchTo: through <see cref="LoadCaseAsync"/>, the same path as File > Open
    /// case.... Choosing the source already chosen does nothing (WPF: reselecting fires nothing), and the picker moving
    /// because a case was loaded some other way (Open case..., LOAD RUN...) loads nothing either. A load that throws is
    /// shown in the run's error area, never lost. Null (nothing chosen yet, or the list just replaced) loads nothing.
    /// </summary>
    public ModelSource? SelectedSource
    {
        get => _selectedSource;
        set
        {
            if (!SetProperty(ref _selectedSource, value)) return;
            if (value is null || _syncingPicker) return;
            if (ChosenCase is { } chosen && chosen.SameSourceAs(value)) return;
            PendingSourceLoad = LoadFromPickerAsync(value);
        }
    }

    /// <summary>The load the NETWORK picker started last (null before one), so a test can wait for it.</summary>
    public Task? PendingSourceLoad { get; private set; }

    /// <summary>
    /// Fill the NETWORK picker with <paramref name="sources"/> (the window passes the model catalogue it found at
    /// start-up, as WPF fills its picker in OnLoaded). Nothing is selected and nothing is loaded here. UI programme slice
    /// 1.8: as in WPF, the window then chooses a source itself when it opens (MainWindow's start-up plan, unless
    /// ShellOptions.AutoLoadAtStart is off), by setting <see cref="SelectedSource"/>, which loads it.
    /// </summary>
    public void SetSources(IReadOnlyList<ModelSource> sources)
    {
        ArgumentNullException.ThrowIfNull(sources);
        Sources = sources.ToList();
        if (ChosenCase is { } chosen) ShowInPicker(chosen);
    }

    // Move the picker onto the chosen source without loading it: the matching entry of the list, or the source added
    // to the list first (with its parent folder in the label if its name repeats another's, as WPF does for a run).
    private void ShowInPicker(ModelSource chosen)
    {
        _syncingPicker = true;
        try
        {
            ModelSource? entry = _sources.FirstOrDefault(s => s.SameSourceAs(chosen));
            if (entry is null)
            {
                List<ModelSource> grown = ModelCatalog.Disambiguate(new List<ModelSource>(_sources) { chosen });
                Sources = grown;
                entry = grown.First(s => s.SameSourceAs(chosen));
            }
            SelectedSource = entry;
        }
        finally { _syncingPicker = false; }
    }

    // The picker's load: a setter cannot await, so the load's own failure is shown in the run's error area.
    private async Task LoadFromPickerAsync(ModelSource source)
    {
        try
        {
            await LoadCaseAsync(source).ConfigureAwait(true);
        }
#pragma warning disable CA1031 // Not swallowed: shown in the error area.
        catch (Exception ex)
#pragma warning restore CA1031
        {
            Run.LoadError = ex.Message;
        }
    }

    /// <summary>
    /// Choose <paramref name="source"/> (<see cref="ChooseCase"/>) and load it through <see cref="Run"/>. On success
    /// the status bar carries the WPF log line for the load; on a refusal it says the case was not loaded, and the
    /// reason (the host's or the licence gate's own words) is in the run's error area in the alarm colour.
    /// </summary>
    public async Task LoadCaseAsync(ModelSource source)
    {
        // UI programme Phase 4.1: a case being edited is never dropped by a load without the user's say (Save / Discard /
        // Cancel). Cancel (or a refused save) keeps the session in Author mode and loads nothing.
        if (_mode == CommandModes.Author && LeaveAuthorBeforeLoad is { } leave && !await leave().ConfigureAwait(true))
        {
            StatusText = $"Not loaded: {source.Label}. The case being edited in AUTHOR was kept (Cancel, or its save was refused).";
            if (Run.CurrentSource is { } current) ShowInPicker(current);   // the picker goes back to the case still loaded
            return;
        }
        ChooseCase(source);
        string? refusal = await Run.LoadAsync(source).ConfigureAwait(true);
        StatusText = refusal is null
            ? Run.LoadedText
            : $"Chosen: {source.Label}. Not loaded yet: it was refused, and the reason is shown in red.";
    }

    /// <summary>Show that <paramref name="command"/> failed, with the reason, in the status bar.</summary>
    public void ReportFailure(AppCommand command, Exception error)
    {
        ArgumentNullException.ThrowIfNull(command);
        ArgumentNullException.ThrowIfNull(error);
        ProblemText = $"{command.Title} failed: {error.Message}";
    }
}
