using System;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Numerics;
using System.Text;
using GridSim.Core.Distribution;
using Xunit;

namespace GridSim.Tests;

/// <summary>
/// N062 / R059 (October 2026): the dist3ph verb's new surfaces, run as the real CLI: the three-phase harmonic
/// penetration (harmonics mode), the branch-current estimator (estimate --method branch-current), and a radial
/// positive-sequence case lifted by the GridModel adapter. Each is a wiring of an existing Core path; the numbers are
/// checked against the Core call where it matters.
/// </summary>
public class MultiPhaseCliModesTests
{
    private static string Root()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "GridSim.slnx"))) dir = dir.Parent;
        Assert.True(dir is not null, $"GridSim.slnx not found above {AppContext.BaseDirectory}");
        return dir!.FullName;
    }

    private static string Feeder(string name) => Path.Combine(Root(), "data", "dist3ph", name);

    [Fact]
    public void Harmonics_runs_a_balanced_library_source_and_prints_each_order()
    {
        CliProcess.Result r = CliProcess.RunBuilt("dist3ph", Feeder("ieee4-gyGy-stepdown-balanced.json"), "harmonics",
            "--bus", "4", "--spectrum", "6-pulse-g5", "--current", "0.1");
        Assert.True(r.ExitCode == 0, r.ToString());
        Assert.Contains("balanced harmonic source at bus 4", r.StdOut, StringComparison.Ordinal);
        Assert.Contains("| 5 |", r.StdOut, StringComparison.Ordinal);
        Assert.Contains("| 49 |", r.StdOut, StringComparison.Ordinal);   // the last order of the G5 six-pulse profile
        Assert.Contains("RSS harmonic voltage at bus 4", r.StdOut, StringComparison.Ordinal);
    }

    [Fact]
    public void Harmonics_matches_the_core_penetration()
    {
        // The printed |Va| at order 5 is the Core solve's, to the 6 printed decimals.
        MultiPhaseFeeder f = MultiPhaseJsonLoader.Load(Feeder("ieee4-gyGy-stepdown-balanced.json"));
        var src = GridSim.Core.Harmonics.ThreePhaseHarmonicSource.Balanced(4, "6-pulse-g5", 0.1, GridSim.Core.Harmonics.HarmonicSourceLibrary.SixPulseG5);
        GridSim.Core.Harmonics.ThreePhasePenetrationResult hr = GridSim.Core.Harmonics.ThreePhaseHarmonicPenetration.Solve(f.Model, new[] { src }, new[] { 5 });
        double va = hr.Voltage[0][hr.BusIds.ToList().IndexOf(4)][0].Magnitude;
        CliProcess.Result r = CliProcess.RunBuilt("dist3ph", Feeder("ieee4-gyGy-stepdown-balanced.json"), "harmonics",
            "--bus", "4", "--spectrum", "6-pulse-g5", "--current", "0.1");
        Assert.Contains($"| 5 | {va.ToString("F6", CultureInfo.InvariantCulture)} |", r.StdOut, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(2, "--bus", "4", "--spectrum", "6-pulse-g5")]                         // no --current
    [InlineData(2, "--bus", "4", "--spectrum", "7-pulse", "--current", "0.1")]         // unknown spectrum
    [InlineData(2, "--bus", "4", "--spectrum", "6-pulse-g5", "--current", "0")]        // not above 0
    [InlineData(1, "--bus", "1", "--spectrum", "6-pulse-g5", "--current", "0.1")]      // the source bus
    [InlineData(1, "--bus", "9", "--spectrum", "6-pulse-g5", "--current", "0.1")]      // not in the feeder
    [InlineData(2, "--bus", "4", "--spectrum", "6-pulse-g5", "--current", "0.1", "--tol", "1e-9")]   // another mode's flag
    public void Harmonics_refuses_bad_input(int exit, params string[] flags)
    {
        CliProcess.Result r = CliProcess.RunBuilt(new[] { "dist3ph", Feeder("ieee4-gyGy-stepdown-balanced.json"), "harmonics" }.Concat(flags).ToArray());
        Assert.True(r.ExitCode == exit, r.ToString());
    }

    [Fact]
    public void Harmonics_refuses_a_feeder_with_the_new_sweep_elements()
    {
        CliProcess.Result r = CliProcess.RunBuilt("dist3ph", Feeder("ieee123.json"), "harmonics", "--bus", "13", "--spectrum", "6-pulse-g5", "--current", "0.1");
        Assert.True(r.ExitCode == 1, r.ToString());
        Assert.Contains("harmonic penetration", r.StdErr, StringComparison.Ordinal);
    }

    [Fact]
    public void The_branch_current_estimator_reproduces_the_sweep_from_its_exact_branch_currents()
    {
        // Measurements: the sweep's own branch currents on every branch (exact), so the estimate's single forward sweep
        // must give the sweep's voltages back.
        string feeder = Feeder("ieee4-gyGy-stepdown-unbalanced.json");
        MultiPhaseFeeder f = MultiPhaseJsonLoader.Load(feeder);
        MultiPhaseResult sw = new ThreePhaseSweep().Solve(f.Model);
        Assert.True(sw.Converged);
        var inv = CultureInfo.InvariantCulture;
        var sb = new StringBuilder("{ \"schema\": \"gridsim-dist3ph-measurements/1\", \"currents\": [");
        for (int k = 0; k < f.Model.Branches.Count; k++)
            for (int p = 0; p < 3; p++)
            {
                Complex i = sw.BranchCurrents![k][p];
                if (k + p > 0) sb.Append(", ");
                sb.Append($"{{ \"from\": {f.Model.Branches[k].From}, \"to\": {f.Model.Branches[k].To}, \"phase\": \"{"abc"[p]}\", " +
                          $"\"realPu\": {i.Real.ToString("R", inv)}, \"imagPu\": {i.Imaginary.ToString("R", inv)}, \"sigmaPu\": 0.001 }}");
            }
        sb.Append("] }");
        string mFile = Path.Combine(Path.GetTempPath(), $"gridsim-bc-{Guid.NewGuid():N}.json");
        File.WriteAllText(mFile, sb.ToString());
        try
        {
            BranchCurrentEstimate core = BranchCurrentDsse.Estimate(f.Model, MultiPhaseJsonLoader.LoadMeasurements(mFile, f).Currents);
            Assert.True(core.Converged);
            for (int b = 0; b < sw.V.Length; b++)
                for (int p = 0; p < 3; p++)
                    Assert.True((core.V[b][p] - sw.V[b][p]).Magnitude < 1e-12, $"bus {sw.BusIds[b]} phase {p}");

            CliProcess.Result r = CliProcess.RunBuilt("dist3ph", feeder, "estimate", "--measurements", mFile, "--method", "branch-current");
            Assert.True(r.ExitCode == 0, r.ToString());
            Assert.Contains("estimate CONVERGED (one forward sweep over 3 measured branch(es))", r.StdOut, StringComparison.Ordinal);

            CliProcess.Result t = CliProcess.RunBuilt("dist3ph", feeder, "estimate", "--measurements", mFile, "--method", "branch-current", "--tol", "1e-9");
            Assert.True(t.ExitCode == 2, t.ToString());
        }
        finally { File.Delete(mFile); }
    }

    [Fact]
    public void The_branch_current_estimator_refuses_a_file_with_voltage_measurements_it_would_ignore()
    {
        CliProcess.Result r = CliProcess.RunBuilt("dist3ph", Feeder("ieee4-gyGy-stepdown-unbalanced.json"), "estimate",
            "--measurements", Feeder("ieee4-gyGy-stepdown-unbalanced.measurements.json"), "--method", "branch-current");
        Assert.True(r.ExitCode == 1, r.ToString());
        Assert.Contains("current phasors only", r.StdErr, StringComparison.Ordinal);
    }

    [Fact]
    public void A_radial_matpower_case_is_lifted_and_swept()
    {
        CliProcess.Result r = CliProcess.RunBuilt("dist3ph", Path.Combine(Root(), "data", "matpower", "dist", "case33bw.m"));
        Assert.True(r.ExitCode == 0, r.ToString());
        Assert.Contains("33 buses, 32 branches", r.StdOut, StringComparison.Ordinal);
        Assert.Contains("sweep CONVERGED", r.StdOut, StringComparison.Ordinal);
    }
}
