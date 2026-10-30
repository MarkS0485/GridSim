using GridSim.Core.IO.Cim;
using GridSim.Core.Model;
using GridSim.Core.PowerFlow;
using Xunit;

namespace GridSim.Tests;

/// <summary>
/// N307 (LF3; ruled by main under Ruling R1): a shared remote voltage control (machines at several buses regulating one
/// remote bus) shares its reactive output in proportion to each machine's reactive range (powsybl; the PSS/E RMPCT
/// default), and a transformer's magnetising conductance g is represented half at each end exactly where the branch puts
/// each half of its susceptance b (PSS/E and PowerFactory carry the iron loss; it used to be listed as not represented).
/// </summary>
public sealed partial class Lf3FollowUpsTests
{
    // ================================================================ shared remote control ====================

    // 110 kV: the slack G1 at n1, lines n1 - n2, n2 - n3, n2 - n4 (0.01 + j0.1 pu each), a load at n2 (60 MW, 40 MVAr),
    // and G2 at n3 (range 100 MVAr) and G3 at n4 (range g3Range) both regulating n2 at 112 kV.
    private static CimNetwork SharedNet(double? g3MinQ = -100.0, double? g3MaxQ = 200.0) => new(
        new[] { new CimBus("n1", "N1", 110.0), new CimBus("n2", "N2", 110.0), new CimBus("n3", "N3", 110.0), new CimBus("n4", "N4", 110.0) },
        new[]
        {
            new CimBranch("l1", "L1", "n1", "n2", 1.21, 12.1, 0.0),
            new CimBranch("l2", "L2", "n2", "n3", 1.21, 12.1, 0.0),
            new CimBranch("l3", "L3", "n2", "n4", 1.21, 12.1, 0.0),
        },
        new[] { new CimLoad("ld", "LD", "n2", 60.0, 40.0) },
        new[]
        {
            new CimGenerator("g1", "G1", "n1", 0.0, TargetKv: 110.0, VoltageControl: true),
            new CimGenerator("g2", "G2", "n3", 20.0, VoltageControl: false, MinQ: -50.0, MaxQ: 50.0, RegulatedNode: "n2", RemoteTargetKv: 112.0),
            new CimGenerator("g3", "G3", "n4", 10.0, VoltageControl: false, MinQ: g3MinQ, MaxQ: g3MaxQ, RegulatedNode: "n2", RemoteTargetKv: 112.0),
        },
        Array.Empty<CimTransformer>());

    [Fact]
    public void N307_a_shared_control_carries_the_machines_reactive_ranges_as_sharing_keys()
    {
        var notes = new List<string>();
        CimBridgeResult bridged = CimGridModelBridge.Bridge(SharedNet(), notes: notes);
        CimRemoteVoltageControl rc = Assert.Single(bridged.RemoteVoltageControls);
        Assert.Equal(new[] { 3, 4 }, rc.GeneratorBusIds);
        Assert.Equal(new[] { 50.0 - -50.0, 200.0 - -100.0 }, rc.ReactiveShareKeys);
        Assert.Contains(notes, n => n.StartsWith("Remote voltage control: bus 2 'N2'")
                                    && n.Contains("in proportion to their reactive ranges (100 / 300 MVAr)"));
        Assert.DoesNotContain(notes, n => n.Contains("holding one common per-unit voltage"));

        // A machine with no finite range: equal shares, said.
        var notes2 = new List<string>();
        CimRemoteVoltageControl eq = Assert.Single(CimGridModelBridge.Bridge(SharedNet(g3MaxQ: null), notes: notes2).RemoteVoltageControls);
        Assert.Equal(new[] { 1.0, 1.0 }, eq.ReactiveShareKeys);
        Assert.Contains(notes2, n => n.Contains("sharing their reactive output equally"));
    }

    [Fact]
    public void N307_a_shared_control_holds_the_remote_bus_and_shares_q_by_reactive_range()
    {
        CimBridgeResult bridged = CimGridModelBridge.Bridge(SharedNet());
        var solver = new CimControlledPowerFlow(new NewtonRaphsonPowerFlow(), bridged);
        PowerFlowResult r = solver.Solve(bridged.Model);
        Assert.True(r.Converged, solver.LastNote);
        Assert.True(solver.ControlsSettled);
        Assert.True(Math.Abs(r.Vm[1] - 112.0 / 110.0) <= 1e-8, $"n2 at {r.Vm[1] * 110.0} kV");
        // Reactive generation at n3 and n4 (no load there): Q3 / 100 = Q4 / 300 within the sharing tolerance, i.e.
        // |Q3 / 100 - Q4 / 300| x 400 / baseMVA <= CimControlledPowerFlow.ShareTolerancePu.
        double q3 = r.QInjMvar[2], q4 = r.QInjMvar[3];
        double share = Math.Abs(q4 / 300.0 - q3 / 100.0) * 400.0 / 100.0;
        Assert.True(share <= CimControlledPowerFlow.ShareTolerancePu, $"Q3 {q3} MVAr, Q4 {q4} MVAr: sharing off by {share:E3} pu");
        Assert.True(q3 > 1.0 && q4 > 1.0);   // both machines lift n2 (it is below 112 kV without them)
        // One state per machine bus, in bus order; neither at a limit.
        Assert.Equal(2, solver.LastStates.Count);
        Assert.All(solver.LastStates, s => Assert.False(s.AtLimit));
        Assert.Contains("(machine bus 3)", solver.LastStates[0].Name);

        // The dense and the sparse engines give the same bits through the loop.
        PowerFlowResult sparse = new CimControlledPowerFlow(new SparseNewtonRaphsonPowerFlow(), bridged).Solve(bridged.Model);
        Assert.Equal(r.Vm, sparse.Vm);
        Assert.Equal(r.VaDeg, sparse.VaDeg);
    }

    [Fact]
    public void N307_sharing_keys_that_cannot_share_are_refused_by_name()
    {
        CimBridgeResult good = CimGridModelBridge.Bridge(SharedNet());
        CimRemoteVoltageControl rc = good.RemoteVoltageControls.Single();
        foreach (IReadOnlyList<double> bad in new[] { new[] { 1.0 }, new[] { 1.0, 0.0 }, new[] { 1.0, double.NaN }, new[] { -1.0, 2.0 } })
        {
            CimBridgeResult b = WithRemote(good, rc with { ReactiveShareKeys = bad });
            var ex = Assert.Throws<ArgumentException>(() => new CimControlledPowerFlow(new NewtonRaphsonPowerFlow(), b));
            Assert.Contains("CIM remote voltage control of bus 2", ex.Message);
        }
        // No keys (a control built in code without them) is equal shares, accepted.
        _ = new CimControlledPowerFlow(new NewtonRaphsonPowerFlow(), WithRemote(good, rc with { ReactiveShareKeys = null }));
    }

    // A copy of a bridge result with one remote control replaced (the internal constructor; tests see internals).
    private static CimBridgeResult WithRemote(CimBridgeResult b, CimRemoteVoltageControl rc) => new(
        b.Model, b.Original, b.Network, b.BusIdOfNode.ToDictionary(kv => kv.Key, kv => kv.Value, StringComparer.Ordinal),
        b.DroppedNodes, b.BranchNodes.ToList(),
        new CimControls(new[] { rc }, b.SusceptanceControls, b.ShuntRegulations));

    // ================================================================ transformer magnetising conductance =======

    // A 120 / 10.5 kV unit (17.28 ohm on its 120 kV side) between a 110 kV and a 10 kV bus, with the slack at the 110 kV
    // bus and magnetising admittance on end 1.
    private static CimNetwork MagnetisingNet(double g, double b = 0.0) => new(
        new[] { new CimBus("hv", "HV", 110.0), new CimBus("lv", "LV", 10.0) },
        Array.Empty<CimBranch>(),
        Array.Empty<CimLoad>(),
        new[] { new CimGenerator("g", "G", "hv", 0.0, TargetKv: 110.0, VoltageControl: true) },
        new[]
        {
            new CimTransformer("t2", "T2", new[]
            {
                new CimTransformerEnd(1, "hv", 0.0, 17.28, 120.0, G: g, B: b),
                new CimTransformerEnd(2, "lv", 0.0, 0.0, 10.5),
            }, 1.0, 0.0),
        });

    [Fact]
    public void N307_a_transformer_magnetising_conductance_is_half_at_each_end_where_b_is()
    {
        var notes = new List<string>();
        GridModel m = CimGridModelBridge.ToGridModel(MagnetisingNet(0.00001, -0.0002), notes: notes);
        Branch t2 = m.Branches.Single();
        // The same expression and referral as b, then half at each end: the to end as it is, the from end through the
        // tap (AdmittanceStamp: Yff = (ys + jb/2) / a^2).
        double n1 = 120.0 / 110.0, n2 = 10.5 / 10.0;
        double gPu = (0.00001 * 120.0 * 120.0 + 0.0 * 10.5 * 10.5) / 100.0;
        gPu /= n2 * n2;
        double tap = 1.0 * (n1 / n2);
        Assert.Equal(tap, t2.TapRatio);
        double half = gPu / 2.0 * 100.0;
        AssertBits(0.0 + half / (tap * tap), m.Buses[0].Gs, "HV Gs");
        AssertBits(0.0 + half, m.Buses[1].Gs, "LV Gs");
        // b is unchanged by N307 (still the branch's own susceptance).
        Assert.Equal((-0.0002 * 120.0 * 120.0 + 0.0 * 10.5 * 10.5) / 100.0 / (n2 * n2), t2.B);
        Assert.Contains(notes, n => n.StartsWith("Transformer T2: its magnetising conductance") && n.Contains("is represented"));

        // Physics: at no load with r = 0 the only active power is the iron loss, so the slack supplies exactly what the two
        // conductances draw at the solved voltages.
        PowerFlowResult r = new NewtonRaphsonPowerFlow().Solve(m);
        Assert.True(r.Converged);
        double iron = m.Buses[0].Gs * r.Vm[0] * r.Vm[0] + m.Buses[1].Gs * r.Vm[1] * r.Vm[1];
        Assert.True(Math.Abs(r.PInjMw[0] - iron) <= 1e-5, $"slack {r.PInjMw[0]} MW vs iron loss {iron} MW");
        Assert.True(iron > 0.05);
    }

    [Fact]
    public void N307_a_transformer_without_magnetising_conductance_is_bridged_exactly_as_before()
    {
        var notes = new List<string>();
        GridModel m = CimGridModelBridge.ToGridModel(MagnetisingNet(0.0, -0.0002), notes: notes);
        AssertBits(0.0, m.Buses[0].Gs, "HV Gs");
        AssertBits(0.0, m.Buses[1].Gs, "LV Gs");
        Assert.DoesNotContain(notes, n => n.Contains("magnetising conductance"));
    }

    [Fact]
    public void N307_a_three_winding_winding_puts_its_conductance_at_its_bus_and_the_star_point()
    {
        var net = new CimNetwork(
            new[] { new CimBus("hv", "HV", 400.0), new CimBus("mv", "MV", 132.0), new CimBus("lv", "LV", 33.0) },
            Array.Empty<CimBranch>(), Array.Empty<CimLoad>(),
            new[] { new CimGenerator("g", "G", "hv", 0.0, TargetKv: 400.0, VoltageControl: true) },
            new[]
            {
                new CimTransformer("t3", "T3", new[]
                {
                    new CimTransformerEnd(1, "hv", 0.0, 96.0, 400.0, G: 0.000002),
                    new CimTransformerEnd(2, "mv", 0.0, 1.44, 132.0),
                    new CimTransformerEnd(3, "lv", 0.0, 1.26, 33.0),
                }, 1.0, 0.0),
            });
        GridModel m = CimGridModelBridge.ToGridModel(net);
        Bus star = m.Buses.Single(b => b.Name == "T3 star point");
        double gPu = 0.000002 * 400.0 * 400.0 / 100.0;
        double half = gPu / 2.0 * 100.0;
        // Ratings equal to the bus bases: every tap is 1.0, so both halves are the same.
        AssertBits(0.0 + half / (1.0 * 1.0), m.Buses[0].Gs, "HV Gs");
        AssertBits(0.0 + half, star.Gs, "star Gs");
        AssertBits(0.0, m.Buses[1].Gs, "MV Gs");
        AssertBits(0.0, m.Buses[2].Gs, "LV Gs");
    }
}
