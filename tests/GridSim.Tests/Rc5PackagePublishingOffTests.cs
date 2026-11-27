using Xunit;

namespace GridSim.Tests;

/// <summary>
/// The NuGet package and the two container images (web, CLI) are a roadmap item: the groundwork is built on every
/// release but nothing is pushed, pending broader validation, and shipping GridSim as a consumable library also needs a
/// safe way to handle licences. These tests read the two workflow files and pin that state: every push sits in a job
/// that runs only when the repository variable GRIDSIM_PUBLISH_PACKAGES is 'true' (it is unset) and never for a release
/// candidate, and only those jobs hold the registry write permission. They read the files; they do not run a workflow.
/// </summary>
public class Rc5PackagePublishingOffTests
{
    private const string Switch = "vars.GRIDSIM_PUBLISH_PACKAGES == 'true'";

    private static string Root()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "GridSim.slnx"))) dir = dir.Parent;
        Assert.NotNull(dir);
        return dir!.FullName;
    }

    private static string Read(string relative) => File.ReadAllText(Path.Combine(Root(), relative)).Replace("\r\n", "\n", StringComparison.Ordinal);

    // The body of every job under the top-level `jobs:` key, by job id. Comment lines are dropped, so a mention in a
    // comment can neither satisfy nor break a check (a comment that sits between two jobs would otherwise land in the
    // earlier one).
    private static Dictionary<string, string[]> Jobs(string yml)
    {
        int start = yml.IndexOf("\njobs:\n", StringComparison.Ordinal);
        Assert.True(start >= 0, "the workflow has no jobs: key");
        var jobs = new Dictionary<string, List<string>>();
        List<string>? current = null;
        foreach (string line in yml[(start + 1)..].Split('\n').Skip(1))
        {
            if (line.StartsWith("  ", StringComparison.Ordinal) && line.Length > 3 && line[2] != ' ' && line[2] != '#' && line.EndsWith(':'))
            {
                current = new List<string>();
                jobs[line.Trim().TrimEnd(':')] = current;
                continue;
            }
            if (line.TrimStart().StartsWith('#')) continue;
            current?.Add(line);
        }
        return jobs.ToDictionary(kv => kv.Key, kv => kv.Value.ToArray());
    }

    private static string[] JobsWith(Dictionary<string, string[]> jobs, Func<string, bool> lineTest) =>
        jobs.Where(kv => kv.Value.Any(lineTest)).Select(kv => kv.Key).OrderBy(k => k, StringComparer.Ordinal).ToArray();

    private static bool Has(string[] body, string prefix) => body.Any(l => l.TrimStart().StartsWith(prefix, StringComparison.Ordinal));

    [Fact]
    public void Only_the_gated_jobs_of_the_release_workflow_hold_the_registry_write_permission()
    {
        Dictionary<string, string[]> jobs = Jobs(Read(".github/workflows/release.yml"));
        // `nuget` only calls publish.yml: a called workflow cannot exceed its caller, so the ceiling is granted there, and the
        // one called job that uses it (push) is gated, below.
        Assert.Equal(new[] { "cli-image-push", "nuget", "web-image-push" }, JobsWith(jobs, l => l.TrimStart().StartsWith("packages: write", StringComparison.Ordinal)));
    }

    [Fact]
    public void Every_image_push_is_in_a_job_that_needs_the_switch_and_refuses_a_candidate()
    {
        string rel = Read(".github/workflows/release.yml");
        Dictionary<string, string[]> jobs = Jobs(rel);
        Assert.Equal(new[] { "cli-image-push", "web-image-push" }, JobsWith(jobs, l => l.TrimStart().StartsWith("docker push", StringComparison.Ordinal)));
        Assert.Equal(new[] { "cli-image-push", "web-image-push" }, JobsWith(jobs, l => l.Contains("docker login", StringComparison.Ordinal)));
        foreach (string job in new[] { "web-image-push", "cli-image-push" })
            Assert.Contains($"    if: needs.verify.outputs.prerelease == 'false' && {Switch}", jobs[job]);
        // The build jobs push nothing and hold no registry permission.
        foreach (string job in new[] { "web-image", "cli-image" })
        {
            Assert.DoesNotContain(jobs[job], l => l.Contains("docker push", StringComparison.Ordinal));
            Assert.DoesNotContain(jobs[job], l => l.Contains("docker login", StringComparison.Ordinal));
        }
    }

    [Fact]
    public void The_package_push_is_one_gated_job_in_publish_yml_and_the_pack_job_cannot_write_packages()
    {
        string pub = Read(".github/workflows/publish.yml");
        Dictionary<string, string[]> jobs = Jobs(pub);
        Assert.Equal(new[] { "push" }, JobsWith(jobs, l => l.Contains("dotnet nuget push", StringComparison.Ordinal)));
        Assert.Equal(new[] { "push" }, JobsWith(jobs, l => l.TrimStart().StartsWith("packages: write", StringComparison.Ordinal)));
        Assert.Contains($"    if: {Switch} && inputs.prerelease != true", jobs["push"]);
        Assert.Contains("    needs: publish", jobs["push"]);
        // The workflow-level permissions grant no package access either.
        int p = pub.IndexOf("\npermissions:\n", StringComparison.Ordinal);
        Assert.True(p >= 0);
        string top = string.Join('\n', pub[(p + 1)..].Split('\n').Skip(1).TakeWhile(l => l.StartsWith("  ", StringComparison.Ordinal)).Select(l => l.Split('#')[0]));
        Assert.DoesNotContain("packages", top, StringComparison.Ordinal);
        // The candidate flag arrives from the caller.
        Assert.Contains("      prerelease: ${{ needs.verify.outputs.prerelease == 'true' }}\n", Read(".github/workflows/release.yml"), StringComparison.Ordinal);
    }

    [Fact]
    public void The_groundwork_is_built_and_kept_as_artefacts_but_never_attached_to_the_release_page()
    {
        string rel = Read(".github/workflows/release.yml");
        Dictionary<string, string[]> jobs = Jobs(rel);
        // Both images are built from their own Dockerfile, probed, saved and uploaded under names that are not release-*.
        Assert.Contains(jobs["web-image"], l => l.Contains("docker build -t gridsim-web:release", StringComparison.Ordinal));
        Assert.Contains(jobs["cli-image"], l => l.Contains("docker build -f Dockerfile.cli -t gridsim-cli:release", StringComparison.Ordinal));
        Assert.Contains("          name: image-web   # not release-*", rel, StringComparison.Ordinal);
        Assert.Contains("          name: image-cli   # not release-*", rel, StringComparison.Ordinal);
        Assert.Contains("    needs: [ verify, ci, sbom ]", jobs["cli-image"]);
        Assert.Contains(jobs["cli-image"], l => l.Contains("compliance verify", StringComparison.Ordinal));
        // The package is packed, signed and attested on every run, and kept as an artefact.
        string pub = Read(".github/workflows/publish.yml");
        Assert.Contains("          name: package-core\n          path: artifacts/*.nupkg\n", pub, StringComparison.Ordinal);
        // The `nuget` job runs for a candidate too (it only packs there), so it carries no prerelease condition of its own.
        Assert.DoesNotContain(jobs["nuget"], l => l.TrimStart().StartsWith("if:", StringComparison.Ordinal));
    }

    [Fact]
    public void The_switch_is_only_ever_compared_never_given_a_value()
    {
        foreach (string wf in new[] { ".github/workflows/release.yml", ".github/workflows/publish.yml", ".github/workflows/ci.yml" })
        {
            foreach (string line in Read(wf).Split('\n').Where(l => !l.TrimStart().StartsWith('#') && l.Contains("GRIDSIM_PUBLISH_PACKAGES", StringComparison.Ordinal)))
                Assert.Contains(Switch, line, StringComparison.Ordinal);
        }
    }
}
