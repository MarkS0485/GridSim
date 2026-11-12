using System;
using System.IO;
using Xunit;

namespace GridSim.Tests;

/// <summary>
/// GR-403 (November 2026): CliExitCodeContractTests check the exit-code convention by reading Program.cs (a new verb
/// that breaks it is caught, but a wrong code at run time is not, and a refactor that keeps the behaviour but moves the
/// text breaks them). These run the BUILT CLI on a case that cannot converge (case9 with ten times its load, the
/// fixture of D-DOC-012) and check what a script sees: exit 3 (validation failure, user manual ch. 19) and the message,
/// for each behaviour the source-text tests pin. The source-text tests are kept beside them.
/// </summary>
public sealed class Gr403CliExitCodeBehaviourTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "gridsim-gr403-" + Guid.NewGuid().ToString("N"));

    public Gr403CliExitCodeBehaviourTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); }
        catch (IOException) { /* best effort */ }
        catch (UnauthorizedAccessException) { /* best effort */ }
    }

    private static void AssertExit(int expected, CliProcess.Result r) => Assert.True(r.ExitCode == expected, r.ToString());

    /// <summary>The shipped case9 with every load multiplied by ten: no power-flow solution exists.</summary>
    private string Case9TimesTen()
    {
        string text = File.ReadAllText(Path.Combine(CliProcess.Root(), "data", "matpower", "lib", "case9.m"));
        foreach ((string from, string to) in new[]
                 {
                     ("\t5\t1\t90\t30\t", "\t5\t1\t900\t300\t"),
                     ("\t7\t1\t100\t35\t", "\t7\t1\t1000\t350\t"),
                     ("\t9\t1\t125\t50\t", "\t9\t1\t1250\t500\t"),
                 })
        {
            Assert.True(text.Contains(from, StringComparison.Ordinal), $"case9.m no longer holds '{from}'");
            text = text.Replace(from, to, StringComparison.Ordinal);
        }
        string path = Path.Combine(_dir, "case9-x10.m");
        File.WriteAllText(path, text);
        return path;
    }

    /// <summary>Every_DID_NOT_CONVERGE_print_sets_exit_3_nearby, run: the default report says DID NOT CONVERGE and exits 3.</summary>
    [Fact]
    public void The_default_report_of_a_non_converging_case_exits_3()
    {
        CliProcess.Result r = CliProcess.RunBuilt(Case9TimesTen(), "--once");
        AssertExit(3, r);
        Assert.Contains("DID NOT CONVERGE", r.StdOut, StringComparison.Ordinal);
    }

    /// <summary>The_default_solve_report_skips_protection_on_a_non_converged_state, run: the protection overlay is skipped
    /// with its message, and the verb neither crashes nor prints a protection verdict.</summary>
    [Fact]
    public void The_default_report_skips_protection_on_a_non_converged_state()
    {
        CliProcess.Result r = CliProcess.RunBuilt(Case9TimesTen(), "--once");
        AssertExit(3, r);
        Assert.Contains("Protection simulation: skipped - the power flow did not converge", r.StdOut, StringComparison.Ordinal);
        Assert.DoesNotContain("Unhandled exception", r.StdErr, StringComparison.Ordinal);
    }

    /// <summary>Fault_and_protection_check_refuse_a_non_converged_state, run (fault).</summary>
    [Fact]
    public void Fault_refuses_a_non_converged_prefault_state_with_exit_3()
    {
        CliProcess.Result r = CliProcess.RunBuilt("fault", Case9TimesTen(), "--bus", "5");
        AssertExit(3, r);
        Assert.Contains("fault: the pre-fault power flow did not converge", r.StdErr, StringComparison.Ordinal);
    }

    /// <summary>Fault_and_protection_check_refuse_a_non_converged_state, run (protection-check).</summary>
    [Fact]
    public void Protection_check_refuses_a_non_converged_state_with_exit_3()
    {
        CliProcess.Result r = CliProcess.RunBuilt("protection-check", Case9TimesTen());
        AssertExit(3, r);
        Assert.Contains("protection-check: the power flow did not converge", r.StdErr, StringComparison.Ordinal);
    }

    /// <summary>The pairing: the same verbs on the shipped case9 converge and do not exit 3, so the code above is the
    /// non-convergence's and not the verb's.</summary>
    [Fact]
    public void The_same_verbs_on_case9_do_not_exit_3()
    {
        CliProcess.Result report = CliProcess.RunBuilt("case9", "--once");
        AssertExit(0, report);
        Assert.DoesNotContain("DID NOT CONVERGE", report.StdOut, StringComparison.Ordinal);
        CliProcess.Result fault = CliProcess.RunBuilt("fault", "case9", "--bus", "5");
        Assert.True(fault.ExitCode != 3, fault.ToString());
        Assert.DoesNotContain("did not converge", fault.StdErr, StringComparison.Ordinal);
        CliProcess.Result prot = CliProcess.RunBuilt("protection-check", "case9");
        Assert.True(prot.ExitCode != 3, prot.ToString());
        Assert.DoesNotContain("did not converge", prot.StdErr, StringComparison.Ordinal);
    }
}
