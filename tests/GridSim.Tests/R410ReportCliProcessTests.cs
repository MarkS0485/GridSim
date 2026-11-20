using System.Text;
using System.Text.Json;
using Xunit;

namespace GridSim.Tests;

/// <summary>RC4 R4-10 through the real entry point: <c>gridsim report</c> makes the workbench report headless, byte for byte.</summary>
public sealed class R410ReportCliProcessTests : IDisposable
{
    private readonly string _root = R401Cases.NewRoot();
    public void Dispose() { try { Directory.Delete(_root, true); } catch (IOException) { } }

    private string P(string name) => Path.Combine(_root, name);

    private string Write(string name, string text)
    {
        File.WriteAllText(P(name), text, new UTF8Encoding(false));
        return P(name);
    }

    private static CliProcess.Result Run(params string[] args) => CliProcess.RunBuilt(args);

    private const string Result = "{ \"status\": \"OK\", \"rows\": [ { \"bus\": 1, \"v\": 1.0123 }, { \"bus\": 2, \"v\": null } ], \"curve\": { \"x\": [1, 2, 3], \"y\": [3, 2, 1] } }";

    private const string Template = "{ \"schema\": \"gridsim-report-template/1\", \"title\": \"CLI report\", \"sections\": [ { \"heading\": \"H\", \"blocks\": [ " +
        "{ \"type\": \"value\", \"label\": \"Status\", \"path\": \"/status\" }, " +
        "{ \"type\": \"table\", \"title\": \"Rows\", \"path\": \"/rows\", \"columns\": [ { \"key\": \"bus\" }, { \"key\": \"v\", \"decimals\": 2 } ] }, " +
        "{ \"type\": \"plot\", \"title\": \"Curve\", \"x\": \"/curve/x\", \"y\": \"/curve/y\" } ] } ] }";

    [Fact]
    public void A_report_is_written_in_each_format_and_repeats_byte_for_byte()
    {
        string t = Write("t.json", Template), r = Write("r.json", Result);
        CliProcess.Result md1 = Run("report", t, r, "--out", P("a.md"));
        Assert.True(md1.ExitCode == 0, md1.ToString());
        Run("report", t, r, "--out", P("b.md"));
        Assert.Equal(File.ReadAllBytes(P("a.md")), File.ReadAllBytes(P("b.md")));
        string md = File.ReadAllText(P("a.md"));
        Assert.Contains("# CLI report", md, StringComparison.Ordinal);
        Assert.Contains("- **Status:** OK", md, StringComparison.Ordinal);
        Assert.Contains("| 2 | n/a |", md, StringComparison.Ordinal);   // the null voltage is a gap, never 0.00
        Assert.Contains("Result SHA-256: " + GridSim.Core.Presentation.Workbench.WorkbenchText.Sha256(File.ReadAllBytes(r)), md, StringComparison.Ordinal);

        Assert.Equal(0, Run("report", t, r, "--out", P("c.html")).ExitCode);
        string html = File.ReadAllText(P("c.html"));
        Assert.Contains("<svg ", html, StringComparison.Ordinal);
        Assert.DoesNotContain("<script", html, StringComparison.OrdinalIgnoreCase);

        Assert.Equal(0, Run("report", t, r, "--out", P("d.txt"), "--format", "csv").ExitCode);
        Assert.Contains("bus,v", File.ReadAllText(P("d.txt")), StringComparison.Ordinal);
    }

    [Fact]
    public void Refusals_exit_1_with_nothing_written_and_a_bad_invocation_exits_2()
    {
        string t = Write("t.json", Template), r = Write("r.json", Result);
        Assert.Equal(0, Run("report", t, r, "--out", P("a.md")).ExitCode);
        byte[] before = File.ReadAllBytes(P("a.md"));
        Assert.Equal(1, Run("report", t, r, "--out", P("a.md")).ExitCode);                       // an existing path is refused
        Assert.Equal(before, File.ReadAllBytes(P("a.md")));

        string hashed = Write("h.json", Template.Replace("\"title\": \"CLI report\"", "\"title\": \"CLI report\", \"resultSha256\": \"" + new string('0', 64) + "\"", StringComparison.Ordinal));
        CliProcess.Result mismatch = Run("report", hashed, r, "--out", P("m.md"));
        Assert.Equal(1, mismatch.ExitCode);
        Assert.Contains("does not match the template", mismatch.StdErr, StringComparison.Ordinal);
        Assert.False(File.Exists(P("m.md")));

        string bad = Write("bad.json", Template.Replace("\"title\": \"CLI report\"", "\"title\": \"x\", \"extra\": 1", StringComparison.Ordinal));
        Assert.Equal(1, Run("report", bad, r, "--out", P("e.md")).ExitCode);
        Assert.False(File.Exists(P("e.md")));
        Assert.Equal(1, Run("report", t, P("missing.json"), "--out", P("f.md")).ExitCode);
        Assert.Equal(1, Run("report", t, r, "--out", P("g.dat")).ExitCode);                       // format not inferable
        Assert.False(File.Exists(P("g.dat")));

        Assert.Equal(2, Run("report").ExitCode);
        Assert.Equal(2, Run("report", t, r).ExitCode);                                           // no --out
        Assert.Equal(2, Run("report", t, "--out", P("x.md")).ExitCode);                          // no result
    }

    [Fact]
    public void The_default_template_for_a_result_is_written_and_can_be_used_again()
    {
        string r = Write("r.json", Result);
        CliProcess.Result made = Run("report", "--template-for", r, "--out", P("auto.json"));
        Assert.True(made.ExitCode == 0, made.ToString());
        using JsonDocument doc = JsonDocument.Parse(File.ReadAllBytes(P("auto.json")));
        Assert.Equal("gridsim-report-template/1", doc.RootElement.GetProperty("schema").GetString());
        Assert.Equal(0, Run("report", P("auto.json"), r, "--out", P("auto.md")).ExitCode);
        Assert.Equal(1, Run("report", "--template-for", r, "--out", P("auto.json")).ExitCode);   // not overwritten
    }

    [Fact]
    public void The_verb_has_its_own_help()
    {
        CliProcess.Result h = Run("report", "--help");
        Assert.Equal(0, h.ExitCode);
        Assert.Contains("report", h.StdOut, StringComparison.Ordinal);
        Assert.Contains("--template-for", h.StdOut, StringComparison.Ordinal);
    }
}
