using GridSim.Core.IO.Psse;
using GridSim.Core.IO.Psse.Topology;
using Xunit;

namespace GridSim.Tests;

public sealed class PsseSubstationTopologyReaderTests
{
    private static string Fixture => Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "fixtures", "psse", "sample-v35.raw"));
    private static string ValidFixtureText() => File.ReadAllText(Fixture)
        .Replace("'CB 1', 2, 1, 1, 0.0,", "'CB 1', 2, 1, 1, 0.0001,", StringComparison.Ordinal);

    [Fact]
    public void Parses_native_identity_status_and_terminal_mapping()
    {
        PsseParseResult parsed = PsseRawParser.ParseWithDiagnostics(ValidFixtureText());
        PsseSubstationTopology topology = PsseSubstationTopologyReader.Read(parsed);
        Assert.Equal(35, topology.Revision);
        Assert.Equal(new[] { 1 }, topology.Substations);
        PsseTopologyNode node = Assert.Single(topology.Nodes, n => n.Number == 1);
        Assert.Equal(2, node.Bus);
        Assert.True(node.InService);
        PsseTopologySwitch sw = Assert.Single(topology.Switches);
        Assert.Equal((1, 2, "1", true), (sw.FromNode, sw.ToNode, sw.CircuitId, sw.Closed));
        PsseTopologyTerminal terminal = Assert.Single(topology.Terminals);
        Assert.Equal((2, 1, "B", 1), (terminal.Bus, terminal.Node, terminal.EquipmentType, terminal.ToBus));
        Assert.Null(terminal.ThirdBus);
    }

    [Fact]
    public void Refuses_zero_reactance_switch_in_raw_data()
    {
        PsseParseResult parsed = PsseRawParser.ParseWithDiagnostics(File.ReadAllText(Fixture));

        InvalidDataException error = Assert.Throws<InvalidDataException>(() => PsseSubstationTopologyReader.Read(parsed));

        Assert.Contains("Switch reactance must be finite and non-zero", error.Message);
    }

    [Fact]
    public void Refuses_missing_node_reference_with_source_line()
    {
        string raw = ValidFixtureText().Replace("2, 1, 'B', 1", "2, 99, 'B', 1", StringComparison.Ordinal);
        PsseParseResult parsed = PsseRawParser.ParseWithDiagnostics(raw);
        InvalidDataException error = Assert.Throws<InvalidDataException>(() => PsseSubstationTopologyReader.Read(parsed));
        Assert.Contains("line ", error.Message);
        Assert.Contains("node 99", error.Message);
    }

    [Fact]
    public void Refuses_unknown_switch_type()
    {
        string raw = ValidFixtureText().Replace("'CB 1', 2, 1", "'CB 1', 9, 1", StringComparison.Ordinal);
        PsseParseResult parsed = PsseRawParser.ParseWithDiagnostics(raw);
        InvalidDataException error = Assert.Throws<InvalidDataException>(() => PsseSubstationTopologyReader.Read(parsed));
        Assert.Contains("Switch type must be 1, 2 or 3", error.Message);
    }

    [Fact]
    public void Preserves_three_winding_terminal_remote_buses()
    {
        string raw = ValidFixtureText().Replace("2, 1, 'B', 1", "2, 1, '3', 1, 3, '1'", StringComparison.Ordinal);
        PsseSubstationTopology topology = PsseSubstationTopologyReader.Read(PsseRawParser.ParseWithDiagnostics(raw));
        PsseTopologyTerminal terminal = Assert.Single(topology.Terminals);
        Assert.Equal("3", terminal.EquipmentType);
        Assert.Equal(1, terminal.ToBus);
        Assert.Equal(3, terminal.ThirdBus);
        Assert.Equal("1", terminal.CircuitId);
    }

    [Fact]
    public void Refuses_terminal_node_assigned_to_a_different_bus()
    {
        string raw = ValidFixtureText().Replace("2, 1, 'B', 1", "1, 1, 'B', 1", StringComparison.Ordinal);
        InvalidDataException error = Assert.Throws<InvalidDataException>(() =>
            PsseSubstationTopologyReader.Read(PsseRawParser.ParseWithDiagnostics(raw)));
        Assert.Contains("references node 1, which is assigned to bus 2", error.Message);
    }

    [Fact]
    public void Refuses_terminal_local_bus_owned_by_another_substation_when_node_is_zero()
    {
        string secondSubstation = "2,'SUB TWO', 0, 0, 0.1\r\n" +
            "1,'NODE 1B', 6, 1, 1.0, 0.0\r\n" +
            "0 / END OF SUBSTATION NODE DATA\r\n" +
            "0 / END OF SUBSTATION SWITCHING DEVICE DATA\r\n" +
            "0 / END OF SUBSTATION TERMINAL DATA\r\n";
        string raw = ValidFixtureText()
            .Replace("0 / END OF SUBSTATION DATA", secondSubstation + "0 / END OF SUBSTATION DATA", StringComparison.Ordinal)
            .Replace("2, 1, 'B', 1", "6, 0, 'B', 1", StringComparison.Ordinal);

        InvalidDataException error = Assert.Throws<InvalidDataException>(() =>
            PsseSubstationTopologyReader.Read(PsseRawParser.ParseWithDiagnostics(raw)));

        Assert.Contains("Terminal's local bus 6 is not assigned to a node in substation 1", error.Message);
    }

    [Fact]
    public void Accepts_native_bus_ids_dropped_from_the_legacy_electrical_model()
    {
        string raw = ValidFixtureText()
            .Replace("1,'NODE 1', 2", "1,'NODE 1', 5", StringComparison.Ordinal)
            .Replace("2,'NODE 2', 2", "2,'NODE 2', 5", StringComparison.Ordinal)
            .Replace("2, 1, 'B', 1", "5, 1, 'B', 1", StringComparison.Ordinal);
        PsseParseResult parsed = PsseRawParser.ParseWithDiagnostics(raw);
        Assert.DoesNotContain(parsed.Model.Buses, b => b.Id == 5);

        PsseSubstationTopology topology = PsseSubstationTopologyReader.Read(parsed);

        Assert.All(topology.Nodes, n => Assert.Equal(5, n.Bus));
    }

    [Fact]
    public void Refuses_a_bus_assigned_to_nodes_in_two_substations()
    {
        string secondSubstation = "2,'SUB TWO', 0, 0, 0.1\r\n" +
            "1,'NODE 1B', 2, 1, 1.0, 0.0\r\n" +
            "0 / END OF SUBSTATION NODE DATA\r\n" +
            "0 / END OF SUBSTATION SWITCHING DEVICE DATA\r\n" +
            "0 / END OF SUBSTATION TERMINAL DATA\r\n";
        string raw = ValidFixtureText().Replace("0 / END OF SUBSTATION DATA", secondSubstation + "0 / END OF SUBSTATION DATA", StringComparison.Ordinal);
        InvalidDataException error = Assert.Throws<InvalidDataException>(() =>
            PsseSubstationTopologyReader.Read(PsseRawParser.ParseWithDiagnostics(raw)));
        Assert.Contains("Bus 2 is assigned to nodes in substations 1 and 2", error.Message);
    }

    [Fact]
    public void Refuses_substation_coordinates_outside_native_ranges()
    {
        string raw = ValidFixtureText().Replace("51.5, -0.1", "91.0, -0.1", StringComparison.Ordinal);
        InvalidDataException error = Assert.Throws<InvalidDataException>(() =>
            PsseSubstationTopologyReader.Read(PsseRawParser.ParseWithDiagnostics(raw)));
        Assert.Contains("Substation latitude must be between -90 and 90", error.Message);
    }

    [Fact]
    public void Preserves_out_of_service_node_without_refusing_its_switch_reference()
    {
        string raw = ValidFixtureText().Replace("2,'NODE 2', 2, 1, 1.0", "2,'NODE 2', 2, 0, 1.0", StringComparison.Ordinal);
        PsseSubstationTopology topology = PsseSubstationTopologyReader.Read(PsseRawParser.ParseWithDiagnostics(raw));

        PsseTopologyNode node = Assert.Single(topology.Nodes, n => n.Number == 2);
        Assert.False(node.InService);
        Assert.Single(topology.Switches);
    }

    [Fact]
    public void Refuses_duplicate_switch_identity_even_with_reversed_endpoints()
    {
        string raw = ValidFixtureText().Replace(
            "0 / END OF SUBSTATION SWITCHING DEVICE DATA",
            "2, 1, '1', 'CB duplicate', 2, 1, 1, 0.0001, 0, 0, 0\r\n0 / END OF SUBSTATION SWITCHING DEVICE DATA",
            StringComparison.Ordinal);
        InvalidDataException error = Assert.Throws<InvalidDataException>(() =>
            PsseSubstationTopologyReader.Read(PsseRawParser.ParseWithDiagnostics(raw)));
        Assert.Contains("Switch identity is duplicated", error.Message);
    }

    [Fact]
    public void Refuses_generated_three_winding_star_bus_as_native_substation_bus()
    {
        PsseParseResult parsed = PsseRawParser.ParseWithDiagnostics(ValidFixtureText());
        int starBus = Assert.Single(parsed.StarPointBuses);
        PsseSubstationBlock block = Assert.Single(parsed.Substations);
        IReadOnlyList<IReadOnlyList<string?>> nodeRows = Array.AsReadOnly(block.Nodes.Select(row =>
        {
            string?[] copy = row.ToArray();
            copy[2] = starBus.ToString(System.Globalization.CultureInfo.InvariantCulture);
            return (IReadOnlyList<string?>)Array.AsReadOnly(copy);
        }).ToArray());
        IReadOnlyList<IReadOnlyList<string?>> terminalRows = Array.AsReadOnly(block.Terminals.Select(row =>
        {
            string?[] copy = row.ToArray();
            copy[0] = starBus.ToString(System.Globalization.CultureInfo.InvariantCulture);
            return (IReadOnlyList<string?>)Array.AsReadOnly(copy);
        }).ToArray());
        PsseParseResult withStar = parsed with
        {
            Substations = Array.AsReadOnly(new[] { block with { Nodes = nodeRows, Terminals = terminalRows } })
        };

        InvalidDataException error = Assert.Throws<InvalidDataException>(() => PsseSubstationTopologyReader.Read(withStar));

        Assert.Contains("absent from the RAW bus records", error.Message);
    }

    [Fact]
    public void Switched_shunt_terminal_has_no_synthetic_native_identifier()
    {
        PsseParseResult parsed = PsseRawParser.ParseWithDiagnostics(ValidFixtureText());
        PsseSubstationBlock block = Assert.Single(parsed.Substations);
        string?[] row = block.Terminals[0].ToArray();
        row[2] = "S";
        PsseParseResult withShuntTerminal = parsed with
        {
            Substations = Array.AsReadOnly(new[] { block with
            {
                Terminals = Array.AsReadOnly(new IReadOnlyList<string?>[] { Array.AsReadOnly(row) })
            } })
        };

        PsseSubstationTopology topology = PsseSubstationTopologyReader.Read(withShuntTerminal);

        Assert.Null(Assert.Single(topology.Terminals).EquipmentId);
    }
}
