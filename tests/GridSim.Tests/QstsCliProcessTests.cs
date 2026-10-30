using System.Text.Json;
using GridSim.Core.Model;
using GridSim.Core.PowerFlow;
using GridSim.Core.Qsts;
using Xunit;

namespace GridSim.Tests;

/// <summary>
/// The <c>qsts</c> verb run as the built CLI on the Baran-Wu 33-bus feeder: the report, a result file whose digest is
/// the library run's (every number bit for bit), and the exit codes (1 a refused study, 2 usage, 3 a step that does not
/// converge, with no result file).
/// </summary>
public sealed class QstsCliProcessTests
{
    private static string TempDir()
    {
        string d = Path.Combine(Path.GetTempPath(), "gridsim-qsts-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(d);
        return d;
    }

    internal const string Study33 = """
        {
          "format": "gridsim-qsts/1",
          "stepSeconds": 1800, "steps": 48,
          "profiles": {
            "load": { "stepSeconds": 3600, "interpolation": "linear", "wrap": true,
                      "values": [0.55,0.5,0.48,0.47,0.48,0.52,0.62,0.75,0.82,0.85,0.86,0.86,0.85,0.84,0.84,0.86,0.92,1.0,1.0,0.95,0.88,0.78,0.68,0.6] },
            "solar": { "stepSeconds": 3600, "wrap": true,
                       "values": [0,0,0,0,0,0,0.05,0.2,0.4,0.6,0.75,0.85,0.9,0.85,0.75,0.6,0.4,0.2,0.05,0,0,0,0,0] }
          },
          "loads": [ { "buses": "all", "profile": "load" } ],
          "pv": [ { "name": "PV18", "bus": 18, "ratedMw": 1.5, "ratedMva": 1.65, "profile": "solar",
                    "control": { "function": "volt-var", "voltVar": "1547-B", "responseSeconds": 5 } } ],
          "storage": [ { "name": "B33", "bus": 33, "ratedMw": 0.3, "capacityMwh": 1.2, "initialSoc": 0.5, "socMin": 0.1, "socMax": 0.9,
                         "chargeEfficiency": 0.95, "dischargeEfficiency": 0.95, "dispatch": { "mode": "self-consumption" } } ],
          "capacitors": [ { "name": "C30", "bus": 30, "stepMvar": 0.3, "maxSteps": 3, "control": "voltage",
                            "onPu": 0.93, "offPu": 1.0, "delaySeconds": 60 } ],
          "limits": { "vminPu": 0.94, "vmaxPu": 1.05 }
        }
        """;

    [Fact]
    public void The_33_bus_day_reports_and_its_result_file_carries_the_library_run_bit_for_bit()
    {
        string dir = TempDir();
        try
        {
            string study = Path.Combine(dir, "study.json"), json = Path.Combine(dir, "r.json");
            File.WriteAllText(study, Study33);
            CliProcess.Result r = CliProcess.RunBuilt("qsts", "case33bw.m", "--study", study, "--json", json);
            Assert.True(r.ExitCode == 0, r.StdErr + r.StdOut);
            Assert.Contains("QSTS - ", r.StdOut, StringComparison.Ordinal);
            Assert.Contains("48 step(s) of 1800 s", r.StdOut, StringComparison.Ordinal);
            Assert.Contains("bank C30", r.StdOut, StringComparison.Ordinal);

            GridModel m = S10TestSupport.LoadMatpower("dist/case33bw.m").Model;
            QstsStudy s = QstsFile.Parse(Study33, "study.json", dir, m);
            QstsResult lib = new QstsEngine(new BackwardForwardSweep(), new PowerFlowOptions()).Run(m, s);
            using JsonDocument doc = JsonDocument.Parse(File.ReadAllText(json));
            JsonElement summary = doc.RootElement.GetProperty("summary");
            Assert.Equal(lib.Digest(), summary.GetProperty("digest").GetString());
            Assert.Equal(lib.TotalCapacitorOperations, summary.GetProperty("capacitor_operations").GetInt64());
            Assert.Equal(BitConverter.DoubleToInt64Bits(lib.Energy.LossMwh), BitConverter.DoubleToInt64Bits(summary.GetProperty("loss_mwh").GetDouble()));
            Assert.Equal(48, doc.RootElement.GetProperty("tables").GetProperty("steps").GetArrayLength());
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public void A_refused_study_exits_1_and_lists_its_problems()
    {
        string dir = TempDir();
        try
        {
            string study = Path.Combine(dir, "bad.json");
            File.WriteAllText(study, """{ "format": "gridsim-qsts/1", "stepSeconds": 60, "steps": 2, "loads": [ { "buses": [999], "profile": "x" } ] }""");
            CliProcess.Result r = CliProcess.RunBuilt("qsts", "case33bw.m", "--study", study);
            Assert.Equal(1, r.ExitCode);
            Assert.Contains("bus 999", r.StdErr, StringComparison.Ordinal);
            Assert.Contains("profile 'x'", r.StdErr, StringComparison.Ordinal);
            File.WriteAllText(study, """{ "format": "gridsim-qsts/1", "stepSeconds": 60, "steps": 2, "colour": "red" }""");
            r = CliProcess.RunBuilt("qsts", "case33bw.m", "--study", study);
            Assert.Equal(1, r.ExitCode);
            Assert.Contains("unknown key 'colour'", r.StdErr, StringComparison.Ordinal);
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public void Usage_errors_exit_2()
    {
        Assert.Equal(2, CliProcess.RunBuilt("qsts", "case33bw.m").ExitCode);
        CliProcess.Result r = CliProcess.RunBuilt("qsts", "case33bw.m", "--study", "x.json", "--post-rating", "B");
        Assert.Equal(2, r.ExitCode);
        Assert.Contains("qsts does not take --post-rating", r.StdErr, StringComparison.Ordinal);
    }

    [Fact]
    public void A_step_that_does_not_converge_exits_3_with_no_result_file()
    {
        string dir = TempDir();
        try
        {
            string study = Path.Combine(dir, "s.json"), json = Path.Combine(dir, "r.json");
            File.WriteAllText(study, """{ "format": "gridsim-qsts/1", "stepSeconds": 60, "steps": 2, "profiles": { "p": { "stepSeconds": 60, "values": [1, 30] } }, "loads": [ { "buses": "all", "profile": "p" } ] }""");
            CliProcess.Result r = CliProcess.RunBuilt("qsts", "case33bw.m", "--study", study, "--json", json);
            Assert.Equal(3, r.ExitCode);
            Assert.Contains("did not converge", r.StdErr, StringComparison.Ordinal);
            Assert.False(File.Exists(json));
        }
        finally { Directory.Delete(dir, true); }
    }
}
