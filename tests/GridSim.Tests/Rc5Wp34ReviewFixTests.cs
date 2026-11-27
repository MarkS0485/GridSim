using System.Text;
using GridSim.Core.IO.Psse;
using GridSim.Core.IO.Ucte;
using GridSim.Core.Model;
using Xunit;

namespace GridSim.Tests;

/// <summary>
/// RC5 WP34 review fixes (review-wp34.md, items 1 and 3 to 7): a UCTE-DEF case read and written again keeps the file's
/// transformer rated voltages, nominal power, regulation record and G and B, so it reads back bit for bit; synthesised
/// UCTE node codes carry a named country, never X; the transformer nominal-power column is written; RAWX text is not
/// HTML-escaped; the induction machine's short code names (SC, DC, TC, BC, PC) place their fields.
/// <para>Oracles. Expected ratios are derived outside GridSim (Python, IEEE doubles) from the reader's documented formula
/// t = (U2 (1 + n' du / 100) / Ub2) / (U1 / Ub1), in the reader's order of operations, so they are compared to the bit.
/// A write-read is compared with the read it came from, field by field, to the bit. Column texts are checked at the
/// UCTE-DEF columns (U1 23-27, U2 29-33, nominal power 35-39, B 55-62, G 64-69).</para>
/// </summary>
public class Rc5Wp34ReviewFixTests
{
    private static bool Same(double a, double b) => BitConverter.DoubleToInt64Bits(a) == BitConverter.DoubleToInt64Bits(b);

    /// <summary>A three-node UCTE-DEF case (small.uct's slack, load and PV nodes): node 1 of the transformer is BRAVO at
    /// 380 kV (level 1), node 2 CHARLIE at 220 kV (level 2), with rated voltages <paramref name="u1"/> / <paramref name="u2"/>.
    /// <paramref name="second"/> adds a parallel transformer (order 2) with its own G and B at the same node 1.</summary>
    private static string Case(string u1, string u2, string? regulation = null, bool second = false)
    {
        var sb = new StringBuilder();
        sb.Append("##C 2007.05.01\n");
        sb.Append("GridSim WP34 review test network, written by hand to the UCTE-DEF version 02 columns.\n");
        sb.Append("##N\n##ZFR\n");
        sb.Append("FAAAAA11 ALPHA        0 3 400.00 0.00000 0.00000 -500.00 0.00000 0.00000 -1000.0 500.000 -500.00\n");
        sb.Append("FBBBBB11 BRAVO        0 0        300.000 100.000\n");
        sb.Append("FCCCCC21 CHARLIE      0 2 225.00 150.000 50.0000 -100.00 0.00000 -10.000 -400.00 200.000 -200.00\n");
        sb.Append("##L\n");
        sb.Append("FAAAAA11 FBBBBB11 1 0 1.4440 14.440   120.00   2000 LINE-AB\n");
        sb.Append("##T\n");
        sb.Append($"FBBBBB11 FCCCCC21 1 0 {u1} {u2} 600.0 0.7220 36.100  -2.5000 0.5000    900 TR-BC\n");
        if (second) sb.Append($"FBBBBB11 FCCCCC21 2 0 {u1} {u2} 600.0 0.7220 36.100  -1.2500 0.2500    900 TR-BC-2\n");
        if (regulation is not null) sb.Append("##R\n").Append(regulation).Append('\n');
        return sb.ToString();
    }

    /// <summary>A phase regulation record: du 1.667 % (columns 21-25), n 9 (27-28), n' <paramref name="step"/> (30-32).</summary>
    private static string PhaseStep(int step) => "FBBBBB11 FCCCCC21 1 1.667  9 " + step.ToString(System.Globalization.CultureInfo.InvariantCulture).PadLeft(3);

    /// <summary>A symmetric angle regulation record: du 2.000 % (columns 40-44), theta 90.00 (46-50), n 4 (52-53), n'
    /// <paramref name="step"/> (55-57), type SYMM (65-68).</summary>
    private static string AngleStep(int step) => "FBBBBB11 FCCCCC21 1".PadRight(39) + "2.000 90.00  4 "
        + step.ToString(System.Globalization.CultureInfo.InvariantCulture).PadLeft(3) + new string(' ', 7) + "SYMM";

    private static string[] Lines(string text) => text.Split('\n');

    private static string TransformerLine(string text, string order) =>
        Lines(text).Single(l => l.StartsWith("FBBBBB11 FCCCCC21 " + order + " ", StringComparison.Ordinal) && l.Length > 76);   // a ##T line runs to its name at 78; a ##R line ends by 68

    private static string RegulationLine(string text)
    {
        string[] lines = Lines(text);
        int at = Array.IndexOf(lines, "##R");
        Assert.True(at >= 0, "no ##R block");
        return lines[at + 1];
    }

    /// <summary>Read, write with the read as source, read again: the second read is the first, to the bit.</summary>
    private static (UcteParseResult Read, UcteWriteResult Written, UcteParseResult Back) RoundTrip(string text)
    {
        UcteParseResult r = UcteParser.ParseWithDiagnostics(text, "t");
        UcteWriteResult w = UcteWriter.Write(r.Model, new UcteWriteOptions { Source = r });
        UcteParseResult back = UcteParser.ParseWithDiagnostics(w.Text, "t");
        Assert.Equal(r.Model.Buses, back.Model.Buses);
        Assert.Equal(r.Model.AllGenerators, back.Model.AllGenerators);
        Assert.Equal(r.Model.Branches, back.Model.Branches);
        for (int i = 0; i < r.Model.Buses.Count; i++)
            Assert.True(Same(r.Model.Buses[i].Gs, back.Model.Buses[i].Gs) && Same(r.Model.Buses[i].Bs, back.Model.Buses[i].Bs), $"bus {i + 1} shunt");
        for (int k = 0; k < r.Model.Branches.Count; k++)
            Assert.True(Same(r.Model.Branches[k].TapRatio, back.Model.Branches[k].TapRatio) && Same(r.Model.Branches[k].PhaseShiftDeg, back.Model.Branches[k].PhaseShiftDeg),
                $"branch {k + 1}: ratio {r.Model.Branches[k].TapRatio:R} vs {back.Model.Branches[k].TapRatio:R}, shift {r.Model.Branches[k].PhaseShiftDeg:R} vs {back.Model.Branches[k].PhaseShiftDeg:R}");
        return (r, w, back);
    }

    // ---- 1. UCTE-DEF: rated voltages off the bus levels ------------------------------------------------------------

    [Fact]
    public void A_400_225_kV_transformer_on_380_220_kV_nodes_reads_back_bit_for_bit()
    {
        // t = (225 / 220) / (400 / 380) = 0.9715909090909092 (Python, the reader's order of operations). Written as a one-step
        // du at the bus levels it would be du = 100 (t - 1) = -2.840909..., "-2.84" in five columns, and read back 0.9716.
        (UcteParseResult r, UcteWriteResult w, UcteParseResult back) = RoundTrip(Case("400.0", "225.0"));
        Assert.True(Same(0.9715909090909092, r.Model.Branches[1].TapRatio), $"read {r.Model.Branches[1].TapRatio:R}");
        Assert.True(Same(0.9715909090909092, back.Model.Branches[1].TapRatio), $"read back {back.Model.Branches[1].TapRatio:R}");
        string t = TransformerLine(w.Text, "1");
        Assert.Equal("400.0", t.Substring(22, 5));   // U1, columns 23-27, as the file had it
        Assert.Equal("225.0", t.Substring(28, 5));   // U2, columns 29-33
        Assert.Equal("600.0", t.Substring(34, 5));   // nominal power, columns 35-39, kept
        Assert.DoesNotContain("##R", w.Text, StringComparison.Ordinal);   // the file had no regulation record; none is made up
    }

    [Theory]
    [InlineData(-9, 0.825823125)]
    [InlineData(-8, 0.8420195454545455)]
    [InlineData(-7, 0.858215965909091)]
    [InlineData(-6, 0.8744123863636364)]
    [InlineData(-5, 0.8906088068181819)]
    [InlineData(-4, 0.9068052272727274)]
    [InlineData(-3, 0.9230016477272728)]
    [InlineData(-2, 0.9391980681818182)]
    [InlineData(-1, 0.9553944886363638)]
    [InlineData(0, 0.9715909090909092)]
    [InlineData(1, 0.9877873295454545)]
    [InlineData(2, 1.00398375)]
    [InlineData(3, 1.0201801704545457)]
    [InlineData(4, 1.036376590909091)]
    [InlineData(5, 1.0525730113636362)]
    [InlineData(6, 1.068769431818182)]
    [InlineData(7, 1.0849658522727275)]
    [InlineData(8, 1.1011622727272727)]
    [InlineData(9, 1.1173586931818182)]
    public void Every_step_of_a_nine_step_tap_changer_reads_back_bit_for_bit(int step, double expected)
    {
        // Expected: t = (225 (1 + n' x 1.667 / 100) / 220) / (400 / 380), derived in Python for every n' from -9 to 9.
        string reg = PhaseStep(step);
        (UcteParseResult r, UcteWriteResult w, UcteParseResult back) = RoundTrip(Case("400.0", "225.0", reg));
        Assert.True(Same(expected, r.Model.Branches[1].TapRatio), $"step {step}: read {r.Model.Branches[1].TapRatio:R}, expected {expected:R}");
        Assert.True(Same(expected, back.Model.Branches[1].TapRatio), $"step {step}: read back {back.Model.Branches[1].TapRatio:R}, expected {expected:R}");
        Assert.Equal(reg, RegulationLine(w.Text));   // du, n and n' as the file had them, not one step
    }

    [Theory]
    [InlineData(-4)]
    [InlineData(-3)]
    [InlineData(-2)]
    [InlineData(-1)]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    public void Every_step_of_a_symmetric_phase_shifter_reads_back_bit_for_bit(int step)
    {
        string reg = AngleStep(step);
        (UcteParseResult r, UcteWriteResult w, UcteParseResult back) = RoundTrip(Case("400.0", "225.0", reg));
        double shift = r.Model.Branches[1].PhaseShiftDeg;
        Assert.Equal(Math.Sign(step), Math.Sign(shift));   // the step reaches the model on every step, with its sign
        Assert.True(Same(shift, back.Model.Branches[1].PhaseShiftDeg), $"step {step}");
        Assert.Equal(reg, RegulationLine(w.Text));
    }

    [Fact]
    public void Two_transformers_sharing_a_node_keep_their_own_g_and_b_and_the_bus_shunt_reads_back_bit_for_bit()
    {
        // The reader adds 0.5 and 0.25 microsiemens at 380 kV: 0.10830000000000001 MW. Written as one sum on the first
        // transformer, 0.7500 in six columns, it would read back 0.1083 (Python): one bit off. So each keeps its own text.
        (UcteParseResult r, UcteWriteResult w, _) = RoundTrip(Case("400.0", "225.0", second: true));
        Assert.True(Same(0.10830000000000001, r.Model.Buses[1].Gs), $"Gs {r.Model.Buses[1].Gs:R}");
        string t1 = TransformerLine(w.Text, "1"), t2 = TransformerLine(w.Text, "2");
        Assert.Equal((" -2.5000", "0.5000"), (t1.Substring(54, 8), t1.Substring(63, 6)));
        Assert.Equal((" -1.2500", "0.2500"), (t2.Substring(54, 8), t2.Substring(63, 6)));
    }

    [Fact]
    public void A_transformer_changed_after_the_read_is_written_as_from_another_format_and_says_so()
    {
        UcteParseResult r = UcteParser.ParseWithDiagnostics(Case("400.0", "225.0"), "t");
        GridModel m = r.Model.CopyWith(branches: r.Model.Branches.Select((b, k) => k == 1 ? b with { TapRatio = 1.0 } : b).ToList());
        UcteWriteResult w = UcteWriter.Write(m, new UcteWriteOptions { Source = r });
        string t = TransformerLine(w.Text, "1");
        Assert.Equal(("380.0", "220.0"), (t.Substring(22, 5), t.Substring(28, 5)));   // the bus levels' rated voltages
        // Nominal power from rating A: sqrt(3) x 380 kV x 900 A / 1000 = 592.3613761885559 MVA, "592.4" (Python).
        Assert.Equal("592.4", t.Substring(34, 5));
        Assert.Contains(w.Notes, n => n.Contains("differ from the source read", StringComparison.Ordinal));
        Assert.True(Same(1.0, UcteParser.ParseWithDiagnostics(w.Text, "t").Model.Branches[1].TapRatio));
    }

    // ---- 4. UCTE-DEF: synthesised node codes and the nominal-power column ----------------------------------------------

    private static GridModel Case14AtUcteLevels()
    {
        GridModel m = S10TestSupport.LoadMatpower("lib/case14.m").Model;
        return m.CopyWith(buses: m.Buses.Select(b => b with { BaseKv = b.Id <= 5 ? 220.0 : 110.0 }).ToList());
    }

    [Fact]
    public void Synthesised_node_codes_carry_the_given_country_and_are_not_x_nodes()
    {
        UcteWriteResult w = UcteWriter.Write(Case14AtUcteLevels(), new UcteWriteOptions { Country = "DE" });
        Assert.All(w.NodeCodes, c => Assert.Equal('D', c[0]));
        Assert.Equal("D0000121", w.NodeCodes[0]);    // D, bus 1 in five digits, level 2 (220 kV), busbar 1
        Assert.Equal("D0001451", w.NodeCodes[13]);   // bus 14, level 5 (110 kV)
        Assert.Contains("##ZDE\n", w.Text, StringComparison.Ordinal);
        Assert.DoesNotContain("##ZXX", w.Text, StringComparison.Ordinal);
        UcteParseResult back = UcteParser.ParseWithDiagnostics(w.Text, "case14", new UcteReadOptions { FrequencyHz = 60 });
        Assert.All(back.Nodes.Values, n => Assert.False(n.XNode, n.Code));
        Assert.DoesNotContain(back.Notes, n => n.Contains("X-node", StringComparison.Ordinal));
    }

    [Fact]
    public void A_synthesised_code_without_a_country_is_refused_naming_the_option()
    {
        var ex = Assert.Throws<InvalidDataException>(() => UcteWriter.Write(Case14AtUcteLevels()));
        Assert.Contains("UcteWriteOptions.Country", ex.Message, StringComparison.Ordinal);
        Assert.Contains("--ucte-country", ex.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("XX", "X-nodes")]
    [InlineData("fr", "not a UCTE country code")]
    [InlineData("QQ", "not a UCTE country code")]
    public void A_country_that_is_not_a_ucte_country_is_refused(string country, string reason)
    {
        var ex = Assert.Throws<ArgumentOutOfRangeException>(() => UcteWriter.Write(Case14AtUcteLevels(), new UcteWriteOptions { Country = country }));
        Assert.Contains(reason, ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void The_country_codes_leave_out_x_and_have_one_character_each()
    {
        Assert.DoesNotContain('X', UcteWriter.CountryCodes.Values);
        Assert.False(UcteWriter.CountryCodes.ContainsKey("XX"));
        Assert.Equal(UcteWriter.CountryCodes.Count, UcteWriter.CountryCodes.Values.Distinct().Count());
        Assert.Equal(('F', 'D', 'O'), (UcteWriter.CountryCodes["FR"], UcteWriter.CountryCodes["DE"], UcteWriter.CountryCodes["AT"]));
    }

    [Fact]
    public void A_synthesised_transformer_has_its_nominal_power_written()
    {
        // case14's transformers (4-7, 4-9, 5-6) have rating A 0: the nominal power is the 100 MVA system base, "100.0" in
        // five columns. A rating A of 250 MVA is written as "250.0".
        GridModel m = Case14AtUcteLevels();
        UcteWriteResult w = UcteWriter.Write(m, new UcteWriteOptions { Country = "FR" });
        string[] xf = Lines(w.Text).SkipWhile(l => l != "##T").Skip(1).TakeWhile(l => !l.StartsWith("##", StringComparison.Ordinal) && l.Length > 0).ToArray();
        Assert.Equal(3, xf.Length);
        Assert.All(xf, l => Assert.Equal("100.0", l.Substring(34, 5)));

        GridModel rated = m.CopyWith(branches: m.Branches.Select(b => b.IsTransformer || b.TapRatio != 0 ? b with { RateAMva = 250 } : b).ToList());
        string[] xf2 = Lines(UcteWriter.Write(rated, new UcteWriteOptions { Country = "FR" }).Text)
            .SkipWhile(l => l != "##T").Skip(1).TakeWhile(l => !l.StartsWith("##", StringComparison.Ordinal) && l.Length > 0).ToArray();
        Assert.Equal(3, xf2.Length);
        Assert.All(xf2, l => Assert.Equal("250.0", l.Substring(34, 5)));
    }

    // ---- 6. RAWX: text is escaped only where JSON requires it ----------------------------------------------------------

    [Fact]
    public void Rawx_writes_an_ampersand_and_angle_brackets_as_they_are_and_reads_back_as_raw_35()
    {
        GridModel m = S10TestSupport.LoadMatpower("lib/case14.m").Model;
        m = m.CopyWith(buses: m.Buses.Select(b => b.Id == 1 ? b with { Name = "A&B <1>+" } : b).ToList());
        string json = PsseRawxWriter.Write(m).Text;
        Assert.Contains("A&B <1>+", json, StringComparison.Ordinal);
        Assert.DoesNotContain("\\u0026", json, StringComparison.Ordinal);
        Assert.DoesNotContain("\\u003C", json, StringComparison.Ordinal);
        Assert.DoesNotContain("\\u002B", json, StringComparison.Ordinal);
        var options = new PsseReadOptions { ApplyTapControl = true, ApplyShuntControl = true };
        GridModel viaRawx = PsseRawParser.ParseWithDiagnostics(json, "case14", options).Model;
        GridModel viaRaw = PsseRawParser.ParseWithDiagnostics(PsseRawWriter.Write(m, 35).Text, "case14", options).Model;
        Assert.Equal(viaRaw.Buses, viaRawx.Buses);
    }

    // ---- 7. Induction machine short code names -------------------------------------------------------------------------

    private static readonly PsseReadOptions Controls = new() { ApplyTapControl = true, ApplyShuntControl = true, FrequencyHz = 50 };

    /// <summary>The induction machine case (custom design, DCODE 0; electrical schedule, PCODE 2) as revision 36, with the
    /// induction machine heading's SCODE, DCODE, TCODE, BCODE and PCODE renamed by <paramref name="rename"/>.</summary>
    private static string Revision36(Func<string, string> rename)
    {
        GridModel m = PsseRawParser.ParseWithDiagnostics(PsseInductionTests.Case(PsseInductionTests.Motor(pcode: 2)), "im", Controls).Model;
        Assert.Equal(InductionPowerCode.Electrical, Assert.Single(m.InductionMachines).PowerCode);
        string[] lines = PsseRawWriter.Write(m, 36).Text.Split('\n');
        int at = Array.FindIndex(lines, l => l.Contains("BEGIN INDUCTION MACHINE DATA", StringComparison.Ordinal));
        Assert.StartsWith("@!I,'ID',STAT,SCODE,DCODE,AREA,ZONE,OWNER,TCODE,BCODE,MBASE,RATEKV,PCODE,PSET,", lines[at + 1], StringComparison.Ordinal);
        lines[at + 1] = rename(lines[at + 1]);
        return string.Join("\n", lines);
    }

    private static string Short(string heading, string dc = "DC", string pc = "PC") => heading
        .Replace(",SCODE,", ",SC,", StringComparison.Ordinal).Replace(",DCODE,", "," + dc + ",", StringComparison.Ordinal)
        .Replace(",TCODE,", ",TC,", StringComparison.Ordinal).Replace(",BCODE,", ",BC,", StringComparison.Ordinal)
        .Replace(",PCODE,", "," + pc + ",", StringComparison.Ordinal);

    [Fact]
    public void A_revision_36_heading_with_the_short_code_names_reads_as_the_long_names()
    {
        GridModel longNames = PsseRawParser.ParseWithDiagnostics(Revision36(h => h), "im", Controls).Model;
        GridModel shortNames = PsseRawParser.ParseWithDiagnostics(Revision36(h => Short(h)), "im", Controls).Model;
        Assert.Equal(longNames.InductionMachines, shortNames.InductionMachines);
        Assert.Equal(InductionPowerCode.Electrical, Assert.Single(shortNames.InductionMachines).PowerCode);
    }

    [Fact]
    public void Without_the_short_names_dcode_and_pcode_would_fall_to_their_defaults()
    {
        // Controls for the test above: an unknown name is dropped, so DCODE takes its default 2 (a standard design, refused
        // as unmodelled) and PCODE its default 1 (a mechanical schedule).
        var ex = Assert.Throws<InvalidDataException>(() => PsseRawParser.ParseWithDiagnostics(Revision36(h => Short(h, dc: "DCX")), "im", Controls));
        Assert.Contains("standard design code", ex.Message, StringComparison.Ordinal);
        GridModel m = PsseRawParser.ParseWithDiagnostics(Revision36(h => Short(h, pc: "PCX")), "im", Controls).Model;
        Assert.Equal(InductionPowerCode.Mechanical, Assert.Single(m.InductionMachines).PowerCode);
    }

    // ---- CLI: convert to .uct ------------------------------------------------------------------------------------------

    private static string Fixture(string name) => Path.Combine(CliProcess.Root(), "tests", "GridSim.Tests", "fixtures", "ucte", name);

    [Fact]
    public void Convert_writes_a_uct_input_back_with_its_own_codes_and_transformer_fields()
    {
        string dir = TestTemp.Under("gridsim-ucte-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            string output = Path.Combine(dir, "small.uct");
            CliProcess.Result r = CliProcess.RunBuilt("convert", Fixture("small.uct"), output);
            Assert.True(r.ExitCode == 0, r.ToString());
            Assert.Contains("round trip: the written case solves bit-identically", r.StdOut, StringComparison.Ordinal);
            string text = File.ReadAllText(output);
            // small.uct's transformer, with its rated voltages and nominal power as the file has them.
            Assert.Contains("FBBBBB11 FCCCCC21 1 0 380.0 220.0 600.0", text, StringComparison.Ordinal);
            Assert.Contains("FBBBBB11 FCCCCC21 1 1.250 16   2", text, StringComparison.Ordinal);   // its ##R record
            Assert.Contains("XFRDE111", text, StringComparison.Ordinal);   // the file's own X-node stays one
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public void Convert_refuses_a_ucte_country_that_is_not_one_and_the_flag_without_a_uct_output()
    {
        string dir = TestTemp.Under("gridsim-ucte-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            CliProcess.Result xx = CliProcess.RunBuilt("convert", Fixture("small.uct"), Path.Combine(dir, "a.uct"), "--ucte-country", "XX");
            Assert.True(xx.ExitCode == 2, xx.ToString());
            Assert.Contains("X-nodes", xx.StdErr, StringComparison.Ordinal);
            CliProcess.Result m = CliProcess.RunBuilt("convert", Fixture("small.uct"), Path.Combine(dir, "a.m"), "--ucte-country", "FR");
            Assert.True(m.ExitCode == 2, m.ToString());
            Assert.Contains("--ucte-country", m.StdErr, StringComparison.Ordinal);
            Assert.False(File.Exists(Path.Combine(dir, "a.uct")) || File.Exists(Path.Combine(dir, "a.m")));
        }
        finally { Directory.Delete(dir, true); }
    }
}
