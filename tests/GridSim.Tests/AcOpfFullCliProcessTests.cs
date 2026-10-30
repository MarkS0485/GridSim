using System.Text.Json;
using GridSim.Core.Analysis;
using Xunit;

namespace GridSim.Tests;

/// <summary>
/// The <c>acopf-full</c> verb (the full AC optimal power flow, <see cref="AcOpfInteriorPoint"/>), run as the built CLI: its
/// report, its result file carrying the library's numbers bit for bit, a cost file, the exit codes (1 a refused case, 2 a
/// foreign flag, 3 not converged, with the iteration log and no result file).
/// </summary>
public sealed class AcOpfFullCliProcessTests
{
    private static string TempDir()
    {
        string d = Path.Combine(Path.GetTempPath(), "gridsim-acopf-full-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(d);
        return d;
    }

    [Fact]
    public void Case9_reports_the_matpower_objective_and_dispatch()
    {
        CliProcess.Result r = CliProcess.RunBuilt("acopf-full", "case9");
        Assert.Equal(0, r.ExitCode);
        Assert.Contains("AC-OPF (full AC, primal-dual interior point)", r.StdOut, StringComparison.Ordinal);
        Assert.Contains("converged in 11 iteration(s)  objective 5296.69 cost units/h", r.StdOut, StringComparison.Ordinal);
        Assert.Contains("134.32 MW", r.StdOut, StringComparison.Ordinal);
        Assert.Contains("binding constraints: 3", r.StdOut, StringComparison.Ordinal);
    }

    [Fact]
    public void The_result_file_carries_the_library_numbers_bit_for_bit()
    {
        string dir = TempDir();
        try
        {
            string json = Path.Combine(dir, "r.json");
            CliProcess.Result r = CliProcess.RunBuilt("acopf-full", "case30", "--json", json);
            Assert.Equal(0, r.ExitCode);
            AcOpfIpmResult lib = AcOpfInteriorPoint.Solve(S10TestSupport.LoadMatpower("lib/case30.m").Model);
            using JsonDocument doc = JsonDocument.Parse(File.ReadAllText(json));
            JsonElement s = doc.RootElement.GetProperty("summary");
            Assert.True(s.GetProperty("converged").GetBoolean());
            Assert.Equal(lib.Iterations, s.GetProperty("iterations").GetInt32());
            Assert.Equal(BitConverter.DoubleToInt64Bits(lib.ObjectivePerHour), BitConverter.DoubleToInt64Bits(s.GetProperty("objective_per_h").GetDouble()));
            JsonElement[] gens = doc.RootElement.GetProperty("tables").GetProperty("generators").EnumerateArray().ToArray();
            Assert.Equal(lib.Generators.Count, gens.Length);
            JsonElement[] buses = doc.RootElement.GetProperty("tables").GetProperty("buses").EnumerateArray().ToArray();
            Assert.Equal(30, buses.Length);
            Assert.Contains(doc.RootElement.GetProperty("tables").GetProperty("binding").EnumerateArray(),
                row => row.ToString().Contains("Sf", StringComparison.Ordinal));
            Assert.Equal(lib.Log.Count, doc.RootElement.GetProperty("tables").GetProperty("iterations").GetArrayLength());
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public void A_cost_file_with_the_case_curves_gives_the_same_optimum()
    {
        string dir = TempDir();
        try
        {
            // case9's own gencost written as a cost file: the optimum is the same, bit for bit.
            string costs = Path.Combine(dir, "costs.json");
            File.WriteAllText(costs, """
                { "format": "gridsim-costs/1", "generators": [
                  { "index": 1, "model": "polynomial", "coefficients": [0.11, 5, 150] },
                  { "index": 2, "model": "polynomial", "coefficients": [0.085, 1.2, 600] },
                  { "index": 3, "model": "polynomial", "coefficients": [0.1225, 1, 335] } ] }
                """);
            string a = Path.Combine(dir, "a.json"), b = Path.Combine(dir, "b.json");
            Assert.Equal(0, CliProcess.RunBuilt("acopf-full", "case9", "--json", a).ExitCode);
            Assert.Equal(0, CliProcess.RunBuilt("acopf-full", "case9", "--cost-file", costs, "--json", b).ExitCode);
            double Obj(string p)
            {
                using JsonDocument d = JsonDocument.Parse(File.ReadAllText(p));
                return d.RootElement.GetProperty("summary").GetProperty("objective_per_h").GetDouble();
            }
            Assert.Equal(BitConverter.DoubleToInt64Bits(Obj(a)), BitConverter.DoubleToInt64Bits(Obj(b)));
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public void Not_converged_exits_3_with_the_log_and_writes_no_result()
    {
        string dir = TempDir();
        try
        {
            string json = Path.Combine(dir, "r.json");
            CliProcess.Result r = CliProcess.RunBuilt("acopf-full", "case9", "--max-iter", "3", "--json", json);
            Assert.Equal(3, r.ExitCode);
            Assert.Contains("acopf-full: not converged: no convergence within 3 iteration(s).", r.StdErr, StringComparison.Ordinal);
            Assert.Contains("iteration log", r.StdErr, StringComparison.Ordinal);
            Assert.DoesNotContain("dispatch (largest", r.StdOut, StringComparison.Ordinal);
            Assert.False(File.Exists(json));
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public void A_case_without_costs_exits_1_with_the_reason()
    {
        CliProcess.Result r = CliProcess.RunBuilt("acopf-full", "uk");
        Assert.Equal(1, r.ExitCode);
        Assert.Contains("has no cost curve", r.StdErr, StringComparison.Ordinal);
    }

    [Fact]
    public void Foreign_flags_and_a_bad_iteration_cap_exit_2()
    {
        Assert.Equal(2, CliProcess.RunBuilt("acopf-full", "case9", "--costs", "case").ExitCode);
        Assert.Equal(2, CliProcess.RunBuilt("acopf-full", "case9", "--loss-tol", "1e-3").ExitCode);
        Assert.Equal(2, CliProcess.RunBuilt("acopf-full", "case9", "--max-iter", "0").ExitCode);
    }
}
