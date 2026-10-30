using System;
using System.Collections.Generic;
using System.Linq;
using GridSim.Core.Dynamics.Models;
using GridSim.Core.IO;
using GridSim.Core.Model;
using GridSim.Core.PowerFlow;
using Xunit;

namespace GridSim.Tests;

/// <summary>
/// The grid-forming (virtual synchronous machine) converter in the RMS engine: an EMF behind a virtual reactance
/// stamped into the network as a Norton admittance, with a swing-driven angle, an integral voltage loop and a
/// current limit. WSCC-9 with unit 3 recast as a grid-forming battery.
/// </summary>
public class RmsGridFormingTests
{
    private static readonly Dictionary<int, double> Xdp = new() { [1] = 0.0608, [2] = 0.1198, [3] = 0.1813 };

    private static (GridModel M, PowerFlowResult Pf, IReadOnlyList<SubtransientInit.MachineData> Data) Case(bool forming)
    {
        GridModel b = IeeeCases.Case9();
        var m = new GridModel
        {
            BaseMva = b.BaseMva, Buses = b.Buses, Branches = b.Branches,
            Generators = b.Generators.Select(g => g.Bus == 3 ? g with { Type = "battery", Name = "BESS", GridForming = forming } : g).ToList(),
        };
        var data = m.Generators.Select(g =>
        {
            double x = Xdp[g.Bus];
            return new SubtransientInit.MachineData(Math.Max(g.H, 1.0), 0.0, 3 * x, x, 0.8 * x, 2.5 * x, 1.5 * x, 0.8 * x, 8, 0.03, 0.4, 0.05);
        }).ToList();
        return (m, new NewtonRaphsonPowerFlow().Solve(m), data);
    }

    private static MultiMachineSubtransient.Result Undisturbed(bool sparse)
    {
        (GridModel m, PowerFlowResult pf, var data) = Case(forming: true);
        SubtransientInit.Prepared p;
        // N271: sparse is the production path; the dense Kron path is the test oracle (was sparseNetwork: false). Same
        // two networks as before, so both rows of the theory check exactly what they did.
        using (sparse ? null : SubtransientInit.DenseKronReferenceScope()) p = SubtransientInit.FromSolvedCase(m, pf, data);
        return sparse
            ? MultiMachineSubtransient.Simulate(p.Machines, p.H, p.Damping, p.Pm, p.Efd, p.Delta0, p.Eqp0, p.Edp0, p.Eqpp0, p.Edpp0,
                p.Network!, 50, 0.005, 1.0, converters: p.Converters)
            : MultiMachineSubtransient.Simulate(p.Machines, p.H, p.Damping, p.Pm, p.Efd, p.Delta0, p.Eqp0, p.Edp0, p.Eqpp0, p.Edpp0,
                p.YReduced, 50, 0.005, 1.0, converters: p.Converters);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Grid_forming_unit_is_stationary_at_flat_start(bool sparse)
    {
        MultiMachineSubtransient.Result r = Undisturbed(sparse);
        RmsConverterTrace c = Assert.Single(r.Converters!);
        Assert.All(c.ActivePowerPu, p => Assert.Equal(0.85, p, 6));
        Assert.All(c.TerminalVoltagePu, v => Assert.Equal(c.TerminalVoltagePu[0], v, 8));
        Assert.All(r.Machines, mt => Assert.All(mt.SpeedPu, w => Assert.True(Math.Abs(w) < 1e-8)));
    }

    [Fact]
    public void Fault_at_the_grid_forming_bus_holds_it_at_its_current_limit()
    {
        (GridModel m, PowerFlowResult pf, var data) = Case(forming: true);
        MultiMachineSubtransient.Result r = RmsTransientStability.Study(m, pf, data, faultBusId: 3,
            onsetSeconds: 0.1, clearingSeconds: 0.1, stepSeconds: 0.002, durationSeconds: 2.0);
        RmsConverterTrace c = Assert.Single(r.Converters!);
        int k = r.Machines[0].TimeSeconds.ToList().FindIndex(t => t >= 0.15);
        double limit = 1.2 * 128.0 / 100.0;
        double iMag = Math.Sqrt(c.ActiveCurrentPu[k] * c.ActiveCurrentPu[k] + c.ReactiveCurrentPu[k] * c.ReactiveCurrentPu[k]);
        Assert.True(c.InDipMode[k]);
        Assert.Equal(limit, iMag, 3);
        Assert.True(r.StayedInSynchronism);
    }

    [Fact]
    public void Grid_forming_holds_its_terminal_voltage_better_at_fault_onset_than_grid_following()
    {
        double VoltageJustAfterOnset(bool forming)
        {
            (GridModel m, PowerFlowResult pf, var data) = Case(forming);
            MultiMachineSubtransient.Result r = RmsTransientStability.Study(m, pf, data, faultBusId: 5,
                onsetSeconds: 0.1, clearingSeconds: 0.1, stepSeconds: 0.002, durationSeconds: 0.3);
            int k = r.Machines[0].TimeSeconds.ToList().FindIndex(t => t >= 0.1);
            return r.Converters![0].TerminalVoltagePu[k];
        }
        double gfm = VoltageJustAfterOnset(true), gfl = VoltageJustAfterOnset(false);
        Assert.True(gfm > gfl + 0.02, $"GFM {gfm:F3} pu vs GFL {gfl:F3} pu at onset");
    }

    [Fact]
    public void Virtual_speed_swings_on_a_disturbance_and_is_damped_back()
    {
        (GridModel m, PowerFlowResult pf, var data) = Case(forming: true);
        SubtransientInit.Prepared p;
        // N271: the dense Kron path is now the test oracle (production is always sparse); same matrices, so this test's numbers are unchanged.
        using (SubtransientInit.DenseKronReferenceScope()) p = SubtransientInit.FromSolvedCase(m, pf, data, faultBusId: 7);
        var stages = new List<MultiMachineSubtransient.NetworkStage> { new(0.1, p.YReducedFaulted!), new(0.18, p.YReduced) };
        var state = new List<double>();
        MultiMachineSubtransient.Result r = MultiMachineSubtransient.Simulate(p.Machines, p.H, p.Damping, p.Pm, p.Efd,
            p.Delta0, p.Eqp0, p.Edp0, p.Eqpp0, p.Edpp0, p.YReduced, 50, 0.002, 6.0, admittanceStages: stages, converters: p.Converters);
        Assert.True(r.StayedInSynchronism);
        RmsConverterTrace c = r.Converters![0];
        // It carried inertial / damping power during the swing, and settles back to its set-point.
        double maxDev = c.ActivePowerPu.Max(x => Math.Abs(x - 0.85));
        Assert.True(maxDev > 0.05, $"power swing {maxDev:F3} pu");
        Assert.Equal(0.85, c.ActivePowerPu[^1], 1);
    }
}
