using GridSim.Core.Analysis;
using GridSim.Core.Model;
using GridSim.Core.PowerFlow;
using Xunit;

namespace GridSim.Tests;

/// <summary>
/// N124: a generator (or bus) outage's lost output used to be picked up wholly by the reference bus in the re-solve. The
/// screen now offers the dispatch choices PSS/E ACCC and PowerFactory's contingency analysis offer: the reference bus (the
/// default, unchanged), governor droop (MBase-weighted, within limits) or inertia (H x MBase). Pinned against hand-built
/// post-outage models solved directly.
/// </summary>
public sealed partial class B3GovernorsDynamicDataTests
{
    // G1 at the reference bus 1, G2 at bus 2 (the outage), G3 at bus 4; all synchronous with known bases.
    private static GridModel N124Model(double g3Pmax = 400.0) => new()
    {
        Name = "four-bus",
        BaseMva = 100,
        Buses = new[]
        {
            new Bus(1, BusType.Slack, BaseKv: 132), new Bus(2, BusType.Pv, BaseKv: 132),
            new Bus(3, BusType.Pq, Pd: 150, Qd: 30, BaseKv: 132), new Bus(4, BusType.Pv, BaseKv: 132),
        },
        Generators = new[]
        {
            new Generator(1, 40, Name: "G1", Pmax: 200, MBase: 100, H: 5, Type: "ccgt"),
            new Generator(2, 60, Name: "G2", Pmax: 200, MBase: 100, H: 4, Type: "ccgt"),
            new Generator(4, 50, Name: "G3", Pmax: g3Pmax, MBase: 300, H: 2, Type: "ccgt"),
        },
        Branches = new[]
        {
            new Branch(1, 3, 0.01, 0.1, RateAMva: 250), new Branch(2, 3, 0.01, 0.1, RateAMva: 250),
            new Branch(1, 2, 0.01, 0.1, RateAMva: 250), new Branch(4, 3, 0.01, 0.1, RateAMva: 250),
        },
    };

    private static double N124SolvedMinVm(GridModel m)
    {
        PowerFlowResult pf = SolverSelector.CreateNewton(m).Solve(m);
        Assert.True(pf.Converged);
        return pf.Vm.Min();
    }

    [Fact]
    public void N124_the_default_is_the_reference_bus_screen_bit_for_bit()
    {
        GridModel m = N124Model();
        var outage = new ElementOutage(OutageKind.Generator, 1);
        ContingencyResult a = ContingencyAnalysis.ScreenElementOutage(m, outage);
        ContingencyResult b = ContingencyAnalysis.ScreenElementOutage(m, outage, dispatch: OutageDispatch.ReferenceBus);
        AssertBits(a.MinVm, b.MinVm, "MinVm");
        AssertBits(a.MaxVm, b.MaxVm, "MaxVm");
        AssertBits(a.MaxBranchLoadingPercent, b.MaxBranchLoadingPercent, "max loading");
        Assert.Equal(a.Note, b.Note);
        Assert.Null(a.Note);
        // And it is the model without G2 solved directly (the reference absorbs the 60 MW).
        GridModel without = m.CopyWith(generators: new[] { m.Generators[0], m.Generators[2] });
        AssertBits(N124SolvedMinVm(without), a.MinVm, "reference-bus MinVm");
    }

    [Fact]
    public void N124_governor_droop_shares_the_lost_output_by_machine_base()
    {
        // Losing G2's 60 MW: G1 (100 MVA) and G3 (300 MVA) share it 1 : 3, so G1 is scheduled 40 + 15 = 55 MW and G3
        // 50 + 45 = 95 MW (exact in binary: 60 * 100 / 400 and 60 * 300 / 400). The screen must be that model solved.
        GridModel m = N124Model();
        ContingencyResult r = ContingencyAnalysis.ScreenElementOutage(m, new ElementOutage(OutageKind.Generator, 1),
            dispatch: OutageDispatch.GovernorDroop);
        Assert.True(r.Converged);
        Assert.Equal(60.0, r.LostGenerationMw);
        Assert.Equal("lost 60.0 MW shared by governor droop over 2 unit(s)", r.Note);
        GridModel byHand = m.CopyWith(generators: new[] { m.Generators[0] with { Pg = 55 }, m.Generators[2] with { Pg = 95 } });
        AssertBits(N124SolvedMinVm(byHand), r.MinVm, "droop MinVm");
    }

    [Fact]
    public void N124_a_unit_at_its_limit_passes_the_rest_of_its_share_on()
    {
        // G3's Pmax 70 leaves it 20 MW of headroom, below its 45 MW share: it is fixed at 70 and G1 takes the other 40.
        GridModel m = N124Model(g3Pmax: 70);
        ContingencyResult r = ContingencyAnalysis.ScreenElementOutage(m, new ElementOutage(OutageKind.Generator, 1),
            dispatch: OutageDispatch.GovernorDroop);
        Assert.Equal("lost 60.0 MW shared by governor droop over 2 unit(s), 1 at a limit", r.Note);
        GridModel byHand = m.CopyWith(generators: new[] { m.Generators[0] with { Pg = 80 }, m.Generators[2] with { Pg = 70 } });
        AssertBits(N124SolvedMinVm(byHand), r.MinVm, "limited droop MinVm");
    }

    [Fact]
    public void N124_inertia_shares_by_stored_energy()
    {
        // H x MBase: G1 5 x 100 = 500, G3 2 x 300 = 600 MW.s, so 60 MW splits 500 : 600.
        GridModel m = N124Model();
        (GridModel shared, string note) = ContingencyAnalysis.ShareLostPower(
            m.CopyWith(generators: new[] { m.Generators[0], m.Generators[2] }), 60.0, OutageDispatch.Inertia);
        Assert.Equal(40.0 + 60.0 * 500.0 / 1100.0, shared.Generators[0].Pg, 12);
        Assert.Equal(50.0 + 60.0 * 600.0 / 1100.0, shared.Generators[1].Pg, 12);
        Assert.Equal("lost 60.0 MW shared by inertia over 2 unit(s)", note);
    }

    [Fact]
    public void N124_converters_and_power_flow_only_units_take_no_share_and_what_cannot_fit_is_left_to_the_reference()
    {
        GridModel m = new()
        {
            BaseMva = 100,
            Buses = new[] { new Bus(1, BusType.Slack, BaseKv: 132), new Bus(2, BusType.Pq, Pd: 100, BaseKv: 132) },
            Branches = new[] { new Branch(1, 2, 0.01, 0.1) },
            Generators = new[]
            {
                new Generator(1, 30, Name: "sync", Pmax: 50, MBase: 100, H: 4, Type: "ccgt"),
                new Generator(2, 20, Name: "wind", Pmax: 200, MBase: 400, Type: "wind-onshore"),
                new Generator(2, 10, Name: "pf-only", Pmax: 200, MBase: 400, Type: "ccgt", PowerFlowOnly: true),
            },
        };
        (GridModel shared, string note) = ContingencyAnalysis.ShareLostPower(m, 50.0, OutageDispatch.GovernorDroop);
        Assert.Equal(50.0, shared.Generators[0].Pg);      // the only participant, up to its Pmax (20 MW of headroom)
        Assert.Equal(20.0, shared.Generators[1].Pg);      // converter: unchanged
        Assert.Equal(10.0, shared.Generators[2].Pg);      // power-flow-only: unchanged
        Assert.Equal("lost 50.0 MW shared by governor droop over 1 unit(s), 1 at a limit; 30.0 MW beyond their headroom left to the reference", note);

        // A bus outage that loses more load than generation lowers the others (a surplus), toward Pmin.
        (GridModel lowered, string note2) = ContingencyAnalysis.ShareLostPower(m, -10.0, OutageDispatch.GovernorDroop);
        Assert.Equal(20.0, lowered.Generators[0].Pg);
        Assert.Equal("surplus 10.0 MW shared by governor droop over 1 unit(s)", note2);
        Assert.Throws<ArgumentOutOfRangeException>(() => ContingencyAnalysis.ShareLostPower(m, double.NaN, OutageDispatch.Inertia));
        Assert.Throws<ArgumentOutOfRangeException>(() => ContingencyAnalysis.ShareLostPower(m, 5.0, OutageDispatch.ReferenceBus));
    }
}
