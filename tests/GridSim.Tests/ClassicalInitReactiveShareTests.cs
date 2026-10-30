using System;
using System.Linq;
using System.Numerics;
using GridSim.Core.Dynamics.Distributed;
using GridSim.Core.IO;
using GridSim.Core.Model;
using GridSim.Core.PowerFlow;
using Xunit;

namespace GridSim.Tests;

/// <summary>
/// GR-083: the classical preparation shares a bus's solved reactive output between co-located machines by reactive
/// capability (Qmax - Qmin), as the subtransient preparation does, not by scheduled active power. A 0 MW machine beside a
/// generating one with the same reactive range carries half the bus's MVAr.
/// </summary>
public class ClassicalInitReactiveShareTests
{
    [Fact]
    public void A_zero_MW_machine_beside_a_generating_one_takes_its_capability_share_of_the_bus_MVAr()
    {
        GridModel c = IeeeCases.Case9();
        Generator g1 = c.Generators.Single(g => g.Bus == 1);
        var model = new GridModel
        {
            BaseMva = c.BaseMva, Buses = c.Buses, Branches = c.Branches, Name = c.Name,
            Generators = c.Generators.Concat(new[] { g1 with { Pg = 0.0, Name = "G1-zero" } }).ToList(),
        };
        PowerFlowResult pf = new NewtonRaphsonPowerFlow().Solve(model);
        Assert.True(pf.Converged);

        const double xdp = 0.0608;
        double[] xdpArr = Enumerable.Repeat(xdp, model.Generators.Count).ToArray();
        ClassicalInit.Prepared p = ClassicalInit.FromSolvedCase(model, pf, xdpArr);

        int bi = pf.BusIds.ToList().IndexOf(1);
        Complex vt = Complex.FromPolarCoordinates(pf.Vm[bi], pf.VaDeg[bi] * Math.PI / 180.0);
        double busQ = pf.QInjMvar[bi] + model.Buses[bi].Qd;
        Assert.True(Math.Abs(busQ) > 1.0, $"the test needs reactive output at bus 1 (got {busQ} MVAr)");

        // The zero-MW unit (the last generator): S = j * busQ / 2 (equal ranges), on the system base.
        int k = p.MachineIndex.ToList().IndexOf(model.Generators.Count - 1);
        Assert.True(k >= 0, "the zero-MW unit must be a machine");
        double mBaseRatio = (g1.MBase > 0 ? g1.MBase : model.BaseMva) / model.BaseMva;
        Complex s = new Complex(0.0, busQ / 2.0) / model.BaseMva;
        Complex ePrime = vt + new Complex(0.0, xdp / mBaseRatio) * Complex.Conjugate(s / vt);
        // Before GR-083 this unit got no MVAr, so its EMF magnitude was |V_t| exactly.
        Assert.NotEqual(pf.Vm[bi], p.Eq[k]);
        Assert.Equal(ePrime.Magnitude, p.Eq[k], 9);
    }
}
