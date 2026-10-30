using GridSim.Cli;
using Xunit;

namespace GridSim.Tests;

/// <summary>
/// DOC-1 (October 2026): unit tests of the shared exit-code mapping in src/GridSim.Cli/CliExit.cs (compiled into this
/// project, see GridSim.Tests.csproj). The process-level tests in CliDoc1CrashExitCodeProcessTests run the real binary;
/// these drive the rules directly, including the failure branches no bundled case reaches from the command line
/// (a gpucheck probe that fails on a machine whose GPUs all work, or have none).
/// </summary>
public sealed class CliExitTests
{
    /// <summary>The codes are the ones manual ch. 19 documents; a script depends on the numbers, so they are pinned.</summary>
    [Fact]
    public void Codes_are_the_documented_numbers()
    {
        Assert.Equal(0, CliExit.Ok);
        Assert.Equal(1, CliExit.InputError);
        Assert.Equal(2, CliExit.Usage);
        Assert.Equal(3, CliExit.Verdict);
    }

    /// <summary>D-DOC-130: an unknown case name is a usage error (2); every other load failure an input error (1).</summary>
    [Fact]
    public void Case_load_failure_maps_unknown_name_to_2_and_everything_else_to_1()
    {
        Assert.Equal(2, CliExit.CaseLoadFailure(new UnknownCaseException("unknown case 'x'")));
        Assert.Equal(1, CliExit.CaseLoadFailure(new FileNotFoundException("missing")));
        Assert.Equal(1, CliExit.CaseLoadFailure(new DirectoryNotFoundException("missing")));
        Assert.Equal(1, CliExit.CaseLoadFailure(new InvalidDataException("bad content")));
        Assert.Equal(1, CliExit.CaseLoadFailure(new IOException("unreadable")));
        // A plain ArgumentException is not an unknown NAME (it is some other refusal of the content): input error.
        Assert.Equal(1, CliExit.CaseLoadFailure(new ArgumentException("bad value")));
        Assert.Throws<ArgumentNullException>(() => CliExit.CaseLoadFailure(null!));
    }

    /// <summary>D-DOC-130: UnknownCaseException stays an ArgumentException, so handlers that caught that still do.</summary>
    [Fact]
    public void Unknown_case_exception_is_still_an_argument_exception()
    {
        Exception ex = new UnknownCaseException("unknown case 'x' (built-in names: ...)");
        Assert.IsAssignableFrom<ArgumentException>(ex);
        Assert.Equal("unknown case 'x' (built-in names: ...)", ex.Message);
    }

    /// <summary>D-DOC-159: the C# parameter-name suffix is removed from an ArgumentException message, nothing else.</summary>
    [Fact]
    public void User_message_drops_the_parameter_name_suffix_only()
    {
        var withParam = new ArgumentException("fault bus 99 is not in the model", "faultBusId");
        Assert.Equal("fault bus 99 is not in the model (Parameter 'faultBusId')", withParam.Message);   // the runtime's form
        Assert.Equal("fault bus 99 is not in the model", CliExit.UserMessage(withParam));

        Assert.Equal("no parameter here", CliExit.UserMessage(new ArgumentException("no parameter here")));
        Assert.Equal("plain message", CliExit.UserMessage(new InvalidOperationException("plain message")));
        // Out-of-range: the same suffix rule (it derives from ArgumentException).
        Assert.Equal("Specified argument was out of the range of valid values.",
            CliExit.UserMessage(new ArgumentOutOfRangeException("capacity")));
        Assert.Throws<ArgumentNullException>(() => CliExit.UserMessage(null!));
    }

    /// <summary>D-DOC-011 / 012: a clean tally exits 0 and prints nothing.</summary>
    [Fact]
    public void Clean_tally_exits_0_with_no_summary()
    {
        var t = new FailureTally();
        Assert.Equal(0, t.ExitCode);
        Assert.Empty(t.SummaryLines("gpucheck"));
        Assert.Empty(t.LoadFailures);
        Assert.Empty(t.RunFailures);
    }

    /// <summary>D-DOC-011: a probe that ran and failed (the gpucheck FAIL line) makes the run exit 3, naming it.</summary>
    [Fact]
    public void Run_failure_exits_3_and_names_every_failure()
    {
        var t = new FailureTally();
        t.RunFailed("ROCm sparse threw DllNotFoundException");
        t.RunFailed("CUDA dense did not converge on case9");
        Assert.Equal(3, t.ExitCode);
        IReadOnlyList<string> lines = t.SummaryLines("gpucheck");
        Assert.Equal(2, lines.Count);
        // Sorted (ordinal), so the message is the same whatever order parallel workers recorded in.
        Assert.Equal("error: gpucheck: 2 part(s) failed: CUDA dense did not converge on case9; ROCm sparse threw DllNotFoundException.", lines[0]);
        Assert.Equal("error: gpucheck: exit 3 (1 when anything could not be loaded, otherwise 3 when anything failed).", lines[1]);
    }

    /// <summary>D-DOC-012: a load failure takes precedence over a failed solve (the batch verbs' rule): exit 1.</summary>
    [Fact]
    public void Load_failure_takes_precedence_exit_1()
    {
        var t = new FailureTally();
        t.RunFailed("case9 on cpu-dense: did not converge");
        t.LoadFailed("nosuchcase (cpu-dense)");
        Assert.Equal(1, t.ExitCode);
        IReadOnlyList<string> lines = t.SummaryLines("bench");
        Assert.Equal(3, lines.Count);
        Assert.Equal("error: bench: 1 part(s) could not be loaded: nosuchcase (cpu-dense).", lines[0]);
        Assert.Equal("error: bench: 1 part(s) failed: case9 on cpu-dense: did not converge.", lines[1]);
        Assert.StartsWith("error: bench: exit 1 ", lines[2], StringComparison.Ordinal);
    }

    /// <summary>D-DOC-012: the bench records from parallel workers; the tally is safe to fill concurrently.</summary>
    [Fact]
    public void Tally_is_safe_under_parallel_recording()
    {
        var t = new FailureTally();
        Parallel.For(0, 1000, i =>
        {
            if (i % 2 == 0) t.RunFailed("run " + i.ToString(System.Globalization.CultureInfo.InvariantCulture));
            else t.LoadFailed("load " + i.ToString(System.Globalization.CultureInfo.InvariantCulture));
        });
        Assert.Equal(500, t.RunFailures.Count);
        Assert.Equal(500, t.LoadFailures.Count);
        Assert.Equal(1, t.ExitCode);
    }
}
