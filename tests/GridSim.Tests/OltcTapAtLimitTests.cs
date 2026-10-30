using GridSim.Core.Model;
using GridSim.Core.PowerFlow;
using Xunit;

namespace GridSim.Tests;

/// <summary>
/// October 2026 docs-programme finding: an OLTC whose tap is pinned at <see cref="Oltc.MinTap"/> /
/// <see cref="Oltc.MaxTap"/> while its controlled bus is still outside the deadband counted as SETTLED (no tap moved,
/// so the loop said "done"), which reported an out-of-band bus as regulated. It is now reported as not settled, and the
/// transformer is named in <see cref="SteadyStateSolver.TapsAtLimit"/>.
/// </summary>
public class OltcTapAtLimitTests
{
    private const double Target = 1.02, Deadband = 0.01;

    /// <summary>A To-side OLTC on T1 regulating bus 2, which sits well below the band. Raising a To-side bus needs a
    /// LOWER tap; <paramref name="minTap"/> sets how far down it may go.</summary>
    private static GridModel Model(double minTap) => new()
    {
        BaseMva = 100,
        Name = "oltc-at-limit",
        Buses = new[]
        {
            new Bus(1, BusType.Slack, Vm: 1.0),
            new Bus(2, BusType.Pq, Pd: 80, Qd: 30),
            new Bus(3, BusType.Pq, Pd: 40, Qd: 15),
        },
        Generators = new[] { new Generator(1, 0) },
        Branches = new[]
        {
            new Branch(1, 2, 0.008, 0.08, Name: "T1", TapRatio: 1.0, IsTransformer: true),
            new Branch(2, 3, 0.02, 0.10, Name: "L23"),
        },
        Oltcs = new[] { new Oltc("T1", ControlsBus: 2, TargetVpu: Target, MinTap: minTap, MaxTap: 1.1, StepPu: 0.0125, DeadbandPu: Deadband) },
    };

    private static readonly PowerFlowOptions Opt = new() { MaxIterations = 60, EnforceQLimits = false };

    private static double VmOf(PowerFlowResult r, int busId)
    {
        for (int i = 0; i < r.BusIds.Count; i++) if (r.BusIds[i] == busId) return r.Vm[i];
        throw new InvalidOperationException($"bus {busId} missing from the result");
    }

    [Fact]
    public void A_tap_pinned_at_its_limit_with_the_bus_out_of_band_is_not_settled()
    {
        // MinTap = the starting tap, so the tap cannot move down at all.
        var s = new SteadyStateSolver();
        PowerFlowResult r = s.Solve(Model(minTap: 1.0), Opt);

        Assert.True(r.Converged);
        double v2 = VmOf(r, 2);
        Assert.True(v2 < Target - Deadband, $"precondition: bus 2 must be below the band, got {v2:R}");
        Assert.Equal(1.0, s.RegulatedModel.Branches[0].TapRatio);   // pinned at MinTap
        Assert.False(s.TapsSettled, "an out-of-band bus behind an exhausted tap is not regulated");
        Assert.Equal(new[] { "T1" }, s.TapsAtLimit);
        Assert.Equal(0, s.OltcPasses);   // no tap ever moved
    }

    [Fact]
    public void A_tap_that_runs_to_its_limit_during_the_loop_is_not_settled()
    {
        // MinTap one step below the start: the loop moves the tap once, then it is pinned.
        var s = new SteadyStateSolver();
        PowerFlowResult r = s.Solve(Model(minTap: 0.9875), Opt);

        Assert.True(r.Converged);
        Assert.True(VmOf(r, 2) < Target - Deadband, "precondition: one step is not enough to reach the band");
        // 1.0 - 0.0125 may differ from the literal 0.9875 in the last bit; either is "at the limit" (the loop's
        // TapChangeEpsilon is 1e-12), so compare to 12 decimal places.
        Assert.Equal(0.9875, s.RegulatedModel.Branches[0].TapRatio, 12);
        Assert.False(s.TapsSettled);
        Assert.Equal(new[] { "T1" }, s.TapsAtLimit);
        Assert.Equal(1, s.OltcPasses);
    }

    [Fact]
    public void A_tap_with_room_to_move_settles_in_band_and_names_nothing()
    {
        var s = new SteadyStateSolver();
        PowerFlowResult r = s.Solve(Model(minTap: 0.9), Opt);

        Assert.True(r.Converged);
        Assert.True(s.TapsSettled);
        Assert.Empty(s.TapsAtLimit);
        Assert.InRange(VmOf(r, 2), Target - Deadband, Target + Deadband);
    }
}
