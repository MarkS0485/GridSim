using GridSim.Core.Dynamics.LongTerm;
using GridSim.Core.Dynamics.Models;
using GridSim.Core.Model;
using GridSim.Core.PowerFlow;
using Xunit;

namespace GridSim.Tests;

/// <summary>
/// RC5 (2.1.126, review of WP21): the step schedule refuses user and plug-in models in the library API too, the QSS engine
/// reports its own frequency loop running out of passes as a numerical failure, and the AGC integrator does not wind up
/// while its units are held at a limit.
/// </summary>
public sealed class Rc5ReviewFixWp21Tests
{
    /// <summary>A user field controller that holds its starting field (the simplest model GridSim cannot inspect).</summary>
    private sealed class HoldField : IRmsFieldController
    {
        private double _efd;
        public void Initialize(double fieldVoltagePu) => _efd = fieldVoltagePu;
        public double EquilibriumErrorPu(double fieldVoltagePu) => 0.0;
        public double Step(double voltageErrorPu, double speedDeviationPu, double dtSeconds) => _efd;
    }

    private static RmsExciter?[] UserExciters(int n)
    {
        var x = new RmsExciter?[n];
        for (int i = 0; i < n; i++) x[i] = RmsExciter.FromUser(new HoldField(), "USER:hold");
        return x;
    }

    [Fact]
    public void A_user_field_controller_is_refused_on_a_schedule_with_more_than_one_step_size_and_runs_on_one()
    {
        (GridModel m, PowerFlowResult pf, IReadOnlyList<SubtransientInit.MachineData> data) = Rc5Wp21BitIdentityTests.Smib();
        RmsEvent[] ev = Rc5Wp21BitIdentityTests.SmibFault();
        var two = new RmsStepSchedule(new[] { new RmsStepWindow(0.0, 0.001), new RmsStepWindow(0.5, 0.005) });
        ArgumentException ex = Assert.ThrowsAny<ArgumentException>(() =>
            RmsTransientStability.StudyEventsScheduled(m, pf, data, ev, two, durationSeconds: 1.0, exciters: UserExciters(data.Count)));
        Assert.Contains("is a user or plug-in model (USER:hold)", ex.Message, StringComparison.Ordinal);
        Assert.Contains("cannot check that it takes the step on each call", ex.Message, StringComparison.Ordinal);
        // One step size: the run is a plain fixed-step run, so the user model is taken.
        MultiMachineSubtransient.Result r = RmsTransientStability.StudyEventsScheduled(m, pf, data, ev, RmsStepSchedule.Fixed(0.001),
            durationSeconds: 1.0, exciters: UserExciters(data.Count));
        Assert.Equal(1.0, r.Machines[0].TimeSeconds[^1]);
    }

    [Fact]
    public void An_event_inside_a_coarse_window_is_named_before_the_window_grid_is_checked()
    {
        // The review's case: (0, 1 ms), (0.05 s, 4 ms) over 1 s leaves a last window of 237.49999999999997 steps (not whole)
        // and the fault at 0.1 s inside the 4 ms window. The event is placed first, so the message names the coarse window.
        (GridModel m, PowerFlowResult pf, IReadOnlyList<SubtransientInit.MachineData> data) = Rc5Wp21BitIdentityTests.Smib();
        var s = new RmsStepSchedule(new[] { new RmsStepWindow(0.0, 0.001), new RmsStepWindow(0.05, 0.004) });
        ArgumentException grid = Assert.ThrowsAny<ArgumentException>(() => s.Expand(1.0));
        Assert.Contains("not a whole number", grid.Message, StringComparison.Ordinal);
        ArgumentException ex = Assert.ThrowsAny<ArgumentException>(() =>
            RmsTransientStability.StudyEventsScheduled(m, pf, data, Rc5Wp21BitIdentityTests.SmibFault(), s, durationSeconds: 1.0));
        Assert.Contains("coarse window", ex.Message, StringComparison.Ordinal);
    }

    private static GridModel TwoBus(double loadMw, double pmax) => new()
    {
        Name = "lt2",
        BaseMva = 100.0,
        Buses = new[] { new Bus(1, BusType.Slack, BaseKv: 132), new Bus(2, BusType.Pq, Pd: loadMw, Qd: 0.0, BaseKv: 132) },
        Generators = new[] { new Generator(1, Pg: loadMw, Vg: 1.0, MBase: 100, Pmax: pmax, Name: "G1") },
        Branches = new[] { new Branch(1, 2, 0.0, 0.1, Name: "L1") },
    };

    [Fact]
    public void The_frequency_loop_running_out_of_passes_is_a_numerical_failure_not_a_loss_of_equilibrium()
    {
        // One pass allowed: at the load step the first pass starts from df = 0 and leaves the whole 20 MW on the reference
        // bus, so the loop cannot settle in one pass. A power flow with a solution exists, so this is GridSim's loop failing.
        var c = new LongTermCase(1.0, 5.0, 0.0, new[] { new LongTermUnit(0, 0.05) }, null, Array.Empty<OltcTimerSpec>(),
            Array.Empty<RecoveringLoadSpec>(), new LongTermEvent[] { new LongTermLoadStep(2.0, 2, 20.0, 0.0) })
        { MaxEquilibriumPasses = 1 };
        LongTermResult r = QssEngine.Run(TwoBus(50.0, 1000.0), c);
        Assert.False(r.Completed);
        Assert.StartsWith("numerical failure at t = 2 s: the frequency loop did not settle within 1 passes", r.StopReason!, StringComparison.Ordinal);
        Assert.Equal(2, r.Samples.Count);   // t = 0 and t = 1 s
        // With the default cap the same case runs to the end.
        Assert.True(QssEngine.Run(TwoBus(50.0, 1000.0), c with { MaxEquilibriumPasses = 30 }).Completed);
    }

    [Fact]
    public void The_agc_integrator_is_frozen_only_in_the_direction_its_units_cannot_follow()
    {
        var agc = new AgcModel(0.01, 80.0, null, new[] { 1.0 });
        agc.Advance(-1.0, 1.0, allAtUpper: true);          // ACE = -80 MW: a raise, frozen at the upper limit
        Assert.Equal(0.0, agc.TotalMw);
        agc.Advance(-1.0, 1.0, allAtLower: true);          // a raise is not blocked by the lower limit: 0.01 x 80 x 1
        Assert.Equal(0.8, agc.TotalMw);
        agc.Advance(1.0, 1.0, allAtUpper: true);           // a lower is not blocked by the upper limit
        Assert.Equal(0.0, agc.TotalMw);
        agc.Advance(1.0, 1.0, allAtLower: true);           // a lower at the lower limit: frozen
        Assert.Equal(0.0, agc.TotalMw);
    }

    [Fact]
    public void The_agc_does_not_wind_up_while_its_unit_is_at_pmax()
    {
        // G1 (R = 0.05, 1/R = 40 MW/Hz) at 100 MW with Pmax 150 MW, load damping 10 MW/Hz, AGC on G1. A 100 MW load step at
        // t = 1 s drives G1 to Pmax (raw set point 100 + 40 |df| above 150), so the rest is the damping's: df = -50 / 10 =
        // -5 Hz while the step is in. Every step G1 sits at Pmax, so P_agc must stay 0 (without the freeze it would integrate
        // Ki B 5 Hz each step). The step is removed at t = 10 s: the system is back at its start, and the AGC with it.
        var c = new LongTermCase(1.0, 15.0, 10.0, new[] { new LongTermUnit(0, 0.05) },
            new AgcSpec(0.01, null, null, new[] { new AgcUnit(0, 1.0) }), Array.Empty<OltcTimerSpec>(), Array.Empty<RecoveringLoadSpec>(),
            new LongTermEvent[] { new LongTermLoadStep(1.0, 2, 100.0, 0.0), new LongTermLoadStep(10.0, 2, -100.0, 0.0) })
        {
            EquilibriumToleranceMw = 1e-9,
            PowerFlowTolerancePu = 1e-12,
        };
        LongTermResult r = QssEngine.Run(TwoBus(100.0, 150.0), c);
        Assert.True(r.Completed, r.StopReason);
        // Up to t = 10 s (the sample shows P_agc before that step advances it) P_agc is exactly 0: df is exactly 0 before
        // the step and every step in it is frozen. After the step is removed df is 0 to within the loop's tolerance: each
        // equilibrium leaves at most 1e-9 MW (the stop) + 1e-10 MW (Newton) unshared, so |df| <= 1.1e-9 / beta with beta =
        // D + 1/R = 50 MW/Hz, and each step adds at most Ki B |df| h = 0.01 x 50 x 2.2e-11 = 1.1e-11 MW; five steps give
        // at most 5.5e-11 MW, asserted to 1e-10 MW.
        for (int n = 0; n < r.Samples.Count; n++)
        {
            double p = r.Samples[n].AgcUnitMw[0];
            if (r.Samples[n].TimeSeconds <= 10.0) Assert.True(p == 0.0, $"t = {r.Samples[n].TimeSeconds}: P_agc {p:R}");
            else Assert.True(Math.Abs(p) <= 1e-10, $"t = {r.Samples[n].TimeSeconds}: P_agc {p:R}");
        }
        Assert.True(r.Samples[5].FrequencyDeviationHz < -4.9, $"df at t = 5 s: {r.Samples[5].FrequencyDeviationHz}");
    }
}
