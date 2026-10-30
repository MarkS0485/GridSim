using GridSim.Core.Model;
using GridSim.Core.PowerFlow;
using Xunit;

namespace GridSim.Tests;

/// <summary>
/// R013 (October 2026 review): the OLTC outer loop used to assume the controlled bus is always the To
/// (untapped) end of the transformer branch, where lowering the tap raises the bus. The Branch tap sits on
/// the From end (V_to ~ V_from / a), so for an OLTC regulating its From bus the rule is reversed
/// (V_from ~ a * V_to): raising the tap raises the bus. With the old rule such an OLTC stepped the wrong way
/// every pass and ran to its tap limit. These tests pin the From-side behaviour and that the To side is
/// unchanged.
/// </summary>
public class OltcFromSideTests
{
    // A slack (HV) bus 1 feeding a loaded bus 2 through transformer T1, then a line to bus 3. The transformer
    // branch is oriented by the caller, so the same physical network can put the regulated bus 2 on either
    // end of the branch.
    private static GridModel Model(bool lvOnFromEnd, double startTap = 1.0, double minTap = 0.9, double maxTap = 1.1) => new()
    {
        BaseMva = 100,
        Name = lvOnFromEnd ? "oltc-from-side" : "oltc-to-side",
        Buses = new[]
        {
            new Bus(1, BusType.Slack, Vm: 1.0),
            new Bus(2, BusType.Pq, Pd: 80, Qd: 30),
            new Bus(3, BusType.Pq, Pd: 40, Qd: 15),
        },
        Generators = new[] { new Generator(1, 0) },
        Branches = new[]
        {
            lvOnFromEnd
                ? new Branch(2, 1, 0.008, 0.08, Name: "T1", TapRatio: startTap, IsTransformer: true)
                : new Branch(1, 2, 0.008, 0.08, Name: "T1", TapRatio: startTap, IsTransformer: true),
            new Branch(2, 3, 0.02, 0.10, Name: "L23"),
        },
        Oltcs = new[] { new Oltc("T1", ControlsBus: 2, TargetVpu: 1.02, MinTap: minTap, MaxTap: maxTap, StepPu: 0.0125, DeadbandPu: 0.01) },
    };

    private static readonly PowerFlowOptions Opt = new() { MaxIterations = 60, EnforceQLimits = false };

    private static double VmOf(PowerFlowResult r, int busId)
    {
        for (int i = 0; i < r.BusIds.Count; i++) if (r.BusIds[i] == busId) return r.Vm[i];
        throw new InvalidOperationException($"bus {busId} missing from the result");
    }

    [Fact]
    public void From_side_oltc_raises_its_tap_to_lift_a_low_bus_and_settles_in_band()
    {
        // Bus 2 starts well below the 1.02 target. Before the fix the loop LOWERED the tap, which lowered the
        // From-end voltage further, and the tap ran to MinTap with TapsSettled = false.
        var solver = new SteadyStateSolver();
        PowerFlowResult r = solver.Solve(Model(lvOnFromEnd: true), Opt);

        Assert.True(r.Converged);
        Assert.True(solver.TapsSettled, "a From-side OLTC must settle inside its deadband");
        double tap = solver.RegulatedModel.Branches[0].TapRatio;
        Assert.True(tap > 1.0, $"raising a tapped-end bus needs a HIGHER tap; got {tap}");
        Assert.True(tap < 1.1, $"the tap must not run to its limit; got {tap}");
        double v2 = VmOf(r, 2);
        Assert.InRange(v2, 1.02 - 0.01, 1.02 + 0.01);
    }

    [Fact]
    public void From_side_oltc_lowers_its_tap_to_bring_down_a_high_bus()
    {
        // Start with the tap high enough that bus 2 sits above the band: the loop must step the tap DOWN.
        var solver = new SteadyStateSolver();
        PowerFlowResult r = solver.Solve(Model(lvOnFromEnd: true, startTap: 1.1, maxTap: 1.15), Opt);

        Assert.True(r.Converged);
        Assert.True(solver.TapsSettled);
        Assert.True(solver.RegulatedModel.Branches[0].TapRatio < 1.1);
        Assert.InRange(VmOf(r, 2), 1.02 - 0.01, 1.02 + 0.01);
    }

    [Fact]
    public void To_side_oltc_still_lowers_its_tap_to_lift_a_low_bus()
    {
        var solver = new SteadyStateSolver();
        PowerFlowResult r = solver.Solve(Model(lvOnFromEnd: false), Opt);

        Assert.True(r.Converged);
        Assert.True(solver.TapsSettled);
        Assert.True(solver.RegulatedModel.Branches[0].TapRatio < 1.0, "a To-side bus is raised by a LOWER tap");
        Assert.InRange(VmOf(r, 2), 1.02 - 0.01, 1.02 + 0.01);
    }

    [Fact]
    public void Both_orientations_regulate_the_same_physical_bus_into_the_band()
    {
        // The same physical network, branch stored either way round: both must end inside the deadband. The
        // taps differ (a From-side tap a corresponds roughly to a To-side tap 1/a), but neither may diverge.
        var s1 = new SteadyStateSolver();
        var s2 = new SteadyStateSolver();
        PowerFlowResult from = s1.Solve(Model(lvOnFromEnd: true), Opt);
        PowerFlowResult to = s2.Solve(Model(lvOnFromEnd: false), Opt);
        Assert.True(s1.TapsSettled && s2.TapsSettled);
        Assert.InRange(VmOf(from, 2), 1.01, 1.03);
        Assert.InRange(VmOf(to, 2), 1.01, 1.03);
    }

    [Fact]
    public void From_side_oltc_that_cannot_reach_target_stops_at_max_tap_not_min_tap()
    {
        // A tap range too narrow to reach 1.02: the loop must run UP to MaxTap (the helpful direction), never
        // down to MinTap (the old wrong-way failure).
        var solver = new SteadyStateSolver();
        PowerFlowResult r = solver.Solve(Model(lvOnFromEnd: true, maxTap: 1.0125), Opt);
        Assert.True(r.Converged);
        Assert.Equal(1.0125, solver.RegulatedModel.Branches[0].TapRatio, 12);
    }
}
