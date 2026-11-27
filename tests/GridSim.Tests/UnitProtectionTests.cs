using GridSim.Core.Assets;
using GridSim.Core.Diagnostics;
using GridSim.Core.IO.Json;
using GridSim.Core.Model;
using GridSim.Core.PowerFlow;
using GridSim.Core.Protection;
using Xunit;
using static GridSim.Tests.V2.V2TestSupport;

namespace GridSim.Tests;

/// <summary>
/// RC5 per-unit protection, stage 1 (data model): a unit protection class default with a per-unit override on top,
/// resolved per setting (unit, else class, else absent), every value carrying its provenance; the cited ENA presets; the
/// v2 case format's two optional members; and the proof that a case without them loads and solves exactly as before.
/// </summary>
public sealed class UnitProtectionTests
{
    private static readonly AssetClassProvenance Placeholder = new(ProvenanceTag.CuratedPlaceholder, "[VERIFY] class default");
    private static readonly AssetClassProvenance Schedule = new(ProvenanceTag.Recorded, "unit protection schedule rev C");

    private static UnitProtectionLayer Layer(IEnumerable<(string Key, double Value)> settings,
        IEnumerable<(string Key, AssetClassProvenance Prov)> prov, string? rocof = null, string? iface = null) =>
        new(settings.ToDictionary(x => x.Key, x => x.Value, StringComparer.Ordinal),
            prov.ToDictionary(x => x.Key, x => x.Prov, StringComparer.Ordinal), rocof, iface);

    private static UnitProtectionClass Class(string name, params (string Key, double Value)[] settings) =>
        new(name, null, Layer(settings, new[] { (AssetClass.DefaultKey, Placeholder) }));

    private static UnitProtectionAssignment Unit(string unit, string? cls, params (string Key, double Value)[] settings) =>
        new(unit, cls, Layer(settings, settings.Select(s => (s.Key, Schedule))));

    private static string CaseDir => Path.Combine(RepoRoot(), "tests", "GridSim.Tests", "fixtures", "unit-protection");

    // ------------------------------------------------------------------------------------------------ resolution

    [Fact]
    public void A_unit_value_wins_over_the_class_default_and_each_setting_keeps_its_own_provenance()
    {
        var set = new UnitProtectionSet(
            new[] { Class("sync", ("uf1Hz", 47.0), ("uf1DelayS", 2.0), ("of1Hz", 52.0), ("of1DelayS", 2.0)) },
            new[] { Unit("G1", "sync", ("uf1Hz", 47.5)) });
        ResolvedUnitProtection r = set.For("G1")!;
        UnitRelayStage uf = r.Stages.Single(s => s.Device == "81U-1");
        Assert.Equal(47.5, uf.Pickup.Value);
        Assert.Equal(UnitSettingOrigin.UnitOverride, uf.Pickup.Origin);
        Assert.Equal(Schedule, uf.Pickup.Provenance);
        Assert.False(uf.Pickup.IsPlaceholder);
        Assert.Equal("sourced", uf.Pickup.Grade);
        Assert.Equal(2.0, uf.Delay.Value);
        Assert.Equal(UnitSettingOrigin.ClassDefault, uf.Delay.Origin);
        Assert.Equal(Placeholder, uf.Delay.Provenance);
        Assert.Equal("placeholder", uf.Delay.Grade);
        Assert.Equal("template 'sync'", uf.Delay.Layer);
        UnitRelayStage of = r.Stages.Single(s => s.Device == "81O-1");
        Assert.Equal(UnitSettingOrigin.ClassDefault, of.Pickup.Origin);
    }

    [Fact]
    public void An_absent_relay_stays_absent_and_nothing_is_invented()
    {
        var set = new UnitProtectionSet(Array.Empty<UnitProtectionClass>(), new[] { Unit("G1", null, ("uv1Pu", 0.8), ("uv1DelayS", 2.5)) });
        ResolvedUnitProtection r = set.For("G1")!;
        Assert.Equal(new[] { "27-1" }, r.Stages.Select(s => s.Device));
        Assert.Null(set.For("G2"));
        Assert.Equal(UnitProtectionSet.DefaultSettingsNominalHz, r.SettingsNominalHz);
        Assert.Null(r.SettingsNominal);
    }

    [Fact]
    public void Stages_come_out_in_the_fixed_order_whatever_order_they_were_written_in()
    {
        var set = new UnitProtectionSet(Array.Empty<UnitProtectionClass>(), new[]
        {
            Unit("G1", null, ("vectorShiftDeg", 6.0), ("vectorShiftDelayS", 0.0), ("uf2Hz", 47.0), ("uf2DelayS", 0.5),
                ("uv1Pu", 0.8), ("uv1DelayS", 2.5), ("reversePowerPu", 0.05), ("reversePowerDelayS", 2.0)),
        });
        Assert.Equal(new[] { "27-1", "32", "81U-2", "78" }, set.For("G1")!.Stages.Select(s => s.Device));
    }

    [Fact]
    public void The_modern_rocof_preset_is_one_hertz_per_second_for_half_a_second_cited_to_g99_and_g59()
    {
        var set = new UnitProtectionSet(Array.Empty<UnitProtectionClass>(),
            new[] { new UnitProtectionAssignment("G1", null, Layer(Array.Empty<(string, double)>(), Array.Empty<(string, AssetClassProvenance)>(), rocof: "modern")) });
        UnitRelayStage r = set.For("G1")!.Stages.Single();
        Assert.Equal("81R", r.Device);
        Assert.Equal(1.0, r.Pickup.Value);
        Assert.Equal(0.5, r.Delay.Value);
        Assert.Equal(ProvenanceTag.Recorded, r.Pickup.Provenance.Tag);
        Assert.Contains("EREC G99 Issue 2", r.Pickup.Provenance.Note, StringComparison.Ordinal);
        Assert.Contains("S124", r.Pickup.Provenance.Note, StringComparison.Ordinal);
        Assert.Contains("S135", r.Pickup.Provenance.Note, StringComparison.Ordinal);
        Assert.Equal("unit, preset rocof 'modern'", r.Pickup.Layer);
    }

    [Theory]
    [InlineData("legacy", 0.125, "K2 = 1.0")]
    [InlineData("legacy-high-impedance", 0.2, "K2 = 1.6")]
    public void The_legacy_rocof_presets_are_k2_times_an_eighth_with_the_recorded_half_second_delay(string preset, double pickup, string k2)
    {
        var set = new UnitProtectionSet(Array.Empty<UnitProtectionClass>(),
            new[] { new UnitProtectionAssignment("G1", null, Layer(Array.Empty<(string, double)>(), Array.Empty<(string, AssetClassProvenance)>(), rocof: preset)) });
        UnitRelayStage r = set.For("G1")!.Stages.Single();
        Assert.Equal(pickup, r.Pickup.Value);
        Assert.Equal(ProvenanceTag.Recorded, r.Pickup.Provenance.Tag);
        Assert.Contains(k2, r.Pickup.Provenance.Note, StringComparison.Ordinal);
        Assert.Contains("S132", r.Pickup.Provenance.Note, StringComparison.Ordinal);
        Assert.Equal(0.5, r.Delay.Value);
        Assert.False(r.Delay.IsPlaceholder);
        Assert.Equal(ProvenanceTag.Recorded, r.Delay.Provenance.Tag);
        Assert.Contains("S135", r.Delay.Provenance.Note, StringComparison.Ordinal);
        Assert.Contains("p.62", r.Delay.Provenance.Note, StringComparison.Ordinal);
    }

    [Fact]
    public void The_g99_hv_interface_preset_is_table_10_1_and_an_explicit_value_in_the_same_layer_wins()
    {
        var layer = Layer(new[] { ("uv1DelayS", 1.5) }, new[] { ("uv1DelayS", Schedule) }, iface: "g99-hv");
        ResolvedUnitProtection r = new UnitProtectionSet(Array.Empty<UnitProtectionClass>(), new[] { new UnitProtectionAssignment("G1", null, layer) }).For("G1")!;
        var byDevice = r.Stages.ToDictionary(s => s.Device);
        Assert.Equal(new[] { "27-1", "59-1", "59-2", "81U-1", "81U-2", "81O-1", "81R" }, r.Stages.Select(s => s.Device));
        Assert.Equal((0.8, 1.5), (byDevice["27-1"].Pickup.Value, byDevice["27-1"].Delay.Value));
        Assert.Equal(Schedule, byDevice["27-1"].Delay.Provenance);
        Assert.Equal((1.1, 1.0), (byDevice["59-1"].Pickup.Value, byDevice["59-1"].Delay.Value));
        Assert.Equal((1.13, 0.5), (byDevice["59-2"].Pickup.Value, byDevice["59-2"].Delay.Value));
        Assert.Equal((47.5, 20.0), (byDevice["81U-1"].Pickup.Value, byDevice["81U-1"].Delay.Value));
        Assert.Equal((47.0, 0.5), (byDevice["81U-2"].Pickup.Value, byDevice["81U-2"].Delay.Value));
        Assert.Equal((52.0, 0.5), (byDevice["81O-1"].Pickup.Value, byDevice["81O-1"].Delay.Value));
        Assert.Equal((1.0, 0.5), (byDevice["81R"].Pickup.Value, byDevice["81R"].Delay.Value));
        Assert.All(r.Stages, s => Assert.Contains("Table 10.1", s.Pickup.Provenance.Note, StringComparison.Ordinal));
    }

    [Fact]
    public void A_unit_rocof_preset_wins_over_the_class_explicit_rocof()
    {
        var cls = Class("old", ("rocofHzPerS", 0.125), ("rocofDelayS", 0.0));
        var unit = new UnitProtectionAssignment("G1", "old", Layer(Array.Empty<(string, double)>(), Array.Empty<(string, AssetClassProvenance)>(), rocof: "modern"));
        UnitRelayStage r = new UnitProtectionSet(new[] { cls }, new[] { unit }).For("G1")!.Stages.Single();
        Assert.Equal((1.0, 0.5), (r.Pickup.Value, r.Delay.Value));
        Assert.Equal(UnitSettingOrigin.UnitOverride, r.Pickup.Origin);
    }

    // ------------------------------------------------------------------------------------------------ refusals

    private static string Refused(Func<UnitProtectionSet> build) => Assert.Throws<InvalidDataException>(() => build()).Message;

    [Fact]
    public void A_pickup_without_a_delay_and_a_delay_without_a_pickup_are_both_refused_in_one_message()
    {
        string msg = Refused(() => new UnitProtectionSet(Array.Empty<UnitProtectionClass>(), new[]
        {
            Unit("G1", null, ("uf1Hz", 47.5)),
            Unit("G2", null, ("of1DelayS", 0.5)),
        }));
        Assert.Contains("unit 'G1': relay 81U-1 has a pickup (uf1Hz", msg, StringComparison.Ordinal);
        Assert.Contains("unit 'G2': relay 81O-1 has a delay (of1DelayS", msg, StringComparison.Ordinal);
    }

    [Fact]
    public void A_unit_value_with_no_source_is_refused()
    {
        var unit = new UnitProtectionAssignment("G1", null, Layer(new[] { ("uv1Pu", 0.8), ("uv1DelayS", 2.5) }, new[] { ("uv1Pu", Schedule) }));
        string msg = Refused(() => new UnitProtectionSet(Array.Empty<UnitProtectionClass>(), new[] { unit }));
        Assert.Contains("unit 'G1': setting 'uv1DelayS' has no provenance", msg, StringComparison.Ordinal);
    }

    [Fact]
    public void A_class_without_a_default_provenance_and_a_placeholder_without_verify_are_refused()
    {
        var noDefault = new UnitProtectionClass("a", null, Layer(new[] { ("uv1Pu", 0.8), ("uv1DelayS", 2.5) }, Array.Empty<(string, AssetClassProvenance)>()));
        var noVerify = new UnitProtectionClass("b", null, Layer(new[] { ("uv1Pu", 0.8), ("uv1DelayS", 2.5) },
            new[] { (AssetClass.DefaultKey, new AssetClassProvenance(ProvenanceTag.CuratedPlaceholder, "a guess")) }));
        string msg = Refused(() => new UnitProtectionSet(new[] { noDefault, noVerify }, Array.Empty<UnitProtectionAssignment>()));
        Assert.Contains("unit protection template 'a': provenance needs a '_default' entry", msg, StringComparison.Ordinal);
        Assert.Contains("unit protection template 'b': the provenance of '_default' is CuratedPlaceholder but its note lacks the literal [VERIFY] token", msg, StringComparison.Ordinal);
    }

    [Fact]
    public void An_unknown_class_a_duplicate_unit_an_unknown_key_and_values_out_of_range_are_refused()
    {
        string msg = Refused(() => new UnitProtectionSet(new[] { Class("c", ("uv1Pu", 1.2), ("uv1DelayS", -1.0)) }, new[]
        {
            Unit("G1", "nope"),
            Unit("G2", "c", ("uf1Hz", 50.5), ("uf1DelayS", 1.0)),
            Unit("G2", "c"),
            Unit("G3", null, ("rocofHzPerSecond", 1.0)),
            Unit("G4", null, ("vectorShiftDeg", double.NaN), ("vectorShiftDelayS", 0.0)),
        }));
        Assert.Contains("unit 'G1' takes unit protection template 'nope', which is not defined", msg, StringComparison.Ordinal);
        Assert.Contains("unit protection template 'c': setting 'uv1Pu' = 1.2 is an undervoltage pickup", msg, StringComparison.Ordinal);
        Assert.Contains("unit protection template 'c': setting 'uv1DelayS' = -1 is a time delay", msg, StringComparison.Ordinal);
        Assert.Contains("unit 'G2': relay 81U-1 pickup 50.5 Hz is not below the 50 Hz settings nominal", msg, StringComparison.Ordinal);
        Assert.Contains("unit 'G2' has more than one unit protection block", msg, StringComparison.Ordinal);
        Assert.Contains("unit 'G3': setting 'rocofHzPerSecond' is not a unit protection setting", msg, StringComparison.Ordinal);
        Assert.Contains("unit 'G4': setting 'vectorShiftDeg' = NaN must be a finite number", msg, StringComparison.Ordinal);
    }

    [Fact]
    public void A_provenance_entry_for_a_preset_value_is_refused_because_the_preset_carries_its_own_source()
    {
        var unit = new UnitProtectionAssignment("G1", null, Layer(Array.Empty<(string, double)>(), new[] { ("rocofHzPerS", Schedule) }, rocof: "modern"));
        string msg = Refused(() => new UnitProtectionSet(Array.Empty<UnitProtectionClass>(), new[] { unit }));
        Assert.Contains("provenance names 'rocofHzPerS', which this layer takes from a preset", msg, StringComparison.Ordinal);
    }

    [Fact]
    public void Loss_of_excitation_needs_offset_diameter_and_delay_together()
    {
        string msg = Refused(() => new UnitProtectionSet(Array.Empty<UnitProtectionClass>(),
            new[] { Unit("G1", null, ("loe1OffsetPu", 0.1), ("loe1DelayS", 0.1)) }));
        Assert.Contains("relay 40-1 has an offset (loe1OffsetPu) but no diameter (loe1DiameterPu)", msg, StringComparison.Ordinal);
    }

    // ------------------------------------------------------------------------------------------------ the model

    private static GridModel ThreeBus() => V2CaseLoader.Load(Path.Combine(CaseDir, "plain.case.json"), SchemasDir);

    [Fact]
    public void WithUnitProtection_refuses_a_unit_that_is_not_a_generator_and_copies_carry_the_set()
    {
        GridModel m = ThreeBus();
        var bad = new UnitProtectionSet(Array.Empty<UnitProtectionClass>(), new[] { Unit("Nobody", null, ("uv1Pu", 0.8), ("uv1DelayS", 2.5)) });
        string msg = Assert.Throws<InvalidDataException>(() => m.WithUnitProtection(bad)).Message;
        Assert.Contains("unit 'Nobody' has unit protection but is not a generator", msg, StringComparison.Ordinal);

        var good = new UnitProtectionSet(Array.Empty<UnitProtectionClass>(), new[] { Unit("CCGT B", null, ("uv1Pu", 0.8), ("uv1DelayS", 2.5)) });
        GridModel withUp = m.WithUnitProtection(good);
        Assert.Null(m.UnitProtection);
        Assert.Same(good, withUp.UnitProtection);
        Assert.Same(good, withUp.CopyWith(name: "copy").UnitProtection);
        Assert.Same(m.Buses, withUp.Buses);
        Assert.Same(m.Branches, withUp.Branches);
        Assert.Same(m.Generators, withUp.Generators);
    }

    [Fact]
    public void The_reference_case_loads_its_classes_and_units_with_the_unit_values_on_top()
    {
        V2CaseLoadResult r = V2CaseLoader.LoadWithNotes(Path.Combine(CaseDir, "three-unit.case.json"), SchemasDir);
        UnitProtectionSet up = r.Model.UnitProtection!;
        Assert.Equal(new[] { "ccgt-placeholder", "g99-embedded" }, up.Classes.Select(c => c.Name));
        Assert.Equal(new[] { "CCGT B", "Embedded C" }, up.Units.Select(u => u.Unit));
        Assert.Contains(r.Notes, n => n.StartsWith("unit protection: 2 units with relays of their own", StringComparison.Ordinal) && n.EndsWith("2 unit protection templates; the power flow does not use them", StringComparison.Ordinal));

        ResolvedUnitProtection b = up.For("CCGT B")!;
        Assert.Equal(new[] { "27-1", "59-1", "32", "40-1", "81U-1", "81O-1", "81R" }, b.Stages.Select(s => s.Device));
        UnitRelayStage uf = b.Stages.Single(s => s.Device == "81U-1");
        Assert.Equal((47.5, UnitSettingOrigin.UnitOverride, ProvenanceTag.Synthetic), (uf.Pickup.Value, uf.Pickup.Origin, uf.Pickup.Provenance.Tag));
        Assert.Equal((2.0, UnitSettingOrigin.ClassDefault, ProvenanceTag.CuratedPlaceholder), (uf.Delay.Value, uf.Delay.Origin, uf.Delay.Provenance.Tag));
        Assert.Equal(1.0, b.Stages.Single(s => s.Device == "81R").Pickup.Value);

        ResolvedUnitProtection c = up.For("Embedded C")!;
        Assert.Equal(new[] { "27-1", "59-1", "59-2", "32", "81U-1", "81U-2", "81O-1", "81R" }, c.Stages.Select(s => s.Device));
        Assert.Equal(0.05, c.Stages.Single(s => s.Device == "32").Pickup.Value);
        Assert.Null(up.For("Hydro A"));
    }

    [Fact]
    public void A_case_with_unit_protection_solves_bit_for_bit_as_the_same_case_without_it()
    {
        GridModel plain = ThreeBus();
        GridModel withUp = V2CaseLoader.Load(Path.Combine(CaseDir, "three-unit.case.json"), SchemasDir);
        Assert.Null(plain.UnitProtection);
        Assert.NotNull(withUp.UnitProtection);
        var opt = new PowerFlowOptions { MaxIterations = 100, EnforceQLimits = true };
        PowerFlowResult a = new NewtonRaphsonPowerFlow().Solve(plain, opt);
        PowerFlowResult b = new NewtonRaphsonPowerFlow().Solve(withUp, opt);
        Assert.True(a.Converged);
        Assert.Equal(a.Iterations, b.Iterations);
        Assert.Equal(a.Vm.Select(BitConverter.DoubleToInt64Bits), b.Vm.Select(BitConverter.DoubleToInt64Bits));
        Assert.Equal(a.VaDeg.Select(BitConverter.DoubleToInt64Bits), b.VaDeg.Select(BitConverter.DoubleToInt64Bits));
        Assert.Equal(a.PInjMw.Select(BitConverter.DoubleToInt64Bits), b.PInjMw.Select(BitConverter.DoubleToInt64Bits));
        Assert.Equal(a.QInjMvar.Select(BitConverter.DoubleToInt64Bits), b.QInjMvar.Select(BitConverter.DoubleToInt64Bits));
    }

    [Fact]
    public void Every_existing_v2_fixture_case_that_loads_carries_no_unit_protection()
    {
        string root = Path.Combine(RepoRoot(), "tests", "GridSim.Tests", "fixtures");
        int loaded = 0;
        foreach (string manifest in Directory.EnumerateFiles(root, "*.case.json", SearchOption.AllDirectories).Order(StringComparer.Ordinal))
        {
            if (manifest.Contains("unit-protection", StringComparison.Ordinal)) continue;
            GridModel m;
            try { m = V2CaseLoader.Load(manifest, SchemasDir, Path.Combine(RepoRoot(), "data")); }
            catch (Exception ex) when (ex is InvalidDataException or IOException) { continue; }   // the refusal fixtures
            Assert.Null(m.UnitProtection);
            loaded++;
        }
        Assert.True(loaded > 0);
    }

    [Fact]
    public void A_case_without_unit_protection_keeps_exactly_its_notes()
    {
        string stiff = Path.Combine(RepoRoot(), "tests", "GridSim.Tests", "fixtures", "motor-start", "stiff.case.json");
        V2CaseLoadResult r = V2CaseLoader.LoadWithNotes(stiff, SchemasDir);
        Assert.Null(r.Model.UnitProtection);
        Assert.Equal(new[] { "motors: 1 placed motor row carried beside the model for the motor acceleration study; the steady-state power flow does not use them, and a verb that writes the case from the model (convert) does not carry them" }, r.Notes);
    }

    [Fact]
    public void The_v2_writer_writes_unit_protection_back_and_rewriting_the_loaded_case_gives_the_same_bytes()
    {
        GridModel m = V2CaseLoader.Load(Path.Combine(CaseDir, "three-unit.case.json"), SchemasDir);
        string dir = TestTemp.Under("gridsim-unitprot-" + Guid.NewGuid().ToString("N"));
        try
        {
            V2CaseWriteResult w1 = V2CaseWriter.Write(m, Path.Combine(dir, "a", "a.case.json"));
            GridModel back = V2CaseLoader.Load(w1.ManifestPath, SchemasDir);
            UnitProtectionSet up = back.UnitProtection!;
            Assert.Equal(m.UnitProtection!.Units.Select(u => u.Unit), up.Units.Select(u => u.Unit));
            foreach (ResolvedUnitProtection r in m.UnitProtection.Resolved)
                Assert.Equal(r.Stages, up.For(r.Unit)!.Stages);
            V2CaseWriteResult w2 = V2CaseWriter.Write(back, Path.Combine(dir, "b", "a.case.json"));
            Assert.Equal(File.ReadAllBytes(w1.NetworkPath), File.ReadAllBytes(w2.NetworkPath));
            Assert.Equal(File.ReadAllBytes(w1.ManifestPath), File.ReadAllBytes(w2.ManifestPath));
            string manifest = File.ReadAllText(w1.ManifestPath);
            Assert.Contains("\"kind\": \"unitProtection\"", manifest, StringComparison.Ordinal);
            Assert.Contains("\"unit\": \"CCGT B\"", manifest, StringComparison.Ordinal);
            Assert.Contains("\"protectionTemplate\": \"gridsim.local.ccgt-placeholder@1\"", File.ReadAllText(w1.NetworkPath), StringComparison.Ordinal);
        }
        finally { if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true); }
    }

    [Fact]
    public void The_v2_writer_output_of_a_model_without_unit_protection_has_no_unit_protection_member()
    {
        GridModel m = ThreeBus();
        string dir = TestTemp.Under("gridsim-unitprot-" + Guid.NewGuid().ToString("N"));
        try
        {
            V2CaseWriteResult w = V2CaseWriter.Write(m, Path.Combine(dir, "p.case.json"));
            Assert.DoesNotContain("protectionTemplate", File.ReadAllText(w.NetworkPath), StringComparison.Ordinal);
            Assert.DoesNotContain("unitProtection", File.ReadAllText(w.ManifestPath), StringComparison.Ordinal);
        }
        finally { if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true); }
    }

    [Fact]
    public void The_v1_writer_says_it_does_not_carry_unit_protection()
    {
        GridModel m = V2CaseLoader.Load(Path.Combine(CaseDir, "three-unit.case.json"), SchemasDir);
        string dir = TestTemp.Under("gridsim-unitprot-" + Guid.NewGuid().ToString("N"));
        try
        {
            IReadOnlyList<string> notes = JsonCaseWriter.WriteDirectory(m, dir);
            Assert.Contains(notes, n => n.Contains("unit protection of 2 unit(s)", StringComparison.Ordinal));
        }
        finally { if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true); }
    }

    // Edit a JSON document as a tree (line ends and indentation do not matter).
    private static string Edit(string json, Action<System.Text.Json.Nodes.JsonNode> change)
    {
        System.Text.Json.Nodes.JsonNode root = System.Text.Json.Nodes.JsonNode.Parse(json)!;
        change(root);
        return root.ToJsonString(new System.Text.Json.JsonSerializerOptions { WriteIndented = true });
    }

    private static string RefusedVariant(Func<string, string> manifest, Func<string, string>? network = null)
    {
        string dir = TestTemp.Under("gridsim-unitprot-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            File.WriteAllText(Path.Combine(dir, "x.case.json"), manifest(File.ReadAllText(Path.Combine(CaseDir, "three-unit.case.json"))));
            string net = File.ReadAllText(Path.Combine(CaseDir, "three-unit.network.json"));
            File.WriteAllText(Path.Combine(dir, "three-unit.network.json"), network is null ? net : network(net));
            return Assert.Throws<InvalidDataException>(() => V2CaseLoader.Load(Path.Combine(dir, "x.case.json"), SchemasDir)).Message;
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    [Fact]
    public void A_per_unit_override_naming_another_template_than_the_units_own_is_refused_naming_the_manifest()
    {
        string msg = RefusedVariant(m => Edit(m, root => root["overrides"]![1]!["template"] = "gridsim.local.ccgt-placeholder@1"));
        Assert.StartsWith("x.case.json", msg, StringComparison.Ordinal);
        Assert.Contains("unit 'Embedded C' takes unit protection template 'g99-embedded', not gridsim.local.ccgt-placeholder@1", msg, StringComparison.Ordinal);
    }

    [Fact]
    public void A_per_unit_override_of_a_unit_with_no_template_and_a_bad_value_are_refused()
    {
        string msg = RefusedVariant(m => m.Replace("\"unit\": \"CCGT B\"", "\"unit\": \"Hydro A\"", StringComparison.Ordinal)
            .Replace("\"reversePowerPu\": 0.05", "\"reversePowerPu\": 1.5", StringComparison.Ordinal));
        Assert.Contains("unit 'Hydro A' has no unit protection template", msg, StringComparison.Ordinal);
        Assert.Contains("reversePowerPu", msg, StringComparison.Ordinal);
    }

    [Fact]
    public void A_protection_template_of_another_kind_is_refused()
    {
        string msg = RefusedVariant(m => m, n => n.Replace("\"protectionTemplate\": \"gridsim.local.ccgt-placeholder@1\"", "\"protectionTemplate\": \"gridsim.local.nowhere@1\"", StringComparison.Ordinal));
        Assert.Contains("template gridsim.local.nowhere@1 is neither imported nor a local template of the case", msg, StringComparison.Ordinal);
    }

    [Fact]
    public void A_binding_rule_gives_a_unit_its_template_and_a_units_own_protectionTemplate_wins_over_a_rule()
    {
        GridModel m = V2CaseLoader.Load(Path.Combine(CaseDir, "three-unit.case.json"), SchemasDir);
        Assert.Equal("g99-embedded", m.UnitProtection!.For("Embedded C")!.ClassName);   // by the chp binding rule
        Assert.Equal("ccgt-placeholder", m.UnitProtection.For("CCGT B")!.ClassName);    // its own protectionTemplate
        Assert.Equal("CCGT B's own underfrequency pickup and the modern loss-of-mains RoCoF setting", m.UnitProtection.Units[0].Reason);
    }

    [Fact]
    public void A_per_unit_override_without_provenance_is_a_verify_placeholder_quoting_its_reason()
    {
        string dir = TestTemp.Under("gridsim-unitprot-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            string man = File.ReadAllText(Path.Combine(CaseDir, "three-unit.case.json"));
            man = Edit(man, root => root["overrides"]![1]!.AsObject().Remove("provenance"));
            File.WriteAllText(Path.Combine(dir, "x.case.json"), man);
            File.Copy(Path.Combine(CaseDir, "three-unit.network.json"), Path.Combine(dir, "three-unit.network.json"));
            GridModel m = V2CaseLoader.Load(Path.Combine(dir, "x.case.json"), SchemasDir);
            UnitSetting rp = m.UnitProtection!.For("Embedded C")!.Stages.Single(s => s.Device == "32").Pickup;
            Assert.True(rp.IsPlaceholder);
            Assert.Equal("[VERIFY] set by a per-unit override in case 'Unit protection three-unit' for unit 'Embedded C': Embedded C's reverse power protection from its connection agreement", rp.Provenance.Note);
        }
        finally { Directory.Delete(dir, recursive: true); }
    }
}
