using GridSim.Core.IO.Json;
using GridSim.Core.IO.Json.Schema;
using Xunit;

namespace GridSim.Tests;

/// <summary>
/// GR-183: the v2 case loader parsed the schema set and the kind registry from disk once for each validation stage of a
/// case load. The loader now loads them once and passes them to V2DocumentValidator.ValidateFiles (a new overload taking
/// the loaded set). Pinned: the overload that takes a pre-loaded schema set and kind registry gives exactly the reports of
/// the original two-argument overload (same files, tags and error texts, in the same order), for a good manifest and for a
/// manifest with a schema error; and a case still loads.
/// </summary>
public class Gr183V2SchemaSetOnceTests
{
    private static string SchemasDir => Path.Combine(CliProcess.Root(), "schemas", "v2");

    private const string Network = """
        {
          "schema": "gridsim-network/2",
          "buses": [
            { "id": 1, "name": "North", "baseKv": 400, "type": "slack" },
            { "id": 2, "name": "South", "baseKv": 400 }
          ],
          "generators": [
            { "name": "Slack", "bus": 1, "type": "ccgt", "capacityMva": 1000, "outputMw": 0, "role": "slack" }
          ],
          "demands": [ { "name": "Load", "bus": 2, "pMw": 100, "qMvar": 10 } ],
          "lines": [ { "name": "L1-2", "from": 1, "to": 2, "rPu": 0.002, "xPu": 0.02, "ratingMva": 1000 } ]
        }
        """;

    private static string Manifest(string name) => $$"""
        {
          "schema": "gridsim-case/2",
          "name": {{name}},
          "system": { "baseMva": 100, "frequencyHz": 50 },
          "network": ["network.json"]
        }
        """;

    private static T WithCase<T>(string manifest, Func<string, T> act)
    {
        string dir = Path.Combine(Path.GetTempPath(), "gridsim-gr183-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            File.WriteAllText(Path.Combine(dir, "network.json"), Network);
            string path = Path.Combine(dir, "case.json");
            File.WriteAllText(path, manifest);
            return act(path);
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    private static string[] Flatten(IReadOnlyList<V2DocumentReport> reports)
        => reports.SelectMany(r => new[] { r.Path, r.SchemaTag ?? "(none)" }.Concat(r.Errors.Select(e => e.ToString()!))).ToArray();

    [Theory]
    [InlineData("\"Preloaded good\"")]
    [InlineData("12345")]   // a manifest whose name is not a string: a schema error that must read the same either way
    public void Gr183_the_preloaded_overload_gives_the_same_reports_as_the_two_argument_overload(string nameJson)
    {
        string[] viaDir = WithCase(Manifest(nameJson), p => Flatten(V2DocumentValidator.ValidateFiles(new[] { p }, SchemasDir)));
        string[] viaSet = WithCase(Manifest(nameJson), p =>
        {
            JsonSchemaSubset schemas = JsonSchemaSubset.LoadDirectory(SchemasDir);
            KindSpecRegistry kinds = KindSpecRegistry.LoadDirectory(Path.Combine(SchemasDir, "kinds"), schemas);
            return Flatten(V2DocumentValidator.ValidateFiles(new[] { p }, SchemasDir, schemas, kinds));
        });
        // The temp folder differs between the two runs, so compare with the folder name masked out of each line.
        string[] a = viaDir.Select(MaskTemp).ToArray();
        string[] b = viaSet.Select(MaskTemp).ToArray();
        Assert.Equal(a, b);
    }

    private static string MaskTemp(string s)
        => System.Text.RegularExpressions.Regex.Replace(s, "gridsim-gr183-[0-9a-f]{32}", "gridsim-gr183-X");

    [Fact]
    public void Gr183_a_case_still_loads_with_the_schema_set_loaded_once()
    {
        var model = WithCase(Manifest("\"Loads\""), p => V2CaseLoader.Load(p, SchemasDir));
        Assert.Equal(2, model.Buses.Count);
    }

    [Fact]
    public void Gr183_a_null_schema_set_or_registry_is_refused_by_name()
    {
        Assert.Throws<ArgumentNullException>(() => V2DocumentValidator.ValidateFiles(Array.Empty<string>(), SchemasDir, null!, null!));
    }
}
