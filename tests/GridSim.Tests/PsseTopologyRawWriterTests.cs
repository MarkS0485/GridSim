using GridSim.Core.IO.Psse;
using GridSim.Core.IO.Psse.Topology;
using Xunit;
using static GridSim.Tests.PsseTopologyFixture;

namespace GridSim.Tests;

/// <summary>
/// Topology-aware RAW export and read-back (separate from the incumbent <see cref="PsseRawWriter"/>). Topology
/// equivalence and preserved equipment fields are checked first; solved values are compared only afterwards.
/// </summary>
public sealed class PsseTopologyRawWriterTests
{
    [Fact]
    public void Round_trip_preserves_the_native_graph_terminals_switch_fields_and_equipment()
    {
        PsseSubstationCase source = ReadCoupler();

        PsseTopologyWriteResult written = PsseTopologyRawWriter.Write(source, requireLossless: true);
        PsseSubstationCase back = Read(written.Text);

        Assert.Empty(written.Omissions);
        Assert.Equal(1 + 4 + 3 + 6, written.SubstationRecordCount);
        Assert.Empty(PsseTopologyEquivalence.Differences(source, back));
        PsseTopologySwitch breaker = back.Topology.Switches.Single(s => s.CircuitId == "B4");
        Assert.Equal(("CCT 2 BREAKER", 2, true, true, 0.0001, 1500.0, 1600.0, 1700.0),
            (breaker.Name, breaker.Type, breaker.Closed, breaker.NormalClosed, breaker.ReactancePu, breaker.Rate1Mva, breaker.Rate2Mva, breaker.Rate3Mva));
        Assert.Equal(source.Topology.Nodes.Select(n => (n.Name, n.VmPu, n.VaDeg)), back.Topology.Nodes.Select(n => (n.Name, n.VmPu, n.VaDeg)));

        // A second pass writes the same text: the export is a fixed point.
        Assert.Equal(written.Text, PsseTopologyRawWriter.Write(back, requireLossless: true).Text);
    }

    [Fact]
    public void Written_switch_state_overlay_is_the_only_topology_difference_and_projects_identically()
    {
        PsseSubstationCase source = ReadCoupler();
        var open = PsseTopologyModelBuilder.With(PsseTopologyModelBuilder.SourceStates(source.Topology), (Coupler, false));

        PsseSubstationCase back = Read(PsseTopologyRawWriter.Write(source, open).Text);

        string difference = Assert.Single(PsseTopologyEquivalence.Differences(source, back));
        Assert.StartsWith("switch 0:", difference);
        Assert.False(back.Topology.Switches.Single(s => s.CircuitId == "C1").Closed);
        Assert.True(back.Topology.Switches.Single(s => s.CircuitId == "C1").NormalClosed);

        // Topology first: the read-back's own file state projects to the same groups and maps as the overlay did.
        PsseTopologyModelResult expected = PsseTopologyModelBuilder.Build(source, open, Options);
        PsseTopologyModelResult actual = PsseTopologyModelBuilder.Build(back, PsseTopologyModelBuilder.SourceStates(back.Topology), Options);
        Assert.Equal(expected.NodeToModelBus.OrderBy(kv => kv.Key.Node), actual.NodeToModelBus.OrderBy(kv => kv.Key.Node));
        Assert.Equal(expected.Buses.Select(b => (b.ModelBusId, b.NativeBus, b.State, b.BusType)),
            actual.Buses.Select(b => (b.ModelBusId, b.NativeBus, b.State, b.BusType)));
        Assert.Equal(expected.BranchSources, actual.BranchSources);
        Assert.Equal(expected.MachineSources, actual.MachineSources);
        Assert.Equal(expected.Terminals.Select(t => (t.ModelBusId, t.State)), actual.Terminals.Select(t => (t.ModelBusId, t.State)));
        Assert.Equal(expected.Model!.Buses, actual.Model!.Buses);
        Assert.Equal(expected.Model.Branches, actual.Model.Branches);

        // Then solved values.
        AssertBitIdentical(Solve(expected.Model), Solve(actual.Model));
    }

    [Fact]
    public void Read_back_of_the_intact_state_solves_bit_identically_to_the_source()
    {
        PsseSubstationCase source = ReadCoupler();
        PsseSubstationCase back = Read(PsseTopologyRawWriter.Write(source).Text);

        Assert.Empty(PsseTopologyEquivalence.Differences(source, back));
        PsseTopologyModelResult a = PsseTopologyModelBuilder.Build(source, PsseTopologyModelBuilder.AllClosed(source.Topology), Options);
        PsseTopologyModelResult b = PsseTopologyModelBuilder.Build(back, PsseTopologyModelBuilder.AllClosed(back.Topology), Options);
        AssertBitIdentical(Solve(a.Model!), Solve(b.Model!));
        AssertBitIdentical(Solve(source.Parsed.Model), Solve(back.Parsed.Model));
    }

    [Fact]
    public void Fields_beyond_the_documented_record_are_listed_and_refuse_a_lossless_write()
    {
        // sample-v35.raw writes six values after the switch X; the record holds three ratings after X.
        string text = File.ReadAllText(Path.Combine(Dir, "sample-v35.raw"))
            .Replace("'CB 1', 2, 1, 1, 0.0,", "'CB 1', 2, 1, 1, 0.0001,", StringComparison.Ordinal);
        PsseSubstationCase source = Read(text);
        int line = LineOf(text, "'CB 1'");

        PsseTopologyWriteResult written = PsseTopologyRawWriter.Write(source);

        Assert.Equal(new[] { line, line }, written.Omissions.Select(o => o.SourceLine).ToArray());
        Assert.Contains("Field 12", written.Omissions[0].Detail);
        Assert.Empty(PsseTopologyEquivalence.Differences(source, Read(written.Text)));
        InvalidDataException error = Assert.Throws<InvalidDataException>(() => PsseTopologyRawWriter.Write(source, requireLossless: true));
        Assert.Contains($"line {line}", error.Message);
    }

    [Fact]
    public void A_partial_switch_overlay_is_refused()
    {
        PsseSubstationCase source = ReadCoupler();
        var partial = new Dictionary<PsseSwitchKey, bool> { [Coupler] = false };

        Assert.Throws<InvalidDataException>(() => PsseTopologyRawWriter.Write(source, partial));
    }
}

/// <summary>
/// The parser's equipment-source retention flags, computed with hashed lookups, against the per-record scan they
/// replaced (the same composite keys: kind, end pair or ordered buses, identifier trimmed and compared ignoring case).
/// </summary>
public sealed class PsseEquipmentRetentionTests
{
    [Theory]
    [InlineData("sample-v35.raw")]
    [InlineData("substation-coupler-v35.raw")]
    [InlineData("case14.raw")]
    [InlineData("case118.raw")]
    [InlineData("ukgds-style-v33.raw")]
    [InlineData("ukgds-style-v30.raw")]
    [InlineData("t_psse_case3.raw")]
    public void Retention_flags_match_the_per_record_scan(string file)
    {
        // The same read options the existing tests use for these fixtures (v30 states no revision; case3 holds LCC links).
        var options = new PsseReadOptions { Revision = file.Contains("v30") ? 30 : null, AllowUnmodelled = file == "t_psse_case3.raw" };
        PsseParseResult r = PsseRawParser.ParseFile(Path.Combine(PsseTopologyFixture.Dir, file), options);
        static bool SameId(string a, string b) => string.Equals(a.Trim(), b.Trim(), StringComparison.OrdinalIgnoreCase);
        var live = r.Model.Buses.Select(b => b.Id).ToHashSet();

        foreach (PsseEquipmentSource s in r.EquipmentSources)
        {
            bool expected = s.Kind switch
            {
                "branch" or "switching device" => s.InService && live.Contains(s.Bus) && live.Contains(s.ToBus!.Value) &&
                    r.BranchKeys.Any(k => k.Kind == s.Kind && ((k.FromBus == s.Bus && k.ToBus == s.ToBus) ||
                        (k.FromBus == s.ToBus && k.ToBus == s.Bus)) && SameId(k.Circuit, s.Id)),
                "transformer" => r.BranchKeys.Any(k => k.Kind == (s.ThirdBus is null ? "transformer" : "transformer3") &&
                    k.FromBus == s.Bus && k.ToBus == s.ToBus && k.ThirdBus == (s.ThirdBus ?? 0) && SameId(k.Circuit, s.Id)),
                "machine" => live.Contains(s.Bus) && r.MachineKeys.Any(k => k.Bus == s.Bus && SameId(k.Id, s.Id)),
                _ => s.LegacyModelRetained,
            };
            Assert.True(expected == s.LegacyModelRetained, $"{file} line {s.SourceLine}: {s.Kind} {s.Bus}-{s.ToBus} '{s.Id}'");
        }
    }
}
