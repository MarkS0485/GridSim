using GridSim.Core.Estimation;
using GridSim.Core.Model;
using GridSim.Core.Physics;
using GridSim.Core.PowerFlow;
using Xunit;

namespace GridSim.Tests;

/// <summary>
/// GR-495: PhysicsFeasibility.Check read whatever state it was given, so a non-converged result was judged against the
/// equipment limits as if it were an operating point. It now refuses a non-converged result by name. (The infinity-sign
/// half of GR-495, refusing a corrupt reactive limit, was fixed earlier and is unchanged.)
/// </summary>
public class Gr495PhysicsFeasibilityConvergedGateTests
{
    private static GridModel TwoBus() => new()
    {
        BaseMva = 100,
        Buses = new[] { new Bus(1, BusType.Slack, BaseKv: 132), new Bus(2, BusType.Pv, Pd: 10, BaseKv: 132) },
        Branches = new[] { new Branch(1, 2, 0.01, 0.1) },
        Generators = new[] { new Generator(1, 10), new Generator(2, 0, Qmin: -5, Qmax: 5) },
    };

    private static PowerFlowResult State(bool converged, PowerFlowStatus status) => new()
    {
        Converged = converged,
        Status = status,
        Iterations = 30,
        MaxMismatch = converged ? 0.0 : 1.0,
        BusIds = new[] { 1, 2 },
        Vm = new[] { 1.0, 0.98 },
        VaDeg = new[] { 0.0, -1.0 },
        PInjMw = new[] { 10.0, -10.0 },
        QInjMvar = new[] { 1.0, 2.0 },
        TotalLossMw = 0.0,
        TotalLossMvar = 0.0,
        LineFlows = Array.Empty<LineFlow>(),
    };

    [Theory]
    [InlineData(PowerFlowStatus.MaxIterations)]
    [InlineData(PowerFlowStatus.NonFinite)]
    public void Gr495_a_non_converged_result_is_refused_by_name(PowerFlowStatus status)
    {
        var ex = Assert.Throws<ArgumentException>(() =>
            PhysicsFeasibility.Check(TwoBus(), State(converged: false, status), new FeasibilityConstraints()));
        Assert.Contains("did not converge", ex.Message);
        Assert.Contains(status.ToString(), ex.Message);
    }

    [Fact]
    public void Gr495_a_converged_result_is_still_checked()
    {
        IReadOnlyList<PhysicsFeasibility.LimitViolation> v =
            PhysicsFeasibility.Check(TwoBus(), State(converged: true, PowerFlowStatus.Converged), new FeasibilityConstraints());
        Assert.NotNull(v);
    }
}
