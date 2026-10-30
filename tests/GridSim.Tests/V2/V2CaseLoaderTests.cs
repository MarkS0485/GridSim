using System.Collections;
using System.Reflection;
using System.Text;
using System.Text.Json;
using GridSim.Core.Assets;
using GridSim.Core.Diagnostics;
using GridSim.Core.IO.Json;
using GridSim.Core.IO.Json.Schema;
using GridSim.Core.Model;
using GridSim.Core.PowerFlow;
using Xunit;
using static GridSim.Tests.V2.V2TestSupport;

namespace GridSim.Tests.V2;

/// <summary>
/// N065 (October 2026), JSON v2 Phase 2: the loader reads a <c>gridsim-case/2</c> case (<see cref="V2CaseLoader"/>).
/// <list type="bullet">
/// <item>Bit identity: every bundled JSON v1 case, projected to v2 by the test projector and loaded by the v2 loader,
/// gives a model equal to the v1 load in every field (doubles by their bits, compared by reflection over every field,
/// public or private, so nothing can be skipped by omission) and a Newton solve equal to the bit.</item>
/// <item>Imports, overrides, bindings and kind-spec enforcement on hand-written fixtures.</item>
/// <item>Fail loud: each rule the schema cannot express, and each piece of v2 content the model cannot carry yet, is
/// refused with a message naming it; every problem is listed.</item>
/// </list>
/// </summary>
public sealed partial class V2CaseLoaderTests
{
    /// <summary>The bundled v1 cases, as paths relative to data/ (the projection tests' list).</summary>
    public static TheoryData<string> BundledCases() => V1ToV2ProjectionTests.BundledCases();

    private static string DataRoot => Path.Combine(RepoRoot(), "data");

    // ------------------------------------------------------------------------------------------------------------
    // Bit identity on every bundled case
    // ------------------------------------------------------------------------------------------------------------

    [Theory]
    [MemberData(nameof(BundledCases))]
    public void A_bundled_case_loaded_through_its_v2_projection_gives_the_v1_model_and_solve_bit_for_bit(string relativeDir)
    {
        string dir = Path.Combine(DataRoot, relativeDir);
        using var temp = new TempFolder();
        string manifest = WriteProjection(dir, temp.Path);

        GridModel v1 = JsonGridLoader.LoadDirectory(dir);
        V2CaseLoadResult loaded = V2CaseLoader.LoadWithNotes(manifest, SchemasDir, DataRoot);
        GridModel v2 = loaded.Model;
        Assert.Empty(loaded.Notes);

        List<string> diffs = DeepDifferences(v1, v2);
        Assert.True(diffs.Count == 0, $"data/{relativeDir}: the v2 model differs from the v1 model:{Environment.NewLine}{string.Join(Environment.NewLine, diffs)}");

        // And the solve: the default Newton engine with its default options on each model.
        PowerFlowResult a = SolverSelector.CreateNewton(v1).Solve(v1);
        PowerFlowResult b = SolverSelector.CreateNewton(v2).Solve(v2);
        AssertSameSolve(a, b, relativeDir);
    }

    [Fact]
    public void Splitting_the_network_over_two_files_gives_the_same_model()
    {
        // The case's network is the union of its network files, in the order listed.
        string dir = Path.Combine(DataRoot, "uk");
        using var temp = new TempFolder();
        V2Projection p = V1Projector.Project(V1Projector.ReadV1(dir));
        using JsonDocument net = JsonDocument.Parse(p.NetworkJson, JsonSchemaSubset.ReaderOptions);
        var first = new[] { "buses", "generators" };
        File.WriteAllText(Path.Combine(temp.Path, "a.network.json"), Subset(net.RootElement, m => first.Contains(m)));
        File.WriteAllText(Path.Combine(temp.Path, "b.network.json"), Subset(net.RootElement, m => !first.Contains(m)));
        string manifestText = Encoding.UTF8.GetString(p.CaseJson).Replace("\"network.json\"", "\"a.network.json\", \"b.network.json\"", StringComparison.Ordinal);
        string manifest = Path.Combine(temp.Path, "case.json");
        File.WriteAllText(manifest, manifestText);

        List<string> diffs = DeepDifferences(JsonGridLoader.LoadDirectory(dir), V2CaseLoader.Load(manifest, SchemasDir));
        Assert.True(diffs.Count == 0, string.Join(Environment.NewLine, diffs));
    }

    // ------------------------------------------------------------------------------------------------------------
    // Imports, overrides, bindings
    // ------------------------------------------------------------------------------------------------------------

    private const string Library = """
        {
          "schema": "gridsim-library/2",
          "templates": [
            { "id": "gridsim.test.gfl", "rev": 1, "kind": "gridFollowingInverter", "status": "active", "title": "GFL r1",
              "params": { "deadbandHz": 0.015, "droopGainMwPerHzPerMw": 2.0 },
              "provenance": { "_default": { "tag": "Synthetic", "note": "test fixture" } } },
            { "id": "gridsim.test.gfl", "rev": 2, "kind": "gridFollowingInverter", "status": "active", "title": "GFL r2",
              "description": "grid-following",
              "params": { "deadbandHz": 0.015, "droopGainMwPerHzPerMw": 3.0 },
              "provenance": { "_default": { "tag": "Synthetic", "note": "test fixture" } } },
            { "id": "gridsim.test.site", "rev": 1, "kind": "site", "status": "active", "title": "A site",
              "params": {}, "provenance": { "_default": { "tag": "Synthetic", "note": "test fixture" } } },
            { "id": "gridsim.test.empty", "rev": 1, "kind": "site", "status": "placeholder", "title": "Empty",
              "params": { "generation": null, "demand": null },
              "provenance": { "_default": { "tag": "CuratedPlaceholder", "note": "nothing [VERIFY]" } } }
          ]
        }
        """;

    private const string Network = """
        {
          "schema": "gridsim-network/2",
          "buses": [
            { "id": 1, "name": "North", "baseKv": 400, "type": "slack" },
            { "id": 2, "name": "South", "baseKv": 400 }
          ],
          "generators": [
            { "name": "Slack", "bus": 1, "type": "ccgt", "capacityMva": 1000, "outputMw": 0, "role": "slack" },
            { "name": "Wind", "bus": 2, "type": "wind-onshore", "capacityMva": 200, "outputMw": 100, "role": "pq" }
          ],
          "demands": [ { "name": "Load", "bus": 2, "pMw": 300, "qMvar": 50 } ],
          "lines": [ { "name": "L1-2", "from": 1, "to": 2, "rPu": 0.002, "xPu": 0.02, "ratingMva": 1000 } ]
        }
        """;

    private const string Manifest = """
        {
          "schema": "gridsim-case/2",
          "name": "Import test",
          "system": { "baseMva": 100, "frequencyHz": 50 },
          "network": ["network.json"],
          "imports": [ { "library": "lib/test.library.json", "templates": ["gridsim.test.gfl", "gridsim.test.gfl@1"] } ],
          "bindings": [ { "matchType": "wind*", "template": "gridsim.test.gfl@2" } ],
          "overrides": [
            { "template": "gridsim.test.gfl@2", "params": { "droopGainMwPerHzPerMw": 2.5 }, "reason": "site study", "shadow": "explicit" }
          ]
        }
        """;

    /// <summary>A case folder and a data root with the fixture library, each in its own temp folder.</summary>
    private sealed class Fixture : IDisposable
    {
        private readonly TempFolder _case = new(), _data = new();
        public string CaseDir => _case.Path;
        public string DataRoot => _data.Path;
        public string ManifestPath => System.IO.Path.Combine(CaseDir, "case.json");

        public Fixture(string manifest = Manifest, string network = Network, string library = Library)
        {
            Directory.CreateDirectory(System.IO.Path.Combine(DataRoot, "lib"));
            File.WriteAllText(System.IO.Path.Combine(DataRoot, "lib", "test.library.json"), library);
            File.WriteAllText(System.IO.Path.Combine(CaseDir, "network.json"), network);
            File.WriteAllText(ManifestPath, manifest);
        }

        public V2CaseLoadResult Load() => V2CaseLoader.LoadWithNotes(ManifestPath, SchemasDir, DataRoot);

        public string Refusal() => Assert.Throws<InvalidDataException>(() => Load()).Message;

        public void Dispose() { _case.Dispose(); _data.Dispose(); }
    }

    [Fact]
    public void An_imported_template_bound_by_a_rule_carries_the_override_and_its_provenance()
    {
        using var f = new Fixture();
        V2CaseLoadResult r = f.Load();
        AssetDynamicsModel dyn = r.Model.AssetDynamics!;

        // Both imported revisions are asset classes, named by id@rev; the unpinned import resolved to the highest.
        Assert.Equal(new[] { "gridsim.test.gfl@2", "gridsim.test.gfl@1" }, dyn.Library.Classes.Select(c => c.Name).ToArray());
        Assert.Contains(r.Notes, n => n.Contains("import 'gridsim.test.gfl' from 'lib/test.library.json' resolved to gridsim.test.gfl@2", StringComparison.Ordinal));

        // The binding rule sends the wind unit to r2; the override replaced its droop, the deadband is the library's.
        Assert.Equal("gridsim.test.gfl@2", dyn.Bindings.Resolve(null, "wind-onshore"));
        AssetClass r2 = dyn.Library.Get("gridsim.test.gfl@2");
        Assert.Equal(AssetKind.GridFollowingInverter, r2.Kind);
        Assert.Equal("grid-following", r2.Mode);
        Assert.Equal(2.5, r2.Params["droopGainMwPerHzPerMw"]);
        Assert.Equal(0.015, r2.Params["deadbandHz"]);
        Assert.Equal(2, r2.Params.Count);   // the library's two parameters, no more

        // The overridden value's source is not stated, so it is a curated placeholder quoting the reason.
        AssetClassProvenance p = r2.ProvenanceFor("droopGainMwPerHzPerMw");
        Assert.Equal(ProvenanceTag.CuratedPlaceholder, p.Tag);
        Assert.Equal("[VERIFY] set by an override in case 'Import test': site study", p.Note);
        Assert.Equal(ProvenanceTag.Synthetic, r2.ProvenanceFor("deadbandHz").Tag);
        // r1 is untouched by the override.
        Assert.Equal(2.0, dyn.Library.Get("gridsim.test.gfl@1").Params["droopGainMwPerHzPerMw"]);
    }

    [Fact]
    public void An_override_with_its_own_provenance_keeps_it()
    {
        string manifest = Manifest.Replace("\"shadow\": \"explicit\" }", "\"shadow\": \"explicit\", \"provenance\": { \"tag\": \"Recorded\", \"note\": \"site test 2026\" } }", StringComparison.Ordinal);
        using var f = new Fixture(manifest);
        AssetClassProvenance p = f.Load().Model.AssetDynamics!.Library.Get("gridsim.test.gfl@2").ProvenanceFor("droopGainMwPerHzPerMw");
        Assert.Equal(ProvenanceTag.Recorded, p.Tag);
        Assert.Equal("site test 2026", p.Note);
    }

    [Fact]
    public void A_local_template_keeps_the_v1_class_name_and_an_element_template_wins_over_a_binding()
    {
        string manifest = """
            { "schema": "gridsim-case/2", "name": "Local", "system": { "baseMva": 100, "frequencyHz": 50 }, "network": ["network.json"],
              "localTemplates": [
                { "id": "gridsim.local.gfl-own", "rev": 1, "kind": "gridFollowingInverter", "status": "active", "title": "own",
                  "params": { "deadbandHz": 0.02 }, "provenance": { "_default": { "tag": "Synthetic", "note": "local" } } },
                { "id": "gridsim.local.gfl-own", "rev": 2, "kind": "gridFollowingInverter", "status": "active", "title": "own r2",
                  "params": { "deadbandHz": 0.03 }, "provenance": { "_default": { "tag": "Synthetic", "note": "local" } } } ],
              "bindings": [ { "matchType": "wind*", "template": "gridsim.local.gfl-own@2" } ] }
            """;
        string network = Network.Replace("\"role\": \"pq\" }", "\"role\": \"pq\", \"template\": \"gridsim.local.gfl-own@1\" }", StringComparison.Ordinal);
        using var f = new Fixture(manifest, network);
        GridModel m = f.Load().Model;
        Assert.Equal(new[] { "gfl-own", "gridsim.local.gfl-own@2" }, m.AssetDynamics!.Library.Classes.Select(c => c.Name).ToArray());
        Generator wind = m.Generators.Single(g => g.Name == "Wind");
        Assert.Equal("gfl-own", wind.DynamicsClass);
        Assert.Equal("gfl-own", m.AssetDynamics.Bindings.Resolve(wind.DynamicsClass, wind.Type));
    }

    [Fact]
    public void The_infinity_strings_load_as_in_v1()
    {
        string network = Network.Replace("\"role\": \"slack\" }", "\"role\": \"slack\", \"qMinMvar\": \"-Infinity\", \"qMaxMvar\": \"Infinity\", \"pMaxMw\": \"Infinity\" }", StringComparison.Ordinal);
        using var f = new Fixture(network: network);
        Generator slack = f.Load().Model.Generators.Single(g => g.Name == "Slack");
        Assert.Equal(double.NegativeInfinity, slack.Qmin);
        Assert.Equal(double.PositiveInfinity, slack.Qmax);
        Assert.Equal(double.PositiveInfinity, slack.Pmax);
    }

    // ------------------------------------------------------------------------------------------------------------
    // Fail loud
    // ------------------------------------------------------------------------------------------------------------

    [Fact]
    public void A_bus_without_baseKv_is_refused_by_the_v2_schema()
    {
        using var f = new Fixture(network: Network.Replace("{ \"id\": 2, \"name\": \"South\", \"baseKv\": 400 }", "{ \"id\": 2, \"name\": \"South\" }", StringComparison.Ordinal));
        string msg = f.Refusal();
        Assert.Contains("schema checks", msg, StringComparison.Ordinal);
        Assert.Contains("/buses/1", msg, StringComparison.Ordinal);
        Assert.Contains("baseKv", msg, StringComparison.Ordinal);
    }

    [Fact]
    public void The_network_schema_requires_baseKv()
    {
        IReadOnlyList<SchemaError> errors = Validate(RealSchemas(), "network.schema.json", """{ "schema": "gridsim-network/2", "buses": [ { "id": 1 } ] }""");
        Assert.Contains(errors, e => e.Pointer == "/buses/0" && e.Message.Contains("baseKv", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("\"droopGainMwPerHzPerMw\": 2.5", "\"noSuchKey\": 2.5", "unknown parameter 'noSuchKey'")]
    [InlineData("\"template\": \"gridsim.test.gfl@2\", \"params\"", "\"template\": \"gridsim.test.gfl@9\", \"params\"", "gridsim.test.gfl@9 is neither imported nor a local template")]
    [InlineData("\"droopGainMwPerHzPerMw\": 2.5", "\"droopGainMwPerHzPerMw\": -1", "below the minimum")]
    public void A_bad_override_is_refused(string from, string to, string expected)
    {
        using var f = new Fixture(Manifest.Replace(from, to, StringComparison.Ordinal));
        Assert.Contains(expected, f.Refusal(), StringComparison.Ordinal);
    }

    [Fact]
    public void A_parameter_overridden_twice_is_refused()
    {
        string manifest = Manifest.Replace("\"shadow\": \"explicit\" }", "\"shadow\": \"explicit\" },\n { \"template\": \"gridsim.test.gfl@2\", \"params\": { \"droopGainMwPerHzPerMw\": 2.6 }, \"reason\": \"again\", \"shadow\": \"explicit\" }", StringComparison.Ordinal);
        using var f = new Fixture(manifest);
        Assert.Contains("is overridden more than once", f.Refusal(), StringComparison.Ordinal);
    }

    [Fact]
    public void A_local_template_may_not_shadow_an_imported_id()
    {
        string manifest = Manifest.Replace("\"bindings\"", """
            "localTemplates": [ { "id": "gridsim.test.gfl", "rev": 7, "kind": "gridFollowingInverter", "status": "active", "title": "shadow",
              "params": {}, "provenance": { "_default": { "tag": "Synthetic", "note": "x" } } } ],
            "bindings"
            """, StringComparison.Ordinal);
        using var f = new Fixture(manifest);
        Assert.Contains("may not shadow an import", f.Refusal(), StringComparison.Ordinal);
    }

    [Fact]
    public void A_local_template_is_checked_against_its_kind()
    {
        string manifest = Manifest.Replace("\"bindings\"", """
            "localTemplates": [ { "id": "gridsim.local.bad", "rev": 1, "kind": "gridFollowingInverter", "status": "active", "title": "bad",
              "params": { "deadbandHz": 99 }, "provenance": { "_default": { "tag": "Synthetic", "note": "x" } } } ],
            "bindings"
            """, StringComparison.Ordinal);
        using var f = new Fixture(manifest);
        string msg = f.Refusal();
        Assert.Contains("local templates", msg, StringComparison.Ordinal);
        Assert.Contains("above the maximum", msg, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("gridsim.test.empty@1", "is a placeholder")]
    [InlineData("gridsim.test.site@1", "is kind 'site'")]
    public void A_binding_to_a_template_no_asset_class_can_be_is_refused(string target, string expected)
    {
        string manifest = Manifest
            .Replace("[\"gridsim.test.gfl\", \"gridsim.test.gfl@1\"]", "[\"gridsim.*\"]", StringComparison.Ordinal)
            .Replace("\"template\": \"gridsim.test.gfl@2\" }", $"\"template\": \"{target}\" }}", StringComparison.Ordinal);
        using var f = new Fixture(manifest);
        Assert.Contains(expected, f.Refusal(), StringComparison.Ordinal);
    }

    [Theory]
    // Moved 2026-10 (LF2 N234, Ruling R1: JSON v2 phase P5 opened for fault sequence data only, ruled by main under
    // Mark's delegation): a generator's subtransientXdPu was listed here as refused ("subtransientXdPu is valid JSON
    // v2"). It now reaches Generator.SubtransientXdPu. Before: InlineData("\"role\": \"slack\" }", "\"role\": \"slack\",
    // \"subtransientXdPu\": 0.2 }", "subtransientXdPu is valid JSON v2"). After: removed from this theory; the load is
    // asserted by V2CaseLoaderTests.N234_* (V2CaseLoaderTests.N234.cs).
    [InlineData("\"ratingMva\": 1000 }", "\"ratingMva\": 1000, \"type\": \"gridsim.linetype.x@1\" }", "a line type is valid JSON v2")]
    [InlineData("\"ratingMva\": 1000 }", "\"ratingMva\": 1000, \"inService\": false }", "an out-of-service line")]
    public void Network_content_the_model_does_not_carry_yet_is_refused_not_dropped(string from, string to, string expected)
    {
        using var f = new Fixture(network: Network.Replace(from, to, StringComparison.Ordinal));
        string msg = f.Refusal();
        Assert.Contains(expected, msg, StringComparison.Ordinal);
        Assert.Contains("remove it to load this case today", msg, StringComparison.Ordinal);
    }

    [Fact]
    public void Transformer_zero_sequence_data_is_refused_and_every_problem_is_listed()
    {
        // Moved 2026-10 (LF2 N234, Ruling R1: JSON v2 phase P5 opened for fault sequence data only, ruled by main under
        // Mark's delegation). Before: this transformer's windings and x0Pu were refused with "2 problem(s)", naming
        // "winding connections" and "zero-sequence impedance". After: the same transformer LOADS and its data reaches
        // the model (asserted below); "every problem is listed" for sequence data that is inconsistent is asserted by
        // V2CaseLoaderTests.N234_inconsistent_sequence_data_is_refused_and_every_problem_is_listed.
        string network = Network.Replace("\"ratingMva\": 1000 } ]", """
            "ratingMva": 1000 } ],
              "transformers": [ { "name": "T", "from": 1, "to": 2, "rPu": 0.001, "xPu": 0.05,
                "windings": { "from": "WyeGrounded", "to": "Delta" }, "x0Pu": 0.04 } ]
            """, StringComparison.Ordinal);
        using var f = new Fixture(network: network);
        Branch t = f.Load().Model.Branches.Single(b => b.Name == "T");
        Assert.Equal(WindingConnection.WyeGrounded, t.FromWinding);
        Assert.Equal(WindingConnection.Delta, t.ToWinding);
        Assert.Equal(0.04, t.X0);
        Assert.Equal(0.0, t.R0);
    }

    [Fact]
    public void An_import_pattern_that_matches_nothing_is_refused()
    {
        using var f = new Fixture(Manifest.Replace("\"gridsim.test.gfl@1\"]", "\"gridsim.nothing.*\"]", StringComparison.Ordinal));
        Assert.Contains("pattern 'gridsim.nothing.*' matches no template", f.Refusal(), StringComparison.Ordinal);
    }

    [Fact]
    public void Imports_without_a_data_root_are_refused()
    {
        using var f = new Fixture();
        string msg = Assert.Throws<InvalidDataException>(() => V2CaseLoader.Load(f.ManifestPath, SchemasDir)).Message;
        Assert.Contains("no data root was given", msg, StringComparison.Ordinal);
    }

    [Fact]
    public void A_library_path_outside_the_data_root_is_refused()
    {
        using var f = new Fixture(Manifest.Replace("\"lib/test.library.json\"", "\"../outside.json\"", StringComparison.Ordinal));
        Assert.Contains("lies outside the data root", f.Refusal(), StringComparison.Ordinal);
    }

    [Fact]
    public void A_network_file_outside_the_case_folder_is_refused()
    {
        using var f = new Fixture(Manifest.Replace("[\"network.json\"]", "[\"../network.json\"]", StringComparison.Ordinal));
        string msg = f.Refusal();
        Assert.True(msg.Contains("lies outside the case folder", StringComparison.Ordinal) || msg.Contains("does not exist", StringComparison.Ordinal), msg);
    }

    [Fact]
    public void A_profile_is_refused_as_a_later_phase()
    {
        using var f = new Fixture(Manifest.Replace("\"network\":", "\"solverProfile\": \"gridsim.solver.default@1\",\n  \"network\":", StringComparison.Ordinal));
        // Moved 2026-10 (DOC-17 D-DOC-162, Ruling R1, Mark delegated 2026-10-04): the refusal quoted plan wording a user
        // cannot act on. Before: "... is a later, Mark-gated phase of JSON v2 (P6); remove it ...". After: "GridSim does
        // not yet bind a solverProfile from a case (the built-in settings apply); remove it ...". Still a refusal
        // naming the member; no plan wording may come back.
        string msg = f.Refusal();
        Assert.Contains("/solverProfile: GridSim does not yet bind a solverProfile from a case", msg, StringComparison.Ordinal);
        Assert.DoesNotContain("Mark-gated", msg, StringComparison.Ordinal);
    }

    [Fact]
    public void A_file_that_is_not_a_case_manifest_is_refused_by_name()
    {
        using var f = new Fixture();
        string msg = Assert.Throws<InvalidDataException>(() => V2CaseLoader.Load(Path.Combine(f.CaseDir, "network.json"), SchemasDir, f.DataRoot)).Message;
        Assert.Contains("not a JSON v2 case manifest: its \"schema\" is 'gridsim-network/2'", msg, StringComparison.Ordinal);
    }

    [Fact]
    public void Duplicate_bus_ids_across_network_files_are_refused()
    {
        using var f = new Fixture(network: Network.Replace("{ \"id\": 2, \"name\": \"South\"", "{ \"id\": 1, \"name\": \"South\"", StringComparison.Ordinal));
        Assert.Contains("bus id 1 is used by 2 buses", f.Refusal(), StringComparison.Ordinal);
    }

    [Fact]
    public void A_failing_builder_check_names_the_v1_file_equivalents()
    {
        // The shared builder refuses a demand on an unknown bus; the v2 loader says what the v1 file names mean.
        using var f = new Fixture(network: Network.Replace("\"name\": \"Load\", \"bus\": 2", "\"name\": \"Load\", \"bus\": 9", StringComparison.Ordinal));
        string msg = f.Refusal();
        Assert.Contains("Demand 'Load' references unknown bus 9", msg, StringComparison.Ordinal);
        Assert.Contains("demand.json = demands", msg, StringComparison.Ordinal);
    }

    // ------------------------------------------------------------------------------------------------------------
    // CLI
    // ------------------------------------------------------------------------------------------------------------

    [Fact]
    public void The_cli_solves_a_v2_manifest_exactly_as_the_v1_folder()
    {
        // pf on the uk folder and on its v2 projection print the same report (the case name is the system name).
        using var temp = new TempFolder();
        string manifest = WriteProjection(Path.Combine(DataRoot, "uk"), temp.Path);
        CliProcess.Result v1 = CliProcess.RunBuilt("pf", Path.Combine(DataRoot, "uk"));
        CliProcess.Result v2 = CliProcess.RunBuilt("pf", manifest);
        Assert.Equal(0, v1.ExitCode);
        Assert.Equal(0, v2.ExitCode);
        Assert.Equal(v1.StdOut, v2.StdOut);
    }

    [Fact]
    public void The_cli_refuses_a_bad_v2_manifest_with_exit_1()
    {
        using var temp = new TempFolder();
        string manifest = Path.Combine(temp.Path, "case.json");
        File.WriteAllText(Path.Combine(temp.Path, "network.json"), """{ "schema": "gridsim-network/2", "buses": [ { "id": 1, "type": "slack" } ] }""");
        File.WriteAllText(manifest, """{ "schema": "gridsim-case/2", "name": "bad", "system": { "baseMva": 100, "frequencyHz": 50 }, "network": ["network.json"] }""");
        CliProcess.Result r = CliProcess.RunBuilt("pf", manifest);
        Assert.Equal(1, r.ExitCode);
        Assert.Contains("baseKv", r.StdErr, StringComparison.Ordinal);
    }

    // ------------------------------------------------------------------------------------------------------------
    // Helpers
    // ------------------------------------------------------------------------------------------------------------

    /// <summary>Project a v1 case folder to v2 and write the manifest (case.json) and network file into a folder.</summary>
    private static string WriteProjection(string v1Dir, string into)
    {
        V2Projection p = V1Projector.Project(V1Projector.ReadV1(v1Dir));
        string manifest = Path.Combine(into, "case.json");
        File.WriteAllBytes(manifest, p.CaseJson);
        File.WriteAllBytes(Path.Combine(into, V1Projector.NetworkFileName), p.NetworkJson);
        return manifest;
    }

    /// <summary>A network document holding only the members <paramref name="keep"/> picks (plus the tag).</summary>
    private static string Subset(JsonElement network, Func<string, bool> keep)
    {
        using var ms = new MemoryStream();
        using (var w = new Utf8JsonWriter(ms))
        {
            w.WriteStartObject();
            foreach (JsonProperty p in network.EnumerateObject())
                if (p.Name == "schema" || keep(p.Name)) p.WriteTo(w);
            w.WriteEndObject();
        }
        return Encoding.UTF8.GetString(ms.ToArray());
    }

    private static void AssertSameSolve(PowerFlowResult a, PowerFlowResult b, string label)
    {
        Assert.Equal(a.Converged, b.Converged);
        Assert.Equal(a.Status, b.Status);
        Assert.Equal(a.Iterations, b.Iterations);
        Assert.Equal(Bits(a.MaxMismatch), Bits(b.MaxMismatch));
        Assert.Equal(a.BusIds, b.BusIds);
        Assert.Equal(a.Vm.Select(Bits), b.Vm.Select(Bits));
        Assert.Equal(a.VaDeg.Select(Bits), b.VaDeg.Select(Bits));
        Assert.Equal(a.PInjMw.Select(Bits), b.PInjMw.Select(Bits));
        Assert.Equal(a.QInjMvar.Select(Bits), b.QInjMvar.Select(Bits));
        Assert.True(Bits(a.TotalLossMw) == Bits(b.TotalLossMw), $"{label}: losses {a.TotalLossMw:R} vs {b.TotalLossMw:R}");
    }

    private static long Bits(double x) => BitConverter.DoubleToInt64Bits(x);

    /// <summary>
    /// Every difference between two object graphs, comparing every FIELD (public or private) of every GridSim type, so
    /// a model property backed by a private list (for example the full generator fleet, or the binding rules) is
    /// compared too. Doubles by their bits, strings ordinally, sequences element by element, dictionaries key by key in
    /// order. A type the comparer does not know throws rather than being skipped.
    /// </summary>
    private static List<string> DeepDifferences(object? expected, object? actual)
    {
        var diffs = new List<string>();
        Compare(expected, actual, "$", diffs, new HashSet<(object, object)>(ReferencePairComparer.Instance));
        return diffs;
    }

    private static void Compare(object? e, object? a, string path, List<string> diffs, HashSet<(object, object)> seen)
    {
        if (diffs.Count >= 50) return;
        if (e is null || a is null)
        {
            if (e is not null || a is not null) diffs.Add($"{path}: expected {e ?? "null"}, found {a ?? "null"}");
            return;
        }
        Type t = e.GetType();
        if (t != a.GetType()) { diffs.Add($"{path}: type {t.Name} vs {a.GetType().Name}"); return; }
        switch (e)
        {
            case double d:
                if (Bits(d) != Bits((double)a)) diffs.Add($"{path}: {d:R} vs {(double)a:R}");
                return;
            case float fl:
                if (BitConverter.SingleToInt32Bits(fl) != BitConverter.SingleToInt32Bits((float)a)) diffs.Add($"{path}: {fl:R} vs {(float)a:R}");
                return;
            case string or bool or int or long or Enum or char:
                if (!e.Equals(a)) diffs.Add($"{path}: {e} vs {a}");
                return;
            case IEqualityComparer<string>:
                // A dictionary's key comparer: the same comparer kind is all that matters.
                return;
        }
        if (!t.IsValueType && !seen.Add((e, a))) return;   // already compared (shared references)
        if (e is IDictionary ed)
        {
            var ad = (IDictionary)a;
            var ek = ed.Keys.Cast<object>().ToList();
            var ak = ad.Keys.Cast<object>().ToList();
            if (ek.Count != ak.Count || !ek.SequenceEqual(ak)) { diffs.Add($"{path}: keys [{string.Join(", ", ek)}] vs [{string.Join(", ", ak)}]"); return; }
            foreach (object k in ek) Compare(ed[k], ad[k], $"{path}[{k}]", diffs, seen);
            return;
        }
        if (e is IEnumerable ee && t.Namespace?.StartsWith("GridSim.", StringComparison.Ordinal) != true)
        {
            var el = ee.Cast<object?>().ToList();
            var al = ((IEnumerable)a).Cast<object?>().ToList();
            if (el.Count != al.Count) { diffs.Add($"{path}: {el.Count} items vs {al.Count}"); return; }
            for (int i = 0; i < el.Count; i++) Compare(el[i], al[i], $"{path}[{i}]", diffs, seen);
            return;
        }
        if (t.Namespace?.StartsWith("GridSim.", StringComparison.Ordinal) != true)
            throw new NotSupportedException($"{path}: the comparer does not know type {t.FullName}; teach it rather than skipping the field.");
        for (Type? cur = t; cur is not null && cur != typeof(object); cur = cur.BaseType)
            foreach (FieldInfo f in cur.GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly))
                Compare(f.GetValue(e), f.GetValue(a), $"{path}.{f.Name}", diffs, seen);
    }

    private sealed class ReferencePairComparer : IEqualityComparer<(object, object)>
    {
        public static readonly ReferencePairComparer Instance = new();
        public bool Equals((object, object) x, (object, object) y) => ReferenceEquals(x.Item1, y.Item1) && ReferenceEquals(x.Item2, y.Item2);
        public int GetHashCode((object, object) p) => HashCode.Combine(System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(p.Item1), System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(p.Item2));
    }

    /// <summary>A fresh temp folder, deleted on dispose.</summary>
    private sealed class TempFolder : IDisposable
    {
        public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "gridsim-v2load-" + Guid.NewGuid().ToString("N"));
        public TempFolder() => Directory.CreateDirectory(Path);
        public void Dispose() { try { Directory.Delete(Path, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { } }
    }
}
