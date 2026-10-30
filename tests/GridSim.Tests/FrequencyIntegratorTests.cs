using System;
using GridSim.Core.Dynamics;
using Xunit;

namespace GridSim.Tests;

/// <summary>
/// The stateful <see cref="FrequencyIntegrator"/> that drives the live dashboard frequency at an
/// arbitrary time-step. Checks it reproduces the swing physics of <see cref="FrequencyDynamics"/>,
/// stays put with no event, and stays finite/stable at a huge deltat.
/// </summary>
public class FrequencyIntegratorTests
{
    private const double InertiaMwS = 200_000; // ~GB-scale spinning energy
    private const double DemandMw = 30_000;

    [Fact]
    public void No_event_holds_at_nominal()
    {
        var f = new FrequencyIntegrator(InertiaMwS, DemandMw);
        for (int i = 0; i < 1000; i++) f.Step(0.1, 0.0);
        Assert.Equal(50.0, f.FrequencyHz, 9);   // no imbalance => dead flat at f0
        Assert.Equal(0.0, f.RocofHzPerS, 9);
    }

    [Fact]
    public void A_generation_loss_drops_the_frequency_with_the_expected_initial_rocof()
    {
        double lost = 1000.0;
        var f = new FrequencyIntegrator(InertiaMwS, DemandMw);
        // First instant: RoCoF ~ f0/(2E)*(-lost), governor not yet responding.
        f.Step(0.02, -lost);
        double expected = 50.0 / (2.0 * InertiaMwS) * -lost;
        Assert.Equal(expected, f.RocofHzPerS, 4);
        Assert.True(f.FrequencyHz < 50.0);

        // Let it settle: droop response arrests the fall at a steady offset below nominal, not NaN.
        for (int i = 0; i < 2000; i++) f.Step(0.02, -lost);
        Assert.InRange(f.FrequencyHz, 47.0, 50.0);
    }

    [Fact]
    public void Matches_the_batch_simulate_trajectory_for_a_trip()
    {
        var cfg = new DynamicsConfig { DurationSeconds = 20 };
        FrequencyResponse batch = FrequencyDynamics.Simulate(
            InertiaMwS, DemandMw, new[] { new FrequencyEvent(0.0, -1000.0) }, cfg);

        var inc = new FrequencyIntegrator(InertiaMwS, DemandMw, cfg);
        int steps = (int)(cfg.DurationSeconds / cfg.StepSeconds);
        for (int i = 0; i < steps; i++) inc.Step(cfg.StepSeconds, -1000.0);

        // Same equations => same settle point (within a hair; off-by-one boundary in the batch loop).
        Assert.Equal(batch.SettleHz, inc.FrequencyHz, 2);
    }

    [Fact]
    public void Large_step_stays_finite_and_matches_substepped_result()
    {
        // One 60 s step vs 3000 x 20 ms steps under the same deficit - sub-stepping must agree.
        var a = new FrequencyIntegrator(InertiaMwS, DemandMw);
        a.Step(60.0, -800.0);
        var b = new FrequencyIntegrator(InertiaMwS, DemandMw);
        for (int i = 0; i < 3000; i++) b.Step(0.02, -800.0);

        Assert.True(double.IsFinite(a.FrequencyHz));
        Assert.Equal(b.FrequencyHz, a.FrequencyHz, 1);   // internal sub-steps => same answer as fine stepping

        // And a 20-minute step doesn't explode.
        var c = new FrequencyIntegrator(InertiaMwS, DemandMw);
        c.Step(1200.0, -800.0);
        Assert.True(double.IsFinite(c.FrequencyHz));
        Assert.InRange(c.FrequencyHz, 47.0, 51.0);
    }

    [Fact]
    public void The_clamped_20_minute_step_still_settles_where_fine_stepping_does()
    {
        // A 1200 s step is split into MaxSubSteps=4000 internal steps, so h degrades to 0.3 s (15x the 20 ms
        // norm). The droop equilibrium is h-independent, so the clamped single step must land where a fine
        // 20 ms sweep of the same duration lands - not merely "finite". (test-gap: clamp asserted only finite)
        var coarse = new FrequencyIntegrator(InertiaMwS, DemandMw);
        coarse.Step(1200.0, -800.0);

        var fine = new FrequencyIntegrator(InertiaMwS, DemandMw);
        for (int i = 0; i < 60_000; i++) fine.Step(0.02, -800.0);   // 1200 s at 20 ms

        Assert.Equal(fine.FrequencyHz, coarse.FrequencyHz, 1);   // same settle point within ~0.05 Hz
    }

    [Fact]
    public void Nadir_matches_the_batch_simulate()
    {
        // The headline metric is the nadir, but the trajectory-match test only checks the settle point. Track the
        // running minimum of the stateful integrator and compare it to the batch nadir - same physics, same nadir.
        var cfg = new DynamicsConfig { DurationSeconds = 20 };
        FrequencyResponse batch = FrequencyDynamics.Simulate(
            InertiaMwS, DemandMw, new[] { new FrequencyEvent(0.0, -1200.0) }, cfg);

        var inc = new FrequencyIntegrator(InertiaMwS, DemandMw, cfg);
        int steps = (int)(cfg.DurationSeconds / cfg.StepSeconds);
        double nadir = 50.0;
        for (int i = 0; i < steps; i++) { inc.Step(cfg.StepSeconds, -1200.0); nadir = Math.Min(nadir, inc.FrequencyHz); }

        Assert.Equal(batch.NadirHz, nadir, 5);
    }

    [Fact]
    public void Fast_response_arrests_the_fall_above_the_no_device_nadir()
    {
        // Exercise the FFR/deadband branch (dead in the rest of this file - every other test leaves FastResponseMw
        // at 0): with fast response armed, the nadir must be strictly shallower than the device-free run.
        double RunNadir(DynamicsConfig cfg)
        {
            var f = new FrequencyIntegrator(InertiaMwS, DemandMw, cfg);
            double nadir = 50.0;
            for (int i = 0; i < 1500; i++) { f.Step(0.02, -1200.0); nadir = Math.Min(nadir, f.FrequencyHz); }
            return nadir;
        }

        double noDevice = RunNadir(new DynamicsConfig());
        double withFfr = RunNadir(new DynamicsConfig { FastResponseMw = 2000 });
        Assert.True(withFfr > noDevice,
            $"fast response should lift the nadir: {withFfr:F3} Hz with FFR vs {noDevice:F3} Hz without");
    }

    [Fact]
    public void A_sub_deadband_deficit_never_engages_fast_response()
    {
        // A deficit tiny enough that |deltaf| stays inside the FFR deadband must leave the fast response dormant, so the
        // trajectory is identical whether or not a device is configured. Pins the deadband guard on the branch.
        double Run(DynamicsConfig cfg)
        {
            var f = new FrequencyIntegrator(5_000_000, DemandMw, cfg);   // huge inertia => deltaf stays sub-deadband
            for (int i = 0; i < 500; i++) f.Step(0.02, -5.0);
            return f.FrequencyHz;
        }
        Assert.Equal(Run(new DynamicsConfig()), Run(new DynamicsConfig { FastResponseMw = 2000 }), 9);
    }

    [Fact]
    public void A_mid_run_dt_change_carries_state_and_settles_the_same()
    {
        // Switching deltat part-way through a run (as the live STEP control does) must carry governor/FFR state across
        // the change and still reach the same droop equilibrium as an all-fine run - the state isn't reset per call.
        var allFine = new FrequencyIntegrator(InertiaMwS, DemandMw);
        for (int i = 0; i < 3000; i++) allFine.Step(0.02, -800.0);       // 60 s at 20 ms

        var mixed = new FrequencyIntegrator(InertiaMwS, DemandMw);
        for (int i = 0; i < 250; i++) mixed.Step(0.02, -800.0);          // first 5 s fine
        for (int i = 0; i < 110; i++) mixed.Step(0.5, -800.0);           // next 55 s coarse

        Assert.True(double.IsFinite(mixed.FrequencyHz));
        Assert.Equal(allFine.FrequencyHz, mixed.FrequencyHz, 1);         // same settle within ~0.05 Hz
    }

    [Fact]
    public void A_second_larger_loss_deepens_the_excursion()
    {
        // A second contingency mid-run (a larger net deficit) must drive frequency down further - the RoCoF turns
        // sharply negative again and the frequency ends below where the first loss had settled. (test-gap: no
        // second-trip discontinuity was exercised.)
        var f = new FrequencyIntegrator(InertiaMwS, DemandMw);
        for (int i = 0; i < 500; i++) f.Step(0.02, -500.0);   // settle under a modest deficit
        double afterFirst = f.FrequencyHz;

        f.Step(0.02, -1500.0);                                // second, larger loss
        Assert.True(f.RocofHzPerS < 0, "the larger deficit should push RoCoF sharply negative again");
        for (int i = 0; i < 500; i++) f.Step(0.02, -1500.0);
        Assert.True(f.FrequencyHz < afterFirst,
            $"a deeper deficit should settle lower: {f.FrequencyHz:F3} vs {afterFirst:F3} Hz");
    }
}
