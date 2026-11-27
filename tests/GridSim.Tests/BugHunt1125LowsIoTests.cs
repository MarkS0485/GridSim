using System.Text;
using GridSim.Core.Dynamics;
using GridSim.Core.IO;
using GridSim.Core.IO.Comtrade;
using GridSim.Core.IO.Dgs;
using GridSim.Core.IO.Matpower;
using GridSim.Core.IO.Psse;
using GridSim.Core.IO.Telemetry;
using GridSim.Core.Model;
using Xunit;

namespace GridSim.Tests;

/// <summary>Bug hunt batch 5, IO lows (IO-13 to IO-26): one regression test per fix.</summary>
public sealed class BugHunt1125LowsIoTests : IDisposable
{
    private readonly string _dir = TestTemp.Under("bh1125-lows-io-" + Guid.NewGuid().ToString("N"));

    public BugHunt1125LowsIoTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    [Fact]
    public void Io13_an_ascii_data_file_ending_in_the_dos_end_of_file_mark_is_read()
    {
        const string cfg = "STN,DEV,1991\n1,1A,0D\n1,IA,A,,A,1,0,0,-32768,32767\n50\n1\n1000,2\n01/01/2020,00:00:00.000000\n01/01/2020,00:00:00.000000\nASCII\n";
        byte[] dat = Encoding.ASCII.GetBytes("1,0,5\r\n2,1000,6\r\n\u001A");
        ComtradeRecording r = ComtradeReader.Parse(cfg, dat);
        Assert.Equal(2, r.Analog[0].Count);
        Assert.Equal(6.0, r.Analog[0][1]);
    }

    [Fact]
    public void Io15_a_large_file_that_is_not_dgs_gives_a_bounded_message()
    {
        var sb = new StringBuilder();
        for (int i = 0; i < 20_000; i++) sb.Append("not dgs at all, line ").Append(i).Append('\n');
        var ex = Assert.Throws<DgsFormatException>(() => DgsTableReader.Read(sb.ToString()));
        Assert.True(ex.Problems.Count <= 1_010, ex.Problems.Count.ToString(System.Globalization.CultureInfo.InvariantCulture));
        Assert.Contains(ex.Problems, x => x.StartsWith("reading stopped at line", StringComparison.Ordinal));
        Assert.Contains(" more.", ex.Message, StringComparison.Ordinal);
        Assert.True(ex.Message.Length < 20_000, ex.Message.Length.ToString(System.Globalization.CultureInfo.InvariantCulture));
    }

    private const string SolveDoc = """
        { "schema": "gridsim-solve-export/1",
          "model": { "buses": [ { "id": 1, "type": "Slack" } ], "generators": [ { "bus": 1, "pg": 10 } ], "branches": [] },
          "solve": { "converged": false, "Converged": true, "iterations": 3, "maxMismatch": 1e-10 },
          "result": { "buses": [], "lineFlows": [] } }
        """;

    [Fact]
    public void Io16_solve_import_refuses_a_repeated_key_and_reports_bad_json_as_invalid_data()
    {
        Assert.Throws<InvalidDataException>(() => SolveImport.Read(SolveDoc));
        Assert.Throws<InvalidDataException>(() => SolveImport.Read("{ \"schema\": "));
        _ = SolveImport.Read(SolveDoc.Replace("\"converged\": false, ", "", StringComparison.Ordinal));
    }

    [Fact]
    public void Io17_a_link_inside_a_case_folder_that_leads_outside_it_is_not_inside()
    {
        string caseDir = Path.Combine(_dir, "case");
        string outside = Path.Combine(_dir, "outside");
        Directory.CreateDirectory(caseDir);
        Directory.CreateDirectory(outside);
        File.WriteAllText(Path.Combine(outside, "net.json"), "{}");
        string link = Path.Combine(caseDir, "sub");
        try { Directory.CreateSymbolicLink(link, outside); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or PlatformNotSupportedException) { return; }   // no link rights here
        Assert.False(GridSim.Core.IO.Json.V2CaseLoader.IsInside(caseDir, Path.Combine(link, "net.json")));
        Assert.True(GridSim.Core.IO.Json.V2CaseLoader.IsInside(caseDir, Path.Combine(caseDir, "plain.json")));
    }

    [Fact]
    public void Io18_a_matpower_file_with_cr_only_line_endings_keeps_everything_after_a_comment()
    {
        const string lf = "function mpc = cr2\nmpc.baseMVA = 100;\nmpc.bus = [\n1 3 0 0 0 0 1 1 0 132 1 1.1 0.9;\n2 1 50 10 0 0 1 1 0 132 1 1.1 0.9;\n];\n%% generators\nmpc.gen = [\n1 0 0 100 -100 1 100 1 200 0;\n];\nmpc.branch = [\n1 2 0.01 0.1 0 0 0 0 0 0 1 -360 360;\n];\n";
        GridModel a = MatpowerParser.Parse(lf, "cr2");
        GridModel b = MatpowerParser.Parse(lf.Replace('\n', '\r'), "cr2");
        Assert.Single(b.Generators);
        Assert.Single(b.Branches);
        Assert.Equal(a.Buses.Count, b.Buses.Count);
    }

    [Fact]
    public void Io20_the_raw_writer_refuses_a_case_name_that_reads_as_a_heading()
    {
        GridModel m = MatpowerParser.Parse("function mpc = x\nmpc.baseMVA = 100;\nmpc.bus = [\n1 3 0 0 0 0 1 1 0 132 1 1.1 0.9;\n2 1 50 10 0 0 1 1 0 132 1 1.1 0.9;\n];\nmpc.gen = [\n1 0 0 100 -100 1 100 1 200 0;\n];\nmpc.branch = [\n1 2 0.01 0.1 0 0 0 0 0 0 1 -360 360;\n];\n", "x");
        _ = PsseRawWriter.Write(m.CopyWith(name: "plain"));
        var ex = Assert.Throws<InvalidDataException>(() => PsseRawWriter.Write(m.CopyWith(name: "@!title")));
        Assert.Contains("heading line", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Io21_a_frequency_export_with_a_non_finite_value_is_written()
    {
        var ev = new[] { new FrequencyEvent(1.0, -1000) };
        FrequencyResponse resp = FrequencyDynamics.Simulate(120_000, 30_000, ev, new DynamicsConfig());
        string json = FrequencyExport.ToJson(120_000, 30_000, ev, new DynamicsConfig(), resp with { NadirHz = double.NaN },
            new FrequencyExportMeta("t", "deadbeef", "2026-07-20T00:00:00Z", 1.0, "host", GridSim.Core.Diagnostics.ProvenanceTag.Synthetic));
        Assert.Contains("NaN", json, StringComparison.Ordinal);
    }

    [Fact]
    public void Io22_an_atomic_write_replaces_the_file_and_leaves_no_temporary_file()
    {
        string p = Path.Combine(_dir, "x.json");
        File.WriteAllText(p, "old");
        AtomicText.Write(p, "new é");
        Assert.Equal("new é", File.ReadAllText(p));
        Assert.Equal(Encoding.UTF8.GetBytes("new é"), File.ReadAllBytes(p));   // no byte-order mark, as File.WriteAllText
        Assert.Single(Directory.EnumerateFiles(_dir));
    }

    [Fact]
    public void Io25_dnp3_refuses_an_address_outside_16_bits()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => Dnp3Frame.ToBytes(new Dnp3DataLinkHeader(0xC4, 65536, 1)));
        Assert.Throws<ArgumentOutOfRangeException>(() => Dnp3Frame.ToBytes(new Dnp3DataLinkHeader(0xC4, 1, -1)));
        Assert.Equal(10, Dnp3Frame.ToBytes(new Dnp3DataLinkHeader(0xC4, 65535, 0)).Length);
    }
}
