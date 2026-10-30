using System.ComponentModel;
using System.Reflection;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using GridSim.Core.IO.Json;
using GridSim.Core.IO.Json.CaseEditing;
using GridSim.Core.Licensing;
using GridSim.Core.Model;
using GridSim.Core.Simulation;
using GridSim.Tests.Editor;
using GridSim.Tests.V2;
using GridSim.Ui.Assets.Editing;
using GridSim.Ui.Assets.Index;
using GridSim.Ui.Assets.Keys;
using GridSim.Ui.Assets.Navigation;
using GridSim.Ui.Commands;
using GridSim.Ui.Shell;
using Xunit;

namespace GridSim.Tests.Ui;

/// <summary>
/// UI programme Phase 4.1: the locked-field list pinned against the v2 loader both ways (behaviour on a real case, and a
/// scan of every refusal site in V2CaseLoader.cs), the field grid's locked rows, the ASSETS tab's Author model (the tree
/// of case records, the sub-tabs, no live values), the navigator's RUN / AUTHOR mode (Q5, Q9), and the Author commands in
/// the registry (Author only, desktop only, no gesture clash, a context menu for every record kind).
/// </summary>
public sealed class UiAuthorModeTests
{
    private static LicenceGate Gate() => CaseSessionTests.Unlimited();

    private static CaseAuthoring OpenV2(string manifest) => CaseAuthoring.Open(AssetCaseSource.V2(manifest), Gate(), Array.Empty<string>());

    private static CaseRef Rec(CaseAuthoring a, string role, int index) => a.Records(a.Collections().First(c => c.Role == role))[index].Ref;

    // =================================================================================================================
    // The locked fields, pinned against the loader
    // =================================================================================================================

    // Set one locked field on a fresh v2 projection of data/uk (lines[0], transformers[0] or the manifest), as a user
    // would by hand, with a value the schema accepts.
    private static void SetLocked(UiOverlayFixture f, LockedField lf)
    {
        string value = lf.OnlyWhen ?? "\"gridsim.test.locked@1\"";
        if (lf.SchemaFile == LockedFields.CaseSchema)
        {
            JsonObject m = JsonNode.Parse(File.ReadAllText(f.ManifestPath))!.AsObject();
            m[lf.Field] = JsonNode.Parse(value);
            File.WriteAllText(f.ManifestPath, m.ToJsonString());
            return;
        }
        string array = lf.ShapePointer switch { "/$defs/line" => "lines", "/$defs/transformer" => "transformers", _ => throw new InvalidOperationException(lf.ShapePointer) };
        JsonObject n = JsonNode.Parse(File.ReadAllText(f.NetworkPath))!.AsObject();
        n[array]![0]!.AsObject()[lf.Field] = JsonNode.Parse(value);
        File.WriteAllText(f.NetworkPath, n.ToJsonString());
    }

    public static TheoryData<int> LockedIndexes()
    {
        var d = new TheoryData<int>();
        for (int i = 0; i < LockedFields.All.Count; i++) d.Add(i);
        return d;
    }

    [Theory]
    [MemberData(nameof(LockedIndexes))]
    public void Every_locked_field_is_one_the_v2_loader_refuses_and_the_grid_shows_it_locked_with_that_reason(int index)
    {
        LockedField lf = LockedFields.All[index];
        using var f = new UiOverlayFixture(withOverlay: false);
        // The case without the field loads (so the refusal below is the field's).
        V2CaseLoader.Load(f.ManifestPath, V2TestSupport.SchemasDir);
        SetLocked(f, lf);
        // The schema accepts it (it is valid JSON v2) ...
        Assert.Empty(BundledSchemas.V2Validate.Validate(V2TestSupport.ReadRoot(lf.SchemaFile == LockedFields.CaseSchema ? f.ManifestPath : f.NetworkPath), lf.SchemaFile));
        // ... and the loader refuses it, naming it.
        string msg = Assert.Throws<InvalidDataException>(() => V2CaseLoader.Load(f.ManifestPath, V2TestSupport.SchemasDir)).Message;
        if (lf.SchemaFile == LockedFields.CaseSchema)
            Assert.Contains("/" + lf.Field + ": GridSim does not yet bind a " + lf.Field + " from a case", msg, StringComparison.Ordinal);
        else
        {
            Assert.Contains("/" + lf.Field + ": ", msg, StringComparison.Ordinal);
            Assert.Contains("is valid JSON v2, but GridSim's model does not carry it yet", msg, StringComparison.Ordinal);
        }

        // The field grid: the row is LOCKED with the reason, as written; an edit is refused with the reason; a reset to
        // absent is allowed (that is how such a case is put right) and the case then loads again.
        CaseAuthoring a = OpenV2(f.ManifestPath);
        CaseRef rec = lf.SchemaFile == LockedFields.CaseSchema ? a.RootRecords().Single().Ref
            : Rec(a, lf.ShapePointer == "/$defs/line" ? CaseRoles.Lines : CaseRoles.Transformers, 0);
        FieldGridModel grid = a.FieldGrid(rec);
        Assert.Equal(lf.SchemaFile, grid.SchemaFile);
        Assert.Equal(lf.ShapePointer, grid.ShapePointer);
        FieldRow row = grid.Row(lf.Field);
        Assert.Equal(FieldState.Locked, row.State);
        Assert.Equal(lf.Reason, row.LockReason);
        Assert.True(row.Present);
        Assert.False(row.IsEditable);
        Assert.True(row.CanReset);
        Assert.Contains(lf.Reason, row.StateText, StringComparison.Ordinal);
        AuthoringResult edit = grid.Edit(lf.Field, lf.OnlyWhen ?? "\"gridsim.test.other@1\"");
        Assert.False(edit.Ok);
        Assert.EndsWith(lf.Reason, edit.Message, StringComparison.Ordinal);
        Assert.False(a.IsDirty);
        AuthoringResult reset = grid.Reset(lf.Field);
        Assert.True(reset.Ok, reset.Message);
        Assert.True(a.Save().Saved);
        V2CaseLoader.Load(f.ManifestPath, V2TestSupport.SchemasDir);
    }

    [Fact]
    public void The_locked_list_is_exactly_the_v2_loaders_refusal_sites()
    {
        string src = File.ReadAllText(Path.Combine(EditorTestSupport.RepoRoot(), "src", "GridSim.Core", "IO", "Json", "V2CaseLoader.cs"));
        var found = new HashSet<(string, string, string)>();
        // Every "NotYet(at + "/field", "a line ..." / "... transformer ...")" site of the element mappers.
        foreach (Match m in Regex.Matches(src, "NotYet\\(at \\+ \"/(?<f>\\w+)\", \"(?<what>[^\"]+)\""))
        {
            string what = m.Groups["what"].Value;
            string shape = what.Contains("transformer", StringComparison.Ordinal) ? "/$defs/transformer"
                : what.Contains("line", StringComparison.Ordinal) ? "/$defs/line"
                : throw new InvalidOperationException("a NotYet refusal of an element the locked list does not know: " + what);
            found.Add((LockedFields.NetworkSchema, shape, m.Groups["f"].Value));
        }
        // The profile refusal: "foreach (string profile in new[] { "gridCodeProfile", "solverProfile" })".
        Match p = Regex.Match(src, "foreach \\(string profile in new\\[\\] \\{ (?<list>[^}]+) \\}\\)");
        Assert.True(p.Success, "the profile refusal loop was not found in V2CaseLoader.cs");
        foreach (Match q in Regex.Matches(p.Groups["list"].Value, "\"(?<n>\\w+)\"")) found.Add((LockedFields.CaseSchema, "", q.Groups["n"].Value));

        Assert.Equal(6, found.Count);
        var listed = LockedFields.All.Select(l => (l.SchemaFile, l.ShapePointer, l.Field)).ToHashSet();
        Assert.Equal(LockedFields.All.Count, listed.Count);   // no entry twice
        Assert.True(found.SetEquals(listed), "locked list " + string.Join(", ", listed) + " differs from the loader's refusals " + string.Join(", ", found));
        // inService is refused only when false (the mapper's own condition), and only those two entries say so.
        Assert.All(LockedFields.All, l => Assert.Equal(l.Field == "inService" ? "false" : null, l.OnlyWhen));
    }

    [Fact]
    public void No_v1_field_is_locked_and_an_unlocked_v2_field_edits_normally()
    {
        using var temp = new EditorTestSupport.TempCopy(EditorTestSupport.CaseDir("uk"));
        CaseAuthoring v1 = CaseAuthoring.Open(AssetCaseSource.V1(temp.Dir), Gate(), Array.Empty<string>());
        foreach (AuthoringRecordSet set in v1.Collections().Where(c => c.Count > 0))
            Assert.DoesNotContain(v1.FieldGrid(v1.Records(set)[0].Ref).Rows, r => r.State == FieldState.Locked);
        using var f = new UiOverlayFixture(withOverlay: false);
        CaseAuthoring v2 = OpenV2(f.ManifestPath);
        FieldGridModel line = v2.FieldGrid(Rec(v2, CaseRoles.Lines, 0));
        Assert.Equal(new[] { "type", "inService" }, line.Rows.Where(r => r.State == FieldState.Locked).Select(r => r.Field));
        Assert.Equal(FieldState.Absent, line.Row("equipment").State);
        Assert.Equal(new[] { "ohl", "cable" }, line.Row("equipment").EnumChoices);
        Assert.True(line.Edit("equipment", "cable").Ok);
    }

    // =================================================================================================================
    // The Author model: the tree of case records, the sub-tabs, no live value
    // =================================================================================================================

    [Fact]
    public void The_Author_tree_is_the_case_files_records_and_the_sub_tabs_show_only_what_has_content()
    {
        using var temp = new EditorTestSupport.TempCopy(EditorTestSupport.CaseDir("uk"));
        CaseAuthoring a = CaseAuthoring.Open(AssetCaseSource.V1(temp.Dir), Gate(), Array.Empty<string>());
        var m = new AssetAuthorModel(a);
        Assert.Equal("AUTHOR", m.ModeLabel);
        Assert.Same(m.Home, m.Selected);
        Assert.Equal(new[] { "OVERVIEW" }, m.SubTabs);
        Assert.NotNull(m.Summary);
        Assert.Equal(Path.GetFileName(temp.Dir), m.Summary!.CaseName);

        // Home, the system file, then one class node per role, each holding exactly its collection's records.
        Assert.Equal("Case", m.Roots[0].Label);
        Assert.Equal(AuthorNodeKind.CaseFile, m.Roots[1].Kind);
        Assert.Equal("system.json", m.Roots[1].Record!.FileName);
        List<AuthorNode> classes = m.Roots.Where(n => n.Kind == AuthorNodeKind.Class).ToList();
        Assert.Equal(a.Collections().Select(c => c.Title), classes.Select(c => c.Label));
        foreach (AuthorNode cls in classes)
        {
            AuthoringRecordSet set = a.Collections().Single(c => c.Role == cls.Role);
            Assert.Equal(set.Count, cls.Children.Count);
            Assert.Equal(CommandTargets.AssetClassNode, cls.Target);
            Assert.All(cls.Children, r => Assert.Equal(AuthorNode.TargetOf(cls.Role), r.Target));
        }
        Assert.Equal(classes.Select(c => (c.Label, c.Children.Count)), m.Summary.Counts.Select(c => (c.Title, c.Count)));

        // A bus with referrers: OVERVIEW (record summary), PROPERTIES (the grid) and CONNECTIONS (where used).
        AuthorNode bus1 = m.ClassNode(CaseRoles.Buses)!.Children[0];
        Assert.True(m.Select(bus1));
        Assert.Equal(new[] { "OVERVIEW", "PROPERTIES", "CONNECTIONS" }, m.SubTabs);
        Assert.Equal("Buses > " + bus1.Label, m.Crumb);
        Assert.NotNull(m.RecordPage);
        Assert.NotNull(m.Fields);
        Assert.Equal(RecordActions.WhereUsed(a, bus1.Record!), m.WhereUsed);
        Assert.True(m.SelectedHasKey);

        // An edit keeps the selection (by id), marks the badge, and the record's grid shows the new literal.
        Assert.True(m.EditField("x", "0.5").Ok);
        Assert.Equal("AUTHOR *", m.ModeLabel);
        Assert.Equal(bus1.Id, m.Selected!.Id);
        Assert.Equal("0.5", m.Fields!.Row("x").Literal);

        // Check: CHECK appears on every record and on Home.
        Assert.Equal(AuthoringOutcome.CheckPassed, m.Check().Outcome);
        Assert.Contains("CHECK", m.SubTabs);
        Assert.False(m.CheckPage!.HasProblems);
        Assert.False(m.StatusIsError);

        // A refused delete: the status says so, with the referrers; nothing changes.
        Assert.False(m.DeleteSelected().Ok);
        Assert.True(m.StatusIsError);
        Assert.StartsWith("Delete refused:", m.Status, StringComparison.Ordinal);
        Assert.Equal(1, a.UndoDepth);

        // Undo, a where-used link, and CONNECTIONS through the command.
        Assert.True(m.Undo().Ok);
        Assert.Equal("AUTHOR", m.ModeLabel);
        Assert.True(m.ShowWhereUsed());
        Assert.Equal("CONNECTIONS", m.SelectedSubTab);
        WhereUsedRow first = m.WhereUsed[0];
        Assert.True(m.OpenWhereUsed(first));
        Assert.Equal(first.Record, m.Selected!.Record);
        Assert.Throws<ArgumentException>(() => m.SelectedSubTab = "NOPE");
    }

    [Fact]
    public void The_Author_model_reads_no_frame_so_it_can_show_no_live_value()
    {
        // Plan Q5 (a): Author mode shows no live values at all. Structurally: nothing of the Author mode's public surface is
        // a frame, a solved result or a model the solver built; it is the case file's records alone.
        Type[] live = { typeof(SimFrame), typeof(GridModel), typeof(GridSim.Core.PowerFlow.PowerFlowResult) };
        foreach (Type t in new[] { typeof(AssetAuthorModel), typeof(CaseAuthoring), typeof(FieldGridModel), typeof(FieldRow), typeof(AuthorNode),
                     typeof(AuthorSummary), typeof(AuthorRecordPage), typeof(AuthorClassPage), typeof(AuthorCheckPage) })
            foreach (PropertyInfo p in t.GetProperties(BindingFlags.Public | BindingFlags.Instance))
                Assert.DoesNotContain(p.PropertyType, live);
    }

    // =================================================================================================================
    // The navigator's RUN / AUTHOR mode
    // =================================================================================================================

    [Fact]
    public void The_navigator_switches_RUN_and_AUTHOR_and_offers_AUTHOR_for_case_files_only()
    {
        var nav = new AssetNavigatorModel();
        Assert.Equal("RUN", nav.ModeLabel);
        Assert.True(nav.IsRunMode);
        Assert.False(nav.CanAuthor);
        Assert.Contains("no case is loaded yet", nav.AuthorUnavailableReason, StringComparison.Ordinal);
        Assert.Equal("AUTHOR unavailable", nav.ModeSwitchLabel);

        nav.SetSource(new AssetCaseSource(AssetSourceKind.Matpower, "case9.m"), Gate());
        Assert.False(nav.CanAuthor);
        Assert.Equal(CaseAuthoring.UnavailableReason(nav.Source), nav.AuthorUnavailableReason);
        Assert.Equal(nav.AuthorUnavailableReason, Assert.Throws<CaseAuthoringException>(() => nav.OpenAuthoring()).Message);
        nav.SetSource(new AssetCaseSource(AssetSourceKind.Replay, "run"), Gate());
        Assert.Contains("replay", nav.AuthorUnavailableReason, StringComparison.Ordinal);

        using var temp = new EditorTestSupport.TempCopy(EditorTestSupport.CaseDir("uk"));
        nav.SetSource(AssetCaseSource.V1(temp.Dir), Gate());
        Assert.True(nav.CanAuthor);
        Assert.Equal("switch to AUTHOR", nav.ModeSwitchLabel);
        nav.AuthorSupported = false;
        Assert.Contains("desktop only", nav.AuthorUnavailableReason, StringComparison.Ordinal);
        nav.AuthorSupported = true;

        nav.Active = true;
        CaseAuthoring a = nav.OpenAuthoring(Array.Empty<string>());
        var raised = new List<string?>();
        ((INotifyPropertyChanged)nav).PropertyChanged += (_, e) => raised.Add(e.PropertyName);
        nav.EnterAuthor(new AssetAuthorModel(a));
        Assert.True(nav.IsAuthoring);
        Assert.False(nav.IsRunMode);
        Assert.Equal("AUTHOR", nav.ModeLabel);
        Assert.Equal("switch to RUN", nav.ModeSwitchLabel);
        Assert.False(nav.Active);          // the explorer refreshes nothing while authoring
        Assert.False(nav.CanShowOnMap);
        Assert.False(nav.ShowOnMap());
        nav.Active = true;                 // the shell's gate is kept, not applied
        Assert.False(nav.Active);
        Assert.Throws<InvalidOperationException>(() => nav.EnterAuthor(new AssetAuthorModel(a)));

        raised.Clear();
        AssetAuthorModel am = Assert.IsType<AssetAuthorModel>(nav.Author);
        am.Select(am.ClassNode(CaseRoles.Buses)!.Children[0]);
        Assert.True(am.EditField("x", "0.5").Ok);
        Assert.Contains(nameof(AssetNavigatorModel.ModeLabel), raised);
        Assert.Equal("AUTHOR *", nav.ModeLabel);

        nav.LeaveAuthor();
        Assert.Equal("RUN", nav.ModeLabel);
        Assert.True(nav.IsRunMode);
        Assert.True(nav.Active);           // the shell's gate again
        Assert.Throws<InvalidOperationException>(() => nav.LeaveAuthor());
    }

    // =================================================================================================================
    // The registry
    // =================================================================================================================

    private sealed class NoActions : IAuthorCommandActions, IShellActions
    {
        public AssetAuthorModel? Author => null;
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
        public Task OpenCaseAsync() => Task.CompletedTask;
        public Task OpenRunFolderAsync() => Task.CompletedTask;
        public Task ExitAsync() => Task.CompletedTask;
        public Task ShowAboutAsync() => Task.CompletedTask;
        public Task ShowThirdPartyNoticesAsync() => Task.CompletedTask;
        public Task ShowLicenceAsync() => Task.CompletedTask;
    }

    [Fact]
    public void The_Author_commands_are_Author_and_desktop_only_with_their_gestures_and_a_context_menu_for_every_kind()
    {
        var r = new CommandRegistry();
        var actions = new NoActions();
        ShellCommands.Register(r, actions);
        IReadOnlyList<AppCommand> author = AuthorCommands.Register(r, actions);
        Assert.Equal(11, author.Count);
        Assert.All(author, c => Assert.Equal(CommandModes.Author, c.Modes));
        Assert.All(author, c => Assert.Equal(CommandPlatforms.Desktop, c.Platforms));
        Assert.All(author, c => Assert.False(c.IsEnabled));   // nothing to act on while not authoring

        // Author mode: Edit and Case join the menu bar between File and Help; the shortcuts are the plan's.
        Assert.Equal(new[] { "File", "Edit", "Case", "Help" }, r.MenuTree(CommandModes.Author, CommandPlatforms.Desktop).Select(n => n.Title));
        Assert.Equal(new[] { "Undo", "Redo" }, r.MenuTree(CommandModes.Author, CommandPlatforms.Desktop)[1].Children.Select(n => n.Title));
        Assert.Equal(new[] { "Check", "Save", "Save As...", "Switch to Run" }, r.MenuTree(CommandModes.Author, CommandPlatforms.Desktop)[2].Children.Select(n => n.Title));
        IReadOnlyDictionary<string, AppCommand> keys = r.Gestures(CommandModes.Author, CommandPlatforms.Desktop);
        Assert.Equal(AuthorCommands.Undo, keys["Ctrl+Z"].Id);
        Assert.Equal(AuthorCommands.Redo, keys["Ctrl+Y"].Id);
        Assert.Equal(AuthorCommands.Save, keys["Ctrl+S"].Id);
        Assert.Equal(AuthorCommands.SaveAs, keys["Ctrl+Shift+S"].Id);
        // Run mode is exactly as before: no Edit or Case menu, none of these keys.
        Assert.Equal(new[] { "File", "Help" }, r.MenuTree(CommandModes.Run, CommandPlatforms.Desktop).Select(n => n.Title));
        foreach (string g in new[] { "Ctrl+Z", "Ctrl+Y", "Ctrl+S", "Ctrl+Shift+S" })
            Assert.False(r.Gestures(CommandModes.Run, CommandPlatforms.Desktop).ContainsKey(g));
        // Mobile: none of them (mobile has no Author mode; the registry would refuse one).
        Assert.DoesNotContain(r.Commands, c => c.IsOfferedIn(CommandModes.Author, CommandPlatforms.Mobile) && author.Contains(c));
        Assert.Empty(r.MenuTree(CommandModes.Author, CommandPlatforms.Mobile));

        // A context menu for every record kind and the class node in Author mode; none in Run mode.
        foreach (CommandTargets kind in new[] { CommandTargets.Bus, CommandTargets.Generator, CommandTargets.Demand, CommandTargets.Branch,
                     CommandTargets.Transformer, CommandTargets.Oltc, CommandTargets.Boundary, CommandTargets.Protection,
                     CommandTargets.AssetClass, CommandTargets.AssetBinding })
        {
            List<string> ids = r.ForTarget(kind, CommandModes.Author, CommandPlatforms.Desktop).Select(c => c.Id).ToList();
            bool keyed = (AuthorCommands.KeyedKinds & kind) != 0;
            Assert.Equal(keyed ? new[] { AuthorCommands.Duplicate, AuthorCommands.Delete, AuthorCommands.Rename, AuthorCommands.WhereUsed }
                : new[] { AuthorCommands.Duplicate, AuthorCommands.Delete }, ids);
            Assert.Empty(r.ForTarget(kind, CommandModes.Run, CommandPlatforms.Desktop));
        }
        Assert.Equal(new[] { AuthorCommands.AddRecord }, r.ForTarget(CommandTargets.AssetClassNode, CommandModes.Author, CommandPlatforms.Desktop).Select(c => c.Id));

        // Registering them twice is refused (each declared once).
        Assert.Throws<ArgumentException>(() => AuthorCommands.Register(r, actions));
    }
}
