using GridSim.Core.IO;
using GridSim.Core.Model;
using GridSim.Core.PowerFlow;
using Xunit;

namespace GridSim.Tests;

/// <summary>
/// Covers the opt-in PV->PQ <em>revert</em> and the slack-Q diagnostic added to
/// <see cref="NewtonShell"/>. The revert transition is exercised directly against
/// <see cref="NewtonShell.ApplyQLimits"/> (deterministic, no dependence on whether a full solve
/// happens to overshoot), plus a full-solve check that turning the option on does not perturb a
/// well-behaved case. Agent C wrote the solver code; this is the test it did not reach.
/// </summary>
public class QLimitRevertTests
{
    // One PV bus (index 0) with Qmax = 50, Qmin = -50 MVAr, no local load, 100 MVA base.
    private static (BusType[] type0, BusType[] eff, double[] q, double[] qd, double[] qMin, double[] qMax,
                    double[] qspec, double[] vm, double[] vg, NewtonShell.QPin[] pinned, int[] rc)
        OneBus(double qGenMva, double vm, double vg, NewtonShell.QPin pinned = NewtonShell.QPin.None)
    {
        const double @base = 100.0;
        return (
            type0: new[] { BusType.Pv },
            eff: new[] { pinned == NewtonShell.QPin.None ? BusType.Pv : BusType.Pq },
            q: new[] { qGenMva / @base },      // per-unit gen reactive (qd = 0 so qGen == q*base)
            qd: new[] { 0.0 },
            qMin: new[] { -50.0 },
            qMax: new[] { 50.0 },
            qspec: new[] { 0.0 },
            vm: new[] { vm },
            vg: new[] { vg },
            pinned: new[] { pinned },
            rc: new[] { 0 });
    }

    [Fact]
    public void Forward_pin_switches_PV_to_PQ_at_Qmax()
    {
        var s = OneBus(qGenMva: 80, vm: 1.02, vg: 1.02);   // exceeds Qmax 50
        bool changed = NewtonShell.ApplyQLimits(1, s.type0, s.eff, s.q, s.qd, s.qMin, s.qMax, s.qspec,
            100.0, s.vm, s.vg, s.pinned, s.rc, revert: true);
        Assert.True(changed);
        Assert.Equal(BusType.Pq, s.eff[0]);
        Assert.Equal(NewtonShell.QPin.Max, s.pinned[0]);
        Assert.Equal(0.5, s.qspec[0], 6);                  // pinned at Qmax = 50/100 pu
    }

    [Fact]
    public void Pinned_bus_does_not_revert_when_option_is_off()
    {
        // Pinned at Qmax with the voltage overshooting the setpoint (would revert if enabled)...
        var s = OneBus(qGenMva: 50, vm: 1.05, vg: 1.02, pinned: NewtonShell.QPin.Max);
        bool changed = NewtonShell.ApplyQLimits(1, s.type0, s.eff, s.q, s.qd, s.qMin, s.qMax, s.qspec,
            100.0, s.vm, s.vg, s.pinned, s.rc, revert: false);
        Assert.False(changed);
        Assert.Equal(BusType.Pq, s.eff[0]);                // stays pinned - one-way behaviour preserved
    }

    [Fact]
    public void Bus_pinned_at_Qmax_reverts_to_PV_when_voltage_overshoots_setpoint()
    {
        // Pinned at Qmax but Vm > Vg: the max limit is injecting MORE than needed -> not binding -> revert.
        var s = OneBus(qGenMva: 50, vm: 1.05, vg: 1.02, pinned: NewtonShell.QPin.Max);
        bool changed = NewtonShell.ApplyQLimits(1, s.type0, s.eff, s.q, s.qd, s.qMin, s.qMax, s.qspec,
            100.0, s.vm, s.vg, s.pinned, s.rc, revert: true);
        Assert.True(changed);
        Assert.Equal(BusType.Pv, s.eff[0]);                // released back to voltage control
        Assert.Equal(NewtonShell.QPin.None, s.pinned[0]);
        Assert.Equal(1.02, s.vm[0], 6);                    // setpoint re-imposed as start point
        Assert.Equal(1, s.rc[0]);                          // revert counted
    }

    [Fact]
    public void Bus_pinned_at_Qmin_reverts_when_voltage_undershoots_setpoint()
    {
        var s = OneBus(qGenMva: -50, vm: 0.98, vg: 1.01, pinned: NewtonShell.QPin.Min);
        bool changed = NewtonShell.ApplyQLimits(1, s.type0, s.eff, s.q, s.qd, s.qMin, s.qMax, s.qspec,
            100.0, s.vm, s.vg, s.pinned, s.rc, revert: true);
        Assert.True(changed);
        Assert.Equal(BusType.Pv, s.eff[0]);
        Assert.Equal(NewtonShell.QPin.None, s.pinned[0]);
    }

    [Fact]
    public void Pinned_bus_stays_PQ_when_the_limit_is_genuinely_binding()
    {
        // Pinned at Qmax and Vm < Vg: the bus cannot reach its setpoint even at full output -> keep it PQ.
        var s = OneBus(qGenMva: 50, vm: 1.00, vg: 1.02, pinned: NewtonShell.QPin.Max);
        bool changed = NewtonShell.ApplyQLimits(1, s.type0, s.eff, s.q, s.qd, s.qMin, s.qMax, s.qspec,
            100.0, s.vm, s.vg, s.pinned, s.rc, revert: true);
        Assert.False(changed);
        Assert.Equal(BusType.Pq, s.eff[0]);
    }

    [Fact]
    public void A_bus_reverts_at_most_once_no_oscillation()
    {
        var s = OneBus(qGenMva: 50, vm: 1.05, vg: 1.02, pinned: NewtonShell.QPin.Max);
        // First pass reverts to PV.
        NewtonShell.ApplyQLimits(1, s.type0, s.eff, s.q, s.qd, s.qMin, s.qMax, s.qspec, 100.0,
            s.vm, s.vg, s.pinned, s.rc, revert: true);
        Assert.Equal(1, s.rc[0]);
        // If it re-pins (over limit again) and then would want to revert once more, the cap holds it.
        s.eff[0] = BusType.Pq; s.pinned[0] = NewtonShell.QPin.Max; s.q[0] = 0.50; s.vm[0] = 1.05;
        bool changed = NewtonShell.ApplyQLimits(1, s.type0, s.eff, s.q, s.qd, s.qMin, s.qMax, s.qspec,
            100.0, s.vm, s.vg, s.pinned, s.rc, revert: true);
        Assert.False(changed);                             // revertCount already 1 -> no second revert
        Assert.Equal(BusType.Pq, s.eff[0]);
    }

    [Fact]
    public void DiagnoseSlackQ_flags_a_breached_aggregate_limit_and_is_quiet_otherwise()
    {
        var type0 = new[] { BusType.Slack };
        var q = new[] { 1.20 };            // 120 MVAr at 100 MVA base
        var qd = new[] { 0.0 };
        // Tight aggregate limit -> breached.
        Assert.NotNull(NewtonShell.DiagnoseSlackQ(1, type0, q, qd,
            qMinBus: new[] { -50.0 }, qMaxBus: new[] { 50.0 }, @base: 100.0));
        // Loose limit -> no note.
        Assert.Null(NewtonShell.DiagnoseSlackQ(1, type0, q, qd,
            qMinBus: new[] { -500.0 }, qMaxBus: new[] { 500.0 }, @base: 100.0));
        // No real machine limits on the slack (0/0) -> skipped, no false alarm.
        Assert.Null(NewtonShell.DiagnoseSlackQ(1, type0, q, qd,
            qMinBus: new[] { 0.0 }, qMaxBus: new[] { 0.0 }, @base: 100.0));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Enabling_revert_does_not_perturb_a_well_behaved_case(bool sparse)
    {
        // On a case that never needs a revert, RevertQLimits=true must reproduce the default solution
        // bus-for-bus - the opt-in is safe to leave on.
        GridModel m = IeeeCases.Case30();
        var solver = new SteadyStateSolver(sparse ? new SparseNewtonRaphsonPowerFlow() : new NewtonRaphsonPowerFlow());
        PowerFlowResult baseline = solver.Solve(m, new PowerFlowOptions { RevertQLimits = false }, maxOltcPasses: 0);
        PowerFlowResult reverted = solver.Solve(m, new PowerFlowOptions { RevertQLimits = true }, maxOltcPasses: 0);
        Assert.True(baseline.Converged);
        Assert.True(reverted.Converged);
        for (int i = 0; i < baseline.Vm.Length; i++)
        {
            Assert.Equal(baseline.Vm[i], reverted.Vm[i], 8);
            Assert.Equal(baseline.VaDeg[i], reverted.VaDeg[i], 8);
        }
    }
}
