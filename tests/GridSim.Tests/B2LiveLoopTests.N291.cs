using GridSim.Core.Assets;
using GridSim.Core.Dynamics;
using GridSim.Core.Fleet;
using GridSim.Core.IO;
using GridSim.Core.Model;
using GridSim.Core.Simulation;
using Xunit;

namespace GridSim.Tests;

/// <summary>
/// N291 live parts, step guard: the live integrators began with <c>if (dtSeconds &lt;= 0) return;</c>, which lets a NaN
/// step through (every comparison with NaN is false) and silently drops a zero or negative one. They now refuse a step
/// that is not a finite number of seconds above zero, and a non-finite event power, naming the engine; LiveSim refuses a
/// step that is not a finite number of minutes above zero before any state moves. (The band part of N291 is with N273.)
/// </summary>
public sealed partial class B2LiveLoopTests
{
    public static TheoryData<double> BadSteps() => new() { double.NaN, double.PositiveInfinity, double.NegativeInfinity, 0.0, -0.0, -0.02 };

    private static IFrequencyIntegrator[] LiveIntegrators()
    {
        AssetClass cls = AssetTestHelpers.GflClass("ffr");
        GridModel m = OneGflModel(cls, pgMw: 100, ratedMw: 400);
        return new IFrequencyIntegrator[]
        {
            new FrequencyIntegrator(200_000, 30_000),
            new AssetAwareFrequencyIntegrator(200_000, 30_000, AssetDynamicsFleet.Build(m, 50.0)),
        };
    }

    [Theory]
    [MemberData(nameof(BadSteps))]
    public void N291_every_live_integrator_refuses_a_step_that_is_not_finite_and_positive(double dt)
    {
        foreach (IFrequencyIntegrator integ in LiveIntegrators())
        {
            integ.Step(0.02, -1320.0);   // a real step first, so the state is not the start state
            double fBefore = integ.FrequencyHz;
            var ex = Assert.Throws<ArgumentOutOfRangeException>(() => integ.Step(dt, -1320.0));
            Assert.Equal("dtSeconds", ex.ParamName);
            Assert.Contains(integ.GetType().Name + ".Step", ex.Message);
            // Refused before anything moved.
            AssertBits(fBefore, integ.FrequencyHz, integ.GetType().Name + " frequency after a refused step");
        }
        var fleet = new MultiDeviceFrequencyIntegrator(50_000, 30_000, FleetSpec.Uniform(3, 0, 300));
        var exFleet = Assert.Throws<ArgumentOutOfRangeException>(() => fleet.Step(dt, -1320.0));
        Assert.Equal("dtSeconds", exFleet.ParamName);
        Assert.Contains("MultiDeviceFrequencyIntegrator.Step", exFleet.Message);
        Assert.Equal(50.0, fleet.FrequencyHz);
    }

    [Theory]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    [InlineData(double.NegativeInfinity)]
    public void N291_every_live_integrator_refuses_a_non_finite_event_power(double eventMw)
    {
        foreach (IFrequencyIntegrator integ in LiveIntegrators())
        {
            var ex = Assert.Throws<ArgumentOutOfRangeException>(() => integ.Step(0.02, eventMw));
            Assert.Equal("eventMw", ex.ParamName);
            Assert.Equal(50.0, integ.FrequencyHz);
        }
        var fleet = new MultiDeviceFrequencyIntegrator(50_000, 30_000, FleetSpec.Uniform(3, 0, 300));
        Assert.Equal("eventMw", Assert.Throws<ArgumentOutOfRangeException>(() => fleet.Step(0.02, eventMw)).ParamName);
    }

    [Fact]
    public void N291_a_finite_positive_step_runs_exactly_as_before_the_guard()
    {
        // The guard only adds a refusal: a finite positive step falls through to the same code. Two integrators, one
        // stepped through a series of refused calls in between, stay bit-identical.
        var a = new FrequencyIntegrator(200_000, 30_000);
        var b = new FrequencyIntegrator(200_000, 30_000);
        for (int i = 0; i < 500; i++)
        {
            double ev = i < 250 ? -1320.0 : 0.0;
            double dt = i % 3 == 0 ? 0.001 : i % 3 == 1 ? 0.02 : 1.7;
            a.Step(dt, ev);
            Assert.Throws<ArgumentOutOfRangeException>(() => b.Step(double.NaN, ev));
            b.Step(dt, ev);
            AssertBits(a.FrequencyHz, b.FrequencyHz, $"step {i} f");
            AssertBits(a.RocofHzPerS, b.RocofHzPerS, $"step {i} RoCoF");
        }
    }

    [Theory]
    [InlineData(double.NaN)]
    [InlineData(0.0)]
    [InlineData(-1.0)]
    [InlineData(double.PositiveInfinity)]
    public void N291_LiveSim_refuses_a_step_that_is_not_finite_and_positive_before_anything_moves(double stepMinutes)
    {
        var sim = new LiveSim(IeeeCases.Case30(), holdSteady: true);
        SimFrame first = sim.Step(1, 1.0);
        var ex = Assert.Throws<ArgumentOutOfRangeException>(() => sim.Step(2, stepMinutes));
        Assert.Equal("stepMinutes", ex.ParamName);
        // The clock did not move: the next good step reports the hour one minute after the first.
        SimFrame next = sim.Step(2, 1.0);
        Assert.Equal(first.Hour + 1.0 / 60.0, next.Hour, 12);
    }

    [Fact]
    public void N291_no_live_integrator_keeps_the_old_silent_guard()
    {
        foreach (string f in new[] { "FrequencyIntegrator.cs", "AssetAwareFrequencyIntegrator.cs", "MultiDeviceFrequencyIntegrator.cs" })
        {
            string src = CoreSrc("Dynamics", f);
            Assert.DoesNotContain("if (dtSeconds <= 0) return;", src);
            Assert.Contains("SwingKernel.RequireLiveStep(dtSeconds, eventMw,", src);
        }
    }
}
