using System.Text.RegularExpressions;
using Xunit;

namespace GridSim.Tests;

/// <summary>
/// Build telemetry is off everywhere (Mark, 2026-10-20). The Avalonia packages bring in Avalonia.BuildServices, whose
/// AvaloniaStats target sends anonymous build statistics before CoreCompile. Two switches keep it off and both are
/// pinned here, so an edit cannot quietly turn it back on:
/// <list type="bullet">
/// <item>Directory.Build.targets replaces the target with an empty one (imported after the package targets, and the
/// last definition of an MSBuild target wins), which covers every build on every machine;</item>
/// <item>every workflow that builds, tests or publishes sets Avalonia's own switch, AVALONIA_TELEMETRY_OPTOUT=1, at
/// workflow level, so the intent is visible where CI builds are configured.</item>
/// </list>
/// </summary>
public class AvaloniaTelemetryOptOutTests
{
    private static string Root()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "GridSim.slnx"))) dir = dir.Parent;
        Assert.NotNull(dir);
        return dir!.FullName;
    }

    private static string Read(string relative) => File.ReadAllText(Path.Combine(Root(), relative)).Replace("\r\n", "\n");

    [Fact]
    public void Directory_build_targets_replaces_the_statistics_target_with_an_empty_one()
    {
        string targets = Read("Directory.Build.targets");
        // An empty, self-closing target of exactly that name: no body, no condition, no DependsOnTargets that could
        // bring the task back.
        Assert.Matches(new Regex(@"<Target\s+Name=""AvaloniaStats""\s*/>"), targets);
        Assert.DoesNotContain("AvaloniaStatsTask", targets, StringComparison.Ordinal);
    }

    [Fact]
    public void Every_building_workflow_sets_the_avalonia_opt_out_at_workflow_level()
    {
        string dir = Path.Combine(Root(), ".github", "workflows");
        int building = 0;
        foreach (string path in Directory.GetFiles(dir, "*.yml"))
        {
            string yml = File.ReadAllText(path).Replace("\r\n", "\n");
            // A workflow that runs the .NET SDK on the solution or a project (build, test, publish, pack or restore)
            // builds Avalonia projects or may.
            if (!Regex.IsMatch(yml, @"\bdotnet (build|test|publish|pack|restore)\b")) continue;
            building++;
            // Workflow-level env (column 0), before the jobs: block, so every job and step inherits it.
            int jobs = yml.IndexOf("\njobs:", StringComparison.Ordinal);
            Assert.True(jobs > 0, $"{Path.GetFileName(path)} has no jobs: block");
            string head = yml[..jobs];
            Assert.True(Regex.IsMatch(head, @"\nenv:\n(  [^\n]*\n)*  AVALONIA_TELEMETRY_OPTOUT: '1'\n"),
                $"{Path.GetFileName(path)} builds with dotnet but does not set AVALONIA_TELEMETRY_OPTOUT: '1' at workflow level");
        }
        // ci, codeql, crossval, publish and release build today; fewer means a workflow stopped being recognised.
        Assert.True(building >= 5, $"only {building} building workflow(s) found");
    }

    [Fact]
    public void Contributor_notes_say_build_telemetry_is_off()
    {
        string notes = Read("CONTRIBUTING.md");
        Assert.Contains("## Build telemetry", notes, StringComparison.Ordinal);
        Assert.Contains("AVALONIA_TELEMETRY_OPTOUT=1", notes, StringComparison.Ordinal);
        Assert.Contains("Directory.Build.targets", notes, StringComparison.Ordinal);
    }
}
