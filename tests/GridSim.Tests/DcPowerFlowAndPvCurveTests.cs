using GridSim.Core.Analysis;
using GridSim.Core.IO;
using GridSim.Core.IO.Json;
using GridSim.Core.Model;
using GridSim.Core.PowerFlow;
using Xunit;

namespace GridSim.Tests;

/// <summary>
/// F18 (October 2026 release gap): the <c>dcpf</c> verb's DC power flow and the per-bus PV-curve export of
/// <c>cpf</c>. Both are built on existing engines and must not create a second copy of any number: the DC power flow's
/// branch flows are bit for bit DistributionFactors.DcFlows, and the PV trace's maximum loadability is bit for bit
/// ContinuationPowerFlow.MaxLoadability.
/// </summary>
public class DcPowerFlowAndPvCurveTests
{
    private static Dictionary<int, double> NetInjections(GridModel m)
    {
        var net = new Dictionary<int, double>();
        foreach (Bus b in m.Buses) net[b.Id] = net.GetValueOrDefault(b.Id) - b.Pd;
        foreach (Generator g in m.Generators) if (net.ContainsKey(g.Bus)) net[g.Bus] += g.Pg;
        return net;
    }

    public static IEnumerable<object[]> Cases => new[] { "9", "14", "30", "118", "300" }.Select(n => new object[] { n });

    private static GridModel Case(string n) => n switch
    {
        "9" => IeeeCases.Case9(), "14" => IeeeCases.Case14(), "30" => IeeeCases.Case30(),
        "118" => IeeeCases.Case118(), _ => IeeeCases.Case300(),
    };

    [Theory]
    [MemberData(nameof(Cases))]
    public void Dc_power_flow_branch_flows_are_bit_identical_to_DcFlows(string n)
    {
        GridModel m = Case(n);
        DcPowerFlowResult r = DcPowerFlow.Solve(m);
        double[] reference = DistributionFactors.Build(m).DcFlows(NetInjections(m), m.BaseMva);
        Assert.Equal(reference.Length, r.FlowMw.Length);
        for (int l = 0; l < reference.Length; l++)
            Assert.True(BitConverter.DoubleToInt64Bits(reference[l]) == BitConverter.DoubleToInt64Bits(r.FlowMw[l]),
                $"case{n} branch {l}: {r.FlowMw[l]:R} vs DcFlows {reference[l]:R}");
    }

    [Theory]
    [MemberData(nameof(Cases))]
    public void Dc_power_flow_balances_every_bus_and_island(string n)
    {
        GridModel m = Case(n);
        DcPowerFlowResult r = DcPowerFlow.Solve(m);
        Assert.Single(r.Islands);
        int refId = r.Islands[0].ReferenceBusId;
        Assert.Equal(0.0, r.VaDeg[r.BusIds.ToList().IndexOf(refId)]);

        // Lossless KCL at every bus: net injection = sum of flows leaving it (1e-6 MW: only rounding of the solve).
        var outflow = new Dictionary<int, double>();
        for (int l = 0; l < m.Branches.Count; l++)
        {
            Branch br = m.Branches[l];
            outflow[br.From] = outflow.GetValueOrDefault(br.From) + r.FlowMw[l];
            outflow[br.To] = outflow.GetValueOrDefault(br.To) - r.FlowMw[l];
        }
        for (int i = 0; i < r.BusIds.Count; i++)
        {
            double residual = r.PInjMw[i] - outflow.GetValueOrDefault(r.BusIds[i]);
            Assert.True(Math.Abs(residual) < 1e-6, $"case{n} bus {r.BusIds[i]}: KCL residual {residual:R} MW");
        }

        // The reference picks up the island's imbalance, so the island's injections sum to zero (to rounding).
        Assert.True(Math.Abs(r.PInjMw.Sum()) < 1e-6, $"case{n}: island injections sum to {r.PInjMw.Sum():R} MW");
        DcIslandBalance isl = r.Islands[0];
        Assert.Equal(m.Buses.Sum(b => b.Pd), isl.LoadMw, 9);
        Assert.Equal(m.Generators.Sum(g => g.Pg), isl.ScheduledGenerationMw, 9);
    }

    [Fact]
    public void Dc_power_flow_angles_reproduce_the_flows()
    {
        // Flow = (theta_from - theta_to) / X x base on every in-service branch with X != 0.
        GridModel m = IeeeCases.Case30();
        DcPowerFlowResult r = DcPowerFlow.Solve(m);
        var idx = r.BusIds.Select((id, i) => (id, i)).ToDictionary(t => t.id, t => t.i);
        for (int l = 0; l < m.Branches.Count; l++)
        {
            Branch br = m.Branches[l];
            double dTheta = (r.VaDeg[idx[br.From]] - r.VaDeg[idx[br.To]]) * Math.PI / 180.0;
            Assert.Equal(dTheta / br.X * m.BaseMva, r.FlowMw[l], 6);
        }
    }

    [Fact]
    public void Dc_power_flow_reports_each_island_of_a_multi_island_case()
    {
        // gb-full carries a 3-bus island with its own slack (N034): two islands, references 194 and 6774.
        GridModel m = JsonGridLoader.LoadDirectory(Path.Combine(S10TestSupport.DataDir, "gb-full"));
        DcPowerFlowResult r = DcPowerFlow.Solve(m);
        Assert.Equal(2, r.Islands.Count);
        Assert.Equal(new[] { 194, 6774 }, r.Islands.Select(i => i.ReferenceBusId).OrderBy(x => x).ToArray());
        Assert.All(r.Islands, i => Assert.True(i.HasGeneration));
    }

    // ---- PV curves -------------------------------------------------------------------------------------------------

    [Theory]
    [InlineData("9")]
    [InlineData("14")]
    [InlineData("30")]
    public void The_pv_trace_max_lambda_is_bit_identical_to_MaxLoadability(string n)
    {
        GridModel m = Case(n);
        double max = ContinuationPowerFlow.MaxLoadability(m);
        PvCurve curve = ContinuationPowerFlow.TracePvCurve(m);
        Assert.Equal(BitConverter.DoubleToInt64Bits(max), BitConverter.DoubleToInt64Bits(curve.MaxLambda));

        Assert.NotEmpty(curve.Points);
        Assert.Equal(1.0, curve.Points[0].Lambda);                       // the base case converges first
        Assert.Equal(max, curve.Points[^1].Lambda);                      // the last converged point is the answer
        for (int k = 1; k < curve.Points.Count; k++)
            Assert.True(curve.Points[k].Lambda > curve.Points[k - 1].Lambda, $"case{n}: point {k} does not rise");
        Assert.All(curve.Points, p => Assert.Equal(m.BusCount, p.Vm.Length));
        Assert.Equal(m.Buses.Select(b => b.Id), curve.BusIds);
    }

    [Fact]
    public void The_first_pv_point_is_the_base_case_solve_bit_for_bit()
    {
        GridModel m = IeeeCases.Case14();
        PvCurve curve = ContinuationPowerFlow.TracePvCurve(m);
        PowerFlowResult direct = SolverSelector.CreateNewton(m).Solve(ContinuationPowerFlow.ScaleLoads(m, 1.0));
        double[] vm = curve.Points[0].Vm;
        for (int i = 0; i < vm.Length; i++)
            Assert.Equal(BitConverter.DoubleToInt64Bits(direct.Vm[i]), BitConverter.DoubleToInt64Bits(vm[i]));
    }
}
