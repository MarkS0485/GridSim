using System.Security.Cryptography;
using System.Text.Json;
using GridSim.Core.Analysis.DynamicsStudy;
using GridSim.Core.Dynamics.Models;
using GridSim.Core.IO.Psse;
using Xunit;
using static GridSim.Tests.DynamicsStudyTestSupport;

namespace GridSim.Tests;

/// <summary>
/// RC5 WP16 review fix: the <c>mtdc</c> device of a <c>dynamics-study</c> file, run through the command line. Oracle: the
/// same study built through the API (<see cref="DynamicsStudyRunner"/> with an <see cref="MtdcGridDynamicsData"/> written
/// out field by field), compared bit for bit on every trace sample, so every member the file names reaches the model.
/// The case is <see cref="PsseMtdcTests.Case"/>'s three-terminal link (rectifiers at buses 2 and 3, the voltage converter
/// at bus 4) behind a GENROU slack unit.
/// </summary>
public sealed class Rc5Wp16MtdcCliTests : IDisposable
{
    private readonly string _dir = TestTemp.Under("gridsim-wp16-cli-" + Guid.NewGuid().ToString("N"));

    public Rc5Wp16MtdcCliTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    private string F(string name) => Path.Combine(_dir, name);

    private static string Sha(string path) => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))).ToLowerInvariant();

    /// <summary>GENROU at bus 1 (case14-study.dyr's bus 1 machine with H = 10 s).</summary>
    private const string Dyr = "     1 'GENROU' 1   7.4  0.03  0.40  0.05  10.0  0.0  1.80  1.75  0.30  0.65  0.23  0.15  0.09  0.38 /\n";

    private static readonly object[] Terminals =
    {
        new { bus = 2, mode = "power", imaxPu = 2.0, mbaseMva = 500.0, tp = 0.02, capacitanceMicroFarads = 100.0, priority = "active" },
        new { bus = 3, mode = "droop", imaxPu = 2.0, mbaseMva = 500.0, tp = 0.02, droop = 5.0, capacitanceMicroFarads = 100.0,
            mmc = new { submodules = 200, csmFarads = 0.01, larmHenries = 0.05 } },
        new { bus = 4, mode = "dcVoltage", imaxPu = 2.0, mbaseMva = 500.0, tq = 0.02, kv = 1.0 },
    };

    /// <summary>The same device through the API, member for member.</summary>
    private static MtdcGridDynamicsData Api() => new("MT1", new[]
    {
        new MtdcTerminalControl(2, MtdcControlMode.Power, 2.0, 500) { Tp = 0.02, CapacitanceMicroFarads = 100, Priority = VscCurrentPriority.Active },
        new MtdcTerminalControl(3, MtdcControlMode.Droop, 2.0, 500) { Tp = 0.02, DroopGain = 5, CapacitanceMicroFarads = 100, Mmc = new Mmc1Data(200, 0.01, 0.05, 0, 0, 0) },
        new MtdcTerminalControl(4, MtdcControlMode.DcVoltage, 2.0, 500) { Tq = 0.02, Kv = 1.0 },
    })
    {
        BusCapacitances = new[] { new MtdcDcBusCapacitance(3, 50) },
    };

    private void WriteStudy(string name, object device)
    {
        File.WriteAllText(F(name), JsonSerializer.Serialize(new
        {
            schemaVersion = 1,
            raw = new { path = "mt.raw", sha256 = Sha(F("mt.raw")) },
            dyr = new { path = "mt.dyr", sha256 = Sha(F("mt.dyr")) },
            initialisation = new { powerFlow = new { tolerancePu = 1e-10, maxIterations = 100 }, unmodelled = "refuse" },
            integration = new { stepSeconds = 0.005, durationSeconds = 0.5 },
            criteria = new { slipLimitRad = 2.0 * Math.PI, maxAngleSpreadDeg = 180.0 },
            angleReference = new { bus = 1, id = "1" },
            channels = new object[] { new { kind = "busVoltage", bus = 2 }, new { kind = "busVoltage", bus = 4 }, new { kind = "speed", machine = new { bus = 1, id = "1" } } },
            cases = new object[] { new { name = "fault", kind = "events", events = new object[] { new { kind = "busFault", at = 0.1, bus = 3 }, new { kind = "faultClear", at = 0.15, bus = 3 } } } },
            devices = new[] { device },
        }));
    }

    private void Inputs()
    {
        File.WriteAllText(F("mt.raw"), PsseMtdcTests.Case().Replace("\r\n", "\n", StringComparison.Ordinal));
        File.WriteAllText(F("mt.dyr"), Dyr);
    }

    [Fact]
    public void An_mtdc_device_in_a_study_file_runs_the_same_bits_as_the_api()
    {
        Inputs();
        WriteStudy("study.json", new { kind = "mtdc", name = "MT1", busCapacitances = new[] { new { dcBus = 3, microFarads = 50.0 } }, terminals = Terminals });

        CliProcess.Result cli = CliProcess.RunBuilt("dynamics-study", F("study.json"));
        Assert.True(cli.ExitCode == 0, cli.StdErr);
        CliProcess.Result again = CliProcess.RunBuilt("dynamics-study", F("study.json"));
        Assert.Equal(cli.StdOut, again.StdOut);

        using JsonDocument doc = JsonDocument.Parse(cli.StdOut);
        Assert.Equal("STABLE", doc.RootElement.GetProperty("status").GetString());
        JsonElement run = doc.RootElement.GetProperty("cases")[0].GetProperty("run");
        Assert.Equal(0, run.GetProperty("converterIterationFailures").GetInt32());

        var cases = new[] { new DynamicsCaseRequest("fault", DynamicsCaseKind.Events, new[]
        {
            new DynamicsEventRequest(DynamicsEventKind.BusFault, 0.1, Bus: 3), new DynamicsEventRequest(DynamicsEventKind.FaultClear, 0.15, Bus: 3),
        }) };
        var channels = new[]
        {
            new DynamicsChannelRequest(DynamicsChannelKind.BusVoltage, Bus: 2), new DynamicsChannelRequest(DynamicsChannelKind.BusVoltage, Bus: 4),
            new DynamicsChannelRequest(DynamicsChannelKind.Speed, M(1)),
        };
        PsseParseResult raw = PsseRawParser.ParseWithDiagnostics(File.ReadAllText(F("mt.raw")), "mt");
        DynamicsStudyRequest request = Request(raw, Dyr, cases, channels, new DynamicsInitialisation(1e-10, 100, DynamicsUnmodelledPolicy.Refuse),
            step: 0.005, duration: 0.5, dyrName: "mt.dyr") with { Devices = new RmsNetworkDeviceData[] { Api() } };
        DynamicsRunEvidence api = DynamicsStudyRunner.Run(request).Cases[0].Run!;

        JsonElement traces = run.GetProperty("channels");
        Assert.Equal(api.Channels.Count, traces.GetArrayLength());
        Assert.Equal(api.Samples, run.GetProperty("samples").GetInt32());
        for (int c = 0; c < api.Channels.Count; c++)
        {
            JsonElement values = traces[c].GetProperty("values");
            Assert.Equal(api.Channels[c].Values.Count, values.GetArrayLength());
            for (int k = 0; k < api.Channels[c].Values.Count; k++)
                Assert.True(BitConverter.DoubleToInt64Bits(api.Channels[c].Values[k]) == BitConverter.DoubleToInt64Bits(values[k].GetDouble()),
                    $"{api.Channels[c].Label} sample {k}: API {api.Channels[c].Values[k]:R}, CLI {values[k].GetDouble():R}");
        }

        // The data reach the model: with the droop converter's AC bus faulted its DC power, and so its DC voltage, moves, so
        // without the droop (a power converter instead) the traces differ. (Its DC bus is joined only to the held bus, so a
        // disturbance elsewhere would leave its voltage, and the droop, untouched.)
        var noDroop = (object[])Terminals.Clone();
        noDroop[1] = new { bus = 3, mode = "power", imaxPu = 2.0, mbaseMva = 500.0, tp = 0.02, capacitanceMicroFarads = 100.0,
            mmc = new { submodules = 200, csmFarads = 0.01, larmHenries = 0.05 } };
        WriteStudy("power.json", new { kind = "mtdc", name = "MT1", busCapacitances = new[] { new { dcBus = 3, microFarads = 50.0 } }, terminals = noDroop });
        CliProcess.Result power = CliProcess.RunBuilt("dynamics-study", F("power.json"));
        Assert.True(power.ExitCode == 0, power.StdErr);
        using JsonDocument pdoc = JsonDocument.Parse(power.StdOut);
        Assert.NotEqual(run.GetProperty("channels").GetRawText(), pdoc.RootElement.GetProperty("cases")[0].GetProperty("run").GetProperty("channels").GetRawText());
    }

    [Theory]
    [InlineData("mode", "devices[0].terminals[0].mode must be \"power\", \"dcVoltage\" or \"droop\" (got \"current\").")]
    [InlineData("field", "devices[0].terminals[0]: unknown field 'gain'.")]
    [InlineData("mmc", "devices[0].terminals[0].mmc: missing required field 'csmFarads'.")]
    [InlineData("name", "the model has no multi-terminal DC link named 'NOPE'")]
    public void A_bad_mtdc_device_is_refused_naming_the_member(string what, string expected)
    {
        Inputs();
        object t0 = what switch
        {
            "mode" => new { bus = 2, mode = "current", imaxPu = 2.0, mbaseMva = 500.0 },
            "field" => new { bus = 2, mode = "power", imaxPu = 2.0, mbaseMva = 500.0, gain = 1.0 },
            "mmc" => new { bus = 2, mode = "power", imaxPu = 2.0, mbaseMva = 500.0, mmc = new { submodules = 200, larmHenries = 0.05 } },
            _ => Terminals[0],
        };
        object[] terminals = { t0, Terminals[1], Terminals[2] };
        WriteStudy("bad.json", new { kind = "mtdc", name = what == "name" ? "NOPE" : "MT1", terminals });
        CliProcess.Result r = CliProcess.RunBuilt("dynamics-study", F("bad.json"));
        // A malformed file is refused before any load (stderr); a link the case does not have stops the row at its
        // initialisation (exit 3, the reason in the result).
        Assert.Equal(what == "name" ? 3 : 1, r.ExitCode);
        Assert.Contains(expected, what == "name" ? r.StdOut : r.StdErr, StringComparison.Ordinal);
    }
}
