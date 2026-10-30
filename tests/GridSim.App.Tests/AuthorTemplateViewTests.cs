using System.Diagnostics;
using System.Text.Json;
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
using GridSim.App.Tabs;
using GridSim.Core.Assets;
using GridSim.Core.IO.Json;
using GridSim.Core.Licensing;
using GridSim.Core.Model;
using GridSim.Ui.Assets.Editing;
using GridSim.Ui.Assets.Editing.Templates;
using GridSim.Ui.Assets.Navigation;
using GridSim.Ui.Commands;
using GridSim.Ui.Models;
using GridSim.Ui.Shell;
using GridSim.Ui.Tabs.InverterFleet;
using Xunit;

namespace GridSim.App.Tests;

/// <summary>
/// UI programme Phase 4.4 (2026-10-21) in the Avalonia window (headless, Skia): inverter templates in AUTHOR mode. On a
/// temporary v2 case (data/uk written by the v2 writer, with the committed template fixture merged into its manifest:
/// tests/GridSim.Tests/fixtures/v2/templates/inverter-templates.json) loaded through the run pipeline and playing:
/// INVERTER FLEET shows the fixture's classes; in AUTHOR the TEMPLATES node and its context menus are the registry's;
/// a template is added (Add template...), a parameter typed into its kind-spec grid in the view, a generator bound to it
/// (Bind to template...), an override set with the view's SET OVERRIDE; the generator's editor shows its template link
/// and the effective parameters from the loader; SAVE, then RUN reloads the case and INVERTER FLEET and the running
/// model show the new template, its parameter and the override.
/// </summary>
public sealed class AuthorTemplateViewTests
{
    private const string Wind = "gridsim.local.wind-gfl@1", NewRef = "gridsim.local.new-gfm@1";

    private sealed class ScriptedPrompts : IAuthorPrompts
    {
        public Queue<DirtyChoice> Dirty { get; } = new();
        public Queue<string?> Texts { get; } = new();
        public Queue<bool> Confirms { get; } = new();
        public Queue<IReadOnlyList<KeyValuePair<string, string>>?> Fields { get; } = new();
        public List<string> Asked { get; } = new();

        public Task<DirtyChoice> AskSaveDiscardCancelAsync(string message)
        {
            Asked.Add("dirty: " + message);
            return Task.FromResult(Dirty.Dequeue());
        }

        public Task<bool> ConfirmAsync(string title, string message)
        {
            Asked.Add("confirm: " + message);
            return Task.FromResult(Confirms.Dequeue());
        }

        public Task<string?> AskTextAsync(string title, string message, string initial)
        {
            Asked.Add("text: " + title + " [" + initial + "]");
            return Task.FromResult(Texts.Dequeue());
        }

        public Task<IReadOnlyList<KeyValuePair<string, string>>?> AskFieldsAsync(string title, string message, IReadOnlyList<NewRecordField> fields)
        {
            Asked.Add("fields: " + title + ": " + string.Join(",", fields.Select(f => f.Field)));
            return Task.FromResult(Fields.Dequeue());
        }
    }

    // ---------------------------------------------------------------- fixtures and pumping (as AuthorModeViewTests)

    private static string RepoRoot()
    {
        for (DirectoryInfo? d = new(AppContext.BaseDirectory); d is not null; d = d.Parent)
            if (File.Exists(Path.Combine(d.FullName, "GridSim.slnx"))) return d.FullName;
        throw new InvalidOperationException("The GridSim checkout (GridSim.slnx) was not found above " + AppContext.BaseDirectory);
    }

    // data/uk written as a JSON v2 case into temp (a case of the user's own, never data/), with the template fixture's
    // localTemplates, bindings and overrides merged into the manifest in the case schema's member order.
    private static string WriteTemplateCase(TempFolder temp)
    {
        GridModel uk = JsonGridLoader.LoadDirectory(Path.Combine(RepoRoot(), "data", "uk"));
        string manifest = temp.File("uk.case.json");
        V2CaseWriter.Write(uk, manifest);
        string block = File.ReadAllText(Path.Combine(RepoRoot(), "tests", "GridSim.Tests", "fixtures", "v2", "templates", "inverter-templates.json"));
        var options = new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true };
        using JsonDocument m = JsonDocument.Parse(File.ReadAllBytes(manifest), options);
        using JsonDocument b = JsonDocument.Parse(block, options);
        string[] order = { "schema", "name", "description", "system", "gridCodeProfile", "solverProfile", "network", "imports", "localTemplates", "bindings", "overrides", "assets" };
        foreach (JsonProperty p in m.RootElement.EnumerateObject())
            Assert.True(order.Contains(p.Name, StringComparer.Ordinal), "the v2 writer wrote a manifest member '" + p.Name + "' the fixture merge does not place");
        using var ms = new MemoryStream();
        using (var w = new Utf8JsonWriter(ms, new JsonWriterOptions { Indented = true }))
        {
            w.WriteStartObject();
            foreach (string name in order)
            {
                JsonElement from = name is "localTemplates" or "bindings" or "overrides" ? b.RootElement : m.RootElement;
                if (from.TryGetProperty(name, out JsonElement v)) { w.WritePropertyName(name); v.WriteTo(w); }
            }
            w.WriteEndObject();
        }
        File.WriteAllBytes(manifest, ms.ToArray());
        return manifest;
    }

    private static ShellOptions Options(TempFolder temp, ModelSource source, ScriptedPrompts prompts) => new()
    {
        SettingsStore = ShellSettingsStore.ForFile(temp.File("settings.json")),
        DiscoverCases = (_, _) => new List<ModelSource> { source },
        LicenceDescription = () => "Test licence",
        Licence = () => new LicenceGate(Entitlement.FreeDefault),
        UseNativeMenu = false,
        AutoLoadAtStart = false,
        LoadGeography = _ => null,
        AuthorPrompts = _ => prompts,
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

    private static void Close(MainWindow w)
    {
        w.Close();
        if (w.StopLoop is { } stop) Await(stop);
    }

    private static MainWindow OpenPlaying(TempFolder temp, ModelSource source, ScriptedPrompts prompts)
    {
        var w = new MainWindow(Options(temp, source, prompts));
        w.Show();
        RunViewModel run = w.ViewModel.Run;
        int frames = 0;
        run.FrameShown += (_, _) => frames++;
        Await(w.ViewModel.LoadCaseAsync(source));
        Assert.False(run.HasError, run.ErrorText);
        Await(w.ViewModel.Registry.Get(RunCommands.SpeedId(60)).RunAsync());
        PumpUntil(() => frames >= 2, "two frames");
        return w;
    }

    private static void Render(MainWindow w)
    {
        Dispatcher.UIThread.RunJobs();
        w.CaptureRenderedFrame()?.Dispose();
        Dispatcher.UIThread.RunJobs();
    }

    private static AssetsView ShowAssets(MainWindow w)
    {
        w.SelectTab(MainWindow.AssetsTabHeader);
        Render(w);
        return w.AssetsView;
    }

    private static List<string> Texts(Control root) =>
        root.GetVisualDescendants().OfType<TextBlock>().Where(t => t.IsEffectivelyVisible).Select(t => t.Text ?? "").ToList();

    private static bool Toggle(MainWindow w, AssetsView v)
    {
        v.FindControl<Button>("ModeToggle")!.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        bool done = Await(v.PendingModeSwitch!);
        Render(w);
        return done;
    }

    private static AuthorView Pane(AssetsView v) => v.FindControl<AuthorView>("AuthorPane")!;

    private static void SubTab(MainWindow w, AuthorView pane, string tab)
    {
        pane.FindControl<TabStrip>("AuthorSubTabStrip")!.SelectedItem = tab;
        Render(w);
    }

    private static T Visible<T>(Control root, string name) where T : Control =>
        root.GetVisualDescendants().OfType<T>().Single(c => c.Name == name && c.IsEffectivelyVisible);

    private static KeyValuePair<string, string>[] Kv(params (string K, string V)[] x) => x.Select(p => new KeyValuePair<string, string>(p.K, p.V)).ToArray();

    private static string[] Entries(ContextMenu? menu) => menu is null ? Array.Empty<string>()
        : menu.Items.Select(i => i is Separator ? "-" : (string)((MenuItem)i!).Header!).ToArray();

    // =================================================================================================================

    [AvaloniaFact]
    public void Templates_edit_bind_override_save_and_RUN_reload_shows_them_in_INVERTER_FLEET()
    {
        using var temp = new TempFolder();
        string manifest = WriteTemplateCase(temp);
        var prompts = new ScriptedPrompts();
        ModelSource source = ModelCatalog.V2CaseSource(manifest);
        MainWindow w = OpenPlaying(temp, source, prompts);
        try
        {
            MainWindowViewModel vm = w.ViewModel;
            CommandRegistry r = vm.Registry;
            InverterFleetModel fleet = vm.Tabs.InverterFleet;
            // ---- RUN: the fixture's templates are the fleet's classes ----
            Assert.True(fleet.Active, fleet.Status);
            Assert.Equal(new[] { "solar-gfm", "wind-gfl" }, fleet.Classes.Select(c => c.ClassName).OrderBy(n => n, StringComparer.Ordinal));
            Assert.Equal("5", fleet.Classes.Single(c => c.ClassName == "wind-gfl").Count);
            foreach (CommandTargets t in new[] { CommandTargets.TemplateClassNode, CommandTargets.Template, CommandTargets.TemplateOverride })
                Assert.Null(w.ContextMenuFor(t));   // Run mode: no template menu

            // ---- AUTHOR: the TEMPLATES node and its menus ----
            AssetsView v = ShowAssets(w);
            Assert.True(Toggle(w, v));
            AuthorView pane = Pane(v);
            AssetAuthorModel author = vm.Tabs.Assets.Author!;
            AuthorNode templates = author.TemplatesNode!;
            Assert.Equal(new[] { "Add template..." }, Entries(pane.MenuFor(templates)));
            Assert.Equal(new[] { "Duplicate", "Delete", "Bind to template..." }, Entries(pane.MenuFor(author.TemplateNode(Wind)!)));
            Assert.Equal(new[] { "Reset override" }, Entries(pane.MenuFor(author.TemplateNode(Wind)!.Children[0])));
            // A generator's menu is unchanged (the template commands live on the template nodes).
            Assert.Equal(new[] { "Duplicate", "Delete" }, Entries(w.ContextMenuFor(CommandTargets.Generator)));
            pane.FindControl<TreeView>("AuthorTree")!.SelectedItem = templates;
            Render(w);
            Assert.Same(templates, author.Selected);
            Assert.True(pane.FindControl<ContentControl>("TemplatesPageHost")!.IsEffectivelyVisible);
            Assert.Contains(Wind, Texts(pane));

            // ---- Add template... (the registry command, its prompt answered) ----
            prompts.Fields.Enqueue(Kv(("kind", "gridFormingInverter"), ("name", "new-gfm"), ("title", "")));
            Await(r.Get(TemplateAuthorCommands.AddTemplate).RunAsync());
            Assert.Equal(NewRef, author.Selected!.TemplateRef);
            Assert.Contains("fields: Add template: kind,name,title", prompts.Asked);

            // ---- a parameter typed into the template's kind-spec grid in the view, Enter: one undo step ----
            Render(w);
            SubTab(w, pane, "PROPERTIES");
            Assert.True(pane.FindControl<ContentControl>("TemplateEditorHost")!.IsEffectivelyVisible);
            Assert.False(pane.FindControl<ContentControl>("FieldsHost")!.IsEffectivelyVisible);
            ItemsControl rows = Visible<ItemsControl>(pane, "TemplateParamRows");
            TemplateEditorModel ed = author.TemplateEditor!;
            Assert.Equal(TemplateKinds.ParamsOf("gridFormingInverter").Select(p => p.Name), ed.Params.Rows.Select(x => x.Field));
            int index = ed.Params.Rows.ToList().FindIndex(x => x.Field == "synInertiaMwsPerHz");
            rows.ScrollIntoView(index);
            Dispatcher.UIThread.RunJobs();
            TextBox box = rows.ContainerFromIndex(index)!.GetVisualDescendants().OfType<TextBox>().Single(t => t.Name == "TemplateParamBox");
            int depth = author.Authoring.UndoDepth;
            box.Text = "321";
            box.RaiseEvent(new KeyEventArgs { RoutedEvent = InputElement.KeyDownEvent, Key = Key.Enter, Source = box });
            Render(w);
            Assert.False(author.StatusIsError, author.Status);
            Assert.Equal(depth + 1, author.Authoring.UndoDepth);
            Assert.Equal("321", author.TemplateEditor!.Params.Row("synInertiaMwsPerHz").Literal);
            // A value the kind spec refuses, typed the same way, is refused with its message in the status line.
            box = Visible<ItemsControl>(pane, "TemplateParamRows").ContainerFromIndex(index)!.GetVisualDescendants().OfType<TextBox>().Single(t => t.Name == "TemplateParamBox");
            box.Text = "-1";
            box.RaiseEvent(new KeyEventArgs { RoutedEvent = InputElement.KeyDownEvent, Key = Key.Enter, Source = box });
            Render(w);
            Assert.True(author.StatusIsError);
            Assert.Contains("is below the minimum 0 for 'synInertiaMwsPerHz'", pane.FindControl<TextBlock>("AuthorStatus")!.Text, StringComparison.Ordinal);

            // ---- Bind to template... : South Solar by its own template member ----
            prompts.Fields.Enqueue(Kv(("generator", "South Solar"), ("matchType", "")));
            Await(r.Get(TemplateAuthorCommands.Bind).RunAsync());
            Assert.False(author.StatusIsError, author.Status);
            Assert.Equal(NewRef, author.Selected!.TemplateRef);   // the template stays selected
            Assert.Equal(NewRef, author.Templates().Generators.Single(g => g.Name == "South Solar").Template);

            // ---- an override set through the view's SET OVERRIDE on the wind template ----
            author.Select(author.TemplateNode(Wind)!);
            Render(w);
            SubTab(w, pane, "PROPERTIES");
            TemplateEditorModel windEd = author.TemplateEditor!;
            windEd.OverrideParam = "actuateTauS";
            windEd.OverrideText = "0.4";
            Visible<Button>(pane, "SetOverrideButton").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Render(w);
            Assert.False(author.StatusIsError, author.Status);
            Assert.Contains("template 0.25 -> override 0.4", Texts(pane));
            Assert.Equal(2, author.TemplateNode(Wind)!.Children.Count);   // two override nodes now

            // ---- the generator's editor: its template link and the effective parameters from the loader ----
            AuthorNode solar = author.ClassNode("generators")!.Children.Single(n => n.Label == "'South Solar'");
            pane.FindControl<TreeView>("AuthorTree")!.SelectedItem = solar;
            Render(w);
            SubTab(w, pane, "PROPERTIES");
            Assert.True(Visible<Border>(pane, "TemplateLinkCard").IsEffectivelyVisible);
            Assert.Equal(NewRef + " (gridFormingInverter), by its own template member.", Visible<TextBlock>(pane, "TemplateLinkText").Text);
            Visible<Button>(pane, "ShowEffectiveButton").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Render(w);
            EffectiveBinding eff = author.GeneratorTemplate!.Effective!;
            Assert.Equal("new-gfm", eff.ClassName);
            Assert.Equal(321.0, eff.Values["synInertiaMwsPerHz"]);
            Assert.Contains("321", Texts(pane));
            Visible<Button>(pane, "OpenTemplateButton").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Render(w);
            Assert.Equal(NewRef, author.Selected!.TemplateRef);

            // ---- Check passes; SAVE; RUN reloads the saved case ----
            pane.FindControl<Button>("CheckButton")!.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            PumpUntil(() => author.Authoring.LastCheck is not null, "the check");
            Assert.True(author.Authoring.LastCheck!.Passed, string.Join(Environment.NewLine, author.Authoring.LastCheck.Lines()));
            Await(r.Get(AuthorCommands.Save).RunAsync());
            Assert.False(author.Authoring.IsDirty, author.Status);
            Assert.True(Toggle(w, v));
            Await(w.AuthorMode.PendingReload!);
            Assert.False(vm.Run.HasError, vm.Run.ErrorText);
            Assert.Equal(CommandModes.Run, vm.Mode);
            int frames = 0;
            vm.Run.FrameShown += (_, _) => frames++;
            PumpUntil(() => frames >= 2 && fleet.Classes.Any(c => c.ClassName == "new-gfm"), "the reloaded case's INVERTER FLEET");

            // INVERTER FLEET: the new template is a grid-forming class of one unit; solar-gfm lost its only generator.
            InverterFleetRowModel added = fleet.Classes.Single(c => c.ClassName == "new-gfm");
            Assert.Equal("grid-forming", added.Mode);
            Assert.Equal("1", added.Count);
            Assert.DoesNotContain(fleet.Classes, c => c.ClassName == "solar-gfm");
            Assert.Equal("5", fleet.Classes.Single(c => c.ClassName == "wind-gfl").Count);
            w.SelectTab(TabModels.InverterFleetHeader);
            Render(w);
            Assert.Contains("new-gfm", Texts(w.FindControl<InverterFleetView>("InverterFleetTab")!));
            // The running model holds the new parameters: the typed value and the override (with its own provenance).
            AssetDynamicsModel dyn = vm.Run.LastFrame!.Model.AssetDynamics!;
            Assert.Equal(321.0, dyn.Library.Get("new-gfm").Params["synInertiaMwsPerHz"]);
            Assert.Equal(0.4, dyn.Library.Get("wind-gfl").Params["actuateTauS"]);
            Assert.Equal(0.08, dyn.Library.Get("wind-gfl").Params["deadbandHz"]);
            Assert.Contains("[VERIFY] set by an override", dyn.Library.Get("wind-gfl").ProvenanceFor("actuateTauS").Note, StringComparison.Ordinal);
        }
        finally { Close(w); }
    }
}
