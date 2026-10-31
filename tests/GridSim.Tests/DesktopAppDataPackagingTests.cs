using System.Xml.Linq;
using Xunit;

namespace GridSim.Tests;

/// <summary>
/// GitHub issues #6 and #9 (Release Candidate 1.0 flaws).
/// <para>#6: the Avalonia app archives shipped without the <c>data/</c> folder, so the case list showed only the cases
/// built into the engine. The desktop head now carries the CLI's bundled-data, licence and schema items, item for item;
/// these tests pin that the two downloads cannot drift apart again.</para>
/// <para>#9: the manual said a fresh install lists and connects to a hosted bridge. The default endpoint is empty; the
/// docs now say so, and the code default is pinned beside the text.</para>
/// </summary>
public sealed class DesktopAppDataPackagingTests
{
    private static string Root()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "GridSim.slnx"))) dir = dir.Parent;
        Assert.NotNull(dir);
        return dir!.FullName;
    }

    /// <summary>Every Content item of a project file that ships bundled data or schemas, as one comparable string
    /// (Include, Exclude, Link, the copy flags and ExcludeFromSingleFile).</summary>
    private static SortedSet<string> DataItems(string project)
    {
        XDocument doc = XDocument.Load(Path.Combine(Root(), project));
        var set = new SortedSet<string>(StringComparer.Ordinal);
        foreach (XElement c in doc.Descendants("Content"))
        {
            string include = (string?)c.Attribute("Include") ?? "";
            if (!include.Contains(@"..\..\data\", StringComparison.Ordinal) && !include.Contains(@"..\..\schemas\", StringComparison.Ordinal)) continue;
            set.Add(string.Join(" | ", include, (string?)c.Attribute("Exclude") ?? "", (string?)c.Attribute("Link") ?? "",
                (string?)c.Attribute("CopyToOutputDirectory") ?? "", (string?)c.Attribute("CopyToPublishDirectory") ?? "",
                (string?)c.Attribute("ExcludeFromSingleFile") ?? ""));
        }
        return set;
    }

    [Fact]
    public void The_desktop_app_ships_exactly_the_bundled_data_and_schemas_the_cli_ships()
    {
        SortedSet<string> cli = DataItems("src/GridSim.Cli/GridSim.Cli.csproj");
        SortedSet<string> app = DataItems("src/GridSim.App.Desktop/GridSim.App.Desktop.csproj");
        // The CLI set is not empty (a renamed folder would make both empty and the comparison vacuous).
        Assert.True(cli.Count >= 6, $"the CLI ships only {cli.Count} data / schema items");
        Assert.Equal(cli, app);
        // The manifest and the case JSON are the parts the case list needs; named so a failure says what is missing.
        Assert.Contains(app, i => i.StartsWith(@"..\..\data\bundled.sha256 |", StringComparison.Ordinal));
        Assert.Contains(app, i => i.StartsWith(@"..\..\data\**\*.json |", StringComparison.Ordinal));
    }

    [Fact]
    public void A_fresh_install_has_no_bridge_endpoint_and_the_manual_says_so()
    {
        // The code default: empty, so no Live bridge source and no connection until the user sets one.
        Assert.Equal("", new GridSim.Ui.Settings.AppSettings().BridgeEndpoint);
        // The three places that described the default now say it is empty; none names a hosted default.
        string[] docs =
        {
            "docs/manual/0023 Replaying estimated states.md",
            "docs/manual/0024 The GDA bridge.md",
            "docs/technical/0009 GDA integration.md",
        };
        foreach (string d in docs)
        {
            string text = File.ReadAllText(Path.Combine(Root(), d));
            Assert.DoesNotContain("twinscrollgridbalancer", text, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("hosted endpoint", text, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("already lists **Live bridge**", text, StringComparison.Ordinal);
        }
        Assert.Contains("The default is empty, so a fresh install does not list",
            File.ReadAllText(Path.Combine(Root(), docs[0])), StringComparison.Ordinal);
    }
}
