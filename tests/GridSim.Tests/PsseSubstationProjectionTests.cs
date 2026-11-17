using GridSim.Core.IO.Psse;
using GridSim.Core.IO.Psse.Topology;
using Xunit;

namespace GridSim.Tests;

public sealed class PsseSubstationProjectionTests
{
    private static string Fixture => Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "fixtures", "psse", "sample-v35.raw"));

    private static PsseSubstationTopology ReadTopology()
    {
        return PsseSubstationTopologyReader.Read(ReadParsed());
    }

    private static PsseParseResult ReadParsed(string? raw = null) =>
        PsseRawParser.ParseWithDiagnostics((raw ?? File.ReadAllText(Fixture))
            .Replace("'CB 1', 2, 1, 1, 0.0,", "'CB 1', 2, 1, 1, 0.0001,", StringComparison.Ordinal));

    [Fact]
    public void Explicit_open_state_splits_the_group_and_maps_terminal()
    {
        PsseSubstationTopology topology = ReadTopology();
        PsseTopologySwitch sw = Assert.Single(topology.Switches);
        var states = new Dictionary<PsseSwitchKey, bool> { [PsseSubstationProjection.Key(sw)] = false };

        PsseSubstationConnectivityResult result = PsseSubstationProjection.Project(topology, states);

        Assert.Equal(2, result.Groups.Count);
        Assert.Equal(2, result.NativeBusToConnectivityGroups[2].Count);
        Assert.Equal(result.NodeToConnectivityGroup[(1, 1)], result.TerminalLineToConnectivityGroup[topology.Terminals[0].SourceLine]);
        Assert.False(result.EffectiveSwitchStates[PsseSubstationProjection.Key(sw)]);
    }

    [Fact]
    public void Explicit_close_state_rejoins_a_switch_open_in_the_source()
    {
        PsseSubstationTopology topology = ReadTopology();
        PsseTopologySwitch sw = Assert.Single(topology.Switches);
        PsseSubstationTopology openSource = topology with { Switches = Array.AsReadOnly(new[] { sw with { Closed = false } }) };
        var states = new Dictionary<PsseSwitchKey, bool> { [PsseSubstationProjection.Key(sw)] = true };

        PsseSubstationConnectivityResult result = PsseSubstationProjection.Project(openSource, states);

        Assert.Single(result.Groups);
    }

    [Fact]
    public void Requires_state_for_every_switch()
    {
        PsseSubstationTopology topology = ReadTopology();
        InvalidDataException error = Assert.Throws<InvalidDataException>(() =>
            PsseSubstationProjection.Project(topology, new Dictionary<PsseSwitchKey, bool>()));
        Assert.Contains("An explicit state is required", error.Message);
    }

    [Fact]
    public void Does_not_connect_through_an_out_of_service_node()
    {
        PsseSubstationTopology topology = ReadTopology();
        PsseTopologySwitch sw = Assert.Single(topology.Switches);
        PsseSubstationTopology inactive = topology with
        {
            Nodes = Array.AsReadOnly(topology.Nodes.Select(n => n.Number == 2 ? n with { InService = false } : n).ToArray())
        };
        var key = PsseSubstationProjection.Key(sw);
        var states = new Dictionary<PsseSwitchKey, bool> { [key] = true };

        PsseSubstationConnectivityResult result = PsseSubstationProjection.Project(inactive, states);

        Assert.Equal(2, result.Groups.Count);
        Assert.True(result.RequestedSwitchStates[key]);
        Assert.False(result.EffectiveSwitchStates[key]);
    }

    [Fact]
    public void Electrical_projection_contracts_an_ideal_switch_and_allocates_stable_non_native_bus_ids()
    {
        PsseParseResult parsed = ReadParsed();
        PsseSubstationTopology topology = PsseSubstationTopologyReader.Read(parsed);
        PsseTopologySwitch sw = Assert.Single(topology.Switches) with { ReactancePu = 0 };
        topology = topology with { Switches = Array.AsReadOnly(new[] { sw }) };
        var states = new Dictionary<PsseSwitchKey, bool> { [PsseSubstationProjection.Key(sw)] = true };

        PsseSubstationElectricalProjectionResult first = PsseSubstationElectricalProjection.Project(parsed, topology, states);
        PsseSubstationElectricalProjectionResult second = PsseSubstationElectricalProjection.Project(parsed, topology, states);

        Assert.Single(first.Buses);
        Assert.Equal(first.Buses.Select(bus => bus.BusId), second.Buses.Select(bus => bus.BusId));
        Assert.All(first.Buses, bus => Assert.DoesNotContain(parsed.RawBusIds, id => id == bus.BusId));
        Assert.Equal(PsseSwitchProjectionDisposition.ContractedIdealConnection, Assert.Single(first.Switches).Disposition);
        Assert.False(first.HasUnsupportedSwitchDisposition);
        Assert.True(first.TerminalMappingsComplete);
    }

    [Fact]
    public void Electrical_projection_keeps_a_closed_finite_switch_as_equipment()
    {
        string raw = File.ReadAllText(Fixture).Replace("'CB 1', 2, 1, 1, 0.0,", "'CB 1', 2, 1, 1, 0.0001,", StringComparison.Ordinal);
        PsseParseResult parsed = ReadParsed(raw);
        PsseSubstationTopology topology = PsseSubstationTopologyReader.Read(parsed);
        PsseTopologySwitch sw = Assert.Single(topology.Switches);
        var states = new Dictionary<PsseSwitchKey, bool> { [PsseSubstationProjection.Key(sw)] = true };

        PsseSubstationElectricalProjectionResult result = PsseSubstationElectricalProjection.Project(parsed, topology, states);

        Assert.Equal(2, result.Buses.Count);
        PsseProjectedSwitch projectedSwitch = Assert.Single(result.Switches);
        Assert.Equal(PsseSwitchProjectionDisposition.RetainedAsBranchEquipment, projectedSwitch.Disposition);
        Assert.Equal(0.0001, projectedSwitch.ReactancePu);
        Assert.False(result.HasUnsupportedSwitchDisposition);
        Assert.True(result.TerminalMappingsComplete);
    }
}
