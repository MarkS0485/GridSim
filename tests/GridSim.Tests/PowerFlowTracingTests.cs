using GridSim.Core.Analysis;
using GridSim.Core.IO;
using GridSim.Core.Model;
using GridSim.Core.Physics.Experimental;
using GridSim.Core.PowerFlow;
using Xunit;

namespace GridSim.Tests;

/// <summary>
/// Power-flow tracing (Bialek proportional sharing) - "where did each generator's power go?". The
/// load-bearing correctness invariants hold on ANY solved case regardless of the (unverifiable) exact
/// allocation: every generator's output is fully accounted for (loads + losses), and every load is
/// fully sourced from the generators. Also a single-generator sanity: one source supplies 100% of
/// every load.
/// </summary>
public class PowerFlowTracingTests
{
    [Theory]
    [InlineData("9")]
    [InlineData("14")]
    [InlineData("30")]
    public void Every_generators_output_is_fully_accounted_and_every_load_fully_sourced(string name)
    {
        GridModel m = name switch
        {
            "9" => IeeeCases.Case9(),
            "14" => IeeeCases.Case14(),
            "30" => IeeeCases.Case30(),
            _ => IeeeCases.Case14(),
        };
        PowerFlowResult r = new NewtonRaphsonPowerFlow().Solve(m, new PowerFlowOptions { EnforceQLimits = true });
        PowerFlowTracing.TraceResult tr = PowerFlowTracing.Trace(m, r);
        Assert.True(tr.FlowGraphAcyclic);

        // (1) Each generator: sum of what it sent to loads + its loss share == its output.
        foreach ((int genBus, double outMw) in tr.GenerationMw)
        {
            double toLoads = tr.Allocations.Where(x => x.GenBus == genBus).Sum(x => x.Mw);
            double loss = tr.GenToLossMw.TryGetValue(genBus, out double lv) ? lv : 0.0;
            Assert.Equal(outMw, toLoads + loss, 3); // MW, 3 dp
        }

        // (2) Each load: sum of contributions from all generators == its demand.
        var demandByBus = m.Buses.Where(b => b.Pd > 1e-9).ToDictionary(b => b.Id, b => b.Pd);
        foreach ((int loadBus, double pd) in demandByBus)
        {
            double sourced = tr.Allocations.Where(x => x.LoadBus == loadBus).Sum(x => x.Mw);
            Assert.Equal(pd, sourced, 2); // MW, 2 dp
        }
    }

    [Fact]
    public void Loss_attribution_sums_to_the_total_network_loss()
    {
        GridModel m = IeeeCases.Case14();
        PowerFlowResult r = new NewtonRaphsonPowerFlow().Solve(m, new PowerFlowOptions { EnforceQLimits = true });
        PowerFlowTracing.TraceResult tr = PowerFlowTracing.Trace(m, r);

        double attributed = tr.GenToLossMw.Values.Sum();
        Assert.Equal(r.TotalLossMw, attributed, 2); // MW
    }

    [Fact]
    public void Boundary_attribution_sums_to_the_boundary_net_transfer()
    {
        // 4-bus: generators at 1,2 feed loads at 3,4 across a two-branch cut.
        var m = new GridModel
        {
            BaseMva = 100,
            Buses = new[]
            {
                new Bus(1, BusType.Slack), new Bus(2, BusType.Pv, Vm: 1.0),
                new Bus(3, BusType.Pq, Pd: 60), new Bus(4, BusType.Pq, Pd: 50),
            },
            Generators = new[] { new Generator(1, Pg: 0), new Generator(2, Pg: 55, Vg: 1.0) },
            Branches = new[]
            {
                new Branch(1, 3, 0.01, 0.06) { Name = "A" },
                new Branch(2, 4, 0.01, 0.06) { Name = "B" },
                new Branch(3, 4, 0.01, 0.06) { Name = "C" },
            },
            Boundaries = new[]
            {
                new Boundary("CUT", 500, new[] { new BoundaryBranch("A", 1), new BoundaryBranch("B", 1) }),
            },
        };
        PowerFlowResult r = new NewtonRaphsonPowerFlow().Solve(m);
        Assert.True(r.Converged);

        double boundaryFlow = BoundaryAnalysis.Evaluate(m, r).Single(b => b.Name == "CUT").FlowMw;
        PowerFlowTracing.BoundaryTransfer bt = PowerFlowTracing.BoundaryTransfers(m, r).Single(b => b.Boundary == "CUT");

        // The per-generator shares sum to the boundary net, which equals the independently-evaluated flow.
        Assert.Equal(boundaryFlow, bt.NetMw, 3);
        Assert.Equal(bt.NetMw, bt.ByGenerator.Sum(g => g.Mw), 6);
    }

    [Fact]
    public void A_single_generator_supplies_all_of_every_load()
    {
        // A 3-bus radial: one generator at bus 1 feeds loads at 2 and 3 down a line.
        var m = new GridModel
        {
            BaseMva = 100,
            Buses = new[]
            {
                new Bus(1, BusType.Slack),
                new Bus(2, BusType.Pq, Pd: 40),
                new Bus(3, BusType.Pq, Pd: 30),
            },
            Generators = new[] { new Generator(1, Pg: 0) },
            Branches = new[]
            {
                new Branch(1, 2, 0.01, 0.05),
                new Branch(2, 3, 0.01, 0.05),
            },
        };
        PowerFlowResult r = new NewtonRaphsonPowerFlow().Solve(m);
        Assert.True(r.Converged);

        PowerFlowTracing.TraceResult tr = PowerFlowTracing.Trace(m, r);
        Assert.True(tr.FlowGraphAcyclic);

        // The sole generator (bus 1) is the source of 100% of both loads.
        Assert.Equal(40.0, tr.Allocations.Single(x => x.GenBus == 1 && x.LoadBus == 2).Mw, 1);
        Assert.Equal(30.0, tr.Allocations.Single(x => x.GenBus == 1 && x.LoadBus == 3).Mw, 1);
    }
}
