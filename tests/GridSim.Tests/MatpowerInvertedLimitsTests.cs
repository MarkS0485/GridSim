using GridSim.Core.Abstractions;
using GridSim.Core.Analysis;
using GridSim.Core.IO.Matpower;
using GridSim.Core.Model;
using GridSim.Core.PowerFlow;
using Xunit;

namespace GridSim.Tests;

/// <summary>
/// RC4 large-case bug A (2.1.073): the PGLib-OPF <c>__api</c> cases carry generator rows with PMIN above PMAX (and
/// may carry QMIN above QMAX). MATPOWER, pandapower, VeraGrid and powsybl load and solve them, because a power flow
/// does not use OPF limits. The parser now loads such rows with a warning note; only an in-service unit's crossed
/// reactive limits (used by the power flow's Q-limit enforcement) are still refused, and every study that uses the
/// active-power range refuses a crossed one itself. Synthetic three-bus cases only (no MATPOWER data committed).
/// </summary>
public class MatpowerInvertedLimitsTests
{
    // Row 2 (bus 2, in service): PMIN 80 above PMAX 50. Row 3 (bus 3, out of service): QMIN 40 above QMAX 10 and
    // PMIN 30 above PMAX 5, as the PGLib __api rows are.
    private const string Crossed = @"
function mpc = crossed
mpc.baseMVA = 100;
mpc.bus = [
    1   3   0    0    0   0   1   1.0   0   230   1   1.1   0.9;
    2   2   0    0    0   0   1   1.0   0   230   1   1.1   0.9;
    3   1   90   30   0   0   1   1.0   0   230   1   1.1   0.9;
];
mpc.gen = [
    1   20   0   300   -300   1.02   100   1   300   0;
    2   40   0   300   -300   1.01   100   1   50    80;
    3   0    0   10    40     1.00   100   0   5     30;
];
mpc.branch = [
    1   2   0.01   0.10   0.02   250   0   0   0   0   1   -360   360;
    2   3   0.01   0.10   0.02   250   0   0   0   0   1   -360   360;
    1   3   0.02   0.20   0.04   250   0   0   0   0   1   -360   360;
];
";

    [Fact]
    public void Crossed_opf_limits_load_with_warnings_and_the_power_flow_runs()
    {
        MatpowerParseResult p = MatpowerParser.ParseWithDiagnostics(Crossed);
        Assert.Contains(p.Notes, n => n.StartsWith("warning: 2 generator row(s) have PMIN above PMAX (mpc.gen row(s) 2, 3);", StringComparison.Ordinal));
        Assert.Contains(p.Notes, n => n.StartsWith("warning: 1 out-of-service generator row(s) have QMIN above QMAX (mpc.gen row(s) 3);", StringComparison.Ordinal));

        // Loaded exactly as written: the limits are not swapped, clamped or dropped.
        Generator g2 = p.Model.Generators[1];
        Assert.Equal(80.0, g2.Pmin);
        Assert.Equal(50.0, g2.Pmax);
        Generator g3 = Assert.Single(p.Model.AllGenerators, g => !g.InService);
        Assert.Equal((40.0, 10.0, 30.0, 5.0), (g3.Qmin, g3.Qmax, g3.Pmin, g3.Pmax));

        Assert.DoesNotContain(p.Model.Validate(), d => d.Severity == ValidationSeverity.Error);
        foreach (IPowerFlowSolver engine in new IPowerFlowSolver[] { new NewtonRaphsonPowerFlow(), new SparseNewtonRaphsonPowerFlow() })
        {
            PowerFlowResult r = engine.Solve(p.Model, new PowerFlowOptions { Tolerance = 1e-8 });
            Assert.True(r.Converged, engine.GetType().Name);
            Assert.True(r.MaxMismatch <= 1e-8);
        }
    }

    [Fact]
    public void Crossed_reactive_limits_on_an_in_service_unit_are_still_refused()
    {
        // Row 3 back in service: its QMIN 40 / QMAX 10 would reach the Q-limit enforcement.
        string s = Crossed.Replace("3   0    0   10    40     1.00   100   0   5     30;", "3   0    0   10    40     1.00   100   1   5     30;", StringComparison.Ordinal);
        Assert.NotEqual(Crossed, s);
        var ex = Assert.Throws<InvalidDataException>(() => MatpowerParser.Parse(s));
        Assert.Contains("mpc.gen row 3 is in service and has QMIN above QMAX (Q 40 / 10)", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Studies_that_use_the_active_power_range_refuse_a_crossed_one()
    {
        GridModel m = MatpowerParser.Parse(Crossed);

        var dc = Assert.Throws<InvalidDataException>(() => DcOptimalPowerFlow.Solve(m));
        Assert.Contains("generator 1 ('' at bus 2) has Pmin 80 MW above Pmax 50 MW", dc.Message, StringComparison.Ordinal);

        var ds = Assert.Throws<ArgumentException>(() =>
            new DistributedSlackPowerFlow(new SparseNewtonRaphsonPowerFlow(), ParticipationRule.Equal).Solve(m, new PowerFlowOptions()));
        Assert.Contains("generator 1 ('' at bus 2) has Pmin 80 MW above Pmax 50 MW", ds.Message, StringComparison.Ordinal);

        // Outage sharing by governor droop: losing unit 0 shares its output among the rest, unit 1 included.
        var share = Assert.Throws<ArgumentException>(() =>
            ContingencyAnalysis.ScreenElementOutage(m, new ElementOutage(OutageKind.Generator, 0), dispatch: OutageDispatch.GovernorDroop));
        Assert.Contains("has Pmin 80 MW above Pmax 50 MW", share.Message, StringComparison.Ordinal);
    }

    /// <summary>Optional: the real PGLib-OPF file the RC4 large-case run refused (CC BY 4.0, not committed here). Set
    /// GRIDSIM_PGLIB_CASE2868_API to the path of pglib_opf_case2868_rte__api.m (PGLib-OPF dc6be4b2). Its mpc.gen row 554
    /// (out of service) has PMIN 13.2 above PMAX 5; the case must load with the warning and reach the solver.</summary>
    [EnvironmentFact("needs the PGLib-OPF case file", "GRIDSIM_PGLIB_CASE2868_API")]
    public void Pglib_case2868_rte_api_loads_and_reaches_the_solver()
    {
        string path = Environment.GetEnvironmentVariable("GRIDSIM_PGLIB_CASE2868_API")!;
        MatpowerParseResult p = MatpowerParser.ParseWithDiagnostics(File.ReadAllText(path), Path.GetFileNameWithoutExtension(path));
        Assert.Contains(p.Notes, n => n.StartsWith("warning: 1 generator row(s) have PMIN above PMAX (mpc.gen row(s) 554);", StringComparison.Ordinal));
        PowerFlowResult r = new SparseNewtonRaphsonPowerFlow().Solve(p.Model,
            new PowerFlowOptions { FlatStart = true, EnforceQLimits = false, Tolerance = 1e-8, MaxIterations = 50 });
        Assert.Equal(p.Model.BusCount, r.Vm.Length);
    }
}
