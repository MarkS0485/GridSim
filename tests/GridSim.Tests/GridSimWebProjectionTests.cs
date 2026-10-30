using GridSim.Core.Dynamics;
using GridSim.Core.IO;
using GridSim.Core.Model;
using GridSim.Core.PowerFlow;
using GridSim.Core.Simulation;
using GridSim.Web.Projection;
using Xunit;

namespace GridSim.Tests;

/// <summary>
/// Pins the GridSim.Web projection layer to Core's physics: the browser DTOs must carry exactly the numbers
/// the solver produces, so the web clone can't silently corrupt them. case9 is the ground-truth anchor.
/// </summary>
public sealed class GridSimWebProjectionTests
{
    private static SimFrame SolveCase9()
    {
        GridModel model = IeeeCases.Case9();
        var solver = new SteadyStateSolver();
        PowerFlowResult r = solver.Solve(model);
        GridModel solved = solver.RegulatedModel;
        double gen = solved.Generators.Sum(g => g.Pg);
        double demand = solved.Buses.Sum(b => b.Pd);
        return new SimFrame(solved, r, Hour: 0, Tick: 0, FrequencyHz: 50, GenerationMw: gen, DemandMw: demand);
    }

    [Fact]
    public void Project_Case9_PreservesTheSolvedNumbers()
    {
        SimFrame f = SolveCase9();
        FrameEnvelope env = FrameProjector.Project(f);

        Assert.True(env.Headline.Converged);
        Assert.Equal(9, env.Nodes.Count);
        Assert.Equal(9, env.Edges.Count);
        // case9's hallmark: ~4.64 MW total loss (validated vs MATPOWER).
        Assert.Equal(4.641, env.Headline.LossMw, 2);
        // Slack bus holds 1.04 pu.
        NodeStateDto slack = env.Nodes.Single(n => n.Id == 1);
        Assert.Equal(1.040, slack.Vm, 3);
        // Every projected voltage matches the solved result exactly.
        for (int i = 0; i < f.Result.BusIds.Count; i++)
        {
            NodeStateDto n = env.Nodes.Single(x => x.Id == f.Result.BusIds[i]);
            Assert.Equal(f.Result.Vm[i], n.Vm, 9);
        }
    }

    [Fact]
    public void Topology_Case9_HasLayoutCoordinates()
    {
        TopologyDto topo = FrameProjector.BuildTopology("case9", "IEEE 9-bus", IeeeCases.Case9(), replay: false, frameCount: 0);
        Assert.Equal(9, topo.Nodes.Count);
        // GraphLayout.EnsureLayout must have spread the flat IEEE case out (not all stacked at the origin).
        Assert.True(topo.Nodes.Select(n => (n.X, n.Y)).Distinct().Count() > 1);
        Assert.All(topo.Nodes, n => Assert.InRange(n.X, 0, 1));
    }

    [Fact]
    public void FuelBreakdown_SplitsSynchronousAndConverter()
    {
        // Two synchronous (CCGT) + one converter (wind) generator.
        var model = new GridModel
        {
            BaseMva = 100,
            Buses = new[] { new Bus(1, BusType.Slack, BaseKv: 400), new Bus(2, BusType.Pq, Pd: 300, BaseKv: 400) },
            Branches = new[] { new Branch(1, 2, 0.01, 0.1) },
            Generators = new[]
            {
                new Generator(1, 200, MBase: 250, H: 4, Type: "CCGT"),
                new Generator(1, 100, MBase: 120, H: 5, Type: "wind-offshore"),
            },
        };
        var fb = new FuelBreakdown(model);

        Assert.Equal(200, fb.SyncMw, 3);
        Assert.Equal(100, fb.ConverterMw, 3);
        // Intrinsic inertia = synchronous H*MBase only: 4*250/1000 = 1.0 GVA.s (wind contributes none).
        Assert.Equal(1.0, fb.IntrinsicInertiaGvaS, 3);
        Assert.Equal(200, fb.LargestUnitMw, 3);
    }

    [Fact]
    public void Rp1_PurchasedTime_IsSelfConsistentWithRocof()
    {
        SimFrame f = SolveCase9();
        var fb = new FuelBreakdown(f.Model);
        Rp1Dto rp1 = TabProjections.Rp1(f, fb);

        // purchased time = (50 - 49.2) / RoCoF, the MIF identity StabilityMetrics encodes.
        if (rp1.RocofHzPerS > 1e-9 && !double.IsInfinity(rp1.PurchasedTimeSeconds))
        {
            double expected = (FrequencyLimits.NominalHz - StabilityMetrics.LfddWorkingSetpointHz) / rp1.RocofHzPerS;
            Assert.Equal(expected, rp1.PurchasedTimeSeconds, 3);
        }
    }

    [Fact]
    public void Rp1_NMinus1Screen_RanksWorstFirstAndFlagsBreaches()
    {
        // 300 GVA.s intrinsic; a big synchronous unit and a smaller one, both online.
        var model = new GridModel
        {
            BaseMva = 100,
            Buses = new[] { new Bus(1, BusType.Slack, BaseKv: 400), new Bus(2, BusType.Pq, Pd: 3000, BaseKv: 400) },
            Branches = new[] { new Branch(1, 2, 0.01, 0.1) },
            Generators = new[]
            {
                new Generator(1, 2400, MBase: 3000, H: 100, Type: "CCGT"),   // 300 GVA.s of intrinsic inertia
                new Generator(1, 600, MBase: 700, H: 0, Type: "wind"),
            },
        };
        var solver = new SteadyStateSolver();
        PowerFlowResult r = solver.Solve(model);
        var f = new SimFrame(solver.RegulatedModel, r, 0, 0, 50, 3000, 3000);
        var fb = new FuelBreakdown(f.Model);

        Rp1Dto rp1 = TabProjections.Rp1(f, fb);

        Assert.NotEmpty(rp1.Screening);
        // Worst-first ordering by RoCoF.
        for (int i = 1; i < rp1.Screening.Count; i++)
            Assert.True(rp1.Screening[i - 1].RocofHzPerS >= rp1.Screening[i].RocofHzPerS);
        // The 2400 MW unit is the worst loss; RoCoF = 50*2400/(2*300000) = 0.2 Hz/s -> breaches legacy 0.125, not modern 1.0.
        Rp1ScreenRowDto worst = rp1.Screening[0];
        Assert.Equal(2400, worst.LossMw, 0);
        Assert.Equal(0.2, worst.RocofHzPerS, 3);
        Assert.True(worst.BreachesLegacy);
        Assert.False(worst.BreachesModern);
    }

    [Theory]
    [InlineData(8.0, "08:00")]
    [InlineData(13.5, "13:30")]
    [InlineData(0.0, "00:00")]
    [InlineData(23.99, "23:59")]        // 0.99*60 = 59.4 -> 59 min
    [InlineData(23.999722, "00:00")]    // rounds up to 60 min -> rolls over to midnight, not 24:00
    [InlineData(double.NaN, "--:--")]   // GR-722: a non-finite hour is the same text on every CPU
    [InlineData(double.PositiveInfinity, "--:--")]
    [InlineData(double.NegativeInfinity, "--:--")]
    public void FormatClock_Works(double hour, string expected)
        => Assert.Equal(expected, FrameProjector.FormatClock(hour));
}
