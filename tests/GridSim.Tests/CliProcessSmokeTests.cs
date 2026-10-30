using GridSim.Core;
using Xunit;

namespace GridSim.Tests;

/// <summary>
/// F11 (release register section 6): end-to-end CLI smoke tests. Until now every Cli*ContractTests class read
/// Program.cs as text; these start the real gridsim binary as a separate process and check what a user or a script
/// actually sees: the exit code (0 ok, 1 input error, 2 usage, 3 non-convergence or validation failure), the key line
/// of output, and the error message for the common mistakes. Every verb run here uses the bundled IEEE 9-bus case and
/// finishes in well under a second.
///
/// Deliberately NOT pinned here: the exit codes of the contingency verbs (nminus1, nminus2, n-1-1, screen, domains)
/// and of `losses` on an unknown case, which the S1 stream (F01) is changing in parallel.
/// </summary>
public sealed class CliProcessSmokeTests
{
    private static CliProcess.Result Run(params string[] args) => CliProcess.RunBuilt(args);

    [Fact]
    public void Version_prints_the_build_stamp_and_exits_0()
    {
        CliProcess.Result r = Run("--version");
        Assert.True(r.ExitCode == 0, r.ToString());
        Assert.Equal($"gridsim {BuildInfo.InformationalVersion}", r.StdOut.Trim());
    }

    [Fact]
    public void Version_is_answered_before_the_unknown_flag_gate()
    {
        CliProcess.Result r = Run("--no-such-flag", "--version");
        Assert.True(r.ExitCode == 0, r.ToString());
        Assert.StartsWith("gridsim ", r.StdOut, StringComparison.Ordinal);
    }

    [Fact]
    public void Help_exits_0_and_lists_the_verb_groups()
    {
        CliProcess.Result r = Run("--help");
        Assert.True(r.ExitCode == 0, r.ToString());
        Assert.Contains("contingency:", r.StdOut, StringComparison.Ordinal);
        Assert.Contains("--version", r.StdOut, StringComparison.Ordinal);
    }

    [Fact]
    public void Unknown_flag_is_a_usage_error_exit_2()
    {
        CliProcess.Result r = Run("case9", "--once", "--no-such-flag");
        Assert.True(r.ExitCode == 2, r.ToString());
        Assert.Contains("unknown flag '--no-such-flag'", r.StdErr, StringComparison.Ordinal);
    }

    [Fact]
    public void Unknown_verb_or_case_is_a_usage_error_exit_2()
    {
        CliProcess.Result r = Run("no-such-verb");
        Assert.True(r.ExitCode == 2, r.ToString());
        Assert.Contains("unknown command or case 'no-such-verb'", r.StdErr, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("--mpc", "does-not-exist.m", "--once")]
    [InlineData("does-not-exist.m", "--once")]
    public void Missing_case_file_is_an_input_error_exit_1_not_a_crash(params string[] args)
    {
        CliProcess.Result r = Run(args);
        Assert.True(r.ExitCode == 1, r.ToString());
        Assert.Contains("not found", r.StdErr, StringComparison.Ordinal);
        Assert.DoesNotContain("Unhandled exception", r.StdErr, StringComparison.Ordinal);
        Assert.DoesNotContain("internal error", r.StdErr, StringComparison.Ordinal);
    }

    [Fact]
    public void Missing_case_folder_is_an_input_error_exit_1()
    {
        CliProcess.Result r = Run("--dir", "no-such-folder", "--once");
        Assert.True(r.ExitCode == 1, r.ToString());
        Assert.DoesNotContain("Unhandled exception", r.StdErr, StringComparison.Ordinal);
    }

    [Fact]
    public void Case9_solves_once_and_exits_0()
    {
        CliProcess.Result r = Run("case9", "--once");
        Assert.True(r.ExitCode == 0, r.ToString());
        Assert.Contains("IEEE 9-bus (WSCC)", r.StdOut, StringComparison.Ordinal);
        Assert.Contains("Converged (", r.StdOut, StringComparison.Ordinal);
    }

    [Fact]
    public void Case9_report_is_identical_on_a_second_run()
    {
        // Determinism through the whole process: the same command prints byte-for-byte the same report.
        CliProcess.Result a = Run("case9", "--once");
        CliProcess.Result b = Run("case9", "--once");
        Assert.Equal(0, a.ExitCode);
        Assert.Equal(a.StdOut, b.StdOut);
    }

    /// <summary>Each verb on case9: exit 0 and the line that proves it ran the analysis it names.</summary>
    [Theory]
    [InlineData("Physics-law audit", "physics-check", "case9")]
    [InlineData("PTDF - IEEE 9-bus (WSCC)", "ptdf", "case9", "--bus", "1")]
    [InlineData("LODF - IEEE 9-bus (WSCC)", "lodf", "case9", "--branch", "0")]
    [InlineData("DC-OPF - IEEE 9-bus (WSCC)", "dcopf", "case9")]
    // Moved 2026-10 (F15, authorised by Mark's 2026-10-02 best-judgement ruling): this row was written by S8 against the
    // old acopf header "AC-OPF - IEEE 9-bus (WSCC)"; F15 (1.95.114) relabels it honestly as
    // "ACOPF (DC dispatch + AC loss loop) - IEEE 9-bus (WSCC) ..." (the verb is a DC dispatch with a loss loop, not an AC-OPF).
    [InlineData("ACOPF (DC dispatch + AC loss loop) - IEEE 9-bus (WSCC)", "acopf", "case9")]
    [InlineData("max loadability", "cpf", "case9")]
    [InlineData("fault current:", "fault", "case9", "--bus", "5")]
    [InlineData("worst voltage THD", "harmonics", "case9", "--synth-sources")]
    [InlineData("status: Converged", "estimate", "case9", "--synth")]
    [InlineData("Protection assessment", "protection-check", "case9")]
    [InlineData("TRANSIENT STABILITY", "stability", "case9")]
    [InlineData("SMALL-SIGNAL / MODAL STABILITY", "modal", "case9")]
    [InlineData("Total losses:", "losses", "case9")]
    [InlineData("Tripped:", "trip", "case9")]
    [InlineData("SIMD", "simdcheck")]
    [InlineData("licence file:", "licence", "status")]
    public void Verb_runs_on_case9_and_exits_0(string expected, params string[] args)
    {
        CliProcess.Result r = Run(args);
        Assert.True(r.ExitCode == 0, r.ToString());
        Assert.Contains(expected, r.StdOut, StringComparison.Ordinal);
    }

    /// <summary>A verb called without its required flag is a usage error (exit 2) that prints the usage line.</summary>
    [Theory]
    [InlineData("iec60909", "case9")]
    [InlineData("grade", "case9")]
    public void Verb_missing_its_required_flag_exits_2_with_usage(params string[] args)
    {
        CliProcess.Result r = Run(args);
        Assert.True(r.ExitCode == 2, r.ToString());
        Assert.Contains("usage: gridsim", r.StdErr, StringComparison.Ordinal);
    }

    [Fact]
    public void Taps_on_a_case_without_tap_changers_is_an_input_error_exit_1()
    {
        CliProcess.Result r = Run("taps", "case9");
        Assert.True(r.ExitCode == 1, r.ToString());
        Assert.Contains("no on-load tap changers", r.StdErr, StringComparison.Ordinal);
    }
}
