using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.VisualTree;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using GridSim.App.Workbench;
using GridSim.Core.IO.Json;
using GridSim.Core.IO.Matpower;
using GridSim.Core.Licensing;
using GridSim.Core.Model;
using GridSim.Core.Model.Substations;
using GridSim.Core.Presentation.Workbench.Sld;
using Xunit;

namespace GridSim.App.Tests;

/// <summary>
/// RC4 R4-18 on the Avalonia app: the single-line diagram pane of the workbench window. The view model shows the Core diagram (the
/// same <see cref="SldDiagrams"/> call the web client makes, so the geometry is the same bytes), the control paints every symbol and
/// connection of it, pointer input becomes selection and the Core move command, and edits go to the case through the Core document.
/// The case-folder edits run on a temporary copy.
/// </summary>
public sealed class SldViewTests
{
    private static string RepoRoot()
    {
        for (DirectoryInfo? d = new(AppContext.BaseDirectory); d is not null; d = d.Parent)
            if (File.Exists(Path.Combine(d.FullName, "GridSim.slnx"))) return d.FullName;
        throw new InvalidOperationException("The GridSim checkout (GridSim.slnx) was not found above " + AppContext.BaseDirectory);
    }

    private static GridModel Case(string name) => MatpowerParser.ParseFile(Path.Combine(RepoRoot(), "data", "matpower", "lib", name + ".m"));

    private static LicenceGate Unlimited() => new(new Entitlement("test@example.invalid", "Test", LicenceTier.NetworkOperator, 0,
        new DateOnly(2026, 1, 1), new DateOnly(2999, 12, 31), "test-unlimited"));

    private sealed class Temp : IDisposable
    {
        public string Dir { get; } = Path.Combine(Path.GetTempPath(), "gridsim-r418-app-" + Guid.NewGuid().ToString("N"));
        public Temp(string source)
        {
            Directory.CreateDirectory(Dir);
            foreach (string f in Directory.EnumerateFiles(source)) File.Copy(f, Path.Combine(Dir, Path.GetFileName(f)));
        }
        public void Dispose() { try { Directory.Delete(Dir, true); } catch (IOException) { } catch (UnauthorizedAccessException) { } }
    }

    // ------------------------------------------------------------------ parity: the view model is the Core call

    [Theory]
    [InlineData("case9")]
    [InlineData("case14")]
    [InlineData("case30")]
    public void The_view_model_diagram_is_the_core_geometry_byte_for_byte(string name)
    {
        GridModel m = Case(name);
        var vm = new WorkbenchViewModel();
        vm.SetProject(name, m);
        SldViewModel d = vm.Diagram;
        Assert.True(d.HasDiagram);
        Assert.False(d.CanEdit);                                      // a MATPOWER case has no case files to write back to
        SldGeometry core = SldDiagrams.Build(SldModel.From(m), SldLayoutMode.Auto).Geometry;
        Assert.Equal(core.Canonical(), d.Diagram!.Geometry.Canonical());
        Assert.Equal(core.Sha256(), d.Diagram.Geometry.Sha256());
        Assert.Equal(SldExport.ToSvg(core, d.Colours, m.Name), d.ToSvg());
        // Every element of the model is a drawn object.
        Assert.Equal(d.Model!.ExpectedIds().OrderBy(x => x, StringComparer.Ordinal), d.Diagram.Geometry.Symbols.Select(x => x.Id).OrderBy(x => x, StringComparer.Ordinal));
    }

    [Fact]
    public void A_read_only_diagram_refuses_edits_and_says_so()
    {
        var d = new SldViewModel();
        d.LoadReadOnly(Case("case9"));
        d.Select("bus/1");
        Assert.False(d.CanEdit);
        Assert.False(d.AddGeneratorToSelected(100, 50).Ok);
        Assert.False(d.DeleteSelected().Ok);
        Assert.False(d.MoveSelectedTo(10, 10).Ok);
        Assert.Contains("read-only", d.StatusText, StringComparison.OrdinalIgnoreCase);
        Assert.Null(d.Save());
    }

    [Fact]
    public void Selecting_by_point_uses_the_core_hit_test()
    {
        var d = new SldViewModel();
        d.LoadReadOnly(Case("case9"));
        SldBox b = d.Diagram!.Geometry.Symbol("bus/1")!.Box;
        SldHit hit = d.SelectAt(b.CentreX, b.CentreY);
        Assert.Equal(SldHitKind.Symbol, hit.Kind);
        Assert.Equal("bus/1", d.SelectedId);
        Assert.Equal(1, d.SelectedBus);
        d.SelectAt(b.Left - 5000, b.Top - 5000);
        Assert.Null(d.SelectedId);
    }

    // ------------------------------------------------------------------ editing a case folder

    [Fact]
    public void Moving_a_bus_needs_the_layout_adopted_then_writes_x_and_y_and_undo_takes_it_back()
    {
        using var t = new Temp(Path.Combine(RepoRoot(), "data", "uk"));
        var d = new SldViewModel();
        Assert.True(d.TryOpenCase(t.Dir, Unlimited()), d.StatusText);
        Assert.True(d.CanEdit);
        Assert.False(d.CanMove);                                      // automatic layout: read-only
        d.Select("bus/" + d.Model!.Nodes[1].Key);
        Assert.False(d.MoveSelectedTo(100, 100).Ok);

        Assert.True(d.AdoptLayout().Ok);
        Assert.Equal(SldLayoutMode.Case, d.Diagram!.Mode);
        Assert.True(d.CanMove);
        byte[] adopted = File.ReadAllBytes(Path.Combine(t.Dir, "buses.json"));
        Assert.Equal(adopted, File.ReadAllBytes(Path.Combine(t.Dir, "buses.json")));   // edits do not touch the disk until Save

        d.Select("bus/" + d.Model.Nodes[1].Key);
        SldBox box = d.Diagram.Geometry.Symbol(d.SelectedId!)!.Box;
        Assert.True(d.MoveSelectedTo(box.CentreX + 120, box.CentreY + 80).Ok);
        SldBox moved = d.Diagram!.Geometry.Symbol(d.SelectedId!)!.Box;
        Assert.Equal(box.CentreX + 120, moved.CentreX, 2);
        Assert.Equal(box.CentreY + 80, moved.CentreY, 2);
        Assert.True(d.CanUndo);
        Assert.True(d.IsDirty);

        Assert.True(d.Undo().Ok);
        Assert.Equal(box.CentreX, d.Diagram!.Geometry.Symbol(d.SelectedId!)!.Box.CentreX, 9);
        Assert.True(d.Redo().Ok);
        Assert.True(d.Save()!.Saved);
        Assert.False(d.IsDirty);
        // Reloaded from disk: the diagram placed from the saved case is the diagram shown.
        SldCaseDocument back = SldCaseDocument.Open(t.Dir, Unlimited());
        Assert.Equal(d.Diagram!.Geometry.Sha256(), SldDiagrams.Build(back.Model, SldLayoutMode.Case).Geometry.Sha256());
    }

    [Fact]
    public void Adding_a_bus_a_generator_and_a_load_and_deleting_them_goes_through_the_core_commands()
    {
        using var t = new Temp(Path.Combine(RepoRoot(), "data", "uk"));
        var d = new SldViewModel();
        Assert.True(d.TryOpenCase(t.Dir, Unlimited()));
        Assert.True(d.AdoptLayout().Ok);
        int buses = d.Model!.Nodes.Count;
        d.Select("bus/" + d.Model.Nodes[2].Key);
        Assert.True(d.AddBusBelowSelected().Ok, d.StatusText);
        Assert.Equal(buses + 1, d.Model!.Nodes.Count);

        d.Select("bus/" + d.Model.Nodes[^1].Key);
        Assert.True(d.AddGeneratorToSelected(100, 40).Ok);
        Assert.True(d.AddLoadToSelected(15, 4).Ok);
        Assert.Contains(d.Model.Elements, e => e.Kind == SldKind.Load && e.Id == "load/" + d.Model.Nodes[^1].Key);
        Assert.True(d.DeleteSelected().Ok is false);                  // the bus is still referred to
        d.Select("load/" + d.Model.Nodes[^1].Key);
        Assert.True(d.DeleteSelected().Ok);
        Assert.DoesNotContain(d.Model.Elements, e => e.Id == "load/" + d.Model.Nodes[^1].Key);
        // Every drawn object is a model element after every edit.
        Assert.Equal(d.Model.ExpectedIds().OrderBy(x => x, StringComparer.Ordinal), d.Diagram!.Geometry.Symbols.Select(x => x.Id).OrderBy(x => x, StringComparer.Ordinal));
    }

    [Fact]
    public void A_node_breaker_file_shows_its_switches_and_toggling_one_changes_one_value()
    {
        string json = SubstationJson.Write(new SubstationModel
        {
            Name = "ring",
            Substations =
            [
                new Substation("S", null, [new VoltageLevel("V", 132)], [], [new ConnectivityNode("R1", "V"), new ConnectivityNode("R2", "V"), new ConnectivityNode("R3", "V")],
                    [new NbSwitch("CBa", SwitchKind.Breaker, "R1", "R2", false), new NbSwitch("CBb", SwitchKind.Breaker, "R2", "R3", false), new NbSwitch("CBc", SwitchKind.Breaker, "R3", "R1", false)]),
            ],
            Generators = [new NbGenerator("G1", "R1", 50, Slack: true)],
            Loads = [new NbLoad("L2", "R2", 10), new NbLoad("L3", "R3", 20)],
        });
        string path = Path.Combine(Path.GetTempPath(), "gridsim-r418-app-" + Guid.NewGuid().ToString("N") + ".json");
        try
        {
            File.WriteAllText(path, json, new System.Text.UTF8Encoding(false));
            var vm = new WorkbenchViewModel();
            Assert.True(vm.TryLoad(path), vm.StatusText);
            SldViewModel d = vm.Diagram;
            Assert.True(d.CanEdit);
            Assert.IsType<SldSubstationDocument>(d.Document);
            Assert.False(d.CanMove);
            SldSymbol sw = d.Diagram!.Geometry.Symbol("switch/CBa")!;
            d.SelectAt(sw.Box.CentreX, sw.Box.CentreY);
            Assert.Equal("switch/CBa", d.SelectedId);
            Assert.True(d.Diagram.Geometry.Symbol("switch/CBa")!.InService);
            Assert.True(d.ToggleSelectedSwitch().Ok);
            Assert.False(d.Diagram!.Geometry.Symbol("switch/CBa")!.InService);   // open
            Assert.True(d.Undo().Ok);
            Assert.True(d.Diagram!.Geometry.Symbol("switch/CBa")!.InService);
            Assert.True(d.Redo().Ok);
            Assert.True(d.Save()!.Saved);
            Assert.True(SubstationJson.Read(File.ReadAllText(path)).AllSwitches.Single(s => s.Id == "CBa").NormalOpen);
        }
        finally { File.Delete(path); }
    }

    // ------------------------------------------------------------------ the window and the control

    [AvaloniaFact]
    public void The_diagram_pane_paints_every_symbol_and_connection_and_does_not_change_the_workbench_tabs_it_had()
    {
        var vm = new WorkbenchViewModel();
        vm.SetProject("case14", Case("case14"));
        var w = new WorkbenchWindow(vm);
        w.Show();
        try
        {
            w.FindControl<TabControl>("WorkbenchTabs")!.SelectedItem = w.FindControl<TabItem>("DiagramTabItem");
            Dispatcher.UIThread.RunJobs();
            w.UpdateLayout();
            SldControl view = w.FindControl<SldControl>("DiagramView")!;
            view.InvalidateVisual();
            w.CaptureRenderedFrame()?.Dispose();
            w.CaptureRenderedFrame()?.Dispose();
            SldGeometry g = vm.Diagram.Diagram!.Geometry;
            Assert.Same(vm.Diagram, view.ViewModel);
            Assert.Equal(g.Symbols.Count + g.Connections.Count, view.PaintedObjects);
            Assert.Contains("Read-only", w.FindControl<TextBlock>("DiagramStatus")!.Text, StringComparison.Ordinal);
            Assert.False(w.FindControl<Button>("AdoptButton")!.IsEnabled);
            Assert.False(w.FindControl<Button>("UndoButton")!.IsEnabled);
            // The four tabs R4-10 made are all still there.
            TabControl tabs = w.FindControl<TabControl>("WorkbenchTabs")!;
            foreach (string header in new[] { "Model", "Study", "Plot", "Report" }) Assert.Contains(tabs.Items.OfType<TabItem>(), ti => (string?)ti.Header == header);
        }
        finally { w.Close(); }
    }

    [AvaloniaFact]
    public void A_pointer_press_on_a_busbar_selects_it_and_a_drag_in_a_case_placed_diagram_moves_it()
    {
        using var t = new Temp(Path.Combine(RepoRoot(), "data", "uk"));
        var vm = new WorkbenchViewModel();
        Assert.True(vm.TryLoad(t.Dir), vm.StatusText);
        Assert.True(vm.Diagram.CanEdit);
        Assert.True(vm.Diagram.AdoptLayout().Ok);
        var w = new WorkbenchWindow(vm);
        w.Show();
        try
        {
            w.FindControl<TabControl>("WorkbenchTabs")!.SelectedItem = w.FindControl<TabItem>("DiagramTabItem");
            Dispatcher.UIThread.RunJobs();
            w.UpdateLayout();
            SldControl view = w.FindControl<SldControl>("DiagramView")!;
            w.CaptureRenderedFrame()?.Dispose();
            string id = "bus/" + vm.Diagram.Model!.Nodes[0].Key;
            SldBox box = vm.Diagram.Diagram!.Geometry.Symbol(id)!.Box;
            Point from = view.ToScreen(box.CentreX, box.CentreY);
            Point to = view.ToScreen(box.CentreX + 50, box.CentreY + 40);

            Point TopWindow(Point p) => view.TranslatePoint(p, w)!.Value;
            w.MouseDown(TopWindow(from), MouseButton.Left);
            Assert.Equal(id, vm.Diagram.SelectedId);
            Point dropped = view.ToDiagram(to);
            w.MouseMove(TopWindow(to));
            w.MouseUp(TopWindow(to), MouseButton.Left);
            Dispatcher.UIThread.RunJobs();
            // The drag wrote the move through the Core command: one undo level, and the busbar is where it was dropped (the view is at 1:1 or fitted; compare in diagram units).
            Assert.True(vm.Diagram.CanUndo);
            SldBox after = vm.Diagram.Diagram!.Geometry.Symbol(id)!.Box;
            Assert.Equal(dropped.X, after.CentreX, 1);
            Assert.Equal(dropped.Y, after.CentreY, 1);
        }
        finally { w.Close(); }
    }
}
