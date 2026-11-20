using GridSim.Core.Model;
using GridSim.Core.Model.Substations;
using GridSim.Core.PowerFlow;
using Xunit;
using static GridSim.Tests.Substations.SubstationFixtures;

namespace GridSim.Tests.Substations;

/// <summary>
/// RC4 R4-11 part A: node-breaker topology processing. Every grouping below is worked out by hand in the comments from
/// the switch states and the fixture's node names (ordinal order). A group is written as its nodes joined with "+" and
/// the buses are listed in the order of each group's lowest node; the bus id (default numbering) is 1 + the ordinal rank
/// of that lowest node among ALL nodes of the model.
/// </summary>
public sealed class SubstationProjectionTests
{
    private static SubstationProjectionResult Normal(SubstationModel m) => SubstationProjection.ProjectNormal(m);

    private static SubstationProjectionResult At(SubstationModel m, string[] open, string[] close) =>
        SubstationProjection.Project(m, SubstationProjection.With(m.NormalStates(), open, close));

    // ------------------------------------------------------------------------------------------ double busbar

    [Fact]
    public void Double_busbar_coupler_closed_is_one_bus()
    {
        // Coupler closed joins X1 and X2. A rides X1 (A_d1), B and D ride X2; every breaker closed. So every node is
        // in one group: A1 A2 B1 B2 D1 D2 X1 X2. Its lowest node is A1 (rank 0), so the bus id is 1.
        SubstationProjectionResult r = Normal(DoubleBusbar());
        Assert.Equal("A1+A2+B1+B2+D1+D2+X1+X2", r.GroupingText());
        Assert.Equal(new[] { 1 }, r.Buses.Select(b => b.BusId));
        Assert.Single(r.Model.Buses);
        Assert.Equal(BusType.Slack, r.Model.Buses[0].Type);
        Assert.Equal(100, r.Model.Buses[0].Pd);   // LA 20 + LD 80
        Assert.Equal(10, r.Model.Buses[0].Qd);
    }

    [Fact]
    public void Double_busbar_coupler_open_splits_the_bus_group_in_two_and_reclosing_restores_it()
    {
        SubstationModel m = DoubleBusbar();
        SubstationProjectionResult closed = Normal(m);

        // Coupler open: X1 keeps A's chain (A1 via A_d1, A2 via A_cb): group A1+A2+X1. X2 keeps B and D:
        // B1 (B_d2), B2 (B_cb), D1 (D_d2), D2 (D_cb): group B1+B2+D1+D2+X2.
        // Ranks: A1=0, B1=2, so ids 1 and 3.
        SubstationProjectionResult open = At(m, new[] { "CPL" }, Array.Empty<string>());
        Assert.Equal("A1+A2+X1|B1+B2+D1+D2+X2", open.GroupingText());
        Assert.Equal(new[] { 1, 3 }, open.Buses.Select(b => b.BusId));
        Assert.Equal(BusType.Slack, open.Model.Buses.Single(b => b.Id == 1).Type);   // GA
        Assert.Equal(BusType.Pv, open.Model.Buses.Single(b => b.Id == 3).Type);      // GB
        Assert.Equal(80, open.Model.Buses.Single(b => b.Id == 3).Pd);               // LD is on the B/D side
        Assert.Equal(20, open.Model.Buses.Single(b => b.Id == 1).Pd);               // LA stays with GA

        // Reclosing restores the same groups, ids and model.
        SubstationProjectionResult back = At(m, Array.Empty<string>(), new[] { "CPL" });
        Assert.Equal(closed.GroupingText(), back.GroupingText());
        Assert.Equal(closed.Model.Buses, back.Model.Buses);
        Assert.Equal(closed.NodeToBus, back.NodeToBus);
    }

    [Fact]
    public void Moving_a_feeder_to_the_other_busbar_changes_the_group_exactly_as_worked_by_hand()
    {
        // Coupler open and D transferred from X2 to X1: close D_d1, open D_d2.
        // X1 group: X1, A1, A2 (A_d1, A_cb) and D1, D2 (D_d1, D_cb). X2 group: X2, B1, B2, which holds only GB, so
        // it is not a bus: GB is isolated and the one bus (id 1) now carries LA and LD.
        SubstationProjectionResult r = At(DoubleBusbar(), new[] { "CPL", "D_d2" }, new[] { "D_d1" });
        Assert.Equal("A1+A2+D1+D2+X1|B1+B2+X2", r.GroupingText());
        Assert.Equal(new[] { 1 }, r.Buses.Select(b => b.BusId));
        Assert.Equal(100, r.Model.Buses.Single(b => b.Id == 1).Pd);
        Assert.Equal(EquipmentDisposition.Isolated, r.Equipment.Single(e => e.EquipmentId == "GB").Disposition);
        Assert.Null(r.Groups[1].BusId);
    }

    [Fact]
    public void Both_busbar_disconnectors_closed_while_the_coupler_is_open_joins_the_busbars_through_the_feeder()
    {
        // A selector fault state: A_d1 and A_d2 both closed couples X1 and X2 through A1 even with CPL open.
        SubstationProjectionResult r = At(DoubleBusbar(), new[] { "CPL" }, new[] { "A_d2" });
        Assert.Equal("A1+A2+B1+B2+D1+D2+X1+X2", r.GroupingText());
    }

    // ------------------------------------------------------------------------------------ breaker and a half

    [Fact]
    public void Breaker_and_a_half_hand_worked_states()
    {
        SubstationModel m = BreakerAndAHalf();
        // All closed: both busbars are tied through both diameters: one group, lowest node P1 (rank 0): id 1.
        Assert.Equal("P1+P2+Q1+Q2+X1+X2", Normal(m).GroupingText());

        // CB1b open: diameter 1 is cut in the middle, but diameter 2 still ties X1 to X2 through P2-Q2. One group.
        Assert.Equal("P1+P2+Q1+Q2+X1+X2", At(m, new[] { "CB1b" }, Array.Empty<string>()).GroupingText());

        // CB1b and CB2b open: nothing ties X1 to X2. X1 side: X1 P1 (CB1a) P2 (CB2a). X2 side: X2 Q1 (CB1c) Q2 (CB2c).
        // Lowest nodes P1 (rank 0) and Q1 (rank 2): ids 1 and 3.
        SubstationProjectionResult split = At(m, new[] { "CB1b", "CB2b" }, Array.Empty<string>());
        Assert.Equal("P1+P2+X1|Q1+Q2+X2", split.GroupingText());
        Assert.Equal(new[] { 1, 3 }, split.Buses.Select(b => b.BusId));

        // CB1a and CB1c open: P1 and Q1 stay joined by CB1b only: group P1+Q1. The rest: P2 Q2 X1 X2 (diameter 2
        // closes X1-P2-Q2-X2). Lowest nodes P1 (rank 0) and P2 (rank 1): ids 1 and 2.
        SubstationProjectionResult mid = At(m, new[] { "CB1a", "CB1c" }, Array.Empty<string>());
        Assert.Equal("P1+Q1|P2+Q2+X1+X2", mid.GroupingText());
        Assert.Equal(new[] { 1, 2 }, mid.Buses.Select(b => b.BusId));
    }

    // ------------------------------------------------------------------------------------------------- ring

    [Fact]
    public void Ring_hand_worked_states()
    {
        SubstationModel m = Ring();
        Assert.Equal("R1+R2+R3+R4", Normal(m).GroupingText());
        // One breaker open: a ring is still one group.
        Assert.Equal("R1+R2+R3+R4", At(m, new[] { "CBa" }, Array.Empty<string>()).GroupingText());
        // CBa (R1-R2) and CBc (R3-R4) open: R2-R3 stay joined (CBb), R4-R1 stay joined (CBd): R1+R4 | R2+R3.
        SubstationProjectionResult two = At(m, new[] { "CBa", "CBc" }, Array.Empty<string>());
        Assert.Equal("R1+R4|R2+R3", two.GroupingText());
        Assert.Equal(new[] { 1, 2 }, two.Buses.Select(b => b.BusId));   // lowest nodes R1 (rank 0) and R2 (rank 1)

        // CBa and CBb open: R2 is alone (one terminal, L2): isolated, not a bus; R1+R3+R4 joined by CBc, CBd.
        SubstationProjectionResult iso = At(m, new[] { "CBa", "CBb" }, Array.Empty<string>());
        Assert.Equal("R1+R3+R4|R2", iso.GroupingText());
        EquipmentOutcome l2 = iso.Equipment.Single(e => e.EquipmentId == "L2");
        Assert.Equal(EquipmentDisposition.Isolated, l2.Disposition);
        Assert.Equal(10, l2.Mw);
        Assert.True(iso.UnbusedNodes.ContainsKey("R2"));
    }

    // -------------------------------------------------------------------------- numbering and determinism

    [Fact]
    public void Node_rank_ids_are_stable_when_a_group_splits_and_compact_ids_are_contiguous()
    {
        SubstationModel m = TwoFeed();
        // Ranks: GA0 GB1 L1 2 L2 3 R1 4 R2 5 X1 6 X2 7 Y 8.
        // Closed: groups {GA GB L1 L2 X1 X2} (id 1) and {R1 R2 Y} (id 5).
        SubstationProjectionResult closed = Normal(m);
        Assert.Equal(new[] { 1, 5 }, closed.Buses.Select(b => b.BusId));
        // CPL open: {GA L1 X1} id 1, {GB L2 X2} id 2, {R1 R2 Y} id 5 - the SB bus keeps id 5.
        SubstationProjectionResult split = At(m, new[] { "CPL" }, Array.Empty<string>());
        Assert.Equal("GA+L1+X1|GB+L2+X2|R1+R2+Y", split.GroupingText());
        Assert.Equal(new[] { 1, 2, 5 }, split.Buses.Select(b => b.BusId));

        SubstationProjectionResult compact = SubstationProjection.Project(m, SubstationProjection.With(m.NormalStates(), new[] { "CPL" }, Array.Empty<string>()),
            new SubstationProjectionOptions(Numbering: BusNumbering.Compact));
        Assert.Equal(new[] { 1, 2, 3 }, compact.Buses.Select(b => b.BusId));
    }

    [Fact]
    public void The_projection_does_not_depend_on_declaration_order()
    {
        SubstationModel m = TwoFeed();
        SubstationModel shuffled = new()
        {
            Name = m.Name,
            Substations = m.Substations.Reverse().Select(s => s with
            {
                Nodes = s.Nodes.Reverse().ToArray(), Switches = s.Switches.Reverse().ToArray(),
            }).ToArray(),
            Lines = m.Lines.Reverse().ToArray(),
            Generators = m.Generators.Reverse().ToArray(),
            Loads = m.Loads.Reverse().ToArray(),
        };
        // The state dictionary is built in a different insertion order too.
        var reversedStates = new Dictionary<string, bool>(StringComparer.Ordinal);
        foreach (var kv in m.NormalStates().Reverse()) reversedStates[kv.Key] = kv.Value;
        reversedStates["CPL"] = true;

        SubstationProjectionResult a = SubstationProjection.Project(m, SubstationProjection.With(m.NormalStates(), new[] { "CPL" }, Array.Empty<string>()));
        SubstationProjectionResult b = SubstationProjection.Project(shuffled, reversedStates);
        Assert.Equal(a.GroupingText(), b.GroupingText());
        Assert.Equal(a.Model.Buses, b.Model.Buses);
        Assert.Equal(a.Model.Branches, b.Model.Branches);
        Assert.Equal(a.Model.Generators, b.Model.Generators);
        Assert.Equal(a.TerminalToBus, b.TerminalToBus);
        Assert.Equal(a.Equipment, b.Equipment);
    }

    // --------------------------------------------------------------------------------- terminals and accounting

    [Fact]
    public void Terminals_map_one_to_one_to_branch_ends()
    {
        SubstationProjectionResult r = Normal(TwoFeed());
        Assert.Equal(2, r.Model.Branches.Count);
        foreach (ProjectedBranch pb in r.Branches)
        {
            Branch br = r.Model.Branches[pb.BranchIndex];
            Assert.Equal(pb.EquipmentId, br.Name);
            Assert.Equal(r.TerminalToBus[pb.FromTerminal], br.From);
            Assert.Equal(r.TerminalToBus[pb.ToTerminal], br.To);
            Assert.Equal(pb.EquipmentId + ".a", pb.FromTerminal);
            Assert.Equal(pb.EquipmentId + ".b", pb.ToTerminal);
        }
        Assert.Equal(new[] { "LN1.a", "LN1.b", "LN2.a", "LN2.b" }.Order(StringComparer.Ordinal),
            r.TerminalToBus.Keys.Where(k => k.StartsWith("LN", StringComparison.Ordinal)));
    }

    [Fact]
    public void A_disconnected_load_is_recorded_with_a_disposition_and_not_counted_as_served()
    {
        SubstationModel m = DoubleBusbar();
        // D_cb open isolates D2 (its only terminal is LD): demand 80 MW is not served.
        SubstationProjectionResult r = At(m, new[] { "D_cb" }, Array.Empty<string>());
        EquipmentOutcome ld = r.Equipment.Single(e => e.EquipmentId == "LD");
        Assert.Equal(EquipmentDisposition.Isolated, ld.Disposition);
        Assert.Contains("not served", ld.Reason, StringComparison.Ordinal);
        Assert.Equal(100, r.DemandMwTotal);
        Assert.Equal(20, r.DemandMwServed);
        Assert.Equal(80, r.DemandMwNotServed);
        Assert.Equal(r.DemandMwTotal, r.DemandMwServed + r.DemandMwNotServed);
        Assert.Equal(20, r.Model.Buses.Sum(b => b.Pd));

        // Normal state: all of it is served.
        SubstationProjectionResult n = Normal(m);
        Assert.Equal(100, n.DemandMwServed);
        Assert.Equal(0, n.DemandMwNotServed);
    }

    [Fact]
    public void A_disconnected_generator_is_recorded_and_generation_adds_up()
    {
        SubstationProjectionResult r = At(DoubleBusbar(), new[] { "B_cb" }, Array.Empty<string>());
        EquipmentOutcome gb = r.Equipment.Single(e => e.EquipmentId == "GB");
        Assert.Equal(EquipmentDisposition.Isolated, gb.Disposition);
        Assert.Equal(80, r.GenerationMwTotal);
        Assert.Equal(60, r.GenerationMwConnected);
        Assert.Single(r.Model.Generators);
    }

    [Fact]
    public void A_closed_earth_switch_earths_its_group_and_its_equipment_is_recorded()
    {
        SubstationModel m = DoubleBusbar();
        var s = m.Substations[0];
        SubstationModel withEarth = new()
        {
            Name = m.Name, Generators = m.Generators, Loads = m.Loads,
            Substations = new[] { s with { Switches = s.Switches.Append(
                new NbSwitch("ES2", SwitchKind.EarthSwitch, "X2", null, NormalOpen: false)).ToArray() } },
        };
        // Coupler open, earth switch closed on X2: the B/D group is earthed.
        SubstationProjectionResult r = SubstationProjection.Project(withEarth,
            SubstationProjection.With(withEarth.NormalStates(), new[] { "CPL" }, Array.Empty<string>()));
        Assert.Equal("A1+A2+X1|B1+B2+D1+D2+X2", r.GroupingText());
        Assert.Equal(new[] { 1 }, r.Buses.Select(b => b.BusId));
        Assert.Equal(EquipmentDisposition.Earthed, r.Equipment.Single(e => e.EquipmentId == "LD").Disposition);
        Assert.Equal(EquipmentDisposition.Earthed, r.Equipment.Single(e => e.EquipmentId == "GB").Disposition);
        Assert.Equal(SwitchDisposition.EarthingClosed, r.Switches.Single(x => x.SwitchId == "ES2").Disposition);
        Assert.Equal(80, r.DemandMwNotServed);
    }

    // ------------------------------------------------------------------------------------ state contract

    [Fact]
    public void An_unspecified_switch_state_is_refused_naming_the_switch_unless_the_policy_is_declared()
    {
        SubstationModel m = DoubleBusbar();
        var partial = new Dictionary<string, bool>(StringComparer.Ordinal) { ["CPL"] = false };
        SubstationTopologyException e = Assert.Throws<SubstationTopologyException>(() => SubstationProjection.Project(m, partial));
        Assert.Contains("'A_cb'", e.Message, StringComparison.Ordinal);
        Assert.Contains("never inferred", e.Message, StringComparison.Ordinal);

        SubstationProjectionResult r = SubstationProjection.Project(m, partial,
            new SubstationProjectionOptions(UnspecifiedStatePolicy.UseNormalState));
        Assert.Equal("A1+A2+B1+B2+D1+D2+X1+X2", r.GroupingText());
        Assert.False(r.SwitchStates["CPL"]);
        Assert.True(r.SwitchStates["A_d2"]);   // taken from its declared normal state
    }

    [Fact]
    public void A_state_for_an_unknown_switch_is_refused()
    {
        SubstationModel m = DoubleBusbar();
        SubstationTopologyException e = Assert.Throws<SubstationTopologyException>(() =>
            SubstationProjection.Project(m, SubstationProjection.With(m.NormalStates(), new[] { "NOPE" }, Array.Empty<string>())));
        Assert.Contains("unknown switch 'NOPE'", e.Message, StringComparison.Ordinal);
    }

    // --------------------------------------------------------------------------------------- refusals

    [Fact]
    public void A_dangling_connectivity_node_is_refused_with_a_clear_message()
    {
        SubstationModel m = new()
        {
            Substations = new[] { Sub("S", 132, new[] { "N1", "N2", "N3", "N4" }, Sw("S1", "N1", "N2"), Sw("S2", "N2", "N3")) },
            Loads = new[] { new NbLoad("L", "N1", 1) },
            Generators = new[] { new NbGenerator("G", "N3", 1, Slack: true) },
        };
        SubstationTopologyException e = Assert.Throws<SubstationTopologyException>(() => SubstationProjection.ProjectNormal(m));
        Assert.Contains("connectivity node 'N4' is dangling: no switch and no equipment terminal", e.Message, StringComparison.Ordinal);
        Assert.Single(e.Problems);
    }

    [Fact]
    public void A_node_held_by_one_switch_and_no_terminal_is_a_dangling_stub()
    {
        SubstationModel m = new()
        {
            Substations = new[] { Sub("S", 132, new[] { "N1", "N2", "N3" }, Sw("S1", "N1", "N2"), Sw("S2", "N2", "N3")) },
            Loads = new[] { new NbLoad("L", "N1", 1) },
        };
        SubstationTopologyException e = Assert.Throws<SubstationTopologyException>(() => m.Validate());
        Assert.Contains("'N3' is dangling: it is held by one switch only", e.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_switch_joining_nodes_of_different_nominal_voltage_is_refused()
    {
        var levels = new[] { new VoltageLevel("HV", 132), new VoltageLevel("LV", 33) };
        var nodes = new[] { new ConnectivityNode("H1", "HV"), new ConnectivityNode("L1", "LV") };
        SubstationModel m = new()
        {
            Substations = new[] { new Substation("S", null, levels, Array.Empty<SubstationBay>(), nodes, new[] { Sw("BAD", "H1", "L1") }) },
            Loads = new[] { new NbLoad("A", "H1", 1), new NbLoad("B", "L1", 1) },
        };
        SubstationTopologyException e = Assert.Throws<SubstationTopologyException>(() => SubstationProjection.ProjectNormal(m));
        Assert.Contains("switch 'BAD' joins nodes of different nominal voltage: 'H1' is 132 kV and 'L1' is 33 kV", e.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Other_inconsistencies_are_refused_together_in_one_message()
    {
        SubstationModel m = new()
        {
            Substations = new[] { Sub("S", 132, new[] { "N1", "N1" }, Sw("S1", "N1", "NX")) },
            Lines = new[] { new NbLine("LN", "N1", "NY", 0, 0.1) },
        };
        SubstationTopologyException e = Assert.Throws<SubstationTopologyException>(() => m.Validate());
        Assert.Contains("'N1'", e.Message, StringComparison.Ordinal);
        Assert.Contains("unknown node 'NX'", e.Message, StringComparison.Ordinal);
        Assert.Contains("unknown connectivity node 'NY'", e.Message, StringComparison.Ordinal);
        Assert.True(e.Problems.Count >= 3);
    }

    // -------------------------------------------------------------------------------- switching scenarios

    [Fact]
    public void A_scenario_records_each_action_and_restarts_from_the_native_graph()
    {
        SubstationModel m = DoubleBusbar();
        var actions = new[]
        {
            new SwitchingAction("CPL", SwitchAction.Open, "split"),
            new SwitchingAction("D_d1", SwitchAction.Close),
            new SwitchingAction("D_d2", SwitchAction.Open, "transfer D to X1"),
            new SwitchingAction("D_d2", SwitchAction.Open, "already open"),
            new SwitchingAction("CPL", SwitchAction.Close, "reclose"),
        };
        IReadOnlyList<SwitchingStep> steps = SwitchingScenario.Run(m, m.NormalStates(), actions);
        Assert.Equal(6, steps.Count);
        Assert.Equal("A1+A2+B1+B2+D1+D2+X1+X2", steps[0].Projection.GroupingText());
        Assert.Equal("A1+A2+X1|B1+B2+D1+D2+X2", steps[1].Projection.GroupingText());
        // D_d1 closed while D_d2 is still closed: X1 and X2 are tied through D1 even with the coupler open.
        Assert.Equal("A1+A2+B1+B2+D1+D2+X1+X2", steps[2].Projection.GroupingText());
        Assert.Equal("A1+A2+D1+D2+X1|B1+B2+X2", steps[3].Projection.GroupingText());
        Assert.False(steps[4].Changed);
        Assert.Equal(steps[3].Projection.GroupingText(), steps[4].Projection.GroupingText());
        Assert.Equal("A1+A2+B1+B2+D1+D2+X1+X2", steps[5].Projection.GroupingText());

        Assert.Equal(new[] { "A1+A2+B1+B2+D1+D2+X1+X2" }, steps[1].GroupsRemoved);
        Assert.Equal(new[] { "A1+A2+X1", "B1+B2+D1+D2+X2" }, steps[1].GroupsAdded);
        Assert.Equal(actions[2], steps[3].Action);
        Assert.True(steps[3].StatesAfter["D_d1"] == false && steps[3].StatesAfter["D_d2"]);
    }

    [Fact]
    public void A_scenario_naming_an_unknown_switch_is_refused()
    {
        SubstationModel m = DoubleBusbar();
        Assert.Throws<SubstationTopologyException>(() =>
            SwitchingScenario.Run(m, m.NormalStates(), new[] { new SwitchingAction("ZZ", SwitchAction.Open) }));
    }

    // -------------------------------------------------------------------------------------- electrical oracles

    private static LineFlow Flow(PowerFlowResult r, string name) => r.LineFlows.Single(f => f.Name == name);

    [Fact]
    public void Open_coupler_gives_the_closed_form_radial_flows_and_closed_coupler_the_symmetric_ones()
    {
        SubstationModel m = TwoFeed();

        // Coupler closed: SA is one bus, LN1 and LN2 are identical and lossless between the same two buses, so each
        // carries half of the 80 MW load exactly: 40 MW.
        PowerFlowResult closed = new NewtonRaphsonPowerFlow().Solve(Normal(m).Model);
        Assert.True(closed.Converged);
        Assert.Equal(40.0, Flow(closed, "LN1").PFromMw, 6);
        Assert.Equal(40.0, Flow(closed, "LN2").PFromMw, 6);

        // Coupler open: GB (30 MW, PV) feeds only LN2 and GA feeds only LN1. A lossless line carries what it delivers, so
        // LN2 = 30 MW and LN1 = 80 - 30 = 50 MW, whatever the voltages.
        SubstationProjectionResult split = At(m, new[] { "CPL" }, Array.Empty<string>());
        PowerFlowResult open = new NewtonRaphsonPowerFlow().Solve(split.Model);
        Assert.True(open.Converged);
        Assert.Equal(50.0, Flow(open, "LN1").PFromMw, 6);
        Assert.Equal(30.0, Flow(open, "LN2").PFromMw, 6);
    }

    [Fact]
    public void Parallel_circuits_with_one_opened_leave_the_remaining_circuit_all_the_flow_closed_form_Xeq()
    {
        SubstationModel m = TwoFeed();
        // For a lossless line with the sending end at 1.0 pu and a receiving end of zero reactive demand,
        // Q = 0 at the receiver gives V2 = V1 cos d and P = V1 V2 sin d / X, so sin 2d = 2 P X and
        // V2 = sqrt((1 + sqrt(1 - (2 P X)^2)) / 2). P = 0.8 pu. Two lines: X = 0.05. One line: X = 0.1.
        static double Vy(double p, double x) => Math.Sqrt((1 + Math.Sqrt(1 - (2 * p * x) * (2 * p * x))) / 2);

        PowerFlowResult both = new NewtonRaphsonPowerFlow().Solve(Normal(m).Model);
        SubstationProjectionResult oneResult = At(m, new[] { "CBL2" }, Array.Empty<string>());
        PowerFlowResult one = new NewtonRaphsonPowerFlow().Solve(oneResult.Model);
        Assert.True(both.Converged && one.Converged);

        // LN2 is left out of the projected model (its A end is alone on its group of nodes), and said so.
        Assert.Equal(EquipmentDisposition.Isolated, oneResult.Equipment.Single(e => e.EquipmentId == "LN2").Disposition);
        Assert.Single(oneResult.Model.Branches);
        Assert.Equal(80.0, Flow(one, "LN1").PFromMw, 6);

        int y = Normal(m).NodeToBus["Y"];
        double vBoth = both.Vm[both.BusIds.ToList().IndexOf(y)];
        double vOne = one.Vm[one.BusIds.ToList().IndexOf(y)];
        Assert.Equal(Vy(0.8, 0.05), vBoth, 7);
        Assert.Equal(Vy(0.8, 0.1), vOne, 7);
        Assert.True(vOne < vBoth);
    }

    [Fact]
    public void The_projected_case_gives_bit_identical_power_flow_results_to_the_equivalent_hand_built_case()
    {
        SubstationModel m = TwoFeed();
        SubstationProjectionResult p = At(m, new[] { "CPL" }, Array.Empty<string>());

        // The same network written directly as a bus-branch case (ids 1, 2, 5 as the node-rank numbering gives).
        var hand = new GridModel
        {
            BaseMva = 100.0,
            Name = "two-feed",
            Buses = new[]
            {
                new Bus(1, BusType.Slack, 0, 0, 0, 0, 132, 1.0, Name: "GA"),
                new Bus(2, BusType.Pv, 0, 0, 0, 0, 132, 1.0, Name: "GB"),
                new Bus(5, BusType.Pq, 80, 0, 0, 0, 132, 1.0, Name: "R1"),
            },
            Generators = new[] { new Generator(1, 0, 0, 1.0, Name: "GA"), new Generator(2, 30, 0, 1.0, Name: "GB") },
            Branches = new[] { new Branch(1, 5, 0, LineX, 0, Name: "LN1"), new Branch(2, 5, 0, LineX, 0, Name: "LN2") },
        };
        PowerFlowResult a = new NewtonRaphsonPowerFlow().Solve(p.Model);
        PowerFlowResult b = new NewtonRaphsonPowerFlow().Solve(hand);
        Assert.True(a.Converged && b.Converged);
        Assert.Equal(b.Iterations, a.Iterations);
        Assert.Equal(b.BusIds, a.BusIds);
        Assert.Equal(b.Vm, a.Vm);
        Assert.Equal(b.VaDeg, a.VaDeg);
        Assert.Equal(b.PInjMw, a.PInjMw);
        Assert.Equal(b.QInjMvar, a.QInjMvar);
        Assert.Equal(b.TotalLossMw, a.TotalLossMw);
        Assert.Equal(b.LineFlows, a.LineFlows);
    }

    [Fact]
    public void The_projection_leaves_its_input_and_the_incumbent_bus_branch_model_alone()
    {
        SubstationModel m = TwoFeed();
        string before = SubstationJson.Write(m);
        _ = At(m, new[] { "CPL" }, Array.Empty<string>());
        Assert.Equal(before, SubstationJson.Write(m));
        // Two runs of the same projection are equal field for field (repeat-run identity).
        SubstationProjectionResult x = At(m, new[] { "CPL" }, Array.Empty<string>());
        SubstationProjectionResult y = At(m, new[] { "CPL" }, Array.Empty<string>());
        Assert.Equal(x.Model.Buses, y.Model.Buses);
        Assert.Equal(x.Model.Branches, y.Model.Branches);
        Assert.Equal(x.Model.Generators, y.Model.Generators);
        Assert.Equal(x.Equipment, y.Equipment);
    }
}
