using System.Text.Json;
using System.Text.RegularExpressions;
using GridSim.Core.Dispatch;
using GridSim.Gda;
using Xunit;

namespace GridSim.Tests;

/// <summary>
/// Q25 (October 2026): GDA's <see cref="FuelMap"/> writes generator types such as <c>wind-onshore</c>,
/// <c>pumped-storage</c>, <c>chp</c>, <c>recip</c>, <c>tidal</c>, <c>grid</c> and <c>other</c>, which
/// <see cref="Dispatcher.DefaultCapacityFactor"/> does not list, so for those types it would return its 0.40
/// catch-all instead of the plant's real capacity factor.
///
/// <para><b>Finding: that fallback never runs on GDA output.</b> The ONLY caller of
/// <see cref="Dispatcher.DefaultCapacityFactor"/> is the v1 JSON loader, and only for a generator with neither
/// <c>outputMw</c> nor <c>capacityFactor</c>. <c>GdaModelWriter</c> always writes <c>outputMw</c> (the in-memory
/// dispatch, itself computed with <see cref="FuelMap.CapacityFactor"/>), and the in-memory GDA build path
/// (<c>GdaModelBuilder</c>) uses <see cref="FuelMap.CapacityFactor"/> directly. So no plant's capacity factor is
/// decided by the Dispatcher table on gb-full or any other bundled GDA case.</para>
///
/// <para>These tests keep it that way: if a bundled generator ever relies on the Dispatcher table, its type must be
/// one the table names explicitly - never the silent catch-all.</para>
/// </summary>
public sealed class DispatcherFallbackReachTests
{
    /// <summary>Lenient on comments and trailing commas only, as the v1 loader is.</summary>
    private static readonly JsonDocumentOptions ReadOptions = new()
    {
        CommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    /// <summary>The type labels Dispatcher.DefaultCapacityFactor names explicitly, read from its source (the switch
    /// is an expression, so reflection cannot list its arms). Everything else takes the 0.40 catch-all.</summary>
    private static HashSet<string> DispatcherExplicitLabels()
    {
        string src = File.ReadAllText(Path.Combine(PresentationConstantsAntiDriftTests.RepoRoot(),
            "src", "GridSim.Core", "Dispatch", "Dispatcher.cs"));
        int start = src.IndexOf("public static double DefaultCapacityFactor", StringComparison.Ordinal);
        Assert.True(start >= 0, "DefaultCapacityFactor not found in Dispatcher.cs");
        int end = src.IndexOf("_ =>", start, StringComparison.Ordinal);
        Assert.True(end > start, "DefaultCapacityFactor has no catch-all arm");
        var labels = Regex.Matches(src[start..end], "\"([a-z_]+)\"").Select(m => m.Groups[1].Value)
            .ToHashSet(StringComparer.Ordinal);
        // Guards against a silently empty parse: the table has had these since it was written.
        Assert.Contains("nuclear", labels);
        Assert.Contains("ccgt", labels);
        return labels;
    }

    /// <summary>Every bundled v1 generation.json (each is one case folder).</summary>
    private static List<string> BundledGenerationFiles()
        => Directory.EnumerateFiles(Path.Combine(PresentationConstantsAntiDriftTests.RepoRoot(), "data"), "generation.json",
                SearchOption.AllDirectories)
            .OrderBy(f => f, StringComparer.Ordinal).ToList();

    /// <summary>A member of a generator object, matched case-insensitively as the v1 loader matches keys.</summary>
    private static JsonElement? Member(JsonElement obj, string name)
    {
        foreach (JsonProperty p in obj.EnumerateObject())
            if (string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase)) return p.Value;
        return null;
    }

    private static bool HasNumber(JsonElement obj, string name)
        => Member(obj, name) is { ValueKind: JsonValueKind.Number };

    [Fact]
    public void The_dispatcher_catch_all_is_reached_by_no_bundled_generator()
    {
        HashSet<string> explicitLabels = DispatcherExplicitLabels();
        List<string> files = BundledGenerationFiles();
        Assert.Contains(files, f => f.Replace('\\', '/').EndsWith("data/gb-full/generation.json", StringComparison.Ordinal));

        int generators = 0, relyOnTable = 0;
        var catchAll = new List<string>();
        foreach (string file in files)
        {
            using JsonDocument doc = JsonDocument.Parse(File.ReadAllText(file), ReadOptions);
            foreach (JsonElement g in doc.RootElement.EnumerateArray())
            {
                generators++;
                if (HasNumber(g, "outputMw") || HasNumber(g, "capacityFactor")) continue;
                // This generator's dispatch comes from Dispatcher.DefaultCapacityFactor(type).
                relyOnTable++;
                string type = Member(g, "type")?.GetString() ?? "ccgt";   // absent type = JsonDefaults.GeneratorType
                if (!explicitLabels.Contains(type.ToLowerInvariant()))
                    catchAll.Add($"{file}: '{Member(g, "name")?.GetString()}' type '{type}'");
            }
        }

        Assert.True(generators > 5000, $"suspiciously few bundled generators read: {generators}");
        Assert.True(catchAll.Count == 0,
            "bundled generators whose output silently takes Dispatcher's 0.40 catch-all:\n  " + string.Join("\n  ", catchAll));
        // Today exactly one bundled generator relies on the table at all: the data/uk marginal CCGT (the slack,
        // whose output the balance then overwrites). Pinned so a new reliance is seen.
        Assert.Equal(1, relyOnTable);
    }

    [Fact]
    public void Every_gda_generator_written_to_json_carries_an_explicit_output()
    {
        // The writer side of the finding: GdaModelWriter writes outputMw for every generator, including the grid
        // infeed (0.0), so the v1 loader never consults Dispatcher for a GDA case. Read from the source because the
        // writer's anonymous objects cannot be reflected.
        string src = File.ReadAllText(Path.Combine(PresentationConstantsAntiDriftTests.RepoRoot(),
            "src", "GridSim.Gda", "GdaModelWriter.cs"));
        Assert.Contains("outputMw = g.Pg,", src, StringComparison.Ordinal);
        Assert.Contains("outputMw = 0.0,", src, StringComparison.Ordinal);
    }
}
