using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using GridSim.Core.IO.Cim;
using GridSim.Core.IO.Cim.Profiles;
using GridSim.Core.IO.Cim.Shacl;
using GridSim.Core.IO.Psse;
using GridSim.Core.Model;
using GridSim.Core.Model.Substations;
using GridSim.Core.PowerFlow;
using GridSim.Tests.Substations;
using Xunit;

namespace GridSim.Tests;

/// <summary>
/// RC5 batch-2 verify (2.1.130), the WP24 review's items on the CGMES 3.0 export and the SHACL checker:
/// <list type="bullet">
/// <item>every sh:sparql constraint the checker meets is listed NOT CHECKED by shape, and a check with one that applies is
/// never a pass (no SPARQL engine is part of GridSim);</item>
/// <item>the bus-branch 3.0 EQ gives every line its BaseVoltage and a Line container, and names CoreEquipment-EU only;</item>
/// <item>the 3.0 SC document leaves PowerTransformerEnd.connectionKind out (an EQ attribute in 3.0) and lists the
/// omission; the 2.4.15 SC document keeps it;</item>
/// <item>the node-breaker 3.0 three-winding tap changers carry neutralU, ltcFlag and, for a phase changer, xMin / xMax;
/// the 2.4.15 document is byte for byte what the 2.1.129 build wrote;</item>
/// <item>the spec's projected-solve test: the node-breaker projection of a three-winding unit solves to the same bits as
/// the PSS/E RAW reader's three-winding record of the same unit.</item>
/// </list>
/// </summary>
public sealed class Rc5Batch2VerifyCgmesTests
{
    private static string Sha(string s) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(s))).ToLowerInvariant();

    private const string Shapes = """
        @prefix sh: <http://www.w3.org/ns/shacl#> .
        @prefix ex: <http://example.org/s#> .
        ex:Counted a sh:NodeShape ; sh:targetClass ex:C ; sh:property ex:p .
        ex:p a sh:PropertyShape ; sh:path ex:q ; sh:minCount 1 .
        ex:Sparql a sh:NodeShape ; sh:targetClass ex:C ;
            sh:sparql [ sh:select "SELECT $this WHERE { $this ex:q ?v . FILTER (?v > 1) }" ] .
        ex:SparqlElsewhere a sh:NodeShape ; sh:targetClass ex:Absent ;
            sh:sparql [ sh:select "SELECT $this WHERE { $this ex:r ?v }" ] .
        """;

    private static RdfGraph Graph(string turtle, string name)
    {
        var g = new RdfGraph();
        TurtleReader.Read(g, turtle, name);
        return g;
    }

    [Fact]
    public void Every_sparql_constraint_met_is_not_checked_by_shape_and_never_a_pass()
    {
        RdfGraph shapes = Graph(Shapes, "s.ttl");
        // Every Core constraint passes on this data (ex:q is there); the SPARQL constraint on the same class cannot be checked.
        ShaclReport r = ShaclValidator.Validate(Graph("@prefix ex: <http://example.org/s#> .\nex:a a ex:C ; ex:q 2 .\n", "d.ttl"), shapes);
        Assert.Empty(r.Results);
        Assert.Equal(ShaclOutcome.NotRun, r.Outcome);
        Assert.False(r.Conforms);
        Assert.Equal("not determined", r.Verdict);
        // Both SPARQL shapes are listed by name, also the one whose target selects nothing here: never skipped in silence.
        var sparql = r.NotRun.Where(n => n.What.StartsWith("sh:sparql", StringComparison.Ordinal)).ToList();
        Assert.Equal(new[] { "<http://example.org/s#Sparql>", "<http://example.org/s#SparqlElsewhere>" }, sparql.Select(n => n.Shape).Order(StringComparer.Ordinal));
        Assert.All(sparql, n => Assert.StartsWith("NOT CHECKED: ", n.Reason, StringComparison.Ordinal));
        Assert.Contains("NOT CHECKED", r.ToCsv(), StringComparison.Ordinal);
        Assert.Contains("NOT CHECKED", r.ToJson(), StringComparison.Ordinal);
        // A Core result still fails the check, and the SPARQL constraint is still listed NOT CHECKED beside it.
        ShaclReport f = ShaclValidator.Validate(Graph("@prefix ex: <http://example.org/s#> .\nex:a a ex:C .\n", "d.ttl"), shapes);
        Assert.Equal(ShaclOutcome.Fail, f.Outcome);
        Assert.Equal(2, f.NotRun.Count(n => n.What.StartsWith("sh:sparql", StringComparison.Ordinal)));
        Assert.Equal(2, ShaclValidator.Census(shapes).SparqlConstraints);
    }

    [Fact]
    public void Bus_branch_3_0_lines_carry_their_base_voltage_and_a_line_container_and_eq_names_core_equipment_only()
    {
        GridModel m = S10TestSupport.LoadMatpower("lib/case9.m").Model;
        CgmesBusBranch30Result w = CgmesBusBranch30Writer.Write(m);
        string eq = w.Files[0].Text;
        Assert.Contains("<md:Model.profile>http://iec.ch/TC57/ns/CIM/CoreEquipment-EU/3.0</md:Model.profile>", eq, StringComparison.Ordinal);
        Assert.DoesNotContain("Operation-EU", eq, StringComparison.Ordinal);
        MatchCollection lines = Regex.Matches(eq, "<cim:ACLineSegment rdf:about=\"[^\"]+\">(.*?)</cim:ACLineSegment>", RegexOptions.Singleline);
        Assert.Equal(w.Source.Network.Branches.Count, lines.Count);
        Assert.True(lines.Count > 0);
        foreach (Match l in lines)
        {
            Assert.Contains("<cim:ConductingEquipment.BaseVoltage rdf:resource=", l.Value, StringComparison.Ordinal);
            Assert.Contains("<cim:Equipment.EquipmentContainer rdf:resource=", l.Value, StringComparison.Ordinal);
        }
        Assert.Equal(lines.Count, Regex.Count(eq, "<cim:Line rdf:about="));
        Assert.Equal(lines.Count, Regex.Count(eq, "<cim:Line.Region rdf:resource="));
        // Read back, the model is the one the 2.4.15 document gives (the containers change nothing the reader uses).
        GridModel back30 = CgmesBusBranch30Writer.ReadBack(w.Files.Select(f => f.Text), m.BaseMva, "back");
        GridModel back24 = CgmesBusBranch30Writer.ReadBack(new[] { CgmesWriter.Write(m).Text }, m.BaseMva, "back");
        Assert.Equal(back24.Branches, back30.Branches);
        Assert.Equal(back24.Buses, back30.Buses);
    }

    private static GridModel Transformer()
    {
        double zbHv = 345.0 * 345.0 / 100.0;
        return new GridModel
        {
            BaseMva = 100, Name = "tx2",
            Buses = new[] { new Bus(1, BusType.Slack, BaseKv: 345), new Bus(2, BusType.Pq, Pd: 10, Qd: 2, BaseKv: 20) },
            Generators = new[] { new Generator(1, 10, Vg: 1.0) },
            Branches = new[] { new Branch(1, 2, 0.001, 0.1, IsTransformer: true, R0: 0.002, X0: 0.09, FromWinding: WindingConnection.WyeGrounded,
                ToWinding: WindingConnection.Delta, FromNeutralXPu: 10.0 / zbHv) },
        };
    }

    [Fact]
    public void Sc_3_0_leaves_connection_kind_to_the_equipment_profile_and_lists_it()
    {
        GridModel tx = Transformer();
        CgmesExtraResult x30 = CgmesExtraProfiles.Write(tx, CgmesBusBranch30Writer.Write(tx), new CgmesExtraOptions(new[] { CgmesProfileKind.SC }, CgmesVersion.V3_0));
        string sc30 = Assert.Single(x30.Files).Text;
        Assert.DoesNotContain("connectionKind", sc30, StringComparison.Ordinal);
        // The SC attributes of the ends stay: grounded on both, the neutral reactance on the grounded wye, the zero sequence.
        Assert.Equal(2, Regex.Count(sc30, "<cim:TransformerEnd.grounded>"));
        Assert.Contains("<cim:TransformerEnd.grounded>true</cim:TransformerEnd.grounded>", sc30, StringComparison.Ordinal);
        Assert.Contains("<cim:TransformerEnd.grounded>false</cim:TransformerEnd.grounded>", sc30, StringComparison.Ordinal);
        Assert.Contains("<cim:TransformerEnd.xground>", sc30, StringComparison.Ordinal);
        Assert.Contains("<cim:PowerTransformerEnd.x0>", sc30, StringComparison.Ordinal);
        CgmesOmission o = Assert.Single(x30.Omissions, o => o.Item == "PowerTransformerEnd.connectionKind");
        Assert.Equal(CgmesProfileKind.SC, o.Profile);
        Assert.StartsWith("2 transformer end(s) carry a winding connection", o.Reason, StringComparison.Ordinal);
        // 2.4.15 keeps it in SC, as before.
        CgmesExtraResult x24 = CgmesExtraProfiles.Write(tx, CgmesWriter.Write(tx), new CgmesExtraOptions(new[] { CgmesProfileKind.SC }));
        string sc24 = Assert.Single(x24.Files).Text;
        Assert.Equal(2, Regex.Count(sc24, "<cim:PowerTransformerEnd.connectionKind "));
        Assert.DoesNotContain(x24.Omissions, o => o.Item == "PowerTransformerEnd.connectionKind");
    }

    private static SubstationModel ThreeWinding() => new()
    {
        Name = "t3", BaseMva = 100,
        Substations = Rc5Wp24Cgmes30Tests.ThreeWinding().Substations,
        Transformers3W = new[] { new NbTransformer3W("T", "h", "m", "l", 0, 0.1, 0, 0.2, 0, 0.15, B: -0.001, Ratio: new[] { 1.025, 1.0, 0.975 }, ShiftDeg: new[] { 0.0, 5.0, 0.0 }) },
        Generators = new[] { new NbGenerator("G", "h", 0, Slack: true), new NbGenerator("G2", "m", 5) },
        Loads = new[] { new NbLoad("D", "l", 10, 2), new NbLoad("D2", "h", 1), new NbLoad("D3", "m", 1) },
    };

    private static string All(CgmesExportResult e) => string.Concat(e.Files.Select(f => f.FileName + "\n" + f.Text + "\n--\n"));

    [Fact]
    public void Three_winding_tap_changers_carry_what_3_0_requires_and_2_4_15_is_unchanged()
    {
        SubstationModel sub = ThreeWinding();
        // 2.4.15: every file, pinned from the 2.1.129 Release Core dll before this change (a scratch F# probe on the same model).
        CgmesExportResult e24 = CgmesSubstationExporter.Export(sub, new Dictionary<string, bool>(), new CgmesExportOptions(CgmesVersion.V2_4_15));
        Assert.Equal("cbb727dc4feed3c69a5fe8870952f66de43f30394222bc788debb6bd14a7a7c2", Sha(All(e24)));
        Assert.DoesNotContain("TapChanger.neutralU", All(e24), StringComparison.Ordinal);

        CgmesExportResult e30 = CgmesSubstationExporter.Export(sub, new Dictionary<string, bool>(), new CgmesExportOptions(CgmesVersion.V3_0));
        string eq = e30.Files.Single(f => f.Kind == CgmesProfileKind.EQ).Text;
        // Ratio changers on windings 1 and 3 (1.025, 0.975), a phase changer on winding 2 (5 degrees).
        MatchCollection rtc = Regex.Matches(eq, "<cim:RatioTapChanger [^>]*>(.*?)</cim:RatioTapChanger>", RegexOptions.Singleline);
        Match ptc = Assert.Single(Regex.Matches(eq, "<cim:PhaseTapChangerLinear [^>]*>(.*?)</cim:PhaseTapChangerLinear>", RegexOptions.Singleline));
        Assert.Equal(2, rtc.Count);
        Assert.Contains("<cim:TapChanger.neutralU>400</cim:TapChanger.neutralU>", rtc[0].Value + rtc[1].Value, StringComparison.Ordinal);
        Assert.Contains("<cim:TapChanger.neutralU>33</cim:TapChanger.neutralU>", rtc[0].Value + rtc[1].Value, StringComparison.Ordinal);
        foreach (Match c in rtc.Append(ptc)) Assert.Contains("<cim:TapChanger.ltcFlag>false</cim:TapChanger.ltcFlag>", c.Value, StringComparison.Ordinal);
        Assert.Contains("<cim:TapChanger.neutralU>132</cim:TapChanger.neutralU>", ptc.Value, StringComparison.Ordinal);
        // xMin = xMax = winding 2's own reactance in ohms, the value its end carries: a flat curve.
        string xEnd = Regex.Match(eq, "gridsim-id=T\\.2</cim:IdentifiedObject.description>.*?<cim:PowerTransformerEnd.x>([^<]+)<", RegexOptions.Singleline).Groups[1].Value;
        Assert.NotEmpty(xEnd);
        Assert.Contains($"<cim:PhaseTapChangerLinear.xMin>{xEnd}</cim:PhaseTapChangerLinear.xMin>", ptc.Value, StringComparison.Ordinal);
        Assert.Contains($"<cim:PhaseTapChangerLinear.xMax>{xEnd}</cim:PhaseTapChangerLinear.xMax>", ptc.Value, StringComparison.Ordinal);
        // The reader takes the unit back with its three ends (as before; xMin and xMax are not read as anything else).
        CimTransformer t = Assert.Single(CimImporter.Parse(e30.Files.Select(f => f.Text)).Transformers);
        Assert.Equal(3, t.Ends.Count);
    }

    private static string R(double v) => v.ToString("R", CultureInfo.InvariantCulture);

    /// <summary>
    /// The spec's test 6: the node-breaker projection of a three-winding unit (Z12 = j0.1, Z13 = j0.2, Z23 = j0.15,
    /// magnetising B -0.001 pu) solves to the same bits as the PSS/E RAW reader's three-winding record of the same unit. The
    /// RAW file is written from the projected model's own buses, loads and machines (numbered h 1, l 2, m 3 as the projection
    /// numbers them; windings I = h, J = m, K = l, so both give the windings in the order 1-star, 3-star, 2-star), so the two
    /// models differ only in how the three-winding unit was read. The RAW reader numbers its star bus 11 (the next power of
    /// ten above the highest bus) where the projection numbers it 4; both put it last, so the solver's bus order is the
    /// same. Both are solved from a flat start with no Q limits (RAW holds no infinite limit): Vm and Va equal bit for bit.
    /// </summary>
    [Fact]
    public void The_projected_three_winding_unit_solves_to_the_raw_readers_bits()
    {
        SubstationModel sub = Rc5Wp24Cgmes30Tests.ThreeWinding();
        GridModel proj = SubstationProjection.Project(sub, new Dictionary<string, bool>()).Model;
        Assert.Equal(new[] { 1, 2, 3, 4 }, proj.Buses.Select(b => b.Id));
        var raw = new StringBuilder("0, 100, 33, 0, 1, 60     / PSS/E-33 RAW\nt3\nprojected-solve test\n");
        foreach (Bus b in proj.Buses.Where(b => b.Id <= 3))
            raw.Append(CultureInfo.InvariantCulture, $"{b.Id}, '', {R(b.BaseKv)}, {(b.Type == BusType.Slack ? 3 : b.Type == BusType.Pv ? 2 : 1)}, 1, 1, 1, 1, 0, 1.1, 0.9, 1.1, 0.9\n");
        raw.Append("0 / END OF BUS DATA, BEGIN LOAD DATA\n");
        foreach (Bus b in proj.Buses.Where(b => b.Id <= 3 && (b.Pd != 0 || b.Qd != 0)))
            raw.Append(CultureInfo.InvariantCulture, $"{b.Id}, '1', 1, 1, 1, {R(b.Pd)}, {R(b.Qd)}, 0, 0, 0, 0, 1, 1, 0\n");
        raw.Append("0 / END OF LOAD DATA, BEGIN FIXED SHUNT DATA\n0 / END OF FIXED SHUNT DATA, BEGIN GENERATOR DATA\n");
        foreach (Generator g in proj.Generators)
            raw.Append(CultureInfo.InvariantCulture, $"{g.Bus}, '1', {R(g.Pg)}, 0, 9999, -9999, {R(g.Vg)}, 0, 100, 0, 1, 0, 0, 1, 1, 100, 9999, -9999, 1, 1\n");
        raw.Append("0 / END OF GENERATOR DATA, BEGIN BRANCH DATA\n0 / END OF BRANCH DATA, BEGIN TRANSFORMER DATA\n");
        // I = h (1), J = m (3), K = l (2): Z12 = h-m = j0.1, Z23 = m-l = j0.15, Z31 = l-h = j0.2; CW 1, CZ 1, CM 1.
        raw.Append("1, 3, 2, '1', 1, 1, 1, 0, -0.001, 2, 'T', 1, 1, 1\n");
        raw.Append("0, 0.1, 100, 0, 0.15, 100, 0, 0.2, 100, 1, 0\n");
        for (int n = 0; n < 3; n++) raw.Append("1, 0, 0, 0, 0, 0, 0, 0, 1.1, 0.9, 1.1, 0.9, 33, 0, 0, 0, 0\n");
        foreach (string s in new[] { "TRANSFORMER", "AREA", "TWO-TERMINAL DC", "VOLTAGE SOURCE CONVERTER", "IMPEDANCE CORRECTION", "MULTI-TERMINAL DC",
                     "MULTI-SECTION LINE", "ZONE", "INTER-AREA TRANSFER", "OWNER", "FACTS CONTROL DEVICE", "SWITCHED SHUNT", "GNE DEVICE", "INDUCTION MACHINE" })
            raw.Append("0 / END OF ").Append(s).Append(" DATA\n");
        raw.Append("Q\n");
        GridModel fromRaw = PsseRawParser.Parse(raw.ToString(), "t3");
        Assert.Equal(new[] { 1, 2, 3, 11 }, fromRaw.Buses.Select(b => b.Id));
        Assert.Equal(proj.Branches.Select(b => (b.From, b.R, b.X, b.TapRatio, b.PhaseShiftDeg)),
            fromRaw.Branches.Select(b => (b.From, b.R, b.X, b.TapRatio, b.PhaseShiftDeg)));
        Assert.Equal(proj.Buses.Select(b => (b.Pd, b.Qd, b.Gs, b.Bs)), fromRaw.Buses.Select(b => (b.Pd, b.Qd, b.Gs, b.Bs)));

        var options = new PowerFlowOptions { FlatStart = true, EnforceQLimits = false, Tolerance = 1e-12 };
        PowerFlowResult a = new NewtonRaphsonPowerFlow().Solve(proj, options);
        PowerFlowResult b2 = new NewtonRaphsonPowerFlow().Solve(fromRaw, options);
        Assert.True(a.Converged && b2.Converged);
        Assert.Equal(a.Vm, b2.Vm);
        Assert.Equal(a.VaDeg, b2.VaDeg);
    }
}
