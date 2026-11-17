using GridSim.Core.IO.Psse;
using Xunit;

namespace GridSim.Tests;

public sealed class PsseSubstationCaptureTests
{
    [Fact]
    public void V35_substation_records_and_source_lines_are_preserved_separately_from_the_model()
    {
        string path = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "fixtures", "psse", "sample-v35.raw"));
        PsseParseResult parsed = PsseRawParser.ParseFile(path);
        PsseSubstationBlock station = Assert.Single(parsed.Substations);
        Assert.Equal("1", station.Header[0]);
        Assert.Equal("SUB ONE", station.Header[1]);
        Assert.Equal(new[] { "1", "NODE 1", "2", "1", "1.0", "0.0" }, station.Nodes[0]);
        Assert.Equal("CB 1", station.SwitchingDevices[0][3]);
        Assert.Equal(new[] { "2", "1", "B", "1" }, station.Terminals[0]);
        Assert.True(station.HeaderLine < station.NodeLines[0]);
        Assert.True(station.NodeLines[0] < station.SwitchingDeviceLines[0]);
        Assert.True(station.SwitchingDeviceLines[0] < station.TerminalLines[0]);
    }

    [Fact]
    public void Captured_records_are_read_only_at_every_collection_level()
    {
        string path = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "fixtures", "psse", "sample-v35.raw"));
        PsseParseResult result = PsseRawParser.ParseFile(path);
        PsseSubstationBlock station = Assert.Single(result.Substations);
        Assert.Throws<NotSupportedException>(() => ((IList<PsseSubstationBlock>)result.Substations)[0] = station);
        Assert.Throws<NotSupportedException>(() => ((IList<string?>)station.Header)[0] = "changed");
        Assert.Throws<NotSupportedException>(() => ((IList<IReadOnlyList<string?>>)station.Nodes)[0] = Array.Empty<string?>());
        Assert.Throws<NotSupportedException>(() => ((IList<int>)station.NodeLines)[0] = 0);
    }

    [Fact]
    public void V35_without_substation_records_returns_an_empty_graph()
    {
        string fixture = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "fixtures", "psse", "sample-v35.raw"));
        string raw = File.ReadAllText(fixture);
        int start = raw.IndexOf("@! IS, 'NAME'", StringComparison.Ordinal);
        int end = raw.IndexOf("0 / END OF SUBSTATION DATA", start, StringComparison.Ordinal);
        string withoutSubstations = raw.Remove(start, end - start);
        PsseParseResult parsed = PsseRawParser.ParseWithDiagnostics(withoutSubstations);
        Assert.Empty(parsed.Substations);
    }

    [Fact]
    public void Equipment_source_inventory_preserves_native_keys_status_fields_and_source_lines()
    {
        string path = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "fixtures", "psse", "sample-v35.raw"));
        PsseParseResult parsed = PsseRawParser.ParseFile(path);

        PsseEquipmentSource fixedShunt = Assert.Single(parsed.EquipmentSources,
            source => source.EquipmentType == "F" && source.Bus == 2);
        PsseEquipmentSource switchedShunt = Assert.Single(parsed.EquipmentSources,
            source => source.EquipmentType == "S" && source.Bus == 3);
        PsseEquipmentSource branch = Assert.Single(parsed.EquipmentSources,
            source => source.EquipmentType == "B" && source.Bus == 2 && source.ToBus == 6 && source.Id == "1");
        PsseEquipmentSource transformer = Assert.Single(parsed.EquipmentSources,
            source => source.EquipmentType == "2" && source.Bus == 1 && source.ToBus == 2);

        Assert.Equal("1", fixedShunt.Id);
        Assert.True(fixedShunt.InService);
        Assert.True(fixedShunt.LegacyModelRetained);
        Assert.True(fixedShunt.SourceLine > 0);
        Assert.NotEmpty(fixedShunt.Fields);
        Assert.True(switchedShunt.InService);
        Assert.Equal("1", branch.Id);
        Assert.True(branch.SourceLine > 0);
        Assert.True(transformer.SourceRows.Count >= 4);
        Assert.Equal(transformer.SourceLine, transformer.SourceRows[0].SourceLine);
        Assert.All(transformer.SourceRows, row => Assert.NotEmpty(row.Fields));
    }
}




