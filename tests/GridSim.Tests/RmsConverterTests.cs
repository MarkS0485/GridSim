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
/// Converter-interfaced plant in the RMS engine. WSCC-9 with its bus-3 unit recast as a battery: the unit
/// leaves the machine fleet and becomes a REGC_A/REEC-style phase-locked current source on a retained bus
/// (<see cref="RmsConverterState"/>, <see cref="ConverterNetwork"/>). Pinned:
///   - an undisturbed run is stationary (the converter reproduces its power-flow injection exactly);
///   - a fault at the converter's own bus drives it onto its current limit, all reactive (LVACM removes the
///     active current at a collapsed terminal), and it recovers its pre-fault power after clearing;
///   - the ramp limits the active-current recovery;
///   - opting out (<c>convertersAsCurrentSources: false</c>) is the old all-machine fleet exactly;
///   - a converter-only fleet is refused with a clear reason.
/// </summary>
public class RmsConverterTests
{
    private static readonly Dictionary<int, double> Xdp = new() { [1] = 0.0608, [2] = 0.1198, [3] = 0.1813 };

    private static GridModel Case9WithBattery(string type = "battery")
    {
        GridModel b = IeeeCases.Case9();
        return new GridModel
        {
            BaseMva = b.BaseMva, Buses = b.Buses, Branches = b.Branches, Name = b.Name, FrequencyHz = b.FrequencyHz,
            Generators = b.Generators.Select(g => g.Bus == 3 ? g with { Type = type, Name = "BESS" } : g).ToList(),
        };
    }

    private static IReadOnlyList<SubtransientInit.MachineData> Data(GridModel m) =>
        m.Generators.Select(g =>
        {
            double x = Xdp[g.Bus];
            return new SubtransientInit.MachineData(Math.Max(g.H, 1.0), 0.0, x, x, x, x, x, x, 8.0, 0.03, 0.4, 0.05);
        }).ToList();

    private static (GridModel M, PowerFlowResult Pf) Solved(string type = "battery")
    {
        GridModel m = Case9WithBattery(type);
        return (m, new NewtonRaphsonPowerFlow().Solve(m));
    }

    [Fact]
    public void Converter_leaves_the_machine_fleet_and_is_retained_as_a_bus()
    {
        (GridModel m, PowerFlowResult pf) = Solved();
        SubtransientInit.Prepared p;
        // N271: the dense Kron path is now the test oracle (production is always sparse); same matrices, so this test's numbers are unchanged.
        using (SubtransientInit.DenseKronReferenceScope()) p = SubtransientInit.FromSolvedCase(m, pf, Data(m), faultBusId: 3);
        Assert.Equal(2, p.Machines.Count);
        RmsConverterUnit u = Assert.Single(p.ConverterUnits);
        Assert.Equal(3, u.Bus);
        Assert.Equal(0.85, u.P0, 6);                       // WSCC-9 unit 3 dispatches 85 MW
        Assert.Equal(3, p.YReduced.GetLength(0));          // 2 machine internal nodes + 1 converter bus
    }

    [Fact]
    public void Undisturbed_run_is_stationary()
    {
        (GridModel m, PowerFlowResult pf) = Solved();
        SubtransientInit.Prepared p;
        // N271: the dense Kron path is now the test oracle (production is always sparse); same matrices, so this test's numbers are unchanged.
        using (SubtransientInit.DenseKronReferenceScope()) p = SubtransientInit.FromSolvedCase(m, pf, Data(m));
        MultiMachineSubtransient.Result r = MultiMachineSubtransient.Simulate(
            p.Machines, p.H, p.Damping, p.Pm, p.Efd, p.Delta0, p.Eqp0, p.Edp0, p.Eqpp0, p.Edpp0, p.YReduced,
            50.0, 0.005, 2.0, converters: p.Converters);

        RmsConverterTrace c = Assert.Single(r.Converters!);
        double v0 = pf.Vm[Array.IndexOf(pf.BusIds.ToArray(), 3)];
        Assert.All(c.TerminalVoltagePu, v => Assert.Equal(v0, v, 6));
        Assert.All(c.ActivePowerPu, pw => Assert.Equal(0.85, pw, 6));
        Assert.All(r.Machines, mt => Assert.All(mt.SpeedPu, w => Assert.True(Math.Abs(w) < 1e-6)));
    }

    [Fact]
    public void Fault_at_the_converter_bus_rides_through_on_its_current_limit_and_recovers()
    {
        (GridModel m, PowerFlowResult pf) = Solved();
        MultiMachineSubtransient.Result r = RmsTransientStability.Study(m, pf, Data(m), faultBusId: 3,
            onsetSeconds: 0.1, clearingSeconds: 0.15, stepSeconds: 0.002, durationSeconds: 3.0);
        Assert.True(r.StayedInSynchronism);
        RmsConverterTrace c = Assert.Single(r.Converters!);
        IReadOnlyList<double> t = r.Machines[0].TimeSeconds;
        int During(double s) => t.ToList().FindIndex(x => x >= s);

        int mid = During(0.2);                              // 100 ms into the fault
        double limit = 1.2 * 128.0 / 100.0;                 // k * rating: WSCC-9 unit 3 is 128 MVA on 100 MVA
        Assert.True(c.InDipMode[mid]);
        Assert.True(c.TerminalVoltagePu[mid] < 0.05, $"V {c.TerminalVoltagePu[mid]}");
        Assert.InRange(c.ReactiveCurrentPu[mid], 0.98 * limit, limit);   // reactive priority, settling on the limit
        Assert.Equal(0.0, c.ActiveCurrentPu[mid], 6);               // LVACM: no active current at a collapsed bus

        Assert.Equal(0.85, c.ActivePowerPu[^1], 2);                 // pre-fault power restored
        Assert.False(c.InDipMode[^1]);
    }

    [Fact]
    public void Active_current_recovery_is_ramp_limited()
    {
        var unit = new RmsConverterUnit(3, "u", RatingPu: 1.0, P0: 1.0, Q0: 0.0, V0: 1.0,
            new RmsConverterParameters(RampUpPuPerSecond: 2.0, LvPnt0: 0.0, LvPnt1: 0.0));
        var s = new RmsConverterState(unit);
        const double dt = 0.001;
        double t = 0;
        for (int i = 0; i < 100; i++, t += dt) s.Step(t, 0.2, dt);          // deep dip
        for (int i = 0; i < 100; i++, t += dt) s.Step(t, 0.2, dt);
        s.Current(0.2, 0, true);
        double ipInDip = s.ActiveCurrent;
        for (int i = 0; i < 100; i++, t += dt) s.Step(t, 1.0, dt);          // 0.1 s after recovery
        s.Current(1.0, 0, true);
        double rise = s.ActiveCurrent - ipInDip;
        Assert.True(rise <= 2.0 * 0.1 + 0.03, $"rise {rise} exceeds the 2 pu/s ramp");
        Assert.True(rise > 0.1, $"rise {rise} should be ramping");
    }

    [Fact]
    public void Energy_store_sustains_the_active_power_until_it_is_empty_but_not_the_reactive_support()
    {
        // 50 MW converter on a 30 MWh NMC pack at 7 % SoC (floor 5 %): about 0.6 MWh usable, ~45 s at 50 MW.
        var pack = GridSim.Core.Storage.PackModel.Sized(GridSim.Core.Storage.BatteryChemistryLibrary.Nmc,
            30e6, 1000, 0.07, ratedPowerW: 60e6);
        var store = new GridSim.Core.Assets.BatteryDcSource(pack);
        var unit = new RmsConverterUnit(1, "bess", RatingPu: 0.6, P0: 0.5, Q0: 0.0, V0: 1.0,
            RmsConverterParameters.WithLimit(1.2), store, BaseMva: 100);
        var s = new RmsConverterState(unit);
        const double dt = 0.01;
        double t = 0;
        for (int i = 0; i < 1000; i++, t += dt) s.Step(t, 1.0, dt);        // 10 s
        s.Current(1.0, 0, true);
        Assert.Equal(0.5, s.ActiveCurrent, 2);
        for (int i = 0; i < 11000; i++, t += dt) s.Step(t, 1.0, dt);       // to 120 s
        s.Current(1.0, 0, true);
        Assert.True(s.ActiveCurrent < 0.01, $"Ip {s.ActiveCurrent}");
        Assert.True(s.StoreSoc <= 0.051);
        for (int i = 0; i < 50; i++, t += dt) s.Step(t, 0.5, dt);          // a dip: reactive support still there
        Assert.True(s.ReactiveCurrent > 0.5);
    }

    [Fact]
    public void Line_commutated_link_blocks_in_the_dip_and_ramps_back()
    {
        var unit = new RmsConverterUnit(3, "lcc", RatingPu: 1.0, P0: 0.8, Q0: 0.0, V0: 1.0,
            RmsConverterParameters.WithLimit(0.0));
        Assert.True(unit.Parameters.BlockInDip);
        var s = new RmsConverterState(unit);
        const double dt = 0.001;
        double t = 0;
        for (int i = 0; i < 100; i++, t += dt) s.Step(t, 1.0, dt);      // undisturbed: carries its power
        s.Current(1.0, 0, true);
        Assert.Equal(0.8, s.ActiveCurrent, 9);
        for (int i = 0; i < 150; i++, t += dt) s.Step(t, 0.5, dt);      // dip: blocked
        s.Current(0.5, 0, true);
        Assert.True(s.InDip);
        Assert.True(Math.Abs(s.ActiveCurrent) < 1e-3 && Math.Abs(s.ReactiveCurrent) < 1e-3);
        for (int i = 0; i < 500; i++, t += dt) s.Step(t, 1.0, dt);      // recovered after the ramp
        s.Current(1.0, 0, true);
        Assert.Equal(0.8, s.ActiveCurrent, 3);
    }

    [Fact]
    public void Opting_out_is_the_all_machine_fleet_exactly()
    {
        (GridModel mb, PowerFlowResult pfb) = Solved("battery");
        (GridModel ms, PowerFlowResult pfs) = Solved("");
        MultiMachineSubtransient.Result optOut = RunStudy(mb, pfb, convertersAsCurrentSources: false);
        MultiMachineSubtransient.Result machine = RunStudy(ms, pfs, convertersAsCurrentSources: true);
        Assert.Null(optOut.Converters);
        for (int i = 0; i < 3; i++)
            Assert.Equal(machine.Machines[i].DeltaRad, optOut.Machines[i].DeltaRad);   // bit-for-bit
    }

    private static MultiMachineSubtransient.Result RunStudy(GridModel m, PowerFlowResult pf, bool convertersAsCurrentSources)
    {
        SubtransientInit.Prepared p;
        // N271: the dense Kron path is now the test oracle (production is always sparse); same matrices, so this test's numbers are unchanged.
        using (SubtransientInit.DenseKronReferenceScope()) p = SubtransientInit.FromSolvedCase(m, pf, Data(m), 7, convertersAsCurrentSources);
        var stages = new List<MultiMachineSubtransient.NetworkStage> { new(0.1, p.YReducedFaulted!), new(0.2, p.YReduced) };
        return MultiMachineSubtransient.Simulate(p.Machines, p.H, p.Damping, p.Pm, p.Efd, p.Delta0, p.Eqp0, p.Edp0,
            p.Eqpp0, p.Edpp0, p.YReduced, 50.0, 0.005, 1.0, admittanceStages: stages, converters: p.Converters);
    }

    [Fact]
    public void Power_flow_only_and_zero_inertia_units_are_held_and_the_run_stays_stationary()
    {
        GridModel b = IeeeCases.Case9();
        var m = new GridModel
        {
            BaseMva = b.BaseMva, Buses = b.Buses, Branches = b.Branches,
            Generators = b.Generators.Select(g => g.Bus switch
            {
                2 => g with { PowerFlowOnly = true, Name = "pf-only" },
                3 => g with { H = 0.0, Name = "no-inertia" },
                _ => g,
            }).ToList(),
        };
        PowerFlowResult pf = new NewtonRaphsonPowerFlow().Solve(m);
        var data = m.Generators.Select(g => new SubtransientInit.MachineData(
            g.H, 0.0, Xdp[g.Bus], Xdp[g.Bus], Xdp[g.Bus], Xdp[g.Bus], Xdp[g.Bus], Xdp[g.Bus], 8.0, 0.03, 0.4, 0.05)).ToList();
        SubtransientInit.Prepared p;
        // N271: the dense Kron path is now the test oracle (production is always sparse); same matrices, so this test's numbers are unchanged.
        using (SubtransientInit.DenseKronReferenceScope()) p = SubtransientInit.FromSolvedCase(m, pf, data);
        Assert.Single(p.Machines);
        Assert.Equal(new[] { "no-inertia" }, p.HeldUnits);
        MultiMachineSubtransient.Result r = MultiMachineSubtransient.Simulate(
            p.Machines, p.H, p.Damping, p.Pm, p.Efd, p.Delta0, p.Eqp0, p.Edp0, p.Eqpp0, p.Edpp0, p.YReduced,
            50.0, 0.005, 1.0, converters: p.Converters);
        Assert.All(r.Machines[0].SpeedPu, w => Assert.True(Math.Abs(w) < 1e-9));
    }

    [Fact]
    public void Island_without_a_machine_gets_a_reference_and_stays_at_its_voltage()
    {
        // WSCC-9 plus a detached two-bus island (its own slack, a battery and a load): the island has no
        // machine, so without a reference its converter current would float the island voltage.
        GridModel b = IeeeCases.Case9();
        var buses = b.Buses.ToList();
        buses.Add(new Bus(10, BusType.Slack, BaseKv: 230));
        buses.Add(new Bus(11, BusType.Pq, Pd: 20, Qd: 5, BaseKv: 230));
        var branches = b.Branches.ToList();
        branches.Add(new Branch(10, 11, 0.01, 0.1));
        var gens = b.Generators.ToList();
        gens.Add(new Generator(11, Pg: 15, MBase: 50, Type: "battery", Name: "island BESS"));
        var m = new GridModel { BaseMva = b.BaseMva, Buses = buses, Branches = branches, Generators = gens };
        PowerFlowResult pf = new GridSim.Core.PowerFlow.SparseNewtonRaphsonPowerFlow().Solve(m, new PowerFlowOptions());
        Assert.True(pf.Converged);

        var data = m.Generators.Select(g => new SubtransientInit.MachineData(
            Math.Max(g.H, 1.0), 0.0, 0.2, 0.2, 0.2, 0.2, 0.2, 0.2, 8.0, 0.03, 0.4, 0.05)).ToList();
        // N271: sparse is now the only production path, so the old sparseNetwork: true argument is gone (same network).
        SubtransientInit.Prepared p = SubtransientInit.FromSolvedCase(m, pf, data);
        Assert.Equal(new[] { "island reference @ bus 10" }, p.IslandReferences);

        MultiMachineSubtransient.Result r = MultiMachineSubtransient.Simulate(
            p.Machines, p.H, p.Damping, p.Pm, p.Efd, p.Delta0, p.Eqp0, p.Edp0, p.Eqpp0, p.Edpp0, p.Network!,
            50.0, 0.005, 1.0, converters: p.Converters);
        RmsConverterTrace bess = r.Converters!.Single(c => c.Name == "island BESS");
        double v0 = pf.Vm[pf.BusIds.ToList().IndexOf(11)];
        Assert.All(bess.TerminalVoltagePu, v => Assert.Equal(v0, v, 8));
    }

    [Fact]
    public void Converter_only_fleet_is_refused()
    {
        GridModel b = IeeeCases.Case9();
        var m = new GridModel
        {
            BaseMva = b.BaseMva, Buses = b.Buses, Branches = b.Branches,
            Generators = b.Generators.Select(g => g with { Type = "solar" }).ToList(),
        };
        PowerFlowResult pf = new NewtonRaphsonPowerFlow().Solve(m);
        var ex = Assert.Throws<InvalidOperationException>(() => SubtransientInit.FromSolvedCase(m, pf, Data(m)));
        Assert.Contains("No synchronous machines", ex.Message);
    }
}
