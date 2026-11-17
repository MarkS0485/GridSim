using GridSim.Core.IO.Psse;
using GridSim.Core.IO.Psse.Topology;
using Xunit;

namespace GridSim.Tests;

public sealed class PsseTerminalIdentityBinderTests
{
    private static string Fixture => Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "fixtures", "psse", "sample-v35.raw"));

    private static PsseParseResult Parse() => PsseRawParser.ParseWithDiagnostics(File.ReadAllText(Fixture)
        .Replace("'CB 1', 2, 1, 1, 0.0,", "'CB 1', 2, 1, 1, 0.0001,", StringComparison.Ordinal));

    private static PsseSubstationTopology Topology(PsseParseResult parsed, PsseTopologyTerminal terminal)
    {
        PsseSubstationTopology basis = PsseSubstationTopologyReader.Read(parsed);
        return basis with { Terminals = Array.AsReadOnly(new[] { terminal }) };
    }

    [Fact]
    public void Binds_reversed_branch_endpoints_using_circuit_id()
    {
        PsseParseResult parsed = Parse();
        var terminal = new PsseTopologyTerminal(1, 6, 0, "B", "1", 2, null, "1", 900);

        PsseTerminalBinding binding = Assert.Single(PsseTerminalIdentityBinder.Bind(Topology(parsed, terminal), parsed));

        Assert.Equal(PsseTerminalBindingStatus.BoundToBranch, binding.Status);
        Assert.NotNull(binding.BranchIndex);
        Assert.Equal(new PsseBranchKey("branch", 2, 6, 0, "1", 0), parsed.BranchKeys[binding.BranchIndex!.Value]);
    }

    [Fact]
    public void Reports_a_matching_skipped_branch_as_omitted_from_the_legacy_model()
    {
        PsseParseResult parsed = Parse();
        var terminal = new PsseTopologyTerminal(1, 2, 0, "B", "1", 5, null, "1", 901);

        PsseTerminalBinding binding = Assert.Single(PsseTerminalIdentityBinder.Bind(Topology(parsed, terminal), parsed));

        Assert.Equal(PsseTerminalBindingStatus.LegacyModelOmitted, binding.Status);
        Assert.Contains("legacy electrical model omitted", binding.Detail);
    }

    [Fact]
    public void Binds_machines_by_bus_and_machine_id()
    {
        PsseParseResult parsed = Parse();
        PsseMachineKey key = parsed.MachineKeys[0];
        var terminal = new PsseTopologyTerminal(1, key.Bus, 0, "M", "1", null, null, null, 902);

        PsseTerminalBinding binding = Assert.Single(PsseTerminalIdentityBinder.Bind(Topology(parsed, terminal), parsed));

        Assert.Equal(PsseTerminalBindingStatus.BoundToMachine, binding.Status);
        Assert.Equal(0, binding.GeneratorIndex);
    }

    [Fact]
    public void Reports_an_out_of_service_system_switch_by_its_native_key()
    {
        string raw = File.ReadAllText(Fixture)
            .Replace("'CB 1', 2, 1, 1, 0.0,", "'CB 1', 2, 1, 1, 0.0001,", StringComparison.Ordinal)
            .Replace("  1, 1, 1, 1, 'BUS COUPLER'", "  0, 1, 1, 1, 'BUS COUPLER'", StringComparison.Ordinal);
        PsseParseResult parsed = PsseRawParser.ParseWithDiagnostics(raw);
        var terminal = new PsseTopologyTerminal(1, 2, 0, "B", "S1", 6, null, "S1", 903);

        PsseTerminalBinding binding = Assert.Single(PsseTerminalIdentityBinder.Bind(Topology(parsed, terminal), parsed));

        Assert.Equal(PsseTerminalBindingStatus.LegacyModelOmitted, binding.Status);
        Assert.Contains("switching device", binding.Detail);
    }

    [Fact]
    public void Binds_load_terminal_by_bus_and_load_id_without_claiming_a_separate_solved_load()
    {
        PsseParseResult parsed = Parse();
        var terminal = new PsseTopologyTerminal(1, 2, 1, "L", "1", null, null, null, 904);

        PsseTerminalBinding binding = Assert.Single(PsseTerminalIdentityBinder.Bind(Topology(parsed, terminal), parsed));

        Assert.Equal(PsseTerminalBindingStatus.BoundToLoadRecord, binding.Status);
        Assert.NotNull(binding.LoadSourceIndex);
        Assert.Equal(new PsseLoadKey(2, "1"), binding.LoadKey);
        Assert.Contains("aggregated into the legacy bus model", binding.Detail);
    }

    [Fact]
    public void Reports_out_of_service_load_terminal_as_omitted()
    {
        PsseParseResult parsed = Parse();
        var terminal = new PsseTopologyTerminal(1, 3, 0, "L", "2", null, null, null, 905);

        PsseTerminalBinding binding = Assert.Single(PsseTerminalIdentityBinder.Bind(Topology(parsed, terminal), parsed));

        Assert.Equal(PsseTerminalBindingStatus.LegacyModelOmitted, binding.Status);
        Assert.NotNull(binding.LoadSourceIndex);
        Assert.False(parsed.LoadSources[binding.LoadSourceIndex!.Value].InService);
    }

    [Fact]
    public void Binds_fixed_shunt_by_bus_and_identifier_and_switched_shunt_by_unique_bus()
    {
        PsseParseResult parsed = Parse();
        Assert.Equal(3, parsed.ShuntSources.Count);
        Assert.All(parsed.ShuntSources, source => Assert.True(source.SourceLine > 0));
        Assert.All(parsed.ShuntSources, source => Assert.NotEmpty(source.Fields));

        var fixedTerminal = new PsseTopologyTerminal(1, 2, 0, "F", "1", null, null, null, 911);
        PsseTerminalBinding fixedBinding = Assert.Single(PsseTerminalIdentityBinder.Bind(Topology(parsed, fixedTerminal), parsed));
        Assert.Equal(PsseTerminalBindingStatus.BoundToShuntRecord, fixedBinding.Status);
        Assert.Equal(new PsseShuntKey(2, "1"), fixedBinding.ShuntKey);
        Assert.Equal(PsseShuntKind.Fixed, parsed.ShuntSources[fixedBinding.ShuntSourceIndex!.Value].Kind);

        var switchedTerminal = new PsseTopologyTerminal(1, 3, 0, "S", null, null, null, null, 912);
        PsseTerminalBinding switchedBinding = Assert.Single(PsseTerminalIdentityBinder.Bind(Topology(parsed, switchedTerminal), parsed));
        Assert.Equal(PsseTerminalBindingStatus.BoundToShuntRecord, switchedBinding.Status);
        Assert.Equal(new PsseShuntKey(3, "1"), switchedBinding.ShuntKey);
        Assert.Equal(PsseShuntKind.Switched, parsed.ShuntSources[switchedBinding.ShuntSourceIndex!.Value].Kind);
    }

    [Fact]
    public void Reports_out_of_service_fixed_shunt_terminal_as_omitted()
    {
        PsseParseResult parsed = Parse();
        var terminal = new PsseTopologyTerminal(1, 3, 0, "F", "1", null, null, null, 913);

        PsseTerminalBinding binding = Assert.Single(PsseTerminalIdentityBinder.Bind(Topology(parsed, terminal), parsed));

        Assert.Equal(PsseTerminalBindingStatus.LegacyModelOmitted, binding.Status);
        Assert.False(parsed.ShuntSources[binding.ShuntSourceIndex!.Value].InService);
    }

    [Fact]
    public void Binds_three_winding_terminal_to_the_matching_legacy_winding_key()
    {
        PsseParseResult parsed = Parse();
        PsseBranchKey key = Assert.Single(parsed.BranchKeys, k => k.Kind == "transformer3" && k.Winding == 1);
        var terminal = new PsseTopologyTerminal(1, key.FromBus, 0, "3", key.Circuit, key.ToBus, key.ThirdBus, key.Circuit, 906);

        PsseTerminalBinding binding = Assert.Single(PsseTerminalIdentityBinder.Bind(Topology(parsed, terminal), parsed));

        Assert.Equal(PsseTerminalBindingStatus.BoundToBranch, binding.Status);
        Assert.Equal(key, binding.BranchKey);
        Assert.Equal(key, parsed.BranchKeys[binding.BranchIndex!.Value]);
    }

    [Fact]
    public void Binds_three_winding_terminal_by_local_bus_even_when_terminal_order_is_rotated()
    {
        PsseParseResult parsed = Parse();
        PsseBranchKey key = Assert.Single(parsed.BranchKeys, k => k.Kind == "transformer3" && k.Winding == 2);
        var terminal = new PsseTopologyTerminal(1, key.ToBus, 0, "3", key.Circuit, key.FromBus, key.ThirdBus, key.Circuit, 907);

        PsseTerminalBinding binding = Assert.Single(PsseTerminalIdentityBinder.Bind(Topology(parsed, terminal), parsed));

        Assert.Equal(PsseTerminalBindingStatus.BoundToBranch, binding.Status);
        Assert.Equal(key, binding.BranchKey);
    }

    [Fact]
    public void Refuses_wrong_circuit_id_instead_of_matching_parallel_branch_by_endpoints()
    {
        PsseParseResult parsed = Parse();
        var terminal = new PsseTopologyTerminal(1, 6, 0, "B", "WRONG", 2, null, "WRONG", 908);

        PsseTerminalBinding binding = Assert.Single(PsseTerminalIdentityBinder.Bind(Topology(parsed, terminal), parsed));

        Assert.Equal(PsseTerminalBindingStatus.MissingSourceRecord, binding.Status);
        Assert.Null(binding.BranchIndex);
    }

    [Fact]
    public void Marks_duplicate_terminal_rows_for_the_same_equipment_end_ambiguous()
    {
        PsseParseResult parsed = Parse();
        var terminal = new PsseTopologyTerminal(1, 6, 0, "B", "1", 2, null, "1", 909);
        PsseSubstationTopology topology = Topology(parsed, terminal) with
        {
            Terminals = Array.AsReadOnly(new[] { terminal, terminal with { SourceLine = 910 } })
        };

        IReadOnlyList<PsseTerminalBinding> bindings = PsseTerminalIdentityBinder.Bind(topology, parsed);

        Assert.Equal(2, bindings.Count);
        Assert.All(bindings, binding => Assert.Equal(PsseTerminalBindingStatus.AmbiguousSourceRecord, binding.Status));
        Assert.All(bindings, binding => Assert.Contains("Duplicate native terminal rows", binding.Detail));
    }

    [Fact]
    public void Terminal_set_audit_reports_a_branch_with_only_one_terminal_end_as_incomplete()
    {
        PsseParseResult parsed = Parse();
        var terminal = new PsseTopologyTerminal(1, 6, 0, "B", "1", 2, null, "1", 914);
        IReadOnlyList<PsseTerminalBinding> bindings = PsseTerminalIdentityBinder.Bind(Topology(parsed, terminal), parsed);

        PsseTerminalSetAudit audit = Assert.Single(PsseTerminalSetAuditor.Audit(bindings));

        Assert.Equal(2, audit.ExpectedEnds);
        Assert.Equal(1, audit.ObservedEnds);
        Assert.Equal(PsseTerminalSetStatus.Incomplete, audit.Status);
    }

    [Fact]
    public void Equipment_inventory_audit_detects_a_shunt_with_no_terminal_row()
    {
        PsseParseResult parsed = Parse();
        PsseSubstationTopology topology = PsseSubstationTopologyReader.Read(parsed);
        IReadOnlyList<PsseTerminalBinding> bindings = PsseTerminalIdentityBinder.Bind(topology, parsed);

        PsseTerminalSetAudit audit = Assert.Single(PsseTerminalSetAuditor.Audit(topology, parsed, bindings),
            item => item.EquipmentIdentity == "F|1|2");

        Assert.Equal(1, audit.ExpectedEnds);
        Assert.Equal(0, audit.ObservedEnds);
        Assert.Equal(PsseTerminalSetStatus.Incomplete, audit.Status);
        Assert.Contains("Observed 0 of 1", audit.Detail);
    }
}
