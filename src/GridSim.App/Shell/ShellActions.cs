using Avalonia.Controls;
using GridSim.App.Dialogs;
using GridSim.Core.Simulation;
using GridSim.Ui.Models;
using GridSim.Ui.Shell;

namespace GridSim.App.Shell;

/// <summary>
/// The real shell actions (UI programme Phase 1.1): what the File and Help commands do in the Avalonia app. Each
/// follows the WPF window's behaviour where it has one (the model picker's last-used choice, the run-folder check);
/// every refusal is reported with the reason, never silently ignored. Phase 1.2: a chosen case or run folder is
/// loaded through the run pipeline (<see cref="MainWindowViewModel.LoadCaseAsync"/>), and a refusal to load it is
/// shown in the run's error area with the WPF text.
/// </summary>
public sealed class ShellActions : IShellActions
{
    private readonly MainWindow _window;
    private readonly MainWindowViewModel _vm;
    private readonly ShellOptions _options;
    private readonly Lazy<string> _licence;

    /// <summary>Actions for <paramref name="window"/>, using <paramref name="options"/> for everything external.</summary>
    public ShellActions(MainWindow window, MainWindowViewModel vm, ShellOptions options)
    {
        _window = window ?? throw new ArgumentNullException(nameof(window));
        _vm = vm ?? throw new ArgumentNullException(nameof(vm));
        _options = options ?? throw new ArgumentNullException(nameof(options));
        // Resolved once, when the About box first needs it (the licence gate reads an environment variable and a
        // file; nothing else in the shell needs it yet).
        _licence = new Lazy<string>(options.LicenceDescription);
        // Shell slice (v2 + pickers, 2026-10-20): the OS pickers are asked through IShellPickers (a test answers them).
        _pickers = new Lazy<IShellPickers>(() => options.Pickers?.Invoke(window) ?? new StoragePickers(window));
    }

    private readonly Lazy<IShellPickers> _pickers;

    /// <summary>The OS pickers this shell asks (the window's own, or a test's from <see cref="ShellOptions.Pickers"/>).</summary>
    public IShellPickers Pickers => _pickers.Value;

    /// <inheritdoc />
    public async Task OpenCaseAsync()
    {
        (List<ModelSource> sources, List<string> skipped) = CaseList();

        int start = _vm.ChosenCase is { } current ? sources.FindIndex(s => s.SameSourceAs(current)) : -1;
        if (start < 0) start = CaseChoice.LastUsedIndex(sources, _vm.Settings);

        var picker = new OpenCaseWindow(sources, start, skipped);
        ModelSource? chosen = await picker.ShowDialog<ModelSource?>(_window).ConfigureAwait(true);
        if (chosen is not null) await _vm.LoadCaseAsync(chosen).ConfigureAwait(true);
    }

    /// <summary>
    /// The list File > Open case... offers, and the folders the scan skipped: the catalogue
    /// (<see cref="ShellOptions.DiscoverCases"/>), then the run folders opened this session (as the WPF picker keeps
    /// them). Shell slice (v2 + pickers, 2026-10-20): then the cases opened this session with Open case folder... and
    /// Open v2 case..., and the JSON v2 case manifests in every folder the user opened (a case folder, a manifest's
    /// folder, a run folder), found fail-soft as the catalogue finds them (an unreadable folder or a manifest that fails
    /// the cheap check is listed among the skipped). Each source is listed once, by path.
    /// </summary>
    public (List<ModelSource> Sources, List<string> Skipped) CaseList()
    {
        var skipped = new List<string>();
        List<ModelSource> sources = _options.DiscoverCases(_vm.Settings, skipped);
        // Run folders opened this session stay on offer, as they do in the WPF picker.
        foreach (string run in _vm.OpenedRunFolders)
            sources = CaseChoice.WithRunFolder(sources, run).Sources;
        sources = WithSessionCases(sources, skipped);
        return (sources, skipped);
    }

    /// <inheritdoc />
    public async Task OpenRunFolderAsync()
    {
        // The OS folder picker (Shell slice, 2026-10-20: asked through IShellPickers, the same IStorageProvider dialog as
        // before, so a test can answer it). Cancelled: nothing happens. A refused pick says why in the status bar.
        PickResult pick = await Pickers.PickFolderAsync("Choose a GridSim run directory (with an exports/ folder)", null).ConfigureAwait(true);
        if (pick.Problem is { } problem)
        {
            _vm.ProblemText = problem;
            return;
        }
        if (pick.Path is null) return;   // cancelled
        await OpenRunFolderAtAsync(pick.Path).ConfigureAwait(true);
    }

    /// <summary>The WPF run-directory check's refusal, word for word (MainWindow.LoadRun_Click).</summary>
    public const string NotARunDirectoryText = "Not a run directory (no exports/slice-*.json or state-*.json).";

    /// <summary>
    /// Open <paramref name="dir"/> as a pre-solved run, as the WPF LOAD RUN... button does once its folder dialog has
    /// returned: the same run-directory check and message, then the run is added to the case list (once) and loaded
    /// through the run pipeline. The folder picker itself is the only part a headless test cannot drive, so the rest is
    /// here, callable on its own.
    /// </summary>
    public async Task OpenRunFolderAtAsync(string dir)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(dir);
        // The same check and message as the WPF LOAD RUN... button; shown in the run's error area in the alarm colour
        // (WPF writes it to its event log), and nothing is loaded.
        if (!PresolvedReplay.IsRunDirectory(dir))
        {
            _vm.Run.LoadError = NotARunDirectoryText;
            return;
        }
        var skipped = new List<string>();
        (List<ModelSource> _, ModelSource selected) =
            CaseChoice.WithRunFolder(_options.DiscoverCases(_vm.Settings, skipped), dir);
        if (!_vm.OpenedRunFolders.Contains(dir, StringComparer.OrdinalIgnoreCase)) _vm.OpenedRunFolders.Add(dir);
        _vm.ProblemText = null;
        await _vm.LoadCaseAsync(selected).ConfigureAwait(true);
    }

    // ======================================== Shell slice (v2 + pickers, 2026-10-20) ========================================

    /// <summary>The refusal when a folder opened as a v1 case has no system.json.</summary>
    public static string NotACaseFolderText(string dir) =>
        $"Not a case folder: {dir} has no system.json (a JSON v1 case folder). For a JSON v2 case use File > Open v2 case....";

    /// <summary>The refusal when a file opened as a v2 case is not one (the catalogue's own check and reason).</summary>
    public static string NotAV2CaseText(string file, string reason) => $"Not a JSON v2 case manifest: {file}: {reason}.";

    /// <summary>
    /// File > Open case folder...: the OS folder picker for a JSON v1 case folder (one holding system.json), then
    /// <see cref="OpenCaseFolderAtAsync"/>. Cancelled: nothing happens. A refused pick says why in the status bar.
    /// </summary>
    public async Task OpenCaseFolderAsync()
    {
        PickResult pick = await Pickers.PickFolderAsync("Choose a GridSim case folder (with a system.json)", LastOpenedFolder()).ConfigureAwait(true);
        if (pick.Problem is { } problem)
        {
            _vm.ProblemText = problem;
            return;
        }
        if (pick.Path is null) return;   // cancelled
        await OpenCaseFolderAtAsync(pick.Path).ConfigureAwait(true);
    }

    /// <summary>
    /// Open <paramref name="dir"/> as a JSON v1 case folder, as File > Open case folder... does once its picker has
    /// returned: a folder without system.json is refused in the run's error area (in red; nothing is loaded and the list
    /// is unchanged); otherwise the case joins this session's case list (once: a folder already in the list is that
    /// entry) and is loaded through the run pipeline, the same path as File > Open case.... The picker itself is the only
    /// part a headless test cannot drive, so the rest is here, callable on its own.
    /// </summary>
    public async Task OpenCaseFolderAtAsync(string dir)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(dir);
        if (!File.Exists(Path.Combine(dir, "system.json")))   // File.Exists never throws
        {
            _vm.Run.LoadError = NotACaseFolderText(dir);
            return;
        }
        await OpenSessionCaseAsync(ModelCatalog.CaseFolderSource(dir), dir).ConfigureAwait(true);
    }

    /// <summary>
    /// File > Open v2 case...: the OS file picker for a JSON v2 case manifest (<c>*.case.json</c>), then
    /// <see cref="OpenV2CaseAtAsync"/>. Cancelled: nothing happens. A refused pick says why in the status bar.
    /// </summary>
    public async Task OpenV2CaseAsync()
    {
        PickResult pick = await Pickers.PickFileAsync("Choose a GridSim JSON v2 case manifest (*.case.json)", LastOpenedFolder(),
            "GridSim v2 case manifest", new[] { ModelCatalog.V2ManifestPattern }).ConfigureAwait(true);
        if (pick.Problem is { } problem)
        {
            _vm.ProblemText = problem;
            return;
        }
        if (pick.Path is null) return;   // cancelled
        await OpenV2CaseAtAsync(pick.Path).ConfigureAwait(true);
    }

    /// <summary>
    /// Open <paramref name="manifest"/> as a JSON v2 case, as File > Open v2 case... does once its picker has returned:
    /// the catalogue's cheap check first (<see cref="ModelCatalog.ProbeV2Manifest"/>: JSON whose schema is
    /// gridsim-case/2); a file that fails it is refused in the run's error area with the reason (nothing loaded, the list
    /// unchanged); otherwise the case joins this session's case list (once) labelled "v2  &lt;file name&gt;" and is loaded
    /// through the run pipeline's v2 path (the full validation is the loader's, and its refusal is shown as for any case).
    /// </summary>
    public async Task OpenV2CaseAtAsync(string manifest)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(manifest);
        if (ModelCatalog.ProbeV2Manifest(manifest) is { } reason)
        {
            _vm.Run.LoadError = NotAV2CaseText(manifest, reason);
            return;
        }
        ModelSource source = ModelCatalog.V2CaseSource(manifest);
        await OpenSessionCaseAsync(source, source.Dir).ConfigureAwait(true);
    }

    // A case the user opened by hand: it and its folder are remembered for this session (the Open case list offers them,
    // and the folder is searched for v2 manifests), the entry already in the NETWORK picker is used if there is one (so
    // the picker does not list it twice), and it is loaded through the one load path.
    private async Task OpenSessionCaseAsync(ModelSource source, string folder)
    {
        if (!_vm.OpenedCases.Any(s => s.SameSourceAs(source))) _vm.OpenedCases.Add(source);
        string full = Path.TrimEndingDirectorySeparator(Path.GetFullPath(folder));
        if (!_vm.OpenedFolders.Contains(full, StringComparer.OrdinalIgnoreCase)) _vm.OpenedFolders.Add(full);
        ModelSource selected = _vm.Sources.FirstOrDefault(s => s.SameSourceAs(source)) ?? source;
        _vm.ProblemText = null;
        await _vm.LoadCaseAsync(selected).ConfigureAwait(true);
    }

    // The catalogue list plus this session's hand-opened cases and the v2 manifests in the folders the user opened (each
    // once, by path; a repeated label gets its folder, as for a run folder). With nothing opened, the list is unchanged.
    private List<ModelSource> WithSessionCases(List<ModelSource> sources, ICollection<string> skipped)
    {
        var extra = new List<ModelSource>(_vm.OpenedCases);
        var folders = new List<string>(_vm.OpenedFolders);
        folders.AddRange(_vm.OpenedRunFolders);
        extra.AddRange(ModelCatalog.V2CasesIn(folders, skipped));
        var list = new List<ModelSource>(sources);
        foreach (ModelSource s in extra)
            if (!list.Any(x => x.SameSourceAs(s))) list.Add(s);
        return list.Count == sources.Count ? sources : ModelCatalog.Disambiguate(list);
    }

    // The picker opens where the user last opened a case this session (null: the platform's choice).
    private string? LastOpenedFolder() => _vm.OpenedFolders.Count > 0 ? _vm.OpenedFolders[^1] : null;

    /// <inheritdoc />
    public Task ExitAsync()
    {
        // Closing runs the window's Closing handler, which saves the settings, exactly as the close button does.
        _window.Close();
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public async Task ShowAboutAsync()
    {
        var about = new AboutWindow(AboutInfo.Lines(_licence.Value),
            showLicence: () => ShowDocument(ShippedDocument.Licence),
            showNotices: () => ShowDocument(ShippedDocument.ThirdPartyNotices));
        await about.ShowDialog(_window).ConfigureAwait(true);
    }

    /// <inheritdoc />
    public Task ShowThirdPartyNoticesAsync()
    {
        ShowDocument(ShippedDocument.ThirdPartyNotices);
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task ShowLicenceAsync()
    {
        ShowDocument(ShippedDocument.Licence);
        return Task.CompletedTask;
    }

    // The viewers are not modal (as the WPF audit-log viewer is not): the user can read the licence beside the app.
    // They are owned by the main window, so they close with it.
    private void ShowDocument(ShippedDocument document)
    {
        var viewer = new DocumentWindow(document.Read(_options.DocumentFolder));
        viewer.Show(_window);
    }
}
