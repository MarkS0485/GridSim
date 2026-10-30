using System.Text.RegularExpressions;
using Xunit;

namespace GridSim.Tests;

/// <summary>
/// R022 / R023 / R220 / R221 (October 2026 review): CLI verbs printed "DID NOT CONVERGE" and exited 0, ran fault and
/// protection studies on a non-converged state, and signalled non-convergence with three different exit codes.
/// The documented convention (user manual ch. 19) is exit 3 for a validation failure, which includes any reported
/// non-convergence. The test project does not reference the CLI, so these are source-level anti-drift checks over
/// src/GridSim.Cli: they fail if a new verb breaks the convention.
/// </summary>
public class CliExitCodeContractTests
{
    private static string CliSource()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "GridSim.slnx"))) dir = dir.Parent;
        Assert.True(dir is not null, $"GridSim.slnx not found above {AppContext.BaseDirectory}");
        string path = Path.Combine(dir!.FullName, "src", "GridSim.Cli", "Program.cs");
        Assert.True(File.Exists(path), $"CLI source not found: {path}");
        return File.ReadAllText(path).Replace("\r\n", "\n");
    }

    [Fact]
    public void Every_did_not_converge_message_exits_3()
    {
        // A 'did not converge' error line followed by an exit code must use 3, never 1 or 2.
        string src = CliSource();
        var wrong = Regex.Matches(src, @"did not converge[^\n]*?\);\s*Environment\.ExitCode\s*=\s*[^3\s][^;]*;")
            .Select(m => m.Value).ToList();
        Assert.True(wrong.Count == 0, "non-convergence with an exit code other than 3:\n" + string.Join("\n", wrong));
    }

    [Fact]
    public void Every_DID_NOT_CONVERGE_print_sets_exit_3_nearby()
    {
        // Each 'DID NOT CONVERGE' status print must be followed, within a few lines, by an exit-3 assignment
        // (R022: several verbs printed it and exited 0). The crossval verb's informational echo is exempt: it exports
        // the state for an external checker, which then judges it.
        string[] lines = CliSource().Split('\n');
        var offenders = new List<string>();
        for (int i = 0; i < lines.Length; i++)
        {
            if (!lines[i].Contains("DID NOT CONVERGE")) continue;
            if (lines[i].Contains("[crossval]")) continue;
            bool ok = false;
            for (int j = i; j < Math.Min(lines.Length, i + 4); j++)
                if (Regex.IsMatch(lines[j], @"Environment\.ExitCode\s*=\s*3")) { ok = true; break; }
            if (!ok) offenders.Add($"line {i + 1}: {lines[i].Trim()}");
        }
        Assert.True(offenders.Count == 0, "DID NOT CONVERGE without exit 3:\n" + string.Join("\n", offenders));
    }

    [Fact]
    public void Fault_and_protection_check_refuse_a_non_converged_state()
    {
        string src = CliSource();
        Assert.Contains("fault: the pre-fault power flow did not converge", src);
        Assert.Contains("protection-check: the power flow did not converge", src);
    }

    [Fact]
    public void The_default_solve_report_skips_protection_on_a_non_converged_state()
    {
        // Regression (found while closing WS4): the default '--once' report ran the protection overlay on whatever
        // state it had; with the convergence contract that threw and crashed the verb on a non-converging case.
        string src = CliSource();
        int report = src.IndexOf("void PrintDetailed(", StringComparison.Ordinal);
        Assert.True(report >= 0, "PrintDetailed not found");
        int assess = src.IndexOf("ProtectionAssessment.Assess(m, r)", report, StringComparison.Ordinal);
        int guard = src.IndexOf("Protection simulation: skipped - the power flow did not converge", report, StringComparison.Ordinal);
        Assert.True(guard >= 0 && assess > guard, "the protection overlay in PrintDetailed must be guarded by a convergence check");
    }
}
