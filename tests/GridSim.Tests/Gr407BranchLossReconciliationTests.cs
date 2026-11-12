using GridSim.Core.IO;
using GridSim.Core.Model;
using GridSim.Core.PowerFlow;
using Xunit;

namespace GridSim.Tests;

/// <summary>
/// GR-407: SolverPropertyTests reconciles the per-branch losses plus the bus-shunt losses with the reported total loss at
/// only 1e-3 of the loss (P) and 1e-2 (Q). The two sides are the same quantity computed two ways from the SAME solved
/// voltages (the nodal sum of S_i = V_i conj(sum_j Y_ij V_j) against the sum of each branch pi-model's end flows plus
/// V^2 (Gs - jBs) at each bus), so they agree whether or not the solve converged tightly, up to rounding alone. This holds
/// them at 1e-9 of the gross branch throughput (the size of the numbers being summed), six or more orders tighter, on
/// every built-in case SolverPropertyTests covers, including the two stress cases. The existing test is unchanged.
/// </summary>
public class Gr407BranchLossReconciliationTests
{
    public static IEnumerable<object[]> Cases =>
        new[] { "9", "14", "30", "57", "118", "300", "24", "145" }.Select(n => new object[] { n });

    private static GridModel Model(string name) => name switch
    {
        "9" => IeeeCases.Case9(),
        "14" => IeeeCases.Case14(),
        "30" => IeeeCases.Case30(),
        "57" => IeeeCases.Case57(),
        "118" => IeeeCases.Case118(),
        "300" => IeeeCases.Case300(),
        "24" => IeeeCases.Case24Rts(),
        "145" => IeeeCases.Case145(),
        _ => throw new ArgumentException($"unknown case {name}", nameof(name)),
    };

    /// <summary>Relative bound on the gross throughput: rounding of sums of a few thousand terms of that size.</summary>
    private const double RelativeToThroughput = 1e-9;

    [Theory]
    [MemberData(nameof(Cases))]
    public void Gr407_branch_plus_shunt_losses_equal_the_nodal_total_to_rounding(string name)
    {
        GridModel m = Model(name);
        PowerFlowResult r = new NewtonRaphsonPowerFlow().Solve(m);
        Assert.True(r.Converged, $"case{name} did not converge");

        double lineP = 0, lineQ = 0, grossP = 0, grossQ = 0;
        foreach (LineFlow f in r.LineFlows)
        {
            lineP += f.PLossMw;
            lineQ += f.QLossMvar;
            grossP += Math.Abs(f.PFromMw) + Math.Abs(f.PToMw);
            grossQ += Math.Abs(f.QFromMvar) + Math.Abs(f.QToMvar);
        }
        double shuntP = 0, shuntQ = 0;
        for (int i = 0; i < r.Vm.Length; i++)
        {
            double vm2 = r.Vm[i] * r.Vm[i];
            shuntP += vm2 * m.Buses[i].Gs;
            shuntQ += -vm2 * m.Buses[i].Bs;
            grossP += Math.Abs(vm2 * m.Buses[i].Gs);
            grossQ += Math.Abs(vm2 * m.Buses[i].Bs);
        }

        double tolP = RelativeToThroughput * Math.Max(1.0, grossP);
        double tolQ = RelativeToThroughput * Math.Max(1.0, grossQ);
        double dP = Math.Abs(lineP + shuntP - r.TotalLossMw);
        double dQ = Math.Abs(lineQ + shuntQ - r.TotalLossMvar);
        Assert.True(dP <= tolP, $"case{name}: |line + shunt - total| P {dP:E3} MW > {tolP:E3} (gross {grossP:F1} MW)");
        Assert.True(dQ <= tolQ, $"case{name}: |line + shunt - total| Q {dQ:E3} MVAr > {tolQ:E3} (gross {grossQ:F1} MVAr)");
    }
}
