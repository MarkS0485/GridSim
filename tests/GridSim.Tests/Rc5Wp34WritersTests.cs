using System.Globalization;
using System.Text;
using System.Text.Json;
using GridSim.Core.Api;
using GridSim.Core.IO.Psse;
using GridSim.Core.IO.Ucte;
using GridSim.Core.Model;
using GridSim.Core.PowerFlow;
using Xunit;

namespace GridSim.Tests;

/// <summary>
/// RC5 parity programme, WP34: the PSS/E RAW revision 36 and RAWX writers, the RAWX device sections, and the UCTE-DEF writer.
/// <para>Sources. RAW 36: the PSS/E 36.0.0 release notes (what revision 36 adds) and GridSim's revision 36 reader, which places
/// fields by the '@!' heading lines. RAWX: PowSyBl's PSS/E model (powsybl-core, MPL-2.0: the FIELD_NAMES_35X lists and
/// PowerFlowRecordGroup) and its RAWX test case IEEE_14_bus_completed_rev35.rawx, checked against surge-io's RAWX reader.
/// UCTE-DEF: "UCTE data exchange format for load flow and three phase short circuit studies", version 02 (2007-05-01), in the
/// columns GridSim's reader reads. No vendor-written revision 36, RAWX or UCTE-DEF file is used.</para>
/// <para>Oracles. A RAW 36 or RAWX write is read back by GridSim's own reader and compared with the RAW revision 35 write of
/// the same model read back the same way: same records, same object-model attributes, same power-flow bits. A UCTE write
/// from a UCTE read is compared bit for bit; a UCTE write from MATPOWER is compared to the precision of the format's columns,
/// with the bound derived from the column widths (stated at <see cref="Half"/>).</para>
/// </summary>
public class Rc5Wp34WritersTests
{
    private static readonly PsseReadOptions Controls = new() { ApplyTapControl = true, ApplyShuntControl = true };

    private const string PhaseShifter = "1, 0, 0, 200, 200, 200, 3, 0, 30, -30, 150, 140, 61, 0, 0, 0, 0";

    public static TheoryData<string> Models()
    {
        var d = new TheoryData<string>();
        foreach (string c in new[] { "case14", "case118", "lcc", "vsc", "mtdc", "facts", "svc", "induction", "flow" }) d.Add(c);
        return d;
    }

    /// <summary>The models: two MATPOWER cases and the device cases of the RAW writer's own tests (PsseRawDeviceWriterTests).
    /// The WP5 revision 35 text is not one: its load has constant-current and constant-admittance parts, which the RAW writer
    /// refuses; it is used for the reader test below instead.</summary>
    private static GridModel Model(string name) => name switch
    {
        "case14" => S10TestSupport.LoadMatpower("lib/case14.m").Model,
        "case118" => S10TestSupport.LoadMatpower("lib/case118.m").Model,
        _ => PsseRawParser.ParseWithDiagnostics(name switch
        {
            "lcc" => PsseLccTests.Case(),
            "vsc" => PsseVscTests.Case(),
            "mtdc" => PsseMtdcTests.Case(),
            "facts" => PsseFactsTests.Case(),
            "svc" => PsseShuntControlTests.TwoBus("2, 0, 1, 1.03, 0.97, 0, 100, '', 0, 4, 10, 2, 20"),
            "induction" => PsseInductionTests.Case(),
            "flow" => PsseFlowControlTests.Parallel(PhaseShifter),
            _ => throw new ArgumentException(name, nameof(name)),
        }, name, Controls with { FrequencyHz = 50 }).Model,
    };

    private static bool Same(double a, double b) => BitConverter.DoubleToInt64Bits(a) == BitConverter.DoubleToInt64Bits(b);

    private static void SameBits(double[] a, double[] b, string what)
    {
        Assert.Equal(a.Length, b.Length);
        for (int i = 0; i < a.Length; i++)
            Assert.True(Same(a[i], b[i]), $"{what} [{i}]: {a[i]:R} vs {b[i]:R}");
    }

    /// <summary>Two read-backs give the same model: every object-model attribute bit for bit, the same records, the same
    /// power-flow bits.</summary>
    private static void SameModel(GridModel a, GridModel b)
    {
        Assert.Null(CaseEditor.AttributeDifference(a, b));
        Assert.Equal(a.Buses, b.Buses);
        Assert.Equal(a.AllGenerators, b.AllGenerators);
        Assert.Equal(a.Branches, b.Branches);
        Assert.Equal(a.LccLinks, b.LccLinks);
        Assert.Equal(a.VscLinks, b.VscLinks);
        Assert.Equal(a.MtdcLinks.Count, b.MtdcLinks.Count);
        for (int k = 0; k < a.MtdcLinks.Count; k++)
        {
            Assert.Equal(a.MtdcLinks[k].Converters, b.MtdcLinks[k].Converters);
            Assert.Equal(a.MtdcLinks[k].DcBuses, b.MtdcLinks[k].DcBuses);
            Assert.Equal(a.MtdcLinks[k].Lines, b.MtdcLinks[k].Lines);
        }
        Assert.Equal(a.FactsDevices, b.FactsDevices);
        Assert.Equal(a.StaticVarCompensators, b.StaticVarCompensators);
        Assert.Equal(a.InductionMachines, b.InductionMachines);
        Assert.Equal(a.Oltcs, b.Oltcs);
        Assert.Equal(a.TransformerFlowControls, b.TransformerFlowControls);
        for (int i = 0; i < a.Buses.Count; i++)
            foreach ((double x, double y, string f) in new[] { (a.Buses[i].Pd, b.Buses[i].Pd, "Pd"), (a.Buses[i].Qd, b.Buses[i].Qd, "Qd"),
                         (a.Buses[i].Gs, b.Buses[i].Gs, "Gs"), (a.Buses[i].Bs, b.Buses[i].Bs, "Bs"), (a.Buses[i].Vm, b.Buses[i].Vm, "Vm") })
                Assert.True(Same(x, y), $"bus {a.Buses[i].Id} {f}: {x:R} vs {y:R}");
        PowerFlowResult ra = new NewtonRaphsonPowerFlow().Solve(a), rb = new NewtonRaphsonPowerFlow().Solve(b);
        Assert.Equal(ra.Converged, rb.Converged);
        Assert.Equal(ra.Iterations, rb.Iterations);
        SameBits(ra.Vm, rb.Vm, "Vm");
        SameBits(ra.VaDeg, rb.VaDeg, "Va");
    }

    // ---- 1. RAW revision 36 -------------------------------------------------------------------------------------------

    [Fact]
    public void Revision_36_is_a_new_member_of_the_writable_revisions_after_the_old_ones()
        => Assert.Equal(new[] { 33, 34, 35, 36 }, PsseRawWriter.WritableRevisions);

    [Theory]
    [MemberData(nameof(Models))]
    public void Write_36_then_read_gives_the_write_35_read_bit_for_bit(string name)
    {
        GridModel m = Model(name);
        PsseWriteResult w35 = PsseRawWriter.Write(m, 35), w36 = PsseRawWriter.Write(m, 36);
        PsseParseResult r35 = PsseRawParser.ParseWithDiagnostics(w35.Text, name, Controls);
        PsseParseResult r36 = PsseRawParser.ParseWithDiagnostics(w36.Text, name, Controls);
        Assert.Equal(35, r35.Revision);
        Assert.Equal(36, r36.Revision);
        Assert.Equal(w35.BranchOrder, w36.BranchOrder);
        SameModel(r35.Model, r36.Model);
    }

    [Theory]
    [MemberData(nameof(Models))]
    public void Write_36_then_read_gives_the_source_model_in_file_order(string name)
    {
        // The source in the file's order, on the attributes RAW carries (it carries no cost curves, names of units or
        // dynamics; see the writer's notes). Compared field by field, bit for bit.
        GridModel m = Model(name);
        PsseWriteResult w36 = PsseRawWriter.Write(m, 36);
        GridModel expected = PsseRawWriter.InFileOrder(m, w36);
        GridModel back = PsseRawParser.ParseWithDiagnostics(w36.Text, name, Controls).Model;
        Assert.Equal(expected.Buses.Count, back.Buses.Count);
        for (int i = 0; i < expected.Buses.Count; i++)
        {
            Bus e = expected.Buses[i], g = back.Buses[i];
            Assert.Equal((e.Id, e.Type), (g.Id, g.Type));
            foreach ((double x, double y, string f) in new[] { (e.Pd, g.Pd, "Pd"), (e.Qd, g.Qd, "Qd"), (e.Gs, g.Gs, "Gs"), (e.Bs, g.Bs, "Bs"),
                         (e.BaseKv, g.BaseKv, "BaseKv"), (e.Vm, g.Vm, "Vm"), (e.VaDeg, g.VaDeg, "Va") })
                Assert.True(Same(x, y), $"bus {e.Id} {f}: {x:R} vs {y:R}");
        }
        Assert.Equal(expected.Branches.Count, back.Branches.Count);
        for (int k = 0; k < expected.Branches.Count; k++)
        {
            Branch e = expected.Branches[k], g = back.Branches[k];
            Assert.Equal((e.From, e.To, e.InService), (g.From, g.To, g.InService));
            // A line's ratio is written as none; the reader gives 1, the solver's reading of MATPOWER's 0.
            foreach ((double x, double y, string f) in new[] { (e.R, g.R, "R"), (e.X, g.X, "X"), (e.B, g.B, "B"),
                         (e.TapRatio == 0 ? 1.0 : e.TapRatio, g.TapRatio == 0 ? 1.0 : g.TapRatio, "ratio"), (e.PhaseShiftDeg, g.PhaseShiftDeg, "shift"),
                         (e.RateAMva, g.RateAMva, "rate A") })
                Assert.True(Same(x, y), $"branch {k + 1} {f}: {x:R} vs {y:R}");
        }
        Assert.Equal(expected.AllGenerators.Count, back.AllGenerators.Count);
        for (int k = 0; k < expected.AllGenerators.Count; k++)
        {
            Generator e = expected.AllGenerators[k], g = back.AllGenerators[k];
            Assert.Equal((e.Bus, e.InService), (g.Bus, g.InService));
            foreach ((double x, double y, string f) in new[] { (e.Pg, g.Pg, "Pg"), (e.Qg, g.Qg, "Qg"), (e.Vg, g.Vg, "Vg"), (e.Qmin, g.Qmin, "Qmin"),
                         (e.Qmax, g.Qmax, "Qmax"), (e.Pmin, g.Pmin, "Pmin"), (e.Pmax, g.Pmax, "Pmax"), (e.MBase, g.MBase, "MBase") })
                Assert.True(Same(x, y), $"generator {k + 1} {f}: {x:R} vs {y:R}");
        }
        PowerFlowResult a = new NewtonRaphsonPowerFlow().Solve(expected), b = new NewtonRaphsonPowerFlow().Solve(back);
        Assert.Equal(a.Converged, b.Converged);
        SameBits(a.Vm, b.Vm, "Vm");
        SameBits(a.VaDeg, b.VaDeg, "Va");
    }

    [Fact]
    public void Revision_36_text_is_the_revision_35_text_plus_heading_lines_and_the_revision_number()
    {
        GridModel m = Model("case118");
        PsseWriteResult w35 = PsseRawWriter.Write(m, 35), w36 = PsseRawWriter.Write(m, 36);
        string[] l36 = w36.Text.Split('\n');
        Assert.StartsWith("0, 100, 36, ", l36[0], StringComparison.Ordinal);
        Assert.Contains("PSS/E-36", l36[0], StringComparison.Ordinal);
        // Every record group the revision 36 reader remaps by heading has its heading line straight after its terminator.
        foreach (string section in new[] { "BUS", "LOAD", "FIXED SHUNT", "GENERATOR", "BRANCH", "SYSTEM SWITCHING DEVICE", "SWITCHED SHUNT", "INDUCTION MACHINE" })
        {
            int at = Array.FindIndex(l36, l => l.StartsWith("0 / END OF ", StringComparison.Ordinal) && l.EndsWith($"BEGIN {section} DATA", StringComparison.Ordinal));
            Assert.True(at > 0, section);
            Assert.StartsWith("@!", l36[at + 1], StringComparison.Ordinal);
        }
        // Remove the heading lines and put REV back: the revision 35 text, byte for byte.
        string[] back = l36.Where(l => !l.StartsWith("@!", StringComparison.Ordinal)).ToArray();
        back[0] = back[0].Replace("0, 100, 36, ", "0, 100, 35, ", StringComparison.Ordinal).Replace("PSS/E-36", "PSS/E-35", StringComparison.Ordinal);
        Assert.Equal(w35.Text, string.Join("\n", back));
        Assert.Equal(w35.Notes.Count + 1, w36.Notes.Count);
    }

    [Fact]
    public void Revision_37_and_32_are_still_refused()
    {
        GridModel m = Model("case14");
        Assert.Throws<ArgumentOutOfRangeException>(() => PsseRawWriter.Write(m, 37));
        Assert.Throws<ArgumentOutOfRangeException>(() => PsseRawWriter.Write(m, 32));
    }

    // ---- 2. RAWX ------------------------------------------------------------------------------------------------------

    [Theory]
    [MemberData(nameof(Models))]
    public void Write_rawx_then_read_gives_the_write_35_read_bit_for_bit(string name)
    {
        GridModel m = Model(name);
        PsseWriteResult w35 = PsseRawWriter.Write(m, 35), wx = PsseRawxWriter.Write(m);
        PsseParseResult r35 = PsseRawParser.ParseWithDiagnostics(w35.Text, name, Controls);
        PsseParseResult rx = PsseRawParser.ParseWithDiagnostics(wx.Text, name, Controls);
        Assert.Equal(35, rx.Revision);
        Assert.Equal(w35.BranchOrder, wx.BranchOrder);
        SameModel(r35.Model, rx.Model);
    }

    [Fact]
    public void Every_rawx_number_is_a_round_trip_token()
    {
        // "R" (round-trip) form: parsing the token and formatting it again gives the token back, so no digit was lost.
        string json = PsseRawxWriter.Write(Model("case118")).Text;
        using JsonDocument doc = JsonDocument.Parse(json);
        int numbers = 0;
        void Walk(JsonElement e)
        {
            switch (e.ValueKind)
            {
                case JsonValueKind.Object: foreach (JsonProperty p in e.EnumerateObject()) Walk(p.Value); break;
                case JsonValueKind.Array: foreach (JsonElement x in e.EnumerateArray()) Walk(x); break;
                case JsonValueKind.Number:
                    string t = e.GetRawText();
                    Assert.Equal(t, double.Parse(t, NumberStyles.Float, CultureInfo.InvariantCulture).ToString("R", CultureInfo.InvariantCulture));
                    numbers++;
                    break;
            }
        }
        Walk(doc.RootElement);
        Assert.True(numbers > 1000, $"only {numbers} numbers");
    }

    [Theory]
    [InlineData("lcc", "twotermdc")]
    [InlineData("vsc", "vscdc")]
    [InlineData("mtdc", "ntermdc")]
    [InlineData("facts", "facts")]
    public void A_device_rawx_with_every_field_and_section_order_reversed_reads_the_same(string name, string section)
    {
        // Fields are placed by name: reverse every section's field list (and its rows with it) and the section order.
        GridModel m = Model(name);
        PsseWriteResult wx = PsseRawxWriter.Write(m);
        Assert.Contains($"\"{section}\"", wx.Text, StringComparison.Ordinal);
        GridModel straight = PsseRawParser.ParseWithDiagnostics(wx.Text, name, Controls).Model;
        GridModel reversed = PsseRawParser.ParseWithDiagnostics(Reversed(wx.Text), name, Controls).Model;
        SameModel(straight, reversed);
    }

    private static string Reversed(string json)
    {
        using JsonDocument doc = JsonDocument.Parse(json);
        using var ms = new MemoryStream();
        using (var w = new Utf8JsonWriter(ms))
        {
            w.WriteStartObject();
            w.WritePropertyName("network");
            w.WriteStartObject();
            foreach (JsonProperty p in Enumerable.Reverse(doc.RootElement.GetProperty("network").EnumerateObject().ToList()))
            {
                w.WritePropertyName(p.Name);
                w.WriteStartObject();
                w.WritePropertyName("fields");
                w.WriteStartArray();
                foreach (JsonElement f in Enumerable.Reverse(p.Value.GetProperty("fields").EnumerateArray().ToList())) f.WriteTo(w);
                w.WriteEndArray();
                w.WritePropertyName("data");
                w.WriteStartArray();
                if (p.Name == "caseid")
                    foreach (JsonElement v in Enumerable.Reverse(p.Value.GetProperty("data").EnumerateArray().ToList())) v.WriteTo(w);
                else
                    foreach (JsonElement row in p.Value.GetProperty("data").EnumerateArray())
                    {
                        w.WriteStartArray();
                        foreach (JsonElement v in Enumerable.Reverse(row.EnumerateArray().ToList())) v.WriteTo(w);
                        w.WriteEndArray();
                    }
                w.WriteEndArray();
                w.WriteEndObject();
            }
            w.WriteEndObject();
            w.WriteEndObject();
        }
        return Encoding.UTF8.GetString(ms.ToArray());
    }

    // ---- 3. RAWX device sections, hand-written -----------------------------------------------------------------------

    // A three-bus case with a two-terminal LCC link (the values of PsseLccTests.Case), an impedance correction table that no
    // transformer uses, a multi-section line grouping and an inter-area transfer, written once as revision 35 RAW and once
    // as RAWX with the field names of the public RAWX layout (PowSyBl's IEEE_14_bus_completed_rev35.rawx), the fields in
    // another order and the defaults written out.
    private const string LccRaw35 = """
        0, 100, 35, 0, 0, 50     / PSS/E-35 RAW, WP34 RAWX check
        GridSim WP34 RAWX
        two-terminal DC
        0 / END OF SYSTEM-WIDE DATA, BEGIN BUS DATA
        1, 'SRC', 345, 3, 1, 1, 1, 1.0, 0
        2, 'REC', 345, 1, 1, 1, 1, 1.0, 0
        3, 'INV', 345, 1, 1, 1, 1, 1.0, 0
        0 / END OF BUS DATA, BEGIN LOAD DATA
        0 / END OF LOAD DATA, BEGIN FIXED SHUNT DATA
        0 / END OF FIXED SHUNT DATA, BEGIN GENERATOR DATA
        1, '1', 0, 0, 999, -999, 1, 0, 0, 1000, 0, 0.01, 0, 0, 1, 1, 100, 9999, -9999
        0 / END OF GENERATOR DATA, BEGIN BRANCH DATA
        1, 2, '1', 0, 0.01, 0, '', 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 1
        1, 3, '1', 0, 0.01, 0, '', 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 1
        0 / END OF BRANCH DATA, BEGIN SYSTEM SWITCHING DEVICE DATA
        0 / END OF SYSTEM SWITCHING DEVICE DATA, BEGIN TRANSFORMER DATA
        0 / END OF TRANSFORMER DATA, BEGIN AREA DATA
        0 / END OF AREA DATA, BEGIN TWO-TERMINAL DC DATA
        'LINK A', 1, 10, 100, 500, 0, 0, 0.15, 'I', 0, 20, 1
        2, 2, 25, 10, 0, 5, 345, 0.56, 1.0, 1.1, 0.9, 0.00625, 0, 0, 0, 0, '1', 0
        3, 2, 25, 18, 0, 5, 345, 0.56, 1.0, 1.1, 0.9, 0.00625, 0, 0, 0, 0, '1', 0
        0 / END OF TWO-TERMINAL DC DATA, BEGIN VOLTAGE SOURCE CONVERTER DATA
        0 / END OF VOLTAGE SOURCE CONVERTER DATA, BEGIN IMPEDANCE CORRECTION DATA
        1, 0.9, 1.1, 0, 1.1, 0.95, 0, 0, 0, 0
        0 / END OF IMPEDANCE CORRECTION DATA, BEGIN MULTI-TERMINAL DC DATA
        0 / END OF MULTI-TERMINAL DC DATA, BEGIN MULTI-SECTION LINE DATA
        1, 3, '&1', 1, 2
        0 / END OF MULTI-SECTION LINE DATA, BEGIN ZONE DATA
        0 / END OF ZONE DATA, BEGIN INTER-AREA TRANSFER DATA
        1, 1, 'A', 0
        0 / END OF INTER-AREA TRANSFER DATA, BEGIN OWNER DATA
        0 / END OF OWNER DATA, BEGIN FACTS CONTROL DEVICE DATA
        0 / END OF FACTS CONTROL DEVICE DATA, BEGIN SWITCHED SHUNT DATA
        0 / END OF SWITCHED SHUNT DATA, BEGIN GNE DEVICE DATA
        0 / END OF GNE DEVICE DATA, BEGIN INDUCTION MACHINE DATA
        0 / END OF INDUCTION MACHINE DATA, BEGIN SUBSTATION DATA
        0 / END OF SUBSTATION DATA
        Q
        """;

    private const string LccRawx = """
        {"network":{
          "caseid":{"fields":["title1","title2","rev","ic","sbase","xfrrat","nxfrat","basfrq"],"data":["GridSim WP34 RAWX","two-terminal DC",35,0,100,0,0,50]},
          "bus":{"fields":["ibus","name","baskv","ide","area","zone","owner","vm","va"],
                 "data":[[1,"SRC",345,3,1,1,1,1.0,0],[2,"REC",345,1,1,1,1,1.0,0],[3,"INV",345,1,1,1,1,1.0,0]]},
          "generator":{"fields":["machid","ibus","pg","qg","qt","qb","vs","ireg","nreg","mbase","zr","zx","rt","xt","gtap","stat","rmpct","pt","pb"],
                 "data":[["1",1,0,0,999,-999,1,0,0,1000,0,0.01,0,0,1,1,100,9999,-9999]]},
          "acline":{"fields":["xpu","rpu","ibus","jbus","ckt","bpu","stat"],"data":[[0.01,0,1,2,"1",0,1],[0.01,0,1,3,"1",0,1]]},
          "twotermdc":{"fields":["ipi","nbi","anmxi","anmni","rci","xci","ebasi","tri","tapi","tmxi","tmni","stpi","ici","ndi","ifi","iti","idi","xcapi",
                                 "name","mdc","rdc","setvl","vschd","vcmod","rcomp","delti","met","dcvmin","cccitmx","cccacc",
                                 "ipr","nbr","anmxr","anmnr","rcr","xcr","ebasr","trr","tapr","tmxr","tmnr","stpr","icr","ndr","ifr","itr","idr","xcapr"],
                 "data":[[3,2,25,18,0,5,345,0.56,1.0,1.1,0.9,0.00625,0,0,0,0,"1",0,
                          "LINK A",1,10,100,500,0,0,0.15,"I",0,20,1,
                          2,2,25,10,0,5,345,0.56,1.0,1.1,0.9,0.00625,0,0,0,0,"1",0]]},
          "impcor":{"fields":["itable","tap","refact","imfact"],"data":[[1,0.9,1.1,0],[1,1.1,0.95,0]]},
          "msline":{"fields":["ibus","jbus","mslid","met","dum1"],"data":[[1,3,"&1",1,2]]},
          "iatransfer":{"fields":["arfrom","arto","trid","ptran"],"data":[[1,1,"A",0]]}
        }}
        """;

    [Fact]
    public void A_hand_written_rawx_with_the_device_sections_reads_as_its_revision_35_equivalent()
    {
        PsseParseResult raw = PsseRawParser.ParseWithDiagnostics(LccRaw35, "wp34");
        PsseParseResult rawx = PsseRawParser.ParseWithDiagnostics(LccRawx, "wp34");
        Assert.Single(raw.Model.LccLinks);
        SameModel(raw.Model, rawx.Model);
        Assert.Equal(raw.Notes.Count(n => n.Contains("multi-section", StringComparison.Ordinal)), rawx.Notes.Count(n => n.Contains("multi-section", StringComparison.Ordinal)));
        Assert.Equal(raw.Notes.Count(n => n.Contains("inter-area", StringComparison.Ordinal)), rawx.Notes.Count(n => n.Contains("inter-area", StringComparison.Ordinal)));
        Assert.Equal(raw.Notes.Count(n => n.Contains("impedance correction", StringComparison.Ordinal)), rawx.Notes.Count(n => n.Contains("impedance correction", StringComparison.Ordinal)));
    }

    [Theory]
    [InlineData("\"ipr\"", "\"ipx\"", "twotermdc")]                       // a rectifier bus the layout does not name
    [InlineData("\"impcor\":{\"fields\":[\"itable\"", "\"impcor\":{\"fields\":[\"itablex\"", "impcor")]
    public void A_device_row_that_cannot_be_placed_is_refused_naming_its_section(string from, string to, string section)
    {
        var ex = Assert.Throws<InvalidDataException>(() => PsseRawParser.ParseWithDiagnostics(LccRawx.Replace(from, to, StringComparison.Ordinal), "wp34"));
        Assert.Contains(section, ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Multi_terminal_rows_must_match_their_link_counts()
    {
        string json = PsseRawxWriter.Write(Model("mtdc")).Text;
        using JsonDocument doc = JsonDocument.Parse(json);
        // Drop the last converter row: the link's NCONV no longer matches, and the read names both.
        JsonElement conv = doc.RootElement.GetProperty("network").GetProperty("ntermdcconv").GetProperty("data");
        string lastRow = conv[conv.GetArrayLength() - 1].GetRawText();
        int at = json.LastIndexOf(lastRow, StringComparison.Ordinal);
        Assert.True(at > 0);
        string cut = json.Remove(at, lastRow.Length);
        cut = cut.Remove(cut.LastIndexOf(',', at - 1), 1);   // the comma before the dropped row
        var ex = Assert.Throws<InvalidDataException>(() => PsseRawParser.ParseWithDiagnostics(cut, "mtdc", Controls));
        Assert.Contains("ntermdcconv", ex.Message, StringComparison.Ordinal);
        Assert.Contains("nconv", ex.Message, StringComparison.Ordinal);
    }

    // ---- 5. UCTE-DEF round trip from a UCTE read ----------------------------------------------------------------------

    private static string Fixture(string name) => Path.Combine(CliProcess.Root(), "tests", "GridSim.Tests", "fixtures", "ucte", name);

    [Fact]
    public void A_ucte_read_written_and_read_again_gives_the_same_model_bit_for_bit()
    {
        UcteParseResult r = UcteParser.ParseFile(Fixture("small.uct"));
        UcteWriteResult w = UcteWriter.Write(r.Model, new UcteWriteOptions { Source = r });
        UcteParseResult back = UcteParser.ParseWithDiagnostics(w.Text, r.Model.Name);
        Assert.Null(CaseEditor.AttributeDifference(r.Model, back.Model));
        Assert.Equal(r.Model.Buses, back.Model.Buses);
        Assert.Equal(r.Model.AllGenerators, back.Model.AllGenerators);
        Assert.Equal(r.Model.Branches, back.Model.Branches);
        foreach (Bus b in r.Model.Buses)
        {
            Bus c = back.Model.Buses.Single(x => x.Id == b.Id);
            Assert.True(Same(b.Gs, c.Gs) && Same(b.Bs, c.Bs) && Same(b.Vm, c.Vm), $"bus {b.Id}");
        }
        for (int k = 0; k < r.Model.Branches.Count; k++)
        {
            Branch e = r.Model.Branches[k], g = back.Model.Branches[k];
            Assert.True(Same(e.R, g.R) && Same(e.X, g.X) && Same(e.B, g.B) && Same(e.TapRatio, g.TapRatio) && Same(e.PhaseShiftDeg, g.PhaseShiftDeg) && Same(e.RateAMva, g.RateAMva),
                $"branch {k + 1}");
        }
        Assert.Equal(r.Nodes.OrderBy(kv => kv.Key), back.Nodes.OrderBy(kv => kv.Key));
        Assert.Equal(r.BranchKeys, back.BranchKeys);
    }

    [Fact]
    public void A_ucte_write_is_canonical_writing_its_read_again_gives_the_same_bytes()
    {
        // small.uct itself is not in the writer's canonical form (it has a comment line of its own, an out-of-service line
        // and an ##E record the model does not keep, and numbers with other decimals), so the byte check is on the second
        // generation: write, read, write again.
        UcteParseResult r = UcteParser.ParseFile(Fixture("small.uct"));
        UcteWriteResult w1 = UcteWriter.Write(r.Model, new UcteWriteOptions { Source = r });
        UcteParseResult r1 = UcteParser.ParseWithDiagnostics(w1.Text, r.Model.Name);
        UcteWriteResult w2 = UcteWriter.Write(r1.Model, new UcteWriteOptions { Source = r1 });
        Assert.Equal(w1.Text, w2.Text);
        Assert.Contains("##R", w1.Text, StringComparison.Ordinal);    // the off-nominal transformer's regulation
        Assert.Contains("FCCCCC21 FCCCCC22 1 2", w1.Text, StringComparison.Ordinal);   // the busbar coupler keeps status 2
        Assert.Contains("##ZXX", w1.Text, StringComparison.Ordinal);
    }

    [Fact]
    public void The_ucte_reader_reads_small_uct_as_it_did()
    {
        // The reader is unchanged by WP34: two of the fixture's figures, by the reader's documented arithmetic (vm = U / Ub,
        // R pu = R ohm / (Ub^2 / 100)).
        UcteParseResult r = UcteParser.ParseFile(Fixture("small.uct"));
        Assert.Equal(5, r.Model.Buses.Count);
        Bus a = r.Model.Buses[0];
        Assert.Equal(400.0 / 380.0, a.Vm);
        Branch line = r.Model.Branches[0];
        Assert.Equal(1.4440 / (380.0 * 380.0 / 100.0), line.R);
    }

    // ---- 6. UCTE-DEF from MATPOWER ------------------------------------------------------------------------------------

    /// <summary>case14 at UCTE voltage levels: MATPOWER's case14 has base kV 0, which UCTE-DEF cannot hold; buses 1 to 5
    /// (the 4-7, 4-9 and 5-6 transformers' high side) at 220 kV, buses 6 to 14 at 110 kV. Per-unit data is unchanged.</summary>
    private static GridModel Case14AtUcteLevels()
    {
        GridModel m = S10TestSupport.LoadMatpower("lib/case14.m").Model;
        return m.CopyWith(buses: m.Buses.Select(b => b with { BaseKv = b.Id <= 5 ? 220.0 : 110.0 }).ToList());
    }

    /// <summary>
    /// The largest error of a value written in <paramref name="width"/> columns. The writer writes as many decimals d as fit:
    /// sign, integer digits, the point, d decimals; rounding to d decimals is off by at most half a unit in the last place,
    /// 0.5 x 10^-d. Here d is taken one smaller than the columns allow, for a rounding carry into a new integer digit
    /// (9.99996 to 10.0000), so the bound is safe by a factor of 10 at worst.
    /// </summary>
    private static double Half(double v, int width)
    {
        double a = Math.Abs(v);
        int intDigits = a < 1 ? 1 : (int)Math.Floor(Math.Log10(a)) + 1;
        int d = width - (v < 0 ? 1 : 0) - intDigits - 2;
        Assert.True(d >= 0, $"{v:R} leaves no decimal in {width} columns");
        return 0.5 * Math.Pow(10, -d);
    }

    /// <summary>|e - g| within the column bound plus four units in the last place of the conversion arithmetic.</summary>
    private static void Close(double e, double g, double bound, string what)
    {
        double ulp = Math.BitIncrement(Math.Max(Math.Abs(e), Math.Abs(g))) - Math.Max(Math.Abs(e), Math.Abs(g));
        Assert.True(Math.Abs(e - g) <= bound + 4 * ulp, $"{what}: {e:R} vs {g:R}, bound {bound:R}");
    }

    [Fact]
    public void Case14_written_as_ucte_reads_back_to_the_column_precision()
    {
        GridModel m = Case14AtUcteLevels();
        UcteWriteResult w = UcteWriter.Write(m, new UcteWriteOptions { Country = "FR" });
        GridModel expected = UcteWriter.InFileOrder(m, w);
        GridModel back = UcteParser.ParseWithDiagnostics(w.Text, "case14", new UcteReadOptions { FrequencyHz = m.FrequencyHz }).Model;
        Assert.Equal("F0000121", w.NodeCodes[0]);   // F (France, as asked), bus 1 in five digits, level 2 (220 kV), busbar 1
        Assert.Equal("F0001451", w.NodeCodes[13]);  // bus 14, level 5 (110 kV)
        Assert.Equal(expected.Buses.Count, back.Buses.Count);
        Assert.Equal(expected.Generators.Count, back.Generators.Count);
        Assert.Equal(expected.Branches.Count, back.Branches.Count);

        for (int i = 0; i < expected.Buses.Count; i++)
        {
            Bus e = expected.Buses[i], g = back.Buses[i];
            string who = $"bus {e.Id}";
            Assert.Equal((e.Id, e.Type, e.BaseKv), (g.Id, g.Type, g.BaseKv));
            // Every case14 load has at most four decimals and fits its seven columns as written, so it reads back to the bit.
            Assert.True(Same(e.Pd, g.Pd) && Same(e.Qd, g.Qd), $"{who} load {e.Pd:R} + j{e.Qd:R} vs {g.Pd:R} + j{g.Qd:R}");
            double u2 = 1e-6 * e.BaseKv * e.BaseKv;
            Close(e.Gs, g.Gs, Half(e.Gs / u2, 6) * u2, who + " Gs");
            Close(e.Bs, g.Bs, Half(e.Bs / u2, 8) * u2, who + " Bs");
        }
        for (int k = 0; k < expected.Generators.Count; k++)
        {
            Generator e = expected.Generators[k], g = back.Generators[k];
            string who = $"unit at bus {e.Bus}";
            Assert.Equal(e.Bus, g.Bus);
            double kv = expected.Buses.Single(b => b.Id == e.Bus).BaseKv;
            Close(e.Vg, g.Vg, Half(e.Vg * kv, 6) / kv, who + " Vg");
            foreach ((double x, double y, string f) in new[] { (e.Pg, g.Pg, "Pg"), (e.Qg, g.Qg, "Qg"), (e.Pmin, g.Pmin, "Pmin"), (e.Pmax, g.Pmax, "Pmax"),
                         (e.Qmin, g.Qmin, "Qmin"), (e.Qmax, g.Qmax, "Qmax") })
                Close(x, y, Half(0.0 - x, 7), $"{who} {f}");   // written with the load sign, so its sign takes a column
        }
        for (int k = 0; k < expected.Branches.Count; k++)
        {
            Branch e = expected.Branches[k], g = back.Branches[k];
            string who = $"branch {k + 1} ({e.From}-{e.To})";
            Assert.Equal((e.From, e.To), (g.From, g.To));
            bool transformer = (e.TapRatio != 0 && e.TapRatio != 1) || e.IsTransformer;
            Assert.Equal(transformer, g.IsTransformer);
            // Impedances in ohm on the base of the bus whose voltage refers them (a line's ends; a transformer's node 1 = to bus).
            double kv = expected.Buses.Single(b => b.Id == e.To).BaseKv, zb = kv * kv / 100.0;
            Close(e.R, g.R, Half(e.R * zb, 6) / zb, who + " R");
            Close(e.X, g.X, Half(e.X * zb, 6) / zb, who + " X");
            Close(e.B, g.B, Half(e.B / zb / 1e-6, 8) * 1e-6 * zb, who + " B");
            double t = e.TapRatio == 0 ? 1.0 : e.TapRatio;
            // The ratio comes back through the five du columns of a one-step ##R: t' = 1 + du / 100.
            Close(t, g.TapRatio, t == 1.0 ? 0 : Half(100.0 * (t - 1.0), 5) / 100.0, who + " ratio");
            Assert.Equal(0.0, g.PhaseShiftDeg);
        }
    }

    [Fact]
    public void Case14_as_matpower_gives_its_base_voltage_problem_by_name()
    {
        GridModel m = S10TestSupport.LoadMatpower("lib/case14.m").Model;   // base kV 0 throughout
        var ex = Assert.Throws<InvalidDataException>(() => UcteWriter.Write(m));
        Assert.Contains("base voltage", ex.Message, StringComparison.Ordinal);
        Assert.Contains("bus 1", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_bus_shunt_with_no_transformer_to_carry_it_is_refused()
    {
        GridModel m = Case14AtUcteLevels();
        m = m.CopyWith(buses: m.Buses.Select(b => b.Id == 14 ? b with { Bs = 5 } : b).ToList());
        var ex = Assert.Throws<InvalidDataException>(() => UcteWriter.Write(m));
        Assert.Contains("bus 14", ex.Message, StringComparison.Ordinal);
        Assert.Contains("node shunt", ex.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(1.4440, 6, "1.4440")]
    [InlineData(400.0, 6, "400.00")]
    [InlineData(-500.0, 7, "-500.00")]
    [InlineData(-0.0, 7, "0.00000")]
    [InlineData(-0.000001, 6, "0.000")]    // rounds to zero at the decimals a sign leaves room for: written without the sign
    [InlineData(9.99996, 6, "10.000")]     // the carry: one decimal fewer
    public void A_number_takes_as_many_decimals_as_its_columns_hold(double v, int width, string expected)
        => Assert.Equal(expected, UcteWriter.Fixed(v, width, "test"));

    [Fact]
    public void A_number_too_large_for_its_columns_is_refused()
    {
        var ex = Assert.Throws<InvalidDataException>(() => UcteWriter.Fixed(1234567.0, 6, "test field"));
        Assert.Contains("test field", ex.Message, StringComparison.Ordinal);
    }
}
