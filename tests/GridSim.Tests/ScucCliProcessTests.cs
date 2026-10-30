using System.Text.Json;
using GridSim.Core.Analysis;
using Xunit;

namespace GridSim.Tests;

/// <summary>
/// The <c>scuc</c> verb (security-constrained unit commitment, <see cref="UnitCommitment"/>), run as the built CLI: its
/// report, its result file carrying the library's numbers bit for bit with a data file, the exit codes (1 a malformed data
/// file, 2 a foreign or bad flag, 3 no proven optimum, with no result file when there is no schedule).
/// </summary>
public sealed class ScucCliProcessTests
{
    private static string TempDir()
    {
        string d = Path.Combine(Path.GetTempPath(), "gridsim-scuc-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(d);
        return d;
    }

    // case9 over four periods: demand 60 %, 100 %, 140 % and 80 % of the case's 315 MW, 50 MW of reserve, every unit off
    // at the start with its gencost start-up cost, unit 3 with a two-period minimum up time.
    private const string Case9Data = """
        { "format": "gridsim-uc/1", "periods": 4, "demandScale": [0.6, 1.0, 1.4, 0.8], "reserveMw": [50, 50, 50, 50],
          "units": [ { "unit": 0, "initiallyOn": false }, { "unit": 1, "initiallyOn": false },
                     { "unit": 2, "initiallyOn": false, "minUp": 2 } ] }
        """;

    [Fact]
    public void Case9_one_period_reports_an_optimal_commitment()
    {
        CliProcess.Result r = CliProcess.RunBuilt("scuc", "case9");
        Assert.Equal(0, r.ExitCode);
        Assert.Contains("SCUC (unit commitment, branch-and-bound)", r.StdOut, StringComparison.Ordinal);
        Assert.Contains("optimal: total cost", r.StdOut, StringComparison.Ordinal);
        Assert.Contains("note: unit", r.StdOut, StringComparison.Ordinal);   // the quadratic gencost cut into chords
    }

    [Fact]
    public void The_result_file_carries_the_library_numbers_bit_for_bit()
    {
        string dir = TempDir();
        try
        {
            string data = Path.Combine(dir, "uc.json"), json = Path.Combine(dir, "r.json");
            File.WriteAllText(data, Case9Data);
            CliProcess.Result r = CliProcess.RunBuilt("scuc", "case9", "--uc-data", data, "--json", json);
            Assert.Equal(0, r.ExitCode);

            var model = S10TestSupport.LoadMatpower("lib/case9.m").Model;
            UcResult lib = UnitCommitment.Solve(model, UnitCommitmentFile.Read(Case9Data, "uc.json", model));
            Assert.True(lib.Optimal, lib.Message);
            using JsonDocument doc = JsonDocument.Parse(File.ReadAllText(json));
            JsonElement s = doc.RootElement.GetProperty("summary");
            Assert.True(s.GetProperty("optimal").GetBoolean());
            Assert.Equal(BitConverter.DoubleToInt64Bits(lib.TotalCost), BitConverter.DoubleToInt64Bits(s.GetProperty("total_cost").GetDouble()));
            Assert.Equal(lib.Nodes, s.GetProperty("nodes").GetInt32());
            Assert.Equal(4, doc.RootElement.GetProperty("tables").GetProperty("periods").GetArrayLength());
            Assert.Equal(12, doc.RootElement.GetProperty("tables").GetProperty("schedule").GetArrayLength());
            Assert.True(lib.StartupCost > 0, "every unit starts off, so the schedule pays start-up costs");
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public void No_schedule_meeting_the_demand_exits_3_and_writes_no_result()
    {
        string dir = TempDir();
        try
        {
            // Ten times case9's load (3,150 MW) against 820 MW of capacity.
            string data = Path.Combine(dir, "uc.json"), json = Path.Combine(dir, "r.json");
            File.WriteAllText(data, """{ "format": "gridsim-uc/1", "periods": 1, "demandScale": [10] }""");
            CliProcess.Result r = CliProcess.RunBuilt("scuc", "case9", "--uc-data", data, "--json", json);
            Assert.Equal(3, r.ExitCode);
            Assert.Contains("scuc: no proven optimum (Infeasible)", r.StdErr, StringComparison.Ordinal);
            Assert.False(File.Exists(json));
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public void A_malformed_data_file_exits_1_with_the_reason()
    {
        string dir = TempDir();
        try
        {
            string data = Path.Combine(dir, "uc.json");
            File.WriteAllText(data, """{ "format": "gridsim-uc/1", "horizon": 4 }""");
            CliProcess.Result r = CliProcess.RunBuilt("scuc", "case9", "--uc-data", data);
            Assert.Equal(1, r.ExitCode);
            Assert.Contains("unknown key 'horizon'", r.StdErr, StringComparison.Ordinal);
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public void Foreign_and_bad_flags_exit_2()
    {
        Assert.Equal(2, CliProcess.RunBuilt("scuc", "case9", "--costs", "case").ExitCode);
        Assert.Equal(2, CliProcess.RunBuilt("scuc", "case9", "--node-cap", "0").ExitCode);
        Assert.Equal(2, CliProcess.RunBuilt("scuc", "case9", "--gap", "-1").ExitCode);
    }
}
