using System.Text.Json.Nodes;
using GridSim.Core.IO.Json.CaseEditing;
using GridSim.Core.IO.Json.Schema;
using GridSim.Core.Licensing;
using GridSim.Tests.Editor;
using GridSim.Ui.Assets.Editing;
using GridSim.Ui.Assets.Editing.Elements;
using GridSim.Ui.Assets.Index;
using GridSim.Ui.Assets.Keys;
using GridSim.Ui.Assets.Navigation;
using Xunit;

namespace GridSim.Tests.Ui;

/// <summary>
/// UI programme Phase 4.3: the per-element editors (bus, line, two-winding transformer, generator with its converter
/// block, demand, OLTC, boundary, protection) as PROPERTIES specialisations over the field grid, on temporary copies of a
/// v1 case (data/uk, with a protection added) and a v2 fixture (data/uk projected to v2, with windings, a neutral
/// reactance, a converter block, a subtransient reactance and a protection added). For each editor and each format:
/// the sections and their field order exactly; the labels, units and help from FieldSpec exactly (v2 fields with no
/// schema title take the v1 counterpart's FieldSpec, named as the source); every derived read-out equal BY BITS to an
/// independent computation here (the formula written in the test from the raw JSON, not a call into the editor); an edit
/// through the editor changes exactly the bytes the generic grid's edit changes (lossless, nothing else); a pick in a
/// reference picker equals typing the id; locked fields stay locked with the loader's reason and a refused value keeps
/// its reason inline on the field; building an editor writes nothing.
/// </summary>
public sealed class UiElementEditorsTests
{
    private static LicenceGate Gate() => CaseSessionTests.Unlimited();

    // ================================================================================================================
    // Fixtures
    // ================================================================================================================

    // The v1 protection added to the copy of data/uk (data/uk has none), and its v2 twin.
    private const string V1Protections = """[ { "name": "P1 overcurrent", "class": "prot-test", "monitorBus": 1, "capacityMw": 500, "targetBranch": "L1-2 N-S Scotland" } ]""";

    /// <summary>A prepared temporary case (v1 copy of data/uk or the v2 fixture) and its authoring session.</summary>
    private sealed class Case : IDisposable
    {
        private readonly EditorTestSupport.TempCopy? _v1;
        private readonly UiOverlayFixture? _v2;

        public Case(bool v2)
        {
            V2 = v2;
            if (!v2)
            {
                _v1 = new EditorTestSupport.TempCopy(EditorTestSupport.CaseDir("uk"));
                Dir = _v1.Dir;
                Patch("transmission.json", a => a[0]!.AsObject()["lengthKm"] = 120);
                Patch("generation.json", a => a[0]!.AsObject()["converterFaultCurrentPu"] = 1.1);
                File.WriteAllText(Path.Combine(Dir, "protections.json"), V1Protections);
                A = CaseAuthoring.Open(AssetCaseSource.V1(Dir), Gate(), Array.Empty<string>());
            }
            else
            {
                _v2 = new UiOverlayFixture(withOverlay: false);
                Dir = _v2.CaseDir;
                JsonObject n = JsonNode.Parse(File.ReadAllText(_v2.NetworkPath))!.AsObject();
                n["lines"]![0]!.AsObject()["lengthKm"] = 120;
                JsonObject t0 = n["transformers"]![0]!.AsObject();
                t0["windings"] = JsonNode.Parse("""{ "from": "WyeGrounded", "to": "Delta" }""");
                t0["neutralXPu"] = JsonNode.Parse("""{ "from": 0.01 }""");
                JsonObject g0 = n["generators"]![0]!.AsObject();
                g0["converter"] = JsonNode.Parse("""{ "interfaced": true, "faultCurrentPu": 1.2 }""");
                g0["subtransientXdPu"] = 0.2;
                n["protections"] = JsonNode.Parse("""[ { "name": "P1 overcurrent", "template": "gridsim.test.relay@1", "monitorBus": 1, "capacityMw": 500, "targetBranch": "L1-2 N-S Scotland" } ]""");
                File.WriteAllText(_v2.NetworkPath, n.ToJsonString());
                A = CaseAuthoring.Open(AssetCaseSource.V2(_v2.ManifestPath), Gate(), Array.Empty<string>());
            }
        }

        public bool V2 { get; }

        public string Dir { get; }

        public CaseAuthoring A { get; }

        private void Patch(string file, Action<JsonArray> change)
        {
            string p = Path.Combine(Dir, file);
            JsonArray a = JsonNode.Parse(File.ReadAllText(p))!.AsArray();
            change(a);
            File.WriteAllText(p, a.ToJsonString());
        }

        /// <summary>The first record of <paramref name="role"/>.</summary>
        public CaseRef Rec(string role, int index = 0) => A.Records(A.Collections().First(c => c.Role == role))[index].Ref;

        /// <summary>The editor of the first record of <paramref name="role"/>.</summary>
        public ElementEditorModel Editor(string role, int index = 0)
        {
            CaseRef r = Rec(role, index);
            return ElementEditors.For(A, role, A.FieldGrid(r)) ?? throw new InvalidOperationException("no editor for " + role);
        }

        /// <summary>The raw JSON of the record (read from disk, independently of the editing engine).</summary>
        public JsonObject Raw(string role, int index = 0) => Elements(role)[index]!.AsObject();

        public JsonArray Elements(string role)
        {
            if (V2) return JsonNode.Parse(File.ReadAllText(_v2!.NetworkPath))![role]!.AsArray();
            string file = role switch
            {
                CaseRoles.Buses => "buses.json",
                CaseRoles.Lines => "transmission.json",
                CaseRoles.Transformers => "transformer.json",
                CaseRoles.Generators => "generation.json",
                CaseRoles.Demands => "demand.json",
                CaseRoles.Oltcs => "onloadtapchanger.json",
                CaseRoles.Boundaries => "boundaries.json",
                CaseRoles.Protections => "protections.json",
                _ => throw new ArgumentException(role),
            };
            return JsonNode.Parse(File.ReadAllText(Path.Combine(Dir, file)))!.AsArray();
        }

        /// <summary>The system base MVA as the file says it (v1 system.json, v2 manifest system.baseMva).</summary>
        public double BaseMva => V2
            ? JsonNode.Parse(File.ReadAllText(_v2!.ManifestPath))!["system"]!["baseMva"]!.GetValue<double>()
            : JsonNode.Parse(File.ReadAllText(Path.Combine(Dir, "system.json")))!["baseMva"]!.GetValue<double>();

        /// <summary>The base kV of bus <paramref name="id"/> as the file says it.</summary>
        public double Kv(int id) => Elements(CaseRoles.Buses).Single(b => b!["id"]!.GetValue<int>() == id)!["baseKv"]!.GetValue<double>();

        /// <summary>Every document's bytes now (null: the file does not exist).</summary>
        public byte[]?[] Bytes() => A.Session.Documents.Select(d => d.Exists ? d.Text.ToArray() : null).ToArray();

        public void Dispose()
        {
            _v1?.Dispose();
            _v2?.Dispose();
        }
    }

    private static double? Opt(JsonObject o, string m) => o[m] is JsonNode n ? n.GetValue<double>() : null;

    private static void Bits(double expected, DerivedReadout r)
    {
        Assert.True(r.Value.HasValue, r.Label + ": " + r.Text);
        Assert.True(BitConverter.DoubleToInt64Bits(expected) == BitConverter.DoubleToInt64Bits(r.Value!.Value),
            $"{r.Label}: expected {expected:R}, the editor shows {r.Value.Value:R} ({r.Formula})");
    }

    public static TheoryData<string, bool> KindsAndFormats()
    {
        var d = new TheoryData<string, bool>();
        foreach (string role in ElementEditors.Roles)
        {
            d.Add(role, false);
            d.Add(role, true);
        }
        return d;
    }

    // ================================================================================================================
    // Sections and field order, exactly
    // ================================================================================================================

    // The expected (section, field paths) of each editor on the prepared records.
    private static (string Title, string[] Paths)[] Expected(string role, bool v2) => role switch
    {
        CaseRoles.Buses => new[]
        {
            ("Identity and type", new[] { "id", "name", "type" }),
            ("Voltage", new[] { "baseKv", "vm", "vaDeg", "vminPu", "vmaxPu" }),
            ("Shunt (at 1 pu)", new[] { "gs", "bs" }),
            ("Load model", new[] { "zip" }),
            ("Layout", new[] { "x", "y" }),
        },
        CaseRoles.Lines => new[]
        {
            ("Identity and ends", new[] { "name", "from", "to", "equipment", "lengthKm" }),
            ("Electrical (per unit on the case base, one circuit)", v2 ? new[] { "rPu", "xPu", "bPu", "circuits", "r0Pu", "x0Pu" } : new[] { "rPu", "xPu", "bPu", "circuits", "rPu0", "xPu0" }),
            ("Ratings (one circuit)", new[] { "ratingMva", "ratingBMva", "ratingCMva" }),
            ("Converted transformer branch", new[] { "isTransformer", "tapRatio", "phaseShiftDeg" }),
            ("Type, template and service", v2 ? new[] { "type", "template", "inService" } : new[] { "dynamicsClass" }),
        },
        CaseRoles.Transformers => v2
            ? new[]
            {
                ("Identity and ends", new[] { "name", "from", "to" }),
                ("Windings and ratings", new[] { "windings", "windings.from", "windings.to", "ratingMva", "ratingBMva", "ratingCMva" }),
                ("Impedance (per unit on the case base)", new[] { "rPu", "xPu", "r0Pu", "x0Pu" }),
                ("Tap and phase shift", new[] { "tapRatio", "phaseShiftDeg" }),
                ("Neutral earthing", new[] { "neutralXPu", "neutralXPu.from", "neutralXPu.to" }),
                ("Type, template and service", new[] { "type", "template", "inService" }),
            }
            : new[]
            {
                ("Identity and ends", new[] { "name", "from", "to" }),
                ("Windings and ratings", new[] { "ratingMva", "ratingBMva", "ratingCMva" }),
                ("Impedance (per unit on the case base)", new[] { "rPu", "xPu" }),
                ("Tap and phase shift", new[] { "tapRatio", "phaseShiftDeg" }),
                ("Type, template and service", new[] { "dynamicsClass" }),
            },
        CaseRoles.Generators => new[]
        {
            ("Identity and role", new[] { "name", "bus", "type", "role", "inService", "powerFlowOnly" }),
            ("Real power", new[] { "capacityMva", "outputMw", "capacityFactor", "pMinMw", "pMaxMw" }),
            ("Reactive power and voltage", new[] { "qMinMvar", "qMaxMvar", "qOutputMvar", "voltageSetpoint" }),
            ("Dynamics parameters", v2 ? new[] { "inertiaH", "unitTransformerXPu", "subtransientXdPu", "negativeSeqXPu", "template" } : new[] { "inertiaH", "unitTransformerXPu", "dynamicsClass" }),
            ("Converter block", v2 ? new[] { "converter", "converter.interfaced", "converter.faultCurrentPu", "converter.gridForming" } : new[] { "converterInterfaced", "converterFaultCurrentPu", "gridForming" }),
        },
        CaseRoles.Demands => new[]
        {
            ("Identity and bus", new[] { "name", "bus" }),
            ("Power", new[] { "pMw", "qMvar", "powerFactor" }),
        },
        CaseRoles.Oltcs => new[]
        {
            ("Transformer and controlled bus", new[] { "transformer", "controlsBus" }),
            ("Voltage control", new[] { "targetVpu", "deadbandPu" }),
            ("Tap range", new[] { "minTap", "maxTap", "stepPu" }),
        },
        CaseRoles.Boundaries => new[]
        {
            ("Identity and limit", new[] { "name", "limitMw" }),
            ("Branches", new[] { "branches", "branches[0].branch", "branches[0].direction" }),
        },
        CaseRoles.Protections => new[]
        {
            ("Identity and template", v2 ? new[] { "name", "template" } : new[] { "name", "class" }),
            ("Monitoring and target", new[] { "monitorBus", "targetBranch", "capacityMw" }),
        },
        _ => throw new ArgumentException(role),
    };

    [Theory]
    [MemberData(nameof(KindsAndFormats))]
    public void Each_editor_has_exactly_its_sections_and_field_order_and_shows_every_grid_row_once(string role, bool v2)
    {
        using var c = new Case(v2);
        ElementEditorModel e = c.Editor(role);
        (string Title, string[] Paths)[] expected = Expected(role, v2);
        Assert.Equal(expected.Select(x => x.Title), e.Sections.Select(s => s.Title));
        for (int i = 0; i < expected.Length; i++)
            Assert.Equal(expected[i].Paths, e.Sections[i].Fields.Select(f => f.Path));
        // Nothing hidden: every row of the record's own grid is a top-level field of the editor, once, and it is the SAME
        // row object (so an edit in the editor is the grid row's own edit).
        FieldGridModel grid = e.Grid;
        List<EditorField> top = e.AllFields.Where(f => !f.IsNested).ToList();
        Assert.Equal(grid.Rows.Count, top.Count);
        Assert.Equal(grid.Rows.ToHashSet(), top.Select(f => f.Row).ToHashSet());
        Assert.DoesNotContain(e.Sections, s => s.Title == ElementEditorModel.OtherSection);
        Assert.Equal(v2, e.IsV2);
        Assert.Equal(grid.Heading, e.Heading);
        Assert.Equal(c.A.Describe(c.Rec(role)), e.Heading);
    }

    [Fact]
    public void Kinds_without_an_editor_and_non_element_records_keep_the_generic_grid()
    {
        using var c = new Case(v2: false);
        CaseRef sys = c.A.RootRecords().Single().Ref;   // system.json
        Assert.Null(ElementEditors.For(c.A, null, c.A.FieldGrid(sys)));
        Assert.Null(ElementEditors.For(c.A, CaseRoles.AssetClasses, c.A.FieldGrid(c.Rec(CaseRoles.Lines))));
        Assert.Equal(new[] { "buses", "lines", "transformers", "generators", "demands", "oltcs", "boundaries", "protections" }, ElementEditors.Roles);

        using var f = new UiOverlayFixture(withOverlay: true);
        CaseAuthoring a = CaseAuthoring.Open(AssetCaseSource.V2(f.ManifestPath), Gate(), Array.Empty<string>());
        CaseRef ov = a.Records(a.Collections().First(x => x.Role == CaseRoles.Overlay))[0].Ref;
        Assert.Null(ElementEditors.For(a, CaseRoles.Overlay, a.FieldGrid(ov)));
    }

    // ================================================================================================================
    // Labels, units and help from FieldSpec
    // ================================================================================================================

    // The test's own list of where each v2 record shape's fields came from in v1 (the v2 descriptions name them).
    private static (string File, string Pointer)? V1ShapeFor(string v2Pointer) => v2Pointer switch
    {
        "/$defs/bus" => ("buses.schema.json", "/$defs/bus"),
        "/$defs/generator" or "/$defs/converter" => ("generation.schema.json", "/$defs/generator"),
        "/$defs/demand" => ("demand.schema.json", "/$defs/demand"),
        "/$defs/line" => ("transmission.schema.json", "/$defs/line"),
        "/$defs/transformer" => ("transformer.schema.json", "/$defs/transformer"),
        "/$defs/oltc" => ("onloadtapchanger.schema.json", "/$defs/oltc"),
        "/$defs/boundary" => ("boundaries.schema.json", "/$defs/boundary"),
        "/$defs/boundary/properties/branches/items" => ("boundaries.schema.json", "/$defs/boundaryBranch"),
        "/$defs/protection" => ("protections.schema.json", "/$defs/protection"),
        _ => null,
    };

    private static string V1Name(string v2Pointer, string field) => (v2Pointer, field) switch
    {
        ("/$defs/line", "r0Pu") => "rPu0",
        ("/$defs/line", "x0Pu") => "xPu0",
        ("/$defs/line" or "/$defs/transformer" or "/$defs/generator", "template") => "dynamicsClass",
        ("/$defs/protection", "template") => "class",
        ("/$defs/converter", "interfaced") => "converterInterfaced",
        ("/$defs/converter", "faultCurrentPu") => "converterFaultCurrentPu",
        _ => field,
    };

    // The schema shape of a field at an editor path ("rPu", "converter.interfaced", "branches[0].branch").
    private static RecordShape ShapeOf(Case c, CaseRef rec, string path)
    {
        CaseDocument d = c.A.Session.Documents[rec.Document];
        int dot = path.LastIndexOf('.');
        if (dot < 0) return c.A.Session.ShapeAt(d, rec.Pointer)!;
        string block = path[..dot].Replace("[", "/", StringComparison.Ordinal).Replace("]", "", StringComparison.Ordinal);
        return c.A.Session.ShapeAt(d, rec.Pointer + "/" + block)!;
    }

    [Theory]
    [MemberData(nameof(KindsAndFormats))]
    public void Labels_units_and_help_come_from_FieldSpec_or_the_v1_counterparts_FieldSpec_naming_the_source(string role, bool v2)
    {
        using var c = new Case(v2);
        CaseRef rec = c.Rec(role);
        ElementEditorModel e = c.Editor(role);
        foreach (EditorField f in e.AllFields)
        {
            RecordShape shape = ShapeOf(c, rec, f.Path);
            FieldSpec spec = shape.Field(f.Field)!;
            Assert.NotNull(spec);
            if (!v2)
            {
                // v1: every field's label, unit and help are its FieldSpec's, exactly as the generic grid shows them.
                Assert.Equal("schema", f.LabelSource);
                Assert.Equal(PropertyRow.SplitTitle(spec.Title), (f.Label, f.Unit));
                Assert.Equal((f.Row.Label, f.Row.Unit), (f.Label, f.Unit));
                Assert.Equal(spec.Description, f.Help);
                Assert.Equal(f.Row.Help, f.Help);
                continue;
            }
            FieldSpec? v1 = V1ShapeFor(shape.Location.Pointer) is { } loc
                ? FieldCatalogue.V1.Shape(new SchemaLocation(loc.File, loc.Pointer)).Field(V1Name(shape.Location.Pointer, f.Field)) : null;
            string v1Source = V1ShapeFor(shape.Location.Pointer) is { } l2 ? "v1 schema " + l2.File + " " + V1Name(shape.Location.Pointer, f.Field) : "";
            if (spec.Title != spec.Name)
            {
                Assert.Equal("schema", f.LabelSource);
                Assert.Equal(PropertyRow.SplitTitle(spec.Title), (f.Label, f.Unit));
            }
            else if (v1 is not null)
            {
                Assert.Equal(v1Source, f.LabelSource);
                Assert.Equal(PropertyRow.SplitTitle(v1.Title), (f.Label, f.Unit));
            }
            else
            {
                Assert.Equal("editor", f.LabelSource);
                Assert.NotEqual(f.Field, f.Label);
            }
            if (spec.Description.Length > 0) Assert.Equal((spec.Description, "schema"), (f.Help, f.HelpSource));
            else if (v1 is { Description.Length: > 0 }) Assert.Equal((v1.Description, v1Source), (f.Help, f.HelpSource));
            else Assert.Contains(f.HelpSource, new[] { "editor", "" });
        }
    }

    [Fact]
    public void V2_examples_of_borrowed_and_own_labels()
    {
        using var c = new Case(v2: true);
        EditorField r = c.Editor(CaseRoles.Lines).Field("rPu");
        Assert.Equal(("Resistance", "pu, one circuit", "v1 schema transmission.schema.json rPu"), (r.Label, r.Unit, r.LabelSource));
        EditorField r0 = c.Editor(CaseRoles.Lines).Field("r0Pu");
        Assert.Equal(("Zero-sequence resistance", "pu", "v1 schema transmission.schema.json rPu0"), (r0.Label, r0.Unit, r0.LabelSource));
        Assert.Equal(("Zero-sequence series resistance, per unit on the case base (v1 rPu0).", "schema"), (r0.Help, r0.HelpSource));
        EditorField t0 = c.Editor(CaseRoles.Transformers).Field("r0Pu");
        Assert.Equal(("Zero-sequence resistance", "pu, case base", "editor"), (t0.Label, t0.Unit, t0.LabelSource));
        EditorField k = c.Editor(CaseRoles.Generators).Field("converter.faultCurrentPu");
        Assert.Equal(("Converter fault current", "x rated", "v1 schema generation.schema.json converterFaultCurrentPu"), (k.Label, k.Unit, k.LabelSource));
        Assert.True(k.IsNested);
    }

    // ================================================================================================================
    // Pickers
    // ================================================================================================================

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void References_enums_and_booleans_have_typed_pickers_over_the_cases_records(bool v2)
    {
        using var c = new Case(v2);
        int[] busIds = c.Elements(CaseRoles.Buses).Select(b => b!["id"]!.GetValue<int>()).Order().ToArray();
        EditorField from = c.Editor(CaseRoles.Lines).Field("from");
        Assert.Equal("bus", from.PickerKind);
        Assert.True(from.IsReference);
        Assert.Equal(busIds.Select(i => i.ToString(System.Globalization.CultureInfo.InvariantCulture)), from.Choices.Select(x => x.Value));
        JsonObject bus1 = c.Elements(CaseRoles.Buses).Single(b => b!["id"]!.GetValue<int>() == 1)!.AsObject();
        Assert.Equal("1  '" + bus1["name"]!.GetValue<string>() + "' (" + c.Kv(1).ToString("R", System.Globalization.CultureInfo.InvariantCulture) + " kV)",
            from.Choices.Single(x => x.Value == "1").Display);

        string[] branchNames = c.Elements(CaseRoles.Lines).Select(l => l!["name"]!.GetValue<string>())
            .Concat(c.Elements(CaseRoles.Transformers).Select(t => t!["name"]!.GetValue<string>())).ToArray();
        EditorField tr = c.Editor(CaseRoles.Oltcs).Field("transformer");
        Assert.Equal("branch", tr.PickerKind);
        Assert.Equal(branchNames.ToHashSet(), tr.Choices.Select(x => x.Value).ToHashSet());
        Assert.Equal(branchNames.Length, tr.ChoiceCount);
        Assert.Equal(c.Elements(CaseRoles.Transformers)[0]!["name"]!.GetValue<string>() + "  (transformer)",
            tr.Choices.Single(x => x.Value == c.Elements(CaseRoles.Transformers)[0]!["name"]!.GetValue<string>()).Display);
        Assert.Equal("branch", c.Editor(CaseRoles.Boundaries).Field("branches[0].branch").PickerKind);
        Assert.Equal("branch", c.Editor(CaseRoles.Protections).Field("targetBranch").PickerKind);
        Assert.Equal("bus", c.Editor(CaseRoles.Protections).Field("monitorBus").PickerKind);

        EditorField eq = c.Editor(CaseRoles.Lines).Field("equipment");
        Assert.Equal("enum", eq.PickerKind);
        Assert.Equal(new[] { "ohl", "cable" }, eq.Choices.Select(x => x.Value));
        EditorField ty = c.Editor(CaseRoles.Buses).Field("type");
        Assert.Equal(v2 ? new[] { "pq", "pv", "slack" } : new[] { "pq", "pv", "slack", "ref", "swing", "gen" }, ty.Choices.Select(x => x.Value));
        EditorField pfo = c.Editor(CaseRoles.Generators).Field("powerFlowOnly");
        Assert.Equal("boolean", pfo.PickerKind);
        Assert.Equal(new[] { "true", "false" }, pfo.Choices.Select(x => x.Value));
        // A plain number has no picker.
        Assert.False(c.Editor(CaseRoles.Lines).Field("rPu").HasChoices);
    }

    // ================================================================================================================
    // Derived read-outs, by bits against an independent computation
    // ================================================================================================================

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Line_read_outs(bool v2)
    {
        using var c = new Case(v2);
        JsonObject l = c.Raw(CaseRoles.Lines);
        double r = l["rPu"]!.GetValue<double>(), x = l["xPu"]!.GetValue<double>();
        double b = Opt(l, "bPu") ?? 0.0, n = Opt(l, "circuits") ?? 1.0, km = l["lengthKm"]!.GetValue<double>(), rating = l["ratingMva"]!.GetValue<double>();
        double kv = c.Kv(l["from"]!.GetValue<int>()), s = c.BaseMva;
        double zb = kv * kv / s;                       // ohm base: kV^2 / MVA
        ElementEditorModel e = c.Editor(CaseRoles.Lines);
        Bits(x / r, e.Readout("X/R ratio"));
        Bits(zb, e.Readout("Base impedance at the from bus"));
        Bits(r * zb, e.Readout("Series resistance per circuit"));
        Bits(x * zb, e.Readout("Series reactance per circuit"));
        Bits(Math.Sqrt(r * r + x * x) * zb, e.Readout("Series impedance per circuit"));
        Bits(Math.Sqrt(r * r + x * x) * zb / n, e.Readout("Series impedance, all circuits in parallel"));
        Bits(x * zb / km, e.Readout("Reactance per km"));
        Bits(b / zb * 1e6, e.Readout("Charging susceptance per circuit"));
        Bits(b * n * s, e.Readout("Charging at 1 pu, all circuits"));
        Bits(rating * n, e.Readout("Rating A, all circuits"));
        Bits(rating / (Math.Sqrt(3.0) * kv), e.Readout("Rated current per circuit"));
        Assert.Equal(new[] { "X/R ratio", "Base impedance at the from bus", "Series resistance per circuit", "Series reactance per circuit",
            "Series impedance per circuit", "Series impedance, all circuits in parallel", "Reactance per km", "Charging susceptance per circuit",
            "Charging at 1 pu, all circuits" }, e.Section(LineEditorModel.Electrical).Readouts.Select(x2 => x2.Label));
        Assert.Equal("ohm", e.Readout("Series impedance per circuit").Unit);
        Assert.Equal("derived", DerivedReadout.Marker);
        Assert.Equal("sqrt(rPu^2 + xPu^2) * Zb", e.Readout("Series impedance per circuit").Formula);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Transformer_read_outs(bool v2)
    {
        using var c = new Case(v2);
        JsonObject t = c.Raw(CaseRoles.Transformers);
        double r = t["rPu"]!.GetValue<double>(), x = t["xPu"]!.GetValue<double>(), rating = t["ratingMva"]!.GetValue<double>(), tap = Opt(t, "tapRatio") ?? 1.0;
        double kvF = c.Kv(t["from"]!.GetValue<int>()), kvT = c.Kv(t["to"]!.GetValue<int>()), s = c.BaseMva;
        ElementEditorModel e = c.Editor(CaseRoles.Transformers);
        Bits(x / r, e.Readout("X/R ratio"));
        Bits(r * rating / s, e.Readout("Resistance on own rating"));          // z(own) = z(case) * S(own) / S(case)
        Bits(x * rating / s, e.Readout("Reactance on own rating"));
        Bits(100.0 * Math.Sqrt(r * r + x * x) * rating / s, e.Readout("Impedance on own rating"));
        Bits(Math.Sqrt(r * r + x * x) * kvF * kvF / s, e.Readout("Series impedance referred to the from side"));
        Bits(rating / (Math.Sqrt(3.0) * kvF), e.Readout("Rated current, from side"));
        Bits(rating / (Math.Sqrt(3.0) * kvT), e.Readout("Rated current, to side"));
        Bits((tap - 1.0) * 100.0, e.Readout("Tap off nominal"));
        if (v2)
        {
            Assert.Equal("YNd", e.Readout("Vector group (connections, no clock number)").Text);
            Bits(0.01 * kvF * kvF / s, e.Readout("Neutral reactance, from side"));
            Assert.DoesNotContain(e.Section(TransformerEditorModel.Neutral).Readouts, x2 => x2.Label == "Neutral reactance, to side");
        }
        else Assert.DoesNotContain(e.Sections, x2 => x2.Title == TransformerEditorModel.Neutral);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Generator_read_outs(bool v2)
    {
        using var c = new Case(v2);
        JsonObject g = c.Raw(CaseRoles.Generators);
        double cap = g["capacityMva"]!.GetValue<double>(), kv = c.Kv(g["bus"]!.GetValue<int>()), s = c.BaseMva;
        double output = Opt(g, "outputMw") ?? cap * g["capacityFactor"]!.GetValue<double>();
        double pMax = Opt(g, "pMaxMw") ?? cap;                                  // absent: capacityMva
        double qMin = Opt(g, "qMinMvar") ?? -cap, qMax = Opt(g, "qMaxMvar") ?? cap;   // absent: -/+ capacityMva
        double q = Math.Max(Math.Abs(qMin), Math.Abs(qMax));
        double sCorner = Math.Sqrt(pMax * pMax + q * q);
        double h = Opt(g, "inertiaH") ?? 0.0, vs = Opt(g, "voltageSetpoint") ?? 1.0, xt = Opt(g, "unitTransformerXPu") ?? 0.0;
        double k = v2 ? g["converter"]!["faultCurrentPu"]!.GetValue<double>() : g["converterFaultCurrentPu"]!.GetValue<double>();
        ElementEditorModel e = c.Editor(CaseRoles.Generators);
        Bits(output, e.Readout("Output (written, else capacity x factor)"));
        Bits(pMax, e.Readout("Maximum output used"));
        Bits(sCorner, e.Readout("Apparent power at the P/Q corner"));
        Bits(pMax / sCorner, e.Readout("Power factor at the corner"));
        Bits(vs * kv, e.Readout("Voltage set point"));
        Bits(h * cap, e.Readout("Stored energy at rated MVA"));                 // E = H * S (MJ)
        Bits(xt * s / cap, e.Readout("Unit transformer reactance on the case base"));
        Bits(k * cap / (Math.Sqrt(3.0) * kv), e.Readout("Converter fault current"));
        if (v2) Bits(0.2 * s / cap, e.Readout("Subtransient reactance on the case base"));
        else Assert.DoesNotContain(e.Section(GeneratorEditorModel.Dynamics).Readouts, x2 => x2.Label == "Subtransient reactance on the case base");
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Demand_bus_oltc_boundary_and_protection_read_outs(bool v2)
    {
        using var c = new Case(v2);
        double s = c.BaseMva;

        // Demand: Q from the power factor when qMvar is absent (lagging: Q = P * sqrt(1 - pf^2) / pf), S, pf.
        JsonObject d = c.Raw(CaseRoles.Demands);
        double p = d["pMw"]!.GetValue<double>();
        double pf = Opt(d, "powerFactor") ?? 0.97;   // the schema's absent rule: 0.97 lagging
        double qd = Opt(d, "qMvar") ?? p * Math.Sqrt(1.0 - pf * pf) / pf;
        ElementEditorModel de = c.Editor(CaseRoles.Demands);
        Bits(qd, de.Readout("Reactive power used"));
        Bits(Math.Sqrt(p * p + qd * qd), de.Readout("Apparent power"));
        Bits(p / Math.Sqrt(p * p + qd * qd), de.Readout("Power factor"));

        // Bus.
        JsonObject b = c.Raw(CaseRoles.Buses);
        double kv = b["baseKv"]!.GetValue<double>();
        ElementEditorModel be = c.Editor(CaseRoles.Buses);
        Bits(kv * kv / s, be.Readout("Base impedance"));
        Bits(s / (Math.Sqrt(3.0) * kv), be.Readout("Base current"));
        Bits((Opt(b, "vm") ?? 1.0) * kv, be.Readout("Voltage magnitude"));
        Bits((Opt(b, "gs") ?? 0.0) / s, be.Readout("Shunt conductance"));
        Bits((Opt(b, "bs") ?? 0.0) / s, be.Readout("Shunt susceptance"));

        // OLTC.
        JsonObject o = c.Raw(CaseRoles.Oltcs);
        double kvC = c.Kv(o["controlsBus"]!.GetValue<int>());
        // The schema's absent rule (the v2 projection leaves a default out): targetVpu 1, minTap 0.9, maxTap 1.1, stepPu
        // 0.0125, deadbandPu 0.01.
        double lo = Opt(o, "minTap") ?? 0.9, hi = Opt(o, "maxTap") ?? 1.1, st = Opt(o, "stepPu") ?? 0.0125;
        ElementEditorModel oe = c.Editor(CaseRoles.Oltcs);
        Bits((Opt(o, "targetVpu") ?? 1.0) * kvC, oe.Readout("Target voltage"));
        Bits((Opt(o, "deadbandPu") ?? 0.01) * kvC, oe.Readout("Deadband"));
        Bits(Math.Round((hi - lo) / st, MidpointRounding.AwayFromZero) + 1.0, oe.Readout("Tap positions"));
        Assert.Equal(17.0, oe.Readout("Tap positions").Value);   // 0.9 .. 1.1 in 0.0125 steps
        Bits((lo - 1.0) * 100.0, oe.Readout("Lowest tap off nominal"));
        Bits((hi - 1.0) * 100.0, oe.Readout("Highest tap off nominal"));
        string tName = o["transformer"]!.GetValue<string>();
        int tIndex = c.Elements(CaseRoles.Transformers).Select((t, i) => (t, i)).First(z => z.t!["name"]!.GetValue<string>() == tName).i;
        Assert.EndsWith((v2 ? "/transformers/" : "/") + tIndex.ToString(System.Globalization.CultureInfo.InvariantCulture) + " (transformers)", oe.Readout("Drives").Text, StringComparison.Ordinal);

        // Boundary: the named branches' A ratings (lines times circuits) and the limit's share of them.
        JsonObject bd = c.Raw(CaseRoles.Boundaries);
        double sum = 0;
        foreach (JsonNode? br in bd["branches"]!.AsArray())
        {
            JsonObject line = c.Elements(CaseRoles.Lines).Single(z => z!["name"]!.GetValue<string>() == br!["branch"]!.GetValue<string>())!.AsObject();
            sum += line["ratingMva"]!.GetValue<double>() * (Opt(line, "circuits") ?? 1.0);
        }
        ElementEditorModel bde = c.Editor(CaseRoles.Boundaries);
        Bits(bd["branches"]!.AsArray().Count, bde.Readout("Branches listed"));
        Bits(sum, bde.Readout("Sum of the named branches' A ratings"));
        Bits(bd["limitMw"]!.GetValue<double>() / sum * 100.0, bde.Readout("Limit as a share of that sum"));

        // Protection.
        ElementEditorModel pe = c.Editor(CaseRoles.Protections);
        Bits(c.Kv(1), pe.Readout("Monitored bus base voltage"));
        Assert.Equal("'L1-2 N-S Scotland' is a line", pe.Readout("Target branch").Text);
    }

    [Fact]
    public void A_read_out_that_cannot_be_computed_says_why_and_never_invents_a_value()
    {
        using var c = new Case(v2: true);
        CaseRef line = c.Rec(CaseRoles.Lines);
        Assert.True(c.A.FieldGrid(line).Edit("rPu", "0").Ok);
        DerivedReadout xr = c.Editor(CaseRoles.Lines).Readout("X/R ratio");
        Assert.Null(xr.Value);
        Assert.Equal("n/a: rPu is 0", xr.Text);
        CaseRef t = c.Rec(CaseRoles.Transformers);
        Assert.True(c.A.FieldGrid(t).Reset("ratingMva").Ok);
        DerivedReadout own = c.Editor(CaseRoles.Transformers).Readout("Impedance on own rating");
        Assert.Null(own.Value);
        Assert.Equal("n/a: ratingMva is 0 (unrated)", own.Text);
    }

    // ================================================================================================================
    // Edits: lossless (the grid's own bytes), locked fields, inline refusals, no write on build
    // ================================================================================================================

    // A plain value edit per kind (the field and a new value of its type).
    private static (string Field, string Typed) PlainEdit(string role) => role switch
    {
        CaseRoles.Buses => ("vm", "1.02"),
        CaseRoles.Lines => ("xPu", "0.21"),
        CaseRoles.Transformers => ("xPu", "0.06"),
        CaseRoles.Generators => ("capacityMva", "1400"),
        CaseRoles.Demands => ("pMw", "650"),
        CaseRoles.Oltcs => ("targetVpu", "1.01"),
        CaseRoles.Boundaries => ("limitMw", "3300"),
        CaseRoles.Protections => ("capacityMw", "550"),
        _ => throw new ArgumentException(role),
    };

    [Theory]
    [MemberData(nameof(KindsAndFormats))]
    public void An_edit_through_the_editor_changes_exactly_the_bytes_the_generic_grids_edit_changes(string role, bool v2)
    {
        (string field, string typed) = PlainEdit(role);
        using var viaEditor = new Case(v2);
        using var viaGrid = new Case(v2);
        byte[]?[] before = viaEditor.Bytes();
        Assert.Equal(before, viaGrid.Bytes());

        ElementEditorModel e = viaEditor.Editor(role);
        Assert.Equal(before, viaEditor.Bytes());   // building an editor writes nothing
        EditorField f = e.Field(field);
        f.EditText = typed;
        AuthoringResult r1 = f.Commit();
        Assert.True(r1.Ok, r1.Message);
        Assert.False(f.HasError);
        AuthoringResult r2 = viaGrid.A.FieldGrid(viaGrid.Rec(role)).Edit(field, typed);
        Assert.True(r2.Ok, r2.Message);
        Assert.Equal(r2.Message, r1.Message);
        Assert.Equal(viaGrid.Bytes(), viaEditor.Bytes());
        Assert.NotEqual(before, viaEditor.Bytes());
        Assert.Equal(1, viaEditor.A.UndoDepth);
        // Undo puts every byte back.
        Assert.True(viaEditor.A.Undo().Ok);
        Assert.Equal(before, viaEditor.Bytes());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void A_pick_in_a_picker_equals_typing_the_value_and_a_nested_edit_is_the_engines_nested_edit(bool v2)
    {
        using var viaPick = new Case(v2);
        using var viaGrid = new Case(v2);
        EditorField to = viaPick.Editor(CaseRoles.Lines).Field("to");
        AuthoringResult r1 = to.Pick(to.Choices.Single(x => x.Value == "3"));
        Assert.True(r1.Ok, r1.Message);
        Assert.True(viaGrid.A.FieldGrid(viaGrid.Rec(CaseRoles.Lines)).Edit("to", "3").Ok);
        Assert.Equal(viaGrid.Bytes(), viaPick.Bytes());

        EditorField eq = viaPick.Editor(CaseRoles.Lines).Field("equipment");
        Assert.True(eq.Pick(eq.Choices.Single(x => x.Value == "cable")).Ok);
        Assert.True(viaGrid.A.FieldGrid(viaGrid.Rec(CaseRoles.Lines)).Edit("equipment", "cable").Ok);
        Assert.Equal(viaGrid.Bytes(), viaPick.Bytes());

        // A boundary entry's direction (a nested record of an array): the editor's edit is the engine's SetField on the entry.
        CaseRef bd = viaGrid.Rec(CaseRoles.Boundaries);
        EditorField dir = viaPick.Editor(CaseRoles.Boundaries).Field("branches[0].direction");
        Assert.True(dir.Pick(dir.Choices.Single(x => x.Value == "-1")).Ok);
        OpResult op = CaseOps.SetField(viaGrid.A.Session, bd.Document, bd.Pointer + "/branches/0", "direction", "-1");
        Assert.True(op.Ok && op.Changed, op.Message);
        Assert.Equal(viaGrid.Bytes(), viaPick.Bytes());
        if (!v2) return;

        // v2: the converter block's fault current multiple, a field of the nested object.
        CaseRef g = viaGrid.Rec(CaseRoles.Generators);
        EditorField k = viaPick.Editor(CaseRoles.Generators).Field("converter.faultCurrentPu");
        k.EditText = "1.5";
        Assert.True(k.Commit().Ok);
        Assert.True(CaseOps.SetField(viaGrid.A.Session, g.Document, g.Pointer + "/converter", "faultCurrentPu", "1.5").Ok);
        Assert.Equal(viaGrid.Bytes(), viaPick.Bytes());
        Assert.Equal("1.5", viaPick.Editor(CaseRoles.Generators).Field("converter.faultCurrentPu").Literal);
    }

    [Theory]
    [InlineData(CaseRoles.Lines)]
    [InlineData(CaseRoles.Transformers)]
    public void V2_fields_the_loader_refuses_stay_locked_with_the_loaders_reason_and_the_refusal_is_inline(string role)
    {
        using var c = new Case(v2: true);
        byte[]?[] before = c.Bytes();
        ElementEditorModel e = c.Editor(role);
        string shape = role == CaseRoles.Lines ? "/$defs/line" : "/$defs/transformer";
        foreach (string field in new[] { "type", "inService" })
        {
            LockedField lf = LockedFields.Find(LockedFields.NetworkSchema, shape, field)!;
            EditorField f = e.Field(field);
            Assert.True(f.IsLocked);
            Assert.False(f.IsEditable);
            Assert.False(f.HasChoices);
            Assert.Equal(lf.Reason, f.LockReason);
            Assert.Contains(lf.Reason, f.StateText, StringComparison.Ordinal);
            Assert.Equal("Type, template and service", e.Sections.Single(s => s.Fields.Contains(f)).Title);
            f.EditText = field == "type" ? "\"gridsim.test.other@1\"" : "false";
            AuthoringResult r = f.Commit();
            Assert.False(r.Ok);
            Assert.EndsWith(lf.Reason, r.Message, StringComparison.Ordinal);
            Assert.True(f.HasError);
            Assert.Equal(r.Message, f.Error);
        }
        Assert.Equal(before, c.Bytes());
        Assert.Equal(0, c.A.UndoDepth);

        // v1 has no locked field in any editor.
        using var v1 = new Case(v2: false);
        foreach (string r1 in ElementEditors.Roles) Assert.DoesNotContain(v1.Editor(r1).AllFields, f => f.IsLocked);
    }

    [Theory]
    [MemberData(nameof(KindsAndFormats))]
    public void A_value_the_engine_refuses_keeps_its_reason_inline_and_changes_nothing(string role, bool v2)
    {
        using var c = new Case(v2);
        byte[]?[] before = c.Bytes();
        (string field, _) = PlainEdit(role);
        EditorField f = c.Editor(role).Field(field);
        f.EditText = "not a number";
        AuthoringResult r = f.Commit();
        Assert.False(r.Ok);
        Assert.Equal(r.Message, f.Error);
        Assert.StartsWith(c.A.Session.ShapeAt(c.A.Session.Documents[c.Rec(role).Document], c.Rec(role).Pointer)!.Field(field)!.Title + ": ", f.Error, StringComparison.Ordinal);
        Assert.Equal(before, c.Bytes());
        // A good value afterwards clears the inline reason.
        f.EditText = PlainEdit(role).Typed;
        Assert.True(f.Commit().Ok);
        Assert.False(f.HasError);
    }

    [Fact]
    public void A_dangling_reference_typed_in_a_picker_field_is_refused_inline_by_the_engine()
    {
        using var c = new Case(v2: true);
        EditorField from = c.Editor(CaseRoles.Lines).Field("from");
        from.EditText = "99999";
        AuthoringResult r = from.Commit();
        Assert.False(r.Ok);
        Assert.Contains("bus 99999 does not exist", from.Error, StringComparison.Ordinal);
        EditorField tr = c.Editor(CaseRoles.Oltcs).Field("transformer");
        tr.EditText = "no such transformer";
        Assert.False(tr.Commit().Ok);
        Assert.Contains("no line or transformer is named 'no such transformer'", tr.Error, StringComparison.Ordinal);
    }

    // ================================================================================================================
    // The Author model: PROPERTIES shows the editor, the grid on request; editor actions report as the grid's
    // ================================================================================================================

    [Fact]
    public void The_Author_model_shows_the_editor_in_PROPERTIES_and_the_generic_grid_on_request_or_for_other_kinds()
    {
        using var c = new Case(v2: true);
        var m = new AssetAuthorModel(c.A);
        Assert.Null(m.Editor);
        Assert.False(m.IsEditorShown);
        Assert.True(m.SelectRecord(c.Rec(CaseRoles.Lines)));
        LineEditorModel e = Assert.IsType<LineEditorModel>(m.Editor);
        Assert.Same(m.Fields, e.Grid);
        Assert.True(m.HasEditor);
        Assert.False(m.IsEditorShown);   // OVERVIEW is shown
        m.SelectedSubTab = AssetNavigatorModel.PropertiesTab;
        Assert.True(m.IsEditorShown);
        Assert.False(m.IsFieldGridShown);
        Assert.True(m.IsViewToggleShown);
        Assert.Equal("SHOW ALL FIELDS AS A GRID", m.ViewToggleText);
        m.ShowFieldGrid = true;
        Assert.False(m.IsEditorShown);
        Assert.True(m.IsFieldGridShown);
        Assert.Equal("SHOW THE LINE EDITOR", m.ViewToggleText);
        m.ShowFieldGrid = false;

        // An editor edit is one undo step and selects the record; a refusal is in the status line AND inline.
        EditorField x = m.Editor!.Field("xPu");
        x.EditText = "0.21";
        Assert.True(m.CommitEditorField(x).Ok);
        Assert.Equal(1, c.A.UndoDepth);
        Assert.False(m.StatusIsError);
        Assert.Equal("0.21", m.Editor!.Field("xPu").Literal);   // the editor was rebuilt over the new grid
        EditorField n = m.Editor.Field("circuits");
        n.EditText = "two";
        AuthoringResult bad = m.CommitEditorField(n);
        Assert.False(bad.Ok);
        Assert.True(m.StatusIsError);
        Assert.Equal(m.Status, n.Error);
        Assert.Same(n, m.Editor.Field("circuits"));   // nothing changed, so the editor (and its inline reason) stays
        Assert.True(m.ResetEditorField(m.Editor.Field("lengthKm")).Ok);
        Assert.Equal(2, c.A.UndoDepth);

        // The case-file record (the manifest) and an asset with no editor keep the generic grid.
        Assert.True(m.SelectRecord(c.A.RootRecords().Single().Ref));
        Assert.Null(m.Editor);
        m.SelectedSubTab = AssetNavigatorModel.PropertiesTab;
        Assert.True(m.IsFieldGridShown);
        Assert.False(m.IsViewToggleShown);
    }
}
