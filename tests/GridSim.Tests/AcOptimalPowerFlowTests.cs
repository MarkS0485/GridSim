using System.Linq;
using GridSim.Core.Analysis;
using GridSim.Core.IO;
using GridSim.Core.Model;
using GridSim.Core.PowerFlow;
using Xunit;

namespace GridSim.Tests;

/// <summary>The loss-aware AC-OPF: it converges to an AC-feasible economic dispatch whose generation covers
/// demand plus the AC losses it accounts for.</summary>
public class AcOptimalPowerFlowTests
{
    [Fact]
    public void Converges_to_a_loss_aware_ac_feasible_dispatch()
    {
        GridModel model = IeeeCases.Case14();
        AcOpfResult r = AcOptimalPowerFlow.Solve(model);

        Assert.True(r.Converged);
        Assert.True(r.TotalLossMw > 0, $"AC losses should be positive, got {r.TotalLossMw}");
        Assert.True(r.TotalCostPerHour > 0);

        double totalPd = model.Buses.Sum(b => b.Pd);
        double totalDispatch = r.Dispatch.Sum(d => d.Pg);
        // The economic dispatch supplies demand plus the losses it was inflated to cover.
        Assert.True(System.Math.Abs(totalDispatch - (totalPd + r.TotalLossMw)) < 0.5,
            $"dispatch {totalDispatch} vs demand+losses {totalPd + r.TotalLossMw}");

        // The dispatch is AC-feasible. GR-409: this used to solve the base case (`dispatched = model`), not the dispatch;
        // it now applies the OPF's dispatch to the generators, one to one in order (as AcOptimalPowerFlow does), and
        // solves that.
        Assert.NotEmpty(r.Dispatch);
        Assert.True(r.Dispatch.Count <= model.Generators.Count, $"{r.Dispatch.Count} dispatch rows for {model.Generators.Count} units");
        var gens = new System.Collections.Generic.List<Generator>(model.Generators.Count);
        for (int i = 0; i < model.Generators.Count; i++)
        {
            if (i >= r.Dispatch.Count) { gens.Add(model.Generators[i]); continue; }   // as AcOptimalPowerFlow.ApplyDispatch
            Assert.Equal(model.Generators[i].Bus, r.Dispatch[i].GenBus);
            gens.Add(model.Generators[i] with { Pg = r.Dispatch[i].Pg });
        }
        GridModel dispatched = model.CopyWith(generators: gens);
        PowerFlowResult ac = new NewtonRaphsonPowerFlow().Solve(dispatched);
        Assert.True(ac.Converged, $"the OPF dispatch does not solve: {ac.Status}");
    }
}
