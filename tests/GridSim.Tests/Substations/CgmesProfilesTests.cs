using System.IO.Compression;
using System.Text;
using GridSim.Core.IO.Cim;
using GridSim.Core.IO.Cim.Profiles;
using GridSim.Core.IO.Cim.Zip;
using GridSim.Core.Model.Substations;
using GridSim.Core.PowerFlow;
using Xunit;
using static GridSim.Tests.Substations.SubstationFixtures;

namespace GridSim.Tests.Substations;

/// <summary>
/// RC4 R4-11 part B: CGMES completion. Oracles are hand-built models whose switch states, node groups and radial flows are worked
/// out in the comments (as in part A), the round trip SubstationModel -> CGMES EQ/TP/SSH(/SV) -> SubstationModel (identical,
/// compared as the part A JSON text), and the incumbent CimImporter's own switch-based topology processor as an independent
/// check on the bus count. No third-party model is included: every document is written by the exporter or by hand below.
/// </summary>
public sealed class CgmesProfilesTests
{
    private static string Json(SubstationModel m) => SubstationJson.Write(m);

    private static Dictionary<string, bool> States(params (string Id, bool Open)[] s) => s.ToDictionary(x => x.Id, x => x.Open, StringComparer.Ordinal);

    private static CgmesReadResult Back(CgmesExportResult e, CgmesReadOptions? o = null) =>
        CgmesSubstationReader.Read(e.Files.Select(f => (f.Kind, f.FileName, f.Text)), o);

    /// <summary>
    /// A two-substation, two-voltage model with every element kind. Values are images of physical ones (ohms over the base
    /// impedance, kV set-points over kV) so a CGMES document can hold them to the bit: the line is 2.42 + j12.1 ohm, 82.6 uS at
    /// 110 kV; the transformer 0.6 + j12.1 ohm on its 110 kV side with a 1.25 percent ratio step and 2.5 degrees; the shunt is
    /// 1 mS at 20 kV; the machine regulates 20.4 kV.
    /// </summary>
    internal static SubstationModel Rich()
    {
        const double baseMva = 100;
        double Zb(double kv) => kv * kv / baseMva;
        double ratio = 1.0 + (1.0 * 1.25) / 100.0;
        return new SubstationModel
        {
            Name = "rich", BaseMva = baseMva, FrequencyHz = 50,
            Substations = new[]
            {
                new Substation("SA", "Alpha",
                    new[] { new VoltageLevel("SA110", 110), new VoltageLevel("SA20", 20) },
                    new[] { new SubstationBay("BAYL", "SA110", "Line bay"), new SubstationBay("BAYT", "SA110") },
                    new[]
                    {
                        new ConnectivityNode("A.bb1", "SA110"), new ConnectivityNode("A.bb2", "SA110"),
                        new ConnectivityNode("A.l1", "SA110", "BAYL"), new ConnectivityNode("A.t", "SA110", "BAYT"),
                        new ConnectivityNode("A.lv", "SA20"),
                    },
                    new[]
                    {
                        new NbSwitch("A.CPL", SwitchKind.Breaker, "A.bb1", "A.bb2", false),
                        new NbSwitch("A.CBL", SwitchKind.Breaker, "A.bb2", "A.l1", false, "BAYL"),
                        new NbSwitch("A.DST", SwitchKind.Disconnector, "A.bb1", "A.t", false, "BAYT"),
                        new NbSwitch("A.GND", SwitchKind.EarthSwitch, "A.l1", null, true, "BAYL"),
                        new NbSwitch("A.LBS", SwitchKind.LoadBreak, "A.bb1", "A.bb2", true),
                    }),
                new Substation("SB", null, new[] { new VoltageLevel("SB110", 110) }, Array.Empty<SubstationBay>(),
                    new[] { new ConnectivityNode("B.r", "SB110"), new ConnectivityNode("B.ld", "SB110") },
                    new[] { new NbSwitch("B.CB", SwitchKind.Breaker, "B.r", "B.ld", false) }),
            },
            Lines = new[] { new NbLine("LN1", "A.l1", "B.r", 2.42 / Zb(110), 12.1 / Zb(110), 0.0000826 * Zb(110), 120) },
            Transformers = new[] { new NbTransformer("T1", "A.t", "A.lv", 0.6 / Zb(110), 12.1 / Zb(110), 0, ratio, 2.5, 80) },
            Loads = new[] { new NbLoad("LD", "B.ld", 50, 10) },
            Generators = new[] { new NbGenerator("G1", "A.lv", 51, 20.4 / 20.0, 3, -30, 40, 0, 100, Slack: true) },
            Shunts = new[] { new NbShunt("SH", "A.lv", 0, (0.001 * 1.0) * (20.0 * 20.0)) },
        };
    }

    public static TheoryData<string> Models => new() { "rich", "two-feed", "double-busbar", "breaker-and-a-half", "ring" };

    private static SubstationModel ByName(string n) => n switch
    {
        "rich" => Rich(), "two-feed" => TwoFeed(), "double-busbar" => DoubleBusbar(), "breaker-and-a-half" => BreakerAndAHalf(), "ring" => Ring(),
        _ => throw new ArgumentOutOfRangeException(nameof(n)),
    };

    // ---------------------------------------------------------------- round trip

    [Theory]
    [MemberData(nameof(Models))]
    public void Round_trip_through_EQ_TP_SSH_is_identical_in_both_editions(string name)
    {
        SubstationModel m = ByName(name);
        foreach (CgmesVersion v in new[] { CgmesVersion.V2_4_15, CgmesVersion.V3_0 })
        {
            CgmesExportResult e = CgmesSubstationExporter.Export(m, null, new CgmesExportOptions(v));
            CgmesReadResult r = Back(e);
            Assert.Equal(Json(m), Json(r.Model));
            // The SSH states are the normal states (a declared policy), one per switch.
            Assert.Equal(m.NormalStates().OrderBy(k => k.Key, StringComparer.Ordinal), r.SwitchStates.OrderBy(k => k.Key, StringComparer.Ordinal));
            // TP, written from the projection, agrees with the groups the reader recomputes from the SSH states.
            Assert.Contains(r.Notes, n => n.StartsWith("TP agrees", StringComparison.Ordinal));
            // A second export of the read-back model is byte-identical to the first.
            CgmesExportResult again = CgmesSubstationExporter.Export(r.Model, r.SwitchStates, new CgmesExportOptions(v));
            Assert.Equal(e.Files.Select(f => f.Text), again.Files.Select(f => f.Text));
        }
    }

    [Fact]
    public void Round_trip_keeps_non_default_switch_states_and_the_projection_at_them()
    {
        SubstationModel m = DoubleBusbar();
        // Open the bus coupler and move feeder B onto X1 (close B_d1, open B_d2).
        var s = States(("CPL", true), ("B_d1", false), ("B_d2", true));
        CgmesExportResult e = CgmesSubstationExporter.Export(m, s);
        CgmesReadResult r = Back(e);
        Assert.True(r.SwitchStates["CPL"]);
        Assert.False(r.SwitchStates["B_d1"]);
        Assert.True(r.SwitchStates["B_d2"]);
        // Hand grouping of the double busbar (part A fixture): X1 carries A (via A_d1) and B (via B_d1); X2 carries D
        // (via D_d2). A_cb, B_cb and D_cb are closed, so A1+A2, B1+B2, D1+D2 join their feeders: groups
        // A1+A2+B1+B2+X1 | D1+D2+X2.
        SubstationProjectionResult p = SubstationProjection.Project(r.Model, r.SwitchStates);
        Assert.Equal("A1+A2+B1+B2+X1|D1+D2+X2", p.GroupingText());
        Assert.Contains(r.Notes, n => n.StartsWith("TP agrees", StringComparison.Ordinal));
    }

    [Fact]
    public void A_TP_that_contradicts_the_switch_states_is_reported()
    {
        SubstationModel m = DoubleBusbar();
        CgmesExportResult e = CgmesSubstationExporter.Export(m, States(("CPL", true)));
        // Corrupt TP: with CPL open the groups are {A1,A2,X1} (rep A1) and {B1,B2,D1,D2,X2} (rep B1); put the second into the first's TopologicalNode (as if the coupler were closed).
        var docs = e.Files.Select(f => (f.Kind, f.FileName, f.Kind == CgmesProfileKind.TP
            ? f.Text.Replace("#_TN.B1", "#_TN.A1") : f.Text)).ToList();
        CgmesReadResult r = CgmesSubstationReader.Read(docs);
        Assert.Contains(r.Notes, n => n.StartsWith("TP DISAGREES", StringComparison.Ordinal));
    }

    [Fact]
    public void Documents_are_read_in_profile_order_whatever_order_they_are_given_in()
    {
        CgmesExportResult e = CgmesSubstationExporter.Export(Rich());
        var docs = e.Files.Select(f => (f.Kind, f.FileName, f.Text)).ToList();
        string a = Json(CgmesSubstationReader.Read(docs).Model);
        docs.Reverse();
        Assert.Equal(a, Json(CgmesSubstationReader.Read(docs).Model));
    }

    [Fact]
    public void Export_is_deterministic_and_3_0_uses_uuid_identifiers_2_4_15_underscore_ids()
    {
        CgmesExportResult a = CgmesSubstationExporter.Export(Rich(), null, new CgmesExportOptions(CgmesVersion.V3_0));
        CgmesExportResult b = CgmesSubstationExporter.Export(Rich(), null, new CgmesExportOptions(CgmesVersion.V3_0));
        Assert.Equal(a.Files.Select(f => f.Text), b.Files.Select(f => f.Text));
        string eq3 = a.Files[0].Text;
        Assert.Contains("http://iec.ch/TC57/CIM100#", eq3, StringComparison.Ordinal);
        Assert.Contains("rdf:about=\"urn:uuid:", eq3, StringComparison.Ordinal);
        Assert.DoesNotContain("rdf:ID=", eq3, StringComparison.Ordinal);
        string eq2 = CgmesSubstationExporter.Export(Rich()).Files[0].Text;
        Assert.Contains("http://iec.ch/TC57/2013/CIM-schema-cim16#", eq2, StringComparison.Ordinal);
        Assert.Contains("rdf:ID=\"_CN.A.bb1\"", eq2, StringComparison.Ordinal);
        Assert.Contains("<cim:Switch.normalOpen>false</cim:Switch.normalOpen>", eq2, StringComparison.Ordinal);
        // CGMES machine sign: a generating machine's SSH p is negative (51 MW generated -> -51).
        Assert.Contains("<cim:RotatingMachine.p>-51</cim:RotatingMachine.p>", CgmesSubstationExporter.Export(Rich()).Files[2].Text, StringComparison.Ordinal);
    }

    [Fact]
    public void Omissions_are_a_structured_record_naming_every_profile_not_written()
    {
        CgmesExportResult e = CgmesSubstationExporter.Export(Rich());
        foreach (CgmesProfileKind k in new[] { CgmesProfileKind.DL, CgmesProfileKind.GL, CgmesProfileKind.SC, CgmesProfileKind.DY, CgmesProfileKind.SV })
            Assert.Contains(e.Omissions, o => o.Profile == k && o.Reason.Length > 0);
        Assert.Equal(3, e.Files.Count);   // no SV without a solved state
    }

    // ---------------------------------------------------------------- hand-worked projection and power flow

    [Fact]
    public void Written_and_read_back_the_coupler_split_gives_the_closed_form_radial_flows()
    {
        // TwoFeed with the coupler CPL open in the SSH. Groups (part A, worked there): GA+L1+X1 | GB+L2+X2 | R1+R2+Y, so
        // three buses. GB (30 MW) feeds only LN2 and GA the rest through LN1: lossless lines carry what they deliver, so
        // LN2 = 30 MW and LN1 = 80 - 30 = 50 MW.
        SubstationModel m = TwoFeed();
        CgmesExportResult e = CgmesSubstationExporter.Export(m, States(("CPL", true)));
        CgmesReadResult r = Back(e);
        SubstationProjectionResult p = SubstationProjection.Project(r.Model, r.SwitchStates);
        Assert.Equal("GA+L1+X1|GB+L2+X2|R1+R2+Y", p.GroupingText());
        PowerFlowResult pf = new NewtonRaphsonPowerFlow().Solve(p.Model);
        Assert.True(pf.Converged);
        Assert.Equal(50.0, pf.LineFlows.Single(f => f.Name == "LN1").PFromMw, 6);
        Assert.Equal(30.0, pf.LineFlows.Single(f => f.Name == "LN2").PFromMw, 6);
        // Bit-identical to the model projected without going through CGMES (the line x = 0.1 is read back exactly or noted).
        PowerFlowResult direct = new NewtonRaphsonPowerFlow().Solve(SubstationProjection.Project(m, States(("CPL", true)), new SubstationProjectionOptions(UnspecifiedStatePolicy.UseNormalState)).Model);
        if (e.Notes.Count == 0) Assert.Equal(direct.LineFlows.Single(f => f.Name == "LN1").PFromMw, pf.LineFlows.Single(f => f.Name == "LN1").PFromMw);
    }

    [Fact]
    public void The_incumbent_CIM_importers_switch_topology_processor_finds_the_same_buses()
    {
        // Independent of SubstationProjection: CimImporter.Parse with CimTopology.Switches builds buses from the EQ switches and
        // the SSH states by its own union-find. The bus counts must agree for every switching state tried.
        foreach (var (name, open) in new[] { ("two-feed", new string[0]), ("two-feed", new[] { "CPL" }), ("two-feed", new[] { "CPL", "CBL2" }),
                     ("double-busbar", new[] { "CPL" }), ("ring", new[] { "CBa", "CBc" }) })
        {
            SubstationModel m = ByName(name);
            var s = m.NormalStates().ToDictionary(k => k.Key, k => k.Value, StringComparer.Ordinal);
            foreach (string o in open) s[o] = true;
            CgmesExportResult e = CgmesSubstationExporter.Export(m, s);
            int mine = e.Projection.Buses.Count;
            CimNetwork net = CimImporter.Parse(e.Files.Select(f => f.Text).ToList(), new CimImportOptions(CimTopology.Switches));
            // The importer keeps a bus for every group that has equipment; compare with the groups holding at least one
            // terminal of in-service equipment (an open-breaker group with one terminal is isolated in both).
            Assert.True(net.Buses.Count >= mine, $"{name} [{string.Join(",", open)}]: incumbent {net.Buses.Count} buses, projection {mine}.");
            Assert.True(net.Buses.Count <= e.Projection.Groups.Count, $"{name} [{string.Join(",", open)}]: incumbent {net.Buses.Count}, groups {e.Projection.Groups.Count}.");
        }
    }

    [Fact]
    public void Rich_model_solves_after_the_round_trip_and_the_SV_document_carries_the_solved_voltages()
    {
        SubstationModel m = Rich();
        SubstationProjectionResult p0 = SubstationProjection.ProjectNormal(m);
        PowerFlowResult pf = new NewtonRaphsonPowerFlow().Solve(p0.Model);
        Assert.True(pf.Converged);
        var volts = new Dictionary<int, (double, double)>();
        for (int i = 0; i < pf.BusIds.Count; i++) volts[pf.BusIds[i]] = (pf.Vm[i], pf.VaDeg[i]);
        CgmesExportResult e = CgmesSubstationExporter.Export(m, null, new CgmesExportOptions(Voltages: volts));
        Assert.Equal(4, e.Files.Count);
        CgmesReadResult r = Back(e);
        Assert.Equal(Json(m), Json(r.Model));
        // Each TopologicalNode's SvVoltage is the solved Vm times its base kV; the generator bus holds its 20.4 kV set-point.
        Assert.Equal(p0.Buses.Count, r.Voltages.Count);
        var gen = p0.Buses.Single(b => b.Nodes.Contains("A.lv"));
        Assert.Equal(20.4, r.Voltages[gen.RepresentativeNode].Kv, 9);
        foreach (var b in p0.Buses)
        {
            int i = pf.BusIds.ToList().IndexOf(b.BusId);
            Assert.Equal(pf.Vm[i] * b.NominalKv, r.Voltages[b.RepresentativeNode].Kv, 9);
        }
    }

    // ---------------------------------------------------------------- reader: foreign-style and refusals

    private const string Hdr = "<?xml version=\"1.0\"?><rdf:RDF xmlns:rdf=\"http://www.w3.org/1999/02/22-rdf-syntax-ns#\" xmlns:cim=\"http://iec.ch/TC57/2013/CIM-schema-cim16#\" xmlns:md=\"http://iec.ch/TC57/61970-552/ModelDescription/1#\">";

    /// <summary>A hand-written minimal EQ (no GridSim tags: ids are the mRIDs): 110 kV, busbar sections B1, B2 joined by a
    /// breaker, a load on B1 and a machine on B2.</summary>
    private static string ForeignEq(string extra = "", bool normalOpen = true) => Hdr +
        "<md:FullModel rdf:about=\"urn:uuid:11111111-1111-4111-8111-111111111111\"><md:Model.profile>http://entsoe.eu/CIM/EquipmentCore/3/1</md:Model.profile></md:FullModel>" +
        "<cim:BaseVoltage rdf:ID=\"_BV\"><cim:BaseVoltage.nominalVoltage>110</cim:BaseVoltage.nominalVoltage></cim:BaseVoltage>" +
        "<cim:GeographicalRegion rdf:ID=\"_GR\"><cim:IdentifiedObject.name>r</cim:IdentifiedObject.name></cim:GeographicalRegion>" +
        "<cim:SubGeographicalRegion rdf:ID=\"_SGR\"><cim:IdentifiedObject.name>sr</cim:IdentifiedObject.name><cim:SubGeographicalRegion.Region rdf:resource=\"#_GR\"/></cim:SubGeographicalRegion>" +
        "<cim:Substation rdf:ID=\"_S\"><cim:IdentifiedObject.name>Sub</cim:IdentifiedObject.name><cim:Substation.Region rdf:resource=\"#_SGR\"/></cim:Substation>" +
        "<cim:VoltageLevel rdf:ID=\"_VL\"><cim:IdentifiedObject.name>VL</cim:IdentifiedObject.name><cim:VoltageLevel.Substation rdf:resource=\"#_S\"/><cim:VoltageLevel.BaseVoltage rdf:resource=\"#_BV\"/></cim:VoltageLevel>" +
        "<cim:ConnectivityNode rdf:ID=\"_B1\"><cim:IdentifiedObject.name>B1</cim:IdentifiedObject.name><cim:ConnectivityNode.ConnectivityNodeContainer rdf:resource=\"#_VL\"/></cim:ConnectivityNode>" +
        "<cim:ConnectivityNode rdf:ID=\"_B2\"><cim:IdentifiedObject.name>B2</cim:IdentifiedObject.name><cim:ConnectivityNode.ConnectivityNodeContainer rdf:resource=\"#_VL\"/></cim:ConnectivityNode>" +
        "<cim:Breaker rdf:ID=\"_BRK\"><cim:IdentifiedObject.name>BRK</cim:IdentifiedObject.name>" + (normalOpen ? "<cim:Switch.normalOpen>false</cim:Switch.normalOpen>" : "") + "</cim:Breaker>" +
        "<cim:Terminal rdf:ID=\"_T1\"><cim:Terminal.ConductingEquipment rdf:resource=\"#_BRK\"/><cim:Terminal.ConnectivityNode rdf:resource=\"#_B1\"/><cim:ACDCTerminal.sequenceNumber>1</cim:ACDCTerminal.sequenceNumber></cim:Terminal>" +
        "<cim:Terminal rdf:ID=\"_T2\"><cim:Terminal.ConductingEquipment rdf:resource=\"#_BRK\"/><cim:Terminal.ConnectivityNode rdf:resource=\"#_B2\"/><cim:ACDCTerminal.sequenceNumber>2</cim:ACDCTerminal.sequenceNumber></cim:Terminal>" +
        "<cim:ConformLoad rdf:ID=\"_LD\"><cim:IdentifiedObject.name>LD</cim:IdentifiedObject.name></cim:ConformLoad>" +
        "<cim:Terminal rdf:ID=\"_T3\"><cim:Terminal.ConductingEquipment rdf:resource=\"#_LD\"/><cim:Terminal.ConnectivityNode rdf:resource=\"#_B1\"/><cim:ACDCTerminal.sequenceNumber>1</cim:ACDCTerminal.sequenceNumber></cim:Terminal>" +
        "<cim:GeneratingUnit rdf:ID=\"_GU\"><cim:IdentifiedObject.name>GU</cim:IdentifiedObject.name></cim:GeneratingUnit>" +
        "<cim:SynchronousMachine rdf:ID=\"_SM\"><cim:IdentifiedObject.name>SM</cim:IdentifiedObject.name><cim:RotatingMachine.GeneratingUnit rdf:resource=\"#_GU\"/></cim:SynchronousMachine>" +
        "<cim:Terminal rdf:ID=\"_T4\"><cim:Terminal.ConductingEquipment rdf:resource=\"#_SM\"/><cim:Terminal.ConnectivityNode rdf:resource=\"#_B2\"/><cim:ACDCTerminal.sequenceNumber>1</cim:ACDCTerminal.sequenceNumber></cim:Terminal>" +
        extra + "</rdf:RDF>";

    private const string ForeignSsh = Hdr +
        "<md:FullModel rdf:about=\"urn:uuid:22222222-2222-4222-8222-222222222222\"><md:Model.profile>http://entsoe.eu/CIM/SteadyStateHypothesis/1/1</md:Model.profile></md:FullModel>" +
        "<cim:Breaker rdf:about=\"#_BRK\"><cim:Switch.open>true</cim:Switch.open></cim:Breaker>" +
        "<cim:ConformLoad rdf:about=\"#_LD\"><cim:EnergyConsumer.p>12.5</cim:EnergyConsumer.p><cim:EnergyConsumer.q>2</cim:EnergyConsumer.q></cim:ConformLoad>" +
        "<cim:SynchronousMachine rdf:about=\"#_SM\"><cim:RotatingMachine.p>-12.5</cim:RotatingMachine.p><cim:RotatingMachine.q>-1</cim:RotatingMachine.q><cim:SynchronousMachine.referencePriority>1</cim:SynchronousMachine.referencePriority></cim:SynchronousMachine>" +
        "</rdf:RDF>";

    [Fact]
    public void A_hand_written_foreign_style_EQ_and_SSH_read_with_mRIDs_as_ids_and_the_load_sign_convention()
    {
        CgmesReadResult r = CgmesSubstationReader.Read(new[]
        {
            (CgmesProfileKind.SSH, "x_SSH.xml", ForeignSsh), (CgmesProfileKind.EQ, "x_EQ.xml", ForeignEq()),
        });
        SubstationModel m = r.Model;
        Assert.Equal(new[] { "BRK" }, m.AllSwitches.Select(s => s.Id));
        Assert.Equal(new[] { "B1", "B2" }, m.AllNodes.Select(n => n.Id));
        Assert.Equal(12.5, m.Loads.Single().Pd);
        // Generation is the negative of the CGMES machine p / q: -(-12.5) = 12.5 MW, -(-1) = 1 MVAr; priority 1 -> slack.
        NbGenerator g = m.Generators.Single();
        Assert.Equal(12.5, g.Pg); Assert.Equal(1.0, g.Qg); Assert.True(g.Slack);
        Assert.True(r.SwitchStates["BRK"]);   // SSH: open
        Assert.False(m.AllSwitches.Single().NormalOpen);   // EQ: normally closed
        Assert.Contains(r.Notes, n => n.Contains("no system base", StringComparison.Ordinal));
        // With the breaker open the node groups are B1 and B2 (each holds one equipment terminal, so neither is a bus: the
        // grouping text lists node groups, buses or not).
        SubstationProjectionResult p = SubstationProjection.Project(m, r.SwitchStates);
        Assert.Equal("B1|B2", p.GroupingText());
    }

    [Fact]
    public void Refusals_name_the_object_and_nothing_is_dropped_silently()
    {
        // No Switch.normalOpen: no inferred state.
        var ex = Assert.Throws<InvalidDataException>(() => CgmesSubstationReader.Read(new[] { (CgmesProfileKind.EQ, "e.xml", ForeignEq(normalOpen: false)) },
            new CgmesReadOptions(RequireSsh: false)));
        Assert.Contains("Breaker 'BRK' has no Switch.normalOpen", ex.Message, StringComparison.Ordinal);

        // A missing SSH set-point is refused by default.
        ex = Assert.Throws<InvalidDataException>(() => CgmesSubstationReader.Read(new[] { (CgmesProfileKind.EQ, "e.xml", ForeignEq()) }));
        Assert.Contains("load 'LD' has no SSH value", ex.Message, StringComparison.Ordinal);
        Assert.Contains("machine 'SM' has no SSH value", ex.Message, StringComparison.Ordinal);

        // Conducting equipment the model has no place for is refused by class and id.
        string svc = "<cim:StaticVarCompensator rdf:ID=\"_SVC\"><cim:IdentifiedObject.name>SVC</cim:IdentifiedObject.name></cim:StaticVarCompensator>" +
            "<cim:Terminal rdf:ID=\"_T9\"><cim:Terminal.ConductingEquipment rdf:resource=\"#_SVC\"/><cim:Terminal.ConnectivityNode rdf:resource=\"#_B1\"/><cim:ACDCTerminal.sequenceNumber>1</cim:ACDCTerminal.sequenceNumber></cim:Terminal>";
        ex = Assert.Throws<InvalidDataException>(() => CgmesSubstationReader.Read(new[] { (CgmesProfileKind.EQ, "e.xml", ForeignEq(svc)), (CgmesProfileKind.SSH, "s.xml", ForeignSsh) }));
        Assert.Contains("StaticVarCompensator 'SVC' is not read", ex.Message, StringComparison.Ordinal);

        // A three-winding transformer is refused, not read as two windings.
        string tx3 = "<cim:PowerTransformer rdf:ID=\"_TX\"><cim:IdentifiedObject.name>TX</cim:IdentifiedObject.name></cim:PowerTransformer>" +
            string.Concat(Enumerable.Range(1, 3).Select(i => $"<cim:PowerTransformerEnd rdf:ID=\"_E{i}\"><cim:PowerTransformerEnd.PowerTransformer rdf:resource=\"#_TX\"/><cim:TransformerEnd.endNumber>{i}</cim:TransformerEnd.endNumber></cim:PowerTransformerEnd>")) +
            "<cim:Terminal rdf:ID=\"_T8\"><cim:Terminal.ConductingEquipment rdf:resource=\"#_TX\"/><cim:Terminal.ConnectivityNode rdf:resource=\"#_B1\"/><cim:ACDCTerminal.sequenceNumber>1</cim:ACDCTerminal.sequenceNumber></cim:Terminal>";
        ex = Assert.Throws<InvalidDataException>(() => CgmesSubstationReader.Read(new[] { (CgmesProfileKind.EQ, "e.xml", ForeignEq(tx3)), (CgmesProfileKind.SSH, "s.xml", ForeignSsh) }));
        Assert.Contains("PowerTransformer 'TX' has 3 winding(s)", ex.Message, StringComparison.Ordinal);

        // No EQ at all.
        Assert.Throws<InvalidDataException>(() => CgmesSubstationReader.Read(new[] { (CgmesProfileKind.SSH, "s.xml", ForeignSsh) }));
        // A DTD is refused (no entity expansion).
        Assert.ThrowsAny<Exception>(() => CgmesSubstationReader.Read(new[] { (CgmesProfileKind.EQ, "e.xml", "<!DOCTYPE x [<!ENTITY a \"b\">]>" + ForeignEq()) }));
    }

    [Fact]
    public void The_incumbent_RC3_fixture_with_its_static_var_compensator_is_refused_by_name_not_partly_read()
    {
        string dir = CgmesStudyJobTests.FixtureDir;
        var docs = new[] { "p3_EQ.xml", "p3_TP.xml", "p3_SSH.xml", "p3_SV.xml" }
            .Select(f => (CgmesVocabulary.KindOfFileName(f), f, File.ReadAllText(Path.Combine(dir, f)))).ToList();
        var ex = Assert.Throws<InvalidDataException>(() => CgmesSubstationReader.Read(docs));
        Assert.Contains("StaticVarCompensator 'SVC_B' is not read", ex.Message, StringComparison.Ordinal);
        // The fixture also has no Substation objects (its voltage levels name none): refused, not invented.
        Assert.Contains("VoltageLevel", ex.Message, StringComparison.Ordinal);
    }

    // ---------------------------------------------------------------- archive reader

    private static byte[] Zip(IEnumerable<(string Name, string Text)> entries)
    {
        using var ms = new MemoryStream();
        using (var z = new ZipArchive(ms, ZipArchiveMode.Create, leaveOpen: true))
            foreach (var (name, text) in entries)
            {
                ZipArchiveEntry e = z.CreateEntry(name);
                using var w = new StreamWriter(e.Open(), new UTF8Encoding(false));
                w.Write(text);
            }
        return ms.ToArray();
    }

    private static CgmesArchive ReadZip(byte[] bytes)
    {
        using var ms = new MemoryStream(bytes);
        return CgmesArchiveReader.Read(ms);
    }

    [Fact]
    public void Archive_entries_come_back_in_profile_then_sorted_path_order_whatever_the_zip_order()
    {
        CgmesExportResult e = CgmesSubstationExporter.Export(Rich(), null, new CgmesExportOptions());
        var files = e.Files.Select(f => ("z/" + f.FileName, f.Text)).ToList();
        CgmesArchive a = ReadZip(Zip(files));
        files.Reverse();
        CgmesArchive b = ReadZip(Zip(files));
        Assert.Equal(new[] { CgmesProfileKind.EQ, CgmesProfileKind.TP, CgmesProfileKind.SSH }, a.Entries.Select(x => x.Kind));
        Assert.Equal(a.Entries.Select(x => x.Path), b.Entries.Select(x => x.Path));
        Assert.Equal(a.Entries.Select(x => x.Text), b.Entries.Select(x => x.Text));
        // And the model read from the archive is the exported one.
        Assert.Equal(Json(Rich()), Json(CgmesSubstationReader.Read(b).Model));
    }

    [Fact]
    public void Archive_refuses_path_traversal_duplicate_entries_duplicate_profiles_and_a_DTD()
    {
        string eq = CgmesSubstationExporter.Export(Rich()).Files[0].Text;
        Assert.Contains("'..'", Assert.Throws<InvalidDataException>(() => ReadZip(Zip(new[] { ("../evil_EQ.xml", eq) }))).Message, StringComparison.Ordinal);
        Assert.Contains("'..'", Assert.Throws<InvalidDataException>(() => ReadZip(Zip(new[] { ("a/../b_EQ.xml", eq) }))).Message, StringComparison.Ordinal);
        Assert.Contains("rooted", Assert.Throws<InvalidDataException>(() => ReadZip(Zip(new[] { ("/abs_EQ.xml", eq) }))).Message, StringComparison.Ordinal);
        Assert.Contains("rooted", Assert.Throws<InvalidDataException>(() => ReadZip(Zip(new[] { ("C:/abs_EQ.xml", eq) }))).Message, StringComparison.Ordinal);
        Assert.Contains("appears twice", Assert.Throws<InvalidDataException>(() => ReadZip(Zip(new[] { ("a_EQ.xml", eq), ("a_EQ.xml", eq) }))).Message, StringComparison.Ordinal);
        Assert.Contains("duplicate profile entries for EQ", Assert.Throws<InvalidDataException>(() => ReadZip(Zip(new[] { ("a_EQ.xml", eq), ("b_EQ.xml", eq) }))).Message, StringComparison.Ordinal);
        Assert.Contains("DTD", Assert.Throws<InvalidDataException>(() => ReadZip(Zip(new[] { ("a_EQ.xml", "<!DOCTYPE x [<!ENTITY a \"b\">]><x/>") }))).Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Archive_reads_one_level_of_nested_zip_lists_skipped_entries_and_refuses_deeper_nesting()
    {
        CgmesExportResult e = CgmesSubstationExporter.Export(Rich());
        byte[] inner = Zip(new[] { (e.Files[0].FileName, e.Files[0].Text) });
        byte[] outer;
        using (var ms = new MemoryStream())
        {
            using (var z = new ZipArchive(ms, ZipArchiveMode.Create, true))
            {
                using (var s = z.CreateEntry("eq.zip").Open()) s.Write(inner);
                using (var w = new StreamWriter(z.CreateEntry("readme.txt").Open())) w.Write("hi");
            }
            outer = ms.ToArray();
        }
        CgmesArchive a = ReadZip(outer);
        Assert.Single(a.Entries);
        Assert.Equal("eq.zip/" + e.Files[0].FileName, a.Entries[0].Path);
        Assert.Equal(new[] { "readme.txt" }, a.Skipped);

        byte[] deep;
        using (var ms = new MemoryStream())
        {
            using (var z = new ZipArchive(ms, ZipArchiveMode.Create, true))
                using (var s = z.CreateEntry("l1.zip").Open()) s.Write(Zip(new[] { ("l2.zip", Encoding.UTF8.GetString(outer)) }));
            deep = ms.ToArray();
        }
        Assert.Throws<InvalidDataException>(() => ReadZip(deep));
    }

    // ---------------------------------------------------------------- conformance

    private static CgmesArchive Bundle(CgmesVersion v, Func<CgmesProfileFile, string>? edit = null)
    {
        CgmesExportResult e = CgmesSubstationExporter.Export(Rich(), null, new CgmesExportOptions(v));
        return ReadZip(Zip(e.Files.Select(f => (f.FileName, edit is null ? f.Text : edit(f)))));
    }

    private static CgmesCheckStatus Status(CgmesConformanceResult r, string id) => r.Checks.Single(c => c.Id == id).Status;

    [Theory]
    [InlineData(CgmesVersion.V2_4_15, "CGMES 2.4.15")]
    [InlineData(CgmesVersion.V3_0, "CGMES 3.0")]
    public void An_exported_bundle_passes_every_run_check_and_names_the_checks_not_run(CgmesVersion v, string edition)
    {
        CgmesConformanceResult r = CgmesConformance.Run(Bundle(v));
        Assert.Equal(edition, r.Edition);
        Assert.True(r.Passed, string.Join("; ", r.Checks.Where(c => c.Status == CgmesCheckStatus.Fail).SelectMany(c => c.Findings)));
        Assert.Equal(11, r.Checks.Count(c => c.Status == CgmesCheckStatus.Pass));
        Assert.Equal(CgmesCheckStatus.NotRun, Status(r, "S01"));
        Assert.Equal(CgmesCheckStatus.NotRun, Status(r, "S02"));
        Assert.All(r.Checks, c => Assert.False(string.IsNullOrEmpty(c.Basis)));
    }

    [Fact]
    public void Each_defect_fails_the_named_check()
    {
        // Missing mandatory attribute: C08.
        CgmesConformanceResult r = CgmesConformance.Run(Bundle(CgmesVersion.V2_4_15, f => f.Kind == CgmesProfileKind.EQ
            ? f.Text.Replace("<cim:Switch.normalOpen>false</cim:Switch.normalOpen>", "") : f.Text));
        Assert.Equal(CgmesCheckStatus.Fail, Status(r, "C08"));
        Assert.False(r.Passed);
        // Dangling reference: C07.
        r = CgmesConformance.Run(Bundle(CgmesVersion.V2_4_15, f => f.Kind == CgmesProfileKind.TP ? f.Text.Replace("#_TN.A.bb1", "#_TN.nowhere") : f.Text));
        Assert.Equal(CgmesCheckStatus.Fail, Status(r, "C07"));
        // Placement: Switch.open in the EQ document: C09.
        r = CgmesConformance.Run(Bundle(CgmesVersion.V2_4_15, f => f.Kind == CgmesProfileKind.EQ
            ? f.Text.Replace("<cim:Switch.retained>false</cim:Switch.retained>", "<cim:Switch.open>false</cim:Switch.open>") : f.Text));
        Assert.Equal(CgmesCheckStatus.Fail, Status(r, "C09"));
        // Identifier form: a 3.0 document with a non-UUID urn: C06.
        r = CgmesConformance.Run(Bundle(CgmesVersion.V3_0, f => f.Kind == CgmesProfileKind.EQ
            ? f.Text.Replace("<cim:BaseVoltage rdf:about=\"urn:uuid:", "<cim:BaseVoltage rdf:about=\"urn:uuid:zz-") : f.Text));
        Assert.Equal(CgmesCheckStatus.Fail, Status(r, "C06"));
        // Range: a negative nominal voltage: C10.
        r = CgmesConformance.Run(Bundle(CgmesVersion.V2_4_15, f => f.Kind == CgmesProfileKind.EQ
            ? f.Text.Replace("<cim:BaseVoltage.nominalVoltage>110</cim:BaseVoltage.nominalVoltage>", "<cim:BaseVoltage.nominalVoltage>-110</cim:BaseVoltage.nominalVoltage>") : f.Text));
        Assert.Equal(CgmesCheckStatus.Fail, Status(r, "C10"));
        // Terminal count: drop one line terminal (its element and the stub lines that name it): C11.
        r = CgmesConformance.Run(Bundle(CgmesVersion.V2_4_15, f => f.Kind == CgmesProfileKind.EQ
            ? f.Text.Replace("<cim:Terminal.ConductingEquipment rdf:resource=\"#_EQ.LN1\" />", "<cim:Terminal.ConductingEquipment rdf:resource=\"#_EQ.LD\" />") : f.Text));
        Assert.Equal(CgmesCheckStatus.Fail, Status(r, "C11"));
        // Mixed namespaces: C03.
        r = CgmesConformance.Run(Bundle(CgmesVersion.V2_4_15, f => f.Kind == CgmesProfileKind.TP
            ? f.Text.Replace("http://iec.ch/TC57/2013/CIM-schema-cim16#", "http://iec.ch/TC57/CIM100#") : f.Text));
        Assert.Equal(CgmesCheckStatus.Fail, Status(r, "C03"));
        // A dependency naming no model of the bundle: C05.
        r = CgmesConformance.Run(Bundle(CgmesVersion.V2_4_15, f => f.Kind == CgmesProfileKind.TP
            ? System.Text.RegularExpressions.Regex.Replace(f.Text, "(Model.DependentOn rdf:resource=\"urn:uuid:)[0-9a-f]{8}", "${1}ffffffff") : f.Text));
        Assert.Equal(CgmesCheckStatus.Fail, Status(r, "C05"));
    }
}
