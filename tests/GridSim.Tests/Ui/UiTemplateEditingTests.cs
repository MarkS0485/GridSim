using System.Globalization;
using System.Text;
using GridSim.Core.Assets;
using GridSim.Core.IO.Json;
using GridSim.Core.IO.Json.CaseEditing;
using GridSim.Core.IO.Json.Schema;
using GridSim.Core.Model;
using GridSim.Tests.Editor;
using GridSim.Tests.V2;
using GridSim.Ui.Assets.Editing;
using GridSim.Ui.Assets.Editing.Templates;
using GridSim.Ui.Assets.Index;
using GridSim.Ui.Assets.Keys;
using GridSim.Ui.Commands;
using Xunit;

namespace GridSim.Tests.Ui;

/// <summary>
/// UI programme Phase 4.4 (2026-10-21): inverter templates in GridSim.Ui's Author mode, on temporary copies only
/// (UiTemplateFixture: data/uk projected to v2 with the committed template fixture merged into its manifest). Pinned: a
/// template's parameter grid has exactly the rows of its kind spec (read here from schemas/v2/kinds on disk, apart from
/// the editor's embedded copy), each with the spec's unit, range, default, requiredness, enum values and help; an edit
/// changes exactly that value token (lossless) and is one undo step; a value the kind spec refuses is refused with the
/// kind spec's own message; add, duplicate and delete with their refusals (delete is refused while the template is used,
/// listing every use); bind and rebind (a generator's own template member, binding rules); an override set against the
/// template's value ("template 0.05 -> override 0.08") and reset to the template; the effective parameters shown in the
/// editor equal, by bits, what V2CaseLoader resolves for each generator after Save and a reload; Check catches a value
/// out of range with the kind spec's message; a library template is read only and can be overridden or copied; the
/// TEMPLATES subtree, the generator editor's template link, and the template commands (Author and desktop only, their
/// own node kinds, so no record's menu changes).
/// </summary>
public sealed class UiTemplateEditingTests
{
    private const string Wind = "gridsim.local.wind-gfl@1", Solar = "gridsim.local.solar-gfm@1", Spare = "gridsim.local.spare-gfl@1";

    private static CaseAuthoring Open(string manifest)
        => CaseAuthoring.Open(AssetCaseSource.V2(manifest), CaseSessionTests.Unlimited(), Array.Empty<string>());

    private static AssetAuthorModel Author(UiTemplateFixture f) => new(Open(f.ManifestPath), f.DataRoot);

    // The kind specs read from the files on disk (not the editor's embedded copy), through the loader's own parser.
    private static KindSpecRegistry DiskKinds() => KindSpecRegistry.LoadDirectory(V2TestSupport.KindsDir, V2TestSupport.RealSchemas());

    private static byte[] ManifestBytes(AssetAuthorModel m) => m.Authoring.Session.Documents[0].Text.ToArray();

    // The text with the FIRST occurrence of <from> replaced by <to> (and the occurrence must exist).
    private static byte[] ReplaceFirst(byte[] bytes, string from, string to)
    {
        string s = Encoding.UTF8.GetString(bytes);
        int at = s.IndexOf(from, StringComparison.Ordinal);
        Assert.True(at >= 0, "'" + from + "' is not in the text");
        return Encoding.UTF8.GetBytes(s[..at] + to + s[(at + from.Length)..]);
    }

    private static GeneratorBindingInfo Gen(AssetAuthorModel m, string name) => m.Templates().Generators.Single(g => g.Name == name);

    private static AuthorNode TemplateNode(AssetAuthorModel m, string reference) => m.TemplateNode(reference) ?? throw new InvalidOperationException(reference + " has no node");

    // =================================================================================================================
    // The parameter grid is the kind spec
    // =================================================================================================================

    [Fact]
    public void The_parameter_grid_rows_equal_the_kind_spec_exactly()
    {
        using var f = new UiTemplateFixture();
        AssetAuthorModel m = Author(f);
        KindSpecRegistry disk = DiskKinds();
        foreach (string reference in new[] { Wind, Solar, Spare })
        {
            Assert.True(m.Select(TemplateNode(m, reference)));
            Assert.Equal(new[] { "OVERVIEW", "PROPERTIES" }, m.SubTabs);
            m.SelectedSubTab = "PROPERTIES";
            Assert.True(m.IsTemplateEditorShown);
            Assert.False(m.IsFieldGridShown);
            TemplateEditorModel ed = m.TemplateEditor!;
            Assert.Equal(reference, ed.Ref);
            KindSpec spec = disk.Get(ed.Info.Kind);

            // One row per kind-spec parameter, in the kind file's order, and nothing else (the fixture writes no stray key).
            Assert.Equal(spec.Params.Keys, ed.Params.Rows.Select(r => r.Field));
            Assert.Equal(spec.Params.Keys, ed.ParamSpecs.Select(p => p.Name));
            foreach ((FieldRow row, TemplateParamInfo info) in ed.Params.Rows.Zip(ed.ParamSpecs))
            {
                KindParamSpec p = spec.Params[row.Field];
                Assert.Equal(p.Unit ?? "", row.Unit);
                Assert.Equal(p.Unit ?? "", info.Unit);
                Assert.Equal(p.Min, info.Min);
                Assert.Equal(p.Max, info.Max);
                Assert.Equal(p.Required, row.Required);
                Assert.Equal(p.Required, info.Required);
                Assert.Equal(p.Values, row.EnumChoices);
                Assert.Equal(p.Values, info.Values);
                Assert.StartsWith(p.Description, row.Help, StringComparison.Ordinal);
                Assert.Equal(p.Description, info.Help);
                // The range and the code default, in words, from the spec.
                if (p.Min is double lo && p.Max is double hi)
                    Assert.Equal(lo.ToString("R", CultureInfo.InvariantCulture) + " to " + hi.ToString("R", CultureInfo.InvariantCulture) + (p.Unit is null or "1" ? "" : " " + p.Unit), info.RangeText);
                Assert.Equal(info.DefaultText, row.AbsentText);
                if (p.CodeDefaults.Count == 0) Assert.StartsWith("none", info.DefaultText, StringComparison.Ordinal);
                else
                    foreach (KindCodeDefault d in p.CodeDefaults)
                        Assert.Contains(d.Value.ValueKind switch
                        {
                            System.Text.Json.JsonValueKind.Number => d.Value.GetDouble().ToString("R", CultureInfo.InvariantCulture),
                            System.Text.Json.JsonValueKind.String => d.Value.GetString()!,
                            _ => d.Value.GetRawText(),
                        }, info.DefaultText, StringComparison.Ordinal);
                // What is written shows as written; what is not shows absent with that default.
                TemplateParamLiteral? written = ed.Info.Param(row.Field);
                Assert.Equal(written is null ? FieldState.Absent : FieldState.Set, row.State);
                Assert.Equal(written?.Literal ?? "", row.Literal);
            }
        }

        // The wind template's numbers as written, the object form's v shown (its unit and provenance kept).
        m.Select(TemplateNode(m, Wind));
        m.SelectedSubTab = "PROPERTIES";
        FieldGridModel g = m.TemplateEditor!.Params;
        Assert.Equal("0.05", g.Row("deadbandHz").Literal);
        Assert.Equal("Hz", g.Row("deadbandHz").Unit);
        Assert.Equal("0.25", g.Row("actuateTauS").Literal);
        Assert.Contains("object form", g.Row("actuateTauS").StateText, StringComparison.Ordinal);
        Assert.Equal("0 to 5 Hz", m.TemplateEditor.ParamSpecs.Single(p => p.Name == "deadbandHz").RangeText);
        Assert.True(g.Row("batteryChemistry").HasChoices);   // an enum parameter lists its values (checked against the spec above)
    }

    // =================================================================================================================
    // Lossless edits, refusals with the kind spec's message
    // =================================================================================================================

    [Fact]
    public void An_edit_changes_exactly_that_token_and_is_one_undo_step()
    {
        using var f = new UiTemplateFixture();
        AssetAuthorModel m = Author(f);
        m.Select(TemplateNode(m, Wind));
        m.SelectedSubTab = "PROPERTIES";
        byte[] before = ManifestBytes(m);
        byte[] disk = File.ReadAllBytes(f.ManifestPath);
        Assert.Equal(disk, before);

        FieldRow dead = m.TemplateEditor!.Params.Row("deadbandHz");
        dead.EditText = "0.06";
        AuthoringResult r = m.CommitField(dead);
        Assert.True(r.Ok, r.Message);
        Assert.True(r.Changed);
        Assert.Equal(ReplaceFirst(before, "\"deadbandHz\": 0.05", "\"deadbandHz\": 0.06"), ManifestBytes(m));
        Assert.Equal("0.06", m.TemplateEditor!.Params.Row("deadbandHz").Literal);   // the page was rebuilt from the session
        Assert.Equal(1, m.Authoring.UndoDepth);

        // The object form: only its v changes; its unit and provenance stay exactly as written.
        byte[] mid = ManifestBytes(m);
        Assert.True(m.TemplateEditor.Params.Edit("actuateTauS", "0.3").Ok);
        Assert.Equal(ReplaceFirst(mid, "\"v\": 0.25", "\"v\": 0.3"), ManifestBytes(m));

        // Typing the same value is no change and no undo step; an absent parameter is inserted in the kind file's order.
        Assert.False(m.TemplateEditor.Params.Edit("deadbandHz", "0.060").Changed);
        Assert.Equal(2, m.Authoring.UndoDepth);
        Assert.True(m.TemplateEditor.Params.Edit("rocofGainMwsPerHz", "1.5").Ok);
        string text = Encoding.UTF8.GetString(ManifestBytes(m));
        int droop = text.IndexOf("\"droopGainMwPerHzPerMw\"", StringComparison.Ordinal), rocof = text.IndexOf("\"rocofGainMwsPerHz\"", StringComparison.Ordinal),
            tau = text.IndexOf("\"actuateTauS\"", StringComparison.Ordinal);
        Assert.True(droop < rocof && rocof < tau, "rocofGainMwsPerHz must land between droopGainMwPerHzPerMw and actuateTauS (the kind file's order)");

        // Reset removes it again; undo of everything gives the bytes on disk back exactly.
        Assert.True(m.TemplateEditor.Params.Reset("rocofGainMwsPerHz").Ok);
        while (m.Authoring.CanUndo) m.Undo();
        Assert.Equal(disk, ManifestBytes(m));

        // Save writes only the edited token.
        m.Select(TemplateNode(m, Wind));
        m.SelectedSubTab = "PROPERTIES";
        Assert.True(m.TemplateEditor!.Params.Edit("deadbandHz", "0.07").Ok);
        AuthoringReport saved = m.Save();
        Assert.True(saved.Saved, string.Join(Environment.NewLine, saved.Lines()));
        Assert.Equal(ReplaceFirst(disk, "\"deadbandHz\": 0.05", "\"deadbandHz\": 0.07"), File.ReadAllBytes(f.ManifestPath));
    }

    [Fact]
    public void A_value_the_kind_spec_refuses_is_refused_with_the_kind_specs_message()
    {
        using var f = new UiTemplateFixture();
        AssetAuthorModel m = Author(f);
        m.Select(TemplateNode(m, Wind));
        m.SelectedSubTab = "PROPERTIES";
        byte[] before = ManifestBytes(m);
        FieldGridModel g = m.TemplateEditor!.Params;

        // Above the kind's maximum (deadbandHz: 0 to 5 Hz): the kind spec's own message, from KindSpecRegistry.CheckParams.
        AuthoringResult high = g.Edit("deadbandHz", "9");
        Assert.False(high.Ok);
        Assert.Contains("9 Hz is above the maximum 5 for 'deadbandHz'.", high.Message, StringComparison.Ordinal);
        Assert.Contains("/localTemplates/0/params/deadbandHz", high.Message, StringComparison.Ordinal);
        AuthoringResult low = g.Edit("headroomUpFraction", "-0.1");
        Assert.False(low.Ok);
        Assert.Contains("-0.1 1 is below the minimum 0 for 'headroomUpFraction'.", low.Message, StringComparison.Ordinal);
        // Not a number at all: refused by the strict parse before the kind check.
        Assert.False(g.Edit("deadbandHz", "fast").Ok);
        // A boolean parameter takes y / n / true / false only.
        Assert.False(g.Edit("rmsBlockInDip", "maybe").Ok);
        // Through the Author model (as the view's SET does), the refusal is the status line's, in the alarm colour.
        FieldRow dead = g.Row("deadbandHz");
        dead.EditText = "9";
        Assert.False(m.CommitField(dead).Ok);
        Assert.True(m.StatusIsError);
        Assert.Contains("9 Hz is above the maximum 5 for 'deadbandHz'.", m.Status, StringComparison.Ordinal);
        Assert.Equal(before, ManifestBytes(m));
        Assert.Equal(0, m.Authoring.UndoDepth);
    }

    [Fact]
    public void Check_catches_a_parameter_out_of_range_with_the_kind_specs_message()
    {
        // The value is written in the file (an edit in AUTHOR would be refused), as a hand-edited case might hold it.
        using var f = new UiTemplateFixture(block => block.Replace("\"deadbandHz\": 0.05", "\"deadbandHz\": 9", StringComparison.Ordinal));
        AssetAuthorModel m = Author(f);
        AuthoringReport check = m.Check();
        Assert.Equal(AuthoringOutcome.CheckFailed, check.Outcome);
        AuthoringProblem p = Assert.Single(check.Errors, e => e.Message.Contains("is above the maximum", StringComparison.Ordinal));
        Assert.Contains("/localTemplates/0/params/deadbandHz: 9 Hz is above the maximum 5 for 'deadbandHz'.", p.Message, StringComparison.Ordinal);
        Assert.StartsWith("the v2 loader refuses the case", p.Message, StringComparison.Ordinal);

        // The template's own CHECK sub-tab lists it; the other templates' do not.
        m.Select(TemplateNode(m, Wind));
        Assert.Contains("CHECK", m.SubTabs);
        Assert.Contains(m.CheckPage!.Problems, e => e.Message.Contains("'deadbandHz'", StringComparison.Ordinal));
        m.Select(TemplateNode(m, Solar));
        Assert.Empty(m.CheckPage!.Problems);

        // Fixed in the grid (one undo step), Check passes.
        m.Select(TemplateNode(m, Wind));
        m.SelectedSubTab = "PROPERTIES";
        Assert.True(m.TemplateEditor!.Params.Edit("deadbandHz", "0.05").Ok);
        Assert.Equal(AuthoringOutcome.CheckPassed, m.Check().Outcome);
    }

    // =================================================================================================================
    // Add, duplicate, delete
    // =================================================================================================================

    [Fact]
    public void Templates_add_duplicate_and_delete_with_their_refusals()
    {
        using var f = new UiTemplateFixture();
        AssetAuthorModel m = Author(f);
        m.Select(m.TemplatesNode!);
        Assert.True(m.CanAddTemplate);

        // Add: an inverter kind and a name; the new template is selected, active, with no parameter set.
        AuthoringResult add = m.AddTemplate("gridFormingInverter", "new-gfm", "A new grid-forming inverter");
        Assert.True(add.Ok, add.Message);
        Assert.Equal("gridsim.local.new-gfm@1", m.Selected!.TemplateRef);
        TemplateInfo added = m.Templates().Find("gridsim.local.new-gfm@1")!;
        Assert.True(added.IsLocal);
        Assert.Equal("gridFormingInverter", added.Kind);
        Assert.Equal("active", added.Status);
        Assert.Empty(added.Params);
        Assert.Equal(AuthoringOutcome.CheckPassed, m.Check().Outcome);   // a new template keeps the case loadable

        // Refusals: the same id again, a kind that is not an inverter kind, a name the id pattern refuses.
        Assert.Contains("is already used by gridsim.local.new-gfm@1", m.AddTemplate("gridFormingInverter", "new-gfm").Message, StringComparison.Ordinal);
        Assert.Contains("'oltc' is not an inverter template kind", m.AddTemplate("oltc", "x").Message, StringComparison.Ordinal);
        Assert.Contains("is not a template id", m.AddTemplate("gridFollowingInverter", "bad name").Message, StringComparison.Ordinal);
        Assert.Equal(TemplateKinds.InverterKinds, new[] { "gridFollowingInverter", "gridFormingInverter" });

        // Duplicate a local template: a byte copy with a fresh id; bindings still name the source.
        Assert.True(m.Select(TemplateNode(m, Wind)));
        AuthoringResult dup = m.DuplicateSelectedTemplate();
        Assert.True(dup.Ok, dup.Message);
        Assert.Equal("gridsim.local.wind-gfl-copy@1", m.Selected!.TemplateRef);
        TemplateInfo copy = m.Templates().Find("gridsim.local.wind-gfl-copy@1")!;
        Assert.Equal(m.Templates().Find(Wind)!.Params, copy.Params);
        Assert.Empty(m.Templates().RulesFor(copy.Ref));
        Assert.True(m.Select(TemplateNode(m, Wind)));
        Assert.True(m.DuplicateSelectedTemplate().Ok);
        Assert.NotNull(m.Templates().Find("gridsim.local.wind-gfl-copy-2@1"));

        // Delete is refused while the template is used, listing every use (two rules and the override).
        Assert.True(m.Select(TemplateNode(m, Wind)));
        AuthoringResult refused = m.DeleteSelectedTemplate();
        Assert.False(refused.Ok);
        Assert.StartsWith("Delete refused: 3 thing(s) in the case use template " + Wind, refused.Message, StringComparison.Ordinal);
        Assert.Contains("uk.case.json /bindings/0 (binding rule for type 'onshore')", refused.Message, StringComparison.Ordinal);
        Assert.Contains("uk.case.json /bindings/1 (binding rule for type 'offshore')", refused.Message, StringComparison.Ordinal);
        Assert.Contains("uk.case.json /overrides/0 (override: test fixture: a site-specific deadband)", refused.Message, StringComparison.Ordinal);
        Assert.NotNull(m.Templates().Find(Wind));

        // ... and while a generator names it itself.
        Assert.True(m.BindGenerator(Gen(m, "Drax Biomass").Record, Spare).Ok);
        Assert.True(m.Select(TemplateNode(m, Spare)));
        AuthoringResult byGen = m.DeleteSelectedTemplate();
        Assert.False(byGen.Ok);
        Assert.Contains("'Drax Biomass' (its own template member)", byGen.Message, StringComparison.Ordinal);
        Assert.True(m.UnbindGenerator(Gen(m, "Drax Biomass").Record).Ok);

        // Unused: deleted (one undo step), and undo brings it back.
        Assert.True(m.Select(TemplateNode(m, Spare)));
        int depth = m.Authoring.UndoDepth;
        Assert.True(m.DeleteSelectedTemplate().Ok);
        Assert.Null(m.Templates().Find(Spare));
        Assert.Equal(depth + 1, m.Authoring.UndoDepth);
        m.Undo();
        Assert.NotNull(m.Templates().Find(Spare));
    }

    // =================================================================================================================
    // Bindings
    // =================================================================================================================

    [Fact]
    public void Generators_bind_and_rebind_by_their_own_member_and_by_rules()
    {
        using var f = new UiTemplateFixture();
        AssetAuthorModel m = Author(f);
        CaseTemplates t = m.Templates();
        Assert.Equal(new[] { "type 'onshore' -> " + Wind, "type 'offshore' -> " + Wind, "type 'solar' -> " + Solar }, t.Bindings.Select(b => b.Text));
        Assert.Equal(Wind, Gen(m, "North Scotland Onshore Wind").Template);
        Assert.Equal("binding rule #0 type 'onshore'", Gen(m, "North Scotland Onshore Wind").Route);
        Assert.Equal("binding rule #1 type 'offshore'", Gen(m, "East Anglia Offshore Wind").Route);
        Assert.Equal(Solar, Gen(m, "South Solar").Template);
        Assert.Null(Gen(m, "Drax Biomass").Template);
        Assert.Equal(5, t.GeneratorsBoundTo(Wind).Count);
        Assert.Empty(t.GeneratorsBoundTo(Spare));

        // Bind one generator from its template's node (its own template member: it wins over the solar rule).
        Assert.True(m.Select(TemplateNode(m, Spare)));
        AuthoringResult bound = m.BindToSelectedTemplate("South Solar", null);
        Assert.True(bound.Ok, bound.Message);
        Assert.Equal(Spare, Gen(m, "South Solar").InlineTemplate);
        Assert.Equal(Spare, Gen(m, "South Solar").Template);
        Assert.Equal("its own template member", Gen(m, "South Solar").Route);
        // Exactly one of a name or a type, and a name that is unknown, are refused.
        Assert.False(m.BindToSelectedTemplate("South Solar", "hydro").Ok);
        Assert.False(m.BindToSelectedTemplate(null, null).Ok);
        Assert.Contains("no generator is named 'Nowhere'", m.BindToSelectedTemplate("Nowhere", null).Message, StringComparison.Ordinal);

        // A new rule for a type; the same type again is refused (the first rule would always win).
        Assert.True(m.BindToSelectedTemplate(null, "hydro").Ok);
        Assert.Equal(Spare, Gen(m, "North Scotland Hydro").Template);
        Assert.Equal("binding rule #3 type 'hydro'", Gen(m, "North Scotland Hydro").Route);
        Assert.Contains("binding rule #3 already binds type 'hydro'", m.AddBindingRule(Wind, "HYDRO").Message, StringComparison.Ordinal);

        // Rebind a rule; remove one; unbind the generator (the rules then apply again).
        Assert.True(m.RebindRule(2, Wind).Ok);
        Assert.Equal(Wind, m.Templates().Bindings[2].Template);
        Assert.True(m.RemoveRule(3).Ok);
        Assert.Null(Gen(m, "North Scotland Hydro").Template);
        Assert.True(m.UnbindGenerator(Gen(m, "South Solar").Record).Ok);
        Assert.Equal(Wind, Gen(m, "South Solar").Template);   // the solar rule, rebound above
        Assert.False(m.UnbindGenerator(Gen(m, "South Solar").Record).Changed);

        // Refusals: a template the case cannot see, a placeholder-free but non-inverter reference is never offered.
        Assert.Contains("is neither imported nor a local template of the case", m.BindGenerator(Gen(m, "Drax Biomass").Record, "gridsim.local.nothing@1").Message, StringComparison.Ordinal);
        Assert.Contains("is neither imported nor a local template of the case", m.RebindRule(0, "gridsim.local.nothing@1").Message, StringComparison.Ordinal);
        Assert.Equal(new[] { Wind, Solar, Spare }, TemplatePages.BindableTemplates(m.Templates()));
        Assert.Equal(AuthoringOutcome.CheckPassed, m.Check().Outcome);
    }

    // =================================================================================================================
    // Overrides
    // =================================================================================================================

    [Fact]
    public void An_override_is_shown_against_the_template_value_set_and_reset_to_the_template()
    {
        using var f = new UiTemplateFixture();
        AssetAuthorModel m = Author(f);
        TemplateOverrideRow row = Assert.Single(TemplatePages.OverridesOf(m.Templates(), Wind));
        Assert.Equal("deadbandHz", row.Param);
        Assert.Equal("template 0.05 -> override 0.08", row.Text);
        Assert.Equal("Hz", row.Unit);
        Assert.Equal("test fixture: a site-specific deadband", row.Reason);

        // The override node under its template, and its page.
        AuthorNode node = Assert.Single(TemplateNode(m, Wind).Children);
        Assert.Equal(AuthorNodeKind.TemplateOverride, node.Kind);
        Assert.Equal(CommandTargets.TemplateOverride, node.Target);
        m.Select(node);
        Assert.Equal("template 0.05 -> override 0.08", m.OverridePage!.Row.Text);

        // A second parameter joins the template's existing override entry (no new reason needed).
        byte[] before = ManifestBytes(m);
        AuthoringResult set = m.SetOverride(Wind, "actuateTauS", "0.3", null);
        Assert.True(set.Ok, set.Message);
        Assert.Equal(new[] { "template 0.05 -> override 0.08", "template 0.25 -> override 0.3" }, TemplatePages.OverridesOf(m.Templates(), Wind).Select(o => o.Text));
        Assert.Single(m.Templates().Overrides);
        // Changing an overridden value replaces its token only.
        byte[] mid = ManifestBytes(m);
        Assert.True(m.SetOverride(Wind, "deadbandHz", "0.09", null).Ok);
        Assert.Equal(ReplaceFirst(mid, "\"deadbandHz\": 0.08", "\"deadbandHz\": 0.09"), ManifestBytes(m));

        // A template with no override yet needs a reason; then a new entry, shadow explicit.
        Assert.Contains("a new override must say why", m.SetOverride(Solar, "dampingMwPerHz", "2500", "  ").Message, StringComparison.Ordinal);
        Assert.True(m.SetOverride(Solar, "dampingMwPerHz", "2500", "test: a stiffer damping leg").Ok);
        TemplateOverrideEntry solarEntry = Assert.Single(m.Templates().OverridesOf(Solar));
        Assert.Equal("test: a stiffer damping leg", solarEntry.Reason);
        Assert.Contains("\"shadow\": \"explicit\"", Encoding.UTF8.GetString(ManifestBytes(m)), StringComparison.Ordinal);
        Assert.Equal("template 2000 -> override 2500", Assert.Single(TemplatePages.OverridesOf(m.Templates(), Solar)).Text);

        // Refused: out of range after the merge (the kind spec's message), an undeclared parameter.
        AuthoringResult high = m.SetOverride(Wind, "deadbandHz", "7", null);
        Assert.False(high.Ok);
        Assert.Contains("7 Hz is above the maximum 5 for 'deadbandHz'.", high.Message, StringComparison.Ordinal);
        Assert.Contains("kind 'gridFollowingInverter' has no parameter 'h'", m.SetOverride(Wind, "h", "4", "x").Message, StringComparison.Ordinal);

        // Reset to the template: the member goes; the last one takes its now-empty entry with it.
        Assert.True(m.ResetOverride(Solar, "dampingMwPerHz").Ok);
        Assert.Empty(m.Templates().OverridesOf(Solar));
        Assert.True(m.ResetOverride(Wind, "actuateTauS").Ok);
        Assert.Equal("template 0.05 -> override 0.09", Assert.Single(TemplatePages.OverridesOf(m.Templates(), Wind)).Text);
        m.Select(Assert.Single(TemplateNode(m, Wind).Children));
        Assert.True(m.ResetSelectedOverride().Ok);
        Assert.Empty(m.Templates().Overrides);
        Assert.False(m.ResetOverride(Wind, "deadbandHz").Changed);
        Assert.Equal(AuthoringOutcome.CheckPassed, m.Check().Outcome);
        // Undo of every step gives the fixture's bytes back.
        while (m.Authoring.CanUndo) m.Undo();
        Assert.Equal(File.ReadAllBytes(f.ManifestPath), ManifestBytes(m));
        Assert.NotEqual(before, Array.Empty<byte>());
    }

    // =================================================================================================================
    // The effective parameters are the loader's
    // =================================================================================================================

    [Fact]
    public void The_effective_parameters_equal_what_the_v2_loader_resolves_after_save_and_reload_by_bits()
    {
        using var f = new UiTemplateFixture();
        AssetAuthorModel m = Author(f);
        // Edits of every kind: a template value, an override, a rebound rule, a generator bound by its own member.
        m.Select(TemplateNode(m, Wind));
        m.SelectedSubTab = "PROPERTIES";
        Assert.True(m.TemplateEditor!.Params.Edit("droopGainMwPerHzPerMw", "3.5").Ok);
        Assert.True(m.SetOverride(Wind, "actuateTauS", "0.3", null).Ok);
        Assert.True(m.RebindRule(1, Spare).Ok);   // offshore -> spare
        Assert.True(m.BindGenerator(Gen(m, "Drax Biomass").Record, Solar).Ok);

        // The generator editor shows the effective parameters once asked for, from the loader.
        CaseRef onshore = Gen(m, "North Scotland Onshore Wind").Record;
        Assert.True(m.SelectRecord(onshore));
        Assert.Equal(Wind, m.GeneratorTemplate!.Template!.Ref);
        Assert.Null(m.GeneratorTemplate.Effective);
        TemplateResolution res = m.ShowEffectiveParameters();
        Assert.True(res.Loaded, res.Error);
        EffectiveBinding mine = m.GeneratorTemplate!.Effective!;
        Assert.Same(mine, m.Editor!.TemplateLink!.Effective);
        Assert.Equal("wind-gfl", mine.ClassName);
        Assert.Equal(3.5, mine.Values["droopGainMwPerHzPerMw"]);
        Assert.Equal(0.3, mine.Values["actuateTauS"]);
        Assert.Equal(0.08, mine.Values["deadbandHz"]);
        Assert.Equal("override (test fixture: a site-specific deadband)", mine.Params.Single(p => p.Name == "deadbandHz").Source);
        Assert.Equal("template", mine.Params.Single(p => p.Name == "droopGainMwPerHzPerMw").Source);
        Assert.Equal("spare-gfl", res.For(Gen(m, "East Anglia Offshore Wind").Record)!.ClassName);
        Assert.Equal("solar-gfm", res.For(Gen(m, "Drax Biomass").Record)!.ClassName);
        Assert.False(res.For(Gen(m, "Heysham Nuclear").Record)!.IsBound);

        // Save, then reload with the v2 loader as the run pipeline does (dispatch on): every generator's class and every
        // parameter, bit for bit.
        AuthoringReport saved = m.Save();
        Assert.True(saved.Saved, string.Join(Environment.NewLine, saved.Lines()));
        GridModel model = V2CaseLoader.Load(f.ManifestPath, V2TestSupport.SchemasDir, dataRoot: null, dispatch: true);
        AssetDynamicsModel dyn = model.AssetDynamics!;
        Assert.Equal(res.Generators.Count, model.Generators.Count);
        int bound = 0;
        for (int i = 0; i < model.Generators.Count; i++)
        {
            Generator g = model.Generators[i];
            EffectiveBinding e = res.Generators.Single(x => x.Generator.Name == g.Name);
            string? cls = dyn.Bindings.Resolve(g.DynamicsClass, g.Type);
            Assert.Equal(e.ClassName, cls);
            if (cls is null) { Assert.Empty(e.Values); continue; }
            bound++;
            IReadOnlyDictionary<string, double> loaded = dyn.Library.Get(cls).Params;
            Assert.Equal(loaded.Keys, e.Values.Keys);
            foreach ((string k, double v) in loaded)
                Assert.Equal(BitConverter.DoubleToInt64Bits(v), BitConverter.DoubleToInt64Bits(e.Values[k]));
        }
        Assert.Equal(7, bound);   // 3 onshore + 2 offshore + solar + Drax
    }

    [Fact]
    public void A_library_template_is_read_only_and_can_be_overridden_or_copied()
    {
        using var f = new UiTemplateFixture(library: true);
        AssetAuthorModel m = Author(f);
        CaseTemplates t = m.Templates();
        TemplateInfo lib = Assert.Single(t.Library);
        Assert.Equal(UiTemplateFixture.LibraryRef, lib.Ref);
        Assert.False(lib.IsLocal);
        Assert.Empty(t.Notes);

        AuthorNode node = TemplateNode(m, UiTemplateFixture.LibraryRef);
        Assert.Equal(TemplatePages.LibraryNodeId(UiTemplateFixture.LibraryRef), node.Id);
        Assert.Contains("library, read only", node.Detail, StringComparison.Ordinal);
        m.Select(node);
        Assert.False(m.HasSelectedLocalTemplate);
        m.SelectedSubTab = "PROPERTIES";
        FieldGridModel g = m.TemplateEditor!.Params;
        Assert.All(g.Rows, r => Assert.Equal(FieldState.Locked, r.State));
        Assert.Equal("250", g.Row("synInertiaMwsPerHz").Literal);
        Assert.False(g.Edit("synInertiaMwsPerHz", "300").Ok);
        Assert.False(g.Reset("synInertiaMwsPerHz").Ok);
        Assert.Contains("is a library template the case imports", m.DeleteSelectedTemplate().Message, StringComparison.Ordinal);

        // Its values can be overridden for this case, and it can be copied in as a local template.
        Assert.True(m.SetOverride(UiTemplateFixture.LibraryRef, "dampingMwPerHz", "1800", "test: site damping").Ok);
        Assert.Equal("template 1500 -> override 1800", Assert.Single(TemplatePages.OverridesOf(m.Templates(), UiTemplateFixture.LibraryRef)).Text);
        m.Select(TemplateNode(m, UiTemplateFixture.LibraryRef));
        AuthoringResult copy = m.DuplicateSelectedTemplate();
        Assert.True(copy.Ok, copy.Message);
        TemplateInfo local = m.Templates().Find("gridsim.local.test.lib-gfm@1")!;
        Assert.True(local.IsLocal);
        Assert.Equal(lib.Params, local.Params);

        // Bound to a generator, the effective parameters (data root given) carry the override, as the loader resolves them.
        Assert.True(m.BindGenerator(Gen(m, "Drax Biomass").Record, UiTemplateFixture.LibraryRef).Ok);
        TemplateResolution res = TemplateResolution.Load(m.Authoring, f.DataRoot);
        Assert.True(res.Loaded, res.Error);
        EffectiveBinding drax = res.For(Gen(m, "Drax Biomass").Record)!;
        Assert.Equal(UiTemplateFixture.LibraryRef, drax.ClassName);
        Assert.Equal(1800.0, drax.Values["dampingMwPerHz"]);
        Assert.Equal(250.0, drax.Values["synInertiaMwsPerHz"]);
    }

    // =================================================================================================================
    // The tree, the generator editor's link, the commands
    // =================================================================================================================

    [Fact]
    public void The_TEMPLATES_subtree_and_the_generator_editors_template_link()
    {
        using var f = new UiTemplateFixture();
        AssetAuthorModel m = Author(f);
        AuthorNode cls = m.TemplatesNode!;
        Assert.Equal(TemplatePages.ClassNodeLabel, cls.Label);
        Assert.Equal(AuthorNodeKind.TemplateClass, cls.Kind);
        Assert.Equal(new[] { CommandTargets.TemplateClassNode }, cls.MenuTargets);
        Assert.Equal(new[] { Wind, Solar, Spare }, cls.Children.Select(c => c.Label));
        Assert.All(cls.Children, c => Assert.Equal(new[] { CommandTargets.Template }, c.MenuTargets));
        Assert.Equal("gridFollowingInverter  local", cls.Children[0].Detail);
        m.Select(cls);
        Assert.Equal(new[] { "OVERVIEW" }, m.SubTabs);
        TemplatesPage page = m.TemplatesPage!;
        Assert.Equal(3, page.Templates.Count);
        Assert.Equal(3, page.Rules.Count);
        Assert.Single(page.Overrides);
        Assert.Equal(29, page.Generators.Count);

        // A generator's editor links to its template and shows that template's overrides.
        AuthorNode gen = m.ClassNode(CaseRoles.Generators)!.Children.Single(n => n.Label == "'North Scotland Onshore Wind'");
        Assert.Equal(new[] { CommandTargets.Generator }, gen.MenuTargets);   // the generator's menu is unchanged
        m.Select(gen);
        GeneratorTemplateModel link = m.Editor!.TemplateLink!;
        Assert.True(m.Editor.HasTemplateLink);
        Assert.Same(link, m.GeneratorTemplate);
        Assert.Equal(Wind, link.Template!.Ref);
        Assert.Equal(Wind + " (gridFollowingInverter), by binding rule #0 type 'onshore'.", link.BindingText);
        Assert.Equal("template 0.05 -> override 0.08", Assert.Single(link.Overrides).Text);
        Assert.Contains("applies to every generator bound to " + Wind, link.OverridesNote, StringComparison.Ordinal);
        Assert.False(link.HasOwnTemplate);
        Assert.True(m.OpenTemplate(link.Template.Ref));
        Assert.Equal(Wind, m.Selected!.TemplateRef);
        // An unbound generator says so.
        m.Select(m.ClassNode(CaseRoles.Generators)!.Children.Single(n => n.Label == "'Heysham Nuclear'"));
        Assert.False(m.Editor!.TemplateLink!.HasTemplate);
        Assert.StartsWith("Not bound to a template: unbound", m.Editor.TemplateLink.BindingText, StringComparison.Ordinal);

        // A v1 case has no TEMPLATES node and no template link (its asset classes are records of asset-classes.json).
        CaseAuthoring v1 = CaseAuthoring.Open(AssetCaseSource.V1(EditorTestSupport.CaseDir("uk")), CaseSessionTests.Unlimited());
        var mv1 = new AssetAuthorModel(v1);
        Assert.Null(mv1.TemplatesNode);
        mv1.Select(mv1.ClassNode(CaseRoles.Generators)!.Children[0]);
        Assert.Null(mv1.Editor!.TemplateLink);
    }

    private sealed class Actions : ITemplateAuthorActions
    {
        public AssetAuthorModel? Author { get; set; }
        public Task AddTemplateAsync() => Task.CompletedTask;
        public Task DuplicateTemplateAsync() => Task.CompletedTask;
        public Task DeleteTemplateAsync() => Task.CompletedTask;
        public Task BindToTemplateAsync() => Task.CompletedTask;
        public Task ResetOverrideAsync() => Task.CompletedTask;
    }

    [Fact]
    public void The_template_commands_are_Author_and_desktop_only_on_their_own_node_kinds()
    {
        var r = new CommandRegistry();
        var actions = new Actions();
        IReadOnlyList<AppCommand> cmds = TemplateAuthorCommands.Register(r, actions);
        Assert.Equal(new[] { "Add template...", "Duplicate", "Delete", "Bind to template...", "Reset override" }, cmds.Select(c => c.Title));
        Assert.All(cmds, c =>
        {
            Assert.Equal(CommandModes.Author, c.Modes);
            Assert.True(c.IsOfferedIn(CommandModes.Author, CommandPlatforms.Desktop));
            Assert.False(c.IsOfferedIn(CommandModes.Author, CommandPlatforms.Mobile));
            Assert.Null(c.Gesture);
            Assert.Null(c.MenuPath);
        });
        Assert.Equal(new[] { TemplateAuthorCommands.AddTemplate }, r.ForTarget(CommandTargets.TemplateClassNode, CommandModes.Author, CommandPlatforms.Desktop).Select(c => c.Id));
        Assert.Equal(new[] { TemplateAuthorCommands.Duplicate, TemplateAuthorCommands.Delete, TemplateAuthorCommands.Bind },
            r.ForTarget(CommandTargets.Template, CommandModes.Author, CommandPlatforms.Desktop).Select(c => c.Id));
        Assert.Equal(new[] { TemplateAuthorCommands.ResetOverride }, r.ForTarget(CommandTargets.TemplateOverride, CommandModes.Author, CommandPlatforms.Desktop).Select(c => c.Id));
        foreach (CommandTargets t in new[] { CommandTargets.TemplateClassNode, CommandTargets.Template, CommandTargets.TemplateOverride })
            Assert.Empty(r.ForTarget(t, CommandModes.Run, CommandPlatforms.Desktop));
        // No record kind gains a template command.
        foreach (CommandTargets t in Enum.GetValues<CommandTargets>().Where(t => t is not (CommandTargets.None or CommandTargets.TemplateClassNode or CommandTargets.Template or CommandTargets.TemplateOverride)))
            Assert.Empty(r.ForTarget(t, CommandModes.Author, CommandPlatforms.Desktop));
        // Declared once.
        Assert.Throws<ArgumentException>(() => TemplateAuthorCommands.Register(r, actions));

        // Enabled by the selection.
        using var f = new UiTemplateFixture();
        actions.Author = Author(f);
        AppCommand add = r.Get(TemplateAuthorCommands.AddTemplate), del = r.Get(TemplateAuthorCommands.Delete), reset = r.Get(TemplateAuthorCommands.ResetOverride);
        Assert.False(add.IsEnabled);
        actions.Author.Select(actions.Author.TemplatesNode!);
        Assert.True(add.IsEnabled);
        Assert.False(del.IsEnabled);
        actions.Author.Select(TemplateNode(actions.Author, Spare));
        Assert.True(del.IsEnabled);
        Assert.False(reset.IsEnabled);
        actions.Author.Select(Assert.Single(TemplateNode(actions.Author, Wind).Children));
        Assert.True(reset.IsEnabled);
    }
}
