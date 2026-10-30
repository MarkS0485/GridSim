using GridSim.Core.Dynamics;
using GridSim.Core.Fleet;
using Xunit;

namespace GridSim.Tests;

/// <summary>R263: FleetDynamics.Simulate ignored the grid-forming fields and silently ran the non-forming swing; a forming
/// config was then refused (ArgumentException naming the mode). N291 (stream B5) routes the fleet through the grid-forming
/// step instead (see the moved test below). Non-forming runs are untouched.</summary>
public sealed partial class HHygieneTests
{
    private static readonly FrequencyEvent[] FleetTrip = { new(1.0, -1_320) };

    [Theory]
    [InlineData(FormingMode.Vsm)]
    [InlineData(FormingMode.Droop)]
    public void R263_a_forming_config_runs_the_forming_swing_instead_of_the_floored_one(FormingMode mode)
    {
        // Moved 2026-10 (B5 N291, Ruling R1: Mark delegated EE calls 2026-10-04; source: the grid-forming step every lumped
        // engine already runs, FormingKernel, and the VSM / droop former models documented on FleetDynamics.Simulate): the
        // refusal was the interim fix for "the fleet ignores the forming fields" until the fleet was routed through the
        // forming step; N291 does that. Before: ArgumentException (param "config", naming the mode). After: the run
        // completes and the first post-event RoCoF is the forming swing's, f0 * dP / (2 (E_real + Ev)), with E_real the
        // base plus the units' inertia (120,000 + 8,000 MW.s) and Ev = 20,000 MW.s: 50 * -1320 / (2 * 148,000) =
        // -0.222972972... Hz/s, where the floored non-forming swing gives 50 * -1320 / (2 * 128,000) = -0.2578125 Hz/s.
        // At that step the frequency is still nominal to round-off and no responder has moved, so the relative tolerance
        // only covers that round-off.
        //
        // Moved 2026-10 (LF4 N311, Ruling R1: Mark delegated EE calls 2026-10-04, ruled by main): a droop former is a pure
        // P-f droop and carries no virtual inertia (VirtualInertiaMwS other than 0 under Droop is now refused by name).
        // The Droop case therefore runs with Ev = 0 and its first post-event RoCoF is f0 dP / (2 E_real):
        // before 50 * -1320 / (2 * 148,000) = -0.222972972... Hz/s, after 50 * -1320 / (2 * 128,000) = -0.2578125 Hz/s
        // (the same first-step figure as the floored swing, as it must be: nothing virtual is added). Vsm is unchanged.
        double ev = mode == FormingMode.Vsm ? 20_000.0 : 0.0;
        var cfg = new DynamicsConfig { Forming = mode, VirtualInertiaMwS = ev, FormingDampingMwPerHz = 500 };
        FleetResult r = FleetDynamics.Simulate(120_000, 25_000, FleetTrip, FleetSpec.Uniform(4, 8_000, 400), cfg);
        double expected = 50.0 * -1_320.0 / (2.0 * (120_000.0 + 8_000.0 + ev));
        Assert.True(Math.Abs(r.Response.InitialRocofHzPerS - expected) <= 1e-9 * Math.Abs(expected),
            $"{mode}: peak RoCoF {r.Response.InitialRocofHzPerS:R} vs the forming swing's {expected:R}");
        Assert.True(double.IsFinite(r.Response.NadirHz) && r.Response.NadirHz < 50.0, $"nadir {r.Response.NadirHz:R}");
    }

    [Fact]
    public void R263_non_forming_runs_are_unchanged_and_ignore_the_forming_only_fields_as_before()
    {
        // With Forming = None the forming-only fields were ignored before and still are: the run equals the plain one
        // bit for bit (the same convention as FrequencyDynamics, which reads them only when forming).
        FleetSpec spec = FleetSpec.Uniform(4, 8_000, 400, 0.5, 0.015, 0.3);
        FleetResult plain = FleetDynamics.Simulate(120_000, 25_000, FleetTrip, spec, new DynamicsConfig());
        FleetResult withFields = FleetDynamics.Simulate(120_000, 25_000, FleetTrip, spec,
            new DynamicsConfig { VirtualInertiaMwS = 20_000, FormingDampingMwPerHz = 500, FormingSetpointMw = 3 });
        FleetResult noConfig = FleetDynamics.Simulate(120_000, 25_000, FleetTrip, spec);
        foreach (FleetResult r in new[] { withFields, noConfig })
        {
            AssertBits(plain.Response.InitialRocofHzPerS, r.Response.InitialRocofHzPerS, "RoCoF");
            AssertBits(plain.Response.NadirHz, r.Response.NadirHz, "nadir");
            AssertBits(plain.Response.SettleHz, r.Response.SettleHz, "settle");
            AssertBits(plain.FfrAtNadirMw, r.FfrAtNadirMw, "FFR at nadir");
            Assert.Equal(plain.Response.Points.Count, r.Response.Points.Count);
            for (int k = 0; k < plain.Response.Points.Count; k++)
                AssertBits(plain.Response.Points[k].Hz, r.Response.Points[k].Hz, $"Hz at {k}");
        }
    }
}
