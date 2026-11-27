using System.Buffers.Binary;
using System.Text;
using GridSim.Core.Diagnostics;
using GridSim.Core.Dynamics.Emt;
using GridSim.Core.IO.Comtrade;
using GridSim.Core.IO.Json;
using GridSim.Core.Model;
using Xunit;

namespace GridSim.Tests;

/// <summary>
/// Review B7 (26 November) fixes: R7-01 (the legacy COMPlus_ prefix of the diagnostic-ports setting), R7-03 (the pi
/// fallback's report: a count, bus ids and branch names, a capped list on stderr, the full list in the output folder) and
/// R7-09 (the BINARY32 missing-data code from revision 1999 only, as the BINARY one).
/// </summary>
public sealed class ReviewB7FixesTests : IDisposable
{
    private readonly string _dir = JournalTestSupport.NewDir("review-b7");

    public void Dispose() => JournalTestSupport.Delete(_dir);

    private static string? Check(string name, string value) =>
        TelemetryGuard.CheckEnvironment(new Dictionary<string, string?>(StringComparer.Ordinal) { [name] = value });

    [Theory]
    [InlineData("DOTNET_DiagnosticPorts")]
    [InlineData("COMPlus_DiagnosticPorts")]
    [InlineData("complus_diagnosticports")]
    [InlineData("COMPLUS_DIAGNOSTICPORTS")]
    public void R7_01_a_non_empty_diagnostic_port_is_refused_under_either_prefix(string name)
    {
        string? refused = Check(name, "/tmp/port,suspend");
        Assert.NotNull(refused);
        Assert.Contains(name, refused, StringComparison.Ordinal);
        Assert.Contains("diagnostic port", refused, StringComparison.Ordinal);
        Assert.Null(Check(name, ""));
        Assert.Null(Check(name, "   "));
    }

    [Fact]
    public void R7_01_the_legacy_name_is_the_runtime_s_COMPlus_form_of_the_same_setting()
    {
        Assert.Equal("DOTNET_DiagnosticPorts", TelemetryGuard.DiagnosticPortsVariable);
        Assert.Equal("COMPlus_DiagnosticPorts", TelemetryGuard.LegacyDiagnosticPortsVariable);
        // The profiler settings fall back to CORECLR_ (already listed), not COMPlus_: a COMPlus_ profiler name with no
        // switch on is not a profiler the runtime loads, and is left alone as the DOTNET_ and CORECLR_ names are.
        Assert.Null(Check("COMPlus_ENABLE_PROFILING", "0"));
    }

    // A chain of buses joined by lines far shorter than a 20 us step (tau about 0.45 us each).
    private static GridModel ShortChain(int lines)
    {
        var buses = new List<Bus> { new(1, BusType.Slack, BaseKv: 132) };
        var branches = new List<Branch>();
        for (int i = 2; i <= lines + 1; i++)
        {
            buses.Add(new Bus(i, BusType.Pq, Pd: 1, BaseKv: 132));
            branches.Add(new Branch(i - 1, i, 0.0001, 0.001, B: 0.002) { Name = "stub-" + i.ToString(System.Globalization.CultureInfo.InvariantCulture) });
        }
        return new GridModel { Name = "chain", BaseMva = 100, Buses = buses, Generators = new[] { new Generator(1, 10) }, Branches = branches };
    }

    [Fact]
    public void R7_03_pi_converted_lines_carry_their_bus_ids_and_branch_name()
    {
        GridModel m = ShortChain(3);
        EmtNetwork net = EmtNetwork.FromGridModel(m, new EmtBuildOptions(
            new[] { new EmtSourceSpec(1, Rs: 0, AmplitudePu: 1.0, FrequencyHz: 0.0) }, IncludeLoads: true, UseTravellingWaveLines: true));
        var engine = new EmtEngine(net, 20e-6);
        Assert.Equal(3, engine.PiConvertedLines.Count);
        PiConvertedLine first = engine.PiConvertedLines[0];
        Assert.Equal("branch #0 (1->2, 'stub-2')", first.Label);
        Assert.Equal(first.Label, first.Describe());
        // A network not built from a case has no label; the element id and nodes name the line.
        Assert.Equal("travelling-wave line 7 (nodes 0->1)", new PiConvertedLine(7, 0, 1, 1e-7, 2e-5).Describe());
    }

    [Fact]
    public void R7_03_emt_prints_the_count_first_caps_the_list_and_records_every_line_in_the_output_folder()
    {
        string c = Path.Combine(_dir, "chain");
        Directory.CreateDirectory(c);
        JsonCaseWriter.WriteDirectory(ShortChain(23), c);
        string outDir = Path.Combine(_dir, "out");
        CliProcess.Result r = JournalTestSupport.Run(_dir, "emt", c, "--lines", "--out", outDir);
        Assert.True(r.ExitCode == 0, r.ToString());

        string[] notes = r.StdErr.Split('\n').Select(l => l.TrimEnd('\r')).Where(l => l.StartsWith("emt: note:", StringComparison.Ordinal)).ToArray();
        Assert.StartsWith("emt: note: 23 travelling-wave line(s) shorter than the step", notes[0], StringComparison.Ordinal);
        Assert.Equal(20, notes.Count(l => l.Contains("it is modelled as a pi branch of the same line.", StringComparison.Ordinal)));
        Assert.Contains("emt: note:   branch #0 (1->2, 'stub-2'): travel time", r.StdErr, StringComparison.Ordinal);
        Assert.Contains("emt: note:   and 3 more.", r.StdErr, StringComparison.Ordinal);

        string[] csv = File.ReadAllLines(Path.Combine(outDir, "pi-converted.csv")).Where(l => !l.StartsWith('#')).ToArray();
        Assert.Equal("element_id,from_bus,to_bus,branch,tau_seconds,step_seconds", csv[0]);
        Assert.Equal(24, csv.Length);
        Assert.StartsWith("0,1,2,", csv[1], StringComparison.Ordinal);
        Assert.Contains("travelling-wave lines modelled as pi branches (shorter than the step): 23",
            File.ReadAllText(Path.Combine(outDir, "summary.md")), StringComparison.Ordinal);
    }

    [Fact]
    public void R7_03_a_run_with_no_short_line_writes_no_pi_list()
    {
        string c = Path.Combine(_dir, "nine");
        Directory.CreateDirectory(c);
        JsonCaseWriter.WriteDirectory(GridSim.Core.IO.IeeeCases.Case30(), c);
        string outDir = Path.Combine(_dir, "out30");
        CliProcess.Result r = JournalTestSupport.Run(_dir, "emt", c, "--lines", "--out", outDir, "--dt-us", "10");
        Assert.True(r.ExitCode == 0, r.ToString());
        Assert.False(File.Exists(Path.Combine(outDir, "pi-converted.csv")));
        Assert.DoesNotContain("pi branches", File.ReadAllText(Path.Combine(outDir, "summary.md")), StringComparison.Ordinal);
    }

    // R8-01 (review B8): the time axis and traces of cosim and emt-partition go through Wp27CliSupport.Series, which writes a
    // NaN or infinite sample as null; they used WriteNumberValue, which throws on one, so a diverged run ended as a crash log.
    [Fact]
    public void R8_01_a_nan_series_is_written_with_nulls_not_a_crash()
    {
        string target = Path.Combine(_dir, "r801", "series.json");
        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
        double[] times = { 0.0, 1e-5, 2e-5, 3e-5 };
        double[] trace = { 1.0, double.NaN, double.PositiveInfinity, double.NegativeInfinity };
        GridSim.Cli.Wp27CliSupport.WriteJson(target, w =>
        {
            GridSim.Cli.Wp27CliSupport.Series(w, "timeS", times);
            w.WriteStartObject("traces");
            GridSim.Cli.Wp27CliSupport.Series(w, "v1", trace);
            w.WriteEndObject();
        });
        using System.Text.Json.JsonDocument doc = System.Text.Json.JsonDocument.Parse(File.ReadAllText(target));
        System.Text.Json.JsonElement[] v = doc.RootElement.GetProperty("traces").GetProperty("v1").EnumerateArray().ToArray();
        Assert.Equal(4, v.Length);
        Assert.Equal(1.0, v[0].GetDouble());
        Assert.All(v.Skip(1), e => Assert.Equal(System.Text.Json.JsonValueKind.Null, e.ValueKind));
        Assert.Equal(times, doc.RootElement.GetProperty("timeS").EnumerateArray().Select(e => e.GetDouble()));
    }

    [Fact]
    public void R8_01_cosim_and_emt_partition_write_no_raw_series_value()
    {
        foreach (string file in new[] { "CoSimCommand.cs", "EmtPartitionCommand.cs" })
        {
            string text = File.ReadAllText(Path.Combine(CliProcess.Root(), "src", "GridSim.Cli", file));
            Assert.DoesNotContain("WriteNumberValue(", text, StringComparison.Ordinal);
            Assert.Contains("Wp27CliSupport.Series(w, \"timeS\"", text, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void R7_09_the_binary32_missing_data_code_is_data_in_a_1991_file()
    {
        const string tail = "50\n1\n1000,2\n01/01/2020,00:00:00.000000\n01/01/2020,00:00:00.000000\nBINARY32\n";
        const string cfg1991 = "STN,DEV,1991\n1,1A,0D\n1,IA,A,,A,1,0,0,-2147483648,2147483647\n" + tail;
        const string cfg2013 = "STN,DEV,2013\n1,1A,0D\n1,IA,A,,A,1,0,0,-2147483648,2147483647,1,1,P\n" + tail + "1\n0,0\n0,0\n";
        byte[] dat = new byte[2 * 12];
        for (int k = 0; k < 2; k++)
        {
            BinaryPrimitives.WriteInt32LittleEndian(dat.AsSpan(12 * k), k + 1);
            BinaryPrimitives.WriteInt32LittleEndian(dat.AsSpan(12 * k + 4), 1000 * k);
            BinaryPrimitives.WriteInt32LittleEndian(dat.AsSpan(12 * k + 8), k == 0 ? 5 : int.MinValue);
        }
        ComtradeRecording old = ComtradeReader.Parse(cfg1991, dat);
        Assert.Equal(int.MinValue, old.Analog[0][1]);
        Assert.Equal(0, old.MissingValues);
        ComtradeRecording newer = ComtradeReader.Parse(cfg2013, dat);
        Assert.True(double.IsNaN(newer.Analog[0][1]));
        Assert.Equal(1, newer.MissingValues);
    }
}
