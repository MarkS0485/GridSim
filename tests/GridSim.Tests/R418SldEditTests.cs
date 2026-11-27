using System.Text;
using System.Text.Json.Nodes;
using GridSim.Core.Analysis;
using GridSim.Core.IO.Json;
using GridSim.Core.Model;
using GridSim.Core.Model.Substations;
using GridSim.Core.PowerFlow;
using GridSim.Core.Presentation.Workbench.Sld;
using GridSim.Tests.Substations;
using Xunit;
using static GridSim.Tests.Editor.EditorTestSupport;

namespace GridSim.Tests;

/// <summary>
/// RC4 R4-18, the diagram's edit commands: each is a transaction through the case engine (the lossless text patch, the schema check,
/// the real case loader), undo and redo are command inversions that restore the bytes, a refused command changes nothing, and the
/// switch-state command on a node-breaker file changes exactly one value, after which the projected case solves to its closed form.
/// </summary>
public sealed class R418SldEditTests
{
    private static SldCaseDocument Open(TempCopy t) => SldCaseDocument.Open(t.Dir, Editor.CaseSessionTests.Unlimited());

    private static JsonArray Buses(string json) => JsonNode.Parse(json)!.AsArray();

    // ------------------------------------------------------------------------------------------------------------------------ layout

    [Fact]
    public void Moving_a_symbol_changes_only_the_x_and_y_of_its_bus_record()
    {
        using var t = new TempCopy(CaseDir("uk"));
        SldCaseDocument d = Open(t);
        Dictionary<string, (byte[] Bytes, DateTime Mtime)> snap = t.Snapshot();
        JsonArray before = Buses(File.ReadAllText(Path.Combine(t.Dir, "buses.json")));
        int bus = d.Model.Nodes[3].Key;

        SldCommandResult r = d.Execute(new SldMoveBus(bus, 0.123456789, 0.5));
        Assert.True(r.Ok && r.Changed, r.Message);
        Assert.True(d.IsDirty);
        Assert.Equal("move bus " + bus, d.UndoDescription);

        // The text diff oracle: only buses.json differs, and in it only x and y of that one record.
        IReadOnlyList<(string File, byte[] Bytes)> changed = d.ChangedFiles();
        Assert.Equal(new[] { "buses.json" }, changed.Select(c => c.File));
        JsonArray after = Buses(Encoding.UTF8.GetString(changed[0].Bytes));
        Assert.Equal(before.Count, after.Count);
        for (int i = 0; i < before.Count; i++)
        {
            var a = (JsonObject)before[i]!.DeepClone();
            var b = (JsonObject)after[i]!.DeepClone();
            if (i == 3)
            {
                Assert.Equal("0.123457", b["x"]!.ToJsonString());      // six decimals, the layout's own precision
                Assert.Equal("0.5", b["y"]!.ToJsonString());
                a.Remove("x"); a.Remove("y"); b.Remove("x"); b.Remove("y");
            }
            Assert.True(JsonNode.DeepEquals(a, b), $"bus record {i} differs beyond the moved bus's x and y");
        }
        // Nothing was written to disk by an edit (only Save writes), and every other file is untouched.
        foreach (KeyValuePair<string, (byte[] Bytes, DateTime Mtime)> f in snap)
            Assert.True(File.ReadAllBytes(Path.Combine(t.Dir, f.Key)).AsSpan().SequenceEqual(f.Value.Bytes), f.Key);

        // Undo restores the very bytes, redo puts the move back.
        Assert.True(d.Undo().Ok);
        Assert.False(d.IsDirty);
        Assert.Empty(d.ChangedFiles());
        Assert.True(d.Redo().Ok);
        Assert.Equal(new[] { "buses.json" }, d.ChangedFiles().Select(c => c.File));
    }

    [Fact]
    public void Adopting_the_automatic_layout_writes_every_bus_in_one_undo_step_and_the_diagram_then_follows_the_case()
    {
        using var t = new TempCopy(CaseDir("uk"));
        SldCaseDocument d = Open(t);
        SldDiagram auto = SldDiagrams.Build(d.Model, SldLayoutMode.Auto);
        Assert.True(d.Execute(new SldAdoptLayout()).Changed);
        Assert.Equal(1, new[] { d.UndoDescription }.Count(x => x == "adopt the automatic layout"));

        SldDiagram placed = SldDiagrams.Build(d.Model, SldLayoutMode.Case);
        Assert.Equal(SldLayoutMode.Case, placed.Mode);
        // The adopted diagram is the automatic one, shifted to the case's origin and rounded to 6 decimals of the layout scale (0.0005 units each way at most... x2000).
        double dx = placed.Geometry.Symbol(d.Model.Nodes[0].Id)!.Box.CentreX - auto.Geometry.Symbol(d.Model.Nodes[0].Id)!.Box.CentreX;
        double dy = placed.Geometry.Symbol(d.Model.Nodes[0].Id)!.Box.CentreY - auto.Geometry.Symbol(d.Model.Nodes[0].Id)!.Box.CentreY;
        foreach (SldNode n in d.Model.Nodes)
        {
            SldBox a = auto.Geometry.Symbol(n.Id)!.Box, p = placed.Geometry.Symbol(n.Id)!.Box;
            Assert.InRange(Math.Abs(p.CentreX - a.CentreX - dx), 0, 0.01);
            Assert.InRange(Math.Abs(p.CentreY - a.CentreY - dy), 0, 0.01);
        }
        Assert.Empty(placed.Geometry.FindOverlaps());

        Assert.True(d.Undo().Ok);
        Assert.Empty(d.ChangedFiles());
        Assert.True(d.Redo().Ok);
        Assert.Single(d.ChangedFiles());
    }

    [Fact]
    public void Moving_one_bus_leaves_every_other_busbar_where_it_was()
    {
        using var t = new TempCopy(CaseDir("uk"));
        SldCaseDocument d = Open(t);
        d.Execute(new SldAdoptLayout());
        SldGeometry g0 = SldDiagrams.Build(d.Model, SldLayoutMode.Case).Geometry;
        int moved = d.Model.Nodes[5].Key;
        SldBox old = g0.Symbol("bus/" + moved)!.Box;
        Assert.True(d.Execute(new SldMoveBus(moved, (old.CentreX + 300) / 2000, (old.CentreY + 200) / 2000)).Changed);
        SldGeometry g1 = SldDiagrams.Build(d.Model, SldLayoutMode.Case).Geometry;
        foreach (SldNode n in d.Model.Nodes.Where(n => n.Key != moved))
            Assert.Equal(g0.Symbol(n.Id)!.Box, g1.Symbol(n.Id)!.Box);
        SldBox now = g1.Symbol("bus/" + moved)!.Box;
        Assert.Equal(old.CentreX + 300, now.CentreX, 2);
        Assert.Equal(old.CentreY + 200, now.CentreY, 2);
        // The bijection still holds after the edit.
        Assert.Equal(d.Model.ExpectedIds().OrderBy(x => x, StringComparer.Ordinal), g1.Symbols.Select(s => s.Id).OrderBy(x => x, StringComparer.Ordinal));
    }

    // -------------------------------------------------------------------------------------------------------------------- structure

    [Fact]
    public void Adding_a_bus_with_its_line_is_one_valid_undoable_step()
    {
        using var t = new TempCopy(CaseDir("uk"));
        SldCaseDocument d = Open(t);
        int buses = d.Model.Nodes.Count, branches = d.Grid.Branches.Count, newId = d.Model.Nodes.Max(n => n.Key) + 1;
        int from = d.Model.Nodes[2].Key;
        double kv = d.Model.Nodes[2].BaseKv;

        SldCommandResult r = d.Execute(new SldAddBus(null, kv, 0.4, 0.6, "NEW", from, SldBranchKind.Line, 0.01, 0.1, 500));
        Assert.True(r.Ok && r.Changed, r.Message + " " + string.Join(" ", r.Problems));
        Assert.Equal(buses + 1, d.Model.Nodes.Count);
        Assert.Equal(branches + 1, d.Grid.Branches.Count);
        Assert.Contains(d.Grid.Buses, b => b.Id == newId && b.X == 0.4 && b.Y == 0.6 && b.Name == "NEW");
        Assert.Contains(d.Grid.Branches, b => b.From == from && b.To == newId && b.R == 0.01 && b.X == 0.1 && b.RateAMva == 500);
        // The step is one undo level although it wrote two records.
        Assert.Equal("add bus", d.UndoDescription);
        Assert.True(d.Undo().Ok);
        Assert.Equal(buses, d.Model.Nodes.Count);
        Assert.Empty(d.ChangedFiles());
        Assert.False(d.CanUndo);
        Assert.True(d.Redo().Ok);
        Assert.Equal(buses + 1, d.Model.Nodes.Count);
    }

    [Fact]
    public void Adding_a_generator_a_load_and_a_branch_changes_the_case_by_exactly_that_element()
    {
        using var t = new TempCopy(CaseDir("uk"));
        SldCaseDocument d = Open(t);
        int bus = d.Model.Nodes.First(n => !d.Model.Elements.Any(e => e.Id == "load/" + n.Key)).Key;   // a bus with no demand yet
        int other = d.Model.Nodes.First(n => n.Key != bus).Key;
        int gens = d.Grid.AllGenerators.Count, branches = d.Grid.Branches.Count;
        double pd = d.Grid.Buses.Sum(b => b.Pd);

        Assert.True(d.Execute(new SldAddGenerator(bus, 200, 120, "GNEW")).Changed);
        Assert.Equal(gens + 1, d.Grid.AllGenerators.Count);
        Assert.Contains(d.Grid.AllGenerators, g => g.Bus == bus && g.Name == "GNEW" && g.Pg == 120 && g.MBase == 200);
        Assert.Contains(d.Model.Elements, e => e.Kind == SldKind.Generator && e.Label == "GNEW");

        Assert.True(d.Execute(new SldAddLoad(bus, 33, 7, "LNEW")).Changed);
        Assert.Equal(pd + 33, d.Grid.Buses.Sum(b => b.Pd), 9);
        Assert.Contains(d.Model.Elements, e => e.Id == "load/" + bus);

        Assert.True(d.Execute(new SldAddBranch(SldBranchKind.Transformer, bus, other, 0.002, 0.05, 300, "TNEW")).Changed);
        Assert.Equal(branches + 1, d.Grid.Branches.Count);
        Assert.Contains(d.Grid.Branches, b => b.Name == "TNEW" && b.IsTransformer && b.RateAMva == 300);

        // Three commands, three undo steps, and the files are back as opened.
        for (int i = 0; i < 3; i++) Assert.True(d.Undo().Ok);
        Assert.Empty(d.ChangedFiles());
        Assert.Equal(gens, d.Grid.AllGenerators.Count);
    }

    [Fact]
    public void Deleting_elements_maps_each_diagram_id_to_its_own_record()
    {
        using var t = new TempCopy(CaseDir("uk"));
        SldCaseDocument d = Open(t);
        int bus = d.Model.Nodes[4].Key, other = d.Model.Nodes[1].Key;

        // A new line, then delete it by its diagram id; the case is as it was (bytes included).
        Assert.True(d.Execute(new SldAddBranch(SldBranchKind.Line, bus, other, 0.01, 0.1, 0, "PAR")).Changed);
        SldElement par = d.Model.Elements.Single(e => e.Label == "PAR");
        Assert.True(d.Execute(new SldDelete(par.Id)).Changed);
        Assert.DoesNotContain(d.Model.Elements, e => e.Label == "PAR");
        Assert.DoesNotContain(d.ChangedFiles(), f => f.File == "transmission.json");   // add then delete: the file is byte-identical to what was opened

        // A bus's demand: delete load/{bus}; its Pd is gone, the file keeps the rest.
        string loadId = d.Model.Elements.First(e => e.Kind == SldKind.Load).Id;
        int loadBus = int.Parse(loadId[5..]);
        Assert.True(d.Execute(new SldDelete(loadId)).Changed);
        Assert.Equal(0, d.Grid.Buses.Single(b => b.Id == loadBus).Pd);
        Assert.DoesNotContain(d.Model.Elements, e => e.Id == loadId);

        // A generator that is not the only one.
        string genId = d.Model.Elements.Last(e => e.Kind == SldKind.Generator).Id;
        int gens = d.Grid.AllGenerators.Count;
        SldCommandResult dg = d.Execute(new SldDelete(genId));
        if (dg.Ok) Assert.Equal(gens - 1, d.Grid.AllGenerators.Count);
        else Assert.Contains("slack", dg.Message + string.Join(" ", dg.Problems), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void A_refused_command_changes_neither_the_documents_nor_the_history()
    {
        using var t = new TempCopy(CaseDir("uk"));
        SldCaseDocument d = Open(t);
        Assert.True(d.Execute(new SldMoveBus(d.Model.Nodes[0].Key, 0.3, 0.3)).Changed);   // give the history something
        long rev = d.Revision;
        IReadOnlyList<(string File, byte[] Bytes)> state = d.ChangedFiles();
        int busCount = d.Model.Nodes.Count;
        string undo = d.UndoDescription!;

        void Unchanged(SldCommandResult r)
        {
            Assert.False(r.Ok);
            Assert.False(r.Changed);
            Assert.Equal(rev, d.Revision);
            Assert.Equal(undo, d.UndoDescription);
            Assert.False(d.CanRedo);
            Assert.Equal(busCount, d.Model.Nodes.Count);
            IReadOnlyList<(string File, byte[] Bytes)> now = d.ChangedFiles();
            Assert.Equal(state.Select(s => s.File), now.Select(s => s.File));
            for (int i = 0; i < now.Count; i++) Assert.True(now[i].Bytes.AsSpan().SequenceEqual(state[i].Bytes));
        }

        // A bus on its own is not a valid case (the loader refuses an island with no slack): refused by the check, rolled back.
        SldCommandResult island = d.Execute(new SldAddBus(null, 132, 0.5, 0.5));
        Unchanged(island);
        Assert.Contains("no longer be valid", island.Message, StringComparison.Ordinal);
        // A bus that anything refers to cannot be deleted.
        Unchanged(d.Execute(new SldDelete("bus/" + d.Model.Nodes[0].Key)));
        // A branch from a bus to itself, to a bus that is not there, a bus id that is not there, a non-number, a switch in a bus-branch case.
        Unchanged(d.Execute(new SldAddBranch(SldBranchKind.Line, d.Model.Nodes[0].Key, d.Model.Nodes[0].Key, 0.01, 0.1)));
        Unchanged(d.Execute(new SldAddBranch(SldBranchKind.Line, d.Model.Nodes[0].Key, 99999, 0.01, 0.1)));
        Unchanged(d.Execute(new SldMoveBus(99999, 0.1, 0.1)));
        Unchanged(d.Execute(new SldMoveBus(d.Model.Nodes[0].Key, double.NaN, 0.1)));
        Unchanged(d.Execute(new SldDelete("nonsense")));
        Unchanged(d.Execute(new SldSetSwitch("CB", true)));
    }

    // ----------------------------------------------------------------------------------------------------------- save and reload

    [Fact]
    public void Edit_save_reload_gives_the_same_case_and_the_same_diagram()
    {
        using var t = new TempCopy(CaseDir("uk"));
        SldCaseDocument d = Open(t);
        Assert.True(d.Execute(new SldAdoptLayout()).Changed);
        Assert.True(d.Execute(new SldMoveBus(d.Model.Nodes[2].Key, 0.31, 0.42)).Changed);
        Assert.True(d.Execute(new SldAddBus(null, d.Model.Nodes[2].BaseKv, 0.6, 0.7, "RT", d.Model.Nodes[2].Key)).Changed);
        Assert.True(d.Execute(new SldAddLoad(d.Model.Nodes[^1].Key, 12, 2, "LRT")).Changed);
        string hash = SldDiagrams.Build(d.Model, SldLayoutMode.Case).Geometry.Sha256();
        GridModel inMemory = d.Grid;

        SldSaveResult s = d.Save();
        Assert.True(s.Saved, string.Join("\n", s.Lines));
        Assert.False(d.IsDirty);

        // Reload from disk with a new session: the same model, the same diagram.
        SldCaseDocument back = SldCaseDocument.Open(t.Dir, Editor.CaseSessionTests.Unlimited());
        Assert.Equal(inMemory.Buses, back.Grid.Buses);
        Assert.Equal(inMemory.Branches, back.Grid.Branches);
        Assert.Equal(inMemory.AllGenerators, back.Grid.AllGenerators);
        Assert.Equal(hash, SldDiagrams.Build(back.Model, SldLayoutMode.Case).Geometry.Sha256());
        // And the real loader, the one every verb uses, loads the saved case.
        GridModel loaded = JsonGridLoader.LoadDirectory(t.Dir, dispatch: false);
        Assert.Equal(inMemory.Buses, loaded.Buses);
        Assert.Equal(inMemory.Branches.Count, loaded.Branches.Count);
    }

    [Fact]
    public void A_working_copy_is_edited_and_saved_without_touching_the_original_and_is_removed_on_dispose()
    {
        using var original = new TempCopy(CaseDir("uk"));
        Dictionary<string, (byte[] Bytes, DateTime Mtime)> before = original.Snapshot();
        SldCaseDocument d = SldCaseDocument.OpenWorkingCopy(original.Dir, Editor.CaseSessionTests.Unlimited());
        Assert.Equal(Path.GetFileName(original.Dir), d.Name);
        Assert.NotEqual(Path.GetFullPath(original.Dir), Path.GetFullPath(d.Folder));
        Assert.True(d.Execute(new SldAdoptLayout()).Changed);
        Assert.True(d.Save().Saved);
        foreach (KeyValuePair<string, (byte[] Bytes, DateTime Mtime)> f in before)
        {
            Assert.True(File.ReadAllBytes(Path.Combine(original.Dir, f.Key)).AsSpan().SequenceEqual(f.Value.Bytes), f.Key);
            Assert.Equal(f.Value.Mtime, File.GetLastWriteTimeUtc(Path.Combine(original.Dir, f.Key)));
        }
        string copy = d.Folder;
        Assert.True(Directory.Exists(copy));
        d.Dispose();
        Assert.False(Directory.Exists(copy));
    }

    // ------------------------------------------------------------------------------------------------------------ switch states

    private static string Text(SubstationModel m) => SubstationJson.Write(m);

    [Fact]
    public void A_switch_state_edit_changes_exactly_one_value_and_undo_restores_the_text()
    {
        string canonical = Text(SubstationFixtures.TwoFeed());
        SldSubstationDocument d = SldSubstationDocument.FromText("two-feed.json", canonical);
        Assert.Equal(canonical, d.Text);

        SldCommandResult r = d.Execute(new SldSetSwitch("CPL", true));
        Assert.True(r.Ok && r.Changed, r.Message);
        string[] a = canonical.Split('\n'), b = d.Text.Split('\n');
        Assert.Equal(a.Length, b.Length);
        int[] differ = Enumerable.Range(0, a.Length).Where(i => a[i] != b[i]).ToArray();
        Assert.Single(differ);                                                  // one line, one value
        Assert.Contains("normalOpen", a[differ[0]], StringComparison.Ordinal);
        Assert.Equal(a[differ[0]].Replace("false", "true"), b[differ[0]]);
        string owner = Enumerable.Range(0, differ[0] + 1).Reverse().Select(i => a[i]).First(l => l.Contains("\"id\":", StringComparison.Ordinal));
        Assert.Contains("\"CPL\"", owner, StringComparison.Ordinal);

        Assert.True(d.Undo().Ok);
        Assert.Equal(canonical, d.Text);
        Assert.False(d.IsDirty);
        Assert.True(d.Redo().Ok);
        Assert.Contains("true", b[differ[0]], StringComparison.Ordinal);
        Assert.Equal(string.Join("\n", b), d.Text);
    }

    [Fact]
    public void The_switch_command_refuses_what_it_cannot_do_and_says_when_nothing_changes()
    {
        SldSubstationDocument d = SldSubstationDocument.FromText("t.json", Text(SubstationFixtures.TwoFeed()));
        Assert.False(d.Execute(new SldSetSwitch("NOPE", true)).Ok);
        Assert.False(d.Execute(new SldMoveBus(1, 0.1, 0.1)).Ok);          // the file has no layout
        SldCommandResult same = d.Execute(new SldSetSwitch("CPL", false)); // already closed
        Assert.True(same.Ok);
        Assert.False(same.Changed);
        Assert.False(d.CanUndo);
        Assert.False(d.Save().Saved);                                     // not opened from a file
    }

    [Fact]
    public void A_switch_file_is_saved_in_place_with_one_changed_value()
    {
        string path = TestTemp.Under("gridsim-r418-" + Guid.NewGuid().ToString("N") + ".json");
        try
        {
            string canonical = Text(SubstationFixtures.TwoFeed());
            File.WriteAllText(path, canonical, new UTF8Encoding(false));
            SldSubstationDocument d = SldSubstationDocument.Open(path);
            Assert.True(d.Execute(new SldSetSwitch("CBL1", true)).Changed);
            Assert.True(d.Save().Saved);
            Assert.False(d.IsDirty);
            Assert.Equal(d.Text, File.ReadAllText(path));
            Assert.False(File.Exists(path + ".gridsim-new"));
            Assert.Equal("No changes to save.", d.Save().Message);
            Assert.True(SubstationJson.Read(File.ReadAllText(path)).AllSwitches.Single(s => s.Id == "CBL1").NormalOpen);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void Toggling_a_switch_keeps_every_position_and_the_projected_case_solves_to_the_closed_form()
    {
        SldSubstationDocument d = SldSubstationDocument.FromText("two-feed.json", Text(SubstationFixtures.TwoFeed()));
        SldGeometry g0 = SldDiagrams.Build(d.Model, SldLayoutMode.Auto).Geometry;
        // The nine nodes, seven switches, two lines, two generators, one load: every object once.
        Assert.Equal(9 + 7 + 2 + 2 + 1, g0.Symbols.Count);
        Assert.Empty(g0.FindCrossings());
        Assert.Empty(g0.FindOverlaps());

        // Coupler CLOSED (as written). SA's busbars are one bus; the two lines are in parallel, x = 0.1 each, so 0.05 pu. The 80 MW (0.8 pu,
        // 0 MVAr) load at the far end of a lossless line: V2 = V1 cos d (no reactive power delivered) and P = V1 V2 sin d / X, so
        // sin 2d = 2 P X = 0.08, d = asin(0.08) / 2 and V2 = cos d.
        (double vmClosed, double vaClosed) = Solve(d.Substations, "Y");
        double dClosed = Math.Asin(0.08) / 2;
        Assert.Equal(Math.Cos(dClosed), vmClosed, 6);
        Assert.Equal(-dClosed * 180 / Math.PI, vaClosed, 4);

        Assert.True(d.Execute(new SldSetSwitch("CPL", true)).Changed);
        SldGeometry g1 = SldDiagrams.Build(d.Model, SldLayoutMode.Auto).Geometry;
        // Every box is where it was (the layout does not depend on switch states); only the open switch's symbol changed state.
        Assert.Equal(g0.Symbols.Select(s => s.Box), g1.Symbols.Select(s => s.Box));
        Assert.Equal(g0.Connections.Select(c => c.Points), g1.Connections.Select(c => c.Points));
        Assert.Equal(new[] { "switch/CPL" }, g1.Symbols.Where((s, i) => s.InService != g0.Symbols[i].InService).Select(s => s.Id));

        // Coupler OPEN: line 1 carries what generator A supplies (80 - 30 = 50 MW) and line 2 generator B's 30 MW, so Vy sin d1 = 0.5 * 0.1,
        // Vy sin d2 = 0.3 * 0.1 and, with no reactive demand, Vy = (cos d1 + cos d2) / 2. Solved here by fixed-point iteration.
        double vy = 1, d1 = 0, d2 = 0;
        for (int i = 0; i < 200; i++)
        {
            d1 = Math.Asin(0.05 / vy);
            d2 = Math.Asin(0.03 / vy);
            vy = (Math.Cos(d1) + Math.Cos(d2)) / 2;
        }
        (double vmOpen, double vaOpen) = Solve(d.Substations, "Y");
        Assert.Equal(vy, vmOpen, 6);
        Assert.Equal(-d1 * 180 / Math.PI, vaOpen, 4);
        Assert.NotEqual(vmClosed, vmOpen);

        // Undo is the inverse command: the closed-coupler solution again.
        Assert.True(d.Undo().Ok);
        (double vmBack, _) = Solve(d.Substations, "Y");
        Assert.Equal(vmClosed, vmBack, 12);
    }

    private static (double Vm, double VaDeg) Solve(SubstationModel m, string node)
    {
        SubstationProjectionResult p = SubstationProjection.ProjectNormal(m);
        PowerFlowResult r = SolverSelector.CreateNewton(p.Model).Solve(p.Model, ContingencyAnalysis.StudyOptions);
        Assert.True(r.Converged);
        int at = r.BusIds.ToList().IndexOf(p.BusOf(node)!.BusId);
        return (r.Vm[at], r.VaDeg[at]);
    }
}
