using Xunit;

namespace GridSim.Tests;

/// <summary>
/// GR-423 (2026-11-01): the web image installs nothing beyond its digest-pinned base, and its health check needs no
/// installed tool. The runtime stage used to <c>apt-get install curl</c> (an unpinned package, whatever the archive held
/// at build time) only for the health check; the check now uses bash's built-in <c>/dev/tcp</c>, and compose runs the
/// very same probe. These pins stop curl (or any other package install) creeping back in, and stop the Dockerfile and
/// compose probes drifting apart. The digest pins on the FROM lines themselves are asserted by
/// <c>BugHunt1101BuildTests</c>.
/// </summary>
public class GA7E3Gr423ImagePinTests
{
    private static string Root()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "GridSim.slnx"))) dir = dir.Parent;
        Assert.NotNull(dir);
        return dir!.FullName;
    }

    private static string Read(string relative) => File.ReadAllText(Path.Combine(Root(), relative)).Replace("\r\n", "\n");

    // The probe: GET / with Host: localhost; healthy on a 2xx or 3xx status line, as `curl -fsS` was.
    private const string Probe =
        "exec 3<>/dev/tcp/127.0.0.1/8080 && printf \"GET / HTTP/1.0\\r\\nHost: localhost\\r\\n\\r\\n\" >&3 && " +
        "IFS= read -r -t 5 status <&3 && case \"$status\" in \"HTTP/1.\"[01]\" \"[23]*) exit 0 ;; *) exit 1 ;; esac";

    [Fact]
    public void The_runtime_image_installs_no_package_and_its_health_check_uses_no_installed_tool()
    {
        string docker = Read("Dockerfile");
        int runtime = docker.IndexOf(" AS runtime\n", StringComparison.Ordinal);
        Assert.True(runtime > 0, "the Dockerfile has no runtime stage");
        // Instructions only: the comments are allowed to say what the probe replaced.
        string instructions = string.Join("\n", docker[runtime..].Split('\n').Where(l => !l.TrimStart().StartsWith('#')));
        Assert.DoesNotContain("apt-get", instructions, StringComparison.Ordinal);
        Assert.DoesNotContain("curl", instructions, StringComparison.Ordinal);
        Assert.Contains("    CMD bash -c '" + Probe + "' || exit 1\n", docker, StringComparison.Ordinal);
    }

    [Fact]
    public void Compose_runs_the_same_probe_as_the_image()
    {
        // compose interpolates $NAME, so a literal $ is written $$ there; un-escaped it must equal the image's probe.
        string compose = Read("compose.yml");
        Assert.Contains("      test: [\"CMD\", \"bash\", \"-c\", '" + Probe.Replace("$", "$$", StringComparison.Ordinal) + "']\n",
            compose, StringComparison.Ordinal);
        Assert.DoesNotContain("\"curl\"", compose, StringComparison.Ordinal);
    }
}
