using System.Text.Json.Nodes;
using GridSim.Core.IO.Json.CaseEditing;
using GridSim.Tests.Editor;
using GridSim.Ui.Assets.Editing;
using GridSim.Ui.Assets.Editing.Elements;
using GridSim.Ui.Assets.Editing.Templates;
using GridSim.Ui.Assets.Index;
using GridSim.Ui.Assets.Keys;
using Xunit;

namespace GridSim.Tests.Ui;

/// <summary>
/// RC5: a v2 generator's protectionTemplate has a picker over the unitProtection templates the case can see (active only,
/// local first), as the template link's picker lists only the bindable dynamics templates. A dynamics template, a
/// placeholder and any other kind are not offered; picking one writes the reference and Check accepts it.
/// </summary>
public sealed class UiUnitProtectionPickerTests
{
    private const string Ccgt = "gridsim.local.ccgt-placeholder@1", G99 = "gridsim.local.g99-embedded@1";

    private sealed class Fixture : IDisposable
    {
        private readonly EditorTestSupport.TempCopy _copy = new(Path.Combine(EditorTestSupport.RepoRoot(), "tests", "GridSim.Tests", "fixtures", "unit-protection"));

        public Fixture()
        {
            string manifest = Path.Combine(_copy.Dir, "three-unit.case.json");
            JsonObject m = JsonNode.Parse(File.ReadAllText(manifest))!.AsObject();
            JsonArray locals = m["localTemplates"]!.AsArray();
            // A placeholder unitProtection template and a dynamics (inverter) template: neither is a protectionTemplate choice.
            locals.Add(JsonNode.Parse("""
                { "id": "gridsim.local.unit-tbd", "rev": 1, "kind": "unitProtection", "status": "placeholder", "title": "Unit protection to be decided",
                  "params": { "uf1Hz": null }, "provenance": { "_default": { "tag": "CuratedPlaceholder", "note": "[VERIFY] not known yet" } } }
                """));
            locals.Add(JsonNode.Parse("""
                { "id": "gridsim.local.wind-gfl", "rev": 1, "kind": "gridFollowingInverter", "status": "active", "title": "Wind grid-following inverter (test)",
                  "params": { "deadbandHz": 0.05 }, "provenance": { "_default": { "tag": "CuratedPlaceholder", "note": "test values [VERIFY]" } } }
                """));
            File.WriteAllText(manifest, m.ToJsonString());
            A = CaseAuthoring.Open(AssetCaseSource.V2(manifest), CaseSessionTests.Unlimited(), Array.Empty<string>());
        }

        public CaseAuthoring A { get; }

        public ElementEditorModel Generator(int index)
        {
            CaseRef r = A.Records(A.Collections().First(c => c.Role == CaseRoles.Generators))[index].Ref;
            return ElementEditors.For(A, CaseRoles.Generators, A.FieldGrid(r)) ?? throw new InvalidOperationException("no generator editor");
        }

        public void Dispose() => _copy.Dispose();
    }

    [Fact]
    public void The_protection_template_picker_lists_only_the_active_unit_protection_templates()
    {
        using var f = new Fixture();
        Assert.Equal(new[] { Ccgt, G99 }, TemplatePages.UnitProtectionTemplates(CaseTemplates.Read(f.A)));
        foreach (int i in new[] { 0, 1, 2 })
        {
            EditorField pt = f.Generator(i).Field("protectionTemplate");
            Assert.Equal("unitProtection template", pt.PickerKind);
            Assert.Equal(new[] { Ccgt, G99 }, pt.Choices.Select(c => c.Value));
            Assert.Equal(2, pt.ChoiceCount);
        }
        // The dynamics template field is not given these choices, and the dynamics picker does not offer them.
        Assert.DoesNotContain(Ccgt, TemplatePages.BindableTemplates(CaseTemplates.Read(f.A)));
        Assert.Contains("gridsim.local.wind-gfl@1", TemplatePages.BindableTemplates(CaseTemplates.Read(f.A)));
        Assert.NotEqual("unitProtection template", f.Generator(0).Field("template").PickerKind);
    }

    [Fact]
    public void Picking_a_unit_protection_template_writes_the_reference_and_the_case_still_checks()
    {
        using var f = new Fixture();
        EditorField pt = f.Generator(0).Field("protectionTemplate");   // Hydro A: no protectionTemplate yet
        Assert.False(pt.Row.Present);
        AuthoringResult r = pt.Pick(pt.Choices.Single(c => c.Value == G99));
        Assert.True(r.Ok, r.Message);
        Assert.Equal("\"" + G99 + "\"", f.Generator(0).Field("protectionTemplate").Literal);
        Assert.Empty(f.A.Check().Errors);
    }
}
