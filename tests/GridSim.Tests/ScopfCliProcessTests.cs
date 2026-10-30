using System.Text.Json;
using GridSim.Core.Analysis;
using GridSim.Core.PowerFlow;
using Xunit;

namespace GridSim.Tests;

/// <summary>
/// The <c>scopf</c> verb (preventive security-constrained DC OPF, <see cref="DcScopf"/>), run as the built CLI: its
/// report, its result file carrying the library's numbers bit for bit, linear cost curves from a cost file, and the exit
/// codes (1 a refused outage or a cost curve that is not linear, 2 a bad or foreign flag, 3 an infeasible security set,
/// with no result file).
/// </summary>
public sealed class ScopfCliProcessTests
{
    private static string TempDir()
    {
        string d = Path.Combine(Path.GetTempPath(), "gridsim-scopf-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(d);
        return d;
    }

    private static long Bits(double v) => BitConverter.DoubleToInt64Bits(v);

    // Linear costs for case9's three units: 10, 30 and 20 per MWh, with constant terms 100, 0 and 5.
    private const string Case9Linear = """
        { "format": "gridsim-costs/1", "generators": [
          { "index": 1, "model": "polynomial", "coefficients": [10, 100] },
          { "index": 2, "model": "polynomial", "coefficients": [0, 30, 0] },
          { "index": 3, "model": "polynomial", "coefficients": [20, 5] } ] }
        """;

    [Fact]
    public void Case9_reports_a_secure_dispatch_and_its_islanding_outages()
    {
        CliProcess.Result r = CliProcess.RunBuilt("scopf", "case9");
        Assert.Equal(0, r.ExitCode);
        Assert.Contains("SCOPF (preventive, DC)", r.StdOut, StringComparison.Ordinal);
        Assert.Contains("outages secured: 6; islanding outages not secured: 3", r.StdOut, StringComparison.Ordinal);
        Assert.Contains("optimal: total cost", r.StdOut, StringComparison.Ordinal);
        Assert.Contains("after outage of branch", r.StdOut, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void The_result_file_carries_the_library_numbers_bit_for_bit(bool fullLp)
    {
        string dir = TempDir();
        try
        {
            string costs = Path.Combine(dir, "costs.json"), json = Path.Combine(dir, "r.json");
            File.WriteAllText(costs, Case9Linear);
            var args = new List<string> { "scopf", "case9", "--linear-costs", costs, "--post-rating", "C", "--json", json };
            if (fullLp) args.Add("--full-lp");
            CliProcess.Result r = CliProcess.RunBuilt(args.ToArray());
            Assert.Equal(0, r.ExitCode);

            var model = S10TestSupport.LoadMatpower("lib/case9.m").Model;
            ScopfResult lib = DcScopf.Solve(model, new ScopfOptions
            {
                CostPerGen = new double[] { 10, 30, 20 }, PostContingencyRating = PostFaultRating.RateC, ConstraintGeneration = !fullLp,
            });
            Assert.True(lib.Optimal, lib.Message);
            using JsonDocument doc = JsonDocument.Parse(File.ReadAllText(json));
            JsonElement s = doc.RootElement.GetProperty("summary");
            Assert.True(s.GetProperty("optimal").GetBoolean());
            Assert.Equal(Bits(lib.TotalCost + 105.0), Bits(s.GetProperty("total_cost_per_h").GetDouble()));   // constants 100 + 0 + 5
            Assert.Equal(lib.Rounds, s.GetProperty("rounds").GetInt32());
            Assert.Equal("C", s.GetProperty("post_fault_rating").GetString());
            JsonElement dispatch = doc.RootElement.GetProperty("tables").GetProperty("dispatch");
            Assert.Equal(3, dispatch.GetArrayLength());
            for (int g = 0; g < 3; g++)
                Assert.Equal(Bits(lib.Dispatch[g].Pmw), Bits(dispatch[g].GetProperty("p_mw").GetDouble()));
            JsonElement prices = doc.RootElement.GetProperty("tables").GetProperty("prices");
            Assert.Equal(9, prices.GetArrayLength());
            for (int i = 0; i < 9; i++)
                Assert.Equal(Bits(lib.Prices[i].Lmp), Bits(prices[i].GetProperty("lmp_per_mwh").GetDouble()));
            Assert.Equal(3, doc.RootElement.GetProperty("tables").GetProperty("islanding_outages").GetArrayLength());
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public void An_infeasible_security_set_exits_3_with_no_result_file()
    {
        string dir = TempDir();
        try
        {
            // Triangle 1-2-3, X 0.1 pu, 150 MW at bus 3, line 1-2 rated 10 MW: the outage of 1-3 needs unit 1 at or below
            // 10 MW and the outage of 2-3 needs it at or above 140 MW.
            string m = Path.Combine(dir, "tri.m"), json = Path.Combine(dir, "r.json");
            File.WriteAllText(m, """
                function mpc = tri
                mpc.version = '2';
                mpc.baseMVA = 100;
                mpc.bus = [
                    1 3 0 0 0 0 1 1 0 230 1 1.1 0.9;
                    2 2 0 0 0 0 1 1 0 230 1 1.1 0.9;
                    3 1 150 0 0 0 1 1 0 230 1 1.1 0.9;
                ];
                mpc.gen = [
                    1 0 0 300 -300 1 100 1 300 0;
                    2 0 0 300 -300 1 100 1 300 0;
                ];
                mpc.branch = [
                    1 2 0 0.1 0 10 0 0 0 0 1 -360 360;
                    1 3 0 0.1 0 200 0 0 0 0 1 -360 360;
                    2 3 0 0.1 0 200 0 0 0 0 1 -360 360;
                ];
                """);
            CliProcess.Result r = CliProcess.RunBuilt("scopf", m, "--json", json);
            Assert.Equal(3, r.ExitCode);
            Assert.Contains("no secure dispatch (Infeasible)", r.StdErr, StringComparison.Ordinal);
            Assert.DoesNotContain("optimal: total cost", r.StdOut, StringComparison.Ordinal);
            Assert.False(File.Exists(json));
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public void Refusals_exit_1_and_bad_flags_exit_2()
    {
        // case9's gencost is quadratic: refused, not approximated.
        CliProcess.Result q = CliProcess.RunBuilt("scopf", "case9", "--linear-costs", "case");
        Assert.Equal(1, q.ExitCode);
        Assert.Contains("quadratic", q.StdErr, StringComparison.Ordinal);
        // Branch 0 of case9 is the generator transformer 1-4: its outage islands bus 1.
        CliProcess.Result isl = CliProcess.RunBuilt("scopf", "case9", "--contingencies", "0");
        Assert.Equal(1, isl.ExitCode);
        Assert.Contains("islands the network", isl.StdErr, StringComparison.Ordinal);
        Assert.Equal(1, CliProcess.RunBuilt("scopf", "case9", "--contingencies", "99").ExitCode);
        Assert.Equal(2, CliProcess.RunBuilt("scopf", "case9", "--post-rating", "D").ExitCode);
        Assert.Equal(2, CliProcess.RunBuilt("scopf", "case9", "--max-rounds", "0").ExitCode);
        Assert.Equal(2, CliProcess.RunBuilt("scopf", "case9", "--contingencies", "1,x").ExitCode);
        Assert.Equal(2, CliProcess.RunBuilt("scopf", "case9", "--costs", "case").ExitCode);
        Assert.Equal(2, CliProcess.RunBuilt("scopf", "case9", "--gap", "1").ExitCode);
        Assert.Equal(2, CliProcess.RunBuilt("scopf").ExitCode);
    }
}
