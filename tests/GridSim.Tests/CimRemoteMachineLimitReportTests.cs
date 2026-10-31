using System.Reflection;
using GridSim.Core.IO.Cim;
using GridSim.Core.Model;
using GridSim.Core.PowerFlow;
using Xunit;

namespace GridSim.Tests;

/// <summary>
/// GitHub issue #5 (regression test for bug hunt 2026-10-29, CimControlledPowerFlow MachinesAtUpperLimit, fixed then
/// but not pinned): the reactive-limit report of remote-controlling machines must read the machine bus's output from the
/// SOLVED buses, whose demand has each regulating SVC's held reactive power taken off, not from the original model's
/// demand. At a bus hosting both device kinds the original demand is off by the SVC's held MVAr, which made the report
/// call the machines "at their upper reactive limit" when they were not (or miss it).
/// <para>The check is a private static of the solver; it is called directly by reflection on a hand-built state so the
/// difference is exact and needs no CGMES fixture.</para>
/// </summary>
public sealed class CimRemoteMachineLimitReportTests
{
    private static bool AtUpper(int bus, PowerFlowResult r, GridModel model, IReadOnlyList<Bus> solved)
    {
        MethodInfo m = typeof(CimControlledPowerFlow).GetMethod("MachinesAtUpperLimit", BindingFlags.Static | BindingFlags.NonPublic)!;
        Assert.NotNull(m);
        return (bool)m.Invoke(null, new object[] { bus, r, model, solved })!;
    }

    private static PowerFlowResult Solved(double qInjMvar) => new()
    {
        Converged = true, Iterations = 1, MaxMismatch = 0.0,
        BusIds = new[] { 1, 2 }, Vm = new[] { 1.0, 1.0 }, VaDeg = new[] { 0.0, 0.0 },
        PInjMw = new[] { 0.0, 0.0 }, QInjMvar = new[] { 0.0, qInjMvar },
        TotalLossMw = 0.0, TotalLossMvar = 0.0, LineFlows = Array.Empty<LineFlow>(),
    };

    // Bus 2 hosts the remote-controlling machines (Qmax 100 MVAr in all) and a regulating SVC holding 20 MVAr, so the
    // solved copy's demand at bus 2 is 50 - 20 = 30 MVAr while the original model's is 50 MVAr.
    private static readonly GridModel Model = new()
    {
        BaseMva = 100, Name = "remote-limit",
        Buses = new List<Bus> { new(1, BusType.Slack, BaseKv: 132), new(2, BusType.Pv, Pd: 0, Qd: 50, BaseKv: 132) },
        Branches = new List<Branch> { new(1, 2, 0.0, 0.1) },
        Generators = new List<Generator> { new(1, 0), new(2, 0, Qmax: 60, Qmin: -60), new(2, 0, Qmax: 40, Qmin: -40) },
    };

    private static readonly IReadOnlyList<Bus> SolvedBuses = new List<Bus>
    {
        new(1, BusType.Slack, BaseKv: 132), new(2, BusType.Pv, Pd: 0, Qd: 30, BaseKv: 132),
    };

    [Fact]
    public void Machines_below_their_limit_are_not_reported_at_it_when_an_SVC_shares_the_bus()
    {
        // Net injection 55 MVAr: the machines make 55 + 30 = 85 MVAr of their 100. Reading the original demand (50) would
        // give 105 and report them at the upper limit.
        Assert.False(AtUpper(2, Solved(55.0), Model, SolvedBuses));
    }

    [Fact]
    public void Machines_at_their_limit_are_reported_at_it_when_an_SVC_shares_the_bus()
    {
        // Net injection 70 MVAr: 70 + 30 = 100 MVAr, the machines' combined Qmax.
        Assert.True(AtUpper(2, Solved(70.0), Model, SolvedBuses));
        // Just below the limit by more than the 1e-3 MVAr shell tolerance: not at it.
        Assert.False(AtUpper(2, Solved(69.99), Model, SolvedBuses));
    }
}
