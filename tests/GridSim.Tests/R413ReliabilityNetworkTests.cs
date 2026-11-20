using GridSim.Core.Analysis.Reliability;
using GridSim.Core.Analysis.Reliability.Network;
using GridSim.Core.Analysis.StudyCases;
using GridSim.Core.Model;
using GridSim.Core.Protection.Devices;
using Xunit;

namespace GridSim.Tests;

/// <summary>
/// RC4 R4-13: reliability and restoration joined on a real network. Every oracle is derived in the test: the series
/// system and the incumbent <see cref="ReliabilityStudy"/> on the same data (bit for bit), a hand-computed lateral feeder
/// (fuse and sectionalising switch), a hand-worked back-feed through one normally-open tie, an AC-limited tie that must
/// not restore, and a Monte Carlo that converges to the enumeration within its own standard error.
/// </summary>
public sealed class R413ReliabilityNetworkTests
{
    // ---------- builders ----------
    private static Bus Slack() => new(1, BusType.Slack, Vm: 1.0, BaseKv: 11);
    private static Bus Load(int id, double pd) => new(id, BusType.Pq, Pd: pd, BaseKv: 11);
    private static Branch Line(int f, int t, double rate = 50, bool inService = true, double lengthKm = 0) =>
        new(f, t, 0.01, 0.03, RateAMva: rate, InService: inService, LengthKm: lengthKm);

    private static GridModel Model(IEnumerable<Bus> buses, IEnumerable<Branch> branches) => new()
    {
        BaseMva = 100,
        Name = "r413",
        Buses = buses.ToList(),
        Generators = [new Generator(1, Pg: 0, Vg: 1.0, Qmin: -300, Qmax: 300, Pmin: 0, Pmax: 500)],
        Branches = branches.ToList(),
    };

    private static BoundGroups Groups(GridModel model, params (string Id, int[] Branches, int Head)[] groups)
    {
        var list = groups.Select(g => new ModelGroup(g.Id, GroupKind.Feeder, null, [], [],
            g.Branches.Select(i => new GroupBranch(i, model.Branches[i].From, model.Branches[i].To)).ToArray(),
            new GroupHead("branch", null, g.Head))).ToArray();
        return BoundGroups.Bind(new ModelGroupSet("0", list), model);
    }

    private static PlacedDevice Device(string id, DeviceType type, GridModel m, int branch) =>
        new(id, type, type == DeviceType.Fuse ? "" : "51", new DeviceLocation(branch, m.Branches[branch].From, m.Branches[branch].To, "from"),
            1, 1, 1, 1, [], null, [], null, null);

    private static SwitchPoint Switch(string id, GridModel m, int branch) => new(id, branch, m.Branches[branch].From, m.Branches[branch].To);
    private static TiePoint Tie(string id, GridModel m, int branch) => new(id, branch, m.Branches[branch].From, m.Branches[branch].To);

    private static FailureGroupData Rates(string group, double perItem, double repair, double? switching = null) =>
        new(group, null, perItem, repair, switching);

    private static NetworkReliabilityRequest Request(GridModel m, BoundGroups g, IReadOnlyList<FailureGroupData> data,
        IReadOnlyList<NetworkLoadPoint> loads, IReadOnlyList<FailureOverride>? overrides = null, IReadOnlyList<PlacedDevice>? devices = null,
        IReadOnlyList<SwitchPoint>? switches = null, IReadOnlyList<TiePoint>? ties = null, double? switchingHours = 0.5, bool ac = true,
        NetworkMethod method = NetworkMethod.Enumeration, NetworkMonteCarlo? mc = null, int order = 1) =>
        new(m, g, data, overrides ?? [], devices ?? [], switches ?? [], ties ?? [], loads, switchingHours, 8760.0,
            new AcVerificationOptions(ac, 0.9, 1.1), method, mc, order);

    private static void BitEqual(double? expected, double? actual, string what)
    {
        Assert.True(expected.HasValue == actual.HasValue, what + " presence");
        if (expected.HasValue)
            Assert.True(BitConverter.DoubleToInt64Bits(expected.Value) == BitConverter.DoubleToInt64Bits(actual!.Value),
                $"{what}: {expected.Value:R} vs {actual.Value:R}");
    }

    // ---------- network A: a series chain 1-2-3-4, compared with the incumbent on identical data ----------
    // Branches b0 1-2, b1 2-3, b2 3-4 with per-branch rate and repair. Loads at buses 2, 3, 4.
    private static readonly double[] RateA = [0.2, 0.3, 0.1];
    private static readonly double[] RepairA = [4, 6, 2];
    private static readonly int[] CustomersA = [100, 200, 50];
    private static readonly double[] LoadA = [2.0, 3.0, 1.5];

    private static (GridModel Model, BoundGroups Groups, NetworkLoadPoint[] Loads, FailureOverride[] Overrides) ChainA()
    {
        GridModel m = Model([Slack(), Load(2, 2.0), Load(3, 3.0), Load(4, 1.5)], [Line(1, 2), Line(2, 3), Line(3, 4)]);
        BoundGroups g = Groups(m, ("F", [0, 1, 2], 0));
        var loads = Enumerable.Range(0, 3).Select(i => new NetworkLoadPoint("LP" + (i + 1), i + 2, CustomersA[i], null)).ToArray();
        var overrides = Enumerable.Range(0, 3).Select(i => new FailureOverride(i, m.Branches[i].From, m.Branches[i].To, RateA[i], RepairA[i], null)).ToArray();
        return (m, g, loads, overrides);
    }

    private static ReliabilityStudyResult IncumbentChain(bool[] fused, bool[] switchable, double switching)
    {
        ReliabilityComponent[] comps = Enumerable.Range(0, 3)
            .Select(i => new ReliabilityComponent("C" + i, i == 0 ? null : "C" + (i - 1), fused[i], switchable[i], RateA[i], RepairA[i])).ToArray();
        ReliabilityLoadPoint[] lps = Enumerable.Range(0, 3)
            .Select(i => new ReliabilityLoadPoint("LP" + (i + 1), "C" + i, CustomersA[i], LoadA[i])).ToArray();
        return ReliabilityStudy.Run(new ReliabilityRequest(comps, lps,
            new ReliabilityAssumptions(ReliabilityStudy.IndependentFirstOrder, ReliabilityStudy.NoAlternateSupply, 8760.0), switching));
    }

    [Fact]
    public void Series_system_is_the_closed_form_sum_of_rates_and_matches_the_closed_form_by_hand()
    {
        var (m, g, loads, overrides) = ChainA();
        NetworkReliabilityResult r = NetworkReliabilityStudy.Run(Request(m, g, [Rates("F", 0, 0)], loads, overrides));
        Assert.Equal("COMPLETE", r.Status);
        // Hand: with no device and no switch every failure interrupts every load point at or below it for its repair time.
        // LP3 (bus 4, end of the chain): lambda_s = 0.2 + 0.3 + 0.1 = 0.6; U_s = 0.2x4 + 0.3x6 + 0.1x2 = 2.8; r_s = 2.8/0.6.
        LoadPointReliability end = r.LoadPoints[2];
        Assert.Equal(0.6, end.InterruptionsPerYear!.Value, 12);
        Assert.Equal(2.8, end.OutageHoursPerYear!.Value, 12);
        Assert.Equal(2.8 / 0.6, end.HoursPerInterruption!.Value, 12);
        // With no device every failure is cleared by the feeder breaker and no switch isolates it, so every load point is
        // interrupted by all three failures and waits each repair: lambda = 0.6 and U = 2.8 at all three.
        Assert.All(r.LoadPoints, p => Assert.Equal(0.6, p.InterruptionsPerYear!.Value, 12));
        Assert.All(r.LoadPoints, p => Assert.Equal(2.8, p.OutageHoursPerYear!.Value, 12));
        // Customers 350: SAIFI = 0.6, SAIDI = 2.8, ENS = L x U summed = (2 + 3 + 1.5) x 2.8.
        Assert.Equal(0.6, r.SystemIndices.Single(i => i.Name == "SAIFI").Value!.Value, 12);
        Assert.Equal(2.8, r.SystemIndices.Single(i => i.Name == "SAIDI").Value!.Value, 12);
        Assert.Equal(6.5 * 2.8, r.SystemIndices.Single(i => i.Name == "ENS").Value!.Value, 11);
        Assert.Equal(1.0 - 2.8 / 8760.0, r.SystemIndices.Single(i => i.Name == "ASAI").Value!.Value, 12);
    }

    public static IEnumerable<object[]> Configurations() =>
    [
        [false, false, false, false, false, false],     // plain series
        [false, false, true, false, true, false],       // fuse on b2, switch on b1
        [false, true, false, true, true, false],        // breaker on b1 (fuse + switch), switch on b0
        [true, false, false, false, false, true],       // fuse on b0, switch on b2
        [false, true, true, true, true, true],          // fuse on b1 and b2, switches on b0, b1 and b2
    ];

    [Theory]
    [MemberData(nameof(Configurations))]
    public void A_radial_feeder_without_ties_equals_the_incumbent_reliability_study_bit_for_bit(bool f0, bool f1, bool f2, bool s0, bool s1, bool s2)
    {
        bool[] fused = [f0, f1, f2], sw = [s0, s1, s2];
        var (m, g, loads, overrides) = ChainA();
        var devices = new List<PlacedDevice>();
        var switches = new List<SwitchPoint>();
        for (int i = 0; i < 3; i++)
        {
            if (fused[i] && sw[i]) devices.Add(Device("D" + i, DeviceType.Breaker, m, i));
            else
            {
                if (fused[i]) devices.Add(Device("D" + i, DeviceType.Fuse, m, i));
                if (sw[i]) switches.Add(Switch("S" + i, m, i));
            }
        }
        ReliabilityStudyResult inc = IncumbentChain(fused, sw, 0.5);
        foreach (bool ac in new[] { true, false })
        {
            NetworkReliabilityResult r = NetworkReliabilityStudy.Run(Request(m, g, [Rates("F", 0, 0)], loads, overrides, devices, switches, null, 0.5, ac));
            Assert.Equal("COMPLETE", r.Status);
            Assert.Equal(inc.LoadPoints.Count, r.LoadPoints.Count);
            for (int j = 0; j < inc.LoadPoints.Count; j++)
            {
                BitEqual(inc.LoadPoints[j].InterruptionsPerYear, r.LoadPoints[j].InterruptionsPerYear, $"lambda LP{j + 1} ac={ac}");
                BitEqual(inc.LoadPoints[j].OutageHoursPerYear, r.LoadPoints[j].OutageHoursPerYear, $"U LP{j + 1} ac={ac}");
                BitEqual(inc.LoadPoints[j].HoursPerInterruption, r.LoadPoints[j].HoursPerInterruption, $"r LP{j + 1} ac={ac}");
                BitEqual(inc.LoadPoints[j].EnergyNotSuppliedMwhPerYear, r.LoadPoints[j].EnergyNotSuppliedMwhPerYear, $"ENS LP{j + 1} ac={ac}");
                Assert.Equal(inc.LoadPoints[j].Exposures.Count, r.LoadPoints[j].Exposures.Count);
                for (int e = 0; e < inc.LoadPoints[j].Exposures.Count; e++)
                    Assert.Equal(inc.LoadPoints[j].Exposures[e].Restoration, r.LoadPoints[j].Exposures[e].Restoration);
            }
            for (int i = 0; i < inc.SystemIndices.Count; i++)
            {
                Assert.Equal(inc.SystemIndices[i].Name, r.SystemIndices[i].Name);
                BitEqual(inc.SystemIndices[i].Value, r.SystemIndices[i].Value, inc.SystemIndices[i].Name + $" ac={ac}");
            }
        }
    }

    // ---------- network B: a lateral feeder worked by hand ----------
    // Buses 1 (source), 2, 3, 4. b0 1-2 (head), b1 2-3 with a sectionalising switch, b2 2-4 a lateral with a fuse.
    // lambda 0.10 / 0.20 / 0.15 per year, repair 5 / 4 / 3 h, switching 0.5 h. Customers 100 / 200 / 50, loads 1 / 2 / 1.5 MW.
    [Fact]
    public void Lateral_feeder_with_a_fuse_and_a_switch_matches_the_hand_computation()
    {
        GridModel m = Model([Slack(), Load(2, 1), Load(3, 2), Load(4, 1.5)], [Line(1, 2), Line(2, 3), Line(2, 4)]);
        BoundGroups g = Groups(m, ("F", [0, 1, 2], 0));
        NetworkLoadPoint[] loads = [new("LP2", 2, 100, null), new("LP3", 3, 200, null), new("LP4", 4, 50, null)];
        FailureOverride[] ovr =
        [
            new(0, 1, 2, 0.10, 5.0, null), new(1, 2, 3, 0.20, 4.0, null), new(2, 2, 4, 0.15, 3.0, null),
        ];
        var r = NetworkReliabilityStudy.Run(Request(m, g, [Rates("F", 0, 0)], loads, ovr,
            [Device("FUSE", DeviceType.Fuse, m, 2)], [Switch("SW", m, 1)]));
        Assert.Equal("COMPLETE", r.Status);
        // Hand (see the plan in the test comment): b0 fails: the feeder breaker clears, no isolation -> every load waits 5 h.
        // b1 fails: breaker clears; SW isolates b1 so b0 re-closes: LP2 and LP4 restored in 0.5 h, LP3 (beyond the failure) waits 4 h.
        // b2 fails: the fuse clears; only LP4 (beyond the fuse) is interrupted, for the 3 h repair.
        // LP2: lambda 0.10 + 0.20 = 0.30, U = 0.10x5 + 0.20x0.5 = 0.6
        // LP3: lambda 0.10 + 0.20 = 0.30, U = 0.10x5 + 0.20x4   = 1.3
        // LP4: lambda 0.10 + 0.20 + 0.15 = 0.45, U = 0.10x5 + 0.20x0.5 + 0.15x3 = 1.05
        double[] lambda = [0.30, 0.30, 0.45], u = [0.6, 1.3, 1.05];
        for (int j = 0; j < 3; j++)
        {
            Assert.Equal(lambda[j], r.LoadPoints[j].InterruptionsPerYear!.Value, 12);
            Assert.Equal(u[j], r.LoadPoints[j].OutageHoursPerYear!.Value, 12);
            Assert.Equal(u[j] / lambda[j], r.LoadPoints[j].HoursPerInterruption!.Value, 12);
        }
        Assert.Equal(112.5 / 350.0, Idx(r, "SAIFI"), 12);
        Assert.Equal(372.5 / 350.0, Idx(r, "SAIDI"), 12);
        Assert.Equal(372.5 / 112.5, Idx(r, "CAIDI"), 12);
        Assert.Equal(1.0 - 372.5 / 350.0 / 8760.0, Idx(r, "ASAI"), 12);
        Assert.Equal(1 * 0.6 + 2 * 1.3 + 1.5 * 1.05, Idx(r, "ENS"), 12);
        Assert.Equal((1 * 0.6 + 2 * 1.3 + 1.5 * 1.05) / 350.0, Idx(r, "AENS"), 12);
        // Events: clearing device and isolation.
        NetworkOutageEvent e1 = r.Events.Single(e => e.Branch == 1);
        Assert.Equal("FeederBreaker", e1.ClearedBy);
        Assert.Equal("SW", e1.IsolatingSwitch);
        Assert.Equal(["Switching", "Repair", "Switching"], e1.Loads.Select(l => l.Restoration).ToArray());
        Assert.Equal("SECURE", e1.AcVerification);
        Assert.Equal("FUSE", r.Events.Single(e => e.Branch == 2).ClearedBy);
        Assert.Equal([4], r.Events.Single(e => e.Branch == 2).InterruptedBuses);
    }

    private static double Idx(NetworkReliabilityResult r, string name) => r.SystemIndices.Single(i => i.Name == name).Value!.Value;

    // ---------- network C: back-feed through one normally-open tie ----------
    // Feeder A: b0 1-2, b1 2-3 (sectionalising switch on b1). Feeder B: b2 1-4. Tie b3 3-4 normally open.
    // lambda 0.1 / 0.2 / 0.3, repair 6 / 4 / 2 h, switching 0.5 h; one customer group of 100 at each of buses 2, 3, 4, 1 MW each.
    private static (GridModel M, BoundGroups G, NetworkLoadPoint[] Loads, FailureOverride[] Ovr) NetworkC(double tieRate = 50, double load3 = 1)
    {
        GridModel m = Model([Slack(), Load(2, 1), Load(3, load3), Load(4, 1)], [Line(1, 2), Line(2, 3), Line(1, 4), Line(3, 4, tieRate, inService: false)]);
        BoundGroups g = Groups(m, ("F", [0, 1, 2], 0));
        NetworkLoadPoint[] loads = [new("LP2", 2, 100, null), new("LP3", 3, 100, null), new("LP4", 4, 100, null)];
        FailureOverride[] ovr = [new(0, 1, 2, 0.1, 6, null), new(1, 2, 3, 0.2, 4, null), new(2, 1, 4, 0.3, 2, null)];
        return (m, g, loads, ovr);
    }

    [Fact]
    public void A_tie_back_feeds_the_isolated_section_so_the_outage_lasts_the_switching_time_not_the_repair()
    {
        var (m, g, loads, ovr) = NetworkC();
        var r = NetworkReliabilityStudy.Run(Request(m, g, [Rates("F", 0, 0)], loads, ovr, null, [Switch("SW", m, 1)], [Tie("T", m, 3)]));
        Assert.Equal("COMPLETE", r.Status);
        // Hand. b0 fails (head of feeder A, no device -> feeder breaker; b0 has no switch): buses 2 and 3 dark. The first switch
        // below b0 is SW on b1, opened; bus 2 stays joined to the failed b0 (waits 6 h), bus 3 is a separate section and the tie 3-4
        // closes from the live bus 4: back-fed, 0.5 h. b1 fails (SW on b1): b0 re-closes, bus 2 back in 0.5 h; bus 3 back-fed in 0.5 h.
        // b2 fails (the only path to bus 4, no switch): bus 4 waits 2 h (the tie cannot be used: the section still holds the failed conductor).
        // LP2: 0.1 x 6 + 0.2 x 0.5 = 0.7 (lambda 0.3). LP3: 0.1 x 0.5 + 0.2 x 0.5 = 0.15 (0.3). LP4: 0.3 x 2 = 0.6 (0.3).
        Assert.Equal([0.3, 0.3, 0.3], r.LoadPoints.Select(p => p.InterruptionsPerYear!.Value).ToArray(), new Near());
        Assert.Equal([0.7, 0.15, 0.6], r.LoadPoints.Select(p => p.OutageHoursPerYear!.Value).ToArray(), new Near());
        NetworkOutageEvent b0 = r.Events.Single(e => e.Branch == 0);
        Assert.Equal(["Repair", "BackFeed"], b0.Loads.Select(l => l.Restoration).ToArray());
        Assert.Equal("T", b0.Loads[1].Tie);
        Assert.Equal(0.5, b0.Loads[1].DurationHours!.Value, 12);          // the switching time, not the 6 h repair
        Assert.Equal(6.0, b0.Loads[0].DurationHours!.Value, 12);
        Assert.NotNull(b0.Loads[0].Reason);
        Assert.Equal("SECURE", b0.AcVerification);
        Assert.Equal(["tie:T"], b0.RestorationPath.Select(s => s.Action).ToArray());
        Assert.Equal("SECURE", b0.RestorationPath[0].Disposition);
        NetworkOutageEvent b1 = r.Events.Single(e => e.Branch == 1);
        Assert.Equal(["Switching", "BackFeed"], b1.Loads.Select(l => l.Restoration).ToArray());
        Assert.Equal(2, b1.RestorationPath.Count);                        // source re-closure and the tie, both verified
        NetworkOutageEvent b2 = r.Events.Single(e => e.Branch == 2);
        Assert.Equal(["Repair"], b2.Loads.Select(l => l.Restoration).ToArray());
        Assert.Contains("failed branch", b2.Loads[0].Reason!);

        // The same feeder without the tie (the incumbent's no-back-feed rule): LP3 waits for every repair.
        var none = NetworkReliabilityStudy.Run(Request(m, g, [Rates("F", 0, 0)], loads, ovr, null, [Switch("SW", m, 1)], null));
        Assert.Equal(0.1 * 6 + 0.2 * 4, none.LoadPoints[1].OutageHoursPerYear!.Value, 12);
        // The back-feed lowers SAIDI: (100 x 0.7 + 100 x 0.15 + 100 x 0.6) / 300 against the no-tie figure.
        Assert.Equal((0.7 + 0.15 + 0.6) / 3.0, Idx(r, "SAIDI"), 12);
        Assert.True(Idx(r, "SAIDI") < Idx(none, "SAIDI"));
    }

    private sealed class Near : IEqualityComparer<double>
    {
        public bool Equals(double x, double y) => Math.Abs(x - y) < 1e-12;
        public int GetHashCode(double v) => 0;
    }

    [Fact]
    public void A_tie_whose_restored_network_fails_the_ac_verifier_does_not_restore_and_says_why()
    {
        // The tie is rated 1 MVA and bus 3 draws 5 MW: closing it overloads the tie, so the load stays unrestored (repair time).
        var (m, g, loads, ovr) = NetworkC(tieRate: 1, load3: 5);
        var r = NetworkReliabilityStudy.Run(Request(m, g, [Rates("F", 0, 0)], loads, ovr, null, [Switch("SW", m, 1)], [Tie("T", m, 3)]));
        Assert.Equal("COMPLETE", r.Status);
        NetworkOutageEvent b0 = r.Events.Single(e => e.Branch == 0);
        NetworkLoadOutcome lp3 = b0.Loads.Single(l => l.LoadPoint == "LP3");
        Assert.Equal("Repair", lp3.Restoration);
        Assert.Equal(6.0, lp3.DurationHours!.Value, 12);
        Assert.Contains("AC verifier", lp3.Reason!);
        Assert.Contains("T", lp3.Reason!);
        // LP3: b0 -> 6 h, b1 -> bus 2 re-closes, bus 3 repair 4 h: U = 0.1 x 6 + 0.2 x 4 = 1.4.
        Assert.Equal(1.4, r.LoadPoints[1].OutageHoursPerYear!.Value, 12);
        // With AC verification off the same tie is accepted on connectivity, and the event says the AC state was not run.
        var off = NetworkReliabilityStudy.Run(Request(m, g, [Rates("F", 0, 0)], loads, ovr, null, [Switch("SW", m, 1)], [Tie("T", m, 3)], ac: false));
        Assert.Equal("BackFeed", off.Events.Single(e => e.Branch == 0).Loads.Single(l => l.LoadPoint == "LP3").Restoration);
        Assert.Equal("NOT RUN", off.Events.Single(e => e.Branch == 0).AcVerification);
        Assert.Equal(0.15, off.LoadPoints[1].OutageHoursPerYear!.Value, 12);
    }

    [Fact]
    public void A_breaker_placed_on_the_failed_branch_lets_the_tie_back_feed_the_section_below_it()
    {
        // b2 (1-4) with a breaker is a fully isolated section: the tie 3-4 can then back-feed bus 4 from feeder A.
        var (m, g, loads, ovr) = NetworkC();
        var r = NetworkReliabilityStudy.Run(Request(m, g, [Rates("F", 0, 0)], loads, ovr, [Device("BRK", DeviceType.Breaker, m, 2)],
            [Switch("SW", m, 1)], [Tie("T", m, 3)]));
        NetworkOutageEvent b2 = r.Events.Single(e => e.Branch == 2);
        Assert.Equal("BRK", b2.ClearedBy);
        Assert.Equal("BackFeed", b2.Loads.Single().Restoration);
        Assert.Equal(0.5, b2.Loads.Single().DurationHours!.Value, 12);
        Assert.Equal(0.3 * 0.5, r.LoadPoints[2].OutageHoursPerYear!.Value, 12);
    }

    // ---------- failure model ----------
    [Fact]
    public void Failure_rate_per_km_uses_the_circuit_length_and_a_missing_length_is_reported_not_read_as_zero()
    {
        GridModel m = Model([Slack(), Load(2, 1), Load(3, 1)], [Line(1, 2, lengthKm: 2.0), Line(2, 3)]);
        BoundGroups g = Groups(m, ("F", [0, 1], 0));
        NetworkLoadPoint[] loads = [new("A", 3, 10, null)];
        // Branch 0: 0.065 per km x 2 km + 0.01 per item; branch 1 has no length.
        var data = new FailureGroupData("F", 0.065, 0.01, 5, null);
        var r = NetworkReliabilityStudy.Run(Request(m, g, [data], loads));
        Assert.Equal("INCOMPLETE", r.Status);
        Assert.Contains(r.Missing, x => x.Component == "b1" && x.Field == "lengthKm");
        IReadOnlyList<FailureComponent> comps = FailureModel.Resolve(m, g, [data], []);
        Assert.Equal(0.065 * 2.0 + 0.01, comps[0].FailuresPerYear!.Value, 15);
        Assert.Null(comps[1].FailuresPerYear);
        // An override supplies the figure and clears the issue.
        var fixedUp = NetworkReliabilityStudy.Run(Request(m, g, [data], loads, [new FailureOverride(1, 2, 3, 0.04, null, null)]));
        Assert.Equal("COMPLETE", fixedUp.Status);
        Assert.Equal(0.065 * 2.0 + 0.01 + 0.04, fixedUp.LoadPoints[0].InterruptionsPerYear!.Value, 14);
    }

    [Fact]
    public void Missing_repair_or_switching_data_gives_incomplete_with_the_withheld_fields_named()
    {
        var (m, g, loads, _) = NetworkC();
        var r = NetworkReliabilityStudy.Run(Request(m, g, [new FailureGroupData("F", null, 0.1, null, null)], loads, null, null,
            [Switch("SW", m, 1)], null, switchingHours: null));
        Assert.Equal("INCOMPLETE", r.Status);
        Assert.Contains(r.Missing, x => x.Field == "repairHours");
        Assert.Contains(r.Missing, x => x.Component == "(study)" && x.Field == "switchingHours");
        Assert.Null(r.SystemIndices.Single(i => i.Name == "SAIDI").Value);
        Assert.Equal(0.2, r.LoadPoints[0].InterruptionsPerYear!.Value, 12);      // b0 and b1 at 0.1 each: the rate is known, the duration is withheld
    }

    // ---------- refusals and scope ----------
    [Fact]
    public void Inputs_outside_the_method_are_refused_or_reported_out_of_scope_never_a_number()
    {
        var (m, g, loads, ovr) = NetworkC();
        var data = new[] { Rates("F", 0, 0) };
        // A tie that is in service, a switch on a tie, an unknown bus, a device whose branch identity does not match, a missing group.
        Assert.Throws<ArgumentException>(() => NetworkReliabilityStudy.Run(Request(m, g, data, loads, ovr, null, null, [Tie("T", m, 3), Tie("T", m, 3)])));
        Assert.Throws<ArgumentException>(() => NetworkReliabilityStudy.Run(Request(m, g, data, loads, ovr, null, [Switch("SW", m, 3)])));
        Assert.Throws<ArgumentException>(() => NetworkReliabilityStudy.Run(Request(m, g, data, [new("X", 99, 1, null)], ovr)));
        var wrong = new PlacedDevice("BAD", DeviceType.Breaker, "51", new DeviceLocation(1, 9, 9, "from"), 1, 1, 1, 1, [], null, [], null, null);
        Assert.Throws<ArgumentException>(() => NetworkReliabilityStudy.Run(Request(m, g, data, loads, ovr, [wrong])));
        Assert.Throws<ArgumentException>(() => NetworkReliabilityStudy.Run(Request(m, g, [Rates("NOPE", 0, 0)], loads)));
        Assert.Throws<ArgumentException>(() => NetworkReliabilityStudy.Run(Request(m, g, [Rates("F", -1, 0)], loads)));
        Assert.Throws<ArgumentException>(() => NetworkReliabilityStudy.Run(Request(m, g, data, loads, ovr, switchingHours: -1)));
        // A meshed in-service network is outside the radial method.
        GridModel mesh = Model(m.Buses, m.Branches.Select((b, i) => i == 3 ? b with { InService = true } : b));
        // The same mesh with the tie declared is radial: the study opens the declared tie, so the stored state does not matter.
        var declared = NetworkReliabilityStudy.Run(Request(mesh, Groups(mesh, ("F", [0, 1, 2], 0)), data, loads, ovr, null, [Switch("SW", mesh, 1)], [Tie("T", mesh, 3)]));
        Assert.Equal("COMPLETE", declared.Status);
        Assert.Equal(0.15, declared.LoadPoints[1].OutageHoursPerYear!.Value, 12);
        var meshResult = NetworkReliabilityStudy.Run(Request(mesh, Groups(mesh, ("F", [0, 1, 2], 0)), data, loads, ovr));
        Assert.Equal("OUT OF SCOPE", meshResult.Status);
        Assert.Contains("radial", meshResult.Reason!);
        Assert.Empty(meshResult.SystemIndices);
        // Second-order enumeration is deferred and says so.
        var second = NetworkReliabilityStudy.Run(Request(m, g, data, loads, ovr, order: 2));
        Assert.Equal("OUT OF SCOPE", second.Status);
        Assert.Contains("Second-order", second.Reason!);
        // The intact network must pass the AC verifier: a feeder that cannot carry its load is refused before any index.
        GridModel heavy = Model([Slack(), Load(2, 400), Load(3, 1), Load(4, 1)], m.Branches);
        var heavyResult = NetworkReliabilityStudy.Run(Request(heavy, Groups(heavy, ("F", [0, 1, 2], 0)), data, loads, ovr));
        Assert.Equal("OUT OF SCOPE", heavyResult.Status);
        Assert.Contains("intact network", heavyResult.Reason!);
        // Monte Carlo needs a seed.
        Assert.Throws<ArgumentException>(() => NetworkReliabilityStudy.Run(Request(m, g, data, loads, ovr, null, null, null, 0.5, true,
            NetworkMethod.MonteCarlo, new NetworkMonteCarlo(null, 100))));
        Assert.Throws<ArgumentException>(() => NetworkReliabilityStudy.Run(Request(m, g, data, loads, ovr, null, null, null, 0.5, true,
            NetworkMethod.Enumeration, new NetworkMonteCarlo(1, 100))));
    }

    // ---------- Monte Carlo ----------
    [Fact]
    public void Monte_carlo_converges_to_the_enumeration_within_its_standard_error_and_is_thread_invariant()
    {
        var (m, g, loads, ovr) = NetworkC();
        var data = new[] { Rates("F", 0, 0) };
        IReadOnlyList<SwitchPoint> sw = [Switch("SW", m, 1)];
        IReadOnlyList<TiePoint> ties = [Tie("T", m, 3)];
        var exact = NetworkReliabilityStudy.Run(Request(m, g, data, loads, ovr, null, sw, ties));
        NetworkReliabilityResult Mc(ulong seed, long years, int threads, string dist = "exponential") =>
            NetworkReliabilityStudy.Run(Request(m, g, data, loads, ovr, null, sw, ties, 0.5, true, NetworkMethod.MonteCarlo,
                new NetworkMonteCarlo(seed, years, dist, "r413", threads)));
        NetworkReliabilityResult one = Mc(11, 200_000, 1);
        Assert.Equal("COMPLETE", one.Status);
        Assert.Equal("monte-carlo", one.Method);
        foreach (string name in new[] { "SAIFI", "SAIDI", "ENS" })
        {
            double se = one.MonteCarlo!.StandardErrors.Single(e => e.Name == name).StandardError!.Value;
            Assert.True(se > 0);
            double diff = Math.Abs(Idx(one, name) - Idx(exact, name));
            Assert.True(diff <= 4.0 * se, $"{name}: MC {Idx(one, name)} vs enumeration {Idx(exact, name)}, 4 SE = {4 * se}");
        }
        // Load-point figures converge too (LP1 is the one with an exponential repair tail).
        for (int j = 0; j < 3; j++)
            Assert.True(Math.Abs(one.LoadPoints[j].OutageHoursPerYear!.Value - exact.LoadPoints[j].OutageHoursPerYear!.Value) < 0.05,
                $"LP{j}: {one.LoadPoints[j].OutageHoursPerYear} vs {exact.LoadPoints[j].OutageHoursPerYear}");
        // Deterministic repair: the same mean, a smaller variance.
        NetworkReliabilityResult det = Mc(11, 100_000, 1, "deterministic");
        double seDet = det.MonteCarlo!.StandardErrors.Single(e => e.Name == "SAIDI").StandardError!.Value;
        Assert.True(Math.Abs(Idx(det, "SAIDI") - Idx(exact, "SAIDI")) <= 4 * seDet);
        // Bit repeatability: 1 and 16 threads agree on every value and the digest; another seed differs; the same seed repeats.
        NetworkReliabilityResult many = Mc(11, 200_000, 16);
        Assert.Equal(one.MonteCarlo!.ResultDigest, many.MonteCarlo!.ResultDigest);
        for (int i = 0; i < one.SystemIndices.Count; i++) BitEqual(one.SystemIndices[i].Value, many.SystemIndices[i].Value, one.SystemIndices[i].Name);
        for (int i = 0; i < one.MonteCarlo.StandardErrors.Count; i++)
            BitEqual(one.MonteCarlo.StandardErrors[i].StandardError, many.MonteCarlo.StandardErrors[i].StandardError, "SE " + one.MonteCarlo.StandardErrors[i].Name);
        Assert.NotEqual(one.MonteCarlo.ResultDigest, Mc(12, 200_000, 1).MonteCarlo!.ResultDigest);
        Assert.Equal(one.MonteCarlo.ResultDigest, Mc(11, 200_000, 4).MonteCarlo!.ResultDigest);
        Assert.Equal("gridsim-rng/1", one.MonteCarlo.GeneratorVersion);
        // The standard error shrinks like 1/sqrt(N): four times the years, about half the error.
        double se50 = Mc(11, 50_000, 1).MonteCarlo!.StandardErrors.Single(e => e.Name == "SAIDI").StandardError!.Value;
        double se200 = one.MonteCarlo.StandardErrors.Single(e => e.Name == "SAIDI").StandardError!.Value;
        Assert.InRange(se50 / se200, 1.8, 2.2);
    }

    [Fact]
    public void The_poisson_inversion_matches_the_closed_form_probabilities()
    {
        // lambda = 0.5: P(0) = e^-0.5 = 0.60653065971..., P(1) = 0.5 e^-0.5 = 0.30326532985..., cumulative P(<=1) = 0.90979598956.
        double p0 = Math.Exp(-0.5), p1 = 0.5 * Math.Exp(-0.5);
        Assert.Equal(0, NetworkMonteCarloRunner.Poisson(0.5, 0.0));
        Assert.Equal(0, NetworkMonteCarloRunner.Poisson(0.5, p0 - 1e-9));
        Assert.Equal(1, NetworkMonteCarloRunner.Poisson(0.5, p0 + 1e-9));
        Assert.Equal(1, NetworkMonteCarloRunner.Poisson(0.5, p0 + p1 - 1e-9));
        Assert.Equal(2, NetworkMonteCarloRunner.Poisson(0.5, p0 + p1 + 1e-9));
    }

    [Fact]
    public void The_result_is_repeatable_and_the_enumeration_order_is_the_model_order()
    {
        var (m, g, loads, ovr) = NetworkC();
        var a = NetworkReliabilityStudy.Run(Request(m, g, [Rates("F", 0, 0)], loads, ovr, null, [Switch("SW", m, 1)], [Tie("T", m, 3)]));
        var b = NetworkReliabilityStudy.Run(Request(m, g, [Rates("F", 0, 0)], loads, ovr, null, [Switch("SW", m, 1)], [Tie("T", m, 3)]));
        Assert.Equal(System.Text.Json.JsonSerializer.Serialize(a), System.Text.Json.JsonSerializer.Serialize(b));
        Assert.Equal([0, 1, 2], a.Events.Select(e => e.Branch).ToArray());
    }
}
