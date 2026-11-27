using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;
using GridSim.Core.Journal;
using Xunit;

namespace GridSim.Tests;

/// <summary>
/// RC5 (run journal), T8: the breaking-change map (src/GridSim.Core/Journal/breaking-changes.json) and the CHANGELOG agree.
/// Every BREAKING CHANGE bullet from 2.1.074 on carries an ID that is in the map, every map ID is carried by a bullet in
/// the release section the map names, IDs run B1, B2, ... in version order, and the map's bytes are pinned. Also the
/// explanation rule and the version order.
/// </summary>
public sealed class BreakingChangeMapTests
{
    private static string MapPath => Path.Combine(CliProcess.Root(), "src", "GridSim.Core", "Journal", "breaking-changes.json");

    /// <summary>The SHA-256 of the map's bytes, as every validate report records it. A change to the map changes this pin
    /// in the same commit.</summary>
    private const string MapSha256 = "b44a70aa4c63ca0c7b91340c972ceaf6cfb6d61bed944f93c1a937da756c6f64";

    [Fact]
    public void T8_the_map_is_embedded_byte_for_byte_has_no_carriage_return_and_its_hash_is_pinned()
    {
        byte[] file = File.ReadAllBytes(MapPath);
        Assert.DoesNotContain((byte)'\r', file);
        Assert.Equal(file, BreakingChangeMap.EmbeddedBytes());
        Assert.Equal(MapSha256, Convert.ToHexStringLower(SHA256.HashData(file)));
        Assert.Equal(MapSha256, BreakingChangeMap.Default.Sha256);
    }

    [Fact]
    public void T8_ids_run_in_order_and_versions_ascend_from_rc4()
    {
        IReadOnlyList<BreakingChange> map = BreakingChangeMap.Default.Entries;
        Assert.Equal(60, map.Count);
        for (int i = 0; i < map.Count; i++)
        {
            Assert.Equal("B" + (i + 1), map[i].Id);
            Assert.True(VersionOrder.Compare(map[i].Version, "2.1.073") > 0, map[i].Id + " is not after RC-4");
            Assert.True(VersionOrder.Compare(map[i].Version, GridSim.Core.BuildInfo.Version) <= 0, map[i].Id + " is after this build");
            if (i > 0) Assert.True(VersionOrder.Compare(map[i - 1].Version, map[i].Version) <= 0);
            Assert.Contains(map[i].Kind, new[] { "behaviour", "bug-fix", "security" });
        }
        // A map out of order is refused.
        string swapped = File.ReadAllText(MapPath).Replace("\"id\": \"B1\"", "\"id\": \"BX\"", StringComparison.Ordinal);
        Assert.Throws<InvalidDataException>(() => BreakingChangeMap.Parse(System.Text.Encoding.UTF8.GetBytes(swapped)));
    }

    [Fact]
    public void T8_every_breaking_change_bullet_since_rc4_carries_one_id_and_the_map_and_changelog_agree()
    {
        string[] lines = File.ReadAllLines(Path.Combine(CliProcess.Root(), "CHANGELOG.md"));
        string? section = null;
        var idsBySection = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        var problems = new List<string>();
        foreach (string l in lines)
        {
            Match h = Regex.Match(l, @"^## \[(\d+\.\d+\.\d+)\]");
            if (h.Success) { section = h.Groups[1].Value; continue; }
            if (section is null || VersionOrder.Compare(section, "2.1.074") < 0) continue;
            if (!l.StartsWith("- BREAKING CHANGE", StringComparison.Ordinal)) continue;
            Match id = Regex.Match(l, @"^- BREAKING CHANGE \((B\d+)[,)]");
            if (!id.Success) { problems.Add($"{section}: a BREAKING CHANGE bullet has no ID: {l[..Math.Min(80, l.Length)]}"); continue; }
            if (Regex.Count(l, @"\bB\d+\b") != 1) problems.Add($"{section}: a bullet carries more than one ID: {l[..Math.Min(80, l.Length)]}");
            if (!idsBySection.TryGetValue(section, out List<string>? list)) idsBySection[section] = list = new List<string>();
            list.Add(id.Groups[1].Value);
        }
        Assert.True(problems.Count == 0, string.Join(Environment.NewLine, problems));
        // Within one release section an ID appears once.
        foreach ((string sec, List<string> ids) in idsBySection)
            Assert.True(ids.Count == ids.Distinct().Count(), $"{sec} carries an ID twice: {string.Join(", ", ids)}");

        IReadOnlyList<BreakingChange> map = BreakingChangeMap.Default.Entries;
        var inChangelog = idsBySection.Values.SelectMany(x => x).ToHashSet(StringComparer.Ordinal);
        Assert.Equal(map.Select(b => b.Id).Order(StringComparer.Ordinal), inChangelog.Order(StringComparer.Ordinal));
        foreach (BreakingChange b in map)
            Assert.True(idsBySection.TryGetValue(b.Changelog, out List<string>? ids) && ids.Contains(b.Id),
                $"{b.Id}: the map names CHANGELOG {b.Changelog}, whose bullets do not carry it");
    }

    [Fact]
    public void The_explanation_rule_is_the_version_window_and_the_scope()
    {
        BreakingChangeMap map = BreakingChangeMap.Default;
        // A run recorded on RC-4: every breaking change since is in its window.
        IReadOnlyList<string> acscopf = map.Explain("2.1.073", "2.1.104", "acscopf", ["acscopf", "case9"], []);
        Assert.Equal(new[] { "B2 (broad scope)", "B12", "B14", "B15", "B25" }, acscopf);
        IReadOnlyList<string> raw = map.Explain("2.1.073", "2.1.104", "pf", ["pf", "net.raw"], ["/work/net.RAW"]);
        Assert.Equal(new[] { "B2 (broad scope)", "B5", "B21" }, raw);
        // A run recorded on 2.1.100 is in the window of 2.1.102 and 2.1.103 only.
        Assert.Equal(new[] { "B26" }, map.Explain("2.1.100", "2.1.104", "dynamics-study", ["dynamics-study", "s.json"], []));
        // An option in scope explains a run of any verb.
        Assert.Contains("B3", map.Explain("2.1.080", "2.1.104", "stability", ["stability", "x.raw", "--dyr-extended"], []));
        // A build older than the change does not count it.
        Assert.Empty(map.Explain("2.1.073", "2.1.074", "pf", ["pf", "case9"], []));
        // Nothing explains a run recorded on this release.
        Assert.Empty(map.Explain("2.1.104", "2.1.104", "acscopf", ["acscopf", "case9"], []));
    }

    [Fact]
    public void Release_numbers_compare_part_by_part_as_numbers()
    {
        Assert.True(VersionOrder.Compare("2.1.090", "2.1.103") < 0);
        Assert.True(VersionOrder.Compare("2.1.9", "2.1.10") < 0);
        Assert.Equal(0, VersionOrder.Compare("v2.1.097-RC-5", "2.1.097"));
        Assert.Equal(0, VersionOrder.Compare("2.1.104+abc", "2.1.104"));
        Assert.True(VersionOrder.Compare("3.0.0", "2.9.999") > 0);
        Assert.Null(VersionOrder.Parse("latest"));
    }

    [Fact]
    public void List_prints_the_map_and_since_keeps_the_later_entries()
    {
        string d = JournalTestSupport.NewDir("bc-list");
        try
        {
            CliProcess.Result all = JournalTestSupport.Run(d, "breaking-change", "list");
            Assert.True(all.ExitCode == 0, all.ToString());
            for (int i = 1; i <= 60; i++) Assert.Contains($"  B{i} ", all.StdOut, StringComparison.Ordinal);
            CliProcess.Result since = JournalTestSupport.Run(d, "breaking-change", "list", "--since", "2.1.100", "--json", "l.json");
            Assert.Equal(0, since.ExitCode);
            using JsonDocument doc = JsonDocument.Parse(File.ReadAllText(Path.Combine(d, "l.json")));
            Assert.Equal(new[] { "B25", "B26", "B27", "B28", "B29", "B30", "B31", "B32", "B33", "B34", "B35", "B36", "B37", "B38", "B39", "B40", "B41", "B42", "B43", "B44", "B45", "B46", "B47", "B48", "B49", "B50", "B51", "B52", "B53", "B54", "B55", "B56", "B57", "B58", "B59", "B60" },
                doc.RootElement.GetProperty("tables").GetProperty("breaking_changes").EnumerateArray().Select(r => r.GetProperty("id").GetString()));
            Assert.Equal(MapSha256, doc.RootElement.GetProperty("summary").GetProperty("map_sha256").GetString());
            Assert.Equal(2, JournalTestSupport.Run(d, "breaking-change", "list", "--since", "soon").ExitCode);
            Assert.Equal(2, JournalTestSupport.Run(d, "breaking-change", "nonsense").ExitCode);
            Assert.Equal(2, JournalTestSupport.Run(d, "breaking-change").ExitCode);
        }
        finally { JournalTestSupport.Delete(d); }
    }
}
