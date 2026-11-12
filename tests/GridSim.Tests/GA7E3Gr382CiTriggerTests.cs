using Xunit;

namespace GridSim.Tests;

/// <summary>
/// GR-382 (2026-11-01, owner decision): ci.yml also runs on every push to main and once a week, so main's state has a
/// CI result between releases, and every job is guarded so those runs happen only on the public repository (free
/// Actions minutes) or when started by hand. A push to the private development repository must start nothing. It still
/// has no tag trigger of its own (the release calls it), which WorkflowReleaseGateTests pins.
/// </summary>
public class GA7E3Gr382CiTriggerTests
{
    private const string Guard = "github.repository == 'MarkS0485/GridSim' || github.event_name == 'workflow_dispatch'";

    private static string Root()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "GridSim.slnx"))) dir = dir.Parent;
        Assert.NotNull(dir);
        return dir!.FullName;
    }

    private static string Ci() => File.ReadAllText(Path.Combine(Root(), ".github", "workflows", "ci.yml")).Replace("\r\n", "\n");

    [Fact]
    public void Ci_runs_on_pushes_to_main_and_weekly_as_well_as_when_called_or_started_by_hand()
    {
        string ci = Ci();
        Assert.Contains("on:\n  workflow_call:\n  workflow_dispatch:\n  push:\n    branches: [main]\n  schedule:\n    - cron: '17 3 * * 1'", ci,
            StringComparison.Ordinal);
        Assert.DoesNotContain("tags:", ci, StringComparison.Ordinal);
        Assert.DoesNotContain("\n  pull_request:", ci, StringComparison.Ordinal);   // a trigger key, not the comment that names it
    }

    [Fact]
    public void Every_ci_job_is_guarded_to_the_public_repository_or_a_run_by_hand()
    {
        string ci = Ci();
        string jobs = ci[ci.IndexOf("\njobs:\n", StringComparison.Ordinal)..];
        // Each job header is a two-space-indented key directly under jobs:. Every one must carry the guard.
        string[] lines = jobs.Split('\n');
        int jobCount = 0;
        for (int i = 2; i < lines.Length; i++)
        {
            string l = lines[i];
            if (l.Length > 2 && l[0] == ' ' && l[1] == ' ' && l[2] != ' ' && l[2] != '#' && l.TrimEnd().EndsWith(':'))
            {
                jobCount++;
                // The job's own block runs to the next two-space key.
                int end = i + 1;
                while (end < lines.Length && !(lines[end].Length > 2 && lines[end][0] == ' ' && lines[end][1] == ' ' && lines[end][2] != ' ' && lines[end][2] != '#'))
                    end++;
                string block = string.Join("\n", lines[i..end]);
                Assert.True(block.Contains("    if: " + Guard, StringComparison.Ordinal)
                            || block.Contains("    if: always() && (" + Guard + ")", StringComparison.Ordinal),
                    $"ci.yml job '{l.Trim()}' is not guarded to the public repository or a run by hand");
            }
        }
        Assert.Equal(4, jobCount);   // matrix, vulnerable, container and the "Build & test" summary
    }
}
