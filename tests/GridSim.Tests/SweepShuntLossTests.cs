using GridSim.Core.IO.Matpower;
using GridSim.Core.Model;
using GridSim.Core.PowerFlow;
using Xunit;

namespace GridSim.Tests;

/// <summary>
/// The backward/forward sweep's TotalLossMw / TotalLossMvar include the bus shunts, as the Newton engines' do (they
/// sum every net injection). Before, the sweep reported the branch losses only, so a radial feeder with a conductive
/// shunt or a capacitor bank had TotalLossMw != sum(PInjMw) and PhysicsLaws' energy-balance law failed a correct solve.
/// </summary>
public class SweepShuntLossTests
{
    // A four-bus radial feeder (11 kV, 10 MVA base) with a conductive shunt on bus 3 (Gs = 0.05 MW at 1 pu) and a
    // capacitor bank on bus 4 (Bs = 0.2 MVAr at 1 pu).
    private const string Feeder = @"function mpc = shuntfeeder
mpc.baseMVA = 10;
mpc.bus = [
  1 3 0    0    0    0   1 1 0 11 1 1.1 0.9;
  2 1 0.8  0.3  0    0   1 1 0 11 1 1.1 0.9;
  3 1 0.5  0.2  0.05 0   1 1 0 11 1 1.1 0.9;
  4 1 0.6  0.25 0    0.2 1 1 0 11 1 1.1 0.9;
];
mpc.gen = [ 1 0 0 10 -10 1.0 10 1 10 0; ];
mpc.branch = [
  1 2 0.010 0.020 0 0 0 0 0 0 1 -360 360;
  2 3 0.012 0.024 0 0 0 0 0 0 1 -360 360;
  3 4 0.015 0.030 0 0 0 0 0 0 1 -360 360;
];";

    private static PowerFlowOptions Tight => new() { Tolerance = 1e-12, MaxIterations = 200 };

    [Fact]
    public void Sweep_loss_totals_equal_the_sum_of_its_own_net_injections()
    {
        GridModel m = MatpowerParser.Parse(Feeder, units: MatpowerUnitHandling.AsWritten);
        PowerFlowResult r = new BackwardForwardSweep().Solve(m, Tight);
        Assert.True(r.Converged, $"sweep did not converge: {r.Status}");

        double sumP = 0.0, sumQ = 0.0;
        for (int i = 0; i < r.PInjMw.Length; i++) { sumP += r.PInjMw[i]; sumQ += r.QInjMvar[i]; }

        // The two are the same quantity summed in a different order, so they agree to rounding (a few ulps of the
        // ~2 MW throughput), not merely to the old shunt-sized gap of 0.05 MW / 0.2 MVAr.
        Assert.True(Math.Abs(r.TotalLossMw - sumP) < 1e-12, $"P: loss {r.TotalLossMw:R} vs sum {sumP:R}");
        Assert.True(Math.Abs(r.TotalLossMvar - sumQ) < 1e-12, $"Q: loss {r.TotalLossMvar:R} vs sum {sumQ:R}");
    }

    [Fact]
    public void Sweep_and_Newton_report_the_same_losses_on_a_feeder_with_shunts()
    {
        GridModel m = MatpowerParser.Parse(Feeder, units: MatpowerUnitHandling.AsWritten);
        PowerFlowResult s = new BackwardForwardSweep().Solve(m, Tight);
        PowerFlowResult n = new NewtonRaphsonPowerFlow().Solve(m, Tight);
        Assert.True(s.Converged && n.Converged);
        // Different algorithms converged to 1e-12 pu on a 10 MVA base: the states agree to ~1e-10 MW.
        Assert.True(Math.Abs(s.TotalLossMw - n.TotalLossMw) < 1e-8, $"P: sweep {s.TotalLossMw:R} newton {n.TotalLossMw:R}");
        Assert.True(Math.Abs(s.TotalLossMvar - n.TotalLossMvar) < 1e-8, $"Q: sweep {s.TotalLossMvar:R} newton {n.TotalLossMvar:R}");
    }

    [Fact]
    public void Sweep_and_Newton_label_an_unnamed_branch_alike()
    {
        // GR-500: the sweep used to name an unnamed branch "{from}-{to}" where Newton leaves the name empty.
        GridModel parsed = MatpowerParser.Parse(Feeder, units: MatpowerUnitHandling.AsWritten);
        GridModel m = parsed.CopyWith(branches: parsed.Branches.Select(b => b with { Name = null }).ToArray());
        PowerFlowResult s = new BackwardForwardSweep().Solve(m, Tight);
        PowerFlowResult n = new NewtonRaphsonPowerFlow().Solve(m, Tight);
        Assert.True(s.Converged && n.Converged);
        Assert.Equal(n.LineFlows.Select(f => f.Name), s.LineFlows.Select(f => f.Name));
        Assert.All(s.LineFlows, f => Assert.Equal("", f.Name));
    }
}
