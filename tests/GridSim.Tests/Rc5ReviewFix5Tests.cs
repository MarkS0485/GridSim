using System.Xml.Linq;
using GridSim.Core.IO.Cim;
using GridSim.Core.IO.Cim.Profiles;
using GridSim.Core.IO.Cim.Shacl;
using GridSim.Core.IO.Psse;
using GridSim.Core.Model;
using Xunit;

namespace GridSim.Tests;

/// <summary>
/// RC5 review fixes for WP10 (CGMES DY and SHACL).
///
/// DY: the expected attribute for each PSS/E CON position is written out here by hand, not taken from GridSim's tables.
/// Sources: the PSS/E model data sheets (CON order of GENROU, GENSAL, GENCLS, SEXS, SCRX, TGOV1) and the CGMES 2.4.15
/// dynamics profile (DynamicsProfileRDFSAugmented-v2_4_15-16Feb2016: the class and attribute names, with their
/// descriptions, for example tppdo "Direct-axis subtransient rotor time constant (T''do)" and ExcSCRX.rcrfd "Rc/Rfd").
/// Every value in a record is distinct, so a swap of any two positions changes a pinned literal.
///
/// SHACL: the three outcomes (pass, fail, not run) of W3C SHACL 2017 checks that include a component the native checker
/// does not run (sh:closed §4.8.1, sh:qualifiedValueShape §4.7.3), alone and nested in sh:not, sh:node, sh:or and sh:and.
/// The expected outcome follows from three-valued logic: a part that is not run can be neither a pass nor a violation.
/// </summary>
public sealed class Rc5ReviewFix5Tests
{
    private const string CimNs = "http://iec.ch/TC57/2013/CIM-schema-cim16#";

    // ---- DY: one literal per CON position ------------------------------------------------------------------------------

    private const string DistinctDyr =
        "1 'GENROU' '1' 7.4 0.03 0.4 0.05 5.148 0.5 1.8 1.75 0.3 0.65 0.23 0.15 0.09 0.38 /\n" +
        "1 'SEXS' '1' 0.1 10 100 0.05 -0.5 4 /\n" +
        "1 'TGOV1' '1' 0.05 0.49 1.1 0.1 2.1 7 0.2 /\n" +
        "2 'GENSAL' '1' 6.1 0.05 0.06 6.54 0.25 1.25 0.7 0.31 0.22 0.12 0.1 0.35 /\n" +
        "2 'SCRX' '1' 0.15 12 200 0.04 -5 6 1 10 /\n" +
        "3 'GENCLS' '1' 4.5 1.5 /\n";

    /// <summary>PSS/E CON order -> the CGMES attribute (class.attribute) that holds it, by hand from the two sources.</summary>
    public static TheoryData<string, string, string[]> ConPositions() => new()
    {
        { "GENROU 1 1", "SynchronousMachineTimeConstantReactance", new[]
            {
                "SynchronousMachineTimeConstantReactance.tpdo=7.4",        // J    T'do
                "SynchronousMachineTimeConstantReactance.tppdo=0.03",      // J+1  T''do
                "SynchronousMachineTimeConstantReactance.tpqo=0.4",        // J+2  T'qo
                "SynchronousMachineTimeConstantReactance.tppqo=0.05",      // J+3  T''qo
                "RotatingMachineDynamics.inertia=5.148",                   // J+4  H
                "RotatingMachineDynamics.damping=0.5",                     // J+5  D
                "SynchronousMachineTimeConstantReactance.xDirectSync=1.8", // J+6  Xd
                "SynchronousMachineTimeConstantReactance.xQuadSync=1.75",  // J+7  Xq
                "SynchronousMachineTimeConstantReactance.xDirectTrans=0.3",// J+8  X'd
                "SynchronousMachineTimeConstantReactance.xQuadTrans=0.65", // J+9  X'q
                "SynchronousMachineTimeConstantReactance.xDirectSubtrans=0.23", // J+10 X''d (= X''q)
                "SynchronousMachineTimeConstantReactance.xQuadSubtrans=0.23",
                "RotatingMachineDynamics.statorLeakageReactance=0.15",     // J+11 Xl
                "RotatingMachineDynamics.saturationFactor=0.09",           // J+12 S(1.0)
                "RotatingMachineDynamics.saturationFactor120=0.38",        // J+13 S(1.2)
            } },
        { "GENSAL 2 1", "SynchronousMachineTimeConstantReactance", new[]
            {
                "SynchronousMachineTimeConstantReactance.tpdo=6.1",        // J    T'do
                "SynchronousMachineTimeConstantReactance.tppdo=0.05",      // J+1  T''do
                "SynchronousMachineTimeConstantReactance.tppqo=0.06",      // J+2  T''qo
                "RotatingMachineDynamics.inertia=6.54",                    // J+3  H
                "RotatingMachineDynamics.damping=0.25",                    // J+4  D
                "SynchronousMachineTimeConstantReactance.xDirectSync=1.25",// J+5  Xd
                "SynchronousMachineTimeConstantReactance.xQuadSync=0.7",   // J+6  Xq
                "SynchronousMachineTimeConstantReactance.xDirectTrans=0.31",// J+7 X'd
                "SynchronousMachineTimeConstantReactance.xDirectSubtrans=0.22", // J+8 X''d (= X''q)
                "SynchronousMachineTimeConstantReactance.xQuadSubtrans=0.22",
                "RotatingMachineDynamics.statorLeakageReactance=0.12",     // J+9  Xl
                "RotatingMachineDynamics.saturationFactor=0.1",            // J+10 S(1.0)
                "RotatingMachineDynamics.saturationFactor120=0.35",        // J+11 S(1.2)
            } },
        { "GENCLS 3 1", "SynchronousMachineSimplified", new[]
            {
                "RotatingMachineDynamics.inertia=4.5",                     // J    H
                "RotatingMachineDynamics.damping=1.5",                     // J+1  D
            } },
        { "SEXS 1 1", "ExcSEXS", new[]
            {
                "ExcSEXS.tatb=0.1", "ExcSEXS.tb=10", "ExcSEXS.k=100", "ExcSEXS.te=0.05", "ExcSEXS.emin=-0.5", "ExcSEXS.emax=4",
                // J..J+5: TA/TB, TB, K, TE, EMIN, EMAX; PSS/E SEXS has no separate field limits, so efdmin/efdmax = EMIN/EMAX.
                "ExcSEXS.efdmin=-0.5", "ExcSEXS.efdmax=4", "ExcSEXS.kc=0", "ExcSEXS.tc=0",
            } },
        { "SCRX 2 1", "ExcSCRX", new[]
            {
                // J..J+7: TA/TB, TB, K, TE, EMIN, EMAX, CSWITCH (1 = solid fed = true), rc/rfd.
                "ExcSCRX.tatb=0.15", "ExcSCRX.tb=12", "ExcSCRX.k=200", "ExcSCRX.te=0.04", "ExcSCRX.emin=-5", "ExcSCRX.emax=6",
                "ExcSCRX.cswitch=true", "ExcSCRX.rcrfd=10",
            } },
        { "TGOV1 1 1", "GovSteam0", new[]
            {
                // J..J+6: R, T1, VMAX, VMIN, T2, T3, Dt; mwbase is the machine's MBASE (100 MVA in case14.raw).
                "GovSteam0.r=0.05", "GovSteam0.t1=0.49", "GovSteam0.vmax=1.1", "GovSteam0.vmin=0.1", "GovSteam0.t2=2.1",
                "GovSteam0.t3=7", "GovSteam0.dt=0.2", "GovSteam0.mwbase=100",
            } },
    };

    private static string Case14RawText() =>
        File.ReadAllText(Path.Combine(CliProcess.Root(), "tests", "GridSim.Tests", "fixtures", "psse", "case14.raw"));

    /// <summary>case14.raw with machine 1's ZSORCE resistance ZR set to <paramref name="zr"/> (the 10th field, rev 33).</summary>
    private static PsseParseResult Case14WithZr(string zr)
    {
        const string line1 = "1, '1', 232.4, -16.9, 10, 0, 1.06, 0, 100, 0, 1, ";
        string text = Case14RawText();
        Assert.Contains(line1, text, StringComparison.Ordinal);
        return PsseRawParser.ParseWithDiagnostics(text.Replace(line1, "1, '1', 232.4, -16.9, 10, 0, 1.06, 0, 100, " + zr + ", 1, ", StringComparison.Ordinal), "case14");
    }

    private static GridModel WithBaseKv(PsseParseResult raw) =>
        raw.Model.CopyWith(buses: raw.Model.Buses.Select(b => b with { BaseKv = 138 }).ToList());

    private static string Dy(PsseParseResult raw, string dyr, bool withRa = true)
    {
        GridModel m = WithBaseKv(raw);
        CgmesExtraResult x = CgmesExtraProfiles.Write(m, CgmesWriter.Write(m), new CgmesExtraOptions(new[] { CgmesProfileKind.DY },
            Dynamics: PsseDyrReader.ReadRecords(dyr), MachineKeys: CgmesDynamicsProfile.KeysOf(raw),
            MachineArmatureResistances: withRa ? CgmesDynamicsProfile.ArmatureResistancesOf(raw) : null));
        return Assert.Single(x.Files).Text;
    }

    /// <summary>The literal of every child of the object named <paramref name="name"/>, by local name.</summary>
    private static Dictionary<string, string> Literals(string dy, string name, string cls)
    {
        XNamespace cim = CimNs;
        XElement o = Assert.Single(XDocument.Parse(dy).Root!.Elements(cim + cls),
            e => (string?)e.Element(cim + "IdentifiedObject.name") == name);
        return o.Elements().Where(e => !e.HasAttributes).ToDictionary(e => e.Name.LocalName, e => e.Value, StringComparer.Ordinal);
    }

    [Theory]
    [MemberData(nameof(ConPositions))]
    public void Dy_writes_each_con_position_to_its_published_cgmes_attribute(string name, string cls, string[] expected)
    {
        Dictionary<string, string> lit = Literals(Dy(Case14WithZr("0"), DistinctDyr), name, cls);
        foreach (string e in expected)
        {
            string attr = e[..e.IndexOf('=', StringComparison.Ordinal)], value = e[(e.IndexOf('=', StringComparison.Ordinal) + 1)..];
            Assert.True(lit.TryGetValue(attr, out string? got), $"{name}: {attr} missing");
            Assert.Equal(value, got);
        }
    }

    [Fact]
    public void Dy_round_trip_of_the_distinct_records_gives_the_same_dyr_text()
    {
        PsseParseResult raw = Case14WithZr("0");
        string dy = Dy(raw, DistinctDyr);
        CgmesDynamicsImport imp = CgmesDynamicsProfile.Read(new[] { dy }, CgmesDynamicsProfile.MachinesOf(WithBaseKv(raw), CgmesDynamicsProfile.KeysOf(raw)));
        // The import writes machine, exciter, governor per machine in document order: the same order as DistinctDyr.
        Assert.Equal(DistinctDyr, imp.DyrText);
    }

    [Fact]
    public void Dy_writes_the_raw_zsorce_resistance_as_stator_resistance_and_reads_it_back_only_when_it_matches()
    {
        // PSS/E machine models take the armature resistance from ZSORCE's ZR (pu on MBASE); CgmesWriter writes ratedS = MBase,
        // so the DY value is ZR unchanged.
        PsseParseResult raw = Case14WithZr("0.0025");
        Assert.Equal(0.0025, raw.MachineSourceResistances[0]);
        Assert.Equal(0.0, raw.MachineSourceResistances[1]);
        const string dyr = "1 'GENROU' '1' 7.4 0.03 0.4 0.05 5.148 0.5 1.8 1.75 0.3 0.65 0.23 0.15 0.09 0.38 /\n3 'GENCLS' '1' 4.5 1.5 /\n";
        string dy = Dy(raw, dyr);
        Assert.Equal("0.0025", Literals(dy, "GENROU 1 1", "SynchronousMachineTimeConstantReactance")["RotatingMachineDynamics.statorResistance"]);
        Assert.Equal("0", Literals(dy, "GENCLS 3 1", "SynchronousMachineSimplified")["RotatingMachineDynamics.statorResistance"]);

        GridModel m = WithBaseKv(raw);
        CgmesDynamicsImport ok = CgmesDynamicsProfile.Read(new[] { dy },
            CgmesDynamicsProfile.MachinesOf(m, CgmesDynamicsProfile.KeysOf(raw), CgmesDynamicsProfile.ArmatureResistancesOf(raw)));
        Assert.Equal(dyr, ok.DyrText);

        var ex = Assert.Throws<InvalidDataException>(() => CgmesDynamicsProfile.Read(new[] { dy }, CgmesDynamicsProfile.MachinesOf(m, CgmesDynamicsProfile.KeysOf(raw))));
        Assert.Contains("RotatingMachineDynamics.statorResistance = 0.0025 differs from the machine's armature resistance 0", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Dy_is_refused_without_the_armature_resistances_or_with_a_negative_one()
    {
        var e1 = Assert.Throws<InvalidDataException>(() => Dy(Case14WithZr("0"), DistinctDyr, withRa: false));
        Assert.Contains("needs the armature resistance (the RAW file's ZSORCE resistance ZR)", e1.Message, StringComparison.Ordinal);
        var e2 = Assert.Throws<InvalidDataException>(() => Dy(Case14WithZr("-0.01"), DistinctDyr));
        Assert.Contains("machine '1' at bus 1: its ZSORCE resistance ZR is -0.01", e2.Message, StringComparison.Ordinal);
    }

    // ---- SHACL: three outcomes -----------------------------------------------------------------------------------------

    private const string Prefixes = """
        @prefix sh: <http://www.w3.org/ns/shacl#> .
        @prefix cim: <http://iec.ch/TC57/2013/CIM-schema-cim16#> .
        @prefix rdf: <http://www.w3.org/1999/02/22-rdf-syntax-ns#> .
        @prefix ex: <http://example.org/shapes#> .

        """;

    private static ShaclReport Validate(string shapesBody)
    {
        var data = new RdfGraph();
        RdfXmlReader.Read(data, Rc5Wp10ShaclTests.Data, "data.xml");
        var shapes = new RdfGraph();
        TurtleReader.Read(shapes, Prefixes + shapesBody, "s.ttl");
        return ShaclValidator.Validate(data, shapes);
    }

    // On _L1: r is present (one value), so "r min 1" passes and "r max 0" fails.
    private const string Passing = "[ sh:path cim:ACLineSegment.r ; sh:minCount 1 ]";
    private const string Failing = "[ sh:path cim:ACLineSegment.r ; sh:maxCount 0 ]";
    private const string Closed = "[ sh:closed true ; sh:ignoredProperties ( rdf:type ) ]";

    [Fact]
    public void Shacl_closed_alone_is_not_run_and_never_a_pass()
    {
        ShaclReport r = Validate("ex:S a sh:NodeShape ; sh:targetNode cim:_L1 ; sh:closed true ; sh:ignoredProperties ( rdf:type ) .");
        Assert.Equal(ShaclOutcome.NotRun, r.Outcome);
        Assert.False(r.Conforms);
        Assert.Empty(r.Results);
        Assert.Contains(r.NotRun, n => n.What == "sh:closed");
        Assert.StartsWith("not determined", r.Summary, StringComparison.Ordinal);
    }

    [Fact]
    public void Shacl_qualified_value_shape_is_not_run_and_never_a_pass()
    {
        ShaclReport r = Validate("ex:S a sh:NodeShape ; sh:targetNode cim:_L1 ; sh:property [ sh:path cim:ACLineSegment.r ; sh:qualifiedValueShape [ sh:datatype <http://www.w3.org/2001/XMLSchema#float> ] ; sh:qualifiedMinCount 1 ] .");
        Assert.Equal(ShaclOutcome.NotRun, r.Outcome);
        Assert.Empty(r.Results);
        Assert.Contains(r.NotRun, n => n.What == "sh:qualifiedValueShape");
    }

    [Fact]
    public void Shacl_not_of_a_shape_not_run_is_not_a_violation()
    {
        // Before the fix the inner shape "conformed" (nothing run), so sh:not reported a false violation.
        ShaclReport r = Validate($"ex:S a sh:NodeShape ; sh:targetNode cim:_L1 ; sh:not {Closed} .");
        Assert.Equal(ShaclOutcome.NotRun, r.Outcome);
        Assert.Empty(r.Results);
        Assert.Contains(r.NotRun, n => n.Shape == "<http://example.org/shapes#S>" && n.What.StartsWith("sh:not ", StringComparison.Ordinal));
    }

    [Fact]
    public void Shacl_node_of_a_shape_not_run_is_not_a_pass()
    {
        // Before the fix sh:node of the closed shape passed.
        ShaclReport r = Validate($"ex:S a sh:NodeShape ; sh:targetNode cim:_L1 ; sh:node {Closed} .");
        Assert.Equal(ShaclOutcome.NotRun, r.Outcome);
        Assert.Empty(r.Results);
        Assert.Contains(r.NotRun, n => n.Shape == "<http://example.org/shapes#S>" && n.What.StartsWith("sh:node ", StringComparison.Ordinal));

        // A shape that fails on a run constraint fails, whatever else it holds: sh:node then reports a definite violation.
        ShaclReport f = Validate($"ex:S a sh:NodeShape ; sh:targetNode cim:_L1 ; sh:node [ sh:closed true ; sh:property {Failing} ] .");
        Assert.Equal(ShaclOutcome.Fail, f.Outcome);
        Assert.Equal("sh:NodeConstraintComponent", Assert.Single(f.Results).Component);
    }

    [Theory]
    // sh:or: one passing member decides it; otherwise a member not run leaves it undecided.
    [InlineData("or", Closed + " " + Passing, ShaclOutcome.Pass, "")]
    [InlineData("or", Closed + " " + Failing, ShaclOutcome.NotRun, "")]
    [InlineData("or", Failing + " " + Failing, ShaclOutcome.Fail, "sh:OrConstraintComponent")]
    // sh:and: one failing member decides it; otherwise a member not run leaves it undecided.
    [InlineData("and", Closed + " " + Failing, ShaclOutcome.Fail, "sh:AndConstraintComponent")]
    [InlineData("and", Closed + " " + Passing, ShaclOutcome.NotRun, "")]
    [InlineData("and", Passing + " " + Passing, ShaclOutcome.Pass, "")]
    // sh:xone: two passing members decide a failure; one pass and one not run is undecided.
    [InlineData("xone", Passing + " " + Passing + " " + Closed, ShaclOutcome.Fail, "sh:XoneConstraintComponent")]
    [InlineData("xone", Passing + " " + Closed, ShaclOutcome.NotRun, "")]
    public void Shacl_logical_constraints_follow_three_valued_logic(string op, string members, ShaclOutcome expected, string component)
    {
        ShaclReport r = Validate($"ex:S a sh:NodeShape ; sh:targetNode cim:_L1 ; sh:{op} ( {members} ) .");
        Assert.Equal(expected, r.Outcome);
        if (component.Length == 0) Assert.Empty(r.Results);
        else Assert.Equal(component, Assert.Single(r.Results).Component);
        bool undecidedListed = r.NotRun.Any(n => n.What.StartsWith("sh:" + op + " ", StringComparison.Ordinal));
        Assert.Equal(expected == ShaclOutcome.NotRun, undecidedListed);
    }

    [Fact]
    public void Shacl_cli_gives_no_pass_exit_for_a_check_not_determined()
    {
        string d = TestTemp.Under("gridsim-fix5-shacl-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(d, "bundle"));
        File.WriteAllText(Path.Combine(d, "bundle", "case_EQ.xml"), Rc5Wp10ShaclTests.Data);
        File.WriteAllText(Path.Combine(d, "closed.ttl"), Prefixes + $"ex:S a sh:NodeShape ; sh:targetNode cim:_L1 ; sh:node {Closed} .\n");
        CliProcess.Result r = CliProcess.RunBuilt("cgmes-shacl", Path.Combine(d, "bundle"), "--shapes", Path.Combine(d, "closed.ttl"), "--report", Path.Combine(d, "r.json"));
        Assert.True(r.ExitCode == 3, r.ToString());
        Assert.Contains(": not determined (SHACL Core subset", r.StdOut, StringComparison.Ordinal);
        string json = File.ReadAllText(Path.Combine(d, "r.json"));
        Assert.Contains("\"conforms\": false", json, StringComparison.Ordinal);
        Assert.Contains("\"verdict\": \"not determined\"", json, StringComparison.Ordinal);
    }
}
