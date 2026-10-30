using System;
using System.IO;
using GridSim.Core.IO;
using GridSim.Core.Model;
using GridSim.Core.PowerFlow;
using GridSim.Core.Protection;
using Xunit;

namespace GridSim.Tests;

/// <summary>
/// R095: ProtectionAssessment.AssessThreePhaseFault is reachable from the CLI as `protection-check --fault-bus ID
/// [--zf pu]`. The Core call the CLI makes is checked directly (settings honoured, fault impedance honoured), and the
/// CLI wiring at source level (the test project does not reference the CLI): the call, the usage-error refusals and
/// the fail-loud converter check.
/// </summary>
public sealed class ProtectionCheckFaultCliTests
{
    private static (GridModel M, PowerFlowResult Pre) Case9()
    {
        GridModel m = IeeeCases.Case9();
        PowerFlowResult pre = new NewtonRaphsonPowerFlow().Solve(m, new PowerFlowOptions { MaxIterations = 100, EnforceQLimits = true });
        Assert.True(pre.Converged);
        return (m, pre);
    }

    [Fact]
    public void The_cli_call_assesses_the_faulted_state_with_the_given_settings()
    {
        (GridModel m, PowerFlowResult pre) = Case9();
        // The CLI's own call shape: the prefault result, the bus, the fault impedance and the settings record.
        var settings = new ProtectionSettings() with { UnderVoltagePu = 0.85 };
        (FaultResult fault, ProtectionReport rep) = ProtectionAssessment.AssessThreePhaseFault(m, pre, 7, 0.0, settings);
        Assert.True(fault.FaultCurrentPu > 0.0);
        Assert.Same(settings, rep.Settings);
        // A bolted fault operates elements; the steady state on the same case does not operate more of them.
        Assert.NotEmpty(rep.Operating);
        Assert.True(ProtectionAssessment.Assess(m, pre, settings).Operating.Count <= rep.Operating.Count);
    }

    [Fact]
    public void A_fault_impedance_lowers_the_fault_current()
    {
        (GridModel m, PowerFlowResult pre) = Case9();
        double bolted = ProtectionAssessment.AssessThreePhaseFault(m, pre, 7, 0.0).Fault.FaultCurrentPu;
        double through = ProtectionAssessment.AssessThreePhaseFault(m, pre, 7, 0.5).Fault.FaultCurrentPu;
        Assert.True(through < bolted, $"Zf 0.5 pu gave {through:R} pu, bolted {bolted:R} pu");
    }

    private static string CliSource()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "GridSim.slnx"))) dir = dir.Parent;
        Assert.True(dir is not null, $"GridSim.slnx not found above {AppContext.BaseDirectory}");
        return File.ReadAllText(Path.Combine(dir!.FullName, "src", "GridSim.Cli", "Program.cs")).Replace("\r\n", "\n");
    }

    [Fact]
    public void Cli_protection_check_fault_bus_is_wired_with_its_exit_codes()
    {
        string src = CliSource();
        foreach (string flag in new[] { "--fault-bus", "--zf" })
            Assert.Contains($"    \"{flag}\",\n", src);   // in the known-flags gate
        // Test changed 2026-10-19: protection-check now runs the two steps of AssessThreePhaseFault itself (fault study,
        // then Assess only when the converter iteration converged), so a non-converged iteration exits 3, not 1.
        Assert.Contains("GridSim.Core.Protection.FaultAnalysis.ThreePhase(m, r, fb, faultZPu)", src);
        Assert.Contains("relays: not assessed", src);
        Assert.Contains("[--cascade [--alarms <soe.csv>] | --fault-bus ID [--zf pu]]", src);
        Assert.Contains("error: --zf is the fault impedance of a --fault-bus study; add --fault-bus.", src);
        Assert.Contains("error: --cascade follows the steady solved state; it does not combine with --fault-bus.", src);
        Assert.Contains("protection-check: fault bus {fbCheck} is not in case '{m.Name}'.", src);
        Assert.Contains("protection-check: the converter current iteration of the fault study did not converge", src);
    }
}
