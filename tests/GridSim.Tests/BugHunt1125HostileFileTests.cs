using System.Diagnostics;
using System.Text;
using GridSim.Core.IO.Cim.Shacl;
using GridSim.Core.IO.Cim.Zip;
using GridSim.Core.IO.Comtrade;
using GridSim.Core.IO.Dgs;
using GridSim.Core.IO.Matpower;
using GridSim.Core.IO.Psse;
using GridSim.Core.Model;
using GridSim.Core.PowerFlow;
using Xunit;

namespace GridSim.Tests;

/// <summary>
/// Bug hunt of 2026-11-25, batch 2 (hostile-file hardening, ruling 5): every recursive reader has a depth limit that ends
/// in a clear refusal, file integers are checked in wide arithmetic, the MATPOWER reader is linear in blank and continued
/// lines, the CGMES limits hold for the folder form too, counts in a file are checked before anything is sized from them,
/// an LCC tap step of 0 is refused and guarded, and the DGS, RAWX and COMTRADE findings IO-09 to IO-11.
/// </summary>
public sealed class BugHunt1125HostileFileTests : IDisposable
{
    private readonly string _dir = JournalTestSupport.NewDir("bh1125-io");

    public void Dispose() => JournalTestSupport.Delete(_dir);

    private const string TwoBusM = """
        function mpc = two
        mpc.version = '2';
        mpc.baseMVA = 100;
        mpc.bus = [
        1 3 0 0 0 0 1 1 0 230 1 1.1 0.9;
        2 1 50 10 0 0 1 VM 0 230 1 1.1 0.9;
        ];
        mpc.gen = [
        1 0 0 300 -300 1 100 1 250 10;
        ];
        mpc.branch = [
        1 2 0.01 0.1 0 250 250 250 0 0 1 -360 360;
        ];
        """;

    private static string Case(string vm) => TwoBusM.Replace("VM", vm, StringComparison.Ordinal);

    // ---- IO-01: the MATPOWER expression evaluator ------------------------------------------------------------------

    [Fact]
    public void Io01_a_deeply_nested_matpower_expression_is_refused_not_a_stack_overflow()
    {
        string deep = new string('(', 20_000) + "1" + new string(')', 20_000);
        var ex = Assert.Throws<InvalidDataException>(() => MatpowerParser.Parse(Case(deep), "deep"));
        Assert.Contains("deeper than 64", ex.InnerException?.Message ?? ex.Message, StringComparison.Ordinal);
        // A long run of unary signs is a loop, not recursion: it reads (an even number of minus signs is +1).
        GridModel signs = MatpowerParser.Parse(Case(new string('-', 20_000) + "1"), "signs");
        Assert.Equal(1.0, signs.Buses[1].Vm);
        // Nesting within the limit, and the arithmetic real cases use, still evaluate.
        Assert.Equal(1.0, MatpowerParser.Parse(Case(new string('(', 60) + "1" + new string(')', 60)), "ok").Buses[1].Vm);
        Assert.Equal(1.05, MatpowerParser.Parse(Case("-(-2.1)/2"), "ok").Buses[1].Vm, 15);
    }

    // ---- IO-05: linear in blank and continued lines --------------------------------------------------------------------

    private static string Continued(int lines)
    {
        var sb = new StringBuilder(TwoBusM.Replace("VM", "1", StringComparison.Ordinal));
        sb.Append("mpc.note = 1 ...\n");
        for (int k = 0; k < lines; k++) sb.Append("+ 1 ...\n");
        sb.Append("+ 1;\n");
        return sb.ToString();
    }

    /// <summary>The median of seven timings of <paramref name="parse"/>, in ticks, each after a full collection, so one
    /// collection or scheduler pause on a busy runner does not land inside a single short timing.</summary>
    private static long Median(Action parse)
    {
        var t = new long[7];
        for (int k = 0; k < t.Length; k++)
        {
            GC.Collect();
            GC.WaitForPendingFinalizers();
            var sw = Stopwatch.StartNew();
            parse();
            t[k] = sw.ElapsedTicks;
        }
        Array.Sort(t);
        return Math.Max(1, t[3]);
    }

    /// <summary>R2-10: linearity is tested as a ratio: four times the lines must take well under sixteen times as long (a
    /// quadratic parse gives about 16, a linear one about 4); an absolute cap stays only as a backstop.</summary>
    [Fact]
    public void Io05_blank_lines_and_continuation_lines_are_read_in_linear_time()
    {
        var total = Stopwatch.StartNew();
        string small = Case("1") + new string('\n', 40_000), large = Case("1") + new string('\n', 160_000);
        MatpowerParser.Parse(small, "warm");
        long a = Median(() => MatpowerParser.Parse(small, "s")), b = Median(() => MatpowerParser.Parse(large, "l"));
        Assert.True(b < 8 * a, $"blank lines: 4x the lines took {(double)b / a:F1}x as long");
        // 50,000 and 200,000 lines (was 5,000 and 20,000): the small parse took about 0.7 ms, so one pause on a shared CI
        // runner turned a measured 2.8x into 9.8x (windows-11-arm, 2.1.174). Measured here: 2.8x at both sizes, linear.
        string cs = Continued(50_000), cl = Continued(200_000);
        MatpowerParser.Parse(cs, "warm");
        long c = Median(() => MatpowerParser.Parse(cs, "s")), d = Median(() => MatpowerParser.Parse(cl, "l"));
        Assert.True(d < 8 * c, $"continuation lines: 4x the lines took {(double)d / c:F1}x as long");
        Assert.Equal(2, MatpowerParser.Parse(large, "l").Buses.Count);
        Assert.True(total.Elapsed < TimeSpan.FromMinutes(2), $"took {total.Elapsed}");
    }

    // ---- IO-02: PSS/E bus numbers near the int limit -----------------------------------------------------------------

    private static string Renumbered(string raw, string from, string to) => raw
        .Replace("\n" + from + ", 'LOAD'", "\n" + to + ", 'LOAD'", StringComparison.Ordinal)
        .Replace("\n" + from + ", '1', 1, 1, 1, 100", "\n" + to + ", '1', 1, 1, 1, 100", StringComparison.Ordinal)
        .Replace("\n1, " + from + ", 0, '1'", "\n1, " + to + ", 0, '1'", StringComparison.Ordinal)
        .Replace("\n" + from + ", 1, 0, 1, 1.03", "\n" + to + ", 1, 0, 1, 1.03", StringComparison.Ordinal);

    /// <summary>R2-10: the hang test runs in a child process with a timeout, so a regression is killed instead of leaving a
    /// spinning thread in the test run.</summary>
    [Fact]
    public void Io02_a_bus_number_near_the_int_limit_loads_instead_of_hanging()
    {
        string raw = Renumbered(PsseShuntControlTests.TwoBus("1, 0, 1, 1.03, 0.97, 0, 100, '', 0, 8, 10"), "2", "2000000000");
        Assert.Contains("2000000000, 'LOAD'", raw, StringComparison.Ordinal);
        string file = Path.Combine(_dir, "big.raw");
        File.WriteAllText(file, raw);
        CliProcess.Result r = CliProcess.RunBuilt("pf", file, "--frequency-hz", "50");   // CliProcess kills it after 120 s
        Assert.True(r.ExitCode is 0 or 3, r.ToString());
        Assert.Contains(PsseRawParser.ParseWithDiagnostics(raw, "big").Model.Buses, b => b.Id == 2000000000);
    }

    /// <summary>R2-09: a J or K of -2147483648 is refused naming the field (Math.Abs threw an OverflowException).</summary>
    [Fact]
    public void R2_09_a_bus_field_of_int_min_is_refused_by_name()
    {
        string raw = PsseShuntControlTests.TwoBus("1, 0, 1, 1.03, 0.97, 0, 100, '', 0, 8, 10").Replace("\n1, 2, 0, '1'", "\n1, -2147483648, 0, '1'", StringComparison.Ordinal);
        Assert.Contains("-2147483648", raw, StringComparison.Ordinal);
        var ex = Assert.Throws<InvalidDataException>(() => PsseRawParser.ParseWithDiagnostics(raw, "min"));
        Assert.Contains("is -2147483648, which is not a bus number", ex.Message, StringComparison.Ordinal);
    }

    // ---- IO-07: switched shunt block counts --------------------------------------------------------------------------

    [Fact]
    public void Io07_a_switched_shunt_block_count_beyond_the_limit_is_refused_before_anything_is_sized()
    {
        var options = new PsseReadOptions { FrequencyHz = 50, ApplyShuntControl = true };
        var ex = Assert.Throws<InvalidDataException>(() => PsseRawParser.ParseWithDiagnostics(
            PsseShuntControlTests.TwoBus("1, 0, 1, 1.03, 0.97, 0, 100, '', 0, 2000000000, 10"), "huge", options));
        Assert.Contains("at most 1000 steps", ex.Message, StringComparison.Ordinal);
        // A real block still reads.
        PsseRawParser.ParseWithDiagnostics(PsseShuntControlTests.TwoBus("1, 0, 1, 1.03, 0.97, 0, 100, '', 0, 8, 10"), "ok", options);
    }

    // ---- NUM-20: an LCC tap step of 0 ---------------------------------------------------------------------------------

    private static string WithStep(string good, string step)
    {
        const string Written = "345, 0.56, 1.0, 1.1, 0.9, 0.00625";
        int at = good.IndexOf(Written, StringComparison.Ordinal);
        Assert.True(at > 0);
        return good[..at] + "345, 0.56, 1.0, 1.1, 0.9, " + step + good[(at + Written.Length)..];
    }

    [Theory]
    [InlineData("0")]
    [InlineData("1e-17")]   // R2-01: below the rounding of a tap near 1: tap - step == tap, the loop never moved
    [InlineData("1e-12")]   // R2-01: representable, about 1e11 steps: a hang in practice
    public void Num20_an_lcc_tap_step_of_zero_or_below_the_floor_is_refused_at_read(string step)
    {
        var ex = Assert.Throws<InvalidDataException>(() => PsseRawParser.ParseWithDiagnostics(WithStep(PsseLccTests.Case(vschd: 520), step), "lcc"));
        Assert.Contains("tap step STP is", ex.Message, StringComparison.Ordinal);
    }

    /// <summary>R2-01: a model from any other route is held to the floor by GridModel.Validate, and the search is bounded by
    /// position count whatever the step (checked directly, so a regression cannot leave a spinning solve behind).</summary>
    [Fact]
    public void Num20_the_tap_search_is_bounded_and_validate_refuses_a_tiny_step()
    {
        GridModel m = PsseRawParser.ParseWithDiagnostics(PsseLccTests.Case(vschd: 520), "lcc").Model;
        foreach (double tiny in new[] { 0.0, 1e-17, 1e-12, double.NaN })
        {
            GridModel stuck = m.CopyWith(lccLinks: m.LccLinks.Select(l => l with { Rectifier = l.Rectifier with { TapStep = tiny } }).ToList());
            Assert.Contains(stuck.Validate(), d => d.Severity == ValidationSeverity.Error && d.Message.Contains("tap step", StringComparison.Ordinal));
        }
        Assert.DoesNotContain(m.Validate(), d => d.Severity == ValidationSeverity.Error);
        var sw = Stopwatch.StartNew();
        Assert.Equal(1.0, LccDcLinks.MoveTap(1.0, 1e-17, 0.9, 1.1, -1, _ => true));            // no movement, no hang
        // Re-pinned (R6-04): a search still moving at the 10,000-position cap is refused, not stopped silently at a wrong
        // tap (only a model that skipped Validate can get here); it still ends quickly.
        var capped = Assert.Throws<InvalidOperationException>(() => LccDcLinks.MoveTap(1.0, 1e-12, 0.9, 1.1, -1, _ => true));
        Assert.Contains("still moving after 10000 steps", capped.Message, StringComparison.Ordinal);
        Assert.Equal(0.9, LccDcLinks.MoveTap(1.0, 0.00625, 0.9, 1.1, -1, _ => true), 1e-12);    // a real step: the old sequence
        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(5), $"took {sw.Elapsed}");
    }

    // ---- IO-03 / IO-04: Turtle and RDF/XML nesting ---------------------------------------------------------------------

    [Fact]
    public void Io03_deeply_nested_turtle_is_refused_not_a_stack_overflow()
    {
        string deep = "@prefix ex: <http://e/> .\nex:a ex:p " + new string('(', 100_000) + new string(')', 100_000) + " .\n";
        var ex = Assert.Throws<InvalidDataException>(() => TurtleReader.Read(new RdfGraph(), deep, "deep.ttl"));
        Assert.Contains("deeper than 256", ex.Message, StringComparison.Ordinal);
        string blank = "@prefix ex: <http://e/> .\nex:a ex:p " + string.Concat(Enumerable.Repeat("[ ex:q ", 5_000)) + "ex:z" + new string(']', 5_000) + " .\n";
        Assert.Throws<InvalidDataException>(() => TurtleReader.Read(new RdfGraph(), blank, "blank.ttl"));
        var ok = new RdfGraph();
        TurtleReader.Read(ok, "@prefix ex: <http://e/> .\nex:a ex:p ( ( ex:b ) [ ex:q ex:c ] ) .\n", "ok.ttl");
    }

    [Fact]
    public void Io04_deeply_nested_rdf_xml_is_refused_before_it_is_read()
    {
        var sb = new StringBuilder("<rdf:RDF xmlns:rdf=\"http://www.w3.org/1999/02/22-rdf-syntax-ns#\" xmlns:e=\"http://e/\">");
        for (int k = 0; k < 30_000; k++) sb.Append("<e:X><e:p>");
        sb.Append("<e:X/>");
        for (int k = 0; k < 30_000; k++) sb.Append("</e:p></e:X>");
        sb.Append("</rdf:RDF>");
        var ex = Assert.Throws<InvalidDataException>(() => RdfXmlReader.Read(new RdfGraph(), sb.ToString(), "deep.rdf"));
        Assert.Contains("deeper than 256", ex.Message, StringComparison.Ordinal);
        var ok = new RdfGraph();
        RdfXmlReader.Read(ok, "<rdf:RDF xmlns:rdf=\"http://www.w3.org/1999/02/22-rdf-syntax-ns#\" xmlns:e=\"http://e/\"><e:X rdf:about=\"#a\"><e:p><e:X rdf:about=\"#b\"/></e:p></e:X></rdf:RDF>", "ok.rdf");
    }

    // ---- IO-06: the CGMES folder form ----------------------------------------------------------------------------------

    [Fact]
    public void Io06_a_cgmes_folder_is_held_to_the_archive_limits()
    {
        string folder = Path.Combine(_dir, "bundle");
        Directory.CreateDirectory(folder);
        foreach (string n in new[] { "a_EQ.xml", "b_TP.xml", "c_SV.xml" }) File.WriteAllText(Path.Combine(folder, n), new string(' ', 50_000));
        var ex = Assert.Throws<InvalidDataException>(() => CgmesArchiveReader.ReadDirectory(folder, 100_000));
        Assert.Contains("expand to more than 100000 bytes in all", ex.Message, StringComparison.Ordinal);
        Assert.Contains("c_SV.xml", ex.Message, StringComparison.Ordinal);
        Exception? other = Record.Exception(() => CgmesArchiveReader.ReadDirectory(folder, 200_000));
        Assert.DoesNotContain("expand to more than", other?.Message ?? "", StringComparison.Ordinal);
    }

    // ---- IO-08 / IO-11: COMTRADE -------------------------------------------------------------------------------------

    [Theory]
    [InlineData("S,D,1999\n2000000000,2000000000A,0D\n", "at most 999999")]
    [InlineData("S,D,1999\n-2147483648,2147483647A,1D\n", "is not the analog count")]
    [InlineData("S,D,1999\n3,2A,1D\n1,IA,A,,A,1,0,0,-1,1\n", "has only")]
    public void Io08_channel_counts_are_checked_before_anything_is_sized_from_them(string cfg, string mentions)
    {
        var ex = Assert.Throws<InvalidDataException>(() => ComtradeReader.ParseConfig(cfg));
        Assert.Contains(mentions, ex.Message, StringComparison.Ordinal);
    }

    private static string Cfg(int year, string rates, string start, string type, string mult = "1")
        => $"STN,DEV,{year}\n1,1A,0D\n1,IA,A,,A,1,0,0,-32768,32767,1,1,P\n50\n{rates}\n{start}\n{start}\n{type}\n{mult}\n";

    [Fact]
    public void Io11_nanosecond_stamps_several_rates_and_missing_values_are_read_as_the_standard_writes_them()
    {
        // A 2013 file whose start time has nine decimals: the stamps count nanoseconds.
        ComtradeRecording ns = ComtradeReader.Parse(Cfg(2013, "0\n0,2", "01/01/2020,00:00:00.000000000", "ASCII"),
            Encoding.ASCII.GetBytes("1,0,5\n2,1000,6\n"));
        Assert.True(Math.Abs(ns.TimesSeconds[1] - 1e-6) < 1e-18, $"time {ns.TimesSeconds[1]:R}");
        ComtradeRecording us = ComtradeReader.Parse(Cfg(2013, "0\n0,2", "01/01/2020,00:00:00.000000", "ASCII"),
            Encoding.ASCII.GetBytes("1,0,5\n2,1000,6\n"));
        Assert.Equal(1e-3, us.TimesSeconds[1], 15);

        // Two rates and no stamps: 1000 Hz for samples 1 to 3, then 100 Hz.
        ComtradeRecording multi = ComtradeReader.Parse(Cfg(1999, "2\n1000,3\n100,5", "01/01/2020,00:00:00.000000", "ASCII"),
            Encoding.ASCII.GetBytes("1,,1\n2,,1\n3,,1\n4,,1\n5,,1\n"));
        Assert.Equal(new[] { 0.0, 0.001, 0.002, 0.012, 0.022 }, multi.TimesSeconds.Select(x => Math.Round(x, 12)));

        // Missing values: 99999 in a 1999 ASCII file, an empty field, 0x8000 in a BINARY word.
        ComtradeRecording ascii = ComtradeReader.Parse(Cfg(1999, "1\n1000,3", "01/01/2020,00:00:00.000000", "ASCII"),
            Encoding.ASCII.GetBytes("1,0,5\n2,1000,99999\n3,2000,\n"));
        Assert.Equal(5.0, ascii.Analog[0][0]);
        Assert.True(double.IsNaN(ascii.Analog[0][1]) && double.IsNaN(ascii.Analog[0][2]));
        Assert.Equal(2, ascii.MissingValues);
        byte[] bin = new byte[2 * 10];
        BitConverter.TryWriteBytes(bin.AsSpan(0), 1); BitConverter.TryWriteBytes(bin.AsSpan(4), 0); BitConverter.TryWriteBytes(bin.AsSpan(8), (short)7);
        BitConverter.TryWriteBytes(bin.AsSpan(10), 2); BitConverter.TryWriteBytes(bin.AsSpan(14), 1000); BitConverter.TryWriteBytes(bin.AsSpan(18), short.MinValue);
        ComtradeRecording binary = ComtradeReader.Parse(Cfg(1999, "1\n1000,2", "01/01/2020,00:00:00.000000", "BINARY"), bin);
        Assert.Equal(7.0, binary.Analog[0][0]);
        Assert.True(double.IsNaN(binary.Analog[0][1]));
        Assert.Equal(1, binary.MissingValues);
    }

    // ---- IO-09: DGS numeric attributes ---------------------------------------------------------------------------------

    [Fact]
    public void Io09_a_dgs_attribute_that_is_not_a_number_is_refused_naming_its_line_not_an_internal_error()
    {
        string dgs = "$$General;ID(a:40);Descr(a:40);Val(a:40)\n1;Version;5.0\n$$ElmTerm;ID(a:40);loc_name(a:40);uknom(a:10);outserv(a:5)\n2;T1;abc;x\n";
        DgsFile file = DgsTableReader.Read(dgs);
        var ex = Assert.Throws<DgsFormatException>(() => DgsImporter.Import(file, new DgsImportOptions(false)));
        Assert.Contains("line 4", ex.Message, StringComparison.Ordinal);
        Assert.Contains("ElmTerm", ex.Message, StringComparison.Ordinal);
        DgsRow row = file.Table("ElmTerm")!.Rows[0];
        Assert.Throws<DgsFormatException>(() => row.Real("uknom"));
        DgsFile whole = DgsTableReader.Read(dgs.Replace("2;T1;abc;x", "2;T1;11;1e0", StringComparison.Ordinal));
        Assert.Equal(1L, whole.Table("ElmTerm")!.Rows[0].Int("outserv"));
    }

    // ---- IO-10: RAWX values that would change a record ----------------------------------------------------------------

    [Theory]
    [InlineData("5 / x")]
    [InlineData("1,2")]
    [InlineData("'7'")]
    [InlineData("1\n2")]   // R2-06: a line break splits the record
    public void Io10_a_non_text_value_that_would_change_its_record_is_refused(string value)
    {
        var ex = Assert.Throws<InvalidDataException>(() => PsseFieldRemap.Line(PsseFieldRemap.Bus, ["1", "BUS1", value]));
        Assert.Contains("BASKV", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Io10_text_with_both_quote_kinds_is_refused_and_ordinary_values_pass()
    {
        var ex = Assert.Throws<InvalidDataException>(() => PsseFieldRemap.Line(PsseFieldRemap.Bus, ["1", "5\" x 3' bar", "132"]));
        Assert.Contains("both a single and a double quote", ex.Message, StringComparison.Ordinal);
        // R2-06: a line break (or any control character) in text splits the record.
        var nl = Assert.Throws<InvalidDataException>(() => PsseFieldRemap.Line(PsseFieldRemap.Bus, ["1", "BUS\n1", "132"]));
        Assert.Contains("line break", nl.Message, StringComparison.Ordinal);
        Assert.Equal("1,'BUS 1',132,1", PsseFieldRemap.Line(PsseFieldRemap.Bus, ["1", "BUS 1", " 132 ", "1"]));
        Assert.Equal("1,\"O'NEIL\",1.5E2", PsseFieldRemap.Line(PsseFieldRemap.Bus, ["1", "O'NEIL", "1.5E2"]));
    }

    // ---- batch 2b ----------------------------------------------------------------------------------------------------

    /// <summary>R2-07: a binary sample number above int.MaxValue is refused by number (it wrapped to a negative number).</summary>
    [Fact]
    public void R2_07_a_comtrade_sample_number_beyond_int_is_refused()
    {
        byte[] bin = new byte[10];
        BitConverter.TryWriteBytes(bin.AsSpan(0), 3_000_000_000u); BitConverter.TryWriteBytes(bin.AsSpan(4), 0); BitConverter.TryWriteBytes(bin.AsSpan(8), (short)7);
        var ex = Assert.Throws<InvalidDataException>(() => ComtradeReader.Parse(Cfg(1999, "1\n1000,2", "01/01/2020,00:00:00.000000", "BINARY"), bin));
        Assert.Contains("sample number 3000000000", ex.Message, StringComparison.Ordinal);
    }

    /// <summary>R2-08: many rate segments and many samples are timed in about linear time, with the same times as the walk.</summary>
    [Fact]
    public void R2_08_many_rate_segments_are_timed_in_linear_time()
    {
        const int n = 100_000;
        var rates = new StringBuilder();
        for (int k = 1; k <= n; k++) rates.Append(k % 2 == 0 ? "2000," : "1000,").Append(k).Append('\n');
        var data = new StringBuilder();
        for (int k = 1; k <= n; k++) data.Append(k).Append(",,1\n");
        var sw = Stopwatch.StartNew();
        ComtradeRecording r = ComtradeReader.Parse(Cfg(1999, n + "\n" + rates.ToString().TrimEnd('\n'), "01/01/2020,00:00:00.000000", "ASCII"), Encoding.ASCII.GetBytes(data.ToString()));
        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(30), $"took {sw.Elapsed}");
        Assert.Equal(n, r.TimesSeconds.Count);
        Assert.Equal(0.0, r.TimesSeconds[0]);
        Assert.Equal(0.0005, r.TimesSeconds[1], 12);   // sample 2 is in the 2000 Hz segment that follows sample 1's
        Assert.Equal(0.0015, r.TimesSeconds[2], 12);   // sample 3: back at 1000 Hz
    }

    /// <summary>R2-13: the folder total counts the bytes read.</summary>
    [Fact]
    public void R2_13_the_cgmes_running_total_is_shared_by_every_route()
    {
        var ex = Assert.Throws<InvalidDataException>(() => CgmesArchiveReader.AddToTotal(900, 200, "p.xml", 1000));
        Assert.Contains("expand to more than 1000 bytes in all", ex.Message, StringComparison.Ordinal);
        Assert.Equal(1000, CgmesArchiveReader.AddToTotal(800, 200, "p.xml", 1000));
    }
}
