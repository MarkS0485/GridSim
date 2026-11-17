using GridSim.Core.IO.Psse;
using GridSim.Core.IO.Psse.Topology;
using Xunit;
using static GridSim.Tests.PsseTopologyFixture;

namespace GridSim.Tests;

/// <summary>
/// P2.4 two-section coupler fixture (fixtures/psse/substation-coupler-v35.raw), topology only: connectivity groups,
/// stable terminal identity, finite-switch preservation, old/new group mapping when the coupler opens and recloses, and
/// fail-closed handling of a conflicting and an unsupported record. No AC quantity is asserted here.
/// </summary>
public sealed class PsseSubstationCouplerFixtureTests
{
    private static IReadOnlyList<int[]> Groups(PsseSubstationConnectivityResult r) =>
        r.Groups.Select(g => g.Nodes.Select(n => n.Number).ToArray()).ToArray();

    [Fact]
    public void Fixture_is_valid_v35_with_finite_switches_explicit_terminals_and_distinct_parallel_circuits()
    {
        PsseSubstationCase c = ReadCoupler();

        Assert.Equal(35, c.Parsed.Revision);
        Assert.Equal(new[] { 100, 200 }, c.Parsed.RawBusIds);
        Assert.Equal(4, c.Topology.Nodes.Count);
        Assert.Equal(3, c.Topology.Switches.Count);
        // The source X of every switch is retained in the native graph: 0.0001 pu, finite and non-zero as RAW requires.
        Assert.All(c.Topology.Switches, sw => Assert.Equal(0.0001, sw.ReactancePu));
        Assert.All(c.Topology.Switches, sw => Assert.False(sw.IsIdealConnection));
        PsseTopologySwitch breaker = c.Topology.Switches.Single(s => s.CircuitId == "B4");
        Assert.Equal((1500.0, 1600.0, 1700.0), (breaker.Rate1Mva, breaker.Rate2Mva, breaker.Rate3Mva));
        Assert.Equal(2000.0, c.Topology.Switches.Single(s => s.CircuitId == "C1").Rate1Mva);
        Assert.False(c.Topology.Switches.Single(s => s.CircuitId == "D3").Closed);

        Assert.Equal(6, c.Topology.Terminals.Count);
        Assert.All(c.TerminalBindings, b => Assert.NotEqual(PsseTerminalBindingStatus.UnsupportedEquipmentType, b.Status));
        PsseTerminalBinding[] branches = c.TerminalBindings.Where(b => b.Terminal.EquipmentType == "B").ToArray();
        Assert.Equal(2, branches.Length);
        Assert.All(branches, b => Assert.Equal(PsseTerminalBindingStatus.BoundToBranch, b.Status));
        Assert.Equal(new[] { "1", "2" }, branches.Select(b => b.BranchKey!.Circuit).ToArray());
        Assert.Equal(new int?[] { 0, 1 }, branches.Select(b => b.BranchIndex).ToArray());
        Assert.All(c.TerminalSetAudits, a => Assert.Equal(PsseTerminalSetStatus.Complete, a.Status));
    }

    [Fact]
    public void Connectivity_groups_follow_each_explicit_switch_state()
    {
        PsseSubstationTopology t = ReadCoupler().Topology;
        var file = PsseTopologyModelBuilder.SourceStates(t);
        var intact = PsseTopologyModelBuilder.AllClosed(t);
        var couplerOpen = PsseTopologyModelBuilder.With(intact, (Coupler, false));
        var breakerOpen = PsseTopologyModelBuilder.With(intact, (Circuit2Breaker, false));

        Assert.Equal(new[] { new[] { 1, 2, 3, 4 } }, Groups(PsseSubstationProjection.Project(t, intact)));
        Assert.Equal(new[] { new[] { 1, 2, 4 }, new[] { 3 } }, Groups(PsseSubstationProjection.Project(t, file)));
        Assert.Equal(new[] { new[] { 1 }, new[] { 2, 3, 4 } }, Groups(PsseSubstationProjection.Project(t, couplerOpen)));
        Assert.Equal(new[] { new[] { 1, 2, 3 }, new[] { 4 } }, Groups(PsseSubstationProjection.Project(t, breakerOpen)));
    }

    [Fact]
    public void The_incumbent_electrical_projection_still_keeps_closed_finite_switches_as_equipment()
    {
        PsseSubstationCase c = ReadCoupler();
        var intact = PsseTopologyModelBuilder.AllClosed(c.Topology);

        PsseSubstationElectricalProjectionResult r = PsseSubstationElectricalProjection.Project(c.Parsed, c.Topology, intact);

        Assert.Equal(4, r.Buses.Count);
        Assert.All(r.Switches, s => Assert.Equal(PsseSwitchProjectionDisposition.RetainedAsBranchEquipment, s.Disposition));
        Assert.All(r.Switches, s => Assert.Equal(0.0001, s.ReactancePu));
    }

    [Fact]
    public void Terminal_identity_is_stable_across_fresh_reads_and_switch_states()
    {
        PsseSubstationCase a = ReadCoupler(), b = ReadCoupler();
        Assert.Equal(a.SourceSha256, b.SourceSha256);
        Assert.Equal(a.TerminalBindings.Select(x => (x.Terminal, x.Status, x.BranchKey, x.LoadKey, x.ShuntKey)),
            b.TerminalBindings.Select(x => (x.Terminal, x.Status, x.BranchKey, x.LoadKey, x.ShuntKey)));

        var intact = PsseTopologyModelBuilder.AllClosed(a.Topology);
        PsseTopologyModelResult closed = PsseTopologyModelBuilder.Build(a, intact, Options);
        PsseTopologyModelResult open = PsseTopologyModelBuilder.Build(b, PsseTopologyModelBuilder.With(intact, (Coupler, false)), Options);
        // The same native terminal rows are reported in both states, in the same order, whatever their bus.
        Assert.Equal(closed.Terminals.Select(t => t.Terminal), open.Terminals.Select(t => t.Terminal));
    }

    [Fact]
    public void Opening_and_reclosing_the_coupler_reports_old_and_new_groups_and_restores_the_same_mapping()
    {
        PsseSubstationCase c = ReadCoupler();
        var intact = PsseTopologyModelBuilder.AllClosed(c.Topology);
        var open = PsseTopologyModelBuilder.With(intact, (Coupler, false));

        PsseTopologyModelResult before = PsseTopologyModelBuilder.Build(c, intact, Options);
        PsseTopologyModelResult opened = PsseTopologyModelBuilder.Build(c, open, Options);
        PsseTopologyModelResult reclosed = PsseTopologyModelBuilder.Build(ReadCoupler(), PsseTopologyModelBuilder.With(open, (Coupler, true)), Options);

        PsseTopologyTransitionResult split = PsseTopologyTransition.Compare(before, opened);
        Assert.Equal(new[] { Coupler }, split.ChangedSwitches);
        PsseBusTransition bus200 = Assert.Single(split.Buses);
        Assert.Equal(200, bus200.BeforeBusId);
        Assert.Equal(new[] { 200, 201 }, bus200.AfterBusIds);
        Assert.Equal(PsseBusTransitionKind.Split, bus200.Kind);
        // Section A (node 1) keeps the native id; section B (nodes 2, 3, 4) is numbered above every native bus.
        Assert.Equal(200, opened.NodeToModelBus[new PsseNodeKey(1, 1)]);
        Assert.All(new[] { 2, 3, 4 }, n => Assert.Equal(201, opened.NodeToModelBus[new PsseNodeKey(1, n)]));
        Assert.Equal(new[] { 200, 201, 200, 201, 201, 201 }, split.Terminals.Select(t => t.AfterBusId!.Value).ToArray());
        Assert.All(split.Terminals, t => Assert.Equal(200, t.BeforeBusId));

        PsseTopologyTransitionResult merge = PsseTopologyTransition.Compare(opened, reclosed);
        Assert.Equal(new[] { 200, 201 }, merge.Buses.Select(b => b.BeforeBusId!.Value).ToArray());
        Assert.All(merge.Buses, b => Assert.Equal(new[] { 200 }, b.AfterBusIds));
        Assert.All(merge.Buses, b => Assert.Equal(PsseBusTransitionKind.Merged, b.Kind));

        Assert.Equal(before.NodeToModelBus.OrderBy(kv => kv.Key.Node), reclosed.NodeToModelBus.OrderBy(kv => kv.Key.Node));
        Assert.Equal(before.Buses, reclosed.Buses, new BusComparer());
        Assert.Equal(before.Terminals.Select(t => (t.Terminal, t.ModelBusId, t.State)),
            reclosed.Terminals.Select(t => (t.Terminal, t.ModelBusId, t.State)));
        Assert.Equal(before.ProjectedRawSha256, reclosed.ProjectedRawSha256);
        Assert.All(PsseTopologyTransition.Compare(before, reclosed).Buses, b => Assert.Equal(PsseBusTransitionKind.Unchanged, b.Kind));
    }

    [Fact]
    public void A_disconnected_terminal_is_reported_and_its_equipment_is_absent_from_the_model()
    {
        PsseSubstationCase c = ReadCoupler();

        PsseTopologyModelResult r = PsseTopologyModelBuilder.Build(c, PsseTopologyModelBuilder.SourceStates(c.Topology), Options);

        Assert.True(r.Built);
        PsseTerminalPlacement load2 = r.Terminals.Single(t => t.Terminal.EquipmentType == "L" && t.Terminal.EquipmentId == "2");
        Assert.Equal(PsseTerminalModelState.SectionDeEnergised, load2.State);
        Assert.Null(load2.ModelBusId);
        Assert.Null(r.NodeToModelBus[new PsseNodeKey(1, 3)]);
        PsseTopologyModelBus dead = r.Buses.Single(b => b.State == PsseSectionState.DeEnergised);
        Assert.Equal((201, 200, 4), (dead.ModelBusId, dead.NativeBus, dead.BusType));
        Assert.All(r.Terminals.Where(t => t != load2), t => Assert.Equal(PsseTerminalModelState.Connected, t.State));
        // The projected parse drops the dead section through the incumbent IDE 4 rule, so the load is not served.
        PsseLoadSource projectedLoad2 = r.ProjectedParse!.LoadSources.Single(l => l.Key.Id == "2");
        Assert.False(projectedLoad2.BusRetained);
        Assert.DoesNotContain(r.Model!.Buses, b => b.Id == 201);
        Assert.Equal(70.0, r.Model.Buses.Sum(b => b.Pd));
    }

    [Fact]
    public void A_conflicting_terminal_reference_fails_with_source_context()
    {
        string text = CouplerText.Replace("   200, 2, 'L', '3 '", "   200, 9, 'L', '3 '", StringComparison.Ordinal);
        int line = LineOf(text, "   200, 9, 'L', '3 '");

        InvalidDataException error = Assert.Throws<InvalidDataException>(() => Read(text));

        Assert.Contains($"substation 1, line {line}: Terminal references node 9", error.Message);
    }

    [Fact]
    public void An_unsupported_terminal_record_is_retained_but_refuses_the_model_with_its_line()
    {
        string text = InsertAfter(CouplerText, "   200, 2, 'F', '1 '", "   200, 2, 'N', 'X1'");
        int line = LineOf(text, "'N', 'X1'");
        PsseSubstationCase c = Read(text);

        PsseTerminalBinding binding = c.TerminalBindings.Single(b => b.Terminal.SourceLine == line);
        Assert.Equal(PsseTerminalBindingStatus.UnsupportedEquipmentType, binding.Status);
        PsseTopologyModelResult r = PsseTopologyModelBuilder.Build(c, PsseTopologyModelBuilder.AllClosed(c.Topology), Options);

        Assert.False(r.Built);
        Assert.Null(r.ProjectedRawText);
        PsseTopologyRefusal refusal = Assert.Single(r.Refusals);
        Assert.Equal((PsseTopologyRefusalKind.UnsupportedTerminal, line), (refusal.Kind, refusal.SourceLine));
    }

    [Fact]
    public void A_switch_between_two_buses_is_accepted_open_refused_closed_and_never_merged_by_the_builder()
    {
        string withBus = InsertAfter(CouplerText, "  200,'SUB 132     '",
            "  210,'SUB 132 B   ', 132.0000,4,   1,   1,   1,1.00000,  -2.0000,1.10000,0.90000,1.10000,0.90000");
        withBus = InsertAfter(withBus, "   4,'CCT 2 BAY', 200, 1", "   5,'BUS 210 NODE', 210, 1");
        string open = InsertAfter(withBus, "   2, 4, 'B4'", "   2, 5, 'K5', 'BUS TIE', 2, 0, 0, 0.0001");
        string closed = InsertAfter(withBus, "   2, 4, 'B4'", "   2, 5, 'K5', 'BUS TIE', 2, 1, 0, 0.0001");

        InvalidDataException error = Assert.Throws<InvalidDataException>(() => Read(closed));
        Assert.Contains($"line {LineOf(closed, "'K5'")}: A switch closed in the file must connect nodes assigned to the same bus-branch bus", error.Message);

        PsseSubstationCase c = Read(open);
        var tie = new PsseSwitchKey(1, 2, 5, "K5");
        Assert.True(PsseTopologyModelBuilder.Build(c, PsseTopologyModelBuilder.SourceStates(c.Topology), Options).Built);
        PsseTopologyModelResult merged = PsseTopologyModelBuilder.Build(c,
            PsseTopologyModelBuilder.With(PsseTopologyModelBuilder.SourceStates(c.Topology), (tie, true)), Options);
        Assert.False(merged.Built);
        Assert.Contains(merged.Refusals, r => r.Kind == PsseTopologyRefusalKind.BusMerge);
    }

    private sealed class BusComparer : IEqualityComparer<PsseTopologyModelBus>
    {
        public bool Equals(PsseTopologyModelBus? x, PsseTopologyModelBus? y) =>
            x is not null && y is not null && x.ModelBusId == y.ModelBusId && x.NativeBus == y.NativeBus &&
            x.State == y.State && x.BusType == y.BusType && x.Nodes.SequenceEqual(y.Nodes);
        public int GetHashCode(PsseTopologyModelBus obj) => obj.ModelBusId;
    }
}
