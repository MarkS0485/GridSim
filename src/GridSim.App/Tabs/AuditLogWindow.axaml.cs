// COMMAND (UI programme Phase 1.6, third batch; registered by the main window since the integration of 2026-10-20): the WPF
// window opens this viewer from its AUDIT LOG... button (MainWindow.AuditLog_Click: new AuditLogWindow(this).Show(),
// modeless). The main window registers it in the File menu after "Open run folder..." (the WPF button sits beside LOAD RUN...):
//     ShellCommands.Register(registry, actions, new[] { AuditLogWindow.Command(() => window) });
// which is this definition (see Command below):
//     Id       = "file.auditLog"            (AuditLogWindow.CommandId)
//     Title    = "Audit log..."
//     MenuPath = "File/Audit log..."
//     Gesture  = none                       (WPF has no shortcut for it)
//     Modes    = CommandModes.Both          (reading a log changes nothing, so it is offered in Author and Run)
//     Platforms = CommandPlatforms.Desktop  (the default: the mobile heads carry no audit tooling)
//     Execute  = a new AuditLogWindow shown modeless over the main window, as WPF.

using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using GridSim.Ui.Commands;
using GridSim.Ui.Tabs.AuditLog;

namespace GridSim.App.Tabs;

/// <summary>
/// The audit-log viewer (N151; UI programme Phase 1.6): pick a <c>gridsim-audit/1</c> file, optionally enter an
/// external anchor, and see what the shared <see cref="AuditLogModel"/> decides (Core's AuditLogDisplay: every entry
/// and the OK line for a log that verifies; only the TAMPERED verdict for one that does not; the error and no entries
/// for a file that cannot be read). The file is chosen through the platform's storage provider; a test sets
/// <see cref="ChooseFile"/> instead.
/// </summary>
public sealed partial class AuditLogWindow : Window
{
    /// <summary>The registry id of the command that opens this window (see the definition at the top of this file).</summary>
    public const string CommandId = "file.auditLog";

    /// <summary>A window with its own, empty model.</summary>
    public AuditLogWindow()
    {
        InitializeComponent();
        ChooseFile = PickWithStorageProvider;
        DataContext = Model;
        Model.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(AuditLogModel.Tone)) ShowTone();
        };
        ShowTone();
    }

    /// <summary>What this window shows.</summary>
    public AuditLogModel Model { get; } = new();

    /// <summary>
    /// The file chooser OPEN LOG... uses: by default the platform's open-file picker (the WPF dialog's title and
    /// filter); a headless test replaces it. It returns the chosen local path, or null when the user cancels.
    /// </summary>
    public Func<Task<string?>> ChooseFile { get; set; }

    /// <summary>The command definition the coordinator registers (see the top of this file); <paramref name="owner"/>
    /// returns the window the viewer is shown over.</summary>
    public static AppCommand Command(Func<Window> owner)
    {
        ArgumentNullException.ThrowIfNull(owner);
        return new AppCommand
        {
            Id = CommandId,
            Title = "Audit log...",
            MenuPath = "File/Audit log...",
            Modes = CommandModes.Both,
            Execute = () =>
            {
                new AuditLogWindow().Show(owner());   // modeless, as the WPF window
                return Task.CompletedTask;
            },
        };
    }

    // The WPF OPEN LOG... button: the model parses the anchor, asks for the file only when it parsed, and shows the
    // verdict (and the entries of a verified log).
    private async void Open_Click(object? sender, RoutedEventArgs e) => await Model.OpenAsync(ChooseFile);

    // The verdict colour: the theme's Text before any check, its Green for a verified log, its Red otherwise (the WPF
    // window's TryFindResource("Green" / "Red"), keeping the old colour if the theme has no such brush).
    private void ShowTone()
    {
        string key = Model.Tone switch
        {
            AuditVerdictTone.Neutral => "Text",
            AuditVerdictTone.Verified => "Green",
            AuditVerdictTone.Failed => "Red",
            _ => throw new InvalidOperationException($"Unknown audit verdict tone {Model.Tone}."),
        };
        if (this.TryFindResource(key, out object? brush) && brush is IBrush b) VerdictText.Foreground = b;
    }

    // The platform's open-file picker with the WPF dialog's title and filter.
    private async Task<string?> PickWithStorageProvider()
    {
        IReadOnlyList<IStorageFile> files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = AuditLogModel.DialogTitle,
            AllowMultiple = false,
            FileTypeFilter = AuditLogModel.DialogFileTypes
                .Select(t => new FilePickerFileType(t.Name) { Patterns = t.Patterns.ToList() })
                .ToList(),
        }).ConfigureAwait(true);
        if (files.Count == 0) return null;   // cancelled
        // A file that is not on this computer (a phone's content provider) has no local path: say so, never read it.
        return files[0].TryGetLocalPath() ?? throw new InvalidOperationException($"{files[0].Name} is not a file on this computer.");
    }
}
