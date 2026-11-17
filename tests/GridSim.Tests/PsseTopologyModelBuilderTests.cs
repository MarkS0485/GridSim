using GridSim.Core.IO.Psse;
using GridSim.Core.IO.Psse.Topology;
using GridSim.Core.Model;
using GridSim.Core.PowerFlow;
using Xunit;
using static GridSim.Tests.PsseTopologyFixture;

namespace GridSim.Tests;

/// <summary>
/// Supported-subset GridModel builder from the projected node-breaker topology. The first claim is the intact one: with
/// every switch closed the projected model, and its AC solve, are bit-identical to the legacy RAW loader's. Only then are
/// switched states solved, and their claims are about mapping and regrouping, checked against the solve's own power
/// balance (tolerances derived from the reported Newton mismatch).
/// </summary>
public sealed class PsseTopologyModelBuilderTests
{
    private static string SampleV35 => File.ReadAllText(Path.Combine(Dir, "sample-v35.raw"))
        .Replace("'CB 1', 2, 1, 1, 0.0,", "'CB 1', 2, 1, 1, 0.0001,", StringComparison.Ordinal);

    [Fact]
    public void Intact_projection_is_the_legacy_model_and_solves_bit_identically()
    {
        PsseParseResult legacy = PsseRawParser.ParseWithDiagnostics(CouplerText);
        PsseSubstationCase c = ReadCoupler();

        PsseTopologyModelResult r = PsseTopologyModelBuilder.Build(c, PsseTopologyModelBuilder.AllClosed(c.Topology), Options);

        Assert.True(r.Built, string.Join("; ", r.Refusals.Select(x => x.Detail)));
        Assert.Empty(r.Refusals);
        PsseTopologyModelBus bus = Assert.Single(r.Buses, b => b.NativeBus == 200);
        Assert.Equal((200, PsseSectionState.Unsplit), (bus.ModelBusId, bus.State));
        Assert.Equal(legacy.Model.Name, r.Model!.Name);
        Assert.Equal(legacy.Model.BaseMva, r.Model.BaseMva);
        Assert.Equal(legacy.Model.Buses, r.Model.Buses);
        Assert.Equal(legacy.Model.Branches, r.Model.Branches);
        Assert.Equal(legacy.Model.AllGenerators, r.Model.AllGenerators);
        Assert.Equal(legacy.BranchKeys, r.BranchSources);
        Assert.Equal(legacy.MachineKeys, r.MachineSources);
        // The projected text differs from the source only by the removed substation data.
        Assert.DoesNotContain("BB SECTION A", r.ProjectedRawText);

        AssertBitIdentical(Solve(legacy.Model), Solve(r.Model));
        Assert.True(Solve(r.Model).Converged);
    }

    [Fact]
    public void Repeat_builds_from_fresh_reads_are_identical()
    {
        PsseSubstationCase a = ReadCoupler(), b = ReadCoupler();
        var open = PsseTopologyModelBuilder.With(PsseTopologyModelBuilder.AllClosed(a.Topology), (Coupler, false));

        PsseTopologyModelResult first = PsseTopologyModelBuilder.Build(a, open, Options);
        PsseTopologyModelResult second = PsseTopologyModelBuilder.Build(b, open, Options);

        Assert.Equal(first.ProjectedRawText, second.ProjectedRawText);
        Assert.Equal(first.ProjectedRawSha256, second.ProjectedRawSha256);
        Assert.Equal(first.Model!.Buses, second.Model!.Buses);
        Assert.Equal(first.BranchSources, second.BranchSources);
        AssertBitIdentical(Solve(first.Model), Solve(second.Model));
    }

    [Fact]
    public void Coupler_open_solves_with_each_section_supplied_by_its_own_circuit()
    {
        PsseSubstationCase c = ReadCoupler();
        var open = PsseTopologyModelBuilder.With(PsseTopologyModelBuilder.AllClosed(c.Topology), (Coupler, false));

        PsseTopologyModelResult r = PsseTopologyModelBuilder.Build(c, open, Options);

        Assert.True(r.Built, string.Join("; ", r.Refusals.Select(x => x.Detail)));
        Assert.Equal(new[] { 100, 200, 201 }, r.Model!.Buses.Select(b => b.Id).ToArray());
        Bus a = r.Model.Buses.Single(b => b.Id == 200), b = r.Model.Buses.Single(b => b.Id == 201);
        Assert.Equal((40.0, 12.0, 0.0), (a.Pd, a.Qd, a.Bs));                 // load 1 only
        Assert.Equal((40.0, 12.0, 5.0), (b.Pd, b.Qd, b.Bs));                 // loads 2 and 3 and the fixed shunt
        Assert.Equal(BusType.Pq, b.Type);
        Assert.Equal(new[] { (100, 200), (100, 201) }, r.Model.Branches.Select(x => (x.From, x.To)).ToArray());
        // Reverse maps name the native records: the parallel circuits keep their own circuit identifiers.
        Assert.Equal(new[] { "1", "2" }, r.BranchSources.Select(k => k.Circuit).ToArray());
        Assert.All(r.BranchSources, k => Assert.Equal((100, 200), (k.FromBus, k.ToBus)));
        Assert.Equal(200, r.NativeBusOf(201));

        PowerFlowResult pf = Solve(r.Model);
        Assert.True(pf.Converged);
        double tol = 10 * pf.MaxMismatch * r.Model.BaseMva + 1e-9;   // MW; the solve's own reported mismatch
        Assert.Equal(new[] { "CCT 1", "CCT 2" }, pf.LineFlows.Select(f => f.Name).ToArray());
        // Each receiving end carries exactly its section's demand: 40 MW on each circuit.
        Assert.InRange(-pf.LineFlows[0].PToMw, 40 - tol, 40 + tol);
        Assert.InRange(-pf.LineFlows[1].PToMw, 40 - tol, 40 + tol);
        Assert.InRange(pf.PInjMw[Array.IndexOf(pf.BusIds.ToArray(), 201)], -40 - tol, -40 + tol);
    }

    [Fact]
    public void Opening_one_parallel_circuit_bay_keeps_the_sister_circuit_energised_and_mapped()
    {
        PsseSubstationCase c = ReadCoupler();
        var breakerOpen = PsseTopologyModelBuilder.With(PsseTopologyModelBuilder.AllClosed(c.Topology), (Circuit2Breaker, false));

        PsseTopologyModelResult r = PsseTopologyModelBuilder.Build(c, breakerOpen, Options);

        Assert.True(r.Built, string.Join("; ", r.Refusals.Select(x => x.Detail)));
        // Node 4 (circuit 2's bay) is its own energised section holding only the circuit's end: the line stays in the
        // model, open at the substation end.
        Assert.Equal(201, r.NodeToModelBus[new PsseNodeKey(1, 4)]);
        Assert.Equal(new[] { (100, 200), (100, 201) }, r.Model!.Branches.Select(x => (x.From, x.To)).ToArray());
        Assert.Equal(80.0, r.Model.Buses.Single(b => b.Id == 200).Pd);

        PowerFlowResult pf = Solve(r.Model);
        Assert.True(pf.Converged);
        double tol = 10 * pf.MaxMismatch * r.Model.BaseMva + 1e-9;
        Assert.Equal("CCT 1", pf.LineFlows[0].Name);
        Assert.Equal("CCT 2", pf.LineFlows[1].Name);
        Assert.InRange(-pf.LineFlows[0].PToMw, 80 - tol, 80 + tol);           // the sister circuit carries all the demand
        Assert.InRange(pf.LineFlows[1].PToMw, -tol, tol);                     // the open-ended circuit carries no active power
        Assert.True(Math.Abs(pf.LineFlows[1].QFromMvar) > 0);                 // but is still energised (charging)
    }

    [Fact]
    public void Every_unsupported_class_and_control_refuses_with_source_lines()
    {
        string text = SampleV35;
        PsseSubstationCase c = Read(text);

        PsseTopologyModelResult r = PsseTopologyModelBuilder.Build(c, PsseTopologyModelBuilder.AllClosed(c.Topology), Options);

        Assert.False(r.Built);
        Assert.Null(r.Model);
        Assert.Contains(r.Refusals, x => x.Kind == PsseTopologyRefusalKind.UnsupportedEquipmentClass &&
            x.Section == "FACTS DEVICE" && x.SourceLine == LineOf(text, "'FACTS 1     '"));
        Assert.Contains(r.Refusals, x => x.Kind == PsseTopologyRefusalKind.UnsupportedControl &&
            x.Section == "SWITCHED SHUNT" && x.SourceLine == LineOf(text, "    3,'1 ',    1,   0,   1,1.05000"));
        Assert.Contains(r.Refusals, x => x.Kind == PsseTopologyRefusalKind.UnsupportedControl &&
            x.Section == "TRANSFORMER" && x.SourceLine == LineOf(text, " 410.000, 400.000"));
        Assert.Equal(r.Refusals.OrderBy(x => x.SourceLine).ThenBy(x => x.Kind), r.Refusals);
    }

    [Theory]
    [InlineData("0 / END OF TWO-TERMINAL DC DATA", "TWO-TERMINAL DC")]
    [InlineData("0 / END OF VOLTAGE SOURCE CONVERTER DATA", "VSC DC")]
    [InlineData("0 / END OF MULTI-TERMINAL DC DATA", "MULTI-TERMINAL DC")]
    [InlineData("0 / END OF INDUCTION MACHINE DATA", "INDUCTION MACHINE")]
    public void Hvdc_and_induction_sections_refuse_even_out_of_service(string terminator, string section)
    {
        // Out-of-service records the legacy reader counts and reads past; the builder still refuses the class.
        string record = section switch
        {
            "TWO-TERMINAL DC" => "'DC1', 0, 1.0, 100.0, 500.0, 0.0, 0.0, 0.0, 'I', 0.0, 20, 1.0\r\n" +
                                 "  100, 1, 15.0, 5.0, 1.0, 1.0, 1.0, 1.0, 1.0, 1.0, 0.0, 0, 0, 0, '1', 0.0\r\n" +
                                 "  200, 1, 15.0, 5.0, 1.0, 1.0, 1.0, 1.0, 1.0, 1.0, 0.0, 0, 0, 0, '1', 0.0",
            "VSC DC" => "'VSC1', 0, 1.0\r\n  100, 1, 1, 0.0, 1.0\r\n  200, 1, 1, 0.0, 1.0",
            "MULTI-TERMINAL DC" => "'MT1', 0, 0, 0, 0, 1.0, 0",
            _ => "  200, 'IM', 0, 1, 1, 1, 1, 0.0",
        };
        string text = CouplerText.Replace(terminator, record + "\r\n" + terminator, StringComparison.Ordinal);
        PsseSubstationCase c;
        try { c = Read(text); }
        catch (InvalidDataException) { return; }   // the incumbent parser refused the record itself: also fail closed

        PsseTopologyModelResult r = PsseTopologyModelBuilder.Build(c, PsseTopologyModelBuilder.AllClosed(c.Topology), Options);

        Assert.False(r.Built);
        PsseTopologyRefusal refusal = Assert.Single(r.Refusals);
        Assert.Equal((PsseTopologyRefusalKind.UnsupportedEquipmentClass, section), (refusal.Kind, refusal.Section));
        Assert.Equal(LineOf(text, record.Split('\r')[0]), refusal.SourceLine);
    }

    [Fact]
    public void A_closed_switch_above_the_declared_zero_impedance_threshold_is_refused()
    {
        PsseSubstationCase c = ReadCoupler();

        PsseTopologyModelResult r = PsseTopologyModelBuilder.Build(c, PsseTopologyModelBuilder.AllClosed(c.Topology),
            new PsseTopologyModelOptions { ZeroImpedanceThresholdPu = 0.00005 });

        Assert.False(r.Built);
        Assert.Equal(3, r.Refusals.Count);
        Assert.All(r.Refusals, x => Assert.Equal(PsseTopologyRefusalKind.UnsupportedSwitch, x.Kind));
    }

    [Fact]
    public void An_equipment_end_without_a_terminal_row_is_refused_only_when_its_bus_splits()
    {
        string text = Remove(CouplerText, "   200, 2, 'F', '1 '");
        PsseSubstationCase c = Read(text);
        var intact = PsseTopologyModelBuilder.AllClosed(c.Topology);

        Assert.True(PsseTopologyModelBuilder.Build(c, intact, Options).Built);
        PsseTopologyModelResult open = PsseTopologyModelBuilder.Build(c, PsseTopologyModelBuilder.With(intact, (Coupler, false)), Options);

        Assert.False(open.Built);
        PsseTopologyRefusal refusal = Assert.Single(open.Refusals);
        Assert.Equal(PsseTopologyRefusalKind.UnplacedEquipmentEnd, refusal.Kind);
        Assert.Equal(LineOf(text, "  200,'1 ',   1,     0.000,     5.000"), refusal.SourceLine);
    }

    [Fact]
    public void A_split_swing_bus_needs_exactly_one_energised_section_with_a_machine()
    {
        // Make bus 200 the swing bus with its machine on section B, then open the coupler: section B keeps IDE 3.
        string text = CouplerText
            .Replace("  100,'GRID 132    ', 132.0000,3", "  100,'GRID 132    ', 132.0000,2", StringComparison.Ordinal)
            .Replace("  200,'SUB 132     ', 132.0000,1", "  200,'SUB 132     ', 132.0000,3", StringComparison.Ordinal);
        text = InsertAfter(text, "  100,'1 ',  80.000",
            "  200,'1 ',   0.000,     0.000,   300.000,  -300.000,1.00000,     0,     0,   200.000, 0.00000E+0, 2.00000E-1, 0.00000E+0, 0.00000E+0,1.00000,   1,  100.0,  300.000,     0.000,  0,   1,1.0000");
        text = InsertAfter(text, "   200, 2, 'F', '1 '", "   200, 2, 'M', '1 '");
        PsseSubstationCase c = Read(text);
        var intact = PsseTopologyModelBuilder.AllClosed(c.Topology);

        PsseTopologyModelResult open = PsseTopologyModelBuilder.Build(c, PsseTopologyModelBuilder.With(intact, (Coupler, false)), Options);

        Assert.True(open.Built, string.Join("; ", open.Refusals.Select(x => x.Detail)));
        Assert.Equal((BusType.Pq, BusType.Slack), (open.Model!.Buses.Single(b => b.Id == 200).Type, open.Model.Buses.Single(b => b.Id == 201).Type));
        Assert.Equal(new PsseMachineKey(200, "1"), open.MachineSources[1]);
        Assert.Equal(201, open.Model.AllGenerators[1].Bus);

        // With the machine's bay also isolated (circuit 2 breaker open) no energised section holds it: refused.
        PsseTopologyModelResult noReference = PsseTopologyModelBuilder.Build(c,
            PsseTopologyModelBuilder.With(intact, (Coupler, false), (Circuit2Breaker, false)), Options);
        Assert.False(noReference.Built);
        Assert.Contains(noReference.Refusals, x => x.Kind == PsseTopologyRefusalKind.SwingBusSplit);
    }

    [Fact]
    public void A_missing_switch_state_is_an_input_error_not_an_inferred_closure()
    {
        PsseSubstationCase c = ReadCoupler();
        var partial = PsseTopologyModelBuilder.AllClosed(c.Topology).Where(kv => kv.Key != Coupler).ToDictionary(kv => kv.Key, kv => kv.Value);

        InvalidDataException error = Assert.Throws<InvalidDataException>(() => PsseTopologyModelBuilder.Build(c, partial, Options));
        Assert.Contains("An explicit state is required", error.Message);
    }
}
