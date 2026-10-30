using System.Text.Json;
using System.Text.RegularExpressions;
using GridSim.Core.Assets;
using Xunit;

namespace GridSim.Tests;

/// <summary>
/// DOC-13 (October 2026). D-DOC-149: the v1 asset-classes schema no longer admits <c>synchronousMachine</c> /
/// <c>machine</c>, which the loader parses but refuses (no controller exists for an authored machine class), so a file
/// the schema (and the case editor) passes is no longer one the loader refuses. D-DOC-173: the refusal text and source
/// comments cited "docs/technical/13", which is no file; they now name the chapter and its real path. Every test is new.
/// </summary>
public sealed class Doc13AssetSchemaTests
{
    private static JsonElement KindEnum()
    {
        string path = Path.Combine(CliProcess.Root(), "schemas", "v1", "asset-classes.schema.json");
        using JsonDocument d = JsonDocument.Parse(File.ReadAllText(path));
        return d.RootElement.GetProperty("$defs").GetProperty("assetClass").GetProperty("properties").GetProperty("kind")
            .GetProperty("enum").Clone();
    }

    [Fact]
    public void D149_the_v1_schema_does_not_admit_the_machine_kinds_the_loader_refuses()
    {
        string[] kinds = KindEnum().EnumerateArray().Select(e => e.GetString()!).ToArray();
        Assert.DoesNotContain("synchronousMachine", kinds);
        Assert.DoesNotContain("machine", kinds);
        // The kinds GridSim can simulate are all still there.
        foreach (string k in new[] { "gridFollowingInverter", "gridFormingInverter", "transformer", "line", "protection" })
            Assert.Contains(k, kinds);
    }

    [Fact]
    public void D149_every_kind_the_schema_admits_is_one_the_loader_accepts()
    {
        // The library refuses an unsupported kind; every kind the schema lists must therefore map to a supported one.
        // The loader's alias table is private, so its source is the reference (as V2KindSpecCoverageTests does).
        string src = PresentationConstantsAntiDriftTests.Source("src", "GridSim.Core", "IO", "Json", "JsonGridLoader.cs");
        int start = src.IndexOf("private static AssetKind ParseAssetKind", StringComparison.Ordinal);
        string body = src[start..src.IndexOf("};", start, StringComparison.Ordinal)];
        foreach (JsonElement e in KindEnum().EnumerateArray())
        {
            string lower = e.GetString()!.ToLowerInvariant();
            Match line = Regex.Match(body, "\"" + Regex.Escape(lower) + "\"[^\\n]*=> AssetKind\\.(\\w+)");
            Assert.True(line.Success, $"schema kind '{e.GetString()}' is not an alias of ParseAssetKind");
            AssetKind kind = Enum.Parse<AssetKind>(line.Groups[1].Value);
            Assert.True(AssetClassSupport.IsSupportedKind(kind), $"schema kind '{e.GetString()}' maps to {kind}, which the loader refuses");
        }
    }

    [Fact]
    public void D173_the_refusal_text_names_a_file_that_exists()
    {
        string why = AssetClassSupport.RefusedKeys[AssetParams.DispatchRampMwPerMin];
        Assert.DoesNotContain("docs/technical/13.", why, StringComparison.Ordinal);
        Match m = Regex.Match(why, @"docs/technical/[\w .-]+\.md");
        Assert.True(m.Success, why);
        Assert.True(File.Exists(Path.Combine(CliProcess.Root(), m.Value.Replace('/', Path.DirectorySeparatorChar))), m.Value);
    }

    [Fact]
    public void D173_no_source_file_cites_the_bare_docs_technical_13_path()
    {
        string core = Path.Combine(CliProcess.Root(), "src");
        var offenders = Directory.EnumerateFiles(core, "*.cs", SearchOption.AllDirectories)
            .Where(f => Regex.IsMatch(File.ReadAllText(f), @"docs/technical/13(?![-\w])"))
            .Select(f => Path.GetRelativePath(CliProcess.Root(), f))
            .ToList();
        Assert.True(offenders.Count == 0, "bare docs/technical/13 cited in: " + string.Join(", ", offenders));
    }
}
