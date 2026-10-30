using System.Globalization;
using System.Text;
using GridSim.Core.IO.Json;
using GridSim.Core.IO.Json.CaseEditing;
using GridSim.Core.IO.Json.Lossless;
using GridSim.Tests.Editor;
using GridSim.Tests.V2;
using GridSim.Ui.Assets.Editing;
using GridSim.Ui.Assets.Index;
using GridSim.Ui.Assets.Keys;
using GridSim.Ui.Assets.Navigation;
using GridSim.Ui.Commands;
using Xunit;

namespace GridSim.Tests.Ui;

/// <summary>
/// UI programme Phase 5b (2026-10-20): asset overlay editing in GridSim.Ui's Author mode (CaseAuthoring, FieldGridModel,
/// RecordActions, OverlayActions, AssetAuthorModel, the overlay command), on temporary copies only (UiOverlayFixture).
/// Pinned: an overlay record's field grid comes from its kind spec (params with units and enum choices, refs and the
/// parent as pickers, the kind locked) and edits through the engine; add (under a parent, attached to a model asset, at the
/// top level), duplicate, delete and rename with their refusals; CHECK after Check shows the overlay checker's problems on
/// the record; Save writes only the edited token and Save As copies the overlay files; the Author tree holds the overlay
/// records by parent and the model assets carry the overlay anchor; the overlay command is Author and desktop only.
/// </summary>
public sealed class UiOverlayAuthoringTests
{
    private const string FileA = "site.assets.json", FileB = "lines.assets.json";

    private static CaseAuthoring Open(string manifest)
        => CaseAuthoring.Open(AssetCaseSource.V2(manifest), CaseSessionTests.Unlimited(), Array.Empty<string>());

    private static Dictionary<string, byte[]> Files(string dir) =>
        Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories)
            .ToDictionary(p => Path.GetRelativePath(dir, p).Replace('\\', '/'), File.ReadAllBytes, StringComparer.Ordinal);

    // The overlay record with this id.
    private static CaseRef Ov(CaseAuthoring a, string id)
        => a.Collections().Where(c => c.Role == CaseRoles.Overlay).SelectMany(a.Records)
            .Select(r => r.Ref).First(r => a.OverlayIdentity(r).Id == id);

    // A model record of a role by its name (or a bus by its id).
    private static CaseRef Model(CaseAuthoring a, string role, string? name = null, int? bus = null)
        => a.Collections().Where(c => c.Role == role).SelectMany(a.Records).Select(r => r.Ref).First(r =>
        {
            JsonSourceNode n = a.Session.Documents[r.Document].Text.Resolve(r.Pointer)!;
            return (name is not null && RecordRead.Str(n, "name", StringComparer.Ordinal) == name) || (bus is not null && RecordRead.Int(n, "id", StringComparer.Ordinal) == bus);
        });

    private static KeyValuePair<string, string>[] Kv(params (string K, string V)[] x) => x.Select(p => new KeyValuePair<string, string>(p.K, p.V)).ToArray();

    // =================================================================================================================
    // The field grid of an overlay record
    // =================================================================================================================

    [Fact]
    public void An_overlay_record_edits_in_the_field_grid_by_its_kind_spec()
    {
        using var f = new UiOverlayFixture();
        CaseAuthoring a = Open(f.ManifestPath);
        Assert.True(a.HasOverlay);
        Assert.Null(a.OverlayRefusal);
        Assert.Equal(new[] { UiOverlayFixture.FileA, UiOverlayFixture.FileB }, a.OpenedOverlayFiles);

        FieldGridModel g = a.FieldGrid(Ov(a, "ov.cbl"));
        Assert.True(g.IsOverlay);
        Assert.Equal("Asset overlay record: switchingDevice (Switching device (asset overlay))", g.ShapeTitle);
        Assert.Equal(new[] { "id", "kind", "parent", "refs.from", "refs.to", "refs.line", "refs.transformer", "refs.generator", "refs.demand",
                             "params.device", "params.normalOpen", "params.ratedCurrentA", "params.breakingCurrentA", "note", "prov" },
            g.Rows.Select(r => r.Field));

        FieldRow kind = g.Row("kind");
        Assert.Equal(FieldState.Locked, kind.State);
        Assert.Equal("kind: refused. the kind of an overlay record is fixed (its params and refs follow it); to change it, add a record of the other kind and delete this one.",
            g.Edit("kind", "busbarSection").Message);

        FieldRow parent = g.Row("parent");
        Assert.Equal("record: substation", parent.Unit);
        Assert.Equal(new[] { "ov.sub" }, parent.EnumChoices);
        Assert.Equal("\"ov.sub\"", parent.Literal);
        Assert.True(parent.Required);

        FieldRow from = g.Row("refs.from");
        Assert.Equal(("from", "record: busbarSection", "Reference"), (from.Label, from.Unit, from.Kind));
        Assert.Equal(new[] { "ov.bb1", "ov.bb2" }, from.EnumChoices);
        FieldRow line = g.Row("refs.line");
        Assert.Equal("\"L1-2 N-S Scotland\"", line.Literal);
        Assert.Equal("line", line.Unit);
        Assert.Contains("L1-2 N-S Scotland", line.EnumChoices);
        Assert.Equal(FieldState.Absent, g.Row("refs.to").State);

        FieldRow device = g.Row("params.device");
        Assert.Equal(new[] { "breaker", "disconnector", "earthSwitch" }, device.EnumChoices);
        Assert.Equal("Enum", device.Kind);
        FieldRow rated = g.Row("params.ratedCurrentA");
        Assert.Equal(("ratedCurrentA", "A", "4000", FieldState.Set), (rated.Label, rated.Unit, rated.Literal, rated.State));
        Assert.Equal("Number", rated.Kind);

        // Edits are engine operations: one undo step each, refused with the kind spec's reason.
        AuthoringResult ok = g.Edit("params.ratedCurrentA", "4500");
        Assert.True(ok.Ok, ok.Message);
        Assert.Equal("params.ratedCurrentA 4000 -> 4500 (U to undo).", ok.Message);
        Assert.Equal("4500", a.FieldGrid(Ov(a, "ov.cbl")).Row("params.ratedCurrentA").Literal);
        Assert.Equal("params.device: 'fuse' is not one of: breaker, disconnector, earthSwitch.", g.Edit("params.device", "fuse").Message);
        Assert.Equal("params.device is required by kind 'switchingDevice' and cannot be reset to absent.", g.Reset("params.device").Message);
        Assert.True(g.Edit("refs.to", "ov.bb2").Ok);
        Assert.Equal("refs.line: no line of the case is named 'nope' (names must match exactly).", g.Edit("refs.line", "nope").Message);
        Assert.True(a.FieldGrid(Ov(a, "ov.cbl")).Reset("refs.line").Ok);
        Assert.Equal(3, a.UndoDepth);

        // The object form keeps its unit and provenance, and the row says so.
        FieldRow objectForm = a.FieldGrid(Ov(a, "ov.bb1")).Row("params.ratedCurrentA");
        Assert.Equal("4000", objectForm.Literal);
        Assert.Equal("as written; object form: unit 'A' and provenance kept when the value is set", objectForm.StateText);
        // A model record's grid is exactly as before.
        Assert.False(a.FieldGrid(Model(a, CaseRoles.Lines, "L1-2 N-S Scotland")).IsOverlay);
    }

    // =================================================================================================================
    // Record actions
    // =================================================================================================================

    [Fact]
    public void Overlay_records_add_duplicate_delete_and_rename_with_their_refusals()
    {
        using var f = new UiOverlayFixture();
        CaseAuthoring a = Open(f.ManifestPath);
        CaseRef sub = Ov(a, "ov.sub"), bb2 = Ov(a, "ov.bb2");
        Assert.Equal("id", RecordActions.KeyField(a, sub));

        AuthoringResult del = RecordActions.Delete(a, sub);
        Assert.False(del.Ok);
        Assert.StartsWith($"Delete refused: 11 record(s) refer to {FileA} record 'ov.sub' (substation): ", del.Message, StringComparison.Ordinal);
        Assert.Equal(new[] { "refs.to", "refs.from", "refs.from" }, RecordActions.WhereUsed(a, bb2).Select(w => w.Field));
        Assert.Equal(new[] { "ov.cpl", "ov.cbd", "ov.es" }, RecordActions.WhereUsed(a, bb2).Select(w => a.OverlayIdentity(w.Record).Id));
        // A model asset's where-used list leads with the overlay records pointing at it.
        IReadOnlyList<WhereUsedRow> busUsed = RecordActions.WhereUsed(a, Model(a, CaseRoles.Buses, bus: 1));
        Assert.Equal(new[] { "refs.bus", "refs.bus" }, busUsed.Take(2).Select(w => w.Field));
        Assert.Equal(new[] { "ov.bb1", "ov.earth" }, busUsed.Take(2).Select(w => a.OverlayIdentity(w.Record).Id));

        AuthoringResult rename = RecordActions.Rename(a, bb2, "ov.bb2x");
        Assert.Equal("Rename id \"ov.bb2\" -> \"ov.bb2x\": 3 reference(s) follow (U to undo).", rename.Message);
        AuthoringResult dup = RecordActions.Duplicate(a, Ov(a, "ov.ds"));
        Assert.True(dup.Ok, dup.Message);
        Assert.Equal("ov.ds-copy", a.OverlayIdentity(dup.Select!).Id);
        Assert.True(RecordActions.Delete(a, dup.Select!).Ok);

        // Add: the kinds by context.
        Assert.Equal(new[] { "busbarSection", "earthPath", "switchingDevice", "transformer3W" }, OverlayActions.KindsFor(a, sub).Select(k => k.Kind));
        CaseRef bus101 = Model(a, CaseRoles.Buses, bus: 101);
        Assert.Equal(new[] { ("busbarSection", "bus"), ("earthPath", "bus"), ("transformer3W", "starBus") },
            OverlayActions.KindsFor(a, bus101).Select(k => (k.Kind, k.AttachRole!)));
        Assert.Equal(13, OverlayActions.KindsFor(a, null).Count);
        Assert.Empty(OverlayActions.KindsFor(a, Ov(a, "ov.hvdc")));   // nothing sits under an HVDC link

        // Attached to bus 101: the bus role is the bus, the prompt asks for the id, the parent and the file only.
        Assert.Equal(new[] { "id", "parent", "file" }, OverlayActions.FieldsFor(a, bus101, "busbarSection").Select(x => x.Field));
        Assert.Equal(new[] { "ov.sub" }, OverlayActions.FieldsFor(a, bus101, "busbarSection")[1].Choices);
        AuthoringResult attached = OverlayActions.Add(a, bus101, "busbarSection", Kv(("id", "ov.bb9"), ("parent", "ov.sub"), ("file", "")));
        Assert.True(attached.Ok, attached.Message);
        Assert.Equal($"Added {FileA} record 'ov.bb9' (busbarSection) (U to undo).", attached.Message);
        Assert.Equal("101", a.FieldGrid(attached.Select!).Row("refs.bus").Literal);

        // Under ov.sub: its parent and its file are the substation's; the prompt asks for the required fields.
        Assert.Equal(new[] { "id", "refs.from", "params.device", "params.normalOpen" }, OverlayActions.FieldsFor(a, sub, "switchingDevice").Select(x => x.Field));
        AuthoringResult child = OverlayActions.Add(a, sub, "switchingDevice", Kv(("id", "ov.cb9"), ("refs.from", "ov.bb9"), ("params.device", "breaker"), ("params.normalOpen", "false")));
        Assert.True(child.Ok, child.Message);
        Assert.Equal("\"ov.sub\"", a.FieldGrid(child.Select!).Row("parent").Literal);

        // In file B, at the top level.
        AuthoringResult top = OverlayActions.Add(a, null, "soilZone", Kv(("id", "soil.2"), ("file", FileB), ("params.resistivityOhmM", "250")));
        Assert.True(top.Ok, top.Message);
        Assert.Equal(FileB, top.Select!.FileName);

        Assert.Equal("parent: a 'busbarSection' record must have a parent (of kind substation).", OverlayActions.Add(a, null, "busbarSection", Kv(("id", "x1"), ("refs.bus", "1"))).Message);
        Assert.Equal("Add refused: 'colour' is not a field of a new overlay record.", OverlayActions.Add(a, null, "soilZone", Kv(("id", "x1"), ("colour", "red"))).Message);
        Assert.StartsWith("Add refused: 'nope.json' is not an overlay file of the case (they are: ", OverlayActions.Add(a, null, "soilZone", Kv(("id", "x1"), ("file", "nope.json"))).Message, StringComparison.Ordinal);
        Assert.Equal("Add refused: the record is added under the selected record 'ov.sub', not 'ov.l6'.", OverlayActions.Add(a, sub, "busbarSection", Kv(("id", "x1"), ("parent", "ov.l6"), ("refs.bus", "1"))).Message);
        Assert.Equal("Add refused: role 'bus' is set by the selected asset; it is not typed.", OverlayActions.Add(a, bus101, "busbarSection", Kv(("id", "x1"), ("parent", "ov.sub"), ("refs.bus", "1"))).Message);
        Assert.Equal(AuthoringOutcome.CheckPassed, a.Check().Outcome);
    }

    // =================================================================================================================
    // Check, Save, Save As
    // =================================================================================================================

    [Fact]
    public void Check_shows_the_overlay_checkers_problems_on_the_record_and_on_Home()
    {
        using var f = new UiOverlayFixture(withOverlay: true, overlayA: OverlayEditingTests.BadOverlayA());
        CaseAuthoring a = Open(f.ManifestPath);
        var m = new AssetAuthorModel(a);
        AuthoringReport r = m.Check();
        Assert.Equal(AuthoringOutcome.CheckFailed, r.Outcome);
        Assert.Equal("CHECK FAILED: 3 error(s).", r.Headline);
        Assert.Equal(3, m.CheckPage!.Problems.Count);   // Home lists every one

        Assert.True(m.SelectRecord(Ov(a, "ov.bb2")));
        Assert.Contains(AssetNavigatorModel.CheckTab, m.SubTabs);
        AuthoringProblem p = Assert.Single(m.CheckPage!.Problems);
        Assert.Equal(new AuthoringProblem(FileA, Ov(a, "ov.bb2").Pointer + "/refs/bus/bus", "record 'ov.bb2': role 'bus' points at bus 99999, which is not a bus of the case."), p);

        // Put it right in the grid (the bus picker), and Check passes.
        Assert.True(m.EditField("refs.bus", "101").Ok);
        Assert.True(m.SelectRecord(Ov(a, "ov.cbl")));
        Assert.True(m.EditField("params.device", "breaker").Ok);
        Assert.True(m.SelectRecord(Ov(a, "ov.ds")));
        Assert.True(m.EditField("refs.from", "ov.bb1").Ok);
        Assert.Equal(AuthoringOutcome.CheckPassed, m.Check().Outcome);
        Assert.Empty(m.CheckPage!.Problems);
    }

    [Fact]
    public void Save_writes_only_the_edited_overlay_token_and_Save_As_copies_the_overlay_files()
    {
        using var f = new UiOverlayFixture();
        Dictionary<string, byte[]> before = Files(f.CaseDir);
        CaseAuthoring a = Open(f.ManifestPath);
        CaseRef l6 = Ov(a, "ov.l6");
        Assert.True(a.FieldGrid(l6).Edit("params.heightM", "52.5").Ok);

        AuthoringReport saved = a.Save();
        Assert.Equal(AuthoringOutcome.Saved, saved.Outcome);
        Assert.Equal(new[] { FileB }, saved.FilesWritten);
        Assert.Contains("the v2 loader reads the case (staged copy, dispatch on, as every verb loads it).", saved.Notes);
        Dictionary<string, byte[]> after = Files(f.CaseDir);
        string expected = Encoding.UTF8.GetString(before[UiOverlayFixture.FileB]).Replace("\"heightM\": 50,", "\"heightM\": 52.5,", StringComparison.Ordinal);
        Assert.Equal(expected, Encoding.UTF8.GetString(after[UiOverlayFixture.FileB]));
        foreach (string k in before.Keys.Where(k => k != UiOverlayFixture.FileB)) Assert.Equal(before[k], after[k]);
        Assert.Equal(52.5, V2CaseLoader.LoadWithNotes(f.ManifestPath, V2TestSupport.SchemasDir).Overlay!.Find("ov.l6")!.Params.GetProperty("heightM").GetDouble());

        // Save As: a new case of the user's own, with its overlay files, which the copy then edits.
        string target = Path.Combine(Path.GetTempPath(), "gridsim-overlay-saveas-" + Guid.NewGuid().ToString("N"));
        try
        {
            Assert.True(a.FieldGrid(Ov(a, "ov.sub")).Edit("params.name", "North Scotland 400").Ok);
            AuthoringReport copy = a.SaveAs(target);
            Assert.Equal(AuthoringOutcome.Saved, copy.Outcome);
            Assert.Contains(UiOverlayFixture.FileA, copy.FilesWritten.Select(p => p.Replace('\\', '/')));
            Assert.Contains(UiOverlayFixture.FileB, copy.FilesWritten.Select(p => p.Replace('\\', '/')));
            Dictionary<string, byte[]> copied = Files(target);
            Assert.Equal(after[UiOverlayFixture.FileB], copied[UiOverlayFixture.FileB]);
            Assert.Contains("\"name\": \"North Scotland 400\"", Encoding.UTF8.GetString(copied[UiOverlayFixture.FileA]), StringComparison.Ordinal);
            Assert.Equal(after, Files(f.CaseDir));   // the source is not written by Save As
            Assert.True(a.HasOverlay);
            Assert.Equal(target, a.RootPath);
            Assert.Equal(new[] { UiOverlayFixture.FileA, UiOverlayFixture.FileB }, a.OpenedOverlayFiles);
            Assert.Equal("North Scotland 400", V2CaseLoader.LoadWithNotes(Path.Combine(target, "uk.case.json"), V2TestSupport.SchemasDir).Overlay!
                .Find("ov.sub")!.Params.GetProperty("name").GetString());
        }
        finally
        {
            try { if (Directory.Exists(target)) Directory.Delete(target, recursive: true); } catch (IOException) { }
        }
    }

    [Fact]
    public void An_assets_list_edited_in_the_session_refuses_Check_and_Save_naming_both_lists()
    {
        using var f = new UiOverlayFixture();
        Dictionary<string, byte[]> before = Files(f.CaseDir);
        CaseAuthoring a = Open(f.ManifestPath);
        CaseRef manifest = a.RootRecords().Single().Ref;
        Assert.True(a.FieldGrid(manifest).Edit("assets", "[\"lines.assets.json\"]").Ok);
        string why = Assert.IsType<string>(a.OverlayRefusal);
        Assert.Equal("the manifest's asset overlay list (assets: lines.assets.json) no longer matches the overlay files this session opened "
            + "(assets/site.assets.json, lines.assets.json), so the case cannot be checked or saved as it stands: undo the change to \"assets\" "
            + "(to change the overlay files, edit the manifest outside AUTHOR and open the case again). The files on disk are unchanged.", why);
        Assert.Equal("NOT SAVED: " + why, a.Save().Headline);
        Assert.Equal("CHECK NOT RUN: " + why, a.Check().Headline);
        Assert.Equal(before, Files(f.CaseDir));
        Assert.True(a.Undo().Ok);
        Assert.Null(a.OverlayRefusal);
        Assert.Equal(AuthoringOutcome.CheckPassed, a.Check().Outcome);
    }

    // =================================================================================================================
    // The Author tree and the overlay command
    // =================================================================================================================

    [Fact]
    public void The_Author_tree_holds_the_overlay_records_by_parent_and_model_assets_carry_the_overlay_anchor()
    {
        using var f = new UiOverlayFixture();
        var m = new AssetAuthorModel(Open(f.ManifestPath));
        AuthorNode cls = m.ClassNode(CaseRoles.Overlay)!;
        Assert.Equal(("Asset overlay", "20", CommandTargets.OverlayAnchor), (cls.Label, cls.Detail, cls.Target));
        Assert.Equal(new[] { "ov.sub", "ov.hvdc", "ov.zebra", "ov.l6", "ov.route", "ov.soil" }, cls.Children.Select(n => n.Label));
        AuthorNode sub = cls.Children[0];
        Assert.Equal(new[] { "ov.bb1", "ov.bb2", "ov.cpl", "ov.cbl", "ov.cbt", "ov.cbg", "ov.cbd", "ov.ds", "ov.es", "ov.earth", "ov.t3w" }, sub.Children.Select(n => n.Label));
        Assert.Equal("substation  " + FileA, sub.Detail);
        Assert.Equal(new[] { "ov.l6.a" }, cls.Children[3].Children.Select(n => n.Label));
        Assert.Equal(new[] { "ov.span1", "ov.ew" }, cls.Children[4].Children.Select(n => n.Label));
        Assert.Equal(UiOverlayFixture.RecordCount, m.PreOrder().Count(n => n.Role == CaseRoles.Overlay && n.Kind == AuthorNodeKind.Record));
        Assert.Equal(new[] { CommandTargets.OverlayRecord }, sub.MenuTargets);

        // Model assets an overlay record attaches to carry the anchor after their own kind; others do not.
        Assert.Equal(new[] { CommandTargets.Bus, CommandTargets.OverlayAnchor }, m.ClassNode(CaseRoles.Buses)!.Children[0].MenuTargets);
        Assert.Equal(new[] { CommandTargets.Generator, CommandTargets.OverlayAnchor }, m.ClassNode(CaseRoles.Generators)!.Children[0].MenuTargets);
        Assert.Equal(new[] { CommandTargets.AssetClassNode }, m.ClassNode(CaseRoles.Buses)!.MenuTargets);
        if (m.ClassNode(CaseRoles.Oltcs) is AuthorNode oltcs && oltcs.Children.Count > 0)
            Assert.Equal(new[] { CommandTargets.Oltc }, oltcs.Children[0].MenuTargets);

        // An overlay record's page: PROPERTIES from its kind spec, CONNECTIONS its children.
        m.Select(sub);
        Assert.Equal(new[] { AssetNavigatorModel.OverviewTab, AssetNavigatorModel.PropertiesTab, AssetNavigatorModel.ConnectionsTab }, m.SubTabs);
        Assert.True(m.Fields!.IsOverlay);
        Assert.Equal(11, m.WhereUsed.Count);
        Assert.True(m.HasSelectedRecord && m.SelectedHasKey && m.CanAddOverlay);
        Assert.Equal("ov.sub", m.SelectedKeyText);
        Assert.Null(m.AddTarget);   // the model Add never acts on the overlay

        AuthoringResult added = m.AddOverlayRecord("busbarSection", Kv(("id", "ov.bb7"), ("refs.bus", "102")));
        Assert.True(added.Ok, added.Message);
        Assert.Equal("ov.bb7", m.Selected!.Label);
        Assert.Equal("ov.sub", m.Selected.Parent!.Label);
        Assert.True(m.EditField("params.label", "New section").Ok);
        Assert.True(m.RenameSelected("ov.bb8").Ok);
        Assert.Equal("ov.bb8", m.Selected!.Label);
        Assert.True(m.DeleteSelected().Ok);
        Assert.StartsWith("Asset overlay (carried beside the model, never used by the solver): assets/site.assets.json, lines.assets.json.", NoteOf(m), StringComparison.Ordinal);

        // A case without an overlay: no overlay node, no anchors, no overlay add.
        using var plain = new UiOverlayFixture(withOverlay: false);
        var p = new AssetAuthorModel(Open(plain.ManifestPath));
        Assert.Null(p.ClassNode(CaseRoles.Overlay));
        Assert.Equal(new[] { CommandTargets.Bus }, p.ClassNode(CaseRoles.Buses)!.Children[0].MenuTargets);
        p.Select(p.ClassNode(CaseRoles.Buses)!.Children[0]);
        Assert.False(p.CanAddOverlay);
        Assert.Equal("", NoteOf(p));
    }

    // The Home page's overlay note.
    private static string NoteOf(AssetAuthorModel m)
    {
        m.Select(m.Home);
        return m.Summary!.OverlayNote;
    }

    private sealed class Actions : IAuthorCommandActions, IOverlayAuthorActions
    {
        public AssetAuthorModel? Author { get; set; }
        public int Adds { get; private set; }
        public Task UndoAsync() => Task.CompletedTask;
        public Task RedoAsync() => Task.CompletedTask;
        public Task CheckAsync() => Task.CompletedTask;
        public Task SaveAsync() => Task.CompletedTask;
        public Task SaveAsAsync() => Task.CompletedTask;
        public Task SwitchToRunAsync() => Task.CompletedTask;
        public Task AddRecordAsync() => Task.CompletedTask;
        public Task DuplicateAsync() => Task.CompletedTask;
        public Task DeleteAsync() => Task.CompletedTask;
        public Task RenameAsync() => Task.CompletedTask;
        public Task WhereUsedAsync() => Task.CompletedTask;
        public Task AddOverlayRecordAsync() { Adds++; return Task.CompletedTask; }
    }

    [Fact]
    public void The_overlay_command_is_Author_and_desktop_only_on_overlay_records_and_anchors_and_declared_once()
    {
        var r = new CommandRegistry();
        var actions = new Actions();
        IReadOnlyList<AppCommand> author = AuthorCommands.Register(r, actions);
        IReadOnlyList<AppCommand> overlay = OverlayAuthorCommands.Register(r, actions);
        AppCommand add = Assert.Single(overlay);
        Assert.Equal((OverlayAuthorCommands.AddOverlayRecord, "Add overlay record...", CommandModes.Author, CommandPlatforms.Desktop), (add.Id, add.Title, add.Modes, add.Platforms));
        Assert.Null(add.MenuPath);
        Assert.Null(add.Gesture);
        Assert.False(add.IsEnabled);   // nothing to act on while not authoring

        Assert.Equal(new[] { AuthorCommands.Duplicate, AuthorCommands.Delete, AuthorCommands.Rename, AuthorCommands.WhereUsed, OverlayAuthorCommands.AddOverlayRecord },
            r.ForTarget(CommandTargets.OverlayRecord, CommandModes.Author, CommandPlatforms.Desktop).Select(c => c.Id));
        Assert.Equal(new[] { OverlayAuthorCommands.AddOverlayRecord }, r.ForTarget(CommandTargets.OverlayAnchor, CommandModes.Author, CommandPlatforms.Desktop).Select(c => c.Id));
        Assert.Empty(r.ForTarget(CommandTargets.OverlayRecord, CommandModes.Run, CommandPlatforms.Desktop));
        Assert.Empty(r.ForTarget(CommandTargets.OverlayAnchor, CommandModes.Run, CommandPlatforms.Desktop));
        Assert.DoesNotContain(r.Commands, c => c.IsOfferedIn(CommandModes.Author, CommandPlatforms.Mobile));
        // The Phase 4.1 menus of every model record kind are unchanged by it.
        Assert.Equal(new[] { AuthorCommands.Duplicate, AuthorCommands.Delete, AuthorCommands.Rename, AuthorCommands.WhereUsed },
            r.ForTarget(CommandTargets.Bus, CommandModes.Author, CommandPlatforms.Desktop).Select(c => c.Id));
        Assert.Equal(11, author.Count);
        Assert.Throws<ArgumentException>(() => OverlayAuthorCommands.Register(r, actions));

        // Enabled exactly while the Author model can add here.
        using var f = new UiOverlayFixture();
        var m = new AssetAuthorModel(Open(f.ManifestPath));
        actions.Author = m;
        m.Select(m.ClassNode(CaseRoles.Overlay)!);
        Assert.True(add.IsEnabled);
        m.Select(m.ClassNode(CaseRoles.Buses)!);
        Assert.False(add.IsEnabled);
        m.Select(m.ClassNode(CaseRoles.Buses)!.Children[0]);
        Assert.True(add.IsEnabled);
    }

    private sealed class StarLegsActions : IStarLegsAuthorActions
    {
        public AssetAuthorModel? Author { get; set; }
        public int Derives { get; private set; }
        public Task DeriveStarLegsAsync() { Derives++; return Task.CompletedTask; }
    }

    [Fact]
    public async Task Derive_star_legs_is_an_Author_desktop_context_command_on_transformer3W_overlay_records_only_and_declared_once()
    {
        var r = new CommandRegistry();
        var actions = new StarLegsActions();
        AppCommand derive = Assert.Single(StarLegsAuthorCommands.Register(r, actions));
        Assert.Equal((StarLegsAuthorCommands.DeriveStarLegs, "Derive star legs...", CommandModes.Author, CommandPlatforms.Desktop, CommandTargets.StarTransformer),
            (derive.Id, derive.Title, derive.Modes, derive.Platforms, derive.Targets));
        Assert.Null(derive.MenuPath);
        Assert.Null(derive.Gesture);
        Assert.False(derive.IsEnabled);   // nothing to act on while not authoring
        Assert.Equal(new[] { StarLegsAuthorCommands.DeriveStarLegs }, r.ForTarget(CommandTargets.StarTransformer, CommandModes.Author, CommandPlatforms.Desktop).Select(c => c.Id));
        Assert.Empty(r.ForTarget(CommandTargets.StarTransformer, CommandModes.Run, CommandPlatforms.Desktop));
        Assert.Empty(r.ForTarget(CommandTargets.StarTransformer, CommandModes.Author, CommandPlatforms.Mobile));
        // No other target gains it (the OverlayRecord menu of every overlay record is unchanged).
        foreach (CommandTargets t in Enum.GetValues<CommandTargets>().Where(t => t is not (CommandTargets.None or CommandTargets.StarTransformer)))
            Assert.Empty(r.ForTarget(t, CommandModes.Author, CommandPlatforms.Desktop));
        Assert.Throws<ArgumentException>(() => StarLegsAuthorCommands.Register(r, actions));

        // The tree: only the transformer3W record carries the star-legs target, after its own kind.
        using var f = new UiOverlayFixture();
        var m = new AssetAuthorModel(Open(f.ManifestPath));
        actions.Author = m;
        AuthorNode sub = m.ClassNode(CaseRoles.Overlay)!.Children.Single(n => n.Label == "ov.sub");
        AuthorNode t3w = sub.Children.Single(n => n.Label == "ov.t3w");
        Assert.Equal(new[] { CommandTargets.OverlayRecord, CommandTargets.StarTransformer }, t3w.MenuTargets);
        Assert.True(t3w.StarTransformer);
        Assert.Equal(new[] { "ov.t3w" },
            m.PreOrder().Where(n => n.MenuTargets.Contains(CommandTargets.StarTransformer)).Select(n => n.Label));

        // Enabled exactly while a transformer3W overlay record is selected; running it calls the action once.
        m.Select(sub);
        Assert.False(derive.IsEnabled);
        m.Select(t3w);
        Assert.True(derive.IsEnabled);
        await derive.RunAsync();
        Assert.Equal(1, actions.Derives);
        m.Select(sub.Children.Single(n => n.Label == "ov.bb1"));
        Assert.False(derive.IsEnabled);
    }
}
