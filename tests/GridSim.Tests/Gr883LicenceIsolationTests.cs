using System;
using System.Collections.Generic;
using System.IO;
using GridSim.Core.Licensing;
using Xunit;

namespace GridSim.Tests;

/// <summary>
/// GR-883 (November 2026): the CLI child processes the tests start used to inherit GRIDSIM_LICENCE and the per-user
/// licence file, so a CLI test could pass on a licensed machine and fail on a clean one. <see cref="LicenceGate"/> now
/// honours GRIDSIM_LICENCE_FILE (the licence file to read and install to), and <see cref="CliProcess"/> strips
/// GRIDSIM_LICENCE and points GRIDSIM_LICENCE_FILE into the run's own temporary folder unless a test opts in to the
/// machine's licence. These tests pin the path rule in-process (without touching the test process's environment) and
/// the isolation through the built CLI.
/// </summary>
public sealed class Gr883LicenceIsolationTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), $"gridsim-gr883-{Guid.NewGuid():N}");

    public Gr883LicenceIsolationTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); }
        catch (IOException) { /* best effort: a leftover temp folder is not a test failure */ }
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void An_unset_or_blank_file_variable_means_the_per_user_file(string? value)
    {
        Assert.Equal(LicenceGate.DefaultLicenceFilePath, LicenceGate.LicenceFilePathFor(value));
        Assert.Equal(Path.Combine("GridSim", "licence" + LicenceGate.FileExtension),
            Path.Combine(Path.GetFileName(Path.GetDirectoryName(LicenceGate.DefaultLicenceFilePath)!), Path.GetFileName(LicenceGate.DefaultLicenceFilePath)));
    }

    [Fact]
    public void A_set_file_variable_is_used_as_a_full_path_with_surrounding_space_removed()
    {
        string file = Path.Combine(_dir, "my.gridsim");
        Assert.Equal(file, LicenceGate.LicenceFilePathFor(file));
        Assert.Equal(file, LicenceGate.LicenceFilePathFor("  " + file + "  "));
        Assert.Equal(Path.GetFullPath("rel-licence.gridsim"), LicenceGate.LicenceFilePathFor("rel-licence.gridsim"));
    }

    [Fact]
    public void The_variable_name_is_the_documented_one()
    {
        Assert.Equal("GRIDSIM_LICENCE_FILE", LicenceGate.FileEnvVar);
    }

    // ---- through the built CLI ----

    [Fact]
    public void By_default_a_cli_child_reads_a_licence_file_in_its_own_temporary_folder()
    {
        CliProcess.Result r = CliProcess.RunBuilt("licence", "path");
        Assert.True(r.ExitCode == 0, r.ToString());
        string path = r.StdOut.Trim();
        Assert.Equal(CliProcess.IsolatedLicenceFileName, Path.GetFileName(path));
        Assert.StartsWith(Path.GetFullPath(Path.GetTempPath()), path, StringComparison.OrdinalIgnoreCase);
        Assert.NotEqual(LicenceGate.DefaultLicenceFilePath, path);
    }

    [Fact]
    public void By_default_a_cli_child_runs_at_the_free_tier_whatever_licence_the_machine_holds()
    {
        // The isolation removes GRIDSIM_LICENCE whatever the parent holds; no key from any source means no source note.
        CliProcess.Result r = CliProcess.RunBuilt("licence", "status");
        Assert.True(r.ExitCode == 0, r.ToString());
        Assert.Contains("1,500 buses", r.StdOut, StringComparison.Ordinal);
        Assert.DoesNotContain("rejected", r.StdOut, StringComparison.Ordinal);
        Assert.DoesNotContain(LicenceGate.DefaultLicenceFilePath, r.StdOut, StringComparison.Ordinal);
    }

    [Fact]
    public void A_licence_file_named_by_the_variable_is_the_one_read()
    {
        // A malformed key in the named file is read and rejected by name, which shows the file was the one consulted.
        string file = Path.Combine(_dir, "other.gridsim");
        File.WriteAllText(file, "# test licence\nGSK1 not-a-real-key\n");
        var env = new Dictionary<string, string?> { ["GRIDSIM_LICENCE_FILE"] = file };

        CliProcess.Result r = CliProcess.RunBuiltWithEnvironment(env, "licence", "status");

        Assert.True(r.ExitCode == 0, r.ToString());
        Assert.Contains("licence key from licence.gridsim rejected", r.StdOut, StringComparison.Ordinal);
        Assert.Contains($"licence file: {file}", r.StdOut, StringComparison.Ordinal);
    }

    [Fact]
    public void A_caller_override_of_GRIDSIM_LICENCE_still_reaches_the_child()
    {
        // The isolation is applied before the caller's own overrides, so a test that forces a key still can.
        var env = new Dictionary<string, string?> { ["GRIDSIM_LICENCE"] = "not-a-key" };

        CliProcess.Result r = CliProcess.RunBuiltWithEnvironment(env, "licence", "status");

        Assert.True(r.ExitCode == 0, r.ToString());
        Assert.Contains("licence key from GRIDSIM_LICENCE rejected", r.StdOut, StringComparison.Ordinal);
    }
}
