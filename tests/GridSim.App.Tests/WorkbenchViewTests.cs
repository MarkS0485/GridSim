using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;
using GridSim.App.Controls;
using GridSim.App.Workbench;
using GridSim.Core.IO.Matpower;
using GridSim.Core.Licensing;
using GridSim.Core.Model;
using GridSim.Core.Presentation.Workbench;
using GridSim.Core.Presentation.Workbench.Sld;
using Xunit;

namespace GridSim.App.Tests;

/// <summary>
/// RC4 R4-10 on the Avalonia app: the workbench window and its view model, in the headless platform with Skia rendering. The
/// view model shows the Core workbench layer, so every table cell, total and plot coordinate it holds equals the Core value
/// (the same one the web DTO is compared with in tests/GridSim.Tests/WebWorkbenchTests.cs); the window lists them; the plot
/// control paints the Core geometry; and the main window is not altered (its tab strip is pinned by TabsTests).
/// </summary>
public sealed class WorkbenchViewTests
{
    private static string RepoRoot()
    {
        for (DirectoryInfo? d = new(AppContext.BaseDirectory); d is not null; d = d.Parent)
            if (File.Exists(Path.Combine(d.FullName, "GridSim.slnx"))) return d.FullName;
        throw new InvalidOperationException("The GridSim checkout (GridSim.slnx) was not found above " + AppContext.BaseDirectory);
    }

    private static string CasePath(string name) => Path.Combine(RepoRoot(), "data", "matpower", "lib", name + ".m");

    private static GridModel Case(string name) => MatpowerParser.ParseFile(CasePath(name));

    private static WorkbenchViewModel Loaded(string name = "case9")
    {
        var vm = new WorkbenchViewModel();
        vm.SetProject(name, Case(name));
        return vm;
    }

    // ------------------------------------------------------------------ view model: Core values, unchanged

    [Fact]
    public void The_view_model_table_is_the_core_table_for_each_kind()
    {
        GridModel m = Case("case14");
        WorkbenchViewModel vm = Loaded("case14");
        foreach (ModelElementKind kind in Enum.GetValues<ModelElementKind>())
        {
            vm.SelectNode("model/" + kind.ToString().ToLowerInvariant());
            ModelTableView core = ModelTable.Build(m, new ModelTableQuery(kind));
            Assert.Equal(kind, vm.TableKind);
            Assert.NotNull(vm.Table);
            Assert.Equal(core.Canonical(), vm.Table!.Canonical());
            Assert.Equal(WorkbenchTextTables.Lines(core), vm.TableLines);
        }
    }

    [Fact]
    public void Sort_filter_and_grouping_flow_through_to_the_table()
    {
        GridModel m = Case("case14");
        WorkbenchViewModel vm = Loaded("case14");
        vm.SelectNode("model/bus");
        vm.SortKey = "pd";
        vm.SortDescending = true;
        vm.FilterColumn = "pd";
        vm.FilterOp = ModelFilterOp.Greater;
        vm.FilterValue = "10";
        vm.RebuildTable();
        int[] expected = m.Buses.Where(b => b.Pd > 10).OrderByDescending(b => b.Pd).Select(b => b.Id).ToArray();
        Assert.Equal(expected, vm.Table!.AllRows.Select(r => (int)r.Cells[0].Number!.Value).ToArray());
        Assert.Contains($"{expected.Length} of 14 shown", vm.StatusText, StringComparison.Ordinal);

        // A filter the model cannot answer is said, not silently dropped, and leaves the last good table.
        vm.FilterValue = "abc";
        vm.RebuildTable();
        Assert.Contains("Filter not applied", vm.StatusText, StringComparison.Ordinal);
        Assert.Equal(expected.Length, vm.Table!.ShownRows);
    }

    [Fact]
    public void Running_a_study_shows_its_result_plot_and_marks_the_tree()
    {
        WorkbenchViewModel vm = Loaded();
        Assert.Contains(vm.TreeRows, r => r.Node.Id == "study/power-flow" && r.Node.Detail == "not run");
        vm.SelectNode("study/power-flow");
        Assert.Null(vm.Result);
        WorkbenchResult? r = vm.RunSelected();
        Assert.NotNull(r);
        Assert.Equal("CONVERGED", r!.Status);
        Assert.Contains(vm.TreeRows, row => row.Node.Id == "study/power-flow" && row.Node.Detail == "CONVERGED");
        // The lines and the plot are Core's.
        Assert.Contains(vm.ResultLines, l => l == "Study power-flow (power-flow)");
        Assert.Equal(r.Table("buses")!.Rows.Count + 2, WorkbenchTextTables.Lines(r.Table("buses")!).Count);
        Assert.Equal(r.Plots[0].Spec.Sha256(), vm.Plot!.Sha256());
        Assert.Equal(["voltage-profile"], vm.PlotIds);
    }

    [Fact]
    public void A_study_that_cannot_run_says_why_and_a_missing_model_is_said()
    {
        var empty = new WorkbenchViewModel();
        Assert.Null(empty.RunSelected());
        Assert.Contains("Load a model first", empty.StatusText, StringComparison.Ordinal);
        Assert.Null(empty.GenerateReport());

        WorkbenchViewModel vm = Loaded();
        vm.SelectNode("study/group-summary");   // not in the project: case9 has no groups document
        Assert.Null(vm.Result);
        Assert.Null(vm.GenerateReport());
        Assert.Contains("Run the study first", vm.StatusText, StringComparison.Ordinal);

        Assert.False(vm.TryLoad(Path.Combine(RepoRoot(), "no", "such", "case.m")));
        Assert.Contains("Cannot open", vm.StatusText, StringComparison.Ordinal);
        Assert.False(vm.TryLoad(CasePath("case9") + ".missing"));
    }

    [Fact]
    public void The_report_is_made_from_the_result_and_saved_once()
    {
        WorkbenchViewModel vm = Loaded();
        vm.RunSelected("power-flow");
        vm.ReportFormat = WorkbenchReportFormat.Markdown;
        string? md = vm.GenerateReport();
        Assert.NotNull(md);
        Assert.Contains("CONVERGED", md, StringComparison.Ordinal);
        Assert.Contains("Result SHA-256: ", md, StringComparison.Ordinal);
        // The report is the one the headless route makes from the same result document.
        byte[] result = System.Text.Encoding.UTF8.GetBytes(vm.Project!.ResultJson("power-flow"));
        byte[] template = System.Text.Encoding.UTF8.GetBytes(ReportTemplate.DefaultJson(result));
        Assert.Equal(ReportRenderer.ToMarkdown(ReportRenderer.Build(template, result)), md);

        vm.ReportFormat = WorkbenchReportFormat.Html;
        Assert.Contains("<svg ", vm.GenerateReport()!, StringComparison.Ordinal);

        using var temp = new TempFolder();
        Assert.True(vm.SaveReport(temp.File("r.html")));
        Assert.Equal(vm.ReportText, File.ReadAllText(temp.File("r.html")));
        Assert.False(vm.SaveReport(temp.File("r.html")));   // never overwritten
        Assert.Contains("already exists", vm.StatusText, StringComparison.Ordinal);
    }

    [Fact]
    public void A_case_file_is_loaded_from_a_path()
    {
        var vm = new WorkbenchViewModel();
        Assert.True(vm.TryLoad(CasePath("case9")));
        Assert.Equal("case9", vm.Project!.Name);
        Assert.Equal(9, vm.Table!.TotalRows);
        Assert.Equal(["power-flow", "n-1-security", "qv-curve"], vm.Project.Studies.Select(s => s.Id));
    }

    // ------------------------------------------------------------------ A1: the licence check on Open

    /// <summary>A chain of <paramref name="buses"/> buses as a MATPOWER file under <paramref name="temp"/> (not a bundled network).</summary>
    private static string BigCase(TempFolder temp, int buses)
    {
        var inv = System.Globalization.CultureInfo.InvariantCulture;
        var lines = new List<string> { "function mpc = bigcase", "mpc.version = '2';", "mpc.baseMVA = 100;", "mpc.bus = [" };
        for (int i = 1; i <= buses; i++) lines.Add(string.Create(inv, $"\t{i}\t{(i == 1 ? 3 : 1)}\t{(i == 1 ? 0 : 0.1)}\t0\t0\t0\t1\t1\t0\t100\t1\t1.1\t0.9;"));
        lines.AddRange(["];", "mpc.gen = [", "\t1\t0\t0\t999\t-999\t1\t100\t1\t9999\t0;", "];", "mpc.branch = ["]);
        for (int i = 1; i < buses; i++) lines.Add(string.Create(inv, $"\t{i}\t{i + 1}\t0.001\t0.01\t0\t9999\t9999\t9999\t0\t0\t1\t-360\t360;"));
        lines.Add("];");
        string path = temp.File("bigcase.m");
        File.WriteAllLines(path, lines);
        return path;
    }

    [Fact]
    public void Opening_a_case_over_the_free_bus_cap_is_refused_as_the_main_app_and_the_cli_do()
    {
        using var temp = new TempFolder();
        string path = BigCase(temp, Entitlement.FreeBusLimit + 1);
        var free = new LicenceGate(Entitlement.FreeDefault);

        var vm = new WorkbenchViewModel();
        Assert.False(vm.TryLoad(path, free));
        Assert.Null(vm.Project);
        Assert.Contains(free.Check(Entitlement.FreeBusLimit + 1, false).Message, vm.StatusText, StringComparison.Ordinal);

        // The refusal is the gate's: a licence with no bus cap opens the same file.
        var unlimited = new LicenceGate(Entitlement.FreeDefault with { BusLimit = 0 });
        Assert.True(vm.TryLoad(path, unlimited));
        Assert.Equal(Entitlement.FreeBusLimit + 1, vm.Project!.Model.Buses.Count);

        // A case under the cap opens on the free tier.
        Assert.True(new WorkbenchViewModel().TryLoad(CasePath("case9"), free));
    }

    // ------------------------------------------------------------------ A2: studies and colouring off the UI thread

    /// <summary>A project with a study whose definition makes the engine throw an exception its own catch does not cover (a NullReferenceException).</summary>
    private static WorkbenchViewModel LoadedWithBrokenStudy()
    {
        WorkbenchViewModel vm = Loaded();
        var project = WorkbenchProject.WithDefaultStudies("case9", Case("case9"));
        project.AddStudy(new WorkbenchStudyDefinition("broken", WorkbenchStudies.QvCurveKind, null!));
        vm.SetProject(project);
        return vm;
    }

    [Fact]
    public void A_study_that_throws_an_unexpected_exception_is_said_in_the_status_not_thrown()
    {
        WorkbenchViewModel vm = LoadedWithBrokenStudy();
        Assert.Null(vm.RunSelected("broken"));
        Assert.Contains("broken failed", vm.StatusText, StringComparison.Ordinal);
        Assert.Contains("NullReferenceException", vm.StatusText, StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_async_run_reports_an_unexpected_exception_and_clears_the_busy_flag()
    {
        WorkbenchViewModel vm = LoadedWithBrokenStudy();
        Assert.Null(await vm.RunSelectedAsync("broken"));
        Assert.Contains("broken failed", vm.StatusText, StringComparison.Ordinal);
        Assert.False(vm.IsBusy);
        // The view model is still usable afterwards.
        Assert.Equal("CONVERGED", (await vm.RunSelectedAsync("power-flow"))!.Status);
    }

    // [AvaloniaFact], not [Fact]: the view model resumes on the caller's context (ConfigureAwait(true)), so on the UI
    // thread the end of the run (IsBusy = false) cannot land between the checks below. Under a plain [Fact] there is no
    // UI context, the run's continuation finishes on a pool thread, and on a loaded machine the study could complete
    // between the busy check and the next step, so the test raced. The assertions are unchanged.
    [AvaloniaFact]
    public async Task The_async_run_returns_while_the_study_runs_and_gives_the_same_result_as_the_sync_run()
    {
        WorkbenchViewModel vm = Loaded("case300");
        Task<WorkbenchResult?> running = vm.RunSelectedAsync("n-1-security");
        Assert.False(running.IsCompleted);   // the call came back to the caller (the UI thread) while the study was still running
        Assert.True(vm.IsBusy);
        Assert.Null(await vm.RunSelectedAsync("power-flow"));   // one study at a time
        Assert.Contains("already running", vm.StatusText, StringComparison.Ordinal);

        WorkbenchResult? r = await running;
        Assert.NotNull(r);
        Assert.False(vm.IsBusy);
        Assert.Same(r, vm.Result);
        WorkbenchViewModel reference = Loaded("case300");
        reference.RunSelected("n-1-security");
        Assert.Equal(reference.Project!.ResultJson("n-1-security"), vm.Project!.ResultJson("n-1-security"));
    }

    // [AvaloniaFact], not [Fact]: the view model resumes on the caller's context (ConfigureAwait(true)), so on the UI
    // thread the end of the run (IsBusy = false) cannot land between the checks below. Under a plain [Fact] there is no
    // UI context, the run's continuation finishes on a pool thread, and on a loaded machine the study could complete
    // between the busy check and the next step, so the test raced. The assertions are unchanged.
    [AvaloniaFact]
    public async Task A_result_for_a_project_replaced_while_it_ran_is_dropped()
    {
        WorkbenchViewModel vm = Loaded("case300");
        Task<WorkbenchResult?> running = vm.RunSelectedAsync("n-1-security");
        vm.SetProject("case9", Case("case9"));
        Assert.Null(await running);
        Assert.Empty(vm.Project!.Results);
        Assert.Contains("dropped", vm.StatusText, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Colour_by_result_runs_async_and_matches_the_sync_colouring()
    {
        var sync = new WorkbenchViewModel();
        sync.SetProject("case9", Case("case9"));
        sync.Diagram.ColourByPowerFlow();
        var vm = new WorkbenchViewModel();
        vm.SetProject("case9", Case("case9"));
        await vm.Diagram.ColourByPowerFlowAsync();
        Assert.False(vm.Diagram.IsBusy);
        Assert.Equal(SldColourMode.Loading, vm.Diagram.ColourMode);
        Assert.Equal(sync.Diagram.Colours!.Symbols, vm.Diagram.Colours!.Symbols);
        Assert.Equal(sync.Diagram.Colours.Connections, vm.Diagram.Colours.Connections);
    }

    [AvaloniaFact]
    public void The_run_button_shows_an_unexpected_failure_in_the_status_line_and_does_not_throw()
    {
        var w = new WorkbenchWindow(LoadedWithBrokenStudy());
        w.Show();
        try
        {
            w.FindControl<ComboBox>("StudyPicker")!.SelectedItem = "broken";
            w.FindControl<Button>("RunButton")!.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            for (int i = 0; i < 100 && (w.ViewModel.IsBusy || !w.FindControl<TextBlock>("StatusLine")!.Text!.Contains("failed", StringComparison.Ordinal)); i++)
            {
                Dispatcher.UIThread.RunJobs();
                Thread.Sleep(50);
            }
            Assert.Contains("broken failed", w.FindControl<TextBlock>("StatusLine")!.Text, StringComparison.Ordinal);
            Assert.True(w.FindControl<Button>("RunButton")!.IsEnabled);
        }
        finally { w.Close(); }
    }

    // ------------------------------------------------------------------ A3: the project model follows the diagram

    private static string CopyOfUkCase(TempFolder temp)
    {
        string dir = temp.File("uk");
        Directory.CreateDirectory(dir);
        foreach (string f in Directory.EnumerateFiles(Path.Combine(RepoRoot(), "data", "uk"), "*.json", SearchOption.TopDirectoryOnly))
            File.Copy(f, Path.Combine(dir, Path.GetFileName(f)));
        return dir;
    }

    private static double TotalLoadMw(GridModel m) => m.Buses.Sum(b => b.Pd);

    [Fact]
    public void A_diagram_edit_rebuilds_the_project_model_so_tables_and_studies_use_the_edited_network()
    {
        using var temp = new TempFolder();
        var vm = new WorkbenchViewModel();
        Assert.True(vm.TryLoad(CopyOfUkCase(temp)), vm.StatusText);
        GridModel before = vm.Project!.Model;
        double loadBefore = TotalLoadMw(before);
        int busesBefore = before.Buses.Count;
        SldViewModel d = vm.Diagram;
        Assert.True(d.CanEdit);

        vm.RunSelected("power-flow");
        Assert.NotEmpty(vm.Project.Results);

        Assert.True(d.AdoptLayout().Ok, d.StatusText);
        d.Select("bus/" + d.Model!.Nodes[2].Key);
        Assert.True(d.AddBusBelowSelected().Ok, d.StatusText);
        d.Select("bus/" + d.Model!.Nodes[^1].Key);
        Assert.True(d.AddLoadToSelected(15, 4).Ok, d.StatusText);

        // The project now describes the edited network, as the diagram does.
        GridModel after = vm.Project!.Model;
        Assert.NotSame(before, after);
        Assert.Equal(busesBefore + 1, after.Buses.Count);
        Assert.Equal(loadBefore + 15, TotalLoadMw(after), 6);
        Assert.Equal(d.Model!.Nodes.Count, after.Buses.Count);
        Assert.Contains("rebuilt from the diagram edit", vm.StatusText, StringComparison.Ordinal);
        vm.SelectNode("model/bus");
        Assert.Equal(busesBefore + 1, vm.Table!.TotalRows);

        // Results of the old network are not shown as if they were the new one's; the studies are still there and run on the new model.
        Assert.Empty(vm.Project.Results);
        Assert.Null(vm.Result);
        Assert.Equal(["power-flow", "n-1-security", "qv-curve"], vm.Project.Studies.Select(x => x.Id));
        WorkbenchResult r = vm.RunSelected("power-flow")!;
        Assert.Equal(after.Buses.Count, r.Table("buses")!.Rows.Count);

        // Undo takes the model back with the diagram.
        Assert.True(d.Undo().Ok);
        Assert.True(d.Undo().Ok);
        Assert.Equal(loadBefore, TotalLoadMw(vm.Project.Model), 6);
        Assert.Equal(d.Model!.Nodes.Count, vm.Project.Model.Buses.Count);
    }

    [Fact]
    public void Saving_the_case_leaves_the_model_and_its_results_alone_and_the_saved_files_load_to_the_same_network()
    {
        using var temp = new TempFolder();
        var vm = new WorkbenchViewModel();
        string dir = CopyOfUkCase(temp);
        Assert.True(vm.TryLoad(dir), vm.StatusText);
        SldViewModel d = vm.Diagram;
        Assert.True(d.AdoptLayout().Ok, d.StatusText);
        d.Select("bus/" + d.Model!.Nodes[2].Key);
        Assert.True(d.AddLoadToSelected(15, 4).Ok, d.StatusText);
        vm.RunSelected("power-flow");
        GridModel edited = vm.Project!.Model;

        Assert.True(d.Save()!.Saved, d.StatusText);
        Assert.Same(edited, vm.Project.Model);
        Assert.NotEmpty(vm.Project.Results);

        // What is on disk is the network the tables and studies are on.
        GridModel onDisk = GridSim.Core.IO.Json.JsonGridLoader.LoadDirectory(dir);
        Assert.Equal(TotalLoadMw(onDisk), TotalLoadMw(edited), 6);
        Assert.Equal(onDisk.Buses.Count, edited.Buses.Count);
        Assert.Equal(onDisk.Branches.Count, edited.Branches.Count);
        Assert.Equal(onDisk.AllGenerators.Select(g => g.Pg), edited.AllGenerators.Select(g => g.Pg));
    }

    [Fact]
    public void Colour_by_result_uses_the_edited_network()
    {
        using var temp = new TempFolder();
        var vm = new WorkbenchViewModel();
        Assert.True(vm.TryLoad(CopyOfUkCase(temp)), vm.StatusText);
        Assert.True(vm.Diagram.AdoptLayout().Ok);
        vm.Diagram.Select("bus/" + vm.Diagram.Model!.Nodes[2].Key);
        Assert.True(vm.Diagram.AddLoadToSelected(15, 4).Ok);
        Assert.Same(vm.Diagram.Document!.Model, vm.Diagram.Model);
        Assert.Equal(vm.Diagram.Model!.Nodes.Count, vm.Project!.Model.Buses.Count);
        Assert.Equal(TotalLoadMw(vm.Diagram.Model.Grid!) , TotalLoadMw(vm.Project.Model), 6);
    }

    // ------------------------------------------------------------------ the window

    [AvaloniaFact]
    public void The_window_lists_the_tree_the_table_and_the_selected_study()
    {
        WorkbenchViewModel vm = Loaded();
        var w = new WorkbenchWindow(vm);
        w.Show();
        try
        {
            Dispatcher.UIThread.RunJobs();
            ListBox tree = w.FindControl<ListBox>("TreeList")!;
            Assert.Equal(vm.TreeRows.Select(r => r.Text), tree.Items.Cast<string>());
            ListBox table = w.FindControl<ListBox>("TableList")!;
            Assert.Equal(vm.TableLines, table.Items.Cast<string>());
            Assert.Equal(2 + 9, table.Items.Count);   // header, rule, nine buses

            // Choose the generators in the tree: the model table switches kind.
            int gens = vm.TreeRows.ToList().FindIndex(r => r.Node.Id == "model/generator");
            tree.SelectedIndex = gens;
            Dispatcher.UIThread.RunJobs();
            Assert.Equal(ModelElementKind.Generator, vm.TableKind);
            Assert.Equal(2 + 3, w.FindControl<ListBox>("TableList")!.Items.Count);

            // Run the power flow with the button.
            ComboBox picker = w.FindControl<ComboBox>("StudyPicker")!;
            picker.SelectedItem = "power-flow";
            w.FindControl<Button>("RunButton")!.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Dispatcher.UIThread.RunJobs();
            Assert.Equal("CONVERGED", vm.Result!.Status);
            ListBox results = w.FindControl<ListBox>("ResultList")!;
            Assert.Equal(vm.ResultLines, results.Items.Cast<string>());
            Assert.Contains("Status:  CONVERGED", results.Items.Cast<string>());

            // The plot control holds the Core plot and paints it.
            PlotControl plot = w.FindControl<PlotControl>("PlotView")!;
            Assert.Same(vm.Plot, plot.Spec);
            w.FindControl<TabControl>("WorkbenchTabs")!.SelectedItem = w.FindControl<TabItem>("PlotTabItem");
            Dispatcher.UIThread.RunJobs();
            w.UpdateLayout();
            plot.InvalidateVisual();
            w.CaptureRenderedFrame()?.Dispose();
            w.CaptureRenderedFrame()?.Dispose();
            Assert.True(plot.IsEffectivelyVisible);
            Assert.Equal(vm.Plot!.Panels.Sum(p => p.Items.Count), plot.PaintedItems);
        }
        finally { w.Close(); }
    }

    [AvaloniaFact]
    public void The_report_tab_generates_and_shows_the_report()
    {
        WorkbenchViewModel vm = Loaded();
        var w = new WorkbenchWindow(vm);
        w.Show();
        try
        {
            vm.RunSelected("power-flow");
            w.FindControl<ComboBox>("FormatPicker")!.SelectedItem = "Csv";
            w.FindControl<Button>("GenerateButton")!.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Dispatcher.UIThread.RunJobs();
            Assert.Equal(WorkbenchReportFormat.Csv, vm.ReportFormat);
            Assert.StartsWith("# Solution summary", w.FindControl<TextBox>("ReportBox")!.Text, StringComparison.Ordinal);
            Assert.Equal(vm.ReportText, w.FindControl<TextBox>("ReportBox")!.Text);
        }
        finally { w.Close(); }
    }

    [AvaloniaFact]
    public void An_open_with_a_bad_path_says_so_and_a_good_path_loads()
    {
        var w = new WorkbenchWindow(new WorkbenchViewModel());
        w.Show();
        try
        {
            w.FindControl<TextBox>("PathBox")!.Text = Path.Combine(RepoRoot(), "no-such.m");
            w.FindControl<Button>("OpenButton")!.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Dispatcher.UIThread.RunJobs();
            Assert.Contains("Cannot open", w.FindControl<TextBlock>("StatusLine")!.Text, StringComparison.Ordinal);
            Assert.Empty(w.FindControl<ListBox>("TableList")!.Items);

            w.FindControl<TextBox>("PathBox")!.Text = CasePath("case9");
            w.FindControl<Button>("OpenButton")!.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Dispatcher.UIThread.RunJobs();
            Assert.Equal(11, w.FindControl<ListBox>("TableList")!.Items.Count);
        }
        finally { w.Close(); }
    }

    // ------------------------------------------------------------------ the plot control

    [AvaloniaFact]
    public void The_plot_control_paints_every_geometry_item_of_the_core_plot()
    {
        PlotSpec spec = PlotBuilders.EigenvaluePlane("eig", "Eigenvalues", [-1, -1], [2, -2]);
        var host = new Window { Width = 700, Height = 460, Content = new PlotControl { Spec = spec } };
        host.Show();
        try
        {
            Dispatcher.UIThread.RunJobs();
            host.CaptureRenderedFrame()?.Dispose();
            PlotControl pc = (PlotControl)host.Content!;
            Assert.Equal(spec.Panels.Sum(p => p.Items.Count), pc.PaintedItems);
            Assert.Equal(spec.Width, pc.DesiredSize.Width);
            Assert.Equal(spec.Height, pc.DesiredSize.Height);
            pc.Spec = null;
            host.CaptureRenderedFrame()?.Dispose();
            Assert.Equal(0, pc.PaintedItems);
        }
        finally { host.Close(); }
    }

    // ------------------------------------------------------------------ the main window is untouched

    [AvaloniaFact]
    public void The_workbench_is_a_window_of_its_own_and_adds_no_tab_to_the_main_window()
    {
        // The main window's tab strip is pinned (TabsTests); the key that opens the workbench is a tunnelled key handler,
        // not a registry command or a tab. This checks the handler's public entry opens a window on the loaded model.
        var w = WorkbenchWindow.Open(null, "unit", Case("case9"));
        try
        {
            Assert.Equal("unit", w.ViewModel.Project!.Name);
            Assert.Equal(9, w.ViewModel.Table!.TotalRows);
        }
        finally { w.Close(); }
    }
}
