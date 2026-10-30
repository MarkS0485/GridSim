using Xunit;

namespace GridSim.Tests;

/// <summary>
/// The `crossval` verb runs its pandapower sidecar from beside the executable. It used to look only at
/// &lt;executable folder&gt;/tools/crossval and five folders up (the source tree of a build), so a published release
/// folder, which carried no tools/ folder, could not run `crossval` at all. The build and the publish now copy
/// crossval.py and the sidecar_common.py it imports to &lt;executable folder&gt;/tools/crossval (GridSim.Cli.csproj);
/// the published-output check is in <see cref="PublishedOutputTests"/>.
/// </summary>
public sealed class CrossvalSidecarShippedTests
{
    private static readonly string[] Sidecar = { "crossval.py", "sidecar_common.py" };

    [Fact]
    public void The_built_cli_carries_the_sidecar_beside_it_byte_for_byte()
    {
        string exeDir = Path.GetDirectoryName(CliProcess.BuiltCliDll())!;
        foreach (string f in Sidecar)
        {
            string shipped = Path.Combine(exeDir, "tools", "crossval", f);
            string source = Path.Combine(CliProcess.Root(), "tools", "crossval", f);
            Assert.True(File.Exists(shipped), $"{shipped} missing: the build must copy the sidecar beside the executable");
            Assert.Equal(File.ReadAllBytes(source), File.ReadAllBytes(shipped));
        }
    }

    [Fact]
    public void Crossval_run_from_a_folder_outside_the_source_tree_finds_its_shipped_sidecar()
    {
        // A copy of the built CLI (its top-level files and the shipped tools/ folder, no data/) in a temporary folder,
        // the shape of a release folder: no source tree five levels up, so only the shipped copy can be found.
        string exeDir = Path.GetDirectoryName(CliProcess.BuiltCliDll())!;
        string tmp = Path.Combine(Path.GetTempPath(), "gridsim-crossval-ship-" + Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(tmp);
            foreach (string f in Directory.GetFiles(exeDir)) File.Copy(f, Path.Combine(tmp, Path.GetFileName(f)));
            Directory.CreateDirectory(Path.Combine(tmp, "tools", "crossval"));
            foreach (string f in Sidecar)
                File.Copy(Path.Combine(exeDir, "tools", "crossval", f), Path.Combine(tmp, "tools", "crossval", f));
            string case9 = Path.Combine(CliProcess.Root(), "data", "matpower", "lib", "case9.m");
            string outDir = Path.Combine(tmp, "out");
            // GRIDSIM_PYTHON naming nothing makes the run stop right after the sidecar lookup with a configuration error
            // (exit 1), so the test needs no Python: the message says whether the sidecar was found.
            var env = new Dictionary<string, string?> { ["GRIDSIM_PYTHON"] = Path.Combine(tmp, "no-such-python.exe") };
            string host = Environment.GetEnvironmentVariable("DOTNET_HOST_PATH") is { Length: > 0 } h ? h : "dotnet";
            CliProcess.Result r = CliProcess.Run(host, new[] { Path.Combine(tmp, "gridsim.dll"), "crossval", case9, "--out", outDir }, environment: env);
            string all = r.StdOut + r.StdErr;
            Assert.DoesNotContain("could not find tools/crossval/crossval.py", all, StringComparison.Ordinal);
            Assert.Contains("GRIDSIM_PYTHON", all, StringComparison.Ordinal);
            Assert.True(r.ExitCode == 1, r.ToString());
            Assert.True(File.Exists(Path.Combine(outDir, "solve-export.json")), r.ToString());
        }
        finally
        {
            try { Directory.Delete(tmp, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
    }
}
