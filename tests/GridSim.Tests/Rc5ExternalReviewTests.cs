using System.Text;
using System.Text.Json;
using GridSim.Core.Analysis;
using GridSim.Core.IO;
using GridSim.Core.Model;
using GridSim.Core.Numerics;
using GridSim.Core.PowerFlow;
using GridSim.Core.Presentation.Workbench;
using GridSim.Core.Storage;
using Xunit;

namespace GridSim.Tests;

/// <summary>
/// RC5 external-review bug fixes, items 1, 3, 4, 6 and 7 (items 2 and 5 are in <c>Rc5ReviewFix6Tests</c>):
/// <list type="bullet">
/// <item>the storage chain's slack output counts a voltage-dependent load at the slack bus at the solved slack voltage;</item>
/// <item>the batched DC power flow groups families by a hash of the B' structure, with the same families, order and bits;</item>
/// <item><c>report --template-for</c> on a verb's <c>--json</c> result;</item>
/// <item>the crossval sidecar's result-coverage and reactive-limit checks (behaviour in
/// <c>tools/crossval/tests/test_crossval_checks.py</c>; the source contract here, as the other crossval tests do).</item>
/// </list>
/// </summary>
public sealed class Rc5ExternalReviewTests
{
    private static void SameBits(double expected, double actual, string what)
        => Assert.True(BitConverter.DoubleToInt64Bits(expected) == BitConverter.DoubleToInt64Bits(actual),
            $"{what}: expected {expected:R}, got {actual:R}");

    private static void SameBits(double[] a, double[] b, string what)
    {
        Assert.Equal(a.Length, b.Length);
        for (int i = 0; i < a.Length; i++) SameBits(a[i], b[i], $"{what} [{i}]");
    }

    // ---- item 4: storage slack output with a voltage-dependent load at the slack bus ----------------------------------

    private static StorageTimeSeriesResult StorageRun(double slackPd, ZipLoad? slackZip)
    {
        // Bus 1: slack held at 1.04 pu, carrying the load under test. Bus 2: a 10 MW load and an idle storage site (its
        // standby draw is a small load there). The slack bus's own load never enters the network equations (the slack
        // row is not solved), so every run below has the same solved state and only the load counted at the slack moves.
        var model = new GridModel
        {
            BaseMva = 100,
            Name = "storage-slack-zip",
            Buses = new[]
            {
                new Bus(1, BusType.Slack, Pd: slackPd, Vm: 1.04, BaseKv: 132, Zip: slackZip),
                new Bus(2, BusType.Pq, Pd: 10, BaseKv: 132),
            },
            Generators = new[] { new Generator(1, 110, Vg: 1.04) },
            Branches = new[] { new Branch(1, 2, 0.01, 0.1, 0.0, Name: "L12") },
        };
        var pack = PackModel.Sized(BatteryChemistryLibrary.ByName("lfp"), 20e6, 1000.0, 0.5, ratedPowerW: 10e6);
        var site = new StorageSite("lfp", 2, 10, pack, new IdlePolicy());
        return StorageTimeSeries.Run(model, new[] { site }, new StorageTimeSeriesSpec(2, 1.0, _ => 1.0), new NewtonRaphsonPowerFlow());
    }

    [Fact]
    public void Storage_slack_output_counts_a_constant_impedance_slack_load_at_the_solved_voltage()
    {
        // Constant impedance: P(V) = Pd V^2 (ZipLoad Ap = 1). At 1.04 pu a 100 MW load draws 100 x 1.04^2 = 108.16 MW
        // (hand arithmetic: 1.04^2 = 1.0816). Before the fix the slack output counted it at its nominal 100 MW.
        var constantZ = new ZipLoad(Ap: 1, Bp: 0, Cp: 0, Aq: 0, Bq: 0, Cq: 1);
        StorageTimeSeriesResult zipRun = StorageRun(100, constantZ);
        StorageTimeSeriesResult noLoad = StorageRun(0, null);
        StorageTimeSeriesResult constantP = StorageRun(100, null);
        Assert.Equal(2, zipRun.Steps.Count);
        for (int s = 0; s < zipRun.Steps.Count; s++)
        {
            StorageStepRecord z = zipRun.Steps[s], n = noLoad.Steps[s], p = constantP.Steps[s];
            Assert.True(z.Converged && n.Converged && p.Converged, $"step {s} did not converge");
            // The same solved state in all three runs, to the bit.
            SameBits(n.VminPu, z.VminPu, $"step {s} Vmin");
            SameBits(n.VmaxPu, z.VmaxPu, $"step {s} Vmax");
            SameBits(n.LossMw, z.LossMw, $"step {s} loss");
            // With no load at the slack the record is the slack's net injection; the ZIP run adds the load at 1.04 pu.
            double loadAtSlack = z.SlackMw - n.SlackMw;
            Assert.True(Math.Abs(loadAtSlack - 108.16) <= 1e-12, $"step {s}: slack load {loadAtSlack:R} MW, expected 108.16");
            Assert.True(Math.Abs(loadAtSlack - 100.0) > 8.0, $"step {s}: the nominal 100 MW was counted");
            // Exactly the formula: the net injection plus LoadAtVoltage at the solved slack voltage.
            SameBits(n.SlackMw + 100.0 * constantZ.PMultiplier(1.04), z.SlackMw, $"step {s} slack");
            // A constant-power slack load is its stored Pd, as before the fix.
            SameBits(n.SlackMw + 100.0, p.SlackMw, $"step {s} constant-power slack");
        }
    }

    // ---- item 6: DC batch family grouping ---------------------------------------------------------------------------

    /// <summary>The grouping DcPowerFlow.SolveBatch made up to 2.1.094, kept here as the reference: the first unassigned
    /// model's B' is a family's template, and every later unassigned model is tested against it with
    /// <see cref="SparseLuFamily.IsMember"/>.</summary>
    private static int[] PairwiseFamilies(CsrMatrix?[] b)
    {
        int count = b.Length;
        var familyOf = new int[count];
        Array.Fill(familyOf, -1);
        int familyCount = 0;
        for (int i = 0; i < count; i++)
        {
            if (b[i] is null || familyOf[i] >= 0) continue;
            var family = new SparseLuFamily(b[i]!);
            int f = familyCount++;
            for (int j = i; j < count; j++)
                if (b[j] is not null && familyOf[j] < 0 && family.IsMember(b[j]!)) familyOf[j] = f;
        }
        return familyOf;
    }

    private static CsrMatrix?[] BPrimes(IReadOnlyList<GridModel> models)
        => models.Select(m => (CsrMatrix?)DistributionFactors.Prepare(m).BPrime).ToArray();

    private static GridModel Without(GridModel m, int branch)
        => m.CopyWith(branches: m.Branches.Where((_, j) => j != branch).ToList());

    private static bool DcSolves(GridModel m)
    {
        try { DcPowerFlow.Solve(m); return true; }
        catch (InvalidOperationException) { return false; }
        catch (ArgumentException) { return false; }
    }

    private static void SameResult(DcPowerFlowResult single, DcPowerFlowResult batched, string what)
    {
        Assert.Equal(single.BusIds, batched.BusIds);
        SameBits(single.VaDeg, batched.VaDeg, what + " Va");
        SameBits(single.PInjMw, batched.PInjMw, what + " Pinj");
        SameBits(single.FlowMw, batched.FlowMw, what + " flow");
        SameBits(single.LoadingPercent, batched.LoadingPercent, what + " loading");
        Assert.Equal(single.Islands.Count, batched.Islands.Count);
        for (int k = 0; k < single.Islands.Count; k++)
        {
            Assert.Equal(single.Islands[k].ReferenceBusId, batched.Islands[k].ReferenceBusId);
            SameBits(single.Islands[k].ReferenceInjectionMw, batched.Islands[k].ReferenceInjectionMw, what + $" island {k}");
        }
    }

    [Fact]
    public void Dc_batch_families_and_results_are_those_of_the_pairwise_scan_on_a_mixed_topology_batch()
    {
        GridModel m118 = IeeeCases.Case118();
        GridModel[] samples = DcPowerFlowBatchTests.Samples(m118, 9, 23UL);
        // Four single outages whose B' structure is not the base one (a parallel circuit's outage would keep it).
        var baseFamily = new SparseLuFamily(DistributionFactors.Prepare(m118).BPrime);
        int[] outages = Enumerable.Range(0, m118.Branches.Count)
            .Where(k => DcSolves(Without(m118, k)) && !baseFamily.IsMember(DistributionFactors.Prepare(Without(m118, k)).BPrime))
            .Take(4).ToArray();
        Assert.Equal(4, outages.Length);
        GridModel c9 = IeeeCases.Case9(), c14 = IeeeCases.Case14();
        // Families interleaved and revisited: a family's later members come after other families' templates.
        var models = new List<GridModel>
        {
            samples[0], c9, Without(samples[1], outages[0]), samples[2], c14, Without(m118, outages[1]),
            samples[3], Without(samples[4], outages[0]), c9.CopyWith(buses: c9.Buses.Select(b => b with { Pd = b.Pd * 1.01 }).ToList()),
            Without(samples[5], outages[2]), samples[6], c14, Without(m118, outages[3]), Without(samples[7], outages[1]), samples[8],
        };
        CsrMatrix?[] b = BPrimes(models);
        int[] reference = PairwiseFamilies(b);
        int[] hashed = DcPowerFlow.FamilyAssignments(b, out int familyCount);
        Assert.Equal(reference, hashed);
        Assert.Equal(reference.Max() + 1, familyCount);
        // The expected numbering, by hand from the list above: 118 base 0, case9 1, outage a 2, case14 3, outage b 4,
        // outage c 5, outage d 6.
        Assert.Equal(new[] { 0, 1, 2, 0, 3, 4, 0, 2, 1, 5, 0, 3, 6, 4, 0 }, hashed);

        // A null entry (a model the DC model cannot represent) has no family and does not shift the numbering.
        CsrMatrix?[] withNull = b.ToArray();
        withNull[1] = null;
        Assert.Equal(PairwiseFamilies(withNull), DcPowerFlow.FamilyAssignments(withNull, out _));

        // Outputs: every batched result is the single solve's bits, as it was before the change (DcPowerFlowBatchTests).
        IReadOnlyList<DcPowerFlowResult> parallel = DcPowerFlow.SolveBatch(models);
        IReadOnlyList<DcPowerFlowResult> serial = DcPowerFlow.SolveBatch(models, maxDegreeOfParallelism: 1);
        for (int i = 0; i < models.Count; i++)
        {
            DcPowerFlowResult single = DcPowerFlow.Solve(models[i]);
            SameResult(single, parallel[i], $"model {i}");
            SameResult(single, serial[i], $"model {i} (one thread)");
        }
    }

    [Fact]
    public void Dc_batch_of_many_distinct_topologies_groups_like_the_pairwise_scan_and_matches_the_single_solve()
    {
        // case118 with each branch out in turn (where the DC model still solves): a parallel circuit's outage keeps the
        // base structure, every other outage gives a structure of its own, so the batch holds over a hundred families.
        GridModel m118 = IeeeCases.Case118();
        List<GridModel> models = Enumerable.Range(0, m118.Branches.Count).Select(k => Without(m118, k)).Where(DcSolves).ToList();
        CsrMatrix?[] b = BPrimes(models);
        int[] reference = PairwiseFamilies(b);
        int[] hashed = DcPowerFlow.FamilyAssignments(b, out int familyCount);
        Assert.Equal(reference, hashed);
        Assert.True(familyCount > 100, $"{familyCount} families");
        // Numbered in order of first appearance.
        int next = 0;
        foreach (int f in hashed)
        {
            Assert.True(f <= next, $"family {f} appears before family {next}");
            if (f == next) next++;
        }
        IReadOnlyList<DcPowerFlowResult> batched = DcPowerFlow.SolveBatch(models);
        for (int i = 0; i < models.Count; i++) SameResult(DcPowerFlow.Solve(models[i]), batched[i], $"outage model {i}");
    }

    // ---- item 7: report --template-for on a verb result ---------------------------------------------------------------

    private const string CliResult = """
        {
          "schema": "gridsim-cli-result/1",
          "tool": "gridsim test",
          "verb": "n-1",
          "args": [ "n-1", "case9" ],
          "exitCode": 0,
          "summary": { "case": "case9", "outages": 9, "worst/loading": 87.5 },
          "tables": {
            "outages": [ { "branch": "1-4", "loadingPct": 87.5, "secure": true }, { "branch": "4-5", "loadingPct": 60.25, "secure": true } ],
            "empty": [ ]
          }
        }
        """;

    [Fact]
    public void Default_template_for_a_verb_result_binds_the_row_arrays_and_renders()
    {
        byte[] result = Encoding.UTF8.GetBytes(CliResult);
        string json = ReportTemplate.DefaultJson(result);   // threw InvalidOperationException (TryGetProperty on an array)
        ReportTemplate t = ReportTemplate.Parse(Encoding.UTF8.GetBytes(json));
        Assert.Equal("Study report: n-1", t.Title);
        Assert.Equal(new[] { "Summary", "outages", "empty" }, t.Sections.Select(s => s.Heading).ToArray());
        string[] summaryPaths = t.Sections[0].Blocks.OfType<ReportValueBlock>().Select(v => v.Path).ToArray();
        Assert.Equal(new[] { "/verb", "/exitCode", "/summary/case", "/summary/outages", "/summary/worst~1loading" }, summaryPaths);
        var table = Assert.IsType<ReportTableBlock>(Assert.Single(t.Sections[1].Blocks));
        Assert.Equal("/tables/outages", table.Path);
        Assert.Null(table.Columns);

        string md = ReportRenderer.ToMarkdown(ReportRenderer.Build(Encoding.UTF8.GetBytes(json), result));
        Assert.Contains("- **Verb:** n-1", md, StringComparison.Ordinal);
        Assert.Contains("- **worst/loading:** 87.5000", md, StringComparison.Ordinal);
        Assert.Contains("| 1-4 | 87.5000 | yes |", md, StringComparison.Ordinal);
        Assert.Contains("| 4-5 | 60.2500 | yes |", md, StringComparison.Ordinal);
    }

    [Fact]
    public void Report_template_for_a_real_verb_result_through_the_cli()
    {
        string dir = R401Cases.NewRoot();
        try
        {
            string res = Path.Combine(dir, "n1.json"), tpl = Path.Combine(dir, "t.json"), md = Path.Combine(dir, "r.md");
            CliProcess.Result solved = CliProcess.RunBuilt("n-1", "case9", "--json", res);
            Assert.True(File.Exists(res), solved.ToString());
            CliProcess.Result made = CliProcess.RunBuilt("report", "--template-for", res, "--out", tpl);
            Assert.True(made.ExitCode == 0, made.ToString());
            using (JsonDocument doc = JsonDocument.Parse(File.ReadAllBytes(tpl)))
                Assert.Equal("gridsim-report-template/1", doc.RootElement.GetProperty("schema").GetString());
            Assert.Contains("\"path\": \"/tables/", File.ReadAllText(tpl), StringComparison.Ordinal);
            CliProcess.Result rendered = CliProcess.RunBuilt("report", tpl, res, "--out", md);
            Assert.True(rendered.ExitCode == 0, rendered.ToString());
            string text = File.ReadAllText(md);
            Assert.Contains("- **Verb:** nminus1", text, StringComparison.Ordinal);   // the alias is written canonically
            Assert.DoesNotContain("No table data at", text, StringComparison.Ordinal);
        }
        finally
        {
            try { Directory.Delete(dir, true); } catch (IOException) { }
        }
    }

    // ---- items 1 and 3: the crossval sidecar (source contract; behaviour in the Python tests) -------------------------

    private static string Crossval() =>
        File.ReadAllText(Path.Combine(CliProcess.Root(), "tools", "crossval", "crossval.py")).Replace("\r\n", "\n", StringComparison.Ordinal);

    [Fact]
    public void Crossval_requires_one_result_row_per_model_bus()
    {
        string py = Crossval();
        Assert.Contains("def check_result_covers_model(doc):", py, StringComparison.Ordinal);
        // Checked when the export is read (exit 3) and again in diff, where the compared count must equal the model's.
        Assert.Contains("    check_result_covers_model(doc)\n    return doc\n", py, StringComparison.Ordinal);
        Assert.Contains("model_bus_count = check_result_covers_model(doc)", py, StringComparison.Ordinal);
        Assert.Contains("if len(rows) != model_bus_count:", py, StringComparison.Ordinal);
        Assert.True(File.Exists(Path.Combine(CliProcess.Root(), "tools", "crossval", "tests", "test_crossval_checks.py")));
    }

    [Fact]
    public void Crossval_q_limit_check_keeps_the_limit_identity()
    {
        string py = Crossval();
        Assert.Contains("def classify_q_limit_outcomes(gs, pp_, ranges, q_gridsim, q_pandapower):", py, StringComparison.Ordinal);
        Assert.Contains("if (a is not None and b is not None) or qmin is None or qmax is None:", py, StringComparison.Ordinal);
        Assert.Contains("limit = float(qmin if pinned == \"min\" else qmax)", py, StringComparison.Ordinal);
        Assert.DoesNotContain("near = min(abs(q_gen - qmax), abs(q_gen - qmin))", py, StringComparison.Ordinal);
        // The boundary slack is still GridSim's own (N243 pins its value).
        Assert.Contains("abs(q_free - limit) <= GRIDSIM_Q_LIMIT_TOL_MVA", py, StringComparison.Ordinal);
    }
}
