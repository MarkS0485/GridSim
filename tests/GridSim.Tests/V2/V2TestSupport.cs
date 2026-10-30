using System.Text.Json;
using GridSim.Core.IO.Json.Schema;
using Xunit;

namespace GridSim.Tests.V2;

/// <summary>
/// Shared helpers for the v2 schema tests. Files are read in place from the repository (found by walking up to
/// GridSim.slnx, as BundledNetworksTests does) rather than from the test output folder, so no app or test csproj
/// has to learn to copy <c>schemas/**</c>.
/// </summary>
internal static class V2TestSupport
{
    /// <summary>The repository root: the nearest folder above the test binaries holding GridSim.slnx.</summary>
    public static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "GridSim.slnx"))) dir = dir.Parent;
        Assert.True(dir is not null, $"GridSim.slnx not found above {AppContext.BaseDirectory}");
        return dir!.FullName;
    }

    /// <summary>schemas/v2 in the repository.</summary>
    public static string SchemasDir => Path.Combine(RepoRoot(), "schemas", "v2");

    /// <summary>schemas/v2/kinds in the repository.</summary>
    public static string KindsDir => Path.Combine(SchemasDir, "kinds");

    /// <summary>data/library in the repository (may be absent: every library is strippable).</summary>
    public static string LibraryDir => Path.Combine(RepoRoot(), "data", "library");

    /// <summary>tests/GridSim.Tests/fixtures/v2 in the repository.</summary>
    public static string FixturesDir => Path.Combine(RepoRoot(), "tests", "GridSim.Tests", "fixtures", "v2");

    /// <summary>The real v2 schema set.</summary>
    public static JsonSchemaSubset RealSchemas() => JsonSchemaSubset.LoadDirectory(SchemasDir);

    /// <summary>Build a schema set from (file name, text) pairs.</summary>
    public static JsonSchemaSubset Schemas(params (string File, string Text)[] files)
        => JsonSchemaSubset.FromSources(files.ToDictionary(f => f.File, f => f.Text, StringComparer.Ordinal));

    /// <summary>Validate a JSON text against the root of one file in a schema set.</summary>
    public static IReadOnlyList<SchemaError> Validate(JsonSchemaSubset set, string file, string json)
    {
        using JsonDocument doc = JsonDocument.Parse(json, JsonSchemaSubset.ReaderOptions);
        return new SchemaValidator(set).Validate(doc.RootElement.Clone(), file);
    }

    /// <summary>Build a single-file schema set "s.schema.json" and validate a JSON text against it.</summary>
    public static IReadOnlyList<SchemaError> ValidateOne(string schema, string json)
        => Validate(Schemas(("s.schema.json", schema)), "s.schema.json", json);

    /// <summary>Read a library file (or fixture) as a detached root element.</summary>
    public static JsonElement ReadRoot(string path)
    {
        using JsonDocument doc = JsonSchemaSubset.ParseFile(path);
        return doc.RootElement.Clone();
    }

    /// <summary>Every error rendered as one string each, for readable assertion messages.</summary>
    public static string Dump(IEnumerable<SchemaError> errors) => string.Join(Environment.NewLine, errors.Select(e => e.ToString()));
}
