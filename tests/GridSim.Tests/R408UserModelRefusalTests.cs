using System.Security.Cryptography;
using System.Text;
using GridSim.Core.Dynamics.UserModels;
using Xunit;

namespace GridSim.Tests;

/// <summary>
/// R4-08 worked case 4 and the contract: every refusal names the offending object, a model outside the vocabulary is
/// refused rather than approximated, the document hash and compiled order are provenance, and the interpreter source holds no
/// reflection, dynamic code or I/O.
/// </summary>
public sealed class R408UserModelRefusalTests
{
    /// <summary>A generic two-block skeleton: input u, blocks supplied as JSON, output y from <paramref name="outFrom"/>.</summary>
    private static string Doc(string blocks, string outFrom = "b", string parameters = "", string initSteps = "", string inUnit = "pu", string extraTop = "")
    {
        string parameterBlock = parameters.Length > 0 ? "\"parameters\": [ " + parameters + " ]," : "";
        return $$"""
        {
          "format": "gridsim-model/1", "id": "T", "role": "generic", "source": "test",
          {{extraTop}}
          {{parameterBlock}}
          "ports": { "inputs": [ { "name": "u", "unit": "{{inUnit}}" } ], "outputs": [ { "name": "y", "unit": "pu", "from": "{{outFrom}}" } ] },
          "blocks": [ {{blocks}} ],
          "initialisation": { "inputs": [ { "name": "u0", "unit": "pu" } ], "steps": [ {{initSteps}} ] }
        }
        """;
    }

    private static UserModelException Refused(string json)
        => Assert.Throws<UserModelException>(() => ModelCompiler.Compile(ModelDocument.Parse(json)));

    private static void HasIssue(UserModelException ex, string code, params string[] mustMention)
    {
        ModelIssue i = Assert.Single(ex.Issues, x => x.Code == code);
        foreach (string m in mustMention)
            Assert.True((i.Object + " " + i.Message).Contains(m, StringComparison.Ordinal), $"the {code} issue should name '{m}': {i}");
    }

    [Fact]
    public void An_algebraic_loop_is_refused_naming_the_blocks_and_the_fix()
    {
        UserModelException ex = Refused(Doc("""
            { "id": "a", "type": "sum", "inputs": ["u", "b"], "signs": "++" },
            { "id": "b", "type": "gain", "inputs": ["a"], "params": { "k": 0.5 } }
            """));
        HasIssue(ex, "algebraic-loop", "a", "b", ".prev");
    }

    [Fact]
    public void A_loop_through_a_declared_pre_step_output_is_accepted_and_evaluates_in_the_stated_order()
    {
        // b is a lag; a reads b.prev, so the loop is broken by a declared delay.
        ModelDocument d = ModelDocument.Parse(Doc("""
            { "id": "a", "type": "sum", "inputs": ["u", "b.prev"], "signs": "+-" },
            { "id": "b", "type": "lag", "inputs": ["a"], "params": { "gain": 1.0, "time": 1.0 } }
            """, "b", initSteps: """{ "action": "initialise", "block": "b", "values": [0.0] }"""));
        CompiledModel c = ModelCompiler.Compile(d);
        Assert.Equal(new[] { "a", "b" }, c.Order);
        // Independent derivation: y(n+1) = a_n*(1-e^-dt) + e^-dt*y(n) with a_n = u - y(n) (the pre-step output).
        ModelInstance m = ModelInstance.Create(c);
        m.Initialize(0.0);
        double y = 0.0, dt = 0.1, e = Math.Exp(-dt);
        for (int n = 0; n < 50; n++)
        {
            double expect = (1.0 - e) * (1.0 - y) + e * y;
            double got = m.Step(1.0, dt);
            Assert.True(Math.Abs(expect - got) < 1e-12);
            y = got;
        }
    }

    [Fact]
    public void An_undeclared_port_or_block_reference_is_refused_naming_it_and_the_block()
    {
        UserModelException ex = Refused(Doc("""{ "id": "b", "type": "gain", "inputs": ["nosuch"], "params": { "k": 2.0 } }"""));
        HasIssue(ex, "undeclared-port", "block b", "nosuch");
        UserModelException ex2 = Refused(Doc("""{ "id": "b", "type": "gain", "inputs": ["u"], "params": { "k": "@Missing" } }"""));
        HasIssue(ex2, "undeclared-parameter", "Missing");
        UserModelException ex3 = Refused(Doc("""{ "id": "b", "type": "gain", "inputs": ["u"], "params": { "k": 1.0 } }""", outFrom: "ghost"));
        HasIssue(ex3, "undeclared-port", "output port y", "ghost");
    }

    [Fact]
    public void A_unit_mismatch_is_refused_naming_the_parameter_or_wire()
    {
        // A time constant declared in per-unit.
        UserModelException ex = Refused(Doc(
            """{ "id": "b", "type": "lag", "inputs": ["u"], "params": { "gain": 1.0, "time": "@T" } }""",
            parameters: """{ "name": "T", "unit": "pu", "value": 0.5 }""",
            initSteps: """{ "action": "initialise", "block": "b", "values": [0.0] }"""));
        HasIssue(ex, "unit-mismatch", "block b", "time", "@T");
        // A level (pu) added to a rate (1/s).
        UserModelException ex2 = Refused(Doc("""
            { "id": "i", "type": "integrator", "inputs": ["u"], "params": { "gain": 1.0 } },
            { "id": "b", "type": "sum", "inputs": ["u", "u"], "signs": "++" },
            { "id": "c", "type": "sum", "inputs": ["b", "i"], "signs": "++" }
            """, "c", initSteps: """{ "action": "initialise", "block": "i", "values": [0.0] }"""));
        HasIssue(ex2, "unit-mismatch", "block c");
        // An input port in seconds feeding a per-unit lag limit.
        UserModelException ex3 = Refused(Doc(
            """{ "id": "b", "type": "limit", "inputs": ["u"], "params": { "min": "@L", "max": 1.0 } }""",
            parameters: """{ "name": "L", "unit": "s", "value": -1.0 }"""));
        HasIssue(ex3, "unit-mismatch", "block b", "min");
    }

    [Theory]
    [InlineData("script")]
    [InlineData("fmu")]
    [InlineData("sqrt")]
    [InlineData("callback")]
    public void A_block_outside_the_closed_catalogue_is_refused_by_name_never_approximated(string type)
    {
        UserModelException ex = Refused(Doc($$"""{ "id": "b", "type": "{{type}}", "inputs": ["u"], "params": { } }"""));
        HasIssue(ex, "unknown-block", "block b", type, "refused rather than approximated");
    }

    [Fact]
    public void Non_finite_and_out_of_bounds_parameters_are_refused_naming_the_parameter()
    {
        CompiledModel c = ModelCompiler.Compile(UserModelLibrary.Document("TGOV1"));
        var nan = Assert.Throws<UserModelException>(() => ModelInstance.Create(c, new Dictionary<string, double> { ["R"] = double.NaN }));
        HasIssue(nan, "non-finite", "parameter R");
        var inf = Assert.Throws<UserModelException>(() => ModelInstance.Create(c, new Dictionary<string, double> { ["T1"] = double.PositiveInfinity }));
        HasIssue(inf, "non-finite", "parameter T1");
        var neg = Assert.Throws<UserModelException>(() => ModelInstance.Create(c, new Dictionary<string, double> { ["R"] = -0.05 }));
        HasIssue(neg, "out-of-bounds", "parameter R", "-0.05");
        var zero = Assert.Throws<UserModelException>(() => ModelInstance.Create(c, new Dictionary<string, double> { ["R"] = 0.0 }));
        HasIssue(zero, "out-of-bounds", "parameter R");
        var unknown = Assert.Throws<UserModelException>(() => ModelInstance.Create(c, new Dictionary<string, double> { ["Rr"] = 1.0 }));
        HasIssue(unknown, "unknown-parameter", "Rr");
        // A parameter that allows infinity accepts it (an unlimited valve).
        ModelInstance ok = ModelInstance.Create(c, new Dictionary<string, double> { ["Vmax"] = double.PositiveInfinity });
        Assert.NotNull(ok);
        // Every issue is listed, not only the first.
        var two = Assert.Throws<UserModelException>(() => ModelInstance.Create(c, new Dictionary<string, double> { ["R"] = -1.0, ["T3"] = 0.0 }));
        Assert.Equal(2, two.Issues.Count);
    }

    [Fact]
    public void A_violated_sum_constraint_and_a_block_that_refuses_its_values_are_refused()
    {
        CompiledModel g1 = ModelCompiler.Compile(UserModelLibrary.Document("IEEEG1"));
        var ex = Assert.Throws<UserModelException>(() => ModelInstance.Create(g1, new Dictionary<string, double> { ["K1"] = 0.9 }));
        HasIssue(ex, "constraint", "K1", "sum");
        // A limit with max below min is refused by the block, naming the block.
        CompiledModel t = ModelCompiler.Compile(UserModelLibrary.Document("TGOV1"));
        var lim = Assert.Throws<UserModelException>(() => ModelInstance.Create(t, new Dictionary<string, double> { ["Vmin"] = 2.0, ["Vmax"] = 1.0 }));
        HasIssue(lim, "block-refused", "valve");
    }

    [Fact]
    public void Structural_mistakes_are_refused_with_the_object_named()
    {
        // a stateful block never initialised
        HasIssue(Refused(Doc("""{ "id": "b", "type": "lag", "inputs": ["u"], "params": { "gain": 1.0, "time": 1.0 } }""")), "not-initialised", "block b");
        // .prev on a block with no declared pre-step output
        HasIssue(Refused(Doc("""
            { "id": "g", "type": "gain", "inputs": ["u"], "params": { "k": 1.0 } },
            { "id": "b", "type": "gain", "inputs": ["g.prev"], "params": { "k": 1.0 } }
            """)), "bad-reference", "g.prev");
        // wrong arity
        HasIssue(Refused(Doc("""{ "id": "b", "type": "gain", "inputs": ["u", "u"], "params": { "k": 1.0 } }""")), "bad-arity", "block b");
        // missing and unknown block parameters
        HasIssue(Refused(Doc("""{ "id": "b", "type": "gain", "inputs": ["u"], "params": { } }""")), "missing-param", "block b", "'k'");
        HasIssue(Refused(Doc("""{ "id": "b", "type": "gain", "inputs": ["u"], "params": { "k": 1.0, "zz": 2.0 } }""")), "unknown-param", "block b", "zz");
        // a sum without signs, a non-increasing table, a delay with a parameter step count
        HasIssue(Refused(Doc("""{ "id": "b", "type": "sum", "inputs": ["u", "u"] }""")), "bad-signs", "block b");
        HasIssue(Refused(Doc("""{ "id": "b", "type": "table1d", "inputs": ["u"], "params": { }, "table": { "x": [0, 0], "y": [1, 2], "xUnit": "pu", "yUnit": "pu" } }""")), "bad-table", "block b");
        HasIssue(Refused(Doc("""{ "id": "b", "type": "delay", "inputs": ["u"], "params": { "steps": 2.5 } }""",
            initSteps: """{ "action": "initialise", "block": "b", "values": [0.0] }""")), "bad-param", "block b");
        // duplicate ids
        HasIssue(Refused(Doc("""
            { "id": "b", "type": "gain", "inputs": ["u"], "params": { "k": 1.0 } },
            { "id": "b", "type": "gain", "inputs": ["u"], "params": { "k": 2.0 } }
            """)), "duplicate-name", "block b");
        // a combinational-only initialisation step with state
        HasIssue(Refused(Doc("""{ "id": "b", "type": "gain", "inputs": ["u"], "params": { "k": 1.0 } }""",
            initSteps: """{ "id": "s", "type": "lag", "inputs": ["u0"], "params": { "gain": 1.0, "time": 1.0 } }""")), "not-combinational", "initialisation step s");
        // an initialisation step reading a later step
        HasIssue(Refused(Doc("""{ "id": "b", "type": "gain", "inputs": ["u"], "params": { "k": 1.0 } }""",
            initSteps: """{ "id": "s", "type": "gain", "inputs": ["later"], "params": { "k": 1.0 } }, { "id": "later", "type": "gain", "inputs": ["u0"], "params": { "k": 1.0 } }""")),
            "undeclared-port", "initialisation step s", "later");
    }

    [Fact]
    public void Role_port_conventions_are_enforced()
    {
        string json = Encoding.UTF8.GetString(UserModelLibrary.Bytes("TGOV1"))
            .Replace("\"name\": \"speed\"", "\"name\": \"w\"", StringComparison.Ordinal)
            .Replace("\"inputs\": [\"speed\"]", "\"inputs\": [\"w\"]", StringComparison.Ordinal);
        UserModelException ex = Refused(json);
        Assert.Contains(ex.Issues, i => i.Code == "role-ports" && i.Message.Contains("speed", StringComparison.Ordinal));
    }

    [Fact]
    public void Document_level_mistakes_are_refused_by_json_path()
    {
        string good = Encoding.UTF8.GetString(UserModelLibrary.Bytes("TGOV1"));
        // unknown member
        var unknown = Assert.Throws<UserModelException>(() => ModelDocument.Parse(good.Replace("\"title\"", "\"script\": \"x\", \"title\"", StringComparison.Ordinal)));
        Assert.Contains("'script'", unknown.Message, StringComparison.Ordinal);
        Assert.Contains("$", unknown.Issues[0].Object, StringComparison.Ordinal);
        // duplicate member
        var dup = Assert.Throws<UserModelException>(() => ModelDocument.Parse(good.Replace("\"role\": \"governor\",", "\"role\": \"governor\", \"role\": \"exciter\",", StringComparison.Ordinal)));
        Assert.Contains("appears twice", dup.Message, StringComparison.Ordinal);
        // wrong format
        var fmt = Assert.Throws<UserModelException>(() => ModelDocument.Parse(good.Replace("gridsim-model/1", "gridsim-model/2", StringComparison.Ordinal)));
        Assert.Contains("gridsim-model/2", fmt.Message, StringComparison.Ordinal);
        // no source
        string noSource = System.Text.RegularExpressions.Regex.Replace(good, "\"source\": \"[^\"]*\"", "\"source\": \" \"");
        var src = Assert.Throws<UserModelException>(() => ModelDocument.Parse(noSource));
        Assert.Contains("source", src.Message, StringComparison.Ordinal);
        // not JSON, and a bad unit text
        Assert.Throws<UserModelException>(() => ModelDocument.Parse("{ nope"));
        var unit = Assert.Throws<UserModelException>(() => ModelDocument.Parse(good.Replace("\"unit\": \"s\"", "\"unit\": \"furlong\"", StringComparison.Ordinal)));
        Assert.Contains("furlong", unit.Message, StringComparison.Ordinal);
    }

    // ---------------------------------------------------------------------------------------------------------------
    // Provenance and determinism of the compiled form
    // ---------------------------------------------------------------------------------------------------------------

    [Fact]
    public void The_document_hash_is_the_sha256_of_the_exact_bytes_and_any_edit_or_reorder_changes_it()
    {
        byte[] bytes = UserModelLibrary.Bytes("GAST");
        ModelDocument d = ModelDocument.Parse(bytes);
        Assert.Equal(Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant(), d.Sha256);
        string text = Encoding.UTF8.GetString(bytes);
        Assert.NotEqual(d.Sha256, ModelDocument.Parse(text + "\n").Sha256);          // whitespace is part of the bytes
        // Swap two independent blocks (pref and droop): the declared order, and so the compiled order, changes.
        string[] lines = text.Replace("\r\n", "\n", StringComparison.Ordinal).Split((char)10);
        int ip = Array.FindIndex(lines, l => l.Contains("\"id\": \"pref\"", StringComparison.Ordinal));
        int id = Array.FindIndex(lines, l => l.Contains("\"id\": \"droop\"", StringComparison.Ordinal));
        Assert.Equal(ip + 1, id);
        (lines[ip], lines[id]) = (lines[id], lines[ip]);
        string swapped = string.Join("\n", lines);
        CompiledModel a = ModelCompiler.Compile(d), b = ModelCompiler.Compile(ModelDocument.Parse(swapped));
        Assert.NotEqual(a.OrderDigest, b.OrderDigest);
        Assert.NotEqual(d.Sha256, ModelDocument.Parse(swapped).Sha256);
        // Compiling twice gives the same order and digest.
        CompiledModel a2 = ModelCompiler.Compile(ModelDocument.Parse(bytes));
        Assert.Equal(a.Order, a2.Order);
        Assert.Equal(a.OrderDigest, a2.OrderDigest);
        // Ready blocks are taken in document order, so the compiled order of this model is its declared order.
        Assert.Equal(d.Blocks.Select(x => x.Id), a.Order);
    }

    [Fact]
    public void Every_shipped_model_validates_has_a_source_and_the_library_lists_them_in_a_fixed_order()
    {
        Assert.Equal(new[] { "GAST", "IEEEG1", "IEEET1", "PSS1A", "SEXS", "SEXS_PSS1A", "TGOV1" }, UserModelLibrary.Ids);
        foreach (string id in UserModelLibrary.Ids)
        {
            ModelDocument d = UserModelLibrary.Document(id);
            Assert.Equal(id, d.Id);
            Assert.False(string.IsNullOrWhiteSpace(d.Source), id);
            Assert.Empty(ModelValidator.Validate(d));
            ModelInstance m = UserModelLibrary.Load(id).NewInstance();
            Assert.Equal(d.Parameters.Count + d.Derived.Count, m.ParameterValues.Count);
        }
    }

    [Fact]
    public void The_published_schema_names_exactly_the_catalogue_and_the_documents_top_level_members()
    {
        string path = Path.Combine(CliProcess.Root(), "schemas", "usermodel", "gridsim-model-1.schema.json");
        using var doc = System.Text.Json.JsonDocument.Parse(File.ReadAllText(path));
        System.Text.Json.JsonElement root = doc.RootElement;
        string[] types = root.GetProperty("$defs").GetProperty("block").GetProperty("properties").GetProperty("type").GetProperty("enum")
            .EnumerateArray().Select(e => e.GetString()!).ToArray();
        Assert.Equal(ModelCatalogue.Rows.Select(r => r.Type).OrderBy(t => t, StringComparer.Ordinal), types.OrderBy(t => t, StringComparer.Ordinal));
        var allowed = root.GetProperty("properties").EnumerateObject().Select(p => p.Name).ToHashSet(StringComparer.Ordinal);
        foreach (string id in UserModelLibrary.Ids)
        {
            using var lib = System.Text.Json.JsonDocument.Parse(UserModelLibrary.Bytes(id));
            foreach (System.Text.Json.JsonProperty p in lib.RootElement.EnumerateObject())
                Assert.Contains(p.Name, allowed);
        }
    }

    [Fact]
    public void The_interpreter_source_holds_no_reflection_dynamic_code_or_io()
    {
        string dir = Path.Combine(CliProcess.Root(), "src", "GridSim.Core", "Dynamics", "UserModels");
        string[] forbidden =
        {
            "System.Reflection", "Reflection.Emit", "Activator.", "DynamicMethod", "DynamicInvoke", "MethodInfo", "Expression.Compile",
            "CSharpScript", "Assembly.Load", "Type.GetType", ".Invoke(", "File.", "Directory.", "StreamReader", "Process.", "HttpClient",
            "Environment.", "Random", "DateTime.", "Stopwatch", "Parallel.", "Task.", "Thread",
        };
        foreach (string file in Directory.EnumerateFiles(dir, "*.cs").OrderBy(f => f, StringComparer.Ordinal))
        {
            string[] lines = File.ReadAllLines(file);
            for (int n = 0; n < lines.Length; n++)
            {
                string line = lines[n];
                string code = line.TrimStart();
                if (code.StartsWith("///", StringComparison.Ordinal) || code.StartsWith("//", StringComparison.Ordinal)) continue;
                foreach (string f in forbidden)
                    if (line.Contains(f, StringComparison.Ordinal))
                        Assert.Fail($"{Path.GetFileName(file)}:{n + 1} uses '{f}': {line.Trim()}");
                // System.Math transcendentals only (DetMath.Exp is the allowed spelling, so match a bare "Math.").
                if (System.Text.RegularExpressions.Regex.IsMatch(line, @"(?<![A-Za-z])Math\.(Exp|Log|Log10|Pow|Sin|Cos|Tan|Atan2|Sinh|Cosh)\("))
                    Assert.Fail($"{Path.GetFileName(file)}:{n + 1} calls a System.Math transcendental: {line.Trim()}");
            }
        }
    }
}
