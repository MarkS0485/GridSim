using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Interactivity;
using Avalonia.Threading;
using GridSim.App.Workbench;
using GridSim.Core.Presentation.Workbench.Sld;
using Xunit;

namespace GridSim.App.Tests;

/// <summary>
/// A5/A6: the workbench's typed entry dialogs (generator, load, branch), the edit path they go through (the same SLD document,
/// undo and A3 rebuild as every other edit) and the File menu entry that opens the workbench.
/// </summary>
public sealed class WorkbenchEntryTests
{
    private static string RepoRoot()
    {
        for (DirectoryInfo? d = new(AppContext.BaseDirectory); d is not null; d = d.Parent)
            if (File.Exists(Path.Combine(d.FullName, "GridSim.slnx"))) return d.FullName;
        throw new InvalidOperationException("The GridSim checkout (GridSim.slnx) was not found above " + AppContext.BaseDirectory);
    }

    private static string CopyOfUkCase(TempFolder temp)
    {
        string dir = temp.File("uk");
        Directory.CreateDirectory(dir);
        foreach (string f in Directory.EnumerateFiles(Path.Combine(RepoRoot(), "data", "uk"), "*.json", SearchOption.TopDirectoryOnly))
            File.Copy(f, Path.Combine(dir, Path.GetFileName(f)));
        return dir;
    }

    private static WorkbenchViewModel Editable(TempFolder temp)
    {
        var vm = new WorkbenchViewModel();
        Assert.True(vm.TryLoad(CopyOfUkCase(temp)), vm.StatusText);
        Assert.True(vm.Diagram.AdoptLayout().Ok, vm.Diagram.StatusText);
        return vm;
    }

    private static string[] V(params string[] v) => v;

    // ------------------------------------------------------------------ typed validation

    [Fact]
    public void Generator_entries_are_validated_and_parsed_with_the_invariant_culture()
    {
        Assert.Null(SldEntryForms.TryGenerator(V("100", "50", "1.02"), out GeneratorEntry? g));
        Assert.Equal(new GeneratorEntry(100, 50, 1.02), g);
        Assert.Contains("Capacity", SldEntryForms.TryGenerator(V("abc", "50", "1"), out _), StringComparison.Ordinal);
        Assert.Contains("capacity", SldEntryForms.TryGenerator(V("100", "150", "1"), out _), StringComparison.Ordinal);
        Assert.Contains("0.5 and 1.5", SldEntryForms.TryGenerator(V("100", "50", "2"), out _), StringComparison.Ordinal);
        Assert.NotNull(SldEntryForms.TryGenerator(V("100", "50", "NaN"), out _));
        Assert.NotNull(SldEntryForms.TryGenerator(V("-5", "0", "1"), out _));
    }

    [Fact]
    public void Load_and_branch_entries_are_validated()
    {
        Assert.Null(SldEntryForms.TryLoad(V("10", "3"), out LoadEntry? l));
        Assert.Equal(new LoadEntry(10, 3), l);
        Assert.NotNull(SldEntryForms.TryLoad(V("-1", "0"), out _));
        Assert.NotNull(SldEntryForms.TryLoad(V("10", ""), out _));

        Assert.Null(SldEntryForms.TryBranch(V("line", "1", "2", "0.01", "0.1", "0.02", "250", ""), out BranchEntry? b));
        Assert.Equal(new BranchEntry(SldBranchKind.Line, 1, 2, 0.01, 0.1, 0.02, 250, 1), b);
        Assert.Null(SldEntryForms.TryBranch(V("transformer", "1", "2", "0.0", "0.08", "0.5", "", "1.05"), out BranchEntry? t));
        Assert.Equal(new BranchEntry(SldBranchKind.Transformer, 1, 2, 0, 0.08, 0, 0, 1.05), t);   // B is a line's, tap a transformer's
        Assert.Contains("two different", SldEntryForms.TryBranch(V("line", "3", "3", "0.01", "0.1", "", "", ""), out _), StringComparison.Ordinal);
        Assert.Contains("Kind", SldEntryForms.TryBranch(V("cable", "1", "2", "0.01", "0.1", "", "", ""), out _), StringComparison.Ordinal);
        Assert.Contains("whole number", SldEntryForms.TryBranch(V("line", "x", "2", "0.01", "0.1", "", "", ""), out _), StringComparison.Ordinal);
        Assert.NotNull(SldEntryForms.TryBranch(V("line", "1", "2", "0", "0", "", "", ""), out _));
        Assert.NotNull(SldEntryForms.TryBranch(V("transformer", "1", "2", "0", "0.1", "", "", "3"), out _));
    }

    [AvaloniaFact]
    public void The_dialog_refuses_a_bad_value_and_closes_with_good_ones()
    {
        var d = new EntryDialog("Add load", [new("P (MW)", "10"), new("Q (MVAr)", "3")], v => SldEntryForms.TryLoad(v, out _));
        d.Show();
        try
        {
            d.SetValue(0, "ten");
            Assert.Null(d.Accept());
            Assert.Contains("P (MW) must be a number", d.ErrorText, StringComparison.Ordinal);
            d.SetValue(0, "12.5");
            Assert.Equal(new[] { "12.5", "3" }, d.Accept());
            Assert.Null(d.ErrorText);
        }
        finally { d.Close(); }
    }

    // ------------------------------------------------------------------ edits through the dialogs

    private static async Task<string[]?> Fill(EntryDialog d, params string?[] values)
    {
        d.Show();
        for (int i = 0; i < values.Length; i++) if (values[i] is { } s) d.SetValue(i, s);
        string[]? r = d.Accept();
        Dispatcher.UIThread.RunJobs();
        await Task.Yield();
        return r;
    }

    [AvaloniaFact]
    public async Task Add_generator_and_add_load_apply_the_typed_values_and_rebuild_the_project()
    {
        using var temp = new TempFolder();
        WorkbenchViewModel vm = Editable(temp);
        var w = new WorkbenchWindow(vm);
        w.Show();
        try
        {
            SldViewModel d = vm.Diagram;
            d.Select("bus/" + d.Model!.Nodes[2].Key);
            int gensBefore = vm.Project!.Model.AllGenerators.Count;
            double loadBefore = vm.Project.Model.Buses.Sum(b => b.Pd);

            w.ShowEntryDialog = dlg => Fill(dlg, "80", "40", "1.03");
            await w.AddGeneratorAsync();
            Assert.Equal(gensBefore + 1, vm.Project!.Model.AllGenerators.Count);
            var gen = vm.Project.Model.AllGenerators[^1];
            Assert.Equal(80, gen.MBase, 6);
            Assert.Equal(40, gen.Pg, 6);
            Assert.Equal(1.03, gen.Vg, 6);

            w.ShowEntryDialog = dlg => Fill(dlg, "22", "5");
            await w.AddLoadAsync();
            Assert.Equal(loadBefore + 22, vm.Project.Model.Buses.Sum(b => b.Pd), 6);
            Assert.True(d.CanUndo);
            Assert.True(d.Undo().Ok);
            Assert.Equal(loadBefore, vm.Project!.Model.Buses.Sum(b => b.Pd), 6);

            // A cancelled dialog changes nothing.
            long revision = d.Document!.Revision;
            w.ShowEntryDialog = dlg => { dlg.Show(); dlg.Close(null); return Task.FromResult<string[]?>(null); };
            await w.AddLoadAsync();
            Assert.Equal(revision, d.Document.Revision);
        }
        finally { w.Close(); }
    }

    [AvaloniaFact]
    public async Task Add_branch_between_two_existing_buses_goes_through_the_edit_path_and_undoes()
    {
        using var temp = new TempFolder();
        WorkbenchViewModel vm = Editable(temp);
        var w = new WorkbenchWindow(vm);
        w.Show();
        try
        {
            SldViewModel d = vm.Diagram;
            int a = d.Model!.Nodes[0].Key, b = d.Model.Nodes[^1].Key;
            d.Select("bus/" + a);
            int branches = vm.Project!.Model.Branches.Count;
            w.ShowEntryDialog = dlg => Fill(dlg, "line", null, b.ToString(System.Globalization.CultureInfo.InvariantCulture), "0.02", "0.2", "0.05", "300", "");
            await w.AddBranchAsync();
            Assert.Equal(branches + 1, vm.Project!.Model.Branches.Count);   // A3: the project follows the edit
            var added = vm.Project.Model.Branches.Single(br => br.From == a && br.To == b && br.X == 0.2);
            Assert.Equal(0.02, added.R, 9);
            Assert.Equal(0.05, added.B, 9);
            Assert.Equal(300, added.RateAMva, 6);
            Assert.True(d.Undo().Ok);
            Assert.Equal(branches, vm.Project!.Model.Branches.Count);

            // A transformer carries its tap, not a B.
            w.ShowEntryDialog = dlg => Fill(dlg, "transformer", null, b.ToString(System.Globalization.CultureInfo.InvariantCulture), "0.001", "0.08", "0.5", "", "1.05");
            await w.AddBranchAsync();
            var t = vm.Project!.Model.Branches.Single(br => br.From == a && br.To == b && br.X == 0.08);
            Assert.Equal(1.05, t.TapRatio, 9);
            Assert.Equal(0, t.B, 9);

            // A refusal by the document (a bus that does not exist) is said in the diagram status.
            Assert.False(d.AddBranchBetween(new BranchEntry(SldBranchKind.Line, a, 987654, 0.01, 0.1, 0, 0, 1)).Ok);
        }
        finally { w.Close(); }
    }

    [Fact]
    public void The_new_command_fields_default_to_the_record_the_commands_always_wrote()
    {
        Assert.Equal(new SldAddBranch(SldBranchKind.Line, 1, 2, 0.01, 0.1), new SldAddBranch(SldBranchKind.Line, 1, 2, 0.01, 0.1, 0, null, 0, 1));
        Assert.Equal(new SldAddGenerator(1, 100, 50), new SldAddGenerator(1, 100, 50, null, null));
    }

    // ------------------------------------------------------------------ A6: the menu entry

    [AvaloniaFact]
    public void File_menu_has_a_Workbench_entry_that_opens_the_workbench_and_the_key_still_works()
    {
        using var temp = new TempFolder();
        var w = new MainWindow(TestShell.Options(temp));
        w.Show();
        try
        {
            MenuItem file = w.FindControl<Menu>("MenuBar")!.Items.OfType<MenuItem>().First();
            MenuItem item = file.Items.OfType<MenuItem>().Single(i => (string?)i.Tag == WorkbenchWindow.CommandId);
            Assert.Equal("Workbench...", (string?)item.Header);
            Assert.Empty(w.OwnedWindows.OfType<WorkbenchWindow>());
            item.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
            Dispatcher.UIThread.RunJobs();
            WorkbenchWindow opened = Assert.Single(w.OwnedWindows.OfType<WorkbenchWindow>());
            opened.Close();
            Assert.Equal(3, w.KeyBindings.Count);   // the shortcut stays the window's key handler: no new key binding
        }
        finally { w.Close(); }
    }
}
