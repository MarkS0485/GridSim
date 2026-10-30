using System;
using System.IO;
using System.Linq;
using System.Text.Json;
using GridSim.Core.Abstractions;
using GridSim.Core.Harmonics;
using GridSim.Core.IO;
using GridSim.Core.Model;
using GridSim.Core.PowerFlow;
using Xunit;

namespace GridSim.Tests;

/// <summary>
/// N243 (October 2026, Mark's machine-epsilon rule: "Machine epsilon to known tools is a must ... it is hard proof the
/// numerical core is executing the math correctly"), with N244 and the N245 remainder. The power-flow cross-validation
/// against pandapower is made like-for-like and judged at 4096 machine epsilons; the harmonic export carries the THD
/// denominator; three more sidecars report a missing package as a skip (77). These are source-level contracts (no
/// Python in the test run) plus the Core facts the comparison relies on. No solver number changes.
/// </summary>
public sealed class N243CrossvalMachineEpsilonTests
{
    private static string Tool(string name)
    {
        string path = Path.Combine(CliProcess.Root(), "tools", "crossval", name);
        Assert.True(File.Exists(path), path);
        return File.ReadAllText(path).Replace("\r\n", "\n");
    }

    private static string Program() =>
        File.ReadAllText(Path.Combine(CliProcess.Root(), "src", "GridSim.Cli", "Program.cs")).Replace("\r\n", "\n");

    // ---- N243: the tolerance -------------------------------------------------------------------------------------

    [Fact]
    public void Powerflow_crossval_default_is_4096_machine_epsilons()
    {
        string py = Tool("crossval.py");
        Assert.Contains("EPS = 2.0 ** -52", py, StringComparison.Ordinal);
        Assert.Contains("DEFAULT_TOL_EPS = 4096", py, StringComparison.Ordinal);
        Assert.Contains("DEFAULT_TOL = DEFAULT_TOL_EPS * EPS", py, StringComparison.Ordinal);
        Assert.Contains("ap.add_argument(\"--tol\", type=float, default=DEFAULT_TOL,", py, StringComparison.Ordinal);
        Assert.DoesNotContain("default=1e-3", py, StringComparison.Ordinal);
        // The old angle rule (100x the |V| tolerance in degrees, floor 0.1 deg) is gone: angles are judged in radians.
        Assert.DoesNotContain("max(tol * 100, 0.1)", py, StringComparison.Ordinal);
        Assert.Contains("max_dva_rad <= tol_rad", py, StringComparison.Ordinal);

        // 4096 eps = 2^-40, the number the impedance / small-signal / harmonic sidecars use (N005, N142).
        double eps = Math.Pow(2, -52);
        Assert.Equal(9.094947017729282e-13, 4096 * eps);
        Assert.Equal(Math.Pow(2, -40), 4096 * eps);
    }

    [Fact]
    public void Powerflow_crossval_solves_the_same_problem_and_guards_the_translation()
    {
        string py = Tool("crossval.py");
        // Generators aggregated per bus (GridSim's model; from_ppc kept the first unit only: uk 6.64e-4 pu).
        Assert.Contains("def aggregate_generators(model)", py, StringComparison.Ordinal);
        Assert.Contains("rows = aggregate_generators(model)", py, StringComparison.Ordinal);
        // Transformer rewrites: charging to bus shunts, LV -> HV branches written HV -> LV (case24 3.85e-2 pu).
        Assert.Contains("moved, reversed_count = rewrite_transformers_for_from_ppc(bus, branch, id_to_row, base_mva)", py, StringComparison.Ordinal);
        Assert.Contains("branch[i, 8] = 1.0 / t", py, StringComparison.Ordinal);
        Assert.Contains("branch[i, 9] = -shift", py, StringComparison.Ordinal);
        // pandapower settings: MATPOWER's pi model, Q limits, no distributed slack, ZIP loads only, tight ladder.
        Assert.Contains("trafo_model=\"pi\"", py, StringComparison.Ordinal);
        Assert.Contains("enforce_q_lims=True", py, StringComparison.Ordinal);
        Assert.Contains("distributed_slack=False", py, StringComparison.Ordinal);
        Assert.Contains("voltage_depend_loads=True", py, StringComparison.Ordinal);
        Assert.Contains("PP_TOLERANCE_LADDER_PU = (1e-8, 1e-9, 1e-10, 1e-11, 1e-12, 1e-13, 1e-14, 1e-15)", py, StringComparison.Ordinal);
        // The guard compares with makeYbus on the UNREWRITTEN export rows and stops (exit 4) on a difference.
        Assert.Contains("YBUS_GUARD_EPS = 4096", py, StringComparison.Ordinal);
        Assert.Contains("ybus_translation_check(net, info[\"raw_ppc\"])", py, StringComparison.Ordinal);
        Assert.Contains("raise RuntimeError(\n            f\"translation guard:", py, StringComparison.Ordinal);
        // The Q-limit outcome of both tools is reported, with GridSim's own Q-limit slack named.
        Assert.Contains("GRIDSIM_Q_LIMIT_TOL_MVA = 1e-3", py, StringComparison.Ordinal);
        Assert.Contains("\"q_limit_outcome_agrees\"", py, StringComparison.Ordinal);
    }

    [Fact]
    public void The_sidecar_Q_limit_slack_equals_GridSims_own()
    {
        // crossval.py names GridSim's Q-limit slack to tell a boundary case from a disagreement; it must stay equal to
        // NewtonShell.QLimitTolMva (internal, read here from the source).
        string shell = File.ReadAllText(Path.Combine(CliProcess.Root(), "src", "GridSim.Core", "PowerFlow", "NewtonShell.cs"));
        Assert.Contains("public const double QLimitTolMva = 1e-3;", shell, StringComparison.Ordinal);
        Assert.Contains("GRIDSIM_Q_LIMIT_TOL_MVA = 1e-3", Tool("crossval.py"), StringComparison.Ordinal);
    }

    [Fact]
    public void Crossval_verb_uses_the_sidecar_default_and_solves_to_the_tightest_tolerance()
    {
        string src = Program();
        int start = src.IndexOf("void RunCrossval(List<string> a, string root)", StringComparison.Ordinal);
        Assert.True(start > 0, "RunCrossval not found");
        int end = src.IndexOf("\nstatic string? FindFirstExisting", start, StringComparison.Ordinal);
        string verb = src[start..end];
        // No looser number of its own: --tol is forwarded only when the user gives it.
        Assert.DoesNotContain("FlagDouble(a, \"--tol\", 1e-3)", verb, StringComparison.Ordinal);
        Assert.Contains("double? tol = a.Contains(\"--tol\") ? FlagDouble(a, \"--tol\", 0.0) : null;", verb, StringComparison.Ordinal);
        Assert.Contains("if (tol is double tolGiven) sidecarArgs.AddRange", verb, StringComparison.Ordinal);
        // The tolerance ladder: fresh solves, the tightest that converges kept, only after the ordinary solve converged.
        Assert.Contains("new[] { 1e-9, 1e-10, 1e-11, 1e-12, 1e-13, 1e-14, 1e-15 }", verb, StringComparison.Ordinal);
        Assert.Contains("raw.Solve(m, opt with { Tolerance = tight, Trace = tightTrace })", verb, StringComparison.Ordinal);
        Assert.Contains("if (!rt.Converged) break;", verb, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("case9")]
    [InlineData("case14")]
    [InlineData("case30")]
    [InlineData("case57")]
    [InlineData("case118")]
    [InlineData("case24")]
    [InlineData("case145")]
    [InlineData("case300")]
    public void Newton_converges_far_below_the_everyday_tolerance_on_the_bundled_cases(string name)
    {
        // The fact the verb's ladder relies on: a GridSim solve that converges at the everyday 1e-8 pu also converges
        // at 1e-12 pu, with a final mismatch below it (the rounding floor of these cases is well under 1e-12 pu).
        GridModel m = name switch
        {
            "case9" => IeeeCases.Case9(),
            "case14" => IeeeCases.Case14(),
            "case30" => IeeeCases.Case30(),
            "case57" => IeeeCases.Case57(),
            "case118" => IeeeCases.Case118(),
            "case24" => IeeeCases.Case24Rts(),
            "case145" => IeeeCases.Case145(),
            _ => IeeeCases.Case300(),
        };
        IPowerFlowSolver solver = m.BusCount <= 200 ? new NewtonRaphsonPowerFlow() : new SparseNewtonRaphsonPowerFlow();
        var opt = new PowerFlowOptions { MaxIterations = 100, EnforceQLimits = true };
        PowerFlowResult everyday = solver.Solve(m, opt);
        Assert.True(everyday.Converged, $"{name}: {everyday.Status}");
        PowerFlowResult tight = solver.Solve(m, opt with { Tolerance = 1e-12 });
        Assert.True(tight.Converged, $"{name} at 1e-12: {tight.Status}");
        Assert.True(tight.MaxMismatch < 1e-12, $"{name}: final mismatch {tight.MaxMismatch:R}");
        // The everyday solve is untouched by this: it still stops at its own tolerance.
        Assert.True(everyday.MaxMismatch < 1e-8);
    }

    // ---- N244: the THD denominator in the harmonic export --------------------------------------------------------

    private static (GridModel Model, HarmonicSourceSet Sources, HarmonicPenetrationResult Pen) HarmonicStudy()
    {
        GridModel model = IeeeCases.Case9();
        var opt = new HarmonicModelOptions { Orders = new[] { 5, 7, 11 } };
        var sources = new HarmonicSourceSet
        {
            Case = "case9",
            Sources = new[] { HarmonicSourceLibrary.Make("6-pulse", model.Buses[4].Id, 0.8) },
        };
        return (model, sources, HarmonicPenetration.Solve(model, sources.Sources, opt));
    }

    [Fact]
    public void N244_harmonic_export_records_the_solved_fundamental_it_divided_by()
    {
        (GridModel model, HarmonicSourceSet sources, HarmonicPenetrationResult pen) = HarmonicStudy();
        PowerFlowResult fundamental = new NewtonRaphsonPowerFlow().Solve(model);
        double[] v1 = HarmonicIndices.SolvedFundamentalVm(pen, fundamental);
        HarmonicIndicesResult idx = HarmonicIndices.FromPenetration(pen, v1);
        var meta = new SolveExportMeta("case9", "harmonics", "abc123", "2026-10-05T00:00:00Z", 0.0, "host");

        using JsonDocument doc = JsonDocument.Parse(HarmonicExport.ToJson(model, sources, pen, idx, meta, fundamentalVm: v1));
        JsonElement list = doc.RootElement.GetProperty("indices").GetProperty("fundamentalVm");
        Assert.Equal(pen.BusIds.Count, list.GetArrayLength());
        int i = 0;
        foreach (JsonElement row in list.EnumerateArray())
        {
            Assert.Equal(pen.BusIds[i], row.GetProperty("bus").GetInt32());
            Assert.Equal(v1[i], row.GetProperty("vmPu").GetDouble());   // the exact double, round-tripped
            // The exported THD is exactly sqrt(sum |V_h|^2) / |V_1| * 100 over THIS |V_1| (what the sidecar re-forms),
            // summed as HarmonicIndices.FromPenetration sums it (Re^2 + Im^2), so the comparison is bit for bit.
            double sumSq = 0;
            for (int oi = 0; oi < pen.Orders.Count; oi++)
                if (pen.Orders[oi] != 1)
                    sumSq += pen.Voltage[oi][i].Real * pen.Voltage[oi][i].Real + pen.Voltage[oi][i].Imaginary * pen.Voltage[oi][i].Imaginary;
            Assert.Equal(Math.Sqrt(sumSq) / v1[i] * 100.0, idx.ThdVoltagePct[i]);
            i++;
        }
    }

    [Fact]
    public void N244_without_the_fundamental_the_export_is_byte_identical_to_before()
    {
        (GridModel model, HarmonicSourceSet sources, HarmonicPenetrationResult pen) = HarmonicStudy();
        HarmonicIndicesResult idx = HarmonicIndices.FromPenetration(pen);
        var meta = new SolveExportMeta("case9", "harmonics", "abc123", "2026-10-05T00:00:00Z", 0.0, "host");
        string before = HarmonicExport.ToJson(model, sources, pen, idx, meta);
        string explicitNull = HarmonicExport.ToJson(model, sources, pen, idx, meta, fundamentalVm: null);
        Assert.Equal(before, explicitNull);
        Assert.DoesNotContain("fundamentalVm", before, StringComparison.Ordinal);
        // A list that does not line up with the study's buses is refused, naming both counts.
        var ex = Assert.Throws<ArgumentException>(() =>
            HarmonicExport.ToJson(model, sources, pen, idx, meta, fundamentalVm: new double[] { 1.0 }));
        Assert.Contains($"{pen.BusIds.Count} buses", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void N244_sidecar_and_verb_use_the_solved_fundamental()
    {
        string py = Tool("crossval_harmonics.py");
        Assert.Contains("export.get(\"indices\", {}).get(\"fundamentalVm\")", py, StringComparison.Ordinal);
        Assert.Contains("mine = float(np.sqrt(sum_sq[bi]) / v1 * 100.0)", py, StringComparison.Ordinal);
        Assert.Contains("\"v1_source\": v1_source", py, StringComparison.Ordinal);
        // Moved 2026-10 (D-DOC-072 meeting N244 at the 1.95.200 merge, Ruling R1: Mark delegated 2026-10-04): DOC-8 routed
        // every CLI output through CliFiles.Output, so the call's path argument is now wrapped. What this test proves is
        // unchanged: the verb passes the solved fundamental v1 as the last argument. Before:
        // "HarmonicExport.WriteJson(path, m, sources, pen, idx, meta, scan, compliance, opt, sweep, v1);"
        Assert.Contains("HarmonicExport.WriteJson(CliFiles.Output(path, \"harmonic export\"), m, sources, pen, idx, meta, scan, compliance, opt, sweep, v1);", Program(),
            StringComparison.Ordinal);
    }

    // ---- N245 remainder: a missing package is a skip (77) ------------------------------------------------------

    [Theory]
    [InlineData("crossval_classical.py")]
    [InlineData("crossval_smallsignal.py")]
    [InlineData("crossval_freq.py")]
    public void N245_missing_numpy_or_scipy_is_a_skip_through_the_shared_helper(string tool)
    {
        string py = Tool(tool);
        Assert.Contains("from sidecar_common import EXIT_SKIPPED, missing", py, StringComparison.Ordinal);
        Assert.Contains("is None:\n        return EXIT_SKIPPED", py, StringComparison.Ordinal);
        Assert.Contains("\"scipy", py, StringComparison.Ordinal);   // SciPy is named in the check
        Assert.DoesNotContain("scipy not installed", py, StringComparison.Ordinal);
        // The package check runs after the export was read, so a bad input is never reported as a skip.
        int read = py.Contains("json.load", StringComparison.Ordinal)
            ? py.IndexOf("def main", StringComparison.Ordinal) : -1;
        int check = py.IndexOf("missing(\"", StringComparison.Ordinal);
        Assert.True(read >= 0 && check > read, $"{tool}: the package check must be inside main, after the export is read");
    }
}
