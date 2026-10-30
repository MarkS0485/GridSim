using GridSim.Core.Licensing;
using GridSim.Ui.Geography;
using GridSim.Ui.Models;
using GridSim.Ui.Settings;
using GridSim.Ui.Shell;

namespace GridSim.App.Shell;

/// <summary>
/// Everything the main window takes from outside (UI programme Phase 1.1). The defaults are the real app's: the
/// shared settings file, the model catalogue, the licence gate and the shipped documents beside the app. A test
/// replaces the parts it must not touch (the user's settings file, the real model folders) with its own.
/// </summary>
public sealed class ShellOptions
{
    /// <summary>Where the settings are read and written (default: the file the WPF window uses).</summary>
    public ShellSettingsStore SettingsStore { get; init; } = ShellSettingsStore.Default;

    /// <summary>
    /// Finds the cases File > Open case... offers (default: <see cref="ModelCatalog.Discover(AppSettings?, ICollection{string}?, CatalogOptions)"/>
    /// with <see cref="CatalogOptions.CrossPlatform"/>); unreadable folders it skipped go in the list it is given, and the
    /// picker shows them.
    /// <para>Shell slice (v2 + pickers, 2026-10-20): the default is the WPF catalogue plus the JSON v2 case manifests
    /// under the same folders (this app loads v2 cases; the WPF window does not, and its own catalogue call is unchanged:
    /// the v2 option is off unless asked for).</para>
    /// </summary>
    public Func<AppSettings, ICollection<string>, List<ModelSource>> DiscoverCases { get; init; }
        = (settings, skipped) => ModelCatalog.Discover(settings, skipped, CatalogOptions.CrossPlatform);

    /// <summary>
    /// The licence gate live cases are checked against (default: resolved the standard way, as the WPF app resolves
    /// App.Licence). The window calls it once, when it is built. A test passes a gate with a known cap.
    /// </summary>
    public Func<LicenceGate> Licence { get; init; } = () => LicenceGate.Resolve();

    /// <summary>The licence description the About box shows (default: the licence gate resolved the standard way).</summary>
    public Func<string> LicenceDescription { get; init; } = () => LicenceGate.Resolve().Describe();

    /// <summary>The folder the shipped LICENSE.md and THIRD-PARTY-NOTICES.md are read from (null: the app's folder).</summary>
    public string? DocumentFolder { get; init; }

    /// <summary>
    /// The shell actions the commands run (default: the real dialogs, <see cref="ShellActions"/>). A test passes its
    /// own to check that a menu item or a shortcut reached the right action.
    /// </summary>
    public Func<MainWindow, IShellActions>? Actions { get; init; }

    /// <summary>
    /// Whether to put the menu bar in the macOS system menu instead of the window (default: on macOS only).
    /// </summary>
    public bool UseNativeMenu { get; init; } = OperatingSystem.IsMacOS();

    /// <summary>
    /// Loads the map's DNO/GSP geography and geocoded assets (default: <see cref="GeographyData.Load"/> from the app's
    /// folder, as the WPF window does), or returns null when none is present locally. A layer that could not be read
    /// is added to the list it is given ("file: reason"), and the window reports it.
    /// </summary>
    public Func<ICollection<string>, GeographyData?> LoadGeography { get; init; }
        = problems => GeographyData.Load(AppContext.BaseDirectory, problems);

    // ---- UI programme slice 1.8 ----

    /// <summary>
    /// Whether the GDA bridge is registered (default: on every desktop, off on Android and iOS). The bridge is desktop
    /// only (UI programme plan, Phase 3: "bridge (GridBridgeClient) is disabled on mobile"): without it, the run pipeline
    /// has no bridge session and refuses a bridge source in plain words
    /// (<see cref="GridSim.App.Run.RunViewModel.BridgeNotAvailableText"/>).
    /// </summary>
    public bool BridgeEnabled { get; init; } = !OperatingSystem.IsAndroid() && !OperatingSystem.IsIOS();

    /// <summary>
    /// Whether the window loads a source by itself when it opens, as the WPF window does in OnLoaded (default: on). The
    /// launch arguments are read and matched (a refusal is reported through <see cref="LaunchRefused"/>); then the model
    /// they name, else the last-used source from the settings (by path, then by label), else the first catalogue entry,
    /// is chosen in the NETWORK picker and loaded. Off, the window opens empty and reads no launch arguments (the app
    /// tests written for an empty window turn it off).
    /// </summary>
    public bool AutoLoadAtStart { get; init; } = true;

    /// <summary>
    /// The launch arguments, without the program path (default: this process's command line, as the WPF window reads
    /// <c>Environment.GetCommandLineArgs()</c> less its first element). Read only when <see cref="AutoLoadAtStart"/> is on.
    /// </summary>
    public Func<IReadOnlyList<string>> LaunchArguments { get; init; } = () => Environment.GetCommandLineArgs().Skip(1).ToArray();

    /// <summary>
    /// What happens when the launch arguments are refused (default: the refusal is shown in a message window and the app
    /// then closes with exit code 2, a usage error, as the WPF window and the CLI do). A test passes its own to record
    /// the text. The window has already stopped its play loop and loaded nothing when this runs.
    /// </summary>
    public Action<MainWindow, string>? LaunchRefused { get; init; }

    // ---- UI programme Phase 4.1: Author mode ----

    /// <summary>
    /// The questions Author mode asks (Save / Discard / Cancel, a Save As folder, a new key, a new record's fields, a
    /// yes / no), default: modal dialogs over the window (<see cref="GridSim.App.Assets.AuthorDialogs"/>). A test passes
    /// its own answers.
    /// </summary>
    public Func<MainWindow, GridSim.App.Assets.IAuthorPrompts>? AuthorPrompts { get; init; }

    /// <summary>The shipped data roots under which a case is bundled, so Save As only (default null: the real ones). A
    /// test passes a temporary root so it never writes near the real data folder.</summary>
    public IReadOnlyList<string>? AuthorBundledRoots { get; init; }

    /// <summary>The folder Save As suggests copies in (default null: the per-user application data folder). A test passes
    /// a temporary folder.</summary>
    public string? AuthorUserCasesFolder { get; init; }

    // ---- Shell slice (v2 + pickers, 2026-10-20) ----

    /// <summary>
    /// The OS file and folder pickers the File menu uses (Open case folder..., Open v2 case..., Open run folder...),
    /// default: the window's own (<see cref="StoragePickers"/>, Avalonia's IStorageProvider). A test passes its own
    /// answers, so no real dialog is opened.
    /// </summary>
    public Func<MainWindow, IShellPickers>? Pickers { get; init; }
}
