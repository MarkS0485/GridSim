using GridSim.Core.IO;
using GridSim.Core.IO.Json;
using GridSim.Core.Model;
using GridSim.Core.PowerFlow;
using Xunit;

namespace GridSim.Tests;

/// <summary>
/// The sparse Newton-Raphson (ILU0 + BiCGSTAB) must reproduce the dense solver's
/// solution  -  it only changes how the linear system is stored and solved, not the
/// physics. We check it against the published IEEE 9-bus case and the UK model.
/// </summary>
public class SparsePowerFlowTests
{
    private static GridModel Uk() => JsonGridLoader.LoadDirectory(Path.Combine(AppContext.BaseDirectory, "data", "uk"));

    private static readonly PowerFlowOptions NoQ = new() { EnforceQLimits = false };

    [Fact]
    public void Matches_dense_on_case9()
    {
        GridModel m = IeeeCases.Case9();
        PowerFlowResult dense = new NewtonRaphsonPowerFlow().Solve(m, NoQ);
        PowerFlowResult sparse = new SparseNewtonRaphsonPowerFlow().Solve(m, NoQ);

        Assert.True(sparse.Converged);
        for (int i = 0; i < dense.Vm.Length; i++)
        {
            Assert.Equal(dense.Vm[i], sparse.Vm[i], 6);
            Assert.Equal(dense.VaDeg[i], sparse.VaDeg[i], 5);
        }
        Assert.Equal(dense.TotalLossMw, sparse.TotalLossMw, 3);
    }

    [Fact]
    public void Matches_dense_on_uk_model()
    {
        GridModel m = Uk();
        PowerFlowResult dense = new NewtonRaphsonPowerFlow().Solve(m, NoQ);
        PowerFlowResult sparse = new SparseNewtonRaphsonPowerFlow().Solve(m, NoQ);

        Assert.True(sparse.Converged);
        double maxDvm = 0, maxDva = 0;
        for (int i = 0; i < dense.Vm.Length; i++)
        {
            maxDvm = Math.Max(maxDvm, Math.Abs(dense.Vm[i] - sparse.Vm[i]));
            maxDva = Math.Max(maxDva, Math.Abs(dense.VaDeg[i] - sparse.VaDeg[i]));
        }
        Assert.True(maxDvm < 1e-5, $"max |deltaVm| = {maxDvm:E2}");
        Assert.True(maxDva < 1e-3, $"max |deltaVa| = {maxDva:E2} deg");
    }

    [Fact]
    public void Matches_dense_when_a_branch_is_out_of_service()
    {
        // Regression: BuildY used to stamp EVERY branch, so an opened branch
        // (recloser hold-off, cascade trip) stayed electrically connected on
        // the sparse path only  -  the solve was that of the unbroken network
        // while its LineFlow reported 0. Dense and sparse must agree on the
        // weakened network, and both must differ from the intact solve.
        GridModel intact = IeeeCases.Case9();
        var m = new GridModel
        {
            BaseMva = intact.BaseMva,
            Buses = intact.Buses,
            Generators = intact.Generators,
            Branches = intact.Branches
                .Select((br, k) => k == 4 ? br with { InService = false } : br)  // open ring corridor 6-7; network stays connected
                .ToList(),
        };

        PowerFlowResult dense = new NewtonRaphsonPowerFlow().Solve(m, NoQ);
        PowerFlowResult sparse = new SparseNewtonRaphsonPowerFlow().Solve(m, NoQ);
        PowerFlowResult before = new NewtonRaphsonPowerFlow().Solve(intact, NoQ);

        Assert.True(dense.Converged);
        Assert.True(sparse.Converged);
        double maxDvm = 0, drift = 0;
        for (int i = 0; i < dense.Vm.Length; i++)
        {
            maxDvm = Math.Max(maxDvm, Math.Abs(dense.Vm[i] - sparse.Vm[i]));
            drift = Math.Max(drift, Math.Abs(dense.VaDeg[i] - before.VaDeg[i]));
        }
        Assert.True(maxDvm < 1e-6, $"sparse diverges from dense with an open branch: {maxDvm:E2}");
        Assert.True(drift > 1e-3, "opening a loaded branch must change the solution");
    }

    [Fact]
    public void Sparse_handles_a_multi_slack_two_island_network()
    {
        // Two independent 2-bus islands, each with its own slack  -  exercises the
        // multi-reference case the full GDA replica produces.
        var buses = new List<Bus>
        {
            new(0, BusType.Slack, BaseKv: 132, Vm: 1.0),
            new(1, BusType.Pq, Pd: 50, Qd: 10, BaseKv: 132),
            new(2, BusType.Slack, BaseKv: 132, Vm: 1.0),
            new(3, BusType.Pq, Pd: 30, Qd: 5, BaseKv: 132),
        };
        var branches = new List<Branch>
        {
            new(0, 1, 0.01, 0.05, 0.0, Name: "A"),
            new(2, 3, 0.01, 0.05, 0.0, Name: "B"),
        };
        var m = new GridModel { BaseMva = 100, Buses = buses, Generators = new List<Generator>(), Branches = branches };

        PowerFlowResult r = new SparseNewtonRaphsonPowerFlow().Solve(m, NoQ);
        Assert.True(r.Converged, $"mismatch {r.MaxMismatch:E2}");
        Assert.InRange(r.Vm.Min(), 0.9, 1.05);
    }
}
