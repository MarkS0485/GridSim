using System;
using System.IO;
using System.Linq;
using System.Text.Json.Nodes;
using GridSim.Core.Estimation;
using GridSim.Core.IO;
using GridSim.Core.Model;
using GridSim.Core.PowerFlow;
using Xunit;

namespace GridSim.Tests;

/// <summary>
/// DOC-9 (docs-session stream 9, October 2026), the built CLI end to end: dist3ph (D-DOC-114 / 165 branch-current
/// messages and reversed currents, D-DOC-115 the IRLS cap warning, D-DOC-156 the harmonics refusal text, D-DOC-167 the
/// element line for a switch-only feeder, D-DOC-217 the residual table, the robust flags and the not-found path) and
/// serve (D-DOC-163 the state and sigma, D-DOC-181 --max-windows 0).
/// </summary>
public sealed class Doc9CliProcessTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "gridsim-doc9-cli-" + Guid.NewGuid().ToString("N"));

    public Doc9CliProcessTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); }
        catch (IOException) { /* best effort */ }
        catch (UnauthorizedAccessException) { /* best effort */ }
    }

    private static void AssertExit(int expected, CliProcess.Result r) => Assert.True(r.ExitCode == expected, r.ToString());

    private static string Feeder(string name) => Path.Combine(CliProcess.Root(), "data", "dist3ph", name);

    private const string Unbalanced = "ieee4-gyGy-stepdown-unbalanced.json";
    private const string UnbalancedMeasurements = "ieee4-gyGy-stepdown-unbalanced.measurements.json";

    // The bundled measurement file's currents only, optionally with branch 2-3 authored from bus 3 (negated) and
    // optionally without phase b of branch 1-2.
    private string CurrentsOnly(bool reverse23, bool dropB12)
    {
        JsonNode root = JsonNode.Parse(File.ReadAllText(Feeder(UnbalancedMeasurements)))!;
        var currents = new JsonArray();
        foreach (JsonNode? c in root["currents"]!.AsArray())
        {
            int from = c!["from"]!.GetValue<int>(), to = c["to"]!.GetValue<int>();
            string phase = c["phase"]!.GetValue<string>();
            if (dropB12 && from == 1 && to == 2 && phase == "b") continue;
            double re = c["realPu"]!.GetValue<double>(), im = c["imagPu"]!.GetValue<double>();
            bool rev = reverse23 && from == 2 && to == 3;
            currents.Add(new JsonObject
            {
                ["from"] = rev ? 3 : from, ["to"] = rev ? 2 : to, ["phase"] = phase,
                ["realPu"] = rev ? -re : re, ["imagPu"] = rev ? -im : im, ["sigmaPu"] = c["sigmaPu"]!.GetValue<double>(),
            });
        }
        var doc = new JsonObject { ["schema"] = "gridsim-dist3ph-measurements/1", ["currents"] = currents };
        string path = Path.Combine(_dir, $"m-{Guid.NewGuid():N}.json");
        File.WriteAllText(path, doc.ToJsonString());
        return path;
    }

    // ================================================================================================================
    // dist3ph
    // ================================================================================================================

    [Fact]
    public void D165_branch_current_accepts_a_current_named_in_reversed_orientation()
    {
        CliProcess.Result r = CliProcess.RunBuilt("dist3ph", Feeder(Unbalanced), "estimate", "--measurements", CurrentsOnly(reverse23: true, dropB12: false), "--method", "branch-current");
        AssertExit(0, r);   // before: exit 3, "a tree branch has no current phasor"
        Assert.Contains("estimate CONVERGED (one forward sweep over 3 measured branch(es))", r.StdOut, StringComparison.Ordinal);
    }

    [Fact]
    public void D114_branch_current_names_an_unmeasured_phase_and_exits_3()
    {
        CliProcess.Result r = CliProcess.RunBuilt("dist3ph", Feeder(Unbalanced), "estimate", "--measurements", CurrentsOnly(reverse23: false, dropB12: true), "--method", "branch-current");
        AssertExit(3, r);   // before: exit 0 with phase b of every bus downstream of 1-2 wrong
        Assert.Contains("1 branch phase(s) have no current phasor and are unobservable: 1-2 b", r.StdErr, StringComparison.Ordinal);
        Assert.DoesNotContain("CONVERGED", r.StdOut, StringComparison.Ordinal);
    }

    [Fact]
    public void D115_D217_wlav_prints_the_residuals_and_warns_when_its_pass_cap_is_hit()
    {
        CliProcess.Result r = CliProcess.RunBuilt("dist3ph", Feeder(Unbalanced), "estimate", "--measurements", Feeder(UnbalancedMeasurements),
            "--method", "wlav", "--robust-passes", "1");
        AssertExit(0, r);
        Assert.Contains("WARNING: the robust re-weighting (IRLS) reached its cap of 1 pass(es) before the weights settled", r.StdOut, StringComparison.Ordinal);
        Assert.Contains("| measurement | phase | measured pu | estimate pu | r/sigma |", r.StdOut, StringComparison.Ordinal);
        Assert.Contains("| |V| bus 2 | a | 0.995022 |", r.StdOut, StringComparison.Ordinal);
        Assert.Contains("| I 1->2 | a | 0.670785-j0.485777 |", r.StdOut, StringComparison.Ordinal);

        // The defaults (8 passes): plain wls prints the residual table and never the warning.
        CliProcess.Result w = CliProcess.RunBuilt("dist3ph", Feeder(Unbalanced), "estimate", "--measurements", Feeder(UnbalancedMeasurements));
        AssertExit(0, w);
        Assert.Contains("| measurement | phase | measured pu | estimate pu | r/sigma |", w.StdOut, StringComparison.Ordinal);
        Assert.DoesNotContain("WARNING", w.StdOut, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("wlav", "--robust-passes", "0")]
    [InlineData("wlav", "--robust-floor", "0")]
    [InlineData("wlav", "--robust-floor", "-1e-3")]
    [InlineData("wls", "--robust-floor", "0.01")]
    [InlineData("wls", "--robust-passes", "3")]
    [InlineData("branch-current", "--robust-passes", "3")]
    public void D217_the_robust_flags_are_wlav_only_and_range_checked(string method, string flag, string value)
    {
        CliProcess.Result r = CliProcess.RunBuilt("dist3ph", Feeder(Unbalanced), "estimate", "--measurements", Feeder(UnbalancedMeasurements),
            "--method", method, flag, value);
        AssertExit(2, r);
    }

    [Fact]
    public void D217_an_absolute_feeder_path_is_not_repeated_and_a_relative_one_gets_its_full_path()
    {
        string abs = Path.Combine(_dir, "nothere.json");
        CliProcess.Result a = CliProcess.RunBuilt("dist3ph", abs);
        AssertExit(1, a);
        Assert.Contains($"three-phase feeder file not found: '{abs}'.", a.StdErr, StringComparison.Ordinal);
        Assert.DoesNotContain("(full path", a.StdErr, StringComparison.Ordinal);

        CliProcess.Result rel = CliProcess.RunBuilt("dist3ph", "nothere.json");
        AssertExit(1, rel);
        Assert.Contains("three-phase feeder file not found: 'nothere.json' (full path ", rel.StdErr, StringComparison.Ordinal);
    }

    [Fact]
    public void D167_a_feeder_whose_only_extra_elements_are_closed_switches_prints_the_element_line()
    {
        // The balanced IEEE 4-node feeder plus a bus 5 joined to bus 4 by a closed switch (a zero-impedance branch).
        JsonNode f = JsonNode.Parse(File.ReadAllText(Feeder("ieee4-gyGy-stepdown-balanced.json")))!;
        f["buses"]!.AsArray().Add(new JsonObject { ["id"] = 5, ["baseKvLL"] = 4.16 });
        f["switches"] = new JsonArray { new JsonObject { ["id"] = "sw4-5", ["from"] = 4, ["to"] = 5, ["closed"] = true } };
        string path = Path.Combine(_dir, "switch-only.json");
        File.WriteAllText(path, f.ToJsonString());
        CliProcess.Result r = CliProcess.RunBuilt("dist3ph", path);
        AssertExit(0, r);
        // Before: no element line at all (HasExtendedElements does not count closed switches).
        Assert.Contains("0 regulator(s), 1 closed switch(es), 0 open switch(es) (not branches), 0 capacitor bank(s), 0 single- or two-phase branch(es)",
            r.StdOut, StringComparison.Ordinal);

        // A v1 feeder still prints none.
        CliProcess.Result v1 = CliProcess.RunBuilt("dist3ph", Feeder("ieee4-gyGy-stepdown-balanced.json"));
        AssertExit(0, v1);
        Assert.DoesNotContain("closed switch(es)", v1.StdOut, StringComparison.Ordinal);
    }

    [Fact]
    public void D156_the_harmonics_refusal_no_longer_says_it_supports_the_loads_it_leaves_out()
    {
        CliProcess.Result r = CliProcess.RunBuilt("dist3ph", Feeder("ieee123.json"), "harmonics", "--bus", "13", "--spectrum", "6-pulse-g5", "--current", "0.1");
        AssertExit(1, r);
        Assert.Contains("It supports three-phase series-impedance lines and accepts constant-power wye loads", r.StdErr, StringComparison.Ordinal);
        Assert.Contains("loads of every kind, constant-power wye included, are accepted and left out of Y(h)", r.StdErr, StringComparison.Ordinal);
        Assert.DoesNotContain("lines and constant-power wye loads, plus", r.StdErr, StringComparison.Ordinal);
    }

    // ================================================================================================================
    // serve
    // ================================================================================================================

    private string Windows(int count)
    {
        string dir = Path.Combine(_dir, "windows-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        GridModel model = IeeeCases.Case9();
        PowerFlowResult truth = new NewtonRaphsonPowerFlow().Solve(model);
        MeasurementSet template = WlsEstimatorTests.ExactFullSet(model, truth);
        var t0 = new DateTime(2024, 3, 1, 12, 0, 0, DateTimeKind.Utc);
        for (int i = 0; i < count; i++)
            (template with { TakenUtc = t0.AddSeconds(i) }).Save(Path.Combine(dir, $"w{i:D3}.json"));
        return dir;
    }

    [Theory]
    [InlineData("0")]
    [InlineData("-3")]
    public void D181_serve_max_windows_below_1_is_a_usage_error_naming_the_flag(string n)
    {
        CliProcess.Result r = CliProcess.RunBuilt("serve", "case9", "--measurements-dir", Windows(2), "--max-windows", n);
        AssertExit(2, r);   // before (0): exit 1, "no *.json measurement windows in '<dir>'"
        Assert.Contains($"error: --max-windows expected a window count of at least 1 but got '{n}'", r.StdErr, StringComparison.Ordinal);
        Assert.DoesNotContain("no *.json measurement windows", r.StdErr, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("wls", "estimated")]
    [InlineData("ekf", "filtered")]
    [InlineData("ukf", "filtered")]
    [InlineData("enkf", "filtered")]
    public void D163_serve_prints_each_windows_sigma_and_the_last_state(string method, string word)
    {
        CliProcess.Result r = CliProcess.RunBuilt("serve", "case9", "--measurements-dir", Windows(3), "--method", method, "--max-windows", "2");
        AssertExit(0, r);
        Assert.Contains("done: processed 2, converged 2, rejected-fresh 0, failed 0", r.StdOut, StringComparison.Ordinal);
        Assert.Contains("max-sigma |V|=", r.StdOut, StringComparison.Ordinal);
        Assert.Contains($"{word} state at window #1 (2024-03-01T12:00:01", r.StdOut, StringComparison.Ordinal);
        Assert.Contains($"), {method}:", r.StdOut, StringComparison.Ordinal);
        Assert.Contains("| bus | |V| pu | sigma |V| pu | angle deg | sigma angle deg |", r.StdOut, StringComparison.Ordinal);
        // One row per bus of case9.
        Assert.Equal(9, r.StdOut.Split('\n').Count(l => System.Text.RegularExpressions.Regex.IsMatch(l, @"^  \| \d+ \| \d+\.\d{6} \| ")));
    }
}
