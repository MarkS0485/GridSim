using GridSim.Core.IO;
using GridSim.Core.Model;
using GridSim.Core.PowerFlow;
using GridSim.Core.Presentation;
using GridSim.Core.Simulation;
using GridSim.Web.Projection;
using GridSim.Web.Sessions;
using Xunit;

namespace GridSim.Tests;

/// <summary>
/// R054: per-machine ramp history was keyed by generator name, which is not unique on gb-full, so namesakes were
/// diffed against each other and a steady operating point showed ramps every frame. R067: the web MECHANICAL ramp
/// divided by the construction-time step even after the transport STEP picker changed it.
/// </summary>
public class MechanicalRampIdentityTests
{
    /// <summary>Two synchronous units sharing the name "- chp" (as gb-full does), at different outputs.</summary>
    private static GridModel Namesakes(double a, double b) => new()
    {
        BaseMva = 100,
        Buses = new[] { new Bus(1, BusType.Slack, BaseKv: 132), new Bus(2, BusType.Pq, Pd: 100, BaseKv: 132) },
        Branches = new[] { new Branch(1, 2, 0.01, 0.1) },
        Generators = new[]
        {
            new Generator(1, a, Pmax: 50, MBase: 50, H: 4, Type: "CCGT", Name: "- chp"),
            new Generator(2, b, Pmax: 50, MBase: 50, H: 4, Type: "CCGT", Name: "- chp"),
        },
    };

    // A frame wrapping the model as-is (no solve): the ramp only reads Generator.Pg, and a solve would move the
    // slack unit's output and blur the "nothing changed" scenario.
    private static SimFrame Frame(GridModel m, int tick) => new(m, new NewtonRaphsonPowerFlow().Solve(m),
        Hour: tick / 3.0, Tick: tick, FrequencyHz: 50, GenerationMw: m.Generators.Sum(g => g.Pg), DemandMw: 100);

    [Fact]
    public void Unit_ramp_history_tracks_each_index_separately()
    {
        var h = new UnitRampHistory();
        GridModel m = Namesakes(24.75, 14.96);
        Assert.Null(h.Exchange(0, m.Generators[0]));
        Assert.Null(h.Exchange(1, m.Generators[1]));
        // Same frame again: each namesake gets ITS OWN previous output back.
        Assert.Equal(24.75, h.Exchange(0, m.Generators[0]));
        Assert.Equal(14.96, h.Exchange(1, m.Generators[1]));
    }

    [Fact]
    public void Unit_ramp_history_refuses_a_different_unit_at_the_same_index()
    {
        var h = new UnitRampHistory();
        h.Exchange(0, new Generator(1, 100, Name: "Unit A"));
        Assert.Null(h.Exchange(0, new Generator(1, 300, Name: "Unit B")));   // different name: no phantom ramp
        Assert.Null(h.Exchange(0, new Generator(2, 300, Name: "Unit B")));   // different bus: same
        Assert.Equal(300.0, h.Exchange(0, new Generator(2, 310, Name: "Unit B")));
        h.Reset();
        Assert.Null(h.Exchange(0, new Generator(2, 310, Name: "Unit B")));
    }

    [Fact]
    public void Steady_namesakes_show_zero_ramp_on_the_web_mechanical_tab()
    {
        // The R054 failure scenario: unchanged dispatch, two units named "- chp" at 24.75 and 14.96 MW.
        var svc = new MechanicalService(dtMinutes: 20);
        GridModel m = Namesakes(24.75, 14.96);
        svc.Project(Frame(m, 0));
        MechanicalDto second = svc.Project(Frame(m, 1));

        // Before the fix: within one frame the second namesake diffed against the first, giving
        // (14.96 - 24.75) / 20 = -0.4895 and (24.75 - 14.96) / 20 = +0.4895 MW/min. Now exactly zero.
        Assert.Equal(2, second.Machines.Count);
        Assert.All(second.Machines, r => Assert.Equal(0.0, r.RampMwPerMin));
    }

    [Fact]
    public void A_real_change_on_one_namesake_is_attributed_to_that_unit_only()
    {
        var svc = new MechanicalService(dtMinutes: 20);
        svc.Project(Frame(Namesakes(24.75, 14.96), 0));
        MechanicalDto next = svc.Project(Frame(Namesakes(24.75, 34.96), 1));   // the second unit ramps +20 MW

        MachineRowDto up = next.Machines.Single(r => r.Pg == 34.96);
        MachineRowDto flat = next.Machines.Single(r => r.Pg == 24.75);
        Assert.Equal((34.96 - 14.96) / 20.0, up.RampMwPerMin);
        Assert.Equal(0.0, flat.RampMwPerMin);
    }

    [Fact]
    public void Mechanical_service_divides_by_the_new_step_after_a_change()
    {
        var svc = new MechanicalService(dtMinutes: 20);
        svc.Project(Frame(Namesakes(20, 20), 0));
        svc.SetStepMinutes(1.0 / 60.0);                                      // 20 min -> 1 s
        Assert.Equal(1.0 / 60.0, svc.StepMinutes);
        MechanicalDto next = svc.Project(Frame(Namesakes(21, 20), 1));

        // +1 MW over one 1-second step is 60 MW/min. Before the fix the divisor stayed 20, giving 0.05 MW/min
        // (understated 1,200 times).
        Assert.Equal(1.0 / (1.0 / 60.0), next.Machines.Single(r => r.Pg == 21).RampMwPerMin);
    }

    [Theory]
    [InlineData(0.0)]
    [InlineData(-1.0)]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    public void Mechanical_service_rejects_an_invalid_step(double minutes)
        => Assert.Throws<ArgumentOutOfRangeException>(() => new MechanicalService(20).SetStepMinutes(minutes));

    [Fact]
    public async Task Live_session_step_change_reaches_the_mechanical_service()
    {
        SimSession s = SimSession.CreateLive("t", "case9", "IEEE 9", IeeeCases.Case9(), holdSteady: true, stepMinutes: 20);
        try
        {
            Assert.Equal(20.0, s.MechanicalStepMinutes);
            s.SetStepMinutes(1.0 / 60.0);
            Assert.Equal(1.0 / 60.0, s.StepMinutes);
            Assert.Equal(1.0 / 60.0, s.MechanicalStepMinutes);   // before the fix: still 20
            s.SetStepMinutes(double.NaN);                          // ignored, as before
            Assert.Equal(1.0 / 60.0, s.MechanicalStepMinutes);
        }
        finally
        {
            await s.DisposeAsync();
        }
    }
}
