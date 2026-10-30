using System.Text.Json;
using Avalonia.Headless.XUnit;
using GridSim.Ui.Settings;
using GridSim.Ui.Shell;
using Xunit;

namespace GridSim.App.Tests;

/// <summary>
/// UI programme Phase 1.1: the shell keeps its settings in the same file, in the same format, as the WPF window, so
/// either app picks up where the other left off. The real file is only located here, never written: every round trip
/// uses a temporary file through the same <see cref="AppSettings"/> code.
/// </summary>
public sealed class ShellSettingsTests
{
    // The options AppSettings.Save uses: indented, otherwise System.Text.Json defaults.
    private static readonly JsonSerializerOptions Indented = new() { WriteIndented = true };

    [Fact]
    public void The_default_store_is_the_settings_file_the_WPF_window_uses()
    {
        // Spelled out independently of the code under test: %LOCALAPPDATA%\GridSim\settings.json on Windows, the
        // platform's local application data folder elsewhere.
        string expected = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "GridSim", "settings.json");
        Assert.Equal(expected, ShellSettingsStore.Default.FilePath);
        // And the main window's default options use that store.
        Assert.Same(ShellSettingsStore.Default, new GridSim.App.Shell.ShellOptions().SettingsStore);
    }

    [Fact]
    public void A_store_path_must_be_absolute()
    {
        Assert.Throws<ArgumentException>(() => ShellSettingsStore.ForFile("settings.json"));
        Assert.Throws<ArgumentException>(() => ShellSettingsStore.ForFile(" "));
    }

    [Fact]
    public void Settings_round_trip_through_the_store_in_the_AppSettings_format()
    {
        using var temp = new TempFolder();
        ShellSettingsStore store = ShellSettingsStore.ForFile(temp.File("settings.json"));

        // A missing file is the first-run case: defaults, no problem.
        AppSettings s = store.Load();
        Assert.Null(s.LoadProblem);
        Assert.Null(s.LastSource);
        Assert.True(s.Minimap);   // the one default that is true
        Assert.Equal("", s.BridgeEndpoint);

        s.LastSource = "Case B";
        s.LastSourcePath = temp.File("case-b");
        s.Coast = true;
        s.Minimap = false;
        s.BridgeEndpoint = "wss://bridge.example.com/ws/bridge";
        s.Save();
        Assert.Null(s.SaveProblem);

        // The file holds exactly what the WPF window writes for the same values: AppSettings serialised as indented
        // JSON with System.Text.Json's defaults (the format is the class, not a copy of it).
        string written = File.ReadAllText(store.FilePath);
        Assert.Equal(JsonSerializer.Serialize(s, Indented), written);
        using (JsonDocument doc = JsonDocument.Parse(written))
        {
            JsonElement root = doc.RootElement;
            Assert.Equal("Case B", root.GetProperty("LastSource").GetString());
            Assert.False(root.GetProperty("Minimap").GetBoolean());
            // The session-only fields are never written.
            Assert.False(root.TryGetProperty("LoadProblem", out _));
            Assert.False(root.TryGetProperty("SaveProblem", out _));
        }

        // Read back through the store, and through a plain deserialise as the WPF Load does.
        AppSettings back = store.Load();
        AppSettings plain = JsonSerializer.Deserialize<AppSettings>(written)!;
        foreach (AppSettings r in new[] { back, plain })
        {
            Assert.Equal("Case B", r.LastSource);
            Assert.Equal(temp.File("case-b"), r.LastSourcePath);
            Assert.True(r.Coast);
            Assert.False(r.Minimap);
            Assert.Equal("wss://bridge.example.com/ws/bridge", r.BridgeEndpoint);
        }
        Assert.Null(back.LoadProblem);
    }

    [Fact]
    public void An_unreadable_settings_file_is_reported_and_never_overwritten()
    {
        using var temp = new TempFolder();
        string path = temp.File("settings.json");
        const string handEdit = "{ \"BridgeEndpoint\": \"wss://x/ws\", }";   // trailing comma: not valid JSON
        File.WriteAllText(path, handEdit);
        ShellSettingsStore store = ShellSettingsStore.ForFile(path);

        AppSettings s = store.Load();
        Assert.NotNull(s.LoadProblem);
        Assert.Contains(path, s.LoadProblem, StringComparison.Ordinal);
        s.LastSource = "Case A";
        s.Save();
        Assert.NotNull(s.SaveProblem);
        Assert.Equal(handEdit, File.ReadAllText(path));
    }

    [AvaloniaFact]
    public void The_window_reports_an_unreadable_settings_file_and_leaves_it_alone_on_exit()
    {
        using var temp = new TempFolder();
        string path = temp.File("settings.json");
        File.WriteAllText(path, "not json");
        var w = new MainWindow(TestShell.Options(temp));
        w.Show();
        w.ViewModel.ChooseCase(TestShell.FakeCases(temp.Path)[0]);   // a change, so exit tries to save
        Assert.True(w.ViewModel.HasProblem);
        Assert.StartsWith("Settings ignored and not overwritten: ", w.ViewModel.ProblemText, StringComparison.Ordinal);
        w.Close();
        Assert.Equal("not json", File.ReadAllText(path));
        Assert.NotNull(w.ViewModel.Settings.SaveProblem);
    }

    [AvaloniaFact]
    public void The_window_saves_on_exit_only_when_something_changed()
    {
        using var temp = new TempFolder();
        string path = temp.File("settings.json");

        var unchanged = new MainWindow(TestShell.Options(temp));
        unchanged.Show();
        unchanged.Close();
        Assert.False(File.Exists(path), "a session that changed nothing must not write the settings file");

        // A file another app wrote (here: with extra spacing) is left byte for byte when nothing changed.
        const string wpfWritten = "{\n  \"LastSource\": \"Case A\",\n  \"Minimap\": true\n}";
        File.WriteAllText(path, wpfWritten);
        var reader = new MainWindow(TestShell.Options(temp));
        reader.Show();
        Assert.Equal("Case A", reader.ViewModel.Settings.LastSource);
        reader.Close();
        Assert.Equal(wpfWritten, File.ReadAllText(path));

        var changer = new MainWindow(TestShell.Options(temp));
        changer.Show();
        changer.ViewModel.ChooseCase(TestShell.FakeCases(temp.Path)[1]);
        changer.Close();
        AppSettings saved = ShellSettingsStore.ForFile(path).Load();
        Assert.Equal("Case B", saved.LastSource);
        Assert.Equal(TestShell.FakeCases(temp.Path)[1].Key, saved.LastSourcePath);
        Assert.True(saved.Minimap);   // values the shell does not touch survive the save
    }
}
