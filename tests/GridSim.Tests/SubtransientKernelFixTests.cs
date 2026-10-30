using System.Numerics;
using GridSim.Core.Dynamics.Distributed;
using GridSim.Core.Dynamics.Models;
using GridSim.Core.IO;
using GridSim.Core.Model;
using GridSim.Core.PowerFlow;
using Xunit;

namespace GridSim.Tests;

/// <summary>
/// R247: the subtransient kernel's loss-of-synchronism test is the classical COI-relative one.
/// R248: a grid-following converter draws from its store only the power it actually injects.
/// R249: a network stage formed as onset + clearing lands on its intended step despite rounding.
/// R250: SubtransientInit rejects X''d != X''q (the network fold assumes them equal).
/// </summary>
public class SubtransientKernelFixTests
{
    private const double F0 = 50.0;

    private static Complex[,] Tie(double xTot)
    {
        Complex y = Complex.One / new Complex(0.0, xTot);
        return new[,] { { y, -y }, { -y, y } };
    }

    private static MultiMachineSubtransient.Machine Flat() => new(
        Xd: 0.3, Xdp: 0.3, Xdpp: 0.3, Xq: 0.3, Xqp: 0.3, Xqpp: 0.3,
        Tdop: 6.0, Tdopp: 0.04, Tqop: 0.4, Tqopp: 0.06);

    // ------------------------------------------------------------------ R247

    /// <summary>Two equal, effectively uncoupled machines driven apart by opposite mechanical powers: their
    /// angle spread grows as ws * p / (2H) * t^2 while the COI stays put, each machine sitting spread/2 from it.</summary>
    private static (bool Classical, bool Subtransient, double FinalSpread) DriftApart(double durationSeconds)
    {
        var h = new[] { 5.0, 5.0 };
        var d = new[] { 0.0, 0.0 };
        var pm = new[] { 0.01, -0.01 };
        var eq0 = new[] { 1.0, 1.0 };
        var delta0 = new[] { 0.0, 0.0 };
        Complex[,] y = Tie(1e6);   // negligible coupling
        const double dt = 0.005;

        ClassicalMultiMachine.Result cls = ClassicalMultiMachine.Simulate(h, d, pm, eq0, delta0, y, F0, dt, durationSeconds);
        MultiMachineSubtransient.Result sub = MultiMachineSubtransient.Simulate(
            new[] { Flat(), Flat() }, h, d, pm, efd: eq0,
            delta0, eqp0: eq0, edp0: new[] { 0.0, 0.0 }, eqpp0: eq0, edpp0: new[] { 0.0, 0.0 },
            y, F0, dt, durationSeconds);
        int last = sub.Machines[0].DeltaRad.Count - 1;
        double spread = Math.Abs(sub.Machines[0].DeltaRad[last] - sub.Machines[1].DeltaRad[last]);
        return (cls.StayedInSynchronism, sub.StayedInSynchronism, spread);
    }

    [Fact]
    public void A_spread_between_2pi_and_4pi_is_in_synchronism_for_both_kernels()
    {
        // Each machine is spread/2 < 2 pi from the COI: in synchronism by the classical definition. The old
        // subtransient spread test (spread > 2 pi) declared it lost - the two kernels disagreed.
        (bool cls, bool sub, double spread) = DriftApart(5.5);
        Assert.InRange(spread, 2.0 * Math.PI * 1.05, 4.0 * Math.PI * 0.95);
        Assert.True(cls);
        Assert.Equal(cls, sub);
    }

    [Fact]
    public void A_spread_beyond_4pi_is_lost_for_both_kernels()
    {
        (bool cls, bool sub, double spread) = DriftApart(8.0);
        Assert.True(spread > 4.0 * Math.PI * 1.05, $"spread {spread}");
        Assert.False(cls);
        Assert.False(sub);
    }

    // ------------------------------------------------------------------ R249

    private static MultiMachineSubtransient.Result FaultRun(double onset, double clear)
    {
        var h = new[] { 4.0, 5.0 };
        var d = new[] { 0.0, 0.0 };
        var eq0 = new[] { 1.10, 1.00 };
        Complex[,] y = Tie(0.5);
        var deltaEq = new[] { 0.30, 0.0 };
        (double[] pm, _, _) = SubtransientNetworkInterface.Inject(eq0, new[] { 0.0, 0.0 }, deltaEq, y);
        var stages = new List<MultiMachineSubtransient.NetworkStage>
        {
            new(onset, Tie(5.0)),   // weakened tie during the fault
            new(clear, y),
        };
        return MultiMachineSubtransient.Simulate(
            new[] { Flat(), Flat() }, h, d, pm, efd: eq0,
            deltaEq, eqp0: eq0, edp0: new[] { 0.0, 0.0 }, eqpp0: eq0, edpp0: new[] { 0.0, 0.0 },
            y, F0, stepSeconds: 0.005, durationSeconds: 1.0, admittanceStages: stages);
    }

    private static void AssertSameTrajectory(MultiMachineSubtransient.Result a, MultiMachineSubtransient.Result b)
    {
        for (int i = 0; i < a.Machines.Count; i++)
            for (int k = 0; k < a.Machines[i].DeltaRad.Count; k++)
                Assert.Equal(a.Machines[i].DeltaRad[k], b.Machines[i].DeltaRad[k]);
    }

    [Fact]
    public void A_clearing_time_rounded_one_ulp_high_still_clears_on_its_step()
    {
        // 0.1 + 0.14 = 0.24000000000000002 > 48 * 0.005 = 0.24: the fault used to persist to step 49 (145 ms).
        double rounded = 0.1 + 0.14;
        Assert.True(rounded > 48 * 0.005, "precondition: the sum rounds above the grid point");
        AssertSameTrajectory(FaultRun(0.1, rounded), FaultRun(0.1, 0.24));
    }

    [Fact]
    public void A_stage_genuinely_between_grid_points_still_waits_for_the_next_step()
    {
        // 0.2425 s lies half-way between steps 48 and 49: it takes effect at step 49, exactly like 0.245.
        AssertSameTrajectory(FaultRun(0.1, 0.2425), FaultRun(0.1, 0.245));
        // ...and that genuinely differs from clearing at step 48.
        MultiMachineSubtransient.Result early = FaultRun(0.1, 0.24), late = FaultRun(0.1, 0.245);
        int lastK = early.Machines[0].DeltaRad.Count - 1;
        Assert.NotEqual(early.Machines[0].DeltaRad[lastK], late.Machines[0].DeltaRad[lastK]);
    }

    // ------------------------------------------------------------------ R250

    [Fact]
    public void Unequal_subtransient_reactances_fail_loud_at_initialisation()
    {
        GridModel model = IeeeCases.Case9();
        PowerFlowResult pf = new NewtonRaphsonPowerFlow().Solve(model);
        var data = model.Generators.Select(g => new SubtransientInit.MachineData(
            H: g.H, Damping: 0.0, Xd: 1.0, Xdp: 0.30, Xdpp: 0.22, Xq: 0.65, Xqp: 0.40, Xqpp: 0.25,
            Tdop: 8.0, Tdopp: 0.03, Tqop: 0.40, Tqopp: 0.05)).ToList();
        var ex = Assert.Throws<ArgumentException>(() => SubtransientInit.FromSolvedCase(model, pf, data));
        Assert.Contains("X''d", ex.Message);
    }

    [Fact]
    public void Equal_subtransient_reactances_initialise_as_before()
    {
        GridModel model = IeeeCases.Case9();
        PowerFlowResult pf = new NewtonRaphsonPowerFlow().Solve(model);
        var data = model.Generators.Select(g => new SubtransientInit.MachineData(
            H: g.H, Damping: 0.0, Xd: 1.0, Xdp: 0.30, Xdpp: 0.22, Xq: 0.65, Xqp: 0.40, Xqpp: 0.22,
            Tdop: 8.0, Tdopp: 0.03, Tqop: 0.40, Tqopp: 0.05)).ToList();
        SubtransientInit.Prepared p = SubtransientInit.FromSolvedCase(model, pf, data);
        Assert.Equal(3, p.Machines.Count);
    }

    // ------------------------------------------------------------------ R248

    [Fact]
    public void A_deep_dip_does_not_drain_the_store_for_power_that_was_never_injected()
    {
        // Below LvPnt0 the LVACM multiplier is 0: no active power is injected however large the (frozen)
        // active current is, so the store's state of charge must not move during the dip.
        // The reference is an identical store that delivers nothing over the same steps (a zero draw still
        // carries the pack's own rest behaviour), so the comparison isolates what the converter debits.
        static GridSim.Core.Assets.BatteryDcSource NewStore() => new(GridSim.Core.Storage.PackModel.Sized(
            GridSim.Core.Storage.BatteryChemistryLibrary.Nmc, 30e6, 1000, 0.5, ratedPowerW: 60e6));
        var store = NewStore();
        var unit = new RmsConverterUnit(1, "bess", RatingPu: 0.6, P0: 0.5, Q0: 0.0, V0: 1.0,
            RmsConverterParameters.WithLimit(1.2), store, BaseMva: 100);
        var s = new RmsConverterState(unit);
        Assert.True(0.2 < unit.Parameters.LvPnt0, "precondition: 0.2 pu is below the LVACM zero point");
        const double dt = 0.01;
        double t = 0;
        var reference = NewStore();
        for (int i = 0; i < 10; i++, t += dt)                       // settle at nominal voltage
        {
            s.Step(t, 1.0, dt);
            reference.Draw(50.0, dt);                               // P0 = 0.5 pu on 100 MVA at 1 pu
        }
        for (int i = 0; i < 100; i++, t += dt)                      // 1 s deep dip: nothing injected
        {
            s.Step(t, 0.2, dt);
            reference.Draw(0.0, dt);
        }
        Assert.Equal(reference.SocFraction, s.StoreSoc);
    }
}
