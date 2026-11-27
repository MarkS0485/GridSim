using System.Text.Json;
using System.Text.Json.Nodes;
using GridSim.Core.Analysis.DynamicsStudy;
using GridSim.Core.Dynamics.Models.Adaptive;
using GridSim.Core.IO.Json;
using GridSim.Core.Protection;
using Xunit;
using static GridSim.Tests.DynamicsStudyTestSupport;

namespace GridSim.Tests;

/// <summary>
/// RC5: the unit protection file (gridsim-unit-protection/1) and RAW-bound unit protection. The file's templates and
/// per-unit values go through the v2 loader's own unit protection code, so they resolve exactly as the same templates and
/// per-unit overrides in a v2 case do; a RAW-bound study names a unit by its PSS/E machine key "&lt;bus&gt;:&lt;id&gt;"; an
/// unknown unit is refused before any work; naming a RAW machine by its key changes no number of the run.
/// </summary>
public sealed class UnitProtectionFileTests
{
    private static string Fixture(string name) => Path.Combine(CliProcess.Root(), "tests", "GridSim.Tests", "fixtures", "unit-protection", name);

    private static readonly DynamicsAdaptiveRequest Adaptive = new(
        new AdaptiveOptions(RelativeTolerance: 1e-8, AbsoluteTolerance: 1e-10, MaxStepSeconds: 0.02, EventTimeToleranceSeconds: 1e-7), CctToleranceSeconds: 1e-3);

    private static UnitProtectionSet Parse(JsonNode doc) =>
        UnitProtectionFile.Parse(System.Text.Encoding.UTF8.GetBytes(doc.ToJsonString()), "units.json", Schemas);

    private static JsonNode Case14Units() => JsonNode.Parse(File.ReadAllText(Fixture("case14-units.json")))!;

    // ------------------------------------------------------------------------------------------------ the file

    [Fact]
    public void The_case14_fixture_loads_with_each_value_from_the_unit_then_the_template_with_its_provenance()
    {
        UnitProtectionSet set = UnitProtectionFile.Load(Fixture("case14-units.json"), Schemas);
        Assert.Equal(new[] { "1:1", "2:1" }, set.Units.Select(u => u.Unit));
        Assert.Equal("sync-test", Assert.Single(set.Classes).Name);   // gridsim.local.NAME@1 -> NAME, as in a v2 case
        ResolvedUnitProtection quiet = set.For("1:1")!, own = set.For("2:1")!;
        UnitRelayStage uf = own.Stages.Single(s => s.Device == "81U-1");
        Assert.Equal(59.99, uf.Pickup.Value);
        Assert.Equal(UnitSettingOrigin.UnitOverride, uf.Pickup.Origin);
        Assert.Equal("test value standing in for this unit's schedule", uf.Pickup.Provenance.Note);   // the object form's own
        Assert.Equal("test values standing in for this unit's schedule", uf.Delay.Provenance.Note);   // the entry's
        Assert.Equal(UnitSettingOrigin.ClassDefault, own.Stages.Single(s => s.Device == "81O-1").Pickup.Origin);
        Assert.Equal(50.0, quiet.Stages.Single(s => s.Device == "81U-1").Pickup.Value);
        Assert.True(quiet.Stages.Single(s => s.Device == "81U-1").Pickup.IsPlaceholder);
        Assert.Equal(60.0, own.SettingsNominalHz);
    }

    [Fact]
    public void A_unit_value_without_provenance_is_a_placeholder_quoting_the_reason_as_in_a_v2_case()
    {
        JsonNode doc = Case14Units();
        doc["units"]![1]!.AsObject().Remove("provenance");
        ResolvedUnitProtection own = Parse(doc).For("2:1")!;
        UnitSetting delay = own.Stages.Single(s => s.Device == "81U-1").Delay;
        Assert.True(delay.IsPlaceholder);
        Assert.Equal("[VERIFY] set by a per-unit override in unit protection file 'units.json' for unit '2:1': unit 2:1 has its own underfrequency stage",
            delay.Provenance.Note);
    }

    [Fact]
    public void The_file_resolves_exactly_as_the_same_templates_and_per_unit_overrides_in_a_v2_case()
    {
        // The three-unit v2 fixture, restated as a unit protection file (same templates, same per-unit values).
        JsonNode v2 = JsonNode.Parse(File.ReadAllText(Fixture("three-unit.case.json")))!;
        JsonNode TemplateOf(int i) => v2["localTemplates"]![i]!.DeepClone();
        JsonObject Unit(string unit, string template, JsonNode? ov)
        {
            var o = new JsonObject { ["unit"] = unit, ["template"] = template };
            if (ov is not null)
            {
                o["params"] = ov["params"]!.DeepClone();
                o["reason"] = ov["reason"]!.DeepClone();
                if (ov["provenance"] is JsonNode p) o["provenance"] = p.DeepClone();
            }
            return o;
        }
        JsonNode ccgtOv = v2["overrides"]![0]!, embOv = v2["overrides"]![1]!;
        var doc = new JsonObject
        {
            ["schema"] = UnitProtectionFile.SchemaTag,
            ["templates"] = new JsonArray(TemplateOf(0), TemplateOf(1)),
            ["units"] = new JsonArray(
                Unit("CCGT B", "gridsim.local.ccgt-placeholder@1", ccgtOv),
                Unit("Embedded C", "gridsim.local.g99-embedded@1", embOv)),
        };
        UnitProtectionSet file = Parse(doc);
        UnitProtectionSet fromCase = V2CaseLoader.LoadWithNotes(Fixture("three-unit.case.json"), Schemas, null).Model.UnitProtection!;
        foreach (string unit in new[] { "CCGT B", "Embedded C" })
        {
            ResolvedUnitProtection a = fromCase.For(unit)!, b = file.For(unit)!;
            Assert.Equal(a.ClassName, b.ClassName);
            Assert.Equal(a.SettingsNominalHz, b.SettingsNominalHz);
            Assert.Equal(a.Stages.Count, b.Stages.Count);
            for (int k = 0; k < a.Stages.Count; k++)
            {
                UnitRelayStage x = a.Stages[k], y = b.Stages[k];
                Assert.Equal(x.Device, y.Device);
                foreach ((UnitSetting? p, UnitSetting? q) in new[] { (x.Pickup, y.Pickup), (x.Delay, y.Delay), (x.Diameter, y.Diameter) })
                {
                    if (p is null) { Assert.Null(q); continue; }
                    Assert.Equal(BitConverter.DoubleToInt64Bits(p.Value), BitConverter.DoubleToInt64Bits(q!.Value));
                    Assert.Equal(p.Origin, q.Origin);
                    Assert.Equal(p.Layer, q.Layer);
                    Assert.Equal(p.Provenance.Tag, q.Provenance.Tag);
                    // A value with no stated source quotes its document; every other note is the same text.
                    Assert.Equal(p.Provenance.Note.Replace("case 'Unit protection three-unit'", "unit protection file 'units.json'", StringComparison.Ordinal), q.Provenance.Note);
                }
            }
        }
    }

    [Fact]
    public void Every_problem_of_the_templates_and_units_is_refused_with_its_location()
    {
        JsonNode doc = Case14Units();
        JsonNode bad = doc["templates"]![0]!.DeepClone();
        bad["id"] = "gridsim.local.other";
        bad["kind"] = "protection";
        doc["templates"]!.AsArray().Add(bad);
        var ex = Assert.Throws<InvalidDataException>(() => Parse(doc));
        Assert.Contains("/templates/1/kind: template gridsim.local.other@1 is kind 'protection'", ex.Message, StringComparison.Ordinal);

        doc = Case14Units();
        doc["units"]!.AsArray().Add(new JsonObject { ["unit"] = "2:1", ["template"] = "gridsim.local.sync-test@1" });
        doc["units"]!.AsArray().Add(new JsonObject { ["unit"] = "3:1", ["template"] = "gridsim.local.nothing@1" });
        doc["units"]!.AsArray().Add(new JsonObject { ["unit"] = "6:1", ["template"] = "gridsim.local.sync-test@1", ["params"] = new JsonObject { ["uf1Hz"] = 59.0 } });
        ex = Assert.Throws<InvalidDataException>(() => Parse(doc));
        Assert.Contains("/units/2/unit: unit '2:1' is listed more than once", ex.Message, StringComparison.Ordinal);
        Assert.Contains("/units/3/template: unit '3:1' takes template gridsim.local.nothing@1, which is not a template of this file", ex.Message, StringComparison.Ordinal);
        Assert.Contains("/units/4/reason: unit '6:1' sets values of its own, so it must say why (reason)", ex.Message, StringComparison.Ordinal);

        // A unit's own value the kind spec refuses (the v2 per-unit override check, shared).
        doc = Case14Units();
        doc["units"]![1]!["params"]!["noSuchSetting"] = 1.0;
        ex = Assert.Throws<InvalidDataException>(() => Parse(doc));
        Assert.Contains("/units/1/params", ex.Message, StringComparison.Ordinal);
        Assert.Contains("noSuchSetting", ex.Message, StringComparison.Ordinal);
        Assert.Contains("(after applying the per-unit override of '2:1' to gridsim.local.sync-test@1)", ex.Message, StringComparison.Ordinal);

        // The schema: an unknown member, a missing tag.
        doc = Case14Units();
        doc["extra"] = true;
        doc.AsObject().Remove("schema");
        ex = Assert.Throws<InvalidDataException>(() => Parse(doc));
        Assert.StartsWith("units.json: ", ex.Message, StringComparison.Ordinal);
        Assert.Contains("extra", ex.Message, StringComparison.Ordinal);
        Assert.Contains("schema", ex.Message, StringComparison.Ordinal);

        // A duplicate member is refused by the reader (as in every v2 file).
        string dup = File.ReadAllText(Fixture("case14-units.json")).Replace("\"title\": \"case14", "\"title\": \"x\", \"title\": \"case14", StringComparison.Ordinal);
        Assert.Throws<InvalidDataException>(() => UnitProtectionFile.Parse(System.Text.Encoding.UTF8.GetBytes(dup), "units.json", Schemas));
    }

    [Fact]
    public void The_machine_key_is_the_bus_number_and_the_id_as_the_raw_reader_stores_it()
    {
        Assert.Equal("101:1", UnitProtectionFile.MachineKey(101, "1"));
        Assert.Equal("7:G1", UnitProtectionFile.MachineKey(7, "G1"));
        // The RAW reader removes the quotes and the blanks around an ID and keeps its case.
        GridSim.Core.IO.Psse.PsseParseResult raw = RawText(File.ReadAllText(Psse("case14.raw")).Replace("\r\n", "\n", StringComparison.Ordinal), "case14");
        Assert.Contains(raw.MachineKeys, k => k.Bus == 2 && k.Id == "1");
        GridSim.Core.Model.GridModel keyed = DynamicsStudyRunner.UnitKeyed(raw);
        Assert.Equal(raw.MachineKeys.Select(k => k.Bus + ":" + k.Id), keyed.AllGenerators.Select(g => g.Name));
        Assert.All(raw.Model.AllGenerators, g => Assert.Null(g.Name));   // the RAW model itself is untouched
    }

    // ------------------------------------------------------------------------------------------------ RAW-bound runs

    private static DynamicsAdaptiveEvidence Run(GridSim.Core.IO.Psse.PsseParseResult raw, UnitProtectionSet? units, DynamicsCaseRequest[] cases) =>
        DynamicsStudyRunner.Run(Request(raw, Text(Study("case14-study.dyr")), cases, duration: 2.0)
            with { Adaptive = Adaptive with { UnitProtection = units } }).Adaptive!;

    [Fact]
    public void A_raw_machine_named_by_its_key_trips_exactly_as_the_same_machine_named_by_a_generator_name()
    {
        GridSim.Core.IO.Psse.PsseParseResult raw = Raw(Psse("case14.raw"));
        var named = raw with
        {
            Model = raw.Model.CopyWith(generators: raw.Model.AllGenerators.Select(g => g with { Name = $"G{g.Bus}" }).ToList(), generatorsAreWholeFleet: true),
        };
        UnitProtectionSet byKey = UnitProtectionFile.Load(Fixture("case14-units.json"), Schemas);
        JsonNode renamed = Case14Units();
        renamed["units"]![0]!["unit"] = "G1";
        renamed["units"]![1]!["unit"] = "G2";
        renamed["units"]![1]!["reason"] = "unit 2:1 has its own underfrequency stage";
        UnitProtectionSet byName = Parse(renamed);
        DynamicsCaseRequest[] cases = { UnitRelayInLoopTests.BaselineCases()[2] };
        DynamicsAdaptiveEvidence a = Run(raw, byKey, cases), b = Run(named, byName, cases);
        DynamicsAdaptiveRow ra = a.Rows[0], rb = b.Rows[0];
        AdaptiveUnitRelayRecord trip = ra.UnitRelays!.Single(r => r.Tripped);
        Assert.Equal(("2:1", "81U-1"), (trip.Unit, trip.Device));
        Assert.Equal(ra.UnitRelays!.Select(r => (r.Device, r.Tripped, r.PickupSeconds, r.TripSeconds, r.TripQuantity)),
            rb.UnitRelays!.Select(r => (r.Device, r.Tripped, r.PickupSeconds, r.TripSeconds, r.TripQuantity)));
        Assert.Equal(JsonSerializer.Serialize(ra.TimeSeconds), JsonSerializer.Serialize(rb.TimeSeconds));
        Assert.Equal(JsonSerializer.Serialize(ra.Channels), JsonSerializer.Serialize(rb.Channels));
        Assert.Equal(ra.Events!.Select(e => (e.Kind, e.Source, e.AppliedSeconds)), rb.Events!.Select(e => (e.Kind, e.Source, e.AppliedSeconds)));
    }

    [Fact]
    public void An_unknown_machine_key_is_refused_before_any_work_with_the_key_rule()
    {
        JsonNode doc = Case14Units();
        doc["units"]![0]!["unit"] = "99:1";
        doc["units"]![1]!["unit"] = "2:01";
        var ex = Assert.Throws<InvalidDataException>(() => Run(Raw(Psse("case14.raw")), Parse(doc), UnitRelayInLoopTests.BaselineCases()));
        Assert.StartsWith("adaptive.unitProtection: unit '99:1' is not a machine of the RAW case; unit '2:01' is not a machine of the RAW case.", ex.Message, StringComparison.Ordinal);
        Assert.Contains("<bus>:<id>", ex.Message, StringComparison.Ordinal);
    }
}
