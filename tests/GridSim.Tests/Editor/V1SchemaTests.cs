using System.Reflection;
using System.Text.Json;
using GridSim.Core.Diagnostics;
using GridSim.Core.IO.Json;
using GridSim.Core.IO.Json.CaseEditing;
using GridSim.Core.IO.Json.Lossless;
using GridSim.Core.IO.Json.Schema;
using GridSim.Tests.V2;
using Xunit;
using static GridSim.Tests.Editor.EditorTestSupport;

namespace GridSim.Tests.Editor;

/// <summary>
/// Slice E2 of the case editor (SCHEMA-EDITOR-PLAN.md section 5): the v1 schemas (schemas/v1), their embedding in
/// Core, and the validator's pointer overload. The drift guards pin the schemas to the loader's DTOs by reflection,
/// so a field added to a DTO without its schema (or the other way round) fails here, loudly.
/// </summary>
public class V1SchemaTests
{
    private static string V1Dir => Path.Combine(SchemasDir, "v1");

    /// <summary>Every v1 DTO type with the schema file and $def that describes it.</summary>
    private static IEnumerable<(Type Dto, string File, string Def)> AllDefs()
        => CaseFileSpec.V1Files.Select(f => (f.DtoType, f.SchemaFile, f.RecordDef)).Concat(CaseFileSpec.NestedDefs);

    private static JsonElement SchemaFile(string file)
    {
        using JsonDocument doc = JsonSchemaSubset.ParseFile(Path.Combine(V1Dir, file));
        return doc.RootElement.Clone();
    }

    // ---------------------------------------------------------------------------------------------------------------
    // The set loads and is embedded byte for byte
    // ---------------------------------------------------------------------------------------------------------------

    [Fact]
    public void The_v1_schema_set_loads_with_one_file_per_loader_file_plus_common()
    {
        JsonSchemaSubset set = BundledSchemas.V1();
        var expected = CaseFileSpec.V1Files.Select(f => f.SchemaFile).Append("v1-common.schema.json").OrderBy(f => f, StringComparer.Ordinal).ToList();
        Assert.Equal(expected, set.Files);
        // The same set loads from disk (the files are well formed under the subset, not only the embedded copies).
        Assert.Equal(expected, JsonSchemaSubset.LoadDirectory(V1Dir).Files);
    }

    [Theory]
    [InlineData("v1", BundledSchemas.V1Prefix)]
    [InlineData("v2", BundledSchemas.V2Prefix)]
    public void Embedded_schema_bytes_equal_the_files_on_disk(string folder, string prefix)
    {
        string dir = Path.Combine(SchemasDir, folder);
        var onDisk = Directory.EnumerateFiles(dir, "*" + JsonSchemaSubset.SchemaFileSuffix, SearchOption.TopDirectoryOnly)
            .Select(p => Path.GetFileName(p)).OrderBy(f => f, StringComparer.Ordinal).ToList();
        var embedded = typeof(BundledSchemas).Assembly.GetManifestResourceNames()
            .Where(n => n.StartsWith(prefix, StringComparison.Ordinal)).Select(n => n[prefix.Length..])
            .OrderBy(f => f, StringComparer.Ordinal).ToList();
        Assert.Equal(onDisk, embedded);
        foreach (string f in onDisk)
            Assert.True(File.ReadAllBytes(Path.Combine(dir, f)).AsSpan().SequenceEqual(BundledSchemas.Bytes(prefix + f)), $"{folder}/{f}: embedded bytes differ from the file");
    }

    [Fact]
    public void The_embedded_v2_set_equals_the_on_disk_v2_set()
        => Assert.Equal(V2TestSupport.RealSchemas().Files, BundledSchemas.V2().Files);

    // ---------------------------------------------------------------------------------------------------------------
    // Drift guards: schema <-> DTO
    // ---------------------------------------------------------------------------------------------------------------

    [Fact]
    public void The_file_list_equals_the_projectors_loader_files()
        => Assert.Equal(V1Projector.LoaderFiles, CaseFileSpec.V1Files.Select(f => f.FileName));

    [Fact]
    public void The_DTO_set_equals_the_projectors_handled_members()
    {
        var specTypes = AllDefs().Select(d => d.Dto).OrderBy(t => t.Name, StringComparer.Ordinal).ToList();
        var handled = V1Projector.HandledMembers.Keys.OrderBy(t => t.Name, StringComparer.Ordinal).ToList();
        Assert.Equal(handled, specTypes);
        Assert.Equal(14, specTypes.Count);
    }

    [Fact]
    public void Every_DTO_member_is_a_schema_property_and_every_property_a_DTO_member()
    {
        foreach ((Type dto, string file, string def) in AllDefs())
        {
            JsonElement props = SchemaFile(file).GetProperty("$defs").GetProperty(def).GetProperty("properties");
            var schemaNames = props.EnumerateObject().Select(p => p.Name).OrderBy(n => n, StringComparer.Ordinal).ToList();
            var dtoNames = dto.GetProperties(BindingFlags.Public | BindingFlags.Instance)
                .Select(p => JsonNamingPolicy.CamelCase.ConvertName(p.Name)).OrderBy(n => n, StringComparer.Ordinal).ToList();
            Assert.True(dtoNames.SequenceEqual(schemaNames, StringComparer.Ordinal),
                $"{file}#/$defs/{def} properties [{string.Join(", ", schemaNames)}] differ from {dto.Name} members [{string.Join(", ", dtoNames)}]");
        }
    }

    [Fact]
    public void Every_schema_property_has_the_type_of_its_DTO_member()
    {
        var defOf = AllDefs().ToDictionary(d => d.Dto, d => (d.File, d.Def));
        foreach ((Type dto, string file, string def) in AllDefs())
        {
            JsonElement record = SchemaFile(file).GetProperty("$defs").GetProperty(def);
            Assert.Equal("object", record.GetProperty("type").GetString());
            Assert.False(record.GetProperty("additionalProperties").GetBoolean());   // unknown members refused, as the loader (R268)
            foreach (PropertyInfo p in dto.GetProperties(BindingFlags.Public | BindingFlags.Instance))
            {
                string name = JsonNamingPolicy.CamelCase.ConvertName(p.Name);
                string where = $"{file}#/$defs/{def}/properties/{name} ({dto.Name}.{p.Name})";
                JsonElement prop = record.GetProperty("properties").GetProperty(name);
                Assert.True(prop.TryGetProperty("title", out _), $"{where}: no title (the editor's field label)");
                Assert.True(prop.TryGetProperty("description", out _), $"{where}: no description (the editor's help line)");
                Type t = Nullable.GetUnderlyingType(p.PropertyType) ?? p.PropertyType;

                // Moved 2026-10 (N101, authorised by Mark's 2026-10-02 best-judgement ruling and the coordinator's approval
                // of the N101 rule): the three generator limits the loader reads through InfinityTolerantDoubleConverter
                // (qMinMvar, qMaxMvar, pMaxMw: MATPOWER's unlimited Inf / -Inf, written by convert since F24) used to be
                // pinned to "type": "number", which encoded the defect itself: the schema refused a case the loader
                // accepts, so the SchemaEditor save gate refused a converted MATPOWER case. Before: Assert.Equal("number",
                // EffectiveType(...)) for those three members as for every double. After: those three must be exactly
                // anyOf [ {type number}, {enum [one infinity string]} ], the string being the unlimited direction the
                // loader keeps ("-Infinity" for qMinMvar, "Infinity" for qMaxMvar and pMaxMw). Every other double is
                // still pinned to "number" exactly as before.
                if (t == typeof(double) && p.GetCustomAttribute<System.Text.Json.Serialization.JsonConverterAttribute>()?.ConverterType?.Name == "InfinityTolerantDoubleConverter")
                {
                    Assert.False(prop.TryGetProperty("type", out _), $"{where}: an unlimited-capable limit takes anyOf, not a bare type");
                    JsonElement[] alts = prop.GetProperty("anyOf").EnumerateArray().ToArray();
                    Assert.Equal(2, alts.Length);
                    Assert.Equal("number", alts[0].GetProperty("type").GetString());
                    string expected = name == "qMinMvar" ? "-Infinity" : "Infinity";
                    Assert.Equal(new[] { expected }, alts[1].GetProperty("enum").EnumerateArray().Select(v => v.GetString()).ToArray());
                }
                else if (t == typeof(double)) Assert.Equal("number", EffectiveType(file, prop, where));
                else if (t == typeof(int)) Assert.Equal("integer", EffectiveType(file, prop, where));
                else if (t == typeof(bool)) Assert.Equal("boolean", EffectiveType(file, prop, where));
                else if (t == typeof(string)) Assert.Equal("string", EffectiveType(file, prop, where));
                else if (t.IsGenericType && t.GetGenericTypeDefinition() == typeof(List<>))
                {
                    Assert.Equal("array", prop.GetProperty("type").GetString());
                    Type item = t.GetGenericArguments()[0];
                    Assert.Equal("#/$defs/" + defOf[item].Def, prop.GetProperty("items").GetProperty("$ref").GetString());
                    Assert.Equal(file, defOf[item].File);
                }
                else if (t == typeof(Dictionary<string, double>))
                {
                    Assert.Equal("object", prop.GetProperty("type").GetString());
                    Assert.Equal("number", prop.GetProperty("additionalProperties").GetProperty("type").GetString());
                }
                else if (t.IsGenericType && t.GetGenericTypeDefinition() == typeof(Dictionary<,>) && t.GetGenericArguments()[0] == typeof(string))
                {
                    Type value = t.GetGenericArguments()[1];
                    Assert.Equal("object", prop.GetProperty("type").GetString());
                    Assert.Equal("#/$defs/" + defOf[value].Def, prop.GetProperty("additionalProperties").GetProperty("$ref").GetString());
                }
                else if (defOf.TryGetValue(t, out (string File, string Def) nested))
                {
                    Assert.Equal("#/$defs/" + nested.Def, prop.GetProperty("$ref").GetString());
                    Assert.Equal(file, nested.File);
                }
                else Assert.Fail($"{where}: the drift guard does not know CLR type {t.FullName}; teach it rather than skipping the member.");
            }
        }
    }

    /// <summary>The JSON type a property schema admits: its own 'type', else its $ref target's, else its enum's.</summary>
    private static string EffectiveType(string file, JsonElement prop, string where)
    {
        if (prop.TryGetProperty("type", out JsonElement type))
        {
            Assert.True(type.ValueKind == JsonValueKind.String, $"{where}: 'type' must be a single name (null is refused in v1 schemas)");
            return type.GetString()!;
        }
        if (prop.TryGetProperty("$ref", out JsonElement r))
        {
            string reference = r.GetString()!;
            int hash = reference.IndexOf('#', StringComparison.Ordinal);
            string targetFile = hash == 0 ? file : reference[..hash];
            JsonElement target = SchemaFile(targetFile);
            foreach (string token in reference[(hash + 1)..].Split('/', StringSplitOptions.RemoveEmptyEntries)) target = target.GetProperty(token);
            return EffectiveType(targetFile, target, where);
        }
        if (prop.TryGetProperty("enum", out JsonElement en))
        {
            var kinds = en.EnumerateArray().Select(v => v.ValueKind == JsonValueKind.Number ? (SchemaValidator.IsIntegral(v) ? "integer" : "number") : v.ValueKind == JsonValueKind.String ? "string" : v.ValueKind.ToString()).Distinct().ToList();
            Assert.True(kinds.Count == 1, $"{where}: an enum mixing value kinds");
            return kinds[0];
        }
        Assert.Fail($"{where}: a property schema with no type, $ref or enum admits anything");
        return "";
    }

    [Fact]
    public void Required_members_are_the_identity_members_and_baseKv()
    {
        var expected = new Dictionary<string, string[]>(StringComparer.Ordinal)
        {
            ["bus"] = new[] { "id", "baseKv" },
            ["generator"] = new[] { "bus" },
            ["demand"] = new[] { "bus" },
            ["line"] = new[] { "from", "to" },
            ["transformer"] = new[] { "from", "to" },
            ["oltc"] = new[] { "transformer", "controlsBus" },
            ["protection"] = new[] { "class", "monitorBus" },
            ["zip"] = new[] { "ap", "bp", "cp", "aq", "bq", "cq" },
        };
        foreach ((Type _, string file, string def) in AllDefs())
        {
            JsonElement record = SchemaFile(file).GetProperty("$defs").GetProperty(def);
            string[] actual = record.TryGetProperty("required", out JsonElement req) ? req.EnumerateArray().Select(e => e.GetString()!).ToArray() : Array.Empty<string>();
            Assert.Equal(expected.TryGetValue(def, out string[]? want) ? want : Array.Empty<string>(), actual);
        }
    }

    [Fact]
    public void The_provenance_tag_enum_is_exactly_the_ProvenanceTag_names()
    {
        JsonElement tags = SchemaFile("asset-classes.schema.json").GetProperty("$defs").GetProperty("provenance")
            .GetProperty("properties").GetProperty("tag").GetProperty("enum");
        Assert.Equal(Enum.GetNames<ProvenanceTag>(), tags.EnumerateArray().Select(e => e.GetString()));
    }

    [Fact]
    public void No_v1_schema_uses_null_or_default()
    {
        foreach (string f in Directory.EnumerateFiles(V1Dir, "*.json"))
        {
            string text = File.ReadAllText(f);
            Assert.DoesNotContain("\"null\"", text, StringComparison.Ordinal);
            Assert.DoesNotContain("\"default\"", text, StringComparison.Ordinal);
            Assert.DoesNotContain(text, c => c > 127);   // plain ASCII
        }
    }

    // ---------------------------------------------------------------------------------------------------------------
    // Every bundled case validates; misspelt and miscased keys
    // ---------------------------------------------------------------------------------------------------------------

    [Theory]
    [MemberData(nameof(BundledCases), MemberType = typeof(EditorTestSupport))]
    public void Every_bundled_v1_case_validates_against_the_v1_schemas(string relative)
    {
        string dir = CaseDir(relative);
        int checkedFiles = 0;
        foreach (CaseFileSpec spec in CaseFileSpec.V1Files)
        {
            string path = Path.Combine(dir, spec.FileName);
            if (!File.Exists(path)) continue;
            JsonElement root = JsonSourceText.Load(path, StringComparer.OrdinalIgnoreCase).ToElement();
            IReadOnlyList<SchemaError> errors = BundledSchemas.V1Validate.Validate(root, spec.SchemaFile);
            Assert.True(errors.Count == 0, $"data/{relative}/{spec.FileName}:{Environment.NewLine}{V2TestSupport.Dump(errors.Take(20))}");
            checkedFiles++;
        }
        Assert.True(checkedFiles >= 3, $"data/{relative}: only {checkedFiles} loader files found");
    }

    [Fact]
    public void A_misspelt_key_fails_both_the_loader_and_the_schema()
    {
        using var copy = new TempCopy(CaseDir("uk"));
        string gen = Path.Combine(copy.Dir, "generation.json");
        string text = File.ReadAllText(gen);
        int at = text.IndexOf("\"capacityMva\"", StringComparison.Ordinal);
        File.WriteAllText(gen, text[..at] + "\"capacityMvaa\"" + text[(at + "\"capacityMva\"".Length)..]);

        var ex = Assert.Throws<InvalidDataException>(() => JsonGridLoader.LoadDirectory(copy.Dir, dispatch: false));
        Assert.Contains("generation.json", ex.Message, StringComparison.Ordinal);

        IReadOnlyList<SchemaError> errors = BundledSchemas.V1Validate.Validate(JsonSourceText.Load(gen, StringComparer.OrdinalIgnoreCase).ToElement(), "generation.schema.json");
        Assert.Contains(errors, e => e.Pointer == "/0/capacityMvaa" && e.Message.Contains("unknown member", StringComparison.Ordinal));
    }

    [Fact]
    public void A_miscased_key_loads_but_the_strict_schema_refuses_it()
    {
        // Q1 resolved to the plan's recommendation (strict): the loader is case-insensitive, the schema is not, so the
        // editor reports a non-canonical spelling and refuses to save until it is normalised.
        using var copy = new TempCopy(CaseDir("uk"));
        string gen = Path.Combine(copy.Dir, "generation.json");
        string text = File.ReadAllText(gen);
        int at = text.IndexOf("\"capacityMva\"", StringComparison.Ordinal);
        File.WriteAllText(gen, text[..at] + "\"CapacityMVA\"" + text[(at + "\"capacityMva\"".Length)..]);

        Assert.NotNull(JsonGridLoader.LoadDirectory(copy.Dir, dispatch: false));
        IReadOnlyList<SchemaError> errors = BundledSchemas.V1Validate.Validate(JsonSourceText.Load(gen, StringComparer.OrdinalIgnoreCase).ToElement(), "generation.schema.json");
        Assert.Contains(errors, e => e.Pointer == "/0/CapacityMVA");
    }

    // ---------------------------------------------------------------------------------------------------------------
    // The pointer overload
    // ---------------------------------------------------------------------------------------------------------------

    [Fact]
    public void One_record_validates_against_its_def_by_pointer()
    {
        SchemaValidator v = BundledSchemas.V1Validate;
        Assert.Empty(v.Validate(Element("{ \"name\": \"G\", \"bus\": 1, \"capacityMva\": 10 }"), "generation.schema.json", "#/$defs/generator"));
        Assert.Empty(v.Validate(Element("{ \"bus\": 1 }"), "generation.schema.json", "/$defs/generator"));   // '#' optional

        IReadOnlyList<SchemaError> bad = v.Validate(Element("{ \"capacityMva\": -1, \"role\": \"swing\" }"), "generation.schema.json", "#/$defs/generator");
        Assert.Contains(bad, e => e.Pointer == "" && e.Message.Contains("'bus'", StringComparison.Ordinal));
        Assert.Contains(bad, e => e.Pointer == "/capacityMva");
        Assert.Contains(bad, e => e.Pointer == "/role");

        // A bus id outside the 32-bit range (the DTO is an int) and a non-integral one.
        Assert.Contains(v.Validate(Element("{ \"id\": 2147483648, \"baseKv\": 400 }"), "buses.schema.json", "#/$defs/bus"), e => e.Pointer == "/id");
        Assert.Contains(v.Validate(Element("{ \"id\": 1.5, \"baseKv\": 400 }"), "buses.schema.json", "#/$defs/bus"), e => e.Pointer == "/id");
    }

    [Fact]
    public void The_pointer_overload_refuses_a_pointer_with_no_schema()
    {
        SchemaValidator v = BundledSchemas.V1Validate;
        var ex = Assert.Throws<InvalidDataException>(() => v.Validate(Element("{}"), "generation.schema.json", "#/$defs/nope"));
        Assert.Contains("/$defs/nope", ex.Message, StringComparison.Ordinal);
        Assert.Throws<InvalidDataException>(() => v.Validate(Element("{}"), "nope.schema.json", "#/$defs/generator"));
    }

    [Fact]
    public void The_root_overload_is_unchanged_by_the_pointer_overload()
    {
        // Same document, root versus the root pointer "": identical errors.
        SchemaValidator v = BundledSchemas.V1Validate;
        JsonElement doc = Element("[ { \"bus\": \"x\" } ]");
        Assert.Equal(v.Validate(doc, "generation.schema.json").Select(e => e.ToString()), v.Validate(doc, "generation.schema.json", "").Select(e => e.ToString()));
    }
}
