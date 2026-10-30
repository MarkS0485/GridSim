using System;
using System.Collections.Generic;
using System.IO;
using GridSim.Gda;
using Xunit;

namespace GridSim.Tests;

/// <summary>
/// D06 (Ruling R1, Mark delegated 2026-10-04): without <c>--root</c>, GridSim reads the lake root from
/// <c>GDA_DATA_ROOT</c> (the GDA estate's convention) first and <c>GDA_ROOT</c> second. A variable that is set but does
/// not name an existing folder is refused naming the variable, never printing its value, and never falling back.
/// The resolver takes the environment and the folder test as functions, so these tests never touch the machine's real
/// variables (which on Mark's machine are a private setting).
/// </summary>
public sealed class D06GdaRootEnvironmentTests
{
    private const string Secret = "Q:\\private\\lake-path-that-must-not-be-printed";

    private static string? Resolve(Dictionary<string, string?> env, ISet<string> folders, out string? variable) =>
        GdaRootEnvironment.Resolve(n => env.TryGetValue(n, out string? v) ? v : null, folders.Contains, out variable);

    [Fact]
    public void The_variable_names_are_the_ruled_ones()
    {
        Assert.Equal("GDA_DATA_ROOT", GdaRootEnvironment.Primary);
        Assert.Equal("GDA_ROOT", GdaRootEnvironment.Fallback);
    }

    [Fact]
    public void Neither_set_gives_null_so_the_verb_prints_usage()
    {
        Assert.Null(Resolve(new(), new HashSet<string>(), out string? v));
        Assert.Null(v);
        // Empty counts as unset, exactly as the CLI treated an empty GDA_ROOT before.
        Assert.Null(Resolve(new() { ["GDA_DATA_ROOT"] = "", ["GDA_ROOT"] = "" }, new HashSet<string>(), out v));
        Assert.Null(v);
    }

    [Fact]
    public void GDA_DATA_ROOT_wins_when_both_are_set_and_exist()
    {
        var env = new Dictionary<string, string?> { ["GDA_DATA_ROOT"] = "A", ["GDA_ROOT"] = "B" };
        Assert.Equal("A", Resolve(env, new HashSet<string> { "A", "B" }, out string? v));
        Assert.Equal("GDA_DATA_ROOT", v);
    }

    [Fact]
    public void GDA_ROOT_is_the_fallback_when_GDA_DATA_ROOT_is_unset_or_empty()
    {
        Assert.Equal("B", Resolve(new() { ["GDA_ROOT"] = "B" }, new HashSet<string> { "B" }, out string? v));
        Assert.Equal("GDA_ROOT", v);
        Assert.Equal("B", Resolve(new() { ["GDA_DATA_ROOT"] = "", ["GDA_ROOT"] = "B" }, new HashSet<string> { "B" }, out v));
        Assert.Equal("GDA_ROOT", v);
    }

    [Fact]
    public void A_set_but_missing_GDA_DATA_ROOT_is_refused_by_name_without_its_value_and_without_falling_back()
    {
        var env = new Dictionary<string, string?> { ["GDA_DATA_ROOT"] = Secret, ["GDA_ROOT"] = "B" };
        var ex = Assert.Throws<DirectoryNotFoundException>(() => Resolve(env, new HashSet<string> { "B" }, out _));
        Assert.Contains("GDA_DATA_ROOT is set but does not name an existing folder", ex.Message, StringComparison.Ordinal);
        Assert.Contains("does not fall back to GDA_ROOT", ex.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(Secret, ex.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("private", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_set_but_missing_GDA_ROOT_is_refused_by_name_without_its_value()
    {
        var ex = Assert.Throws<DirectoryNotFoundException>(() =>
            Resolve(new() { ["GDA_ROOT"] = Secret }, new HashSet<string>(), out _));
        Assert.Contains("GDA_ROOT is set but does not name an existing folder", ex.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("GDA_DATA_ROOT", ex.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(Secret, ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Null_delegates_are_refused()
    {
        Assert.Throws<ArgumentNullException>(() => GdaRootEnvironment.Resolve(null!, _ => true, out _));
        Assert.Throws<ArgumentNullException>(() => GdaRootEnvironment.Resolve(_ => null, null!, out _));
    }

    [Fact]
    public void The_cli_and_the_lake_tool_read_both_variables_in_the_ruled_order()
    {
        string root = PresentationConstantsAntiDriftTests.RepoRoot();
        string program = File.ReadAllText(Path.Combine(root, "src", "GridSim.Cli", "Program.cs"));
        int gda = program.IndexOf("void RunGda(List<string> a)", StringComparison.Ordinal);
        Assert.True(gda >= 0);
        string body = program.Substring(gda, 2000);
        Assert.Contains("GdaRootEnvironment.Resolve(out _)", body, StringComparison.Ordinal);
        Assert.DoesNotContain("GetEnvironmentVariable(\"GDA_ROOT\")", body, StringComparison.Ordinal);

        string lake = File.ReadAllText(Path.Combine(root, "tools", "crossval", "lake_conformance.py"));
        int primary = lake.IndexOf("os.environ.get(\"GDA_DATA_ROOT\")", StringComparison.Ordinal);
        int fallback = lake.IndexOf("os.environ.get(\"GDA_ROOT\")", StringComparison.Ordinal);
        Assert.True(primary >= 0 && fallback > primary);
        Assert.Contains("value not printed", lake, StringComparison.Ordinal);
    }

    [Fact]
    public void The_gda_verb_refuses_a_missing_GDA_DATA_ROOT_with_exit_1_naming_only_the_variable()
    {
        string missing = Path.Combine(Path.GetTempPath(), "gridsim-d06-missing-" + Guid.NewGuid().ToString("N"));
        var r = CliProcess.RunBuiltWithEnvironment(new Dictionary<string, string?>
        {
            ["GDA_DATA_ROOT"] = missing,
            ["GDA_ROOT"] = null,
        }, "gda");
        Assert.Equal(1, r.ExitCode);
        Assert.Contains("GDA root error: the environment variable GDA_DATA_ROOT is set but does not name an existing folder",
            r.StdErr, StringComparison.Ordinal);
        Assert.DoesNotContain(missing, r.StdErr + r.StdOut, StringComparison.Ordinal);
    }

    [Fact]
    public void The_gda_verb_with_no_root_and_no_variable_still_prints_usage_and_exits_2()
    {
        var r = CliProcess.RunBuiltWithEnvironment(new Dictionary<string, string?>
        {
            ["GDA_DATA_ROOT"] = null,
            ["GDA_ROOT"] = null,
        }, "gda");
        Assert.Equal(2, r.ExitCode);
        // GR-687: a usage banner for a missing required argument is an error report, so it now goes to stderr (it used
        // to go to stdout, where it mixed into piped output and was lost when only stderr was captured). Not on stdout.
        Assert.Contains("usage: gridsim gda --root <path-to-/gda>", r.StdErr, StringComparison.Ordinal);
        Assert.DoesNotContain("usage: gridsim gda", r.StdOut, StringComparison.Ordinal);
    }
}
