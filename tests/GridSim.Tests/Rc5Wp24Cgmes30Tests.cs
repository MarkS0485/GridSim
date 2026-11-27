using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using GridSim.Core.Api;
using GridSim.Core.IO.Cim;
using GridSim.Core.IO.Cim.Profiles;
using GridSim.Core.IO.Cim.Shacl;
using GridSim.Core.IO.Psse;
using GridSim.Core.Model;
using GridSim.Core.Model.Substations;
using GridSim.Tests.Substations;
using Xunit;

namespace GridSim.Tests;

/// <summary>
/// WP24: CGMES 3.0 bus-branch export (EQ, TP, SSH, SV) with the DL, GL, SC and DY profiles in 3.0, node-breaker
/// three-winding transformers, and the SHACL census.
/// <para>Sources. CGMES 3.0 class and attribute names, the profile of each attribute and the required properties: the
/// ENTSO-E CGMES-AP files (github.com/entsoe/CGMES-AP, Apache-2.0): RDFS Equipment.rdf, Topology.rdf,
/// SteadyStateHypothesis.rdf, StateVariables.rdf, ShortCircuit-RDFS.rdf, Dynamics-RDFS.rdf, DiagramLayout-RDFS.rdf,
/// GeographicalLocation-RDFS.rdf, and the 3.0.0 constraint (SHACL) files of the four core profiles. The profile URIs are the
/// owl:versionIRI of the CGMES-AP PROF files (already pinned by Rc5Wp10). The star formula Zk = (Zki + Zkj - Zij) / 2 is the
/// textbook T equivalent of a three-winding transformer, as the PSS/E reader evaluates it.</para>
/// <para>The 2.4.15 pins in test 5 were computed with the 2.1.128 GridSim.Core binary before any WP24 edit (a scratch
/// console probe, the same construction as below). The CGMES IO code is unchanged since the v2.1.105-RC-5 tag, so they are
/// also the tagged release's bytes.</para>
/// </summary>
public sealed class Rc5Wp24Cgmes30Tests
{
    private const double U = 1.1102230246251565e-16;   // 2^-53, the unit roundoff
    private static readonly XNamespace Rdf = CgmesVocabulary.Rdf;
    private static readonly XNamespace Cim100 = "http://iec.ch/TC57/CIM100#";

    private static string Sha(string s) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(s))).ToLowerInvariant();
    private static bool Same(double a, double b) => BitConverter.DoubleToInt64Bits(a) == BitConverter.DoubleToInt64Bits(b);

    /// <summary>case14 (MATPOWER) with every bus on 138 kV: MATPOWER's case14 gives base kV 0, which CGMES cannot hold.</summary>
    private static GridModel Case14()
    {
        GridModel m = S10TestSupport.LoadMatpower("lib/case14.m").Model;
        return m.CopyWith(buses: m.Buses.Select(b => b with { BaseKv = 138 }).ToList());
    }

    /// <summary>case14 at 138 kV with a rating on branch 1, a 2.5 degree shift on the transformer 4-7 (branch 8), a gas unit at
    /// bus 1 and a hydro unit at bus 2: the classes case14 alone does not exercise (limits, the phase changer, the unit subclasses).</summary>
    private static GridModel Case14Rich()
    {
        GridModel m = Case14();
        var branches = m.Branches.Select((b, k) => k switch
        {
            0 => b with { RateAMva = 120 },
            7 => b with { PhaseShiftDeg = 2.5 },
            _ => b,
        }).ToList();
        var gens = m.Generators.Select((g, k) => k switch { 0 => g with { Type = "gas" }, 1 => g with { Type = "hydro" }, _ => g }).ToList();
        return m.CopyWith(branches: branches, generators: gens);
    }

    // ---- 1. round trip ------------------------------------------------------------------------------------------------

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Round_trip_through_the_3_0_documents_reads_back_exactly_what_the_2_4_15_document_reads_back(bool rich)
    {
        GridModel m = rich ? Case14Rich() : Case14();
        CgmesBusBranch30Result r = CgmesBusBranch30Writer.Write(m);
        Assert.Equal(new[] { CgmesProfileKind.EQ, CgmesProfileKind.TP, CgmesProfileKind.SSH, CgmesProfileKind.SV }, r.Files.Select(f => f.Kind));
        GridModel back30 = CgmesBusBranch30Writer.ReadBack(r.Files.Select(f => f.Text), m.BaseMva, "back");
        GridModel back24 = CgmesWriter.ReadBack(r.Source.Text, m.BaseMva, "back");
        // Every object-model attribute of every element, doubles bit for bit: the 3.0 set carries what the 2.4.15 document does.
        Assert.Null(CaseEditor.AttributeDifference(back24, back30));
        // Against the model itself, the two routes have the same (first) difference: none beyond the 2.4.15 round trip's.
        GridModel want = CgmesWriter.InFileOrder(m, r.Source);
        Assert.Equal(CaseEditor.AttributeDifference(want, back24), CaseEditor.AttributeDifference(want, back30));
        // The 3.0 source is the CgmesWriter result for the same model (same values, same names).
        Assert.Equal(CgmesWriter.Write(m).Text, r.Source.Text);
    }

    [Fact]
    public void Rich_case_writes_the_limit_phase_changer_and_unit_classes_in_their_3_0_profiles()
    {
        CgmesBusBranch30Result r = CgmesBusBranch30Writer.Write(Case14Rich());
        string eq = r.Files[0].Text, ssh = r.Files[2].Text;
        Assert.Contains("<cim:ApparentPowerLimit.normalValue>120</cim:ApparentPowerLimit.normalValue>", eq, StringComparison.Ordinal);
        Assert.Contains("<cim:ApparentPowerLimit.value>120</cim:ApparentPowerLimit.value>", ssh, StringComparison.Ordinal);
        Assert.DoesNotContain("ApparentPowerLimit.value>", eq, StringComparison.Ordinal);
        Assert.Contains("<eu:OperationalLimitType.kind rdf:resource=\"http://iec.ch/TC57/CIM100-European#LimitKind.patl\" />", eq, StringComparison.Ordinal);
        Assert.Contains("<cim:PhaseTapChangerLinear ", eq, StringComparison.Ordinal);
        Assert.Contains("<cim:TapChanger.step>2.5</cim:TapChanger.step>", ssh, StringComparison.Ordinal);
        Assert.Contains("<cim:ThermalGeneratingUnit ", eq, StringComparison.Ordinal);
        Assert.Contains("FuelType.gas", eq, StringComparison.Ordinal);
        Assert.Contains("<cim:HydroGeneratingUnit ", eq, StringComparison.Ordinal);
        // p / q, targets and steps sit in SSH only.
        foreach (string ssHOnly in new[] { "EnergyConsumer.p>", "RotatingMachine.p>", "RegulatingControl.targetValue>", "TapChanger.step>", "Equipment.inService>" })
            Assert.DoesNotContain(ssHOnly, eq, StringComparison.Ordinal);
    }

    // ---- 2. namespaces and headers --------------------------------------------------------------------------------------

    [Fact]
    public void Every_3_0_document_uses_cim100_declares_its_profile_uri_and_depends_on_the_right_models()
    {
        CgmesBusBranch30Result r = CgmesBusBranch30Writer.Write(Case14());
        string Model(CgmesProfileKind k) => "urn:uuid:" + CgmesVocabulary.Uuid("model/case14/" + k);
        var depends = new Dictionary<CgmesProfileKind, string[]>
        {
            [CgmesProfileKind.EQ] = Array.Empty<string>(),
            [CgmesProfileKind.TP] = new[] { Model(CgmesProfileKind.EQ) },
            [CgmesProfileKind.SSH] = new[] { Model(CgmesProfileKind.EQ) },
            [CgmesProfileKind.SV] = new[] { Model(CgmesProfileKind.TP), Model(CgmesProfileKind.SSH) },
        };
        foreach (CgmesProfileFile f in r.Files)
        {
            Assert.Contains("xmlns:cim=\"http://iec.ch/TC57/CIM100#\"", f.Text, StringComparison.Ordinal);
            Assert.DoesNotContain("cim16", f.Text, StringComparison.Ordinal);
            Assert.DoesNotContain("entsoe.eu/CIM/SchemaExtension", f.Text, StringComparison.Ordinal);
            XElement fm = XDocument.Parse(f.Text).Root!.Element((XNamespace)CgmesVocabulary.Md + "FullModel")!;
            Assert.Equal(Model(f.Kind), (string?)fm.Attribute(Rdf + "about"));
            // 2.1.130 (WP24 review): the EQ names CoreEquipment-EU only, since nothing of Operation-EU is written.
            Assert.Equal(CgmesVocabulary.ProfileUris(CgmesVersion.V3_0, f.Kind).Where(p => !p.Contains("/Operation-EU/", StringComparison.Ordinal)),
                fm.Elements((XNamespace)CgmesVocabulary.Md + "Model.profile").Select(e => e.Value).ToArray());
            Assert.Equal(depends[f.Kind], fm.Elements((XNamespace)CgmesVocabulary.Md + "Model.DependentOn").Select(e => (string)e.Attribute(Rdf + "resource")!).ToArray());
            Assert.Equal("case14_" + f.Kind + ".xml", f.FileName);
        }
    }

    // ---- 3. mRID -----------------------------------------------------------------------------------------------------------

    [Fact]
    public void Every_identified_object_carries_an_mrid_equal_to_its_uuid()
    {
        CgmesBusBranch30Result r = CgmesBusBranch30Writer.Write(Case14Rich());
        // Classes defined here that are not IdentifiedObjects in CGMES 3.0 (no mRID): the table point and SvVoltage.
        var notIdentified = new HashSet<string>(StringComparer.Ordinal) { "RatioTapChangerTablePoint", "SvVoltage" };
        var seen = new HashSet<string>(StringComparer.Ordinal);
        int defined = 0;
        foreach (CgmesProfileFile f in r.Files)
            foreach (XElement e in XDocument.Parse(f.Text).Root!.Elements().Where(e => e.Name.Namespace == Cim100))
            {
                string about = (string)e.Attribute(Rdf + "about")!;
                Assert.StartsWith("urn:uuid:", about, StringComparison.Ordinal);
                if (!seen.Add(about)) continue;   // a description of an object defined in an earlier profile
                if (notIdentified.Contains(e.Name.LocalName)) { Assert.Null(e.Element(Cim100 + "IdentifiedObject.mRID")); continue; }
                Assert.Equal(about["urn:uuid:".Length..], Assert.Single(e.Elements(Cim100 + "IdentifiedObject.mRID")).Value);
                Assert.Single(e.Elements(Cim100 + "IdentifiedObject.name"));
                defined++;
            }
        // 14 nodes, 14 substations, 14 voltage levels, 1 base voltage, the regions, 20 branches with their terminals ...: well
        // over a hundred; the exact count is not the point, that none is missed is.
        Assert.True(defined > 100, defined.ToString(CultureInfo.InvariantCulture));
    }

    // ---- 4. the extra profiles in 3.0 --------------------------------------------------------------------------------------

    /// <summary>case9 (345 kV) with a layout, R0 / X0 on branch 2 and machine fault data, as Rc5Wp10's case.</summary>
    private static GridModel Wp10Case() => Rc5Wp10CgmesProfilesTests.Wp10Case();

    private static GeoSidecar Geo()
    {
        var sites = Enumerable.Range(1, 9).Select(b => new GeoBusSite(b, b == 1 ? 51.5072 : 51.5 + 0.01 * b, b == 1 ? -0.1276 : -0.1 - 0.01 * b)).ToList();
        return new GeoSidecar(sites, new[] { new GeoRoute(2, new[] { (51.51, -0.12), (51.52, -0.125) }) });
    }

    private static void AssertEveryReferenceResolves(IEnumerable<CgmesProfileFile> files)
    {
        var list = files.ToList();
        var defined = new HashSet<string>(StringComparer.Ordinal);
        foreach (CgmesProfileFile f in list)
            foreach (Match m in Regex.Matches(f.Text, "rdf:(about|ID)=\"([^\"]+)\""))
                defined.Add(m.Groups[1].Value == "ID" ? "#" + m.Groups[2].Value : m.Groups[2].Value);
        int refs = 0;
        foreach (CgmesProfileFile f in list)
            foreach (Match m in Regex.Matches(f.Text, "rdf:resource=\"([^\"]+)\""))
            {
                string v = m.Groups[1].Value;
                if (v.StartsWith("http://", StringComparison.Ordinal)) continue;   // an enumeration literal
                refs++;
                Assert.True(defined.Contains(v), $"{f.FileName}: {v} names no object of the set");
            }
        Assert.True(refs > 0);
    }

    [Fact]
    public void Dl_gl_and_sc_in_3_0_refer_only_to_objects_of_the_3_0_set()
    {
        GridModel m = Wp10Case();
        CgmesBusBranch30Result w = CgmesBusBranch30Writer.Write(m);
        CgmesExtraResult x = CgmesExtraProfiles.Write(m, w, new CgmesExtraOptions(new[] { CgmesProfileKind.DL, CgmesProfileKind.GL, CgmesProfileKind.SC }, CgmesVersion.V3_0, Geo()));
        Assert.Equal(new[] { "case9_DL.xml", "case9_GL.xml", "case9_SC.xml" }, x.Files.Select(f => f.FileName));
        foreach (CgmesProfileFile f in x.Files)
        {
            Assert.Contains("<md:Model.profile>" + CgmesVocabulary.ExtraProfileUris(CgmesVersion.V3_0, f.Kind) + "</md:Model.profile>", f.Text, StringComparison.Ordinal);
            Assert.Contains("<md:Model.DependentOn rdf:resource=\"urn:uuid:" + CgmesVocabulary.Uuid("model/case9/EQ") + "\" />", f.Text, StringComparison.Ordinal);
            Assert.DoesNotContain("cim16", f.Text, StringComparison.Ordinal);
        }
        AssertEveryReferenceResolves(w.Files.Concat(x.Files));
        // The bus-branch objects are named by their 3.0 identifiers, never by the 2.4.15 "#_N1" form.
        string dl = x.Files[0].Text;
        Assert.Contains("rdf:resource=\"" + CgmesBusBranch30Writer.ObjectIri("case9", "N1") + "\"", dl, StringComparison.Ordinal);
        Assert.DoesNotContain("\"#_N1\"", dl, StringComparison.Ordinal);
        // New objects that are IdentifiedObjects carry their mRID; the points do not.
        Assert.Contains("<cim:IdentifiedObject.mRID>", dl, StringComparison.Ordinal);
        foreach (Match p in Regex.Matches(dl, "<cim:DiagramObjectPoint [^>]*>(.*?)</cim:DiagramObjectPoint>", RegexOptions.Singleline))
            Assert.DoesNotContain("IdentifiedObject.mRID", p.Value, StringComparison.Ordinal);
    }

    [Fact]
    public void Sc_in_3_0_writes_machine_sequence_impedances_in_ohms_on_the_machine_rating()
    {
        GridModel m = Wp10Case();
        CgmesBusBranch30Result w = CgmesBusBranch30Writer.Write(m);
        string sc = Assert.Single(CgmesExtraProfiles.Write(m, w, new CgmesExtraOptions(new[] { CgmesProfileKind.SC }, CgmesVersion.V3_0)).Files).Text;
        double Value(string prop, string mrid = "G1")
        {
            int i = sc.IndexOf("rdf:about=\"" + CgmesBusBranch30Writer.ObjectIri("case9", mrid) + "\"", StringComparison.Ordinal);
            Assert.True(i >= 0, mrid);
            string open = "<cim:" + prop + ">";
            int a = sc.IndexOf(open, i, StringComparison.Ordinal) + open.Length;
            return double.Parse(sc[a..sc.IndexOf('<', a)], NumberStyles.Float, CultureInfo.InvariantCulture);
        }
        // case9's machines: MBase 100 MVA at 345 kV, so Zbase = 345^2 / 100 = 1190.25 ohm, exact in binary (4761 / 4). The
        // products 0.2 x 1190.25 and 0.05 x 1190.25 are not exact, so the writer searches the double whose division by Zbase
        // gives the PU value back to the bit, and the reader's division does.
        Generator g1 = m.Generators[0];
        double zb = 345.0 * 345.0 / g1.MBase;
        Assert.True(Same(0.2, Value("SynchronousMachine.x2") / zb), "x2");
        Assert.True(Same(0.05, Value("SynchronousMachine.x0") / zb), "x0");
        Assert.Equal(0.2, Value("SynchronousMachine.satDirectSubtransX"));   // PU in 3.0 as in 2.4.15
        // By hand: 0.2 x 1190.25 = 238.05 ohm and 0.05 x 1190.25 = 59.5125 ohm.
        Assert.Equal(238.05, Value("SynchronousMachine.x2"), 1e-9);
        Assert.Equal(59.5125, Value("SynchronousMachine.x0"), 1e-9);
        // The line's zero sequence is in ohms in both editions: 0.051 x 1190.25 = 60.70275 ohm.
        Assert.Equal(60.70275, Value("ACLineSegment.r0", "L2"), 1e-9);
        // RotatingMachine.ratedU, the base of the ohm values, is in the 3.0 EQ.
        Assert.Contains("<cim:RotatingMachine.ratedU>345</cim:RotatingMachine.ratedU>", w.Files[0].Text, StringComparison.Ordinal);
    }

    private const string CanonicalDyr =
        "1 'GENROU' '1' 7.4 0.03 0.4 0.05 5.148 0 1.8 1.75 0.3 0.65 0.23 0.15 0.09 0.38 /\n" +
        "1 'SEXS' '1' 0.1 10 100 0.05 0 4 /\n" +
        "1 'TGOV1' '1' 0.05 0.49 1.1 0 2.1 7 0 /\n" +
        "2 'GENSAL' '1' 6.1 0.05 0.06 6.54 0 1.25 0.7 0.31 0.25 0.12 0 0 /\n" +
        "3 'GENROU' '1' 6 0.04 0.4 0.06 4 0 1.3 1.25 0.18 0.25 0.14 0.1 0 0 /\n" +
        "6 'GENROU' '1' 7 0.03 0.5 0.05 3 0 1.6 1.55 0.25 0.4 0.18 0.12 0 0 /\n" +
        "8 'GENROU' '1' 7 0.03 0.5 0.05 3 0 1.6 1.55 0.25 0.4 0.18 0.12 0 0 /\n";

    private static PsseParseResult Case14Raw() =>
        PsseRawParser.ParseFile(Path.Combine(CliProcess.Root(), "tests", "GridSim.Tests", "fixtures", "psse", "case14.raw"));

    [Fact]
    public void Dy_in_3_0_uses_the_same_class_and_attribute_names_and_names_the_3_0_machines()
    {
        PsseParseResult raw = Case14Raw();
        GridModel m = raw.Model.CopyWith(buses: raw.Model.Buses.Select(b => b with { BaseKv = 138 }).ToList());
        var opts = new CgmesExtraOptions(new[] { CgmesProfileKind.DY }, CgmesVersion.V3_0, Dynamics: PsseDyrReader.ReadRecords(CanonicalDyr),
            MachineKeys: CgmesDynamicsProfile.KeysOf(raw), MachineArmatureResistances: CgmesDynamicsProfile.ArmatureResistancesOf(raw));
        CgmesBusBranch30Result w = CgmesBusBranch30Writer.Write(m);
        string dy3 = Assert.Single(CgmesExtraProfiles.Write(m, w, opts).Files).Text;
        string dy2 = Assert.Single(CgmesExtraProfiles.Write(m, CgmesWriter.Write(m), opts with { Version = CgmesVersion.V2_4_15 }).Files).Text;
        AssertEveryReferenceResolves(w.Files.Append(new CgmesProfileFile(CgmesProfileKind.DY, "case14_DY.xml", dy3)));
        // The 3.0 DY document uses the 2.4.15 class and attribute names: the element names of the two documents are the same
        // set, apart from the mRIDs 3.0 adds. (Each name was looked up by hand in the CGMES-AP 3.0 Dynamics RDFS when this was
        // written; this test does not read the RDFS, which is not in the repository.)
        static SortedSet<string> Names(string doc) => new(XDocument.Parse(doc).Root!.Descendants()
            .Where(e => e.Name.LocalName != "IdentifiedObject.mRID" && e.Name.NamespaceName != CgmesVocabulary.Md && e.Name.NamespaceName != CgmesVocabulary.Rdf)
            .Select(e => e.Name.LocalName), StringComparer.Ordinal);
        Assert.Equal(Names(dy2), Names(dy3));
        Assert.Contains("rdf:resource=\"http://iec.ch/TC57/CIM100#RotorKind.roundRotor\"", dy3, StringComparison.Ordinal);
        Assert.Contains("rdf:resource=\"" + CgmesBusBranch30Writer.ObjectIri("case14", "G1") + "\"", dy3, StringComparison.Ordinal);
    }

    [Fact]
    public void Each_entry_point_refuses_the_other_edition()
    {
        GridModel m = Wp10Case();
        var e1 = Assert.Throws<InvalidDataException>(() => CgmesExtraProfiles.Write(m, CgmesWriter.Write(m), new CgmesExtraOptions(new[] { CgmesProfileKind.DL }, CgmesVersion.V3_0)));
        Assert.Contains("CGMES 3.0 is not written for the bus-branch export", e1.Message, StringComparison.Ordinal);
        var e2 = Assert.Throws<InvalidDataException>(() => CgmesExtraProfiles.Write(m, CgmesBusBranch30Writer.Write(m), new CgmesExtraOptions(new[] { CgmesProfileKind.DL })));
        Assert.Contains("this entry point writes CGMES 3.0 profiles", e2.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void The_3_0_writer_refuses_what_the_cgmes_writer_refuses()
    {
        GridModel m = S10TestSupport.LoadMatpower("lib/case14.m").Model;   // base kV 0
        var ex = Assert.Throws<InvalidDataException>(() => CgmesBusBranch30Writer.Write(m));
        Assert.Contains("no base voltage", ex.Message, StringComparison.Ordinal);
    }

    // ---- 5. 2.4.15 unchanged ---------------------------------------------------------------------------------------------

    [Fact]
    public void The_2_4_15_documents_are_byte_identical_to_the_2_1_128_build()
    {
        GridModel m = Wp10Case();
        CgmesWriteResult w = CgmesWriter.Write(m);
        Assert.Equal("e78527d8a7b0615ee92b80687a6d5fd8d8f927175873bd7aa13b63a2141d1c1a", Sha(w.Text));
        CgmesExtraResult x = CgmesExtraProfiles.Write(m, w, new CgmesExtraOptions(new[] { CgmesProfileKind.DL, CgmesProfileKind.GL, CgmesProfileKind.SC }, Geo: Geo()));
        Assert.Equal("934b7a998f64a393d276d1fabc0c0d3abc9df2f425a284046404debd148e5ba4", Sha(string.Concat(x.Files.Select(f => f.FileName + "\n" + f.Text + "\n--\n"))));
        Assert.Equal("e184c4a9d1c982ff2c26e20231d1ad3778cdeeb8e98a441cb952f9cfc84ec005", Sha(string.Join("|", x.Omissions.Select(o => o.Profile + ":" + o.Item + ":" + o.Reason))));
        Assert.Equal("c3f76e4369c5db7eddc45f3b06851b16fe4bda71d3182aa1293d1c2040aaf94e", Sha(string.Join("|", x.Notes)));

        PsseParseResult raw = Case14Raw();
        GridModel mm = raw.Model.CopyWith(buses: raw.Model.Buses.Select(b => b with { BaseKv = 138 }).ToList());
        CgmesExtraResult dy = CgmesExtraProfiles.Write(mm, CgmesWriter.Write(mm), new CgmesExtraOptions(new[] { CgmesProfileKind.DY }, Dynamics: PsseDyrReader.ReadRecords(CanonicalDyr),
            MachineKeys: CgmesDynamicsProfile.KeysOf(raw), MachineArmatureResistances: CgmesDynamicsProfile.ArmatureResistancesOf(raw)));
        Assert.Equal("09d373228cab86bdc353a243d20b4452cc4e52b5be8a6d61a05309a6fc2e0430", Sha(string.Concat(dy.Files.Select(f => f.FileName + "\n" + f.Text + "\n--\n"))));
        Assert.Equal("b342c91538277b3c5b3a8951784718a92a6a52f34d498ae4d103cbdbbd0e8eb9", Sha(string.Join("|", dy.Omissions.Select(o => o.Profile + ":" + o.Item + ":" + o.Reason))));
        Assert.Equal("4d7143c3a47661db4bc9987cc77e5f062587c6bc91ddac067e7fe2fd64091d13", Sha(string.Join("|", dy.Notes)));

        double zbHv = 345.0 * 345.0 / 100.0, zbLv = 20.0 * 20.0 / 100.0;
        var tx = new GridModel
        {
            BaseMva = 100, Name = "tx2",
            Buses = new[] { new Bus(1, BusType.Slack, BaseKv: 345), new Bus(2, BusType.Pq, Pd: 10, Qd: 2, BaseKv: 20) },
            Generators = new[] { new Generator(1, 10, Vg: 1.0) },
            Branches = new[] { new Branch(1, 2, 0.001, 0.1, IsTransformer: true, R0: 0.002, X0: 0.09, FromWinding: WindingConnection.WyeGrounded,
                ToWinding: WindingConnection.WyeGrounded, FromNeutralXPu: 10.0 / zbHv, ToNeutralXPu: 10.0 / zbLv) },
        };
        CgmesWriteResult wt = CgmesWriter.Write(tx);
        Assert.Equal("6e4e140f8443fb7833e6f0dac915a988684709d90117cea68a72d0f70fd63ed7", Sha(wt.Text));
        Assert.Equal("44f0b05e1468af4e70644bda69443753a270427ce5b688d7ea75d1f0decf7339",
            Sha(CgmesExtraProfiles.Write(tx, wt, new CgmesExtraOptions(new[] { CgmesProfileKind.SC })).Files[0].Text));
    }

    // ---- 6. three-winding star -------------------------------------------------------------------------------------------

    [Fact]
    public void Star_split_of_j01_j02_j015_gives_j0075_j0025_j0125_within_the_derived_rounding_bound()
    {
        // Z12 = j0.1, Z13 = j0.2, Z23 = j0.15: Z1 = (0.1 + 0.2 - 0.15) / 2 = 0.075, Z2 = (0.1 + 0.15 - 0.2) / 2 = 0.025,
        // Z3 = (0.2 + 0.15 - 0.1) / 2 = 0.125 in exact arithmetic. In doubles each input is within u|x| of its decimal value,
        // the sum and the difference each add at most u times their size and the halving is exact, so (bound, then the error
        // of the decimal result's own double): Z1 <= ((0.1+0.2+0.15) + 0.3 + 0.15) u / 2 + 0.075 u = 0.525 u; Z2 <= (0.45 +
        // 0.25 + 0.05) u / 2 + 0.025 u = 0.4 u; Z3 <= (0.45 + 0.35 + 0.25) u / 2 + 0.125 u = 0.65 u.
        (double z1, double z2, double z3) = ThreeWindingStar.Split(0.1, 0.15, 0.2);
        Assert.True(Math.Abs(z1 - 0.075) <= 0.525 * U, z1.ToString("R", CultureInfo.InvariantCulture));
        Assert.True(Math.Abs(z2 - 0.025) <= 0.4 * U, z2.ToString("R", CultureInfo.InvariantCulture));
        Assert.True(Math.Abs(z3 - 0.125) <= 0.65 * U, z3.ToString("R", CultureInfo.InvariantCulture));
        // The same expressions, in the same order, as the PSS/E reader's (PsseRawParser: sr / sx), so the bits are those.
        Assert.True(Same((0.1 + 0.2 - 0.15) / 2, z1) && Same((0.1 + 0.15 - 0.2) / 2, z2) && Same((0.2 + 0.15 - 0.1) / 2, z3));
    }

    /// <summary>One substation with 400 / 132 / 33 kV nodes h, m, l, a 3W unit Z12 = j0.1, Z13 = j0.2, Z23 = j0.15 and a
    /// magnetising B of -0.001 pu, a slack at h, a PV machine at m and loads on all three.</summary>
    internal static SubstationModel ThreeWinding(IReadOnlyList<double>? ratio = null) => new()
    {
        Name = "t3", BaseMva = 100,
        Substations = new[]
        {
            new Substation("S", null, new[] { new VoltageLevel("H", 400), new VoltageLevel("M", 132), new VoltageLevel("L", 33) },
                Array.Empty<SubstationBay>(), new[] { new ConnectivityNode("h", "H"), new ConnectivityNode("m", "M"), new ConnectivityNode("l", "L") },
                Array.Empty<NbSwitch>()),
        },
        Transformers3W = new[] { new NbTransformer3W("T", "h", "m", "l", 0, 0.1, 0, 0.2, 0, 0.15, B: -0.001, Ratio: ratio) },
        Generators = new[] { new NbGenerator("G", "h", 0, Slack: true), new NbGenerator("G2", "m", 5) },
        Loads = new[] { new NbLoad("D", "l", 10, 2), new NbLoad("D2", "h", 1), new NbLoad("D3", "m", 1) },
    };

    [Fact]
    public void Projection_makes_a_star_bus_and_three_winding_branches_with_the_star_impedances()
    {
        SubstationModel sub = ThreeWinding();
        SubstationProjectionResult p = SubstationProjection.Project(sub, new Dictionary<string, bool>());
        // Three nodes, so node-rank ids 1..3 (h, l, m by ordinal node id) and the star point after them, 4.
        Assert.Equal(4, p.StarPoints["T"]);
        Bus star = p.Model.Buses.Single(b => b.Id == 4);
        Assert.Equal(("T star point", BusType.Pq, 400.0), (star.Name, star.Type, star.BaseKv));
        Assert.True(Same(-0.001 * 100, star.Bs));
        (double r1, double r2, double r3, double x1, double x2, double x3) = sub.Transformers3W[0].Star();
        Assert.Equal(3, p.Model.Branches.Count);
        Branch w1 = p.Model.Branches[0], w2 = p.Model.Branches[1], w3 = p.Model.Branches[2];
        Assert.Equal((1, 4, 3, 4, 2, 4), (w1.From, w1.To, w2.From, w2.To, w3.From, w3.To));
        Assert.True(Same(x1, w1.X) && Same(x2, w2.X) && Same(x3, w3.X) && Same(r1, w1.R) && Same(r2, w2.R) && Same(r3, w3.R));
        Assert.True(w1.IsTransformer && w2.IsTransformer && w3.IsTransformer);
        Assert.Equal(EquipmentDisposition.Connected, p.Equipment.Single(e => e.Kind == NbEquipmentKind.Transformer3W).Disposition);
        Assert.Equal(new[] { "T.hv", "T.mv", "T.lv" }, p.Branches.Select(b => b.FromTerminal));
    }

    [Fact]
    public void A_model_without_three_winding_units_projects_and_writes_as_before()
    {
        SubstationModel rich = CgmesProfilesTests.Rich();
        Assert.Empty(rich.Transformers3W);
        Assert.DoesNotContain("transformers3w", SubstationJson.Write(rich), StringComparison.Ordinal);
        Assert.Empty(SubstationProjection.ProjectNormal(rich, new SubstationProjectionOptions(UnspecifiedStatePolicy.UseNormalState)).StarPoints);
        CgmesExportResult e = CgmesSubstationExporter.Export(rich, null, new CgmesExportOptions(CgmesVersion.V3_0));
        Assert.Contains(e.Omissions, o => o.Item.Contains("three-winding transformers", StringComparison.Ordinal));
    }

    [Fact]
    public void Three_winding_json_round_trips_and_bad_data_is_refused_by_name()
    {
        SubstationModel sub = ThreeWinding(new[] { 1.025, 1.0, 0.975 });
        string json = SubstationJson.Write(sub);
        Assert.Contains("\"transformers3w\"", json, StringComparison.Ordinal);
        Assert.Equal(json, SubstationJson.Write(SubstationJson.Read(json)));
        var bad = new SubstationModel
        {
            Name = sub.Name, Substations = sub.Substations, Generators = sub.Generators, Loads = sub.Loads,
            Transformers3W = new[] { new NbTransformer3W("T", "h", "h", "l", 0, 0.1, 0, 0.1, 0, 0.2, Ratio: new[] { 1.0, 1.0 }) },
        };
        IReadOnlyList<string> problems = bad.Problems();
        Assert.Contains(problems, q => q.Contains("has two windings on one node", StringComparison.Ordinal));
        Assert.Contains(problems, q => q.Contains("ratio needs three entries", StringComparison.Ordinal));
        // Z12 = 0.1, Z13 = 0.1, Z23 = 0.2: Z1 = (0.1 + 0.1 - 0.2) / 2 = 0 exactly (no rounding: 0.1 + 0.1 is 0.2's double).
        Assert.Contains(problems, q => q.Contains("star impedance is 0", StringComparison.Ordinal));
    }

    // ---- 7. three-winding export round trip ----------------------------------------------------------------------------

    [Theory]
    [InlineData(CgmesVersion.V2_4_15)]
    [InlineData(CgmesVersion.V3_0)]
    public void Three_winding_export_gives_the_star_impedances_back_on_each_end(CgmesVersion v)
    {
        SubstationModel sub = ThreeWinding(new[] { 1.025, 1.0, 1.0 });
        CgmesExportResult e = CgmesSubstationExporter.Export(sub, new Dictionary<string, bool>(), new CgmesExportOptions(v));
        Assert.DoesNotContain(e.Omissions, o => o.Item.Contains("three-winding transformers", StringComparison.Ordinal));
        // GridSim's CIM reader (node-breaker view through the ConnectivityNodes) reads the unit with its three ends.
        CimNetwork net = CimImporter.Parse(e.Files.Select(f => f.Text));
        CimTransformer t = Assert.Single(net.Transformers);
        Assert.Equal(3, t.Ends.Count);
        var (_, _, _, x1, x2, x3) = sub.Transformers3W[0].Star();
        double[] want = { x1, x2, x3 }, kv = { 400, 132, 33 };
        for (int n = 0; n < 3; n++)
        {
            CimTransformerEnd end = t.Ends.Single(x => x.EndNumber == n + 1);
            Assert.Equal(kv[n], end.RatedU);
            double back = end.X / (kv[n] * kv[n] / 100.0);
            // The writer searches the double whose reader division gives the per-unit value back; when none exists the
            // nearest is written, at most one ulp off: 2^-52 relative, inside the 1e-15 the spec allows.
            Assert.True(Math.Abs(back - want[n]) <= 1e-15 * Math.Abs(want[n]), $"winding {n + 1}: {back:R} vs {want[n]:R}");
        }
        Assert.Equal(1.025, t.Ends.Single(x => x.EndNumber == 1).TapRatio, 15);
        // The node-breaker reader still reads two-winding units only, and says so by name (no silent drop).
        var ex = Assert.Throws<InvalidDataException>(() => CgmesSubstationReader.Read(e.Files.Select(f => (f.Kind, f.FileName, f.Text))));
        Assert.Contains("has 3 winding(s); only two-winding transformers are read", ex.Message, StringComparison.Ordinal);
    }

    // ---- 8. SHACL ---------------------------------------------------------------------------------------------------------

    [Fact]
    public void Census_counts_each_shacl_feature_and_how_the_checker_handles_it()
    {
        const string shapes = """
            @prefix sh: <http://www.w3.org/ns/shacl#> .
            @prefix cim: <http://iec.ch/TC57/CIM100#> .
            @prefix ex: <http://example.org/s#> .
            ex:Line a sh:NodeShape ; sh:targetClass cim:ACLineSegment ; sh:property ex:r , ex:x ;
                sh:sparql [ sh:select "SELECT $this WHERE { }" ] .
            ex:r a sh:PropertyShape ; sh:path cim:ACLineSegment.r ; sh:minCount 1 ; sh:datatype <http://www.w3.org/2001/XMLSchema#float> .
            ex:x a sh:PropertyShape ; sh:path [ sh:inversePath cim:Terminal.ConductingEquipment ] ; sh:maxCount 2 ; sh:closed true .
            """;
        var g = new RdfGraph();
        TurtleReader.Read(g, shapes, "s.ttl");
        ShaclCensus c = ShaclValidator.Census(g);
        Assert.Equal((1, 2, 3, 1, 0, 1, 1), (c.NodeShapes, c.PropertyShapes, c.Shapes, c.SparqlConstraints, c.SparqlTargets, c.PredicatePaths, c.ComplexPaths));
        var by = c.Rows.ToDictionary(r => r.Feature, StringComparer.Ordinal);
        Assert.Equal(("run", 1), (by["sh:minCount"].Handling, by["sh:minCount"].Count));
        Assert.Equal(("run", 2), (by["sh:property"].Handling, by["sh:property"].Count));
        Assert.Equal(("not run", 1), (by["sh:sparql"].Handling, by["sh:sparql"].Count));
        Assert.Equal(("not run", 1), (by["sh:closed"].Handling, by["sh:closed"].Count));
        Assert.Equal(("inert", 2), (by["sh:path"].Handling, by["sh:path"].Count));
        Assert.Equal(2, c.NotRunUses);
        Assert.Equal(c.Rows.Select(r => r.Feature).OrderBy(f => f, StringComparer.Ordinal), c.Rows.Select(r => r.Feature));
    }

    /// <summary>
    /// The public CGMES 3.0 shapes on the case14 3.0 export. The files are third-party (entsoe/CGMES-AP, Apache-2.0) and are
    /// not in the repository: point GRIDSIM_CGMES_AP_SHAPES at a folder holding them under their published names (from
    /// CGMES/SHACL of github.com/entsoe/CGMES-AP). Without it the test is SKIPPED, never passed. Each file's SHA-256 is pinned
    /// (as published, commit e96dd4e3), so a different release fails here instead of being judged silently.
    /// <para>
    /// The four "Simple" files of 61970-600-2 (EQ, TP, SSH, SV) hold cardinality and datatype checks only, with no
    /// sh:sparql. On them the verdict is "does not conform" and every result is GeneratingUnit.normalPF (SSH, minCount 1),
    /// which the case does not hold and the export lists as an omission. These figures were observed (the 2.1.130 build on
    /// these files); they are not a claim about the other CGMES-AP files.
    /// </para>
    /// <para>
    /// The "Complex" files hold SHACL-SPARQL constraints (61970-301 Equipment Complex: checked here), which GridSim does not
    /// evaluate: each one is listed NOT CHECKED by shape and the verdict is never "conforms". The other Complex and
    /// cross-profile files (61970-452, -456, -457 and the rest) are not run here and are NOT CHECKED.
    /// </para>
    /// </summary>
    [EnvironmentFact("the public CGMES-AP 3.0 shapes are third-party files, not in the repository", "GRIDSIM_CGMES_AP_SHAPES")]
    public void Public_3_0_shapes_select_nodes_of_the_case14_3_0_export_and_list_only_normal_pf()
    {
        string dir = Environment.GetEnvironmentVariable("GRIDSIM_CGMES_AP_SHAPES")!;
        var pinned = new (string File, string Sha256)[]
        {
            ("61970-600-2_Equipment-AP-Con-Simple-SHACL.ttl", "f80369ece84b8e28cbbdbd32310b65e77ab614551a4b4c472cb95ceff42fd56c"),
            ("61970-600-2_StateVariables-AP-Con-Simple-SHACL.ttl", "10ca73a153c791e7de3ec614f431144bdcc963561e0e383ce4478af8b81836a1"),
            ("61970-600-2_SteadyStateHypothesis-AP-Con-Simple-SHACL.ttl", "04866d2a3541df9b251458abb4c6ae3ab72f3029667d79b3a2779d51a2ffbd0a"),
            ("61970-600-2_Topology-AP-Con-Simple-SHACL.ttl", "6df020daaae3a7ee570d39923cc422067875e9670edfc1b3053d8f85e98c82d0"),
        };
        const string complex = "61970-301_Equipment-AP-Con-Complex-SHACL.ttl";
        const string complexSha = "8878a696e4483ea9540a888677e2052c4029a0a4d4af6f867ca05ae130da300e";
        static string FileSha(string path) => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))).ToLowerInvariant();
        foreach ((string f, string sha) in pinned.Append((complex, complexSha)))
            Assert.True(FileSha(Path.Combine(dir, f)) == sha, $"{f}: not the published file this test was written against (sha256 {sha})");

        var shapes = new RdfGraph();
        foreach ((string f, _) in pinned) TurtleReader.Read(shapes, File.ReadAllText(Path.Combine(dir, f)), f);
        ShaclCensus c = ShaclValidator.Census(shapes);
        Assert.Equal(0, c.SparqlConstraints);
        Assert.Equal(0, c.NotRunUses);

        var data = new RdfGraph();
        foreach (CgmesProfileFile f in CgmesBusBranch30Writer.Write(Case14()).Files) RdfXmlReader.Read(data, f.Text, f.FileName);
        ShaclReport r = ShaclValidator.Validate(data, shapes);
        Assert.Equal(FocusNodesSimple, r.FocusNodes);
        Assert.Equal(ShaclOutcome.Fail, r.Outcome);
        Assert.Empty(r.NotRun);
        Assert.Equal(5, r.Results.Count);
        Assert.All(r.Results, x => Assert.Equal("<http://iec.ch/TC57/CIM100#GeneratingUnit.normalPF>", x.Path));
        Assert.All(r.Results, x => Assert.Equal("sh:MinCountConstraintComponent", x.Component));

        // The Complex Equipment file: its SPARQL constraints are counted, each is NOT CHECKED by shape, never a pass.
        RdfGraph complexShapes = new();
        TurtleReader.Read(complexShapes, File.ReadAllText(Path.Combine(dir, complex)), complex);
        ShaclCensus cc = ShaclValidator.Census(complexShapes);
        Assert.True(cc.SparqlConstraints > 0);
        ShaclReport rc = ShaclValidator.Validate(data, complexShapes);
        Assert.NotEqual(ShaclOutcome.Pass, rc.Outcome);
        var notChecked = rc.NotRun.Where(n => n.What.StartsWith("sh:sparql", StringComparison.Ordinal)).ToList();
        Assert.Equal(cc.SparqlConstraints, notChecked.Count);
        Assert.All(notChecked, n => Assert.StartsWith("NOT CHECKED: ", n.Reason, StringComparison.Ordinal));
    }

    /// <summary>Focus nodes of the Simple files on the case14 3.0 export (observed; see test 8). 2.1.129 gave 172; 2.1.130
    /// adds a Line container for each of case14's 17 ACLineSegments (the other 3 branches are transformers), each a focus
    /// node of the Line shapes: 172 + 17 = 189.</summary>
    private const int FocusNodesSimple = 189;

    // ---- CLI ---------------------------------------------------------------------------------------------------------------

    [Fact]
    public void Cli_model_bus_branch_writes_the_four_3_0_documents_and_the_profiles()
    {
        string d = TestTemp.Under("gridsim-wp24-cli-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(d);
        string case9 = Path.Combine(CliProcess.Root(), "data", "matpower", "lib", "case9.m");
        CliProcess.Result r = CliProcess.RunBuilt("cgmes-export", case9, "--dir", Path.Combine(d, "out"), "--model", "bus-branch", "--cgmes-version", "3.0", "--profiles", "sc");
        Assert.True(r.ExitCode == 0, r.ToString());
        Assert.Equal(new[] { "case9_EQ.xml", "case9_SC.xml", "case9_SSH.xml", "case9_SV.xml", "case9_TP.xml" },
            Directory.GetFiles(Path.Combine(d, "out")).Select(Path.GetFileName).OrderBy(n => n, StringComparer.Ordinal).ToArray());
        CgmesBusBranch30Result api = CgmesBusBranch30Writer.Write(S10TestSupport.LoadMatpower("lib/case9.m").Model);
        Assert.Equal(api.Files[0].Text, File.ReadAllText(Path.Combine(d, "out", "case9_EQ.xml")));

        Assert.Equal(0, CliProcess.RunBuilt("cgmes-export", case9, "--dir", Path.Combine(d, "plain"), "--model", "bus-branch", "--cgmes-version", "3.0").ExitCode);
        Assert.Equal(4, Directory.GetFiles(Path.Combine(d, "plain")).Length);
        Assert.Equal(2, CliProcess.RunBuilt("cgmes-export", case9, "--dir", Path.Combine(d, "x"), "--model", "bus-branch").ExitCode);
        Assert.Equal(2, CliProcess.RunBuilt("cgmes-export", case9, "--dir", Path.Combine(d, "x"), "--model", "node-breaker", "--cgmes-version", "3.0").ExitCode);
        // Without --model the WP10 route is unchanged: --profiles with 3.0 is still refused as usage.
        Assert.Equal(2, CliProcess.RunBuilt("cgmes-export", case9, "--dir", Path.Combine(d, "x"), "--profiles", "sc", "--cgmes-version", "3.0").ExitCode);
    }

    [Fact]
    public void Cli_census_lists_the_features_of_a_shapes_file()
    {
        string d = TestTemp.Under("gridsim-wp24-census-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(d);
        string file = Path.Combine(d, "s.ttl");
        File.WriteAllText(file, "@prefix sh: <http://www.w3.org/ns/shacl#> .\n@prefix ex: <http://example.org/s#> .\nex:S a sh:NodeShape ; sh:targetClass ex:C ; sh:property ex:p .\nex:p a sh:PropertyShape ; sh:path ex:q ; sh:minCount 1 .\n");
        CliProcess.Result r = CliProcess.RunBuilt("cgmes-shacl", "--census", file);
        Assert.True(r.ExitCode == 0, r.ToString());
        Assert.Contains("0 sh:sparql constraint(s)", r.StdOut, StringComparison.Ordinal);
        Assert.Contains("sh:minCount", r.StdOut, StringComparison.Ordinal);
        Assert.Equal(2, CliProcess.RunBuilt("cgmes-shacl", "--census").ExitCode);
        Assert.Equal(1, CliProcess.RunBuilt("cgmes-shacl", "--census", Path.Combine(d, "missing.ttl")).ExitCode);
    }
}
