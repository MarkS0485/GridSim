using GridSim.Core.Dynamics;
using GridSim.Core.IO;
using GridSim.Core.Model;
using GridSim.Core.PowerFlow;
using GridSim.Core.Presentation;
using GridSim.Core.Simulation;
using GridSim.Web.Projection;
using Xunit;

namespace GridSim.Tests;

/// <summary>
/// R051 (October 2026 review): the web and WPF status lamps never read PowerFlowResult.Converged, so a Newton solve
/// that stopped at its iteration cap with voltages still inside the band showed SYSTEM NORMAL. The web lamp is now the
/// pure <see cref="StatusLamp.Evaluate"/>; these tests pin its priority order, with NOT CONVERGED above every verdict
/// read off the solved state. The WPF twin is checked at source level in <see cref="WpfPresentationSourceContractTests"/>.
/// </summary>
public class StatusLampConvergenceTests
{
    // Moved 2026-10 (Q10, authorised by Mark's best-judgement ruling): the VOLTAGE ALERT lamp is now judged on the
    // lowest / highest bus voltage, not the average, so HeadlineDto carries MinVoltagePu / MaxVoltagePu. This helper
    // used to set only AvgVoltagePu = v; it now sets all three to v (a flat voltage profile), so every voltage case
    // below keeps its old meaning and expectation. The min / max rule itself is pinned by the new Q10 tests below.
    private static HeadlineDto Headline(bool converged, double f = 50.0, double rocof = 0.0, double v = 1.0) =>
        new(FrequencyHz: f, RocofHzPerS: rocof, GenerationMw: 1000, DemandMw: 990, LossMw: 10,
            InertiaGvaS: 100, AvgVoltagePu: v, Converged: converged, MinVoltagePu: v, MaxVoltagePu: v);

    [Fact]
    public void No_frame_is_a_solve_fault()
    {
        Assert.Equal(("lamp-fault", "SOLVE FAULT"), StatusLamp.Evaluate(null, anyBoundaryOverloaded: false));
    }

    [Fact]
    public void A_non_converged_solve_with_healthy_numbers_is_NOT_CONVERGED_never_SYSTEM_NORMAL()
    {
        // The exact failure scenario of R051: everything in band, but the solve did not converge.
        (string cls, string text) = StatusLamp.Evaluate(Headline(converged: false), anyBoundaryOverloaded: false);
        Assert.Equal("NOT CONVERGED", text);
        Assert.Equal("lamp-unconverged", cls);
        Assert.NotEqual("SYSTEM NORMAL", text);
    }

    [Theory]
    [InlineData(49.0, 0.0, 1.0, false)]   // would be FREQUENCY EVENT
    [InlineData(50.0, 0.5, 1.0, false)]   // was a FREQUENCY EVENT (RoCoF) under the legacy 0.125 Hz/s clause; NOT CONVERGED either way
    [InlineData(50.0, 0.0, 0.80, false)]  // would be VOLTAGE ALERT
    [InlineData(50.0, 0.0, 1.0, true)]    // would be CORRIDOR ALERT
    public void NOT_CONVERGED_outranks_every_verdict_read_off_the_solved_state(double f, double rocof, double v, bool overloaded)
    {
        Assert.Equal(StatusLamp.NotConverged, StatusLamp.Evaluate(Headline(false, f, rocof, v), overloaded));
    }

    [Theory]
    [InlineData(50.0, 0.0, 1.00, false, "SYSTEM NORMAL")]
    // Moved 2026-10 (Q10, authorised by Mark's best-judgement ruling): was "FREQUENCY EVENT" ("exactly on the band
    // edge: >= 0.2 Hz, as before"). The ruling is the strict WPF rule, |f - 50| > 0.2 Hz, because 0.2 Hz is inside the
    // operational limit, so 49.8 Hz on the edge is not an event. (StatusLampRules compares f with the 49.8 / 50.2
    // edges: 50 - 49.8 is 0.20000000000000284 in doubles, so the subtraction form would still have fired here.)
    [InlineData(49.8, 0.0, 1.00, false, "SYSTEM NORMAL")]
    [InlineData(50.19, 0.0, 1.00, false, "SYSTEM NORMAL")]
    // Moved 2026-10 (B4 N041, Ruling R1: Mark delegated EE calls 2026-10-04; source EREC G99 Issue 2 s.10.6.7.1 Table
    // 10.1, LoM (RoCoF) 1 Hz/s, and Ofgem's DC0079 decision, "RoCoF settings changed to 1Hz/s"): the lamp's RoCoF clause is
    // the shared post-ALoMCP setting, strictly above 1 Hz/s, in both shells. Before: 0.125 Hz/s (the legacy setting)
    // gave "FREQUENCY EVENT". After: "SYSTEM NORMAL" (0.125 Hz/s is an ordinary post-trip slope today).
    [InlineData(50.0, 0.125, 1.00, false, "SYSTEM NORMAL")]
    [InlineData(50.0, -0.124, 1.00, false, "SYSTEM NORMAL")]
    [InlineData(50.0, 0.0, 0.939, false, "VOLTAGE ALERT")]
    [InlineData(50.0, 0.0, 0.94, false, "SYSTEM NORMAL")]     // band edges inclusive, as before
    [InlineData(50.0, 0.0, 1.06, false, "SYSTEM NORMAL")]
    [InlineData(50.0, 0.0, 1.061, false, "VOLTAGE ALERT")]
    [InlineData(50.0, 0.0, 1.00, true, "CORRIDOR ALERT")]
    [InlineData(49.0, 0.0, 0.80, true, "FREQUENCY EVENT")]    // priority: frequency > voltage > corridor
    [InlineData(50.0, 0.0, 0.80, true, "VOLTAGE ALERT")]
    public void Converged_frames_keep_the_old_priority_and_band_edges(double f, double rocof, double v, bool overloaded, string expected)
    {
        Assert.Equal(expected, StatusLamp.Evaluate(Headline(true, f, rocof, v), overloaded).Text);
    }

    [Fact]
    public void Named_thresholds_equal_the_literals_they_replaced_and_the_Core_band()
    {
        // R203: named, values unchanged.
        Assert.Equal(0.2, StatusLamp.FrequencyEventBandHz);
        Assert.Equal(50.0, FrequencyLimits.NominalHz);
        Assert.Equal(0.125, FrequencyLimits.RocofLimitLegacyHzPerS);
        Assert.Equal(0.94, ScadaThresholds.VoltageBandLowPu);
        Assert.Equal(1.06, ScadaThresholds.VoltageBandHighPu);
        // The deviation band is the Core operational band, to within the rounding of the subtraction.
        Assert.Equal(FrequencyLimits.NominalHz - FrequencyLimits.OperationalLowHz, StatusLamp.FrequencyEventBandHz, 12);
        Assert.Equal(FrequencyLimits.OperationalHighHz - FrequencyLimits.NominalHz, StatusLamp.FrequencyEventBandHz, 12);
    }

    [Fact]
    public void A_really_non_converged_projected_frame_lights_NOT_CONVERGED()
    {
        // End to end through the real projector: IEEE 14-bus capped at one Newton iteration cannot converge.
        GridModel model = IeeeCases.Case14();
        PowerFlowResult r = new SteadyStateSolver().Solve(model, new PowerFlowOptions { MaxIterations = 1 });
        Assert.False(r.Converged);
        var f = new SimFrame(model, r, Hour: 0, Tick: 0, FrequencyHz: 50, GenerationMw: 0, DemandMw: 0);

        FrameEnvelope env = FrameProjector.Project(f);

        Assert.False(env.Headline.Converged);
        Assert.Equal(StatusLamp.NotConverged, StatusLamp.Evaluate(env.Headline, anyBoundaryOverloaded: false));
    }
}
