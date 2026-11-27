using System.Security.Cryptography;
using System.Text;
using GridSim.Core.Dynamics.LongTerm;
using GridSim.Core.IO;
using GridSim.Core.Presentation.Workbench;
using GridSim.Core.Protection.Placed;
using Xunit;

namespace GridSim.Tests;

/// <summary>
/// 2.1.135 (B31 widened, security): the CSV formula-injection guard (CWE-1236) on the writers 2.1.134 did not cover: the
/// report CSV (<c>gridsim report</c>, <see cref="ReportRenderer.ToCsv"/>), the inline CLI writers (estimate residuals and
/// HTI files, emt probe headers, the bench, bench --estimate, stress and nminus1-sweep case labels, and the rest listed in
/// the CHANGELOG), the long-term CSV events cell, the RMS converter and device names and the placed-protection device id.
/// A string cell that starts with = + - @ TAB or CR gets a leading apostrophe; a number is never touched.
/// The byte-for-byte pins were taken from the 2.1.134 Release CLI (736a5ff4) BEFORE this change.
/// </summary>
public sealed class CsvFormulaInjectionWritersTests : IDisposable
{
    private readonly string _dir = JournalTestSupport.NewDir("csvinj2");

    public void Dispose() => JournalTestSupport.Delete(_dir);

    public static TheoryData<string> Triggers => new() { "=cmd", "+one", "-two", "@three", "\tfour", "\rfive" };

    private static string Sha(string text) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(text)));

    // ------------------------------------------------------------------ report CSV

    // The exact template and result bytes of the pin (LF line ends, a final LF), built line by line so the source file's own
    // line ends cannot change them.
    private static readonly string PinTemplate = string.Join("\n",
        "{ \"schema\": \"gridsim-report-template/1\", \"title\": \"Pin\",",
        "  \"sections\": [ { \"heading\": \"Buses\", \"blocks\": [",
        "    { \"type\": \"table\", \"title\": \"Table\", \"path\": \"/rows\", \"columns\": [",
        "      { \"key\": \"name\", \"title\": \"Name\" }, { \"key\": \"v\", \"title\": \"Angle\", \"unit\": \"deg\", \"decimals\": 3 },",
        "      { \"key\": \"p\", \"title\": \"P\" }, { \"key\": \"ok\", \"title\": \"OK\" }, { \"key\": \"none\", \"title\": \"Missing\" } ] },",
        "    { \"type\": \"table\", \"title\": \"Auto\", \"path\": \"/rows\" } ] } ] }") + "\n";

    private static readonly string PinResult = string.Join("\n",
        "{ \"rows\": [",
        "  { \"name\": \"Bus 1\", \"v\": -12.3456, \"p\": -40, \"ok\": true, \"none\": null },",
        "  { \"name\": \"1-2\", \"v\": 0.5, \"p\": 2.25, \"ok\": false },",
        "  { \"name\": \"a, \\\"b\\\"\", \"v\": -0.0001, \"p\": -1234.56789 } ] }") + "\n";

    [Fact]
    public void Report_csv_of_an_ordinary_result_is_byte_for_byte_the_2_1_134_file()
    {
        // Pinned: "gridsim report t.json r.json --out o.csv" with the 2.1.134 CLI on exactly these bytes. The file holds
        // five negative numbers (-12.346, -40, -12.3456, -0.0001, -1234.5679 twice) and they must not move.
        string csv = ReportRenderer.ToCsv(ReportRenderer.Build(Encoding.UTF8.GetBytes(PinTemplate), Encoding.UTF8.GetBytes(PinResult)));
        Assert.Equal("cf53d8ed9b39f12ca0c545438b4926db434b61d7828b1c20f3a1013248d0e820", Sha(csv));
        Assert.Contains("\nBus 1,-12.346,-40,yes,\n", csv, StringComparison.Ordinal);
        Assert.Contains("\n\"a, \"\"b\"\"\",-0.0001,-1234.5679,,\n", csv, StringComparison.Ordinal);
    }

    [Theory]
    [MemberData(nameof(Triggers))]
    public void Report_csv_neutralises_a_string_cell_starting_with_each_trigger(string name)
    {
        string result = "{ \"rows\": [ { \"name\": " + System.Text.Json.JsonSerializer.Serialize(name) + ", \"v\": -7.5 } ] }";
        string template = "{ \"schema\": \"gridsim-report-template/1\", \"title\": \"T\", \"sections\": [ { \"heading\": \"H\", \"blocks\": [ " +
            "{ \"type\": \"table\", \"title\": \"H\", \"path\": \"/rows\", \"columns\": [ { \"key\": \"name\" }, { \"key\": \"v\", \"decimals\": 2 } ] } ] } ] }";
        string csv = ReportRenderer.ToCsv(ReportRenderer.Build(Encoding.UTF8.GetBytes(template), Encoding.UTF8.GetBytes(result)));
        // The apostrophe, then the usual RFC 4180 quoting (a carriage return forces quotes). The number keeps its minus.
        string cell = name.Contains('\r', StringComparison.Ordinal) ? "\"'" + name + "\"" : "'" + name;
        Assert.Contains("\n" + cell + ",-7.50\n", csv, StringComparison.Ordinal);
    }

    [Theory]
    [MemberData(nameof(Triggers))]
    public void Report_csv_neutralises_a_heading_starting_with_each_trigger(string title)
    {
        string template = "{ \"schema\": \"gridsim-report-template/1\", \"title\": \"T\", \"sections\": [ { \"heading\": \"H\", \"blocks\": [ " +
            "{ \"type\": \"table\", \"title\": \"H\", \"path\": \"/rows\", \"columns\": [ { \"key\": \"v\", \"title\": " +
            System.Text.Json.JsonSerializer.Serialize(title) + " } ] } ] } ] }";
        string csv = ReportRenderer.ToCsv(ReportRenderer.Build(Encoding.UTF8.GetBytes(template), Encoding.UTF8.GetBytes("{ \"rows\": [ { \"v\": -3 } ] }")));
        string cell = title.Contains('\r', StringComparison.Ordinal) ? "\"'" + title + "\"" : "'" + title;
        Assert.Contains("\n" + cell + "\n-3\n", csv, StringComparison.Ordinal);
    }

    [Fact]
    public void Report_csv_never_touches_a_number_even_a_negative_one()
    {
        // Every numeric cell below starts with '-'. Each is written as the number text alone.
        string template = "{ \"schema\": \"gridsim-report-template/1\", \"title\": \"T\", \"sections\": [ { \"heading\": \"H\", \"blocks\": [ " +
            "{ \"type\": \"table\", \"title\": \"H\", \"path\": \"/rows\" } ] } ] }";
        string csv = ReportRenderer.ToCsv(ReportRenderer.Build(Encoding.UTF8.GetBytes(template),
            Encoding.UTF8.GetBytes("{ \"rows\": [ { \"a\": -1, \"b\": -2.5, \"c\": -1e-3 } ] }")));
        Assert.Contains("\na,b,c\n-1,-2.5000,-0.0010\n", csv, StringComparison.Ordinal);
        Assert.DoesNotContain("'-", csv, StringComparison.Ordinal);
    }

    [Fact]
    public void Report_csv_neutralises_a_string_that_looks_like_a_negative_number()
    {
        // The guard is by type, not by look: a JSON STRING "-5" is text and is neutralised; the JSON NUMBER -5 is not.
        string template = "{ \"schema\": \"gridsim-report-template/1\", \"title\": \"T\", \"sections\": [ { \"heading\": \"H\", \"blocks\": [ " +
            "{ \"type\": \"table\", \"title\": \"H\", \"path\": \"/rows\" } ] } ] }";
        string csv = ReportRenderer.ToCsv(ReportRenderer.Build(Encoding.UTF8.GetBytes(template),
            Encoding.UTF8.GetBytes("{ \"rows\": [ { \"s\": \"-5\", \"n\": -5 } ] }")));
        Assert.Contains("\ns,n\n'-5,-5\n", csv, StringComparison.Ordinal);
    }

    // ------------------------------------------------------------------ long-term CSV (events cell)

    private static LongTermResult OneSample(string events) => new(0.5, 1.0, [], [], [],
        [new LongTermSample(0.5, -0.01, 0.95, 3, [], [], [], [], 2, -0.25, events)], [], [], true, null);

    [Fact]
    public void Long_term_csv_of_an_ordinary_run_is_unchanged()
    {
        // The 2.1.134 rule written out by hand: round-trip numbers, the events cell always in quotes.
        Assert.Equal("t_s,df_hz,min_v_pu,min_v_bus,passes,residual_mw,events\n0.5,-0.01,0.95,3,2,-0.25,\"branch 1-2 trip\"\n",
            OneSample("branch 1-2 trip").ToCsv());
    }

    [Theory]
    [MemberData(nameof(Triggers))]
    public void Long_term_csv_neutralises_an_events_cell_starting_with_each_trigger(string events) =>
        Assert.EndsWith("0.5,-0.01,0.95,3,2,-0.25,\"'" + events + "\"\n", OneSample(events).ToCsv(), StringComparison.Ordinal);

    // ------------------------------------------------------------------ RMS converter and device names

    [Theory]
    [MemberData(nameof(Triggers))]
    public void Rms_csv_name_neutralises_each_trigger(string name) =>
        Assert.Equal("'" + name, RmsEventsExport.CsvName(name));

    [Theory]
    [InlineData("VSC 1", "VSC 1")]
    [InlineData("a,b", "\"a,b\"")]
    [InlineData("q\"x", "\"q\"\"x\"")]
    [InlineData("=a,b", "\"'=a,b\"")]
    public void Rms_csv_name_keeps_its_quoting_rule(string name, string expected) =>
        Assert.Equal(expected, RmsEventsExport.CsvName(name));

    // ------------------------------------------------------------------ placed-protection CSV

    private static PlacedRow Row(string id, string? reason) =>
        new(id, "51", "50/51", true, reason, -12.5, "A sec", null, null, null, 0.3, 0.3, null);

    [Fact]
    public void Placed_csv_of_an_ordinary_row_is_unchanged()
    {
        string csv = PlacedElementEvaluator.ToCsv([Row("R1", null)]);
        Assert.EndsWith("\nR1,51,50/51,true,,-12.5,A sec" + new string(',', 10) + "0.3,true\n", csv, StringComparison.Ordinal);
    }

    [Theory]
    [MemberData(nameof(Triggers))]
    public void Placed_csv_neutralises_a_device_id_and_a_reason_starting_with_each_trigger(string text)
    {
        string csv = PlacedElementEvaluator.ToCsv([Row(text, text)]);
        Assert.Contains("\n'" + text + ",51,50/51,true,\"'" + text + "\",-12.5,", csv, StringComparison.Ordinal);
    }

    // ------------------------------------------------------------------ inline CLI writers (Program.cs)

    private static string CopyCase14(string dir, string name)
    {
        Directory.CreateDirectory(dir);
        string path = Path.Combine(dir, name);
        File.Copy(Path.Combine(CliProcess.Root(), "data", "matpower", "lib", "case14.m"), path);
        return path;
    }

    private static string DataLines(string path) =>
        string.Concat(File.ReadAllText(path).Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n')
            .Where(l => !l.StartsWith('#')).Select(l => l + "\n"))[..^1];

    [Fact]
    public void Estimate_residuals_csv_is_byte_for_byte_the_2_1_134_file()
    {
        // Pinned from "estimate case14 --synth --out est" with the 2.1.134 CLI: the data lines (the '#' run header holds the
        // clock and the machine), LF line ends. 91 lines carry a negative number; they are written by type and unchanged.
        // The location cell is Measurement.Describe(), which always starts "bus " or "branch ", so no trigger can reach it
        // from the CLI; the guard there is defence in depth and this pin shows it changes nothing.
        CliProcess.Result r = JournalTestSupport.Run(_dir, "estimate", "case14", "--synth", "--out", "est");
        Assert.True(r.ExitCode == 0, r.ToString());
        string text = DataLines(Path.Combine(_dir, "est", "residuals.csv"));
        Assert.Equal(91, text.Split('\n').Count(l => l.Contains(",-", StringComparison.Ordinal)));
        Assert.Equal("8f26a3b700e03d454c26b3916ee0d0e9b2115758dde46808818a0d7eab77289f", Sha(text));
    }

    [Fact]
    public void Emt_timeseries_csv_is_byte_for_byte_the_2_1_134_file()
    {
        // Pinned from "emt case14 --duration 0.02 --freq 50 --probe v:1,i:1-2 --out emt" with the 2.1.134 CLI: the data
        // lines, LF line ends; 553 rows hold a negative sample. A probe token must start "v:" or "i:", so no trigger can
        // reach the header from the CLI; the guard is defence in depth and this pin shows it changes nothing.
        CliProcess.Result r = JournalTestSupport.Run(_dir, "emt", "case14", "--duration", "0.02", "--freq", "50", "--probe", "v:1,i:1-2", "--out", "emt");
        Assert.True(r.ExitCode == 0, r.ToString());
        string text = DataLines(Path.Combine(_dir, "emt", "timeseries.csv"));
        Assert.StartsWith("t_seconds,v:1,i:1-2\n", text, StringComparison.Ordinal);
        Assert.Equal(553, text.Split('\n').Count(l => l.Contains(",-", StringComparison.Ordinal)));
        Assert.Equal("6c08f01a72b553e98a68b2164f9c3cc53b195470bd859037cabb6a86c786f883", Sha(text));
    }

    // A case label comes from a file name. A file name cannot hold a tab or a carriage return on Windows, and a token
    // starting with '-' is refused as an unknown option unless it sits inside a list (--cases a,-b), so the CLI checks
    // below cover = + - @; the guard's handling of TAB and CR is the shared helper's, covered by CsvFormulaInjectionTests.

    private static List<string[]> Rows(string csvPath) =>
        File.ReadAllText(csvPath).Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Where(l => !l.StartsWith('#')).Skip(1).Select(l => l.Split(',')).ToList();

    [Fact]
    public void Bench_estimate_csv_neutralises_case_labels_and_keeps_an_ordinary_label()
    {
        string w = Path.Combine(_dir, "be");
        foreach (string n in new[] { "=x.m", "+b.m", "-c.m", "@d.m" }) CopyCase14(w, n);
        CliProcess.Result r = JournalTestSupport.Run(w, "bench", "--estimate", "--runs", "1", "--cases", "=x.m,+b.m,-c.m,@d.m,case14");
        Assert.True(r.ExitCode == 0, r.ToString());
        List<string[]> rows = Rows(Path.Combine(w, "results", "bench-estimate", "bench-estimate.csv"));
        // Two rows (dense, sparse) per case, in the order given; the remaining columns are numbers and booleans.
        Assert.Equal(new[] { "'=x.m", "'=x.m", "'+b.m", "'+b.m", "'-c.m", "'-c.m", "'@d.m", "'@d.m", "case14", "case14" },
            rows.Select(x => x[0]).ToArray());
        Assert.All(rows, x => Assert.Equal(new[] { "14", "122", "27" }, x[1..4]));
    }

    [Fact]
    public void Stress_csv_neutralises_a_case_label()
    {
        string w = Path.Combine(_dir, "st");
        CopyCase14(w, "@d.m");
        CliProcess.Result r = JournalTestSupport.Run(w, "stress", "@d.m", "--from", "1", "--to", "1.1", "--step", "0.1");
        Assert.True(r.ExitCode == 0, r.ToString());
        List<string[]> rows = Rows(Path.Combine(w, "results", "stress.csv"));
        Assert.Single(rows);
        // Pinned values of the 2.1.134 run (label, buses, max loadability, vmin at max); only the label gains the apostrophe.
        Assert.Equal(new[] { "'@d.m", "14", "1.10", "1.010" }, rows[0][..4]);
    }

    [Fact]
    public void Nminus1_sweep_csv_neutralises_a_case_label()
    {
        string w = Path.Combine(_dir, "sw");
        CopyCase14(w, "+x.m");
        CliProcess.Result r = JournalTestSupport.Run(w, "nminus1-sweep", "+x.m");
        Assert.True(r.ExitCode == 3, r.ToString());   // case14 is not N-1 secure (2.1.134: exit 3)
        List<string[]> rows = Rows(Path.Combine(w, "results", "nminus1-sweep.csv"));
        Assert.Single(rows);
        // Pinned from the 2.1.134 run: label, buses, branches, screened, secure, violations, islanded.
        Assert.Equal(new[] { "'+x.m", "14", "20", "20", "16", "3", "0" }, rows[0][..7]);
    }
}
