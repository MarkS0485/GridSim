using GridSim.Core.Model;
using GridSim.Core.PowerFlow;
using GridSim.Core.Simulation;
using GridSim.Web.Projection;
using GridSim.Web.Sessions;
using Xunit;

namespace GridSim.Tests;

/// <summary>
/// F-047: a bridge session passed stepMinutes 0, so its MECHANICAL service was built with a silent 1.0 minute
/// divisor and kept it for ever. A 30-minute lake tick had every MW/min ramp overstated 30 times. The bridge
/// service now divides by the real gap between tick instants, and a fixed-step service refuses an invalid step.
/// </summary>
public class F047BridgeMechanicalStepTests
{
    /// <summary>One synchronous unit at <paramref name="pg"/> MW (the ramp only reads Generator.Pg).</summary>
    private static GridModel OneUnit(double pg) => new()
    {
        BaseMva = 100,
        Buses = new[] { new Bus(1, BusType.Slack, BaseKv: 132), new Bus(2, BusType.Pq, Pd: 100, BaseKv: 132) },
        Branches = new[] { new Branch(1, 2, 0.01, 0.1) },
        Generators = new[] { new Generator(1, pg, Pmax: 200, MBase: 200, H: 4, Type: "CCGT", Name: "Unit A") },
    };

    private static SimFrame Frame(GridModel m, int tick) => new(m, new NewtonRaphsonPowerFlow().Solve(m),
        Hour: tick / 2.0, Tick: tick, FrequencyHz: 50, GenerationMw: m.Generators.Sum(g => g.Pg), DemandMw: 100);

    private static readonly DateTime T0 = new(2026, 6, 1, 12, 0, 0, DateTimeKind.Utc);

    private static double Ramp(MechanicalDto d) => d.Machines.Single().RampMwPerMin;

    [Theory]
    [InlineData(0.0)]
    [InlineData(-1.0)]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    public void F047_fixed_step_service_refuses_an_invalid_step_instead_of_defaulting_to_one_minute(double minutes)
        => Assert.Throws<ArgumentOutOfRangeException>(() => new MechanicalService(minutes));

    [Fact]
    public void F047_thirty_minute_lake_ticks_divide_by_thirty()
    {
        MechanicalService svc = MechanicalService.ForTimestampedFeed();
        Assert.True(svc.IsTimestamped);
        Assert.True(double.IsNaN(svc.StepMinutes));                       // unknown before any tick

        Assert.Equal(0.0, Ramp(svc.ProjectAtTick(Frame(OneUnit(100), 0), T0)));
        Assert.True(double.IsNaN(svc.StepMinutes));                       // one tick: still no interval

        // +30 MW over a 30-minute tick is exactly 1 MW/min. Before the fix the divisor was 1.0, giving 30 MW/min.
        MechanicalDto d = svc.ProjectAtTick(Frame(OneUnit(130), 1), T0.AddMinutes(30));
        Assert.Equal(30.0, svc.StepMinutes);
        Assert.Equal(1.0, Ramp(d));
    }

    [Fact]
    public void F047_step_follows_each_tick_gap_including_one_second_raw_ticks()
    {
        MechanicalService svc = MechanicalService.ForTimestampedFeed();
        svc.ProjectAtTick(Frame(OneUnit(100), 0), T0);
        svc.ProjectAtTick(Frame(OneUnit(100), 1), T0.AddMinutes(30));
        MechanicalDto d = svc.ProjectAtTick(Frame(OneUnit(101), 2), T0.AddMinutes(30).AddSeconds(1));

        // +1 MW over one second is 60 MW/min (same expression as the R067 fixed-step test).
        Assert.Equal(1.0 / 60.0, svc.StepMinutes);
        Assert.Equal(1.0 / (1.0 / 60.0), Ramp(d));
    }

    [Theory]
    [InlineData(0)]     // the same tick again
    [InlineData(-30)]   // a seek back or a loop restart
    public void F047_a_tick_at_or_before_the_previous_is_a_discontinuity_not_a_ramp(int minutesFromPrevious)
    {
        MechanicalService svc = MechanicalService.ForTimestampedFeed();
        svc.ProjectAtTick(Frame(OneUnit(100), 0), T0);
        svc.ProjectAtTick(Frame(OneUnit(100), 1), T0.AddMinutes(30));
        MechanicalDto d = svc.ProjectAtTick(Frame(OneUnit(150), 2), T0.AddMinutes(30 + minutesFromPrevious));

        Assert.Equal(0.0, Ramp(d));                                       // history dropped: no phantom ramp
        Assert.True(double.IsNaN(svc.StepMinutes));

        // The next increasing tick measures from the discontinuity, not from the old timeline.
        MechanicalDto next = svc.ProjectAtTick(Frame(OneUnit(160), 3), T0.AddMinutes(40 + minutesFromPrevious));
        Assert.Equal(10.0, svc.StepMinutes);
        Assert.Equal((160.0 - 150.0) / 10.0, Ramp(next));
    }

    [Fact]
    public void F047_a_bridge_frame_without_its_tick_instant_fails_loud()
    {
        MechanicalService svc = MechanicalService.ForTimestampedFeed();
        InvalidOperationException ex = Assert.Throws<InvalidOperationException>(
            () => svc.ProjectAtTick(Frame(OneUnit(100), 0), null));
        Assert.Contains("without its tick instant", ex.Message);
        Assert.Throws<InvalidOperationException>(() => svc.ProjectAtTick(Frame(OneUnit(100), 0), DateTime.MinValue));
    }

    [Fact]
    public void F047_the_two_modes_cannot_be_mixed()
    {
        MechanicalService bridge = MechanicalService.ForTimestampedFeed();
        Assert.Throws<InvalidOperationException>(() => bridge.Project(Frame(OneUnit(100), 0)));
        Assert.Throws<InvalidOperationException>(() => bridge.SetStepMinutes(30));

        var fixedStep = new MechanicalService(30);
        Assert.False(fixedStep.IsTimestamped);
        Assert.Throws<InvalidOperationException>(() => fixedStep.ProjectAtTick(Frame(OneUnit(100), 0), T0));
    }

    [Fact]
    public async Task F047_bridge_session_uses_the_timestamped_service_not_a_one_minute_default()
    {
        // Nothing listens on this endpoint: the connect fails in the background and is reported as the session
        // error. Only the construction-time wiring is under test here.
        SimSession s = SimSession.CreateBridge("t-f047", new Uri("wss://bridge.example.invalid/ws"), "gb-full", "presolved",
            resilience: null, licence: _ => GridSim.Core.Licensing.GateResult.Ok());
        try
        {
            Assert.True(double.IsNaN(s.MechanicalStepMinutes));             // before the fix: 1.0
        }
        finally
        {
            await s.DisposeAsync();
        }
    }
}
