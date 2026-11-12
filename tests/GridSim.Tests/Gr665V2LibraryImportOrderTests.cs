using GridSim.Core.IO.Json;
using GridSim.Core.Model;
using Xunit;

namespace GridSim.Tests;

/// <summary>
/// GR-665 (test only): the recorded decision for the v2 loader is that two library files which both match an imported id
/// prefix contribute their asset classes in the CASE'S IMPORT ORDER (the order the author wrote the "imports" entries),
/// then the file's own template order, with no sort by file name or id. The decision was recorded in the known
/// limitations chapter but never pinned. Pinned: with the same two library files, importing them A then B gives the
/// classes A's then B's, and importing them B then A gives B's then A's; within one file the template order is kept.
/// </summary>
public sealed class Gr665V2LibraryImportOrderTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "gridsim-gr665-" + Guid.NewGuid().ToString("N"));
    private string CaseDir => Path.Combine(_root, "case");
    private string DataDir => Path.Combine(_root, "data");
    private static string SchemasDir => Path.Combine(CliProcess.Root(), "schemas", "v2");

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch (IOException) { }
        GC.SuppressFinalize(this);
    }

    private static string Template(string id) => $$"""
        { "id": "{{id}}", "rev": 1, "kind": "gridFollowingInverter", "status": "active", "title": "{{id}}",
          "params": { "deadbandHz": 0.015, "droopGainMwPerHzPerMw": 2.0 },
          "provenance": { "_default": { "tag": "Synthetic", "note": "test fixture" } } }
        """;

    // Library A holds two templates in a deliberately non-alphabetical order (a2 before a1); library B holds one.
    private static string LibraryA => $$"""{ "schema": "gridsim-library/2", "templates": [ {{Template("gridsim.test.a2")}}, {{Template("gridsim.test.a1")}} ] }""";
    private static string LibraryB => $$"""{ "schema": "gridsim-library/2", "templates": [ {{Template("gridsim.test.b1")}} ] }""";

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

    private string[] ClassOrder(params string[] importsInOrder)
    {
        Directory.CreateDirectory(Path.Combine(DataDir, "lib"));
        Directory.CreateDirectory(CaseDir);
        File.WriteAllText(Path.Combine(DataDir, "lib", "a.library.json"), LibraryA);
        File.WriteAllText(Path.Combine(DataDir, "lib", "b.library.json"), LibraryB);
        File.WriteAllText(Path.Combine(CaseDir, "network.json"), Network);
        string imports = string.Join(", ", importsInOrder.Select(f => $$"""{ "library": "lib/{{f}}.library.json", "templates": ["gridsim.test.*"] }"""));
        File.WriteAllText(Path.Combine(CaseDir, "case.json"), $$"""
            {
              "schema": "gridsim-case/2",
              "name": "Order test",
              "system": { "baseMva": 100, "frequencyHz": 50 },
              "network": ["network.json"],
              "imports": [ {{imports}} ]
            }
            """);
        GridModel m = V2CaseLoader.Load(Path.Combine(CaseDir, "case.json"), SchemasDir, DataDir);
        return m.AssetDynamics!.Library.Classes.Select(c => c.Name).ToArray();
    }

    [Fact]
    public void Gr665_importing_a_then_b_gives_a_classes_then_b_classes_in_each_files_own_order()
    {
        Assert.Equal(new[] { "gridsim.test.a2@1", "gridsim.test.a1@1", "gridsim.test.b1@1" }, ClassOrder("a", "b"));
    }

    [Fact]
    public void Gr665_importing_b_then_a_gives_b_classes_then_a_classes_no_sort_by_name()
    {
        Assert.Equal(new[] { "gridsim.test.b1@1", "gridsim.test.a2@1", "gridsim.test.a1@1" }, ClassOrder("b", "a"));
    }
}
