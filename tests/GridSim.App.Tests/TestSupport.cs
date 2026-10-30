using Avalonia.Threading;
using GridSim.App.Shell;
using GridSim.Ui.Models;
using GridSim.Ui.Shell;

namespace GridSim.App.Tests;

/// <summary>Shell actions that only record which one ran, so a test can check a menu item or a shortcut reached it.</summary>
internal sealed class RecordingShellActions : IShellActions
{
    public List<string> Calls { get; } = new();

    public Task OpenCaseAsync() { Calls.Add("OpenCase"); return Task.CompletedTask; }
    public Task OpenRunFolderAsync() { Calls.Add("OpenRunFolder"); return Task.CompletedTask; }
    public Task ExitAsync() { Calls.Add("Exit"); return Task.CompletedTask; }
    public Task ShowAboutAsync() { Calls.Add("About"); return Task.CompletedTask; }
    public Task ShowThirdPartyNoticesAsync() { Calls.Add("Notices"); return Task.CompletedTask; }
    public Task ShowLicenceAsync() { Calls.Add("Licence"); return Task.CompletedTask; }
}

/// <summary>
/// A temporary folder for one test (its own settings file, its own document folder), deleted afterwards. Nothing a
/// test does touches the user's real settings or the app's real folders.
/// </summary>
internal sealed class TempFolder : IDisposable
{
    public TempFolder()
    {
        Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "gridsim-app-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path);
    }

    public string Path { get; }

    public string File(string name) => System.IO.Path.Combine(Path, name);

    public void Dispose()
    {
        try { Directory.Delete(Path, recursive: true); }
        catch (IOException) { /* a viewer still closing; the OS temp clean-up takes it */ }
    }
}

internal static class TestShell
{
    /// <summary>
    /// The test model list: two cases that need no files (the picker only lists them; nothing is loaded).
    /// </summary>
    public static List<ModelSource> FakeCases(string root) => new()
    {
        new ModelSource("Case A", System.IO.Path.Combine(root, "case-a")),
        new ModelSource("Case B", System.IO.Path.Combine(root, "case-b")),
    };

    /// <summary>Options for a window that reads and writes only inside <paramref name="temp"/>.</summary>
    public static ShellOptions Options(TempFolder temp, Func<MainWindow, IShellActions>? actions = null, string? documentFolder = null)
        => new()
        {
            SettingsStore = ShellSettingsStore.ForFile(temp.File("settings.json")),
            DiscoverCases = (_, _) => FakeCases(temp.Path),
            LicenceDescription = () => "Test licence",
            DocumentFolder = documentFolder,
            Actions = actions,
            UseNativeMenu = false,
            // Test retargeted 2026-10-20 (UI programme 1.8): the window now loads a source by itself when it opens, as
            // the WPF window does; these tests were written for the window that opens empty, so they turn that off
            // (ShellOptions.AutoLoadAtStart). Every assertion is unchanged.
            AutoLoadAtStart = false,
        };

    /// <summary>Run the UI thread's queued work (bindings, async continuations) until it is idle.</summary>
    public static void Pump() => Dispatcher.UIThread.RunJobs();
}
