using GridSim.Core.IO.Psse.Topology;
using Xunit;
using static GridSim.Tests.PsseTopologyFixture;

namespace GridSim.Tests;

/// <summary>IO-21: a bus cut by an open switch is split for the placement check even when terminal rows exist in one group only.</summary>
public sealed class PsseTopologyCutBusTests
{
    private static string GroupALess() => Remove(Remove(Remove(CouplerText, "   200, 1, 'L', '1 '"), "   200, 2, 'L', '3 '"), "   200, 2, 'F', '1 '");

    [Fact]
    public void A_bus_cut_by_an_open_switch_with_terminal_rows_on_one_side_only_refuses_the_unplaced_ends()
    {
        string text = GroupALess();
        text = Remove(Remove(text, "   200, 1, 'B', 100, '1 '"), "   200, 4, 'B', 100, '2 '");
        PsseSubstationCase c = Read(text);

        PsseTopologyModelResult r = PsseTopologyModelBuilder.Build(c, PsseTopologyModelBuilder.SourceStates(c.Topology), Options);

        Assert.False(r.Built);
        Assert.Contains(r.Refusals, x => x.Kind == PsseTopologyRefusalKind.UnplacedEquipmentEnd);
    }

    [Fact]
    public void The_same_cut_with_the_occupied_node_out_of_service_is_refused_rather_than_dropping_the_bus()
    {
        string text = Remove(Remove(GroupALess(), "   200, 1, 'B', 100, '1 '"), "   200, 4, 'B', 100, '2 '")
            .Replace("   3,'LOAD BAY 2', 200, 1", "   3,'LOAD BAY 2', 200, 0", StringComparison.Ordinal);
        PsseSubstationCase c = Read(text);

        PsseTopologyModelResult r = PsseTopologyModelBuilder.Build(c, PsseTopologyModelBuilder.SourceStates(c.Topology), Options);

        Assert.False(r.Built);
        Assert.Contains(r.Refusals, x => x.Kind == PsseTopologyRefusalKind.UnplacedEquipmentEnd);
    }
}
