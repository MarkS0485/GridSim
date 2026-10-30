using System.Text.Json;
using GridSim.Core.Model;
using GridSim.Core.PowerFlow;
using GridSim.Core.Qsts;
using Xunit;

namespace GridSim.Tests;

/// <summary>
/// The <c>hosting</c> verb run as the built CLI on the Baran-Wu 33-bus feeder: the report, the result file carrying the
/// library's capacities bit for bit (snapshot and QSTS methods), and the exit codes (1 refused, 2 usage).
/// </summary>
public sealed class HostingCliProcessTests
{
    private static string TempDir()
    {
        string d = Path.Combine(Path.GetTempPath(), "gridsim-hosting-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(d);
        return d;
    }

    private static long Bits(double v) => BitConverter.DoubleToInt64Bits(v);

    // The report's constraint names ("reverse-power") as the enum's ("ReversePower").
    private static string Pascal(string name) => string.Concat(name.Split('-').Select(p => char.ToUpperInvariant(p[0]) + p[1..]));

    [Fact]
    public void Snapshot_hosting_on_the_33_bus_feeder_matches_the_library_bit_for_bit()
    {
        string dir = TempDir();
        try
        {
            string json = Path.Combine(dir, "h.json");
            CliProcess.Result r = CliProcess.RunBuilt("hosting", "case33bw.m", "--buses", "6,18,33", "--max-mw", "10", "--tolerance-mw", "0.01",
                "--threads", "2", "--json", json);
            Assert.True(r.ExitCode == 0, r.StdErr + r.StdOut);
            Assert.Contains("Hosting capacity (PV, snapshot method)", r.StdOut, StringComparison.Ordinal);

            GridModel m = S10TestSupport.LoadMatpower("dist/case33bw.m").Model;
            var study = new HostingStudy
            {
                Candidates = new[] { 6, 18, 33 }, MaxMw = 10, ToleranceMw = 0.01, Base = new QstsStudy { StepSeconds = 1, Steps = 1 },
                ReverseFlowBranch = m.Branches.FirstOrDefault(b => b.InService && b.Name is not null && m.Buses.Any(x => x.Id == b.From && x.Type == BusType.Slack))?.Name,
            };
            HostingResult lib = HostingCapacity.Run(m, study, () => new BackwardForwardSweep(), new PowerFlowOptions());
            using JsonDocument doc = JsonDocument.Parse(File.ReadAllText(json));
            JsonElement rows = doc.RootElement.GetProperty("tables").GetProperty("locations");
            Assert.Equal(3, rows.GetArrayLength());
            for (int i = 0; i < 3; i++)
            {
                Assert.Equal(lib.Locations[i].Bus, rows[i].GetProperty("bus_id").GetInt32());
                Assert.Equal(Bits(lib.Locations[i].HostingMw), Bits(rows[i].GetProperty("hosting_mw").GetDouble()));
                Assert.Equal(lib.Locations[i].Primary.ToString(), Pascal(rows[i].GetProperty("limiting").GetString()!));
            }
            // The end of the feeder hosts less than near the source.
            Assert.True(lib.Locations[2].HostingMw < lib.Locations[0].HostingMw);
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public void Qsts_hosting_runs_the_study_file_and_matches_the_library()
    {
        string dir = TempDir();
        try
        {
            string study = Path.Combine(dir, "s.json"), json = Path.Combine(dir, "h.json");
            File.WriteAllText(study, QstsCliProcessTests.Study33);
            CliProcess.Result r = CliProcess.RunBuilt("hosting", "case33bw.m", "--method", "qsts", "--study", study, "--resource-profile", "solar",
                "--buses", "18", "--max-mw", "8", "--tolerance-mw", "0.05", "--json", json);
            Assert.True(r.ExitCode == 0, r.StdErr + r.StdOut);
            GridModel m = S10TestSupport.LoadMatpower("dist/case33bw.m").Model;
            var hs = new HostingStudy
            {
                Method = HostingMethod.Qsts, Candidates = new[] { 18 }, MaxMw = 8, ToleranceMw = 0.05, ResourceProfile = "solar",
                Base = QstsFile.Parse(QstsCliProcessTests.Study33, "s.json", dir, m),
                ReverseFlowBranch = m.Branches.FirstOrDefault(b => b.InService && b.Name is not null && m.Buses.Any(x => x.Id == b.From && x.Type == BusType.Slack))?.Name,
            };
            HostingResult lib = HostingCapacity.Run(m, hs, () => new BackwardForwardSweep(), new PowerFlowOptions());
            using JsonDocument doc = JsonDocument.Parse(File.ReadAllText(json));
            JsonElement row = doc.RootElement.GetProperty("tables").GetProperty("locations")[0];
            Assert.Equal(Bits(lib.Locations[0].HostingMw), Bits(row.GetProperty("hosting_mw").GetDouble()));
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public void Refusals_and_usage_errors()
    {
        CliProcess.Result r = CliProcess.RunBuilt("hosting", "case33bw.m", "--buses", "999");
        Assert.Equal(1, r.ExitCode);
        Assert.Contains("candidate bus 999", r.StdErr, StringComparison.Ordinal);
        Assert.Equal(2, CliProcess.RunBuilt("hosting", "case33bw.m", "--method", "qsts").ExitCode);
        Assert.Equal(2, CliProcess.RunBuilt("hosting", "case33bw.m", "--method", "fast").ExitCode);
        Assert.Equal(2, CliProcess.RunBuilt("hosting", "case33bw.m", "--study-file", "x").ExitCode);
    }
}
