using GridSim.Core.Dynamics;
using GridSim.Core.Fleet;
using Xunit;

namespace GridSim.Tests;

/// <summary>
/// R253: a fleet unit armed on the first excursion used to stay armed for good, so in a continuously running live twin
/// every later event met an already-armed fleet and the onset stagger did nothing. A unit now re-arms once the frequency
/// has stayed inside its deadband for its own latency (FleetUnitBank, shared by the batch and live fleet engines).
/// </summary>
public sealed partial class B1SwingKernelTests
{
    [Fact]
    public void R253_a_zero_stagger_fleet_is_bit_identical_through_repeated_excursions()
    {
        // Events that take the frequency out of the deadband, back in and out again many times. Zero stagger: re-arming
        // can change nothing (the unit re-arms on the very step it leaves), so the old loop is still the oracle.
        FleetSpec fleet = FleetSpec.Uniform(5, 10_000, 1500, maxStaggerS: 0.0);
        var oldI = new OldMultiDevice(50_000, 30_000, fleet, null);
        var newI = new MultiDeviceFrequencyIntegrator(50_000, 30_000, fleet);
        for (int i = 0; i < 6000; i++)
        {
            double ev = (i / 1500) % 2 == 0 ? -1320.0 : 0.0;   // 30 s on, 30 s off, twice
            oldI.Step(0.02, ev);
            newI.Step(0.02, ev);
            AssertBits(oldI.FrequencyHz, newI.FrequencyHz, $"step {i} f");
            AssertBits(oldI.FleetFfrMw, newI.FleetFfrMw, $"step {i} FFR");
        }
    }

    [Fact]
    public void R253_a_staggered_fleet_meets_its_latency_again_on_a_second_event()
    {
        FleetSpec fleet = FleetSpec.Uniform(10, 0, 2000, maxStaggerS: 2.0);   // units start 0, 0.22, ... 2.0 s after detection
        var oldI = new OldMultiDevice(50_000, 30_000, fleet, null);
        var newI = new MultiDeviceFrequencyIntegrator(50_000, 30_000, fleet);
        void Step(double ev) { oldI.Step(0.02, ev); newI.Step(0.02, ev); }

        for (int i = 0; i < 200; i++) Step(-1320.0);           // first event, 4 s: every unit arms and delivers
        Assert.True(newI.FleetFfrMw > 0.0);
        int insideSteps = 0;
        for (int i = 0; i < 3000; i++)                          // 60 s with the deficit gone: back to nominal
        {
            Step(0.0);
            insideSteps = Math.Abs(newI.FrequencyHz - 50.0) <= 0.015 ? insideSteps + 1 : 0;
        }
        Assert.True(insideSteps * 0.02 >= 2.0, $"the frequency must sit inside the deadband for at least the longest stagger ({insideSteps * 0.02:R} s)");
        // (The two paths may already differ slightly here: the recovery overshoot left the deadband after the units had
        // re-armed, and met their latency again.)

        for (int i = 0; i < 15; i++) Step(-1320.0);             // second event, 0.3 s
        // Before R253 every unit was still armed, so all ten responded at once; now the units staggered beyond the
        // detection-to-now time are waiting again, as they did on the first event.
        Assert.True(newI.FleetFfrMw < oldI.FleetFfrMw,
            $"re-armed fleet should deliver less 0.3 s into the second event: {newI.FleetFfrMw:R} vs {oldI.FleetFfrMw:R} MW");
    }

    [Fact]
    public void R253_the_batch_twin_re_arms_too_and_a_single_loss_is_unchanged()
    {
        // Single loss: the frequency never comes back inside the deadband, so the batch run is the old one (the R080
        // oracle test covers it bit for bit). Two separated losses: the second meets the stagger again, so the fleet has
        // less fast response online around the second nadir than the old (permanently armed) loop gave.
        FleetSpec fleet = FleetSpec.Uniform(10, 0, 2000, maxStaggerS: 2.0);
        var cfg = new DynamicsConfig { DurationSeconds = 120 };
        var events = new[] { new FrequencyEvent(1.0, -1320.0), new FrequencyEvent(5.0, +1320.0), new FrequencyEvent(80.0, -1320.0) };
        (FrequencyResponse oldR, _, _) = OldFleet.Simulate(50_000, 30_000, events, fleet, cfg);
        FleetResult newR = FleetDynamics.Simulate(50_000, 30_000, events, fleet, cfg);
        // Identical until the frequency first comes back inside the deadband (after the cancelling step at 5 s); after
        // that the staggered units re-arm and the paths may part (the recovery overshoot already meets the latency).
        int k5 = (int)Math.Round(5.0 / cfg.StepSeconds);
        for (int k = 0; k <= k5; k++)
            AssertBits(oldR.Points[k].Hz, newR.Response.Points[k].Hz, $"before the frequency returns, point {k}");
        int k80 = (int)Math.Round(80.0 / cfg.StepSeconds);
        double oldMin = oldR.Points.Skip(k80).Min(p => p.Hz), newMin = newR.Response.Points.Skip(k80).Min(p => p.Hz);
        Assert.True(newMin < oldMin, $"the re-armed fleet arrests the second loss later: {newMin:R} vs {oldMin:R} Hz");
    }
}
