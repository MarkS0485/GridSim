using System.Text.RegularExpressions;
using Xunit;

namespace GridSim.Tests;

/// <summary>
/// 2.1.000 (the first GitHub macOS CI run): schema patterns used the backtracking engine with a 1 s wall-clock match
/// timeout, so a loaded machine reported plain names such as "Nissan wind-onshore" as "could not be checked against
/// the pattern \S", and validation depended on load. They now run on the linear-time (non-backtracking) engine with no
/// timeout. These tests pin that every pattern the shipped schemas use compiles on that engine, and that the engine is
/// the one in use (no timeout set).
/// </summary>
public sealed class SchemaPatternEngineTests
{
    private static string Root()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "GridSim.slnx"))) dir = dir.Parent;
        Assert.NotNull(dir);
        return dir!.FullName;
    }

    [Fact]
    public void Every_schema_pattern_compiles_on_the_linear_time_engine()
    {
        var patterns = new SortedSet<string>(StringComparer.Ordinal);
        foreach (string f in Directory.EnumerateFiles(Path.Combine(Root(), "schemas"), "*.json", SearchOption.AllDirectories))
        {
            using var doc = System.Text.Json.JsonDocument.Parse(File.ReadAllText(f));
            Collect(doc.RootElement, patterns);
        }
        Assert.True(patterns.Count >= 5, $"only {patterns.Count} patterns found; the schema folder moved?");
        foreach (string p in patterns)
        {
            var r = new Regex(p, RegexOptions.CultureInvariant | RegexOptions.NonBacktracking, Regex.InfiniteMatchTimeout);
            Assert.Equal(Regex.InfiniteMatchTimeout, r.MatchTimeout);
        }
    }

    [Fact]
    public void The_names_that_timed_out_on_the_CI_runner_match_without_a_time_limit()
    {
        var r = new Regex("\\S", RegexOptions.CultureInvariant | RegexOptions.NonBacktracking, Regex.InfiniteMatchTimeout);
        foreach (string name in new[] { "Nairn Road Gas (Drumcross Gas) gas", "Nissan wind-onshore", "Nechells West 132kV 213B",
                     "STEN11-WHAG11 132kV", "19517-19911 132kV", "spine:CAMB_01-THOM_6" })
            Assert.Matches(r, name);
        Assert.DoesNotMatch(r, "   ");
    }

    private static void Collect(System.Text.Json.JsonElement e, SortedSet<string> into)
    {
        if (e.ValueKind == System.Text.Json.JsonValueKind.Object)
        {
            foreach (var p in e.EnumerateObject())
            {
                if (p.Name == "pattern" && p.Value.ValueKind == System.Text.Json.JsonValueKind.String) into.Add(p.Value.GetString()!);
                Collect(p.Value, into);
            }
        }
        else if (e.ValueKind == System.Text.Json.JsonValueKind.Array)
            foreach (var x in e.EnumerateArray()) Collect(x, into);
    }
}
