using System.Globalization;
using System.Text.Json;
using GridSim.Cli;
using Xunit;

namespace GridSim.Tests;

/// <summary>
/// F10 (October 2026): unit tests of the shared CLI result writer (src/GridSim.Cli/CliResultWriter.cs, compiled into
/// this test project as well, like InterposeTransport.cs). They pin the three promises the writer makes:
///
///   1. numbers round-trip exactly: parsing a written double gives back the very same bits (no display rounding);
///   2. the files are invariant-culture and laid out as documented in manual ch. 19 "Result files";
///   3. the flags are checked fail-loud (missing value, wrong extension, duplicates, a verb that writes no result).
/// </summary>
public sealed class CliResultWriterTests
{
    // Values chosen to break any lossy formatting: shortest-round-trip edge cases, sub-normals, the extremes, signed
    // zero, and numbers whose "F"/"G" forms at display precision are not exact.
    public static readonly double[] TrickyDoubles =
    {
        0.0, -0.0, 0.1, 0.2, 0.30000000000000004, 1.0 / 3.0, 2.0 / 3.0, Math.PI, Math.E, 1e-300, 5e-324,
        double.Epsilon, double.MaxValue, double.MinValue, 1.7976931348623157e308, 123456789.12345679, -9.87654321e-12,
        147.88057464425765, 71.11942535574262, 1e21, 1e-7, 4.35, 2.675, 1.0000000000000002, 0.9999999999999999,
    };

    private static long Bits(double d) => BitConverter.DoubleToInt64Bits(d);

    private static string TempDir()
    {
        string d = Path.Combine(Path.GetTempPath(), "gridsim-writer-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(d);
        return d;
    }

    [Fact]
    public void Number_text_round_trips_every_tricky_double_to_the_same_bits()
    {
        foreach (double d in TrickyDoubles)
        {
            string t = CliResultWriter.Number(d);
            double back = double.Parse(t, NumberStyles.Float, CultureInfo.InvariantCulture);
            Assert.True(Bits(d) == Bits(back), $"{d:R} wrote '{t}', read back {back:R}");
        }
    }

    [Fact]
    public void Non_finite_numbers_are_named_not_dropped()
    {
        Assert.Equal("NaN", CliResultWriter.Number(double.NaN));
        Assert.Equal("Infinity", CliResultWriter.Number(double.PositiveInfinity));
        Assert.Equal("-Infinity", CliResultWriter.Number(double.NegativeInfinity));
    }

    [Fact]
    public void Json_numbers_round_trip_exactly_and_non_finite_values_are_strings()
    {
        var r = new CliResult("test");
        CliResultTable t = r.Table("values", "i", "x");
        for (int i = 0; i < TrickyDoubles.Length; i++) t.Row(i, TrickyDoubles[i]);
        r.Set("nan", double.NaN).Set("pinf", double.PositiveInfinity).Set("ninf", double.NegativeInfinity).Set("none", null);

        string json = CliResultWriter.JsonText(r, "gridsim test", new[] { "test", "--json", "x.json" }, 3);
        using JsonDocument doc = JsonDocument.Parse(json);
        JsonElement root = doc.RootElement;
        Assert.Equal(CliResultWriter.Schema, root.GetProperty("schema").GetString());
        Assert.Equal("test", root.GetProperty("verb").GetString());
        Assert.Equal(3, root.GetProperty("exitCode").GetInt32());
        Assert.Equal(new[] { "test", "--json", "x.json" }, root.GetProperty("args").EnumerateArray().Select(e => e.GetString()).ToArray());

        JsonElement rows = root.GetProperty("tables").GetProperty("values");
        Assert.Equal(TrickyDoubles.Length, rows.GetArrayLength());
        int k = 0;
        foreach (JsonElement row in rows.EnumerateArray())
        {
            Assert.Equal(k, row.GetProperty("i").GetInt32());
            // Parse the raw token ourselves, so the check does not depend on the JSON reader's own number handling.
            double back = double.Parse(row.GetProperty("x").GetRawText(), NumberStyles.Float, CultureInfo.InvariantCulture);
            Assert.True(Bits(TrickyDoubles[k]) == Bits(back), $"row {k}: {TrickyDoubles[k]:R} came back as {back:R}");
            k++;
        }
        JsonElement s = root.GetProperty("summary");
        Assert.Equal("NaN", s.GetProperty("nan").GetString());
        Assert.Equal("Infinity", s.GetProperty("pinf").GetString());
        Assert.Equal("-Infinity", s.GetProperty("ninf").GetString());
        Assert.Equal(JsonValueKind.Null, s.GetProperty("none").ValueKind);
    }

    [Fact]
    public void Json_keeps_readable_text_such_as_arrows_and_names_with_quotes()
    {
        var r = new CliResult("grade");
        r.Set("pair", "R1->R2").Set("name", "Line \"A\" & <B>");
        using JsonDocument doc = JsonDocument.Parse(CliResultWriter.JsonText(r, "t", Array.Empty<string>(), 0));
        Assert.Equal("R1->R2", doc.RootElement.GetProperty("summary").GetProperty("pair").GetString());
        Assert.Equal("Line \"A\" & <B>", doc.RootElement.GetProperty("summary").GetProperty("name").GetString());
        Assert.Contains("R1->R2", CliResultWriter.JsonText(r, "t", Array.Empty<string>(), 0), StringComparison.Ordinal);
    }

    [Fact]
    public void Csv_layout_is_main_table_then_stem_table_files_then_summary()
    {
        string dir = TempDir();
        try
        {
            var r = new CliResult("demo");
            r.Set("case", "case9").Set("total", 1.0 / 3.0);
            r.Table("first", "a", "b").Row(1, 0.1).Row(2, "x,y");
            r.Table("second", "c").Row(true).Row((object?)null);   // cast: a bare null would be the params array itself
            IReadOnlyList<string> written = CliResultWriter.WriteCsv(r, Path.Combine(dir, "out.csv"), 3);

            Assert.Equal(new[] { Path.Combine(dir, "out.csv"), Path.Combine(dir, "out-second.csv"), Path.Combine(dir, "out-summary.csv") }, written);
            string nl = Environment.NewLine;
            Assert.Equal($"a,b{nl}1,0.1{nl}2,\"x,y\"{nl}", File.ReadAllText(written[0]));
            Assert.Equal($"c{nl}true{nl}{nl}", File.ReadAllText(written[1]));
            Assert.Equal($"key,value{nl}verb,demo{nl}exit_code,3{nl}case,case9{nl}total,{(1.0 / 3.0).ToString("R", CultureInfo.InvariantCulture)}{nl}",
                File.ReadAllText(written[2]));
            // No byte-order mark: the files start with the header text itself.
            Assert.Equal((byte)'a', File.ReadAllBytes(written[0])[0]);
            // No temporary file is left behind.
            Assert.Equal(3, Directory.GetFiles(dir).Length);
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    [Fact]
    public void Csv_with_no_table_writes_the_summary_as_the_main_file()
    {
        string dir = TempDir();
        try
        {
            var r = new CliResult("cpf");
            r.Set("max_lambda", 2.5);
            IReadOnlyList<string> written = CliResultWriter.WriteCsv(r, Path.Combine(dir, "sub", "cpf.csv"), 0);
            Assert.Single(written);
            string nl = Environment.NewLine;
            Assert.Equal($"key,value{nl}verb,cpf{nl}exit_code,0{nl}max_lambda,2.5{nl}", File.ReadAllText(written[0]));
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    [Fact]
    public void Files_are_invariant_culture_whatever_the_thread_culture()
    {
        CultureInfo saved = CultureInfo.CurrentCulture;
        try
        {
            // German uses a comma as the decimal separator: a culture-sensitive writer would emit "0,5".
            CultureInfo.CurrentCulture = new CultureInfo("de-DE");
            var r = new CliResult("demo");
            r.Table("t", "x", "n").Row(0.5, 1234567);
            var sw = new StringWriter(CultureInfo.InvariantCulture);
            CliResultWriter.WriteTableCsv(sw, r.Tables[0]);
            Assert.Equal($"x,n{Environment.NewLine}0.5,1234567{Environment.NewLine}", sw.ToString());
            Assert.Contains("\"x\": 0.5", CliResultWriter.JsonText(r, "t", Array.Empty<string>(), 0), StringComparison.Ordinal);
        }
        finally { CultureInfo.CurrentCulture = saved; }
    }

    [Fact]
    public void Csv_fields_are_quoted_only_when_needed()
    {
        Assert.Equal("plain", CliResultWriter.CsvField("plain"));
        Assert.Equal("\"a,b\"", CliResultWriter.CsvField("a,b"));
        Assert.Equal("\"say \"\"hi\"\"\"", CliResultWriter.CsvField("say \"hi\""));
        Assert.Equal("\"two\nlines\"", CliResultWriter.CsvField("two\nlines"));
        Assert.Equal("", CliResultWriter.CsvCell(null));
        Assert.Equal("false", CliResultWriter.CsvCell(false));
    }

    [Fact]
    public void Values_are_normalised_exactly_and_unknown_types_are_refused()
    {
        Assert.Equal(7L, CliResultValues.Normalise(7));
        Assert.Equal(7L, CliResultValues.Normalise((short)7));
        Assert.Equal((double)0.1f, CliResultValues.Normalise(0.1f));   // float widens exactly
        Assert.Equal("Friday", CliResultValues.Normalise(DayOfWeek.Friday));
        Assert.Equal("2026-10-03T12:00:00.0000000Z", CliResultValues.Normalise(new DateTime(2026, 10, 3, 12, 0, 0, DateTimeKind.Utc)));
        Assert.Throws<InvalidOperationException>(() => CliResultValues.Normalise(new object()));
        Assert.Throws<InvalidOperationException>(() => CliResultValues.Normalise(1.5m));
    }

    [Fact]
    public void Report_code_defects_fail_loud()
    {
        var r = new CliResult("demo");
        r.Set("a", 1);
        Assert.Throws<InvalidOperationException>(() => r.Set("a", 2));              // a key set twice
        Assert.Throws<InvalidOperationException>(() => r.Set("Bad-Key", 1));        // not snake_case
        Assert.Throws<InvalidOperationException>(() => r.Table("summary", "x"));    // reserved
        CliResultTable t = r.Table("t", "x", "y");
        Assert.Throws<InvalidOperationException>(() => r.Table("t", "z"));          // a table created twice
        Assert.Throws<InvalidOperationException>(() => t.Row(1));                   // too few cells
        Assert.Throws<InvalidOperationException>(() => r.Table("u", "x", "x"));     // a column named twice
        Assert.False(r.Completed);
        r.Complete();
        Assert.True(r.Completed);
    }

    // ---- Flag parsing (CliOutput.Parse) -------------------------------------------------------------------------

    private static (string? Error, List<string> Remaining, List<(string Format, string Path)> Requests) Parse(params string[] args)
    {
        var rest = args.ToList();
        var requests = new List<(string, string)>();
        string? error = CliOutput.Parse(rest, requests);
        return (error, rest, requests);
    }

    [Fact]
    public void Writer_flags_are_taken_off_the_argument_list_with_their_values()
    {
        var p = Parse("dcopf", "--json", "r.json", "case9", "--csv", "r.csv", "--top", "5", "--out", "o.json");
        Assert.Null(p.Error);
        Assert.Equal(new[] { "dcopf", "case9", "--top", "5" }, p.Remaining);
        Assert.Equal(new[] { ("json", "r.json"), ("csv", "r.csv"), ("json", "o.json") }, p.Requests);
    }

    [Theory]
    [InlineData("--json", "r.csv", "a file name ending in .json")]
    [InlineData("--csv", "r.json", "a file name ending in .csv")]
    [InlineData("--out", "r.txt", "a file name ending in .json or .csv")]
    [InlineData("--out", "results", "a file name ending in .json or .csv")]
    public void A_wrong_extension_is_refused_naming_what_was_expected(string flag, string value, string expected)
    {
        var p = Parse("ptdf", "case9", "--bus", "5", flag, value);
        Assert.Equal($"error: {flag} expected {expected} but got '{value}'", p.Error);
    }

    [Fact]
    public void A_missing_value_a_repeated_flag_or_one_file_named_twice_is_refused()
    {
        Assert.Equal("error: --json expected a file path but got ''", Parse("dcopf", "case9", "--json").Error);
        Assert.Equal("error: --json expected a file path but got ''", Parse("dcopf", "case9", "--json", "--top", "3").Error);
        Assert.Equal("error: --csv given more than once; name one file per flag.", Parse("dcopf", "case9", "--csv", "a.csv", "--csv", "b.csv").Error);
        Assert.StartsWith("error: two result flags name the same file", Parse("dcopf", "case9", "--json", "a.json", "--out", "a.json").Error, StringComparison.Ordinal);
    }

    [Fact]
    public void Verbs_that_write_no_result_refuse_json_and_csv_instead_of_ignoring_them()
    {
        Assert.StartsWith("error: --json writes a verb's result file", Parse("stability", "case9", "--json", "x.json").Error, StringComparison.Ordinal);
        Assert.StartsWith("error: --csv writes a verb's result file", Parse("case9", "--csv", "x.csv").Error, StringComparison.Ordinal);
        Assert.StartsWith("error: --json writes a verb's result file", Parse("serve", "case9", "--json", "x.json").Error, StringComparison.Ordinal);
        // modal's own --json (print JSON to the console) and screen's own --csv (its table) keep working, untouched.
        var modal = Parse("modal", "case9", "--json");
        Assert.Null(modal.Error);
        Assert.Equal(new[] { "modal", "case9", "--json" }, modal.Remaining);
        var screen = Parse("screen", "--csv", "s.csv");
        Assert.Null(screen.Error);
        Assert.Equal(new[] { "screen", "--csv", "s.csv" }, screen.Remaining);
        // --out belongs to many verbs (output folders, report paths): left alone on the verbs that are not writers.
        var sweep = Parse("nminus1-sweep", "case9", "--out", "r.md");
        Assert.Null(sweep.Error);
        Assert.Equal(new[] { "nminus1-sweep", "case9", "--out", "r.md" }, sweep.Remaining);
        Assert.Empty(sweep.Requests);
    }

    [Fact]
    public void Convert_takes_json_and_csv_but_leaves_out_for_its_own_refusal()
    {
        var p = Parse("convert", "case9", "out.m", "--out", "r.json", "--json", "r.json");
        Assert.Null(p.Error);
        Assert.Equal(new[] { "convert", "case9", "out.m", "--out", "r.json" }, p.Remaining);   // RequireVerbFlags refuses --out
        Assert.Equal(new[] { ("json", "r.json") }, p.Requests);
    }

    [Fact]
    public void The_writer_verbs_are_the_f10_list_and_serve_is_excluded()
    {
        string[] f10 =
        {
            "dcopf", "acopf", "cpf", "taps", "ptdf", "lodf", "iec60909", "fault", "protection-check", "grade", "nminus1",
            "nminus2", "n-1-1", "cycle", "freq", "domains", "dist3ph", "losses", "physics-check", "experimental",
            "dcpf", "convert", "validate",
            "zonal",   // added 2026-10 (S2 / P02): the zonal verb writes through the shared writer (new coverage, not a moved expectation)
            "pf",      // added 2026-10 (N122): the F22 pf verb writes through the shared writer (new coverage, not a moved expectation)
            "restore", // added 2026-10 (R072): the new restore verb writes through the shared writer (new coverage, not a moved expectation)
            "pmu-ingest", // added 2026-10 (N061): the new pmu-ingest verb writes through the shared writer (new coverage, not a moved expectation)
            "pmu-estimate", // added 2026-10 (1.95.245): the new pmu-estimate verb writes through the shared writer (new coverage, not a moved expectation)
            "acopf-full", // added 2026-10-25: the new acopf-full verb writes through the shared writer (new coverage, not a moved expectation)
            "scuc",       // added 2026-10-25: the new scuc verb writes through the shared writer (new coverage, not a moved expectation)
            "inertia-placement", // added 2026-10-25: the new inertia-placement verb writes through the shared writer (new coverage, not a moved expectation)
            "scopf",      // added 2026-10-25: the new scopf verb writes through the shared writer (new coverage, not a moved expectation)
            "qsts",       // added on the QSTS branch: the new qsts verb writes through the shared writer (new coverage, not a moved expectation)
            "hosting",    // added on the QSTS branch: the new hosting verb writes through the shared writer (new coverage, not a moved expectation)
        };
        foreach (string v in f10) Assert.Contains(v, CliOutput.WriterVerbs);
        Assert.DoesNotContain("serve", CliOutput.WriterVerbs);
        Assert.Equal(f10.Order(StringComparer.Ordinal), CliOutput.WriterVerbs.Select(CliOutput.Canonical).Distinct().Order(StringComparer.Ordinal));
        Assert.Equal("nminus1", CliOutput.Canonical("n-1"));
        Assert.Equal("nminus2", CliOutput.Canonical("N-2"));
        Assert.Equal("n-1-1", CliOutput.Canonical("nminus1minus1"));
    }
}
