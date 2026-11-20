using System.Text.Json;
using Xunit;

namespace GridSim.Tests;

/// <summary>RC4 R4-03 verbs run as the built CLI: output, result files, exit codes and usage refusals.</summary>
public sealed class R403CliProcessTests
{
    private static string TempFile(string name)
    {
        string d = Path.Combine(Path.GetTempPath(), "gridsim-r403-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(d);
        return Path.Combine(d, name);
    }

    private static JsonElement Result(string path)
    {
        using JsonDocument doc = JsonDocument.Parse(File.ReadAllText(path));
        return doc.RootElement.Clone();
    }

    [Fact]
    public void Sensitivity_kinds_run_on_case9_and_write_their_tables()
    {
        string json = TempFile("s.json");
        CliProcess.Result r = CliProcess.RunBuilt("sensitivity", "case9", "--kind", "dvdq", "--bus", "5", "--json", json);
        Assert.Equal(0, r.ExitCode);
        Assert.Contains("dV/dQ", r.StdOut, StringComparison.Ordinal);
        JsonElement tables = Result(json).GetProperty("tables");
        Assert.Equal(9, tables.GetProperty("voltage").GetArrayLength());

        Assert.Equal(0, CliProcess.RunBuilt("sensitivity", "case9", "--kind", "tap", "--branch", "0").ExitCode);
        Assert.Equal(0, CliProcess.RunBuilt("sensitivity", "case9", "--kind", "effectiveness", "--bus", "5").ExitCode);
        CliProcess.Result p = CliProcess.RunBuilt("sensitivity", "case9", "--kind", "psdf", "--branch", "3");
        Assert.Equal(0, p.ExitCode);
        Assert.Contains("PSDF", p.StdOut, StringComparison.Ordinal);
        CliProcess.Result o = CliProcess.RunBuilt("sensitivity", "case9", "--kind", "otdf", "--branch", "3", "--bus", "5");
        Assert.Equal(0, o.ExitCode);
        Assert.Contains("OTDF", o.StdOut, StringComparison.Ordinal);
    }

    [Fact]
    public void Sensitivity_refuses_bad_usage_with_exit_2()
    {
        Assert.Equal(2, CliProcess.RunBuilt("sensitivity", "case9", "--bus", "5").ExitCode);                                  // no --kind
        Assert.Equal(2, CliProcess.RunBuilt("sensitivity", "case9", "--kind", "nonsense").ExitCode);
        Assert.Equal(2, CliProcess.RunBuilt("sensitivity", "case9", "--kind", "otdf", "--bus", "5").ExitCode);               // no --branch
        Assert.Equal(2, CliProcess.RunBuilt("sensitivity", "case9", "--kind", "dvdq", "--bus", "99").ExitCode);              // no such bus
        Assert.Equal(2, CliProcess.RunBuilt("sensitivity", "case9", "--kind", "psdf", "--branch", "99").ExitCode);           // no such branch
        Assert.Equal(2, CliProcess.RunBuilt("sensitivity", "case9", "--kind", "psdf", "--branch", "1", "--bus", "5").ExitCode); // --bus not used
        Assert.Equal(2, CliProcess.RunBuilt("qv-curve", "case9", "--bus", "5", "--top", "3").ExitCode);                      // --top is not a qv-curve flag
    }

    [Fact]
    public void Qv_curve_on_case9_writes_the_curve_and_the_margin()
    {
        string json = TempFile("qv.json");
        CliProcess.Result r = CliProcess.RunBuilt("qv-curve", "case9", "--bus", "5", "--vmin", "0.3", "--json", json);
        Assert.Equal(0, r.ExitCode);
        Assert.Contains("reactive margin", r.StdOut, StringComparison.Ordinal);
        JsonElement root = Result(json);
        Assert.Equal("OK", root.GetProperty("summary").GetProperty("status").GetString());
        Assert.True(root.GetProperty("tables").GetProperty("curve").GetArrayLength() > 10);
        Assert.Equal(2, CliProcess.RunBuilt("qv-curve", "case9", "--bus", "1").ExitCode);   // the slack: out of scope
    }

    [Fact]
    public void Dc_and_pv_and_fault_verbs_run_and_write_result_files()
    {
        string dc = TempFile("dc.json");
        CliProcess.Result d = CliProcess.RunBuilt("dc-contingency", "case9", "--json", dc);
        Assert.Contains(d.ExitCode, new[] { 0, 3 });
        Assert.Contains("DC N-1", d.StdOut, StringComparison.Ordinal);
        JsonElement dcRoot = Result(dc);
        Assert.Equal(9, dcRoot.GetProperty("summary").GetProperty("outages").GetInt32());
        Assert.Equal(10, dcRoot.GetProperty("tables").GetProperty("outages").GetArrayLength());   // base + 9

        string pv = TempFile("pv.json");
        CliProcess.Result p = CliProcess.RunBuilt("pv-contingency", "case9", "--branches", "0,1", "--json", pv);
        Assert.Equal(0, p.ExitCode);
        Assert.Equal(2, Result(pv).GetProperty("tables").GetProperty("outages").GetArrayLength());

        string fl = TempFile("fl.json");
        CliProcess.Result f = CliProcess.RunBuilt("fault-level-n1", "case9", "--buses", "5,7", "--objective", "min", "--json", fl);
        Assert.Equal(0, f.ExitCode);
        JsonElement rows = Result(fl).GetProperty("tables").GetProperty("buses");
        Assert.Equal(2, rows.GetArrayLength());
        Assert.Equal(2, CliProcess.RunBuilt("fault-level-n1", "case9", "--objective", "avg").ExitCode);
        Assert.Equal(2, CliProcess.RunBuilt("dc-contingency", "case9", "--post-rating", "Z").ExitCode);
        Assert.Equal(2, CliProcess.RunBuilt("pv-contingency", "case9", "--branches", "0,x").ExitCode);
    }
}
