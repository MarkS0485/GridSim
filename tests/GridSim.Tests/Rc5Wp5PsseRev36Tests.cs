using System.Globalization;
using System.Numerics;
using System.Text;
using GridSim.Core.IO.Psse;
using GridSim.Core.Model;
using GridSim.Core.PowerFlow;
using Xunit;

namespace GridSim.Tests;

/// <summary>
/// RC5 parity programme, WP5: PSS/E RAW revision 36 and RAWX (JSON) import.
/// <para>No vendor-written revision 36 or RAWX file is used. One small network is written as data (field name to value) and
/// rendered four ways: as revision 35 text in the documented field order (the reference), as revision 36 text whose
/// '@!' heading lines name the fields in another order and add the revision 36 items (NAME on load, fixed shunt, generator and
/// switched shunt, DROOPNAME on the generator, BYPASS on the branch, rating-table names on the switching device) plus the
/// revision 36 sections (droop control, rating table, load type, interface, interface element), and as RAWX JSON with the
/// field names RAWX uses. The sources for the revision 36 changes are the PSS/E 36.0.0 release notes ("Power Flow Data (RAW
/// and RAWX formats) File Changes"); the revision 35 order is the layout the existing reader and its tests pin. The check is
/// that every rendering reads to the same model as the revision 35 text, bit for bit, with the same Y-bus and the same
/// load-flow solution.</para>
/// </summary>
public class Rc5Wp5PsseRev36Tests
{
    // ---- The network as data ---------------------------------------------------------------------------------------

    private static readonly string[] TextFields = { "ID", "LOADTYPE", "CKT", "NAME", "RMIDNT", "VECGRP", "DROOPNAME", "SWDRATNAM", "ARNAME", "ZONAME", "OWNAME" };

    private static readonly string[] BusF = { "I", "NAME", "BASKV", "IDE", "AREA", "ZONE", "OWNER", "VM", "VA" };
    private static readonly string[] LoadF = { "I", "ID", "STATUS", "AREA", "ZONE", "PL", "QL", "IP", "IQ", "YP", "YQ", "OWNER", "SCALE", "INTRPT", "DGENP", "DGENQ", "DGENF", "LOADTYPE" };
    private static readonly string[] ShuntF = { "I", "ID", "STATUS", "GL", "BL" };
    private static readonly string[] GenF = { "I", "ID", "PG", "QG", "QT", "QB", "VS", "IREG", "NREG", "MBASE", "ZR", "ZX", "RT", "XT", "GTAP", "STAT", "RMPCT", "PT", "PB", "BASLOD" };
    private static readonly string[] BranchF = { "I", "J", "CKT", "R", "X", "B", "NAME", "RATE1", "RATE2", "RATE3", "RATE4", "RATE5", "RATE6", "RATE7", "RATE8", "RATE9", "RATE10", "RATE11", "RATE12", "GI", "BI", "GJ", "BJ", "ST", "MET", "LEN" };
    private static readonly string[] SwitchF = { "I", "J", "CKT", "X", "RATE1", "RATE2", "RATE3", "RATE4", "RATE5", "RATE6", "RATE7", "RATE8", "RATE9", "RATE10", "RATE11", "RATE12", "STAT", "NSTAT", "MET", "STYPE", "NAME" };
    private static readonly string[] SwShuntF = { "I", "ID", "MODSW", "ADJM", "STAT", "VSWHI", "VSWLO", "SWREG", "NREG", "RMPCT", "RMIDNT", "BINIT", "S1", "N1", "B1" };
    private static readonly string[] AreaF = { "I", "ISW", "PDES", "PTOL", "ARNAME" };

    private sealed record Rec(string[] Fields, Dictionary<string, string> V);

    private static Rec R(string[] fields, params (string K, string V)[] v) => new(fields, v.ToDictionary(x => x.K, x => x.V));

    private static readonly Rec[] Buses =
    {
        R(BusF, ("I", "1"), ("NAME", "SLACK"), ("BASKV", "132"), ("IDE", "3"), ("AREA", "1"), ("ZONE", "1"), ("OWNER", "1"), ("VM", "1.02"), ("VA", "0")),
        R(BusF, ("I", "2"), ("NAME", "MID"), ("BASKV", "132"), ("IDE", "1"), ("AREA", "1"), ("ZONE", "1"), ("OWNER", "1"), ("VM", "1"), ("VA", "0")),
        R(BusF, ("I", "3"), ("NAME", "LOAD"), ("BASKV", "33"), ("IDE", "1"), ("AREA", "1"), ("ZONE", "1"), ("OWNER", "1"), ("VM", "1"), ("VA", "0")),
        R(BusF, ("I", "4"), ("NAME", "TIE"), ("BASKV", "132"), ("IDE", "1"), ("AREA", "1"), ("ZONE", "1"), ("OWNER", "1"), ("VM", "1"), ("VA", "0")),
    };

    private static readonly Rec[] Loads =
    {
        R(LoadF, ("I", "3"), ("ID", "1"), ("STATUS", "1"), ("AREA", "1"), ("ZONE", "1"), ("PL", "40"), ("QL", "12"), ("IP", "2.5"), ("IQ", "1.5"),
            ("YP", "0.5"), ("YQ", "-0.25"), ("OWNER", "1"), ("SCALE", "1"), ("INTRPT", "0"), ("DGENP", "6"), ("DGENQ", "1"), ("DGENF", "1"), ("LOADTYPE", "RES"), ("NAME", "LOAD A")),
        R(LoadF, ("I", "3"), ("ID", "2"), ("STATUS", "0"), ("AREA", "1"), ("ZONE", "1"), ("PL", "99"), ("QL", "99"), ("OWNER", "1"), ("NAME", "LOAD B")),
    };

    private static readonly Rec[] Shunts =
    {
        R(ShuntF, ("I", "2"), ("ID", "1"), ("STATUS", "1"), ("GL", "0.1"), ("BL", "15"), ("NAME", "SH1")),
    };

    private static readonly Rec[] Gens =
    {
        R(GenF, ("I", "1"), ("ID", "1"), ("PG", "60"), ("QG", "5"), ("QT", "50"), ("QB", "-50"), ("VS", "1.02"), ("IREG", "0"), ("NREG", "0"), ("MBASE", "100"), ("ZR", "0"),
            ("ZX", "0.2"), ("RT", "0"), ("XT", "0"), ("GTAP", "1"), ("STAT", "1"), ("RMPCT", "100"), ("PT", "100"), ("PB", "0"), ("BASLOD", "0"), ("NAME", "G1"), ("DROOPNAME", "DR1")),
        R(GenF, ("I", "2"), ("ID", "2"), ("PG", "10"), ("QG", "0"), ("QT", "10"), ("QB", "-5"), ("VS", "1"), ("MBASE", "50"), ("ZX", "0.25"), ("STAT", "1"), ("PT", "20"), ("PB", "0"), ("NAME", "G2")),
    };

    private static readonly Rec[] Branches =
    {
        R(BranchF, ("I", "1"), ("J", "2"), ("CKT", "1"), ("R", "0.01"), ("X", "0.05"), ("B", "0.02"), ("NAME", "LINE 1-2"), ("RATE1", "200"), ("RATE2", "220"), ("RATE3", "240"),
            ("GI", "0.001"), ("BI", "0.002"), ("GJ", "0"), ("BJ", "0"), ("ST", "1"), ("MET", "1"), ("LEN", "12.5"), ("BYPASS", "0")),
        R(BranchF, ("I", "1"), ("J", "2"), ("CKT", "2"), ("R", "0.012"), ("X", "0.06"), ("B", "0.03"), ("ST", "1"), ("BYPASS", "0")),
    };

    private static readonly Rec[] Switches =
    {
        R(SwitchF, ("I", "2"), ("J", "4"), ("CKT", "S1"), ("X", "0"), ("STAT", "1"), ("NSTAT", "1"), ("MET", "1"), ("STYPE", "1"), ("NAME", "COUPLER"), ("SWDRATNAM", "RT1")),
    };

    private static readonly Rec[] SwShunts =
    {
        R(SwShuntF, ("I", "3"), ("ID", "1"), ("MODSW", "0"), ("ADJM", "0"), ("STAT", "1"), ("VSWHI", "1"), ("VSWLO", "1"), ("SWREG", "0"), ("NREG", "0"), ("RMPCT", "100"),
            ("BINIT", "5"), ("S1", "1"), ("N1", "1"), ("B1", "5"), ("NAME", "SW1")),
    };

    // A two-winding transformer 2-3 and the area, zone and owner records: the same text in every rendering.
    private static readonly string[] TransformerLines =
    {
        "    2,     3,     0,'1 ',2,2,1, 1.00000E-3,-5.00000E-3,2,'T 2-3       ',1,   1,1.0000,   0,1.0000,   0,1.0000,   0,1.0000,'YNyn0       ',1",
        " 2.00000E-3, 1.20000E-1,   100.00",
        " 138.600, 132.000,   0.000,  100.00,  110.00,  120.00, 0,0,0,0,0,0,0,0,0,  0,     0,     0, 1.10000, 0.90000, 1.10000, 0.90000,  33,    0, 0.00000E+0, 0.00000E+0,  0.000",
        " 33.000, 33.000",
    };

    private static readonly string[] Labels =
    {
        "SYSTEM-WIDE", "BUS", "LOAD", "FIXED SHUNT", "GENERATOR", "BRANCH", "SYSTEM SWITCHING DEVICE", "TRANSFORMER", "AREA", "TWO-TERMINAL DC",
        "VOLTAGE SOURCE CONVERTER", "IMPEDANCE CORRECTION", "MULTI-TERMINAL DC", "MULTI-SECTION LINE", "ZONE", "INTER-AREA TRANSFER", "OWNER",
        "FACTS DEVICE", "SWITCHED SHUNT", "GNE DEVICE", "INDUCTION MACHINE", "SUBSTATION",
    };

    // ---- Renderers -------------------------------------------------------------------------------------------------

    private static string Field(string name, string? v)
    {
        if (v is null) return "";
        if (Array.IndexOf(TextFields, name) >= 0) return "'" + v + "'";
        return v;
    }

    /// <summary>One record in the order <paramref name="order"/>; trailing fields the record has no value for are left off.</summary>
    private static string Line(Rec r, IReadOnlyList<string> order)
    {
        var f = order.Select(n => r.V.TryGetValue(n, out string? v) ? Field(n, v) : "").ToList();
        while (f.Count > 0 && f[^1].Length == 0) f.RemoveAt(f.Count - 1);
        return string.Join(",", f);
    }

    private static string Heading(IEnumerable<string> order) => "@!" + string.Join(",", order);

    private static string Section(IEnumerable<string> lines, string label, string? next)
    {
        var sb = new StringBuilder();
        foreach (string l in lines) sb.Append(l).Append('\n');
        sb.Append("0 / END OF ").Append(label).Append(" DATA");
        if (next is not null) sb.Append(", BEGIN ").Append(next).Append(" DATA");
        sb.Append('\n');
        return sb.ToString();
    }

    private static IEnumerable<string> Group(IReadOnlyList<Rec> recs, IReadOnlyList<string> order, bool heading)
    {
        if (heading) yield return Heading(order);
        foreach (Rec r in recs) yield return Line(r, order);
    }

    private static string Case(int rev, Func<string, IReadOnlyList<string>, IReadOnlyList<string>> orderOf, bool headings, string basfrq = "50.00")
    {
        var sb = new StringBuilder();
        sb.Append("0, 100.00, ").Append(rev.ToString(CultureInfo.InvariantCulture)).Append(", 0, 1, ").Append(basfrq).Append(" / PSS(R)E-").Append(rev.ToString(CultureInfo.InvariantCulture)).Append(" TEST\n");
        sb.Append("GRIDSIM RAW 36 EQUIVALENCE\nSMALL CASE\n");
        var data = new Dictionary<string, IEnumerable<string>>
        {
            ["BUS"] = Group(Buses, BusF, headings),
            ["LOAD"] = Group(Loads, orderOf("LOAD", LoadF), headings),
            ["FIXED SHUNT"] = Group(Shunts, orderOf("FIXED SHUNT", ShuntF), headings),
            ["GENERATOR"] = Group(Gens, orderOf("GENERATOR", GenF), headings),
            ["BRANCH"] = Group(Branches, orderOf("BRANCH", BranchF), headings),
            ["SYSTEM SWITCHING DEVICE"] = Group(Switches, orderOf("SYSTEM SWITCHING DEVICE", SwitchF), headings),
            ["TRANSFORMER"] = TransformerLines,
            ["AREA"] = new[] { "    1,     1,     0.000,    10.000,'AREA1      '" },
            ["ZONE"] = new[] { "    1,'ZONE1       '" },
            ["OWNER"] = new[] { "    1,'OWN1        '" },
            ["SWITCHED SHUNT"] = Group(SwShunts, orderOf("SWITCHED SHUNT", SwShuntF), headings),
        };
        for (int s = 0; s < Labels.Length; s++)
        {
            string label = Labels[s];
            string? next = s + 1 < Labels.Length ? Labels[s + 1] : null;
            IEnumerable<string> lines = data.TryGetValue(label, out IEnumerable<string>? l) ? l : Array.Empty<string>();
            sb.Append(Section(lines, label, next));
        }
        sb.Append("Q\n");
        return sb.ToString();
    }

    internal static string Raw35() => Case(35, (_, canonical) => canonical, headings: true);

    /// <summary>Revision 36: headings name the fields in a different order and add NAME, DROOPNAME, BYPASS and the rating table
    /// name; the new sections are in the file between the old ones.</summary>
    internal static string Raw36(bool namesFirst = true, bool headings = true)
    {
        IReadOnlyList<string> Order(string section, IReadOnlyList<string> canonical)
        {
            var o = canonical.ToList();
            switch (section)
            {
                case "LOAD":
                case "FIXED SHUNT":
                case "SWITCHED SHUNT":
                    if (namesFirst) o.Insert(2, "NAME"); else o.Add("NAME");
                    break;
                case "GENERATOR":
                    if (namesFirst) o.Insert(2, "NAME"); else o.Add("NAME");
                    o.Add("DROOPNAME");
                    break;
                case "BRANCH":
                    o.Add("BYPASS");
                    if (namesFirst) { o.Remove("NAME"); o.Insert(3, "NAME"); }
                    break;
                case "SYSTEM SWITCHING DEVICE":
                    // The twelve rating columns are replaced by the name of a switching device rating table.
                    o.RemoveAll(n => n.StartsWith("RATE", StringComparison.Ordinal));
                    o.Add("SWDRATNAM");
                    break;
            }
            return o;
        }
        string text = Case(36, Order, headings).Replace("\r\n", "\n", StringComparison.Ordinal);
        // The revision 36 system-wide record, and the new sections placed between the old ones.
        text = text.Replace("\nGRIDSIM RAW 36 EQUIVALENCE\nSMALL CASE\n", "\nGRIDSIM RAW 36 EQUIVALENCE\nSMALL CASE\nSWDRATNAM, 1, 'RT1', 'RATING TABLE 1'\n", StringComparison.Ordinal);
        text = text.Replace("0 / END OF LOAD DATA, BEGIN FIXED SHUNT DATA\n",
            "0 / END OF LOAD DATA, BEGIN LOAD TYPE DATA\n@!'LTYPE','DESCRIPTION'\n'RES','RESIDENTIAL'\n0 / END OF LOAD TYPE DATA, BEGIN FIXED SHUNT DATA\n", StringComparison.Ordinal);
        text = text.Replace("0 / END OF GENERATOR DATA, BEGIN BRANCH DATA\n",
            "0 / END OF GENERATOR DATA, BEGIN VOLTAGE DROOP CONTROL DATA\n@!'DROOPNAME',I,'ID',KDROOP\n'DR1',1,'1',0.05\n0 / END OF VOLTAGE DROOP CONTROL DATA, BEGIN BRANCH DATA\n", StringComparison.Ordinal);
        text = text.Replace("0 / END OF SYSTEM SWITCHING DEVICE DATA, BEGIN TRANSFORMER DATA\n",
            "0 / END OF SYSTEM SWITCHING DEVICE DATA, BEGIN SWITCHING DEVICE RATING TABLE DATA\n'RT1',100.0,100.0\n0 / END OF SWITCHING DEVICE RATING TABLE DATA, BEGIN TRANSFORMER DATA\n", StringComparison.Ordinal);
        text = text.Replace("0 / END OF SUBSTATION DATA\n",
            "0 / END OF SUBSTATION DATA, BEGIN INTERFACE DATA\n'IF1',1,1\n0 / END OF INTERFACE DATA, BEGIN INTERFACE ELEMENT DATA\n'IF1',1,2,'1'\n0 / END OF INTERFACE ELEMENT DATA\n", StringComparison.Ordinal);
        return text;
    }

    // ---- RAWX ------------------------------------------------------------------------------------------------------

    private static string RawxName(string section, string canonical) => canonical switch
    {
        "I" => section == "area" ? "iarea" : "ibus",
        "J" => "jbus",
        "ID" => section == "load" ? "loadid" : section == "generator" ? "machid" : "shntid",
        "CKT" => "ckt",
        "R" => "rpu",
        "X" => "xpu",
        "B" => "bpu",
        "STATUS" => "stat",
        "ST" => "stat",
        _ => canonical.ToLowerInvariant(),
    };

    private static string JsonValue(string name, string v)
    {
        if (Array.IndexOf(TextFields, name) >= 0) return "\"" + v + "\"";
        return v;
    }

    private static string RawxTable(string section, IReadOnlyList<Rec> recs, IReadOnlyList<string> fieldOrder)
    {
        string fields = string.Join(",", fieldOrder.Select(n => "\"" + RawxName(section, n) + "\""));
        var rows = recs.Select(r => "[" + string.Join(",", fieldOrder.Select(n => r.V.TryGetValue(n, out string? v) ? JsonValue(n, v) : "null")) + "]");
        return $"\"{section}\":{{\"fields\":[{fields}],\"data\":[{string.Join(",", rows)}]}}";
    }

    /// <summary>The same network as RAWX: fields in a shuffled order, the revision 36 extras present, and a transformer record.</summary>
    private static string Rawx(int rev = 36)
    {
        var tables = new List<string>
        {
            $"\"caseid\":{{\"fields\":[\"ic\",\"sbase\",\"rev\",\"xfrrat\",\"nxfrat\",\"basfrq\",\"title1\",\"title2\"],\"data\":[0,100.0,{rev},0,1,50.0,\"GRIDSIM RAW 36 EQUIVALENCE\",\"SMALL CASE\"]}}",
            RawxTable("bus", Buses, BusF.Reverse().ToArray()),
            RawxTable("load", Loads, LoadF.Reverse().Concat(new[] { "NAME" }).ToArray()),
            RawxTable("fixshunt", Shunts, ShuntF.Concat(new[] { "NAME" }).ToArray()),
            RawxTable("generator", Gens, GenF.Reverse().Concat(new[] { "NAME", "DROOPNAME" }).ToArray()),
            RawxTable("acline", Branches, BranchF.Reverse().Concat(new[] { "BYPASS" }).ToArray()),
            RawxTable("sysswd", Switches, SwitchF.Where(n => !n.StartsWith("RATE", StringComparison.Ordinal)).ToArray()),
            "\"transformer\":{\"fields\":[\"ibus\",\"jbus\",\"kbus\",\"ckt\",\"cw\",\"cz\",\"cm\",\"mag1\",\"mag2\",\"nmet\",\"name\",\"stat\",\"o1\",\"f1\",\"o2\",\"f2\",\"o3\",\"f3\",\"o4\",\"f4\",\"vecgrp\",\"zcod\",\"r1-2\",\"x1-2\",\"sbase1-2\",\"windv1\",\"nomv1\",\"ang1\",\"wdg1rate1\",\"wdg1rate2\",\"wdg1rate3\",\"cod1\",\"cont1\",\"node1\",\"rma1\",\"rmi1\",\"vma1\",\"vmi1\",\"ntp1\",\"tab1\",\"windv2\",\"nomv2\",\"ang2\"],\"data\":[[2,3,0,\"1 \",2,2,1,1.0E-3,-5.0E-3,2,\"T 2-3       \",1,1,1.0,0,1.0,0,1.0,0,1.0,\"YNyn0       \",1,2.0E-3,0.12,100.0,138.6,132.0,0.0,100.0,110.0,120.0,0,0,0,1.1,0.9,1.1,0.9,33,0,33.0,33.0,0.0]]}",
            "\"area\":{\"fields\":[\"iarea\",\"isw\",\"pdes\",\"ptol\",\"arnam\"],\"data\":[[1,1,0.0,10.0,\"AREA1      \"]]}",
            "\"zone\":{\"fields\":[\"izone\",\"zoname\"],\"data\":[[1,\"ZONE1       \"]]}",
            "\"owner\":{\"fields\":[\"iowner\",\"ownam\"],\"data\":[[1,\"OWN1        \"]]}",
            RawxTable("swshunt", SwShunts, SwShuntF.Concat(new[] { "NAME" }).ToArray()),
            "\"twotermdc\":{\"fields\":[\"name\"],\"data\":[]}",
            "\"interface\":{\"fields\":[\"name\"],\"data\":[[\"IF1\"]]}",
        };
        return "{\"network\":{" + string.Join(",", tables) + "}}";
    }

    // ---- Equality --------------------------------------------------------------------------------------------------

    private static void AssertSameBits(double expected, double actual, string what)
        => Assert.True(BitConverter.DoubleToInt64Bits(expected) == BitConverter.DoubleToInt64Bits(actual), $"{what}: expected {expected:R}, got {actual:R}");

    private static void AssertSameModel(PsseParseResult reference, PsseParseResult other)
    {
        GridModel a = reference.Model, b = other.Model;
        Assert.Equal((a.BaseMva, a.FrequencyHz, a.Buses.Count, a.AllGenerators.Count, a.Branches.Count),
            (b.BaseMva, b.FrequencyHz, b.Buses.Count, b.AllGenerators.Count, b.Branches.Count));
        for (int i = 0; i < a.Buses.Count; i++)
        {
            Bus e = a.Buses[i], g = b.Buses[i];
            Assert.Equal((e.Id, e.Type, e.Name), (g.Id, g.Type, g.Name));
            foreach ((double x, double y, string f) in new[] { (e.Pd, g.Pd, "Pd"), (e.Qd, g.Qd, "Qd"), (e.Gs, g.Gs, "Gs"), (e.Bs, g.Bs, "Bs"),
                         (e.BaseKv, g.BaseKv, "BaseKv"), (e.Vm, g.Vm, "Vm"), (e.VaDeg, g.VaDeg, "Va") })
                AssertSameBits(x, y, $"bus {e.Id} {f}");
        }
        Assert.Equal(a.AllGenerators, b.AllGenerators);
        Assert.Equal(a.Branches, b.Branches);
        Assert.Equal(reference.BusMeta.OrderBy(kv => kv.Key), other.BusMeta.OrderBy(kv => kv.Key));
        Assert.Equal(reference.BranchKeys, other.BranchKeys);
        Assert.Equal(reference.MachineKeys, other.MachineKeys);
        Assert.Equal(reference.Titles, other.Titles);

        Complex[,] ya = YBus.Build(a).Y, yb = YBus.Build(b).Y;
        for (int i = 0; i < ya.GetLength(0); i++)
            for (int j = 0; j < ya.GetLength(1); j++)
            {
                AssertSameBits(ya[i, j].Real, yb[i, j].Real, $"G[{i},{j}]");
                AssertSameBits(ya[i, j].Imaginary, yb[i, j].Imaginary, $"B[{i},{j}]");
            }
        PowerFlowResult ra = new NewtonRaphsonPowerFlow().Solve(a), rb = new NewtonRaphsonPowerFlow().Solve(b);
        Assert.True(ra.Converged && rb.Converged, "both solves must converge");
        for (int i = 0; i < ra.Vm.Length; i++)
        {
            AssertSameBits(ra.Vm[i], rb.Vm[i], $"Vm bus {ra.BusIds[i]}");
            AssertSameBits(ra.VaDeg[i], rb.VaDeg[i], $"Va bus {ra.BusIds[i]}");
        }
    }

    // ---- Tests -----------------------------------------------------------------------------------------------------

    [Fact]
    public void The_revision_35_reference_text_reads_with_the_values_it_was_written_with()
    {
        PsseParseResult r = PsseRawParser.ParseWithDiagnostics(Raw35(), "ref");
        Assert.Equal(35, r.Revision);
        Assert.Equal(4, r.Model.Buses.Count);
        Assert.Equal(2, r.Model.AllGenerators.Count);
        Assert.Equal(60.0, r.Model.AllGenerators[0].Pg);
        // Load 3-1 is 40 MW less 6 MW of distributed generation; load 3-2 is out of service, so its 99 MW is not there.
        Assert.InRange(r.Model.Buses.Single(b => b.Id == 3).Pd, 30.0, 45.0);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Revision_36_reads_to_the_same_model_as_revision_35_bit_for_bit(bool namesFirst)
    {
        PsseParseResult reference = PsseRawParser.ParseWithDiagnostics(Raw35(), "case");
        PsseParseResult r36 = PsseRawParser.ParseWithDiagnostics(Raw36(namesFirst), "case");
        Assert.Equal(36, r36.Revision);
        AssertSameModel(reference, r36);
        Assert.Contains(r36.Notes, n => n.Contains("revision 36", StringComparison.Ordinal) && n.Contains("read past", StringComparison.Ordinal));
    }

    [Fact]
    public void Revision_36_can_be_asked_for_when_the_file_does_not_say()
    {
        string text = Raw36().Replace("0, 100.00, 36, 0, 1, 50.00 / PSS(R)E-36 TEST", "0, 100.00, , 0, 1, 50.00", StringComparison.Ordinal);
        PsseParseResult r = PsseRawParser.ParseWithDiagnostics(text, "case", new PsseReadOptions { Revision = 36 });
        Assert.Equal(36, r.Revision);
        Assert.Throws<InvalidDataException>(() => PsseRawParser.ParseWithDiagnostics(Raw36(), "case", new PsseReadOptions { Revision = 35 }));
    }

    [Fact]
    public void A_revision_36_record_group_with_no_heading_line_is_refused_naming_the_line()
    {
        string text = Raw36();
        int at = text.IndexOf("@!I,ID,NAME,STATUS", StringComparison.Ordinal);
        Assert.True(at > 0);
        int end = text.IndexOf('\n', at);
        text = text.Remove(at, end - at + 1);
        var ex = Assert.Throws<InvalidDataException>(() => PsseRawParser.ParseWithDiagnostics(text, "case"));
        Assert.Contains("LOAD data", ex.Message, StringComparison.Ordinal);
        Assert.Contains("heading", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_bypassed_in_service_branch_is_refused_not_guessed_at()
    {
        string text = Raw36(namesFirst: false);
        // Branch 1-2 circuit 2 carries BYPASS as its last heading item: set it to 1.
        string[] lines = text.Split('\n');
        int k = Array.FindIndex(lines, l => l.StartsWith("1,2,'2',", StringComparison.Ordinal));
        Assert.True(k > 0);
        string[] f = lines[k].Split(',');
        // The branch heading is the 35 order then BYPASS: I J CKT R X B NAME RATE1..RATE12 GI BI GJ BJ ST MET LEN BYPASS.
        string[] row = new string[27];
        Array.Fill(row, "");
        Array.Copy(f, row, f.Length);
        row[26] = "1";
        lines[k] = string.Join(",", row);
        var ex = Assert.Throws<InvalidDataException>(() => PsseRawParser.ParseWithDiagnostics(string.Join("\n", lines), "case"));
        Assert.Contains("BYPASS", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_terminator_with_no_label_in_a_revision_36_file_is_refused()
    {
        string text = Raw36().Replace("0 / END OF LOAD TYPE DATA, BEGIN FIXED SHUNT DATA", "0", StringComparison.Ordinal);
        var ex = Assert.Throws<InvalidDataException>(() => PsseRawParser.ParseWithDiagnostics(text, "case"));
        Assert.Contains("label", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_section_that_neither_revision_defines_is_refused()
    {
        string text = Raw36().Replace("BEGIN LOAD TYPE DATA", "BEGIN WIDGET DATA", StringComparison.Ordinal);
        var ex = Assert.Throws<InvalidDataException>(() => PsseRawParser.ParseWithDiagnostics(text, "case"));
        Assert.Contains("WIDGET", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_revision_35_file_is_read_exactly_as_before()
    {
        PsseParseResult a = PsseRawParser.ParseWithDiagnostics(Raw35(), "case");
        PsseParseResult b = PsseRawParser.ParseWithDiagnostics(Raw35(), "case");
        AssertSameModel(a, b);
        Assert.DoesNotContain(a.Notes, n => n.Contains("revision 36", StringComparison.Ordinal));
    }

    [Fact]
    public void Rawx_reads_to_the_same_model_as_revision_35_bit_for_bit_with_fields_in_any_order()
    {
        PsseParseResult reference = PsseRawParser.ParseWithDiagnostics(Raw35(), "case");
        PsseParseResult rawx = PsseRawParser.ParseWithDiagnostics(Rawx(36), "case");
        Assert.Equal(36, rawx.Revision);
        AssertSameModel(reference, rawx);
        Assert.Contains(rawx.Notes, n => n.Contains("RAWX", StringComparison.Ordinal));
        PsseParseResult rawx35 = PsseRawParser.ParseWithDiagnostics(Rawx(35), "case");
        Assert.Equal(35, rawx35.Revision);
        AssertSameModel(reference, rawx35);
    }

    [Fact]
    public void Rawx_field_positions_are_by_name_a_short_literal_file()
    {
        // Two buses, a line and a generator, fields listed in an order of the writer's choosing; a null leaves the default.
        const string json = """
        {"network":{
          "caseid":{"fields":["sbase","ic","rev","basfrq","title1","title2","xfrrat","nxfrat"],"data":[100.0,0,35,50.0,"T1","T2",0,1]},
          "bus":{"fields":["name","ibus","baskv","ide","vm","va","area","zone","owner"],"data":[["A",1,132.0,3,1.04,0.0,1,1,1],["B",2,132.0,1,1.0,-1.0,1,1,1]]},
          "load":{"fields":["pl","ql","ibus","loadid"],"data":[[30.0,10.0,2,"1"]]},
          "generator":{"fields":["pg","ibus","machid","vs","qt","qb","pt","pb","mbase","zx"],"data":[[35.0,1,"1",1.04,40.0,-40.0,100.0,0.0,100.0,0.2]]},
          "acline":{"fields":["xpu","rpu","jbus","ibus","ckt","bpu","rate1"],"data":[[0.1,0.01,2,1,"1",0.04,150.0]]}
        }}
        """;
        PsseParseResult r = PsseRawParser.ParseWithDiagnostics(json, "mini");
        Assert.Equal(2, r.Model.Buses.Count);
        Branch br = Assert.Single(r.Model.Branches);
        Assert.Equal((1, 2, 0.01, 0.1, 0.04, 150.0), (br.From, br.To, br.R, br.X, br.B, br.RateAMva));
        Assert.Equal(30.0, r.Model.Buses.Single(b => b.Id == 2).Pd);
        Assert.Equal(35.0, Assert.Single(r.Model.AllGenerators).Pg);
        Assert.Equal(new[] { "T1", "T2" }, r.Titles);
    }

    [Fact]
    public void Rawx_with_a_section_it_does_not_read_is_refused_naming_it()
    {
        string json = Rawx().Replace("\"twotermdc\":{\"fields\":[\"name\"],\"data\":[]}", "\"twotermdc\":{\"fields\":[\"name\"],\"data\":[[\"DC1\"]]}", StringComparison.Ordinal);
        var ex = Assert.Throws<InvalidDataException>(() => PsseRawParser.ParseWithDiagnostics(json, "case"));
        Assert.Contains("twotermdc", ex.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("{\"x\":1}", "network")]
    [InlineData("{\"network\":{\"bus\":{\"fields\":[\"ibus\"],\"data\":[[1]]}}}", "caseid")]
    [InlineData("{ not json", "JSON")]
    public void Rawx_refuses_a_file_that_is_not_a_network(string json, string mentions)
    {
        var ex = Assert.Throws<InvalidDataException>(() => PsseRawParser.ParseWithDiagnostics(json, "case"));
        Assert.Contains(mentions, ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Rawx_with_a_revision_other_than_35_or_36_is_refused()
    {
        var ex = Assert.Throws<InvalidDataException>(() => PsseRawParser.ParseWithDiagnostics(Rawx(34), "case"));
        Assert.Contains("34", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_rawx_file_is_read_from_disk_by_its_content()
    {
        string path = TestTemp.Under("gridsim-wp5-" + Guid.NewGuid().ToString("N") + ".rawx");
        try
        {
            File.WriteAllText(path, Rawx(36));
            PsseParseResult r = PsseRawParser.ParseFile(path);
            Assert.Equal(4, r.Model.Buses.Count);
        }
        finally { File.Delete(path); }
    }
}
