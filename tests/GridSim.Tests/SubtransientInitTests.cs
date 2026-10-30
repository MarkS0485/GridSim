using System;
using System.Collections.Generic;
using GridSim.Core.Dynamics.Distributed;
using GridSim.Core.Dynamics.Models;
using GridSim.Core.IO;
using GridSim.Core.Model;
using GridSim.Core.PowerFlow;
using Xunit;

namespace GridSim.Tests;

/// <summary>
/// Validation of the power-flow-based subtransient initialisation (<see cref="SubtransientInit"/>):
///   - FIXED POINT: a fleet seeded from a solved WSCC-9 case and run undisturbed stays stationary (the init
///     computes a true equilibrium of all six states per machine);
///   - REDUCTION TO CLASSICAL INIT: with every reactance equal to X'd the rotor angles and internal-voltage
///     magnitudes match the trusted <see cref="ClassicalInit"/>, and the q-axis EMFs are zero.
/// </summary>
public class SubtransientInitTests
{
    private static IReadOnlyList<SubtransientInit.MachineData> GenrouData(GridModel model)
    {
        var data = new List<SubtransientInit.MachineData>();
        foreach (Generator gen in model.Generators)
            data.Add(new SubtransientInit.MachineData(
                H: gen.H, Damping: 0.0,
                Xd: 1.0, Xdp: 0.30, Xdpp: 0.22, Xq: 0.65, Xqp: 0.40, Xqpp: 0.22,
                Tdop: 8.0, Tdopp: 0.03, Tqop: 0.40, Tqopp: 0.05));
        return data;
    }

    // ---- The seeded fleet is a true fixed point: undisturbed, it does not drift ----
    [Fact]
    public void Seeded_from_a_solved_case_the_fleet_is_stationary()
    {
        GridModel model = IeeeCases.Case9();
        PowerFlowResult pf = new NewtonRaphsonPowerFlow().Solve(model);

        SubtransientInit.Prepared p;
        // N271: the dense Kron path is now the test oracle (production is always sparse); same matrices, so this test's numbers are unchanged.
        using (SubtransientInit.DenseKronReferenceScope()) p = SubtransientInit.FromSolvedCase(model, pf, GenrouData(model));
        MultiMachineSubtransient.Result r = MultiMachineSubtransient.Simulate(
            p.Machines, p.H, p.Damping, p.Pm, p.Efd,
            p.Delta0, p.Eqp0, p.Edp0, p.Eqpp0, p.Edpp0, p.YReduced,
            f0: 50.0, stepSeconds: 0.005, durationSeconds: 10.0);

        Assert.True(r.StayedInSynchronism);
        for (int i = 0; i < p.Machines.Count; i++)
        {
            int last = r.Machines[i].DeltaRad.Count - 1;
            Assert.True(Math.Abs(r.Machines[i].SpeedPu[last]) < 1e-6, $"machine {i} drifted in speed");
            Assert.Equal(p.Delta0[i], r.Machines[i].DeltaRad[last], 5);
            Assert.Equal(p.Eqpp0[i], r.Machines[i].EqppPu[last], 5);
            Assert.Equal(p.Edpp0[i], r.Machines[i].EddpPu[last], 5);
        }
    }

    // ---- With flat reactances the init reduces to the classical init ----
    [Fact]
    public void Flat_reactances_reduce_to_the_classical_init()
    {
        GridModel model = IeeeCases.Case9();
        PowerFlowResult pf = new NewtonRaphsonPowerFlow().Solve(model);

        const double x = 0.20;   // every reactance equal to this X'd
        var flat = new List<SubtransientInit.MachineData>();
        var xdp = new double[model.Generators.Count];
        for (int i = 0; i < xdp.Length; i++)
        {
            flat.Add(new SubtransientInit.MachineData(
                H: model.Generators[i].H, Damping: 0.0,
                Xd: x, Xdp: x, Xdpp: x, Xq: x, Xqp: x, Xqpp: x,
                Tdop: 8.0, Tdopp: 0.03, Tqop: 0.40, Tqopp: 0.05));
            xdp[i] = x;
        }

        SubtransientInit.Prepared sub = SubtransientInit.FromSolvedCase(model, pf, flat);
        ClassicalInit.Prepared cls = ClassicalInit.FromSolvedCase(model, pf, xdp);

        for (int i = 0; i < model.Generators.Count; i++)
        {
            Assert.Equal(cls.Delta0[i], sub.Delta0[i], 9);     // same rotor angle (E behind X)
            Assert.Equal(cls.Eq[i], sub.Eqpp0[i], 9);          // |E''| = |E'| = the classical internal EMF
            Assert.Equal(0.0, sub.Edp0[i], 9);                 // no q-axis EMF when the axes are symmetric
            Assert.Equal(0.0, sub.Edpp0[i], 9);
        }
    }
}
