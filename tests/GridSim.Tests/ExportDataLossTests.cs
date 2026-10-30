using Xunit;

namespace GridSim.Tests;

// DATA LOSS fix (docs-session finding, 2026-10-04): `gridsim export <manifest.json>` used to take the manifest as the
// OUTPUT path and write the visualisation scene over it, destroying the user's case. These tests pin the fix:
//   1. an existing GridSim case manifest given to export is read as the CASE and left byte-for-byte untouched;
//   2. export refuses (exit 2) to write over an existing file that is not a scene it wrote earlier;
//   3. re-running export onto its own earlier scene is still allowed.
// Every test works on copies in a fresh temp folder, never on the repository's own files.
public class ExportDataLossTests
{
    // A fresh scratch folder per test, removed afterwards.
    private static string NewTempDir()
    {
        string dir = Path.Combine(Path.GetTempPath(), "gridsim-export-dataloss-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        return dir;
    }

    [Fact]
    public void An_existing_json_file_that_is_not_a_scene_is_never_overwritten()
    {
        string dir = NewTempDir();
        try
        {
            // Any JSON a user keeps (here a stand-in for a case, a schema or notes): not a scene, so it must survive.
            string keep = Path.Combine(dir, "my-notes.json");
            const string content = "{ \"note\": \"do not overwrite me\" }";
            File.WriteAllText(keep, content);

            CliProcess.Result r = CliProcess.RunBuilt("export", "case9", keep, "--frames", "2");

            Assert.True(r.ExitCode == 2, r.ToString());
            Assert.Contains("already exists and is not a gridviz scene; refusing to overwrite it", r.StdErr, StringComparison.Ordinal);
            Assert.Equal(content, File.ReadAllText(keep));
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    [Fact]
    public void Export_may_rewrite_its_own_earlier_scene()
    {
        string dir = NewTempDir();
        try
        {
            string scene = Path.Combine(dir, "scene.json");
            CliProcess.Result first = CliProcess.RunBuilt("export", "case9", scene, "--frames", "2");
            Assert.True(first.ExitCode == 0, first.ToString());

            // The same output path again: it now holds a scene, so the re-run is allowed.
            CliProcess.Result second = CliProcess.RunBuilt("export", "case9", scene, "--frames", "2");
            Assert.True(second.ExitCode == 0, second.ToString());
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    [Fact]
    public void A_case_manifest_argument_is_read_as_the_case_and_left_untouched()
    {
        string dir = NewTempDir();
        try
        {
            // Copy the v2 test fixtures (the manifest refers to its network file by a relative path) into scratch.
            string src = Path.Combine(AppContext.BaseDirectory, "fixtures", "v2");
            if (!Directory.Exists(src))
                src = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "fixtures", "v2"));
            Assert.True(Directory.Exists(src), $"v2 fixtures not found at {src}");
            foreach (string f in Directory.EnumerateFiles(src, "*", SearchOption.AllDirectories))
            {
                string to = Path.Combine(dir, Path.GetRelativePath(src, f));
                Directory.CreateDirectory(Path.GetDirectoryName(to)!);
                File.Copy(f, to);
            }
            string manifest = Path.Combine(dir, "cases", "good.case.json");
            byte[] before = File.ReadAllBytes(manifest);

            CliProcess.Result r = CliProcess.RunBuilt("export", manifest, "--frames", "2");

            // Whatever the load outcome, the manifest must be byte-for-byte unchanged and must not be the output.
            Assert.Equal(before, File.ReadAllBytes(manifest));
            Assert.DoesNotContain("-> " + Path.GetFullPath(manifest), r.StdOut, StringComparison.Ordinal);
        }
        finally { Directory.Delete(dir, recursive: true); }
    }
}
