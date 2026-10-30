using System.Diagnostics;
using System.Text.Json.Nodes;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;
using GridSim.App.Assets;
using GridSim.App.Run;
using GridSim.App.Shell;
using GridSim.Core.IO.Json;
using GridSim.Core.Licensing;
using GridSim.Core.Model;
using GridSim.Ui.Assets.Editing;
using GridSim.Ui.Assets.Editing.Elements;
using GridSim.Ui.Assets.Navigation;
using GridSim.Ui.Commands;
using GridSim.Ui.Models;
using GridSim.Ui.Shell;
using Xunit;

namespace GridSim.App.Tests;

/// <summary>
/// UI programme Phase 4.3 in the Avalonia window (headless, Skia): the per-element editors in AUTHOR mode's PROPERTIES
/// sub-tab. On a temporary v2 case (data/uk written by the convert verb's writer) and a temporary v1 copy of data/uk
/// with a protection placed (data/gb-synthetic's asset classes), loaded through the run pipeline: for a selected record of each
/// kind the editor renders (its heading, its kind, every section title, every derived read-out with its value) in place
/// of the generic grid; typing a value and Enter is one undo step, and the registry's Undo puts it back; a refused value
/// shows its reason inline on the field (and in the status line); a pick in a picker commits; a locked field's box is
/// disabled with the loader's reason shown; the EDITOR / GRID switch shows the generic grid and back.
/// </summary>
public sealed class ElementEditorsViewTests
{
    // ---------------------------------------------------------------- fixtures (as AuthorModeViewTests)

    private sealed class NoPrompts : IAuthorPrompts
    {
        public Task<DirtyChoice> AskSaveDiscardCancelAsync(string message) => throw new InvalidOperationException("unexpected prompt: " + message);

        public Task<bool> ConfirmAsync(string title, string message) => throw new InvalidOperationException("unexpected prompt: " + message);

        public Task<string?> AskTextAsync(string title, string message, string initial) => throw new InvalidOperationException("unexpected prompt: " + message);

        public Task<IReadOnlyList<KeyValuePair<string, string>>?> AskFieldsAsync(string title, string message, IReadOnlyList<NewRecordField> fields)
            => throw new InvalidOperationException("unexpected prompt: " + message);
    }

    private static string RepoRoot()
    {
        for (DirectoryInfo? d = new(AppContext.BaseDirectory); d is not null; d = d.Parent)
            if (File.Exists(Path.Combine(d.FullName, "GridSim.slnx"))) return d.FullName;
        throw new InvalidOperationException("The GridSim checkout (GridSim.slnx) was not found above " + AppContext.BaseDirectory);
    }

    // data/uk written as a JSON v2 case by the convert verb's writer, which keeps a transformer as a line with
    // isTransformer (V2CaseWriter: "a transformer as a line with isTransformer ... exactly as the v1 writer"); the
    // trailing run of those (data/uk's transformers come after its lines, so the branch order is kept) is then moved
    // into the network's "transformers" array as v2 transformer elements, so the case has a record of every kind.
    private static string WriteV2Case(TempFolder temp)
    {
        GridModel uk = JsonGridLoader.LoadDirectory(Path.Combine(RepoRoot(), "data", "uk"));
        string manifest = temp.File("uk.case.json");
        V2CaseWriter.Write(uk, manifest);
        string network = temp.File(V2CaseWriter.NetworkFileNameFor(manifest));
        JsonObject n = JsonNode.Parse(File.ReadAllText(network))!.AsObject();
        JsonArray lines = n["lines"]!.AsArray();
        var keep = new HashSet<string>(StringComparer.Ordinal) { "name", "from", "to", "rPu", "xPu", "tapRatio", "phaseShiftDeg", "ratingMva", "ratingBMva", "ratingCMva" };
        var transformers = new JsonArray();
        while (lines.Count > 0 && lines[^1]!["isTransformer"]?.GetValue<bool>() == true)
        {
            JsonObject t = lines[^1]!.AsObject();
            lines.RemoveAt(lines.Count - 1);
            var e = new JsonObject();
            foreach (KeyValuePair<string, JsonNode?> kv in t)
                if (keep.Contains(kv.Key)) e[kv.Key] = kv.Value?.DeepClone();
            transformers.Insert(0, e);
        }
        Assert.NotEmpty(transformers);
        n["transformers"] = transformers;
        File.WriteAllText(network, n.ToJsonString(new System.Text.Json.JsonSerializerOptions { WriteIndented = true }));
        return manifest;
    }

    // A v1 copy of data/uk in temp (a case of the user's own, not bundled) with protections: data/gb-synthetic's asset
    // classes (two of which are protection classes) and a protections.json placing one of them, as gb-synthetic does
    // (gb-synthetic itself is above the test licence's bus limit).
    private static string WriteV1CaseWithProtection(TempFolder temp)
    {
        string dir = temp.File("uk-with-protection");
        Directory.CreateDirectory(dir);
        foreach (string f in Directory.EnumerateFiles(Path.Combine(RepoRoot(), "data", "uk"), "*.json"))
            File.Copy(f, Path.Combine(dir, Path.GetFileName(f)));
        File.Copy(Path.Combine(RepoRoot(), "data", "gb-synthetic", "asset-classes.json"), Path.Combine(dir, "asset-classes.json"));
        File.WriteAllText(Path.Combine(dir, "protections.json"),
            """[ { "name": "embedded-dg-lom", "class": "prot-lom-legacy", "monitorBus": 1, "capacityMw": 800 } ]""");
        return dir;
    }

    private static ShellOptions Options(TempFolder temp, ModelSource source) => new()
    {
        SettingsStore = ShellSettingsStore.ForFile(temp.File("settings.json")),
        DiscoverCases = (_, _) => new List<ModelSource> { source },
        LicenceDescription = () => "Test licence",
        Licence = () => new LicenceGate(Entitlement.FreeDefault),
        UseNativeMenu = false,
        AutoLoadAtStart = false,
        LoadGeography = _ => null,
        AuthorPrompts = _ => new NoPrompts(),
        AuthorUserCasesFolder = temp.File("user-cases"),
    };

    private static void PumpUntil(Func<bool> done, string what, int timeoutMs = 120_000)
    {
        var clock = Stopwatch.StartNew();
        while (!done())
        {
            if (clock.ElapsedMilliseconds > timeoutMs) throw new TimeoutException($"Timed out after {timeoutMs} ms waiting for {what}");
            Dispatcher.UIThread.RunJobs();
            Thread.Sleep(2);
        }
        Dispatcher.UIThread.RunJobs();
    }

    private static T Await<T>(Task<T> task)
    {
        PumpUntil(() => task.IsCompleted, "a task to finish");
        return task.GetAwaiter().GetResult();
    }

    private static void Await(Task task)
    {
        PumpUntil(() => task.IsCompleted, "a task to finish");
        task.GetAwaiter().GetResult();
    }

    private static void Render(MainWindow w)
    {
        Dispatcher.UIThread.RunJobs();
        w.CaptureRenderedFrame()?.Dispose();
        Dispatcher.UIThread.RunJobs();
    }

    // Open the window, load the case through the run pipeline, show ASSETS and switch it to AUTHOR.
    private static (MainWindow W, AssetsView V, AuthorView Pane, AssetAuthorModel Author) OpenAuthor(TempFolder temp, ModelSource source)
    {
        var w = new MainWindow(Options(temp, source));
        w.Show();
        RunViewModel run = w.ViewModel.Run;
        int frames = 0;
        run.FrameShown += (_, _) => frames++;
        Await(w.ViewModel.LoadCaseAsync(source));
        Assert.False(run.HasError, run.ErrorText);
        Await(w.ViewModel.Registry.Get(RunCommands.SpeedId(60)).RunAsync());
        PumpUntil(() => frames >= 1, "a frame");
        w.SelectTab(MainWindow.AssetsTabHeader);
        Render(w);
        AssetsView v = w.AssetsView;
        v.FindControl<Button>("ModeToggle")!.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Assert.True(Await(v.PendingModeSwitch!));
        Render(w);
        AuthorView pane = v.FindControl<AuthorView>("AuthorPane")!;
        AssetAuthorModel author = Assert.IsType<AssetAuthorModel>(w.ViewModel.Tabs.Assets.Author);
        return (w, v, pane, author);
    }

    private static void Close(MainWindow w)
    {
        w.Close();
        if (w.StopLoop is { } stop) Await(stop);
    }

    private static List<string> NamedTexts(Control root, string name) =>
        root.GetVisualDescendants().OfType<TextBlock>().Where(t => t.Name == name && t.IsEffectivelyVisible).Select(t => t.Text ?? "").ToList();

    // The editor control named <name> bound to the field at <path> (the editor is rebuilt after every change, so this
    // is looked up again each time).
    private static T EditorControl<T>(AuthorView pane, string name, string path) where T : Control =>
        pane.GetVisualDescendants().OfType<T>().Single(c => c.Name == name && c.DataContext is EditorField f && f.Path == path);

    private static void TypeAndEnter(MainWindow w, TextBox box, string text)
    {
        box.Text = text;
        box.RaiseEvent(new KeyEventArgs { RoutedEvent = InputElement.KeyDownEvent, Key = Key.Enter, Source = box });
        Render(w);
    }

    // Select the first record of <role> in the tree and show PROPERTIES.
    private static ElementEditorModel ShowEditor(MainWindow w, AuthorView pane, AssetAuthorModel author, string role)
    {
        AuthorNode? cls = author.ClassNode(role);
        Assert.True(cls is { Children.Count: > 0 }, "the Author tree has no " + role + " record");
        AuthorNode node = cls!.Children[0];
        pane.FindControl<TreeView>("AuthorTree")!.SelectedItem = node;
        Render(w);
        Assert.Same(node, author.Selected);
        pane.FindControl<TabStrip>("AuthorSubTabStrip")!.SelectedItem = AssetNavigatorModel.PropertiesTab;
        Render(w);
        return Assert.IsAssignableFrom<ElementEditorModel>(author.Editor);
    }

    // The editor renders: heading, kind, every section title in order, every read-out label and value.
    private static void AssertRendered(AuthorView pane, AssetAuthorModel author, ElementEditorModel e)
    {
        Assert.True(author.IsEditorShown);
        Assert.True(pane.FindControl<ContentControl>("EditorHost")!.IsEffectivelyVisible);
        Assert.False(pane.FindControl<ContentControl>("FieldsHost")!.IsEffectivelyVisible);
        Assert.True(pane.FindControl<Button>("EditorViewToggle")!.IsEffectivelyVisible);
        Assert.Equal(new[] { e.Heading }, NamedTexts(pane, "EditorHeading"));
        Assert.Equal(new[] { e.KindTitle + " editor" }, NamedTexts(pane, "EditorKind"));
        Assert.Equal(e.Sections.Select(s => s.Title), NamedTexts(pane, "EditorSectionTitle"));
        Assert.Equal(e.AllFields.Select(f => f.Label), NamedTexts(pane, "EditorLabel"));
        List<DerivedReadout> ro = e.Sections.SelectMany(s => s.Readouts).ToList();
        Assert.NotEmpty(ro);
        Assert.Equal(ro.Select(r => r.Label), NamedTexts(pane, "ReadoutLabel"));
        Assert.Equal(ro.Select(r => r.Text), NamedTexts(pane, "ReadoutValue"));
    }

    // An edit typed into the editor and Enter is one undo step; the registry's Undo puts it back; a refused value shows
    // its reason inline on the field and in the status line, and changes nothing.
    private static void EditUndoRefuse(MainWindow w, AuthorView pane, AssetAuthorModel author, string field, string good)
    {
        int depth = author.Authoring.UndoDepth;
        string old = author.Editor!.Field(field).Literal;
        Assert.NotEqual(good, old);
        TypeAndEnter(w, EditorControl<TextBox>(pane, "EditorBox", field), good);
        Assert.False(author.StatusIsError, author.Status);
        Assert.Equal(depth + 1, author.Authoring.UndoDepth);
        Assert.Equal(good, author.Editor!.Field(field).Literal);
        Assert.True(author.IsEditorShown);   // the record stays selected and the editor shown
        Await(w.ViewModel.Registry.Get(AuthorCommands.Undo).RunAsync());
        Render(w);
        Assert.Equal(depth, author.Authoring.UndoDepth);
        Assert.Equal(old, author.Editor!.Field(field).Literal);

        TypeAndEnter(w, EditorControl<TextBox>(pane, "EditorBox", field), "not a number");
        Assert.True(author.StatusIsError);
        EditorField f = author.Editor!.Field(field);
        Assert.True(f.HasError);
        Assert.Equal(author.Status, f.Error);
        TextBlock inline = EditorControl<TextBlock>(pane, "EditorError", field);
        Assert.True(inline.IsEffectivelyVisible);
        Assert.Equal(f.Error, inline.Text);
        Assert.Equal(depth, author.Authoring.UndoDepth);
        Assert.Equal(old, f.Literal);
    }

    // =================================================================================================================

    [AvaloniaFact]
    public void Each_v2_element_editor_renders_edits_undoes_and_shows_a_refusal_inline()
    {
        using var temp = new TempFolder();
        ModelSource source = ModelCatalog.V2CaseSource(WriteV2Case(temp));
        (MainWindow w, AssetsView _, AuthorView pane, AssetAuthorModel author) = OpenAuthor(temp, source);
        try
        {
            foreach ((string role, string field, string good, Type kind) in new[]
            {
                ("buses", "vm", "1.02", typeof(BusEditorModel)),
                ("lines", "xPu", "0.21", typeof(LineEditorModel)),
                ("transformers", "xPu", "0.06", typeof(TransformerEditorModel)),
                ("generators", "capacityMva", "1400", typeof(GeneratorEditorModel)),
                ("demands", "pMw", "650", typeof(DemandEditorModel)),
                ("oltcs", "targetVpu", "1.01", typeof(OltcEditorModel)),
                ("boundaries", "limitMw", "3300", typeof(BoundaryEditorModel)),
            })
            {
                ElementEditorModel e = ShowEditor(w, pane, author, role);
                Assert.IsType(kind, e);
                Assert.True(e.IsV2);
                AssertRendered(pane, author, e);
                EditUndoRefuse(w, pane, author, field, good);
            }

            // A line: the loader-refused fields are locked (box disabled, the loader's reason shown); a pick commits.
            ShowEditor(w, pane, author, "lines");
            foreach (string locked in new[] { "type", "inService" })
            {
                EditorField f = author.Editor!.Field(locked);
                Assert.True(f.IsLocked);
                Assert.False(EditorControl<TextBox>(pane, "EditorBox", locked).IsEnabled);
                Assert.Contains(f.LockReason!, EditorControl<TextBlock>(pane, "EditorState", locked).Text, StringComparison.Ordinal);
            }
            int depth = author.Authoring.UndoDepth;
            ComboBox picker = EditorControl<ComboBox>(pane, "EditorPicker", "equipment");
            Assert.True(picker.IsEffectivelyVisible);
            picker.SelectedItem = ((EditorField)picker.DataContext!).Choices.Single(c => c.Value == "cable");
            Render(w);
            Assert.False(author.StatusIsError, author.Status);
            Assert.Equal(depth + 1, author.Authoring.UndoDepth);
            Assert.Equal("\"cable\"", author.Editor!.Field("equipment").Literal);
            Await(w.ViewModel.Registry.Get(AuthorCommands.Undo).RunAsync());
            Render(w);
            Assert.Equal(depth, author.Authoring.UndoDepth);

            // The EDITOR / GRID switch: the generic grid, then back to the editor.
            Button toggle = pane.FindControl<Button>("EditorViewToggle")!;
            Assert.Equal("SHOW ALL FIELDS AS A GRID", toggle.Content);
            toggle.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Render(w);
            Assert.True(pane.FindControl<ContentControl>("FieldsHost")!.IsEffectivelyVisible);
            Assert.False(pane.FindControl<ContentControl>("EditorHost")!.IsEffectivelyVisible);
            Assert.Equal("SHOW THE LINE EDITOR", toggle.Content);
            toggle.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Render(w);
            Assert.True(pane.FindControl<ContentControl>("EditorHost")!.IsEffectivelyVisible);
            Assert.False(author.Authoring.IsDirty);
        }
        finally { Close(w); }
    }

    [AvaloniaFact]
    public void The_v1_protection_editor_and_the_other_v1_editors_render_edit_undo_and_refuse_inline()
    {
        using var temp = new TempFolder();
        ModelSource source = ModelCatalog.CaseFolderSource(WriteV1CaseWithProtection(temp));
        (MainWindow w, AssetsView _, AuthorView pane, AssetAuthorModel author) = OpenAuthor(temp, source);
        try
        {
            foreach ((string role, string field, string good, Type kind) in new[]
            {
                ("protections", "capacityMw", "550", typeof(ProtectionEditorModel)),
                ("lines", "xPu", "0.21", typeof(LineEditorModel)),
                ("generators", "capacityMva", "1400", typeof(GeneratorEditorModel)),
            })
            {
                ElementEditorModel e = ShowEditor(w, pane, author, role);
                Assert.IsType(kind, e);
                Assert.False(e.IsV2);
                AssertRendered(pane, author, e);
                EditUndoRefuse(w, pane, author, field, good);
            }
            Assert.False(author.Authoring.IsDirty);
        }
        finally { Close(w); }
    }
}
