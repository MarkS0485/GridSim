using System.Text.Json;
using GridSim.Core.IO.Json.Schema;
using GridSim.Tests.Editor;
using GridSim.Tests.V2;

namespace GridSim.Tests.Ui;

/// <summary>
/// UI programme Phase 4.4 (2026-10-21): a JSON v2 case with inverter templates for the template editor tests. The case is
/// the bundled GB reduced case (data/uk) projected to v2 exactly as the v2 bit-identity pins project it (V1Projector),
/// written to a fresh temporary folder (never data/), with the template members of the committed fixture
/// tests/GridSim.Tests/fixtures/v2/templates/inverter-templates.json merged into its manifest: three local inverter
/// templates (gridsim.local.wind-gfl@1 and gridsim.local.solar-gfm@1, bound by type rules to the onshore, offshore and
/// solar generators; gridsim.local.spare-gfl@1, bound to nothing) and one override (wind-gfl's deadbandHz 0.05 -> 0.08).
/// <paramref name="edit"/> may change the fixture text first (a value out of range for the Check test); with
/// <paramref name="library"/> a library file is written under a temporary data root and imported (the read-only view).
/// </summary>
internal sealed class UiTemplateFixture : IDisposable
{
    /// <summary>The committed fixture with the template members.</summary>
    public static string BlockPath => Path.Combine(V2TestSupport.FixturesDir, "templates", "inverter-templates.json");

    /// <summary>The library template a fixture made with <c>library: true</c> imports.</summary>
    public const string LibraryRef = "gridsim.test.lib-gfm@1";

    /// <summary>The library file (relative to the data root) a fixture made with <c>library: true</c> imports.</summary>
    public const string LibraryFile = "library/test-inverters.json";

    public const string LibraryJson = """
        {
          "schema": "gridsim-library/2",
          "title": "Phase 4.4 test library (temporary data root only)",
          "templates": [
            {
              "id": "gridsim.test.lib-gfm",
              "rev": 1,
              "kind": "gridFormingInverter",
              "status": "active",
              "title": "Library grid-forming inverter (test)",
              "params": { "synInertiaMwsPerHz": 250, "dampingMwPerHz": 1500 },
              "provenance": { "_default": { "tag": "CuratedPlaceholder", "note": "test library values [VERIFY]" } }
            }
          ]
        }
        """;

    private readonly string _root = Path.Combine(Path.GetTempPath(), "gridsim-templates-4-4-" + Guid.NewGuid().ToString("N"));

    public UiTemplateFixture(Func<string, string>? edit = null, bool library = false)
    {
        Directory.CreateDirectory(CaseDir);
        V2Projection p = V1Projector.Project(V1Projector.ReadV1(Path.Combine(EditorTestSupport.DataDir, "uk")));
        ManifestPath = Path.Combine(CaseDir, "uk.case.json");
        File.WriteAllBytes(ManifestPath, p.CaseJson);
        File.WriteAllBytes(Path.Combine(CaseDir, V1Projector.NetworkFileName), p.NetworkJson);
        string block = File.ReadAllText(BlockPath);
        if (edit is not null) block = edit(block);
        Merge(ManifestPath, block, library ? LibraryFile : null);
        if (library)
        {
            Directory.CreateDirectory(Path.Combine(DataRoot, "library"));
            File.WriteAllText(Path.Combine(DataRoot, LibraryFile.Replace('/', Path.DirectorySeparatorChar)), LibraryJson);
        }
    }

    /// <summary>The case folder.</summary>
    public string CaseDir => Path.Combine(_root, "case");

    /// <summary>The temporary data root a library import resolves against.</summary>
    public string DataRoot => Path.Combine(_root, "data");

    /// <summary>The manifest (gridsim-case/2).</summary>
    public string ManifestPath { get; }

    /// <summary>The network file of the projection.</summary>
    public string NetworkPath => Path.Combine(CaseDir, V1Projector.NetworkFileName);

    /// <summary>
    /// Write the fixture's localTemplates, bindings and overrides into the manifest in the case schema's member order
    /// (after network and imports, before assets), and, with <paramref name="importLibrary"/>, an import of every template
    /// of that library file.
    /// </summary>
    public static void Merge(string manifestPath, string block, string? importLibrary)
    {
        using JsonDocument m = JsonSchemaSubset.ParseFile(manifestPath);
        using JsonDocument b = JsonDocument.Parse(block, JsonSchemaSubset.ReaderOptions);
        string[] order = { "schema", "name", "description", "system", "gridCodeProfile", "solverProfile", "network", "imports", "localTemplates", "bindings", "overrides", "assets" };
        // Fail loud rather than drop a member the projection writes that this merge does not know.
        foreach (JsonProperty prop in m.RootElement.EnumerateObject())
            if (!order.Contains(prop.Name, StringComparer.Ordinal))
                throw new InvalidOperationException("the projected manifest has a member '" + prop.Name + "' the template fixture merge does not place.");
        using var ms = new MemoryStream();
        using (var w = new Utf8JsonWriter(ms, new JsonWriterOptions { Indented = true }))
        {
            w.WriteStartObject();
            foreach (string name in order)
            {
                if (name == "imports" && importLibrary is not null)
                {
                    w.WriteStartArray("imports");
                    w.WriteStartObject();
                    w.WriteString("library", importLibrary);
                    w.WriteStartArray("templates");
                    w.WriteStringValue("gridsim.*");
                    w.WriteEndArray();
                    w.WriteEndObject();
                    w.WriteEndArray();
                    continue;
                }
                if (name is "localTemplates" or "bindings" or "overrides")
                {
                    if (b.RootElement.TryGetProperty(name, out JsonElement v)) { w.WritePropertyName(name); v.WriteTo(w); }
                    continue;
                }
                if (m.RootElement.TryGetProperty(name, out JsonElement mv)) { w.WritePropertyName(name); mv.WriteTo(w); }
            }
            w.WriteEndObject();
        }
        File.WriteAllBytes(manifestPath, ms.ToArray());
    }

    public void Dispose()
    {
        try { if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true); }
        catch (IOException) { /* a leftover temp folder is harmless */ }
        catch (UnauthorizedAccessException) { /* likewise */ }
    }
}
