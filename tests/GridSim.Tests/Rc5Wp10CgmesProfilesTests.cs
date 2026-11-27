using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using GridSim.Core.IO.Cim;
using GridSim.Core.IO.Cim.Profiles;
using GridSim.Core.IO.Cim.Zip;
using GridSim.Core.IO.Psse;
using GridSim.Core.Model;
using GridSim.Core.Protection.ShortCircuit;
using GridSim.Tests.Substations;
using Xunit;

namespace GridSim.Tests;

/// <summary>
/// RC5 WP10: the CGMES DL, GL, SC and DY profiles of the bus-branch export (CgmesExtraProfiles), their import, and the
/// pins that the existing CGMES writers and the conformity run are unchanged.
/// <para>Sources. Profile URIs: the ENTSO-E CGMES 2.4.15 RDFS (ENTSOE_CGMES_v2.4.15_04Jul2016_RDFS.zip, CGMES Library at
/// entsoe.eu) and the CGMES-AP PROF files (github.com/entsoe/CGMES-AP, CGMES/PROF/CGMES-*-PROF.rdf, owl:versionIRI).
/// SC units: EquipmentProfileCoreShortCircuitRDFSAugmented-v2_4_15-4Jul2016 (ACLineSegment.r0 Resistance, x0 Reactance,
/// TransformerEnd.xground Reactance, SynchronousMachine.x2 / x0 / satDirectSubtransX PU). The ohm figures are the hand
/// products R0 x Zbase with Zbase = 345^2 / 100 = 1190.25 ohm: 0.051 x 1190.25 = 60.70275 and 0.276 x 1190.25 = 328.509.
/// DY: attribute names from DynamicsProfileRDFSAugmented-v2_4_15-16Feb2016, CON orders from the PSS/E data sheets (as the
/// DYR reader reads them). The pinned hashes were computed with the 2.1.089 GridSim.Core binary, before WP10.</para>
/// </summary>
public sealed class Rc5Wp10CgmesProfilesTests
{
    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    private static string Sha(string s) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(s))).ToLowerInvariant();
    private static bool Same(double a, double b) => BitConverter.DoubleToInt64Bits(a) == BitConverter.DoubleToInt64Bits(b);

    /// <summary>case9 (base kV 345) with a hand layout (bus 1 at 0.25 / 0.75), R0 = 0.051, X0 = 0.276 on line 4-5, and every
    /// generator with X''d = 0.2, X2 = 0.2 and Z0 = j0.05.</summary>
    internal static GridModel Wp10Case()
    {
        GridModel m = S10TestSupport.LoadMatpower("lib/case9.m").Model;
        var buses = m.Buses.Select((b, i) => b with { X = 0.25 + 0.0625 * i, Y = 0.75 - 0.0625 * i }).ToList();
        var branches = m.Branches.Select((br, k) => k == 1 ? br with { R0 = 0.051, X0 = 0.276 } : br).ToList();
        var gens = m.Generators.Select(g => g with { SubtransientXdPu = 0.2, NegativeSeqXPu = 0.2, ZeroSequenceZPu = new MachineZeroSequence(0.0, 0.05) }).ToList();
        return m.CopyWith(buses: buses, branches: branches, generators: gens);
    }

    private static CimBridgeResult Bridge(string text, double baseMva) =>
        CimGridModelBridge.Bridge(CimImporter.Parse(text, CgmesWriter.ReadBackOptions), baseMva, "back");

    private static string Extra(GridModel m, CgmesWriteResult w, CgmesProfileKind k, GeoSidecar? geo = null) =>
        Assert.Single(CgmesExtraProfiles.Write(m, w, new CgmesExtraOptions(new[] { k }, Geo: geo)).Files).Text;

    // ---- 1. pins ------------------------------------------------------------------------------------------------------

    [Fact]
    public void Pin_the_existing_writers_conformity_run_and_vocabulary_are_unchanged()
    {
        Assert.Equal("e78527d8a7b0615ee92b80687a6d5fd8d8f927175873bd7aa13b63a2141d1c1a", Sha(CgmesWriter.Write(Wp10Case()).Text));
        CgmesExportResult e24 = CgmesSubstationExporter.Export(CgmesProfilesTests.Rich(), null, new CgmesExportOptions(CgmesVersion.V2_4_15));
        CgmesExportResult e30 = CgmesSubstationExporter.Export(CgmesProfilesTests.Rich(), null, new CgmesExportOptions(CgmesVersion.V3_0));
        static string All(CgmesExportResult e) => string.Concat(e.Files.Select(f => f.FileName + "\n" + f.Text + "\n--\n"));
        Assert.Equal("f0a9640c9973a54cc2789649d67fb7fe328d27c544b8abc1a05ae1b4117b9eee", Sha(All(e24)));
        // Re-pinned at 2.1.134 for BREAKING CHANGE B30 (2.1.131): the 3.0 export of the two-winding T1 now carries
        // TapChanger.neutralU, TapChanger.ltcFlag and PhaseTapChangerLinear.xMin / xMax. Proof that nothing else moved: with
        // exactly those added lines removed, the document hashes to the earlier pin.
        Assert.Equal("36705e066887d89056db01c2fe98528e4419a65d3fab9c9a4bd16a2d56243bb5", Sha(All(e30)));
        string withoutB30 = System.Text.RegularExpressions.Regex.Replace(All(e30),
            "[ \\t]*<cim:(TapChanger\\.neutralU|TapChanger\\.ltcFlag|PhaseTapChangerLinear\\.xMin|PhaseTapChangerLinear\\.xMax)>[^<]*</cim:[^>]+>\\r?\\n", "");
        Assert.Equal("5e2f9bb49d7ccdbe382348ecc39c1ef6e138d5a1ba518af52f13b0c3d3679475", Sha(withoutB30));
        Assert.Equal(9, e24.Omissions.Count);
        Assert.Contains(e24.Omissions, o => o.Profile == CgmesProfileKind.DL && o.Item == "the whole DL profile");

        string d = TestTemp.Under("gridsim-wp10-pin-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(d);
        foreach (CgmesProfileFile f in e24.Files) File.WriteAllText(Path.Combine(d, f.FileName), f.Text);
        CgmesConformanceResult c = CgmesConformance.Run(CgmesArchiveReader.ReadDirectory(d));
        Assert.Equal("66400412fd173782085bf323b91674953e93e399d2e49cfd1f2ac72af38551fe",
            Sha(c.Edition + "|" + string.Join("|", c.Checks.Select(k => k.Id + ":" + k.Status + ":" + k.Name + ":" + k.Basis + ":" + string.Join("/", k.Findings))) + "|" + string.Join("|", c.Notes)));

        foreach (CgmesProfileKind k in new[] { CgmesProfileKind.DL, CgmesProfileKind.GL, CgmesProfileKind.SC, CgmesProfileKind.DY })
            Assert.Throws<ArgumentOutOfRangeException>(() => CgmesVocabulary.ProfileUris(CgmesVersion.V2_4_15, k));
    }

    // ---- 2. profile URIs ----------------------------------------------------------------------------------------------

    [Theory]
    [InlineData(CgmesVersion.V2_4_15, CgmesProfileKind.DL, "http://entsoe.eu/CIM/DiagramLayout/3/1")]
    [InlineData(CgmesVersion.V2_4_15, CgmesProfileKind.GL, "http://entsoe.eu/CIM/GeographicalLocation/2/1")]
    [InlineData(CgmesVersion.V2_4_15, CgmesProfileKind.SC, "http://entsoe.eu/CIM/EquipmentShortCircuit/3/1")]
    [InlineData(CgmesVersion.V2_4_15, CgmesProfileKind.DY, "http://entsoe.eu/CIM/Dynamics/3/1")]
    [InlineData(CgmesVersion.V3_0, CgmesProfileKind.DL, "http://iec.ch/TC57/ns/CIM/DiagramLayout-EU/3.0")]
    [InlineData(CgmesVersion.V3_0, CgmesProfileKind.GL, "http://iec.ch/TC57/ns/CIM/GeographicalLocation-EU/3.0")]
    [InlineData(CgmesVersion.V3_0, CgmesProfileKind.SC, "http://iec.ch/TC57/ns/CIM/ShortCircuit-EU/3.0")]
    [InlineData(CgmesVersion.V3_0, CgmesProfileKind.DY, "http://iec.ch/TC57/ns/CIM/Dynamics-EU/1.0")]
    public void Uris_are_the_published_profile_uris_and_KindOf_classifies_them(CgmesVersion v, CgmesProfileKind k, string uri)
    {
        Assert.Equal(uri, CgmesVocabulary.ExtraProfileUris(v, k));
        Assert.Equal(k, CgmesVocabulary.KindOf(new[] { uri }));
    }

    [Fact]
    public void Every_profile_document_declares_its_uri_and_depends_on_the_equipment_model()
    {
        GridModel m = Wp10Case();
        CgmesWriteResult w = CgmesWriter.Write(m);
        CgmesExtraResult x = CgmesExtraProfiles.Write(m, w, new CgmesExtraOptions(new[] { CgmesProfileKind.SC, CgmesProfileKind.DL }));
        Assert.Equal(new[] { "case9_DL.xml", "case9_SC.xml" }, x.Files.Select(f => f.FileName));
        string dep = "urn:uuid:" + CgmesVocabulary.Uuid("model/case9/EQ");
        foreach (CgmesProfileFile f in x.Files)
        {
            Assert.Contains("<md:Model.profile>" + CgmesVocabulary.ExtraProfileUris(CgmesVersion.V2_4_15, f.Kind) + "</md:Model.profile>", f.Text, StringComparison.Ordinal);
            Assert.Contains("<md:Model.DependentOn rdf:resource=\"" + dep + "\" />", f.Text, StringComparison.Ordinal);
        }
        var ex = Assert.Throws<InvalidDataException>(() => CgmesExtraProfiles.Write(m, w, new CgmesExtraOptions(new[] { CgmesProfileKind.DL }, CgmesVersion.V3_0)));
        Assert.Contains("CGMES 3.0 is not written for the bus-branch export", ex.Message, StringComparison.Ordinal);
    }

    // ---- 3. DL ----------------------------------------------------------------------------------------------------------

    [Fact]
    public void Dl_round_trip_writes_the_positions_as_they_are_and_reads_them_back_to_the_bits()
    {
        GridModel m = Wp10Case();
        CgmesWriteResult w = CgmesWriter.Write(m);
        string dl = Extra(m, w, CgmesProfileKind.DL);
        Assert.Contains("<cim:DiagramObjectPoint.xPosition>0.25</cim:DiagramObjectPoint.xPosition>", dl, StringComparison.Ordinal);
        Assert.Contains("<cim:DiagramObjectPoint.yPosition>0.75</cim:DiagramObjectPoint.yPosition>", dl, StringComparison.Ordinal);
        Assert.Contains("OrientationKind.negative", dl, StringComparison.Ordinal);

        CimBridgeResult b = Bridge(w.Text, m.BaseMva);
        CgmesLayout lay = CgmesDiagramLayout.Read(new[] { w.Text, dl }, b.BusIdOfNode);
        Assert.Equal(9, lay.Positions.Count);
        GridModel applied = CgmesDiagramLayout.Apply(b.Model, lay);
        for (int i = 0; i < m.Buses.Count; i++)
            Assert.True(Same(m.Buses[i].X, applied.Buses[i].X) && Same(m.Buses[i].Y, applied.Buses[i].Y), $"bus {i + 1}");
        // The import alone changes nothing: the bridge's model keeps the CGMES-less layout (0, 0).
        Assert.All(b.Model.Buses, bus => Assert.True(bus.X == 0 && bus.Y == 0));
    }

    private static string ForeignDl(string orientation) => $"""
        <?xml version="1.0" encoding="UTF-8"?>
        <rdf:RDF xmlns:rdf="http://www.w3.org/1999/02/22-rdf-syntax-ns#" xmlns:cim="http://iec.ch/TC57/2013/CIM-schema-cim16#">
          <cim:Diagram rdf:ID="_D"><cim:Diagram.orientation rdf:resource="http://iec.ch/TC57/2013/CIM-schema-cim16#OrientationKind.{orientation}"/></cim:Diagram>
          <cim:DiagramObject rdf:ID="_O1"><cim:DiagramObject.Diagram rdf:resource="#_D"/><cim:DiagramObject.IdentifiedObject rdf:resource="#_N1"/></cim:DiagramObject>
          <cim:DiagramObject rdf:ID="_O2"><cim:DiagramObject.Diagram rdf:resource="#_D"/><cim:DiagramObject.IdentifiedObject rdf:resource="#_N2"/></cim:DiagramObject>
          <cim:DiagramObjectPoint rdf:ID="_P1"><cim:DiagramObjectPoint.DiagramObject rdf:resource="#_O1"/><cim:DiagramObjectPoint.sequenceNumber>1</cim:DiagramObjectPoint.sequenceNumber><cim:DiagramObjectPoint.xPosition>100</cim:DiagramObjectPoint.xPosition><cim:DiagramObjectPoint.yPosition>200</cim:DiagramObjectPoint.yPosition></cim:DiagramObjectPoint>
          <cim:DiagramObjectPoint rdf:ID="_P2"><cim:DiagramObjectPoint.DiagramObject rdf:resource="#_O2"/><cim:DiagramObjectPoint.sequenceNumber>1</cim:DiagramObjectPoint.sequenceNumber><cim:DiagramObjectPoint.xPosition>300</cim:DiagramObjectPoint.xPosition><cim:DiagramObjectPoint.yPosition>600</cim:DiagramObjectPoint.yPosition></cim:DiagramObjectPoint>
        </rdf:RDF>
        """;

    [Theory]
    [InlineData("negative", 0.0, 1.0)]
    [InlineData("positive", 1.0, 0.0)]
    public void Dl_foreign_coordinates_are_scaled_by_the_bounding_box_and_flipped_for_positive_orientation(string orientation, double y1, double y2)
    {
        // Hand derivation: xmin 100, ymin 200, s = max(300 - 100, 600 - 200) = 400, so (0, 0) and (200 / 400, 400 / 400) = (0.5, 1).
        var nodes = new Dictionary<string, int>(StringComparer.Ordinal) { ["N1"] = 1, ["N2"] = 2 };
        CgmesLayout lay = CgmesDiagramLayout.Read(new[] { ForeignDl(orientation) }, nodes);
        Assert.Equal((0.0, y1), lay.Positions[1]);
        Assert.Equal((0.5, y2), lay.Positions[2]);
    }

    // ---- 4. GL ----------------------------------------------------------------------------------------------------------

    private static GeoSidecar Geo()
    {
        var sites = Enumerable.Range(1, 9).Select(b => new GeoBusSite(b, b == 1 ? 51.5072 : 51.5 + 0.01 * b, b == 1 ? -0.1276 : -0.1 - 0.01 * b)).ToList();
        var routes = new[] { new GeoRoute(2, new[] { (51.51, -0.12), (51.52, -0.125) }) };
        return new GeoSidecar(sites, routes);
    }

    [Fact]
    public void Gl_round_trip_writes_longitude_as_x_and_latitude_as_y_and_reads_back_to_the_bits()
    {
        GridModel m = Wp10Case();
        CgmesWriteResult w = CgmesWriter.Write(m);
        GeoSidecar geo = GeoSidecar.Read(Geo().ToJson());
        string gl = Extra(m, w, CgmesProfileKind.GL, geo);
        Assert.Contains("<cim:CoordinateSystem.crsUrn>urn:ogc:def:crs:EPSG::4326</cim:CoordinateSystem.crsUrn>", gl, StringComparison.Ordinal);
        Assert.Contains("<cim:PositionPoint.xPosition>-0.1276</cim:PositionPoint.xPosition>", gl, StringComparison.Ordinal);
        Assert.Contains("<cim:PositionPoint.yPosition>51.5072</cim:PositionPoint.yPosition>", gl, StringComparison.Ordinal);
        Assert.DoesNotContain("<cim:PositionPoint.xPosition>51.5072</cim:PositionPoint.xPosition>", gl, StringComparison.Ordinal);

        CgmesGeoResult back = CgmesGeographicalLocation.Read(new[] { w.Text, gl }, Bridge(w.Text, m.BaseMva));
        Assert.Equal(geo.ToJson(), back.Geo.ToJson());
        GeoBusSite s1 = back.Geo.Sites[0];
        Assert.True(s1.Bus == 1 && Same(s1.LatDeg, 51.5072) && Same(s1.LonDeg, -0.1276));
        Assert.Equal(2, back.Geo.Lines.Single().Branch);
    }

    [Fact]
    public void Gl_refuses_another_crs_in_the_sidecar_and_in_the_document()
    {
        string json = Geo().ToJson().Replace("EPSG:4326", "EPSG:27700", StringComparison.Ordinal);
        var e1 = Assert.Throws<InvalidDataException>(() => GeoSidecar.Read(json));
        Assert.Contains("EPSG:27700", e1.Message, StringComparison.Ordinal);

        GridModel m = Wp10Case();
        CgmesWriteResult w = CgmesWriter.Write(m);
        string gl = Extra(m, w, CgmesProfileKind.GL, Geo()).Replace("EPSG::4326", "EPSG::27700", StringComparison.Ordinal);
        var e2 = Assert.Throws<InvalidDataException>(() => CgmesGeographicalLocation.Read(new[] { w.Text, gl }, Bridge(w.Text, m.BaseMva)));
        Assert.Contains("urn:ogc:def:crs:EPSG::27700", e2.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Gl_refuses_a_site_no_located_line_touches()
    {
        GridModel m = Wp10Case();
        CgmesWriteResult w = CgmesWriter.Write(m);
        var geo = new GeoSidecar(new[] { new GeoBusSite(1, 51.0, -1.0) }, Array.Empty<GeoRoute>());
        var ex = Assert.Throws<InvalidDataException>(() => CgmesExtraProfiles.Write(m, w, new CgmesExtraOptions(new[] { CgmesProfileKind.GL }, Geo: geo)));
        Assert.Contains("bus 1 has a site but no ACLineSegment", ex.Message, StringComparison.Ordinal);
    }

    // ---- 5. SC ----------------------------------------------------------------------------------------------------------

    private static double AboutValue(string doc, string about, string prop)
    {
        int i = doc.IndexOf("rdf:about=\"#_" + about + "\"", StringComparison.Ordinal);
        Assert.True(i >= 0, about);
        string open = "<cim:" + prop + ">";
        int a = doc.IndexOf(open, i, StringComparison.Ordinal) + open.Length;
        return double.Parse(doc[a..doc.IndexOf('<', a)], NumberStyles.Float, Inv);
    }

    [Fact]
    public void Sc_writes_the_hand_ohm_values_and_reads_back_the_bits_and_the_same_slg_current()
    {
        GridModel m = Wp10Case();
        CgmesWriteResult w = CgmesWriter.Write(m);
        string sc = Extra(m, w, CgmesProfileKind.SC);
        Assert.Equal(60.70275, AboutValue(sc, "L2", "ACLineSegment.r0"), 1e-9);
        Assert.Equal(328.509, AboutValue(sc, "L2", "ACLineSegment.x0"), 1e-9);
        Assert.Equal(0.2, AboutValue(sc, "G1", "SynchronousMachine.satDirectSubtransX"));
        Assert.Equal(0.2, AboutValue(sc, "G1", "SynchronousMachine.x2"));
        Assert.Equal(0.05, AboutValue(sc, "G1", "SynchronousMachine.x0"));

        CimBridgeResult b = Bridge(w.Text, m.BaseMva);
        CgmesShortCircuitResult r = CgmesShortCircuitProfile.Apply(b, new[] { w.Text, sc });
        GridModel got = r.Model;
        Assert.DoesNotContain(CgmesExtraProfiles.Write(m, w, new CgmesExtraOptions(new[] { CgmesProfileKind.SC })).Notes, n => n.Contains("no ohm form", StringComparison.Ordinal));
        Assert.True(Same(0.051, got.Branches[1].R0) && Same(0.276, got.Branches[1].X0));
        for (int k = 0; k < got.Generators.Count; k++)
        {
            Generator g = got.Generators[k];
            Assert.True(Same(0.2, g.SubtransientXdPu) && Same(0.2, g.NegativeSeqXPu) && g.ZeroSequenceZPu == new MachineZeroSequence(0.0, 0.05), $"generator {k + 1}");
        }

        // The same read-back model given the sequence data directly: the SLG current at bus 5 is the same to the bit.
        GridModel direct = b.Model.CopyWith(
            branches: b.Model.Branches.Select((br, k) => k == 1 ? br with { R0 = 0.051, X0 = 0.276 } : br).ToList(),
            generators: b.Model.Generators.Select(g => g with { SubtransientXdPu = 0.2, NegativeSeqXPu = 0.2, ZeroSequenceZPu = new MachineZeroSequence(0.0, 0.05) }).ToList());
        double viaSc = Iec60909.SingleLineToGround(got, 5).IkppKa, viaDirect = Iec60909.SingleLineToGround(direct, 5).IkppKa;
        Assert.True(viaSc > 0 && Same(viaSc, viaDirect), $"{viaSc:R} vs {viaDirect:R}");
        // The bridge's own model is not changed by Apply.
        Assert.Equal(0.0, b.Model.Branches[1].R0);
    }

    private static GridModel TwoBusTransformer(double xgHv, double xgLv)
    {
        double zbHv = 345.0 * 345.0 / 100.0, zbLv = 20.0 * 20.0 / 100.0;
        return new GridModel
        {
            BaseMva = 100,
            Name = "tx2",
            Buses = new[] { new Bus(1, BusType.Slack, BaseKv: 345), new Bus(2, BusType.Pq, Pd: 10, Qd: 2, BaseKv: 20) },
            Generators = new[] { new Generator(1, 10, Vg: 1.0) },
            Branches = new[]
            {
                new Branch(1, 2, 0.001, 0.1, IsTransformer: true, R0: 0.002, X0: 0.09,
                    FromWinding: WindingConnection.WyeGrounded, ToWinding: WindingConnection.WyeGrounded,
                    FromNeutralXPu: xgHv / zbHv, ToNeutralXPu: xgLv / zbLv),
            },
        };
    }

    [Fact]
    public void Sc_transformer_yn_ends_with_a_10_ohm_xground_read_back_as_10_over_zbase()
    {
        GridModel m = TwoBusTransformer(10.0, 10.0);
        CgmesWriteResult w = CgmesWriter.Write(m);
        string sc = Extra(m, w, CgmesProfileKind.SC);
        Assert.Contains("WindingConnection.Yn", sc, StringComparison.Ordinal);
        Assert.Equal(10.0, AboutValue(sc, "T1_e2", "TransformerEnd.xground"), 1e-12);
        CgmesShortCircuitResult r = CgmesShortCircuitProfile.Apply(Bridge(w.Text, 100), new[] { w.Text, sc });
        Branch t = Assert.Single(r.Model.Branches);
        Assert.True(Same(10.0 / (20.0 * 20.0 / 100.0), t.ToNeutralXPu), t.ToNeutralXPu.ToString("R", Inv));
        Assert.True(Same(10.0 / (345.0 * 345.0 / 100.0), t.FromNeutralXPu));
        Assert.Equal(WindingConnection.WyeGrounded, t.FromWinding);
        Assert.True(Same(0.002, t.R0) && Same(0.09, t.X0));

        // A Yn end that is not grounded is refused by name.
        string bad = sc.Replace("<cim:TransformerEnd.grounded>true</cim:TransformerEnd.grounded>", "<cim:TransformerEnd.grounded>false</cim:TransformerEnd.grounded>", StringComparison.Ordinal);
        var ex = Assert.Throws<InvalidDataException>(() => CgmesShortCircuitProfile.Apply(Bridge(w.Text, 100), new[] { w.Text, bad }));
        Assert.Contains("connectionKind Yn with grounded false", ex.Message, StringComparison.Ordinal);
    }

    // ---- 6. DY ----------------------------------------------------------------------------------------------------------

    private const string CanonicalDyr =
        "1 'GENROU' '1' 7.4 0.03 0.4 0.05 5.148 0 1.8 1.75 0.3 0.65 0.23 0.15 0.09 0.38 /\n" +
        "1 'SEXS' '1' 0.1 10 100 0.05 0 4 /\n" +
        "1 'TGOV1' '1' 0.05 0.49 1.1 0 2.1 7 0 /\n" +
        "2 'GENSAL' '1' 6.1 0.05 0.06 6.54 0 1.25 0.7 0.31 0.25 0.12 0 0 /\n" +
        "3 'GENROU' '1' 6 0.04 0.4 0.06 4 0 1.3 1.25 0.18 0.25 0.14 0.1 0 0 /\n" +
        "6 'GENROU' '1' 7 0.03 0.5 0.05 3 0 1.6 1.55 0.25 0.4 0.18 0.12 0 0 /\n" +
        "8 'GENROU' '1' 7 0.03 0.5 0.05 3 0 1.6 1.55 0.25 0.4 0.18 0.12 0 0 /\n";

    private static PsseParseResult Case14() =>
        PsseRawParser.ParseFile(Path.Combine(CliProcess.Root(), "tests", "GridSim.Tests", "fixtures", "psse", "case14.raw"));

    private static (GridModel Model, string Dy, PsseParseResult Raw) DyExport()
    {
        PsseParseResult raw = Case14();
        // CgmesWriter needs base voltages (case14.raw gives 0); the DY document does not use them.
        GridModel m = raw.Model.CopyWith(buses: raw.Model.Buses.Select(b => b with { BaseKv = 138 }).ToList());
        CgmesWriteResult w = CgmesWriter.Write(m);
        CgmesExtraResult x = CgmesExtraProfiles.Write(m, w, new CgmesExtraOptions(new[] { CgmesProfileKind.DY },
            Dynamics: PsseDyrReader.ReadRecords(CanonicalDyr), MachineKeys: CgmesDynamicsProfile.KeysOf(raw),
            MachineArmatureResistances: CgmesDynamicsProfile.ArmatureResistancesOf(raw)));
        return (m, Assert.Single(x.Files).Text, raw);
    }

    [Fact]
    public void Dy_round_trip_gives_the_same_records_and_the_same_dynamics_document_byte_for_byte()
    {
        (GridModel m, string dy, PsseParseResult raw) = DyExport();
        Assert.Contains("RotorKind.roundRotor", dy, StringComparison.Ordinal);
        Assert.Contains("RotorKind.salientPole", dy, StringComparison.Ordinal);
        Assert.Contains("<cim:GovSteam0.mwbase>", dy, StringComparison.Ordinal);

        CgmesDynamicsImport imp = CgmesDynamicsProfile.Read(new[] { dy }, CgmesDynamicsProfile.MachinesOf(m, CgmesDynamicsProfile.KeysOf(raw)));
        Assert.Equal(CanonicalDyr, imp.DyrText);
        IReadOnlyList<PsseDyrRecord> before = PsseDyrReader.ReadRecords(CanonicalDyr);
        Assert.Equal(before.Count, imp.Records.Count);
        for (int i = 0; i < before.Count; i++)
        {
            Assert.Equal((before[i].Bus, before[i].Model, before[i].Id), (imp.Records[i].Bus, imp.Records[i].Model, imp.Records[i].Id));
            for (int j = 0; j < before[i].Values.Count; j++)
                Assert.True(Same(double.Parse(before[i].Values[j], Inv), double.Parse(imp.Records[i].Values[j], Inv)), $"record {i} value {j}");
        }
        var opts = new PsseDyrOptions { SourceName = "case14.dyr" };
        string direct = PsseDyrReader.Read(CanonicalDyr, raw, "case14", opts).Json;
        string viaDy = imp.ToDynamics(raw, "case14", opts).Json;
        Assert.Equal(direct, viaDy);
    }

    [Fact]
    public void Dy_refuses_a_genrou_with_two_subtransient_reactances_govhydro1_and_an_unknown_class_by_name()
    {
        (GridModel m, string dy, PsseParseResult raw) = DyExport();
        var machines = CgmesDynamicsProfile.MachinesOf(m, CgmesDynamicsProfile.KeysOf(raw));
        string twoX = dy.Replace("<cim:SynchronousMachineTimeConstantReactance.xQuadSubtrans>0.23<", "<cim:SynchronousMachineTimeConstantReactance.xQuadSubtrans>0.24<", StringComparison.Ordinal);
        Assert.NotEqual(dy, twoX);
        var e1 = Assert.Throws<InvalidDataException>(() => CgmesDynamicsProfile.Read(new[] { twoX }, machines));
        Assert.Contains("xQuadSubtrans 0.24 differs from xDirectSubtrans 0.23", e1.Message, StringComparison.Ordinal);

        string smd = "_" + CgmesVocabulary.Uuid("DY:G1/machine");
        string extra = $"""
              <cim:GovHydro1 rdf:ID="_hydro"><cim:DynamicsFunctionBlock.enabled>true</cim:DynamicsFunctionBlock.enabled><cim:TurbineGovernorDynamics.SynchronousMachineDynamics rdf:resource="#{smd}" /></cim:GovHydro1>
              <cim:FooDynamics rdf:ID="_foo"><cim:DynamicsFunctionBlock.enabled>true</cim:DynamicsFunctionBlock.enabled></cim:FooDynamics>
            </rdf:RDF>
            """;
        string withOthers = dy.Replace("</rdf:RDF>", extra, StringComparison.Ordinal);
        var e2 = Assert.Throws<InvalidDataException>(() => CgmesDynamicsProfile.Read(new[] { withOthers }, machines));
        Assert.Contains("GovHydro1 'hydro': its correspondence to PSS/E HYGOV is not confirmed", e2.Message, StringComparison.Ordinal);
        Assert.Contains("FooDynamics 'foo': not in the WP10 DY mapping", e2.Message, StringComparison.Ordinal);

        // A DYR model outside the mapping is refused by name on export.
        GridModel mm = raw.Model.CopyWith(buses: raw.Model.Buses.Select(b => b with { BaseKv = 138 }).ToList());
        var e3 = Assert.Throws<InvalidDataException>(() => CgmesExtraProfiles.Write(mm, CgmesWriter.Write(mm), new CgmesExtraOptions(new[] { CgmesProfileKind.DY },
            Dynamics: PsseDyrReader.ReadRecords("1 'GENROU' '1' 7.4 0.03 0.4 0.05 5.148 0 1.8 1.75 0.3 0.65 0.23 0.15 0.09 0.38 /\n1 'IEEET1' '1' 0 400 0.04 7.3 -7.3 1 0.8 0.03 1 0 0 0 0 0 /\n"),
            MachineKeys: CgmesDynamicsProfile.KeysOf(raw), MachineArmatureResistances: CgmesDynamicsProfile.ArmatureResistancesOf(raw))));
        Assert.Contains("IEEET1 for machine '1' at bus 1 (line 2): not in the WP10 DY mapping", e3.Message, StringComparison.Ordinal);
    }

    // ---- CLI ------------------------------------------------------------------------------------------------------------

    [Fact]
    public void Cli_profiles_writes_the_equipment_document_as_convert_does_plus_the_profile_documents()
    {
        string d = TestTemp.Under("gridsim-wp10-cli-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(d);
        string case9 = Path.Combine(CliProcess.Root(), "data", "matpower", "lib", "case9.m");
        CliProcess.Result r = CliProcess.RunBuilt("cgmes-export", case9, "--dir", Path.Combine(d, "out"), "--profiles", "sc");
        Assert.True(r.ExitCode == 0, r.ToString());
        Assert.Equal(new[] { "case9_EQ.xml", "case9_SC.xml" }, Directory.GetFiles(Path.Combine(d, "out")).Select(Path.GetFileName).OrderBy(n => n, StringComparer.Ordinal).ToArray());
        string eq = File.ReadAllText(Path.Combine(d, "out", "case9_EQ.xml"));
        Assert.Equal(CgmesWriter.Write(S10TestSupport.LoadMatpower("lib/case9.m").Model).Text, eq);

        Assert.Equal(2, CliProcess.RunBuilt("cgmes-export", case9, "--dir", Path.Combine(d, "x"), "--profiles", "xx").ExitCode);
        Assert.Equal(2, CliProcess.RunBuilt("cgmes-export", case9, "--dir", Path.Combine(d, "x"), "--profiles", "gl").ExitCode);
        Assert.Equal(2, CliProcess.RunBuilt("cgmes-export", case9, "--dir", Path.Combine(d, "x"), "--profiles", "dl", "--cgmes-version", "3.0").ExitCode);
        CliProcess.Result dl = CliProcess.RunBuilt("cgmes-export", case9, "--dir", Path.Combine(d, "y"), "--profiles", "dl");
        Assert.Equal(1, dl.ExitCode);
        Assert.Contains("every bus is at (0, 0)", dl.StdErr, StringComparison.Ordinal);
    }
}
