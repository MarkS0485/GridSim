using System.Text.Json;
using GridSim.Core.Estimation;
using GridSim.Core.Gda;
using GridSim.Core.IO;
using GridSim.Core.Model;
using GridSim.Core.PowerFlow;
using GridSim.Core.Presentation;
using Xunit;
using static GridSim.Tests.Doc15Frames;

namespace GridSim.Tests;

/// <summary>
/// DOC-13 (October 2026), part B: small findings taken with stream 13. N241: an EMT <c>i:</c> probe on parallel branches
/// is refused as ambiguous unless <c>#k</c> names one. N256: the <c>--hti</c> passes count from 0 like every other
/// estimation loop; a GDA series step with no timeUtc or load factor is refused; <c>synth</c> writes under results/ by
/// default; the estimate export carries the whole generator fleet. N258: desktop interconnector pages and fleet rows,
/// and substation connection rows, are one per element (by index), not one per name. N260: <c>pmu-ingest</c> follows
/// the configuration in force at each instant for its per-PMU table and missing-PMU count. Every test is new.
/// </summary>
public sealed class Doc13LowFruitTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "gridsim-doc13-lf-" + Guid.NewGuid().ToString("N"));

    public Doc13LowFruitTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    private static void AssertExit(int expected, CliProcess.Result r)
    {
        Assert.True(r.ExitCode == expected, $"expected exit {expected}\n{r}");
        Assert.DoesNotContain("internal error", r.StdErr, StringComparison.Ordinal);
    }

    // ---- N241: EMT branch-current probes on parallel branches ---------------------------------------------------------

    [Fact]
    public void N241_a_probe_on_parallel_branches_is_refused_as_ambiguous()
    {
        // case118 joins buses 49 and 54 by two in-service lines. i:49-54 used to read the first without a word.
        CliProcess.Result r = CliProcess.RunBuilt("emt", "case118", "--duration", "0.001", "--probe", "i:49-54", "--out", Path.Combine(_dir, "emt-a"));
        AssertExit(2, r);
        Assert.Contains("has 2 in-service series branches 49->54 in parallel, so the probe is ambiguous", r.StdErr, StringComparison.Ordinal);
        Assert.Contains("i:49-54#1 .. i:49-54#2", r.StdErr, StringComparison.Ordinal);
    }

    [Fact]
    public void N241_hash_k_names_one_parallel_branch_and_an_out_of_range_k_is_refused()
    {
        CliProcess.Result ok = CliProcess.RunBuilt("emt", "case118", "--duration", "0.001", "--probe", "v:49,i:49-54#1,i:49-54#2", "--out", Path.Combine(_dir, "emt-b"));
        AssertExit(0, ok);

        CliProcess.Result bad = CliProcess.RunBuilt("emt", "case118", "--duration", "0.001", "--probe", "i:49-54#3", "--out", Path.Combine(_dir, "emt-c"));
        AssertExit(2, bad);
        Assert.Contains("has 2 in-service series branch(es) 49->54, so there is no #3", bad.StdErr, StringComparison.Ordinal);

        CliProcess.Result zero = CliProcess.RunBuilt("emt", "case118", "--duration", "0.001", "--probe", "i:49-54#0", "--out", Path.Combine(_dir, "emt-d"));
        AssertExit(2, zero);
        Assert.Contains("is not a parallel-branch number", zero.StdErr, StringComparison.Ordinal);
    }

    [Fact]
    public void N241_a_single_branch_needs_no_hash_and_accepts_hash_1()
    {
        CliProcess.Result r = CliProcess.RunBuilt("emt", "case9", "--duration", "0.001", "--probe", "i:1-4,i:1-4#1", "--out", Path.Combine(_dir, "emt-e"));
        AssertExit(0, r);
    }

    // ---- N256: --hti pass numbering -------------------------------------------------------------------------------------

    [Fact]
    public void N256_an_hti_removal_and_an_lnr_removal_from_the_first_estimate_are_both_pass_0()
    {
        GridModel model = IeeeCases.Case9();
        PowerFlowResult truth = new NewtonRaphsonPowerFlow().Solve(model);
        MeasurementSet exact = WlsEstimatorTests.ExactFullSet(model, truth);
        var rows = new List<Measurement>(exact.Measurements);
        int bad = rows.FindIndex(m => m.Kind == MeasurementKind.VmPu && m.Bus != 1);
        rows[bad] = rows[bad] with { Value = rows[bad].Value + 0.25 };
        MeasurementSet set = exact with { Measurements = rows };

        HtiEstimationResult hti = HtiBadDataEstimation.Estimate(new WlsStateEstimator(), model, set, new EstimationOptions());
        HtiRemoval removal = Assert.Single(hti.Removed);
        Assert.Equal(0, removal.Pass);    // identified by the first estimate (it was 1)
        Assert.Equal(2, hti.Passes);      // Passes still counts the estimates computed

        EstimationResult lnr = new WlsStateEstimator().Estimate(model, set, new EstimationOptions());
        Assert.Equal(0, Assert.Single(lnr.Removed).Pass);   // the LNR loop's numbering, unchanged

        HtiEstimationResult corrected = HtiBadDataEstimation.Estimate(new WlsStateEstimator(), model, set, new EstimationOptions(),
            action: HtiAction.CorrectThenReverify);
        Assert.Equal(0, Assert.Single(corrected.Corrected).Pass);
    }

    // ---- N256: GDA time-series steps ----------------------------------------------------------------------------------

    [Theory]
    [InlineData("""{ "name": "x", "steps": [ { "systemLoadFactor": 0.9 } ] }""", "steps[0] has no timeUtc")]
    [InlineData("""{ "name": "x", "steps": [ { "timeUtc": "2024-03-01T12:00:00Z" } ] }""", "steps[0] has no systemLoadFactor")]
    [InlineData("""{ "name": "x", "steps": [ { "timeUtc": null, "systemLoadFactor": 0.9 } ] }""", "steps[0] has no timeUtc")]
    [InlineData("""{ "name": "x", "steps": [ { "timeUtc": "2024-03-01T12:00:00Z", "systemLoadFactor": 0.9 }, { "timeUtc": "2024-03-01T12:30:00Z", "systemLoadFactor": -0.1 } ] }""",
        "steps[1] has systemLoadFactor -0.1")]
    public void N256_a_series_step_missing_a_field_is_refused_naming_the_step(string json, string fragment)
    {
        string path = Path.Combine(_dir, "series.json");
        File.WriteAllText(path, json);
        InvalidDataException ex = Assert.Throws<InvalidDataException>(() => GdaTimeSeries.Load(path));
        Assert.Contains(fragment, ex.Message, StringComparison.Ordinal);
        Assert.Contains(path, ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void N256_a_complete_series_still_loads_in_any_key_case_and_a_saved_series_round_trips()
    {
        string path = Path.Combine(_dir, "ok.json");
        File.WriteAllText(path, """{ "Name": "x", "STEPS": [ { "TimeUTC": "2024-03-01T12:00:00Z", "SystemLoadFactor": 0 } ] }""");
        GdaTimeSeries s = GdaTimeSeries.Load(path);
        Assert.Equal(0.0, Assert.Single(s.Steps).SystemLoadFactor);   // an explicit 0 is a written value, accepted

        string saved = Path.Combine(_dir, "saved.json");
        GdaTimeSeries.Synthetic("syn", new DateTime(2024, 3, 1, 0, 0, 0, DateTimeKind.Utc), 4, 30).Save(saved);
        Assert.Equal(4, GdaTimeSeries.Load(saved).Steps.Count);
    }

    // ---- N256: synth writes under results/ by default -------------------------------------------------------------

    [Fact]
    public void N256_synth_with_no_out_writes_under_results_and_never_the_bundled_folder()
    {
        string bundled = Path.Combine(CliProcess.Root(), "data", "gb-synthetic", "buses.json");
        DateTime before = File.GetLastWriteTimeUtc(bundled);
        CliProcess.Result r = CliProcess.RunBuilt("synth", "--scale", "0.02");
        AssertExit(0, r);
        Assert.Contains("written -> ", r.StdOut, StringComparison.Ordinal);
        Assert.Contains(Path.Combine("results", "synth", "gb-synthetic"), r.StdOut, StringComparison.Ordinal);
        Assert.Equal(before, File.GetLastWriteTimeUtc(bundled));
    }

    [Fact]
    public void N256_synth_out_with_no_value_is_a_usage_error()
    {
        CliProcess.Result r = CliProcess.RunBuilt("synth", "--scale", "0.02", "--out");
        AssertExit(2, r);
    }

    // ---- N256: the estimate export carries the whole fleet -------------------------------------------------------------

    [Fact]
    public void N256_the_estimate_export_lists_out_of_service_units_with_their_state()
    {
        GridModel m = IeeeCases.Case9();
        GridModel withSpare = m.CopyWith(generators: m.Generators.Concat(new[] { new Generator(5, Pg: 0, Pmin: 2, Name: "spare", InService: false) }).ToList());
        PowerFlowResult truth = new NewtonRaphsonPowerFlow().Solve(withSpare);
        MeasurementSet set = WlsEstimatorTests.ExactFullSet(withSpare, truth);
        EstimationResult est = new WlsStateEstimator().Estimate(withSpare, set, new EstimationOptions());
        var meta = new SolveExportMeta("case9", "cpu", "test", "2024-01-01T00:00:00Z", 1.0, "test");
        using JsonDocument doc = JsonDocument.Parse(EstimateExport.ToJson(withSpare, set, est, meta));
        JsonElement[] gens = doc.RootElement.GetProperty("model").GetProperty("generators").EnumerateArray().ToArray();
        Assert.Equal(withSpare.AllGenerators.Count, gens.Length);
        // Unnamed units have no "name" key (nulls are omitted), so the name is read with TryGetProperty.
        static string? NameOf(JsonElement g) => g.TryGetProperty("name", out JsonElement n) ? n.GetString() : null;
        JsonElement spare = gens.Single(g => NameOf(g) == "spare");
        Assert.False(spare.GetProperty("inService").GetBoolean());
        Assert.Equal(2.0, spare.GetProperty("pmin").GetDouble());
        Assert.All(gens.Where(g => NameOf(g) != "spare"), g => Assert.True(g.GetProperty("inService").GetBoolean()));
    }

    // ---- N258: desktop pages by index -------------------------------------------------------------------------------

    [Fact]
    public void N258_two_links_sharing_a_name_resolve_to_their_own_index()
    {
        // Two links of one name (as gb-full's two "Auchencrosh (interconnector CCT) interconnector" units), on different
        // buses: each row's own index, confirmed by its bus and name, resolves to itself.
        var m = new GridModel
        {
            BaseMva = 100,
            Buses = new[] { new Bus(1, BusType.Slack, BaseKv: 400), new Bus(2, BusType.Pq, BaseKv: 400), new Bus(3, BusType.Pq, BaseKv: 400) },
            Branches = new[] { new Branch(1, 2, 0.01, 0.1), new Branch(2, 3, 0.01, 0.1) },
            Generators = new[]
            {
                new Generator(1, 100),
                new Generator(2, 50, Name: "Link X", Type: "interconnector"),
                new Generator(3, -40, Name: "Link X", Type: "interconnector"),
            },
        };
        int firstByName = m.Generators.ToList().FindIndex(g => g.Name == "Link X");
        Assert.Equal(1, AssetLookup.FindGenerator(m, 1, 2, "Link X")!.Value.Index);
        Assert.Equal(2, AssetLookup.FindGenerator(m, 2, 3, "Link X")!.Value.Index);
        Assert.NotEqual(2, firstByName);   // the old name lookup opened index 1 for both
    }

    [Fact]
    public void N258_the_interconnector_page_fleet_rows_and_substation_rows_use_the_index()
    {
        // Test retargeted 2026-10-20 (UI programme ASSETS A2): src/GridSim.Wpf/Views/Assets/AssetPages.cs and
        // SubstationPages.cs moved to src/GridSim.Ui/Assets/Pages/AssetPages.cs and SubstationPages.cs; assertions
        // unchanged (the lookup, the explorer route, the click-through and both row keys are asserted, string for string,
        // in the moved files), and strengthened: the WPF page keeps the asserted index-first declaration, word for word,
        // and must derive from the moved model, whose own declaration (the same parameters, with the neutral explorer
        // type) is asserted too; neither file may look a link up by name or offer a name route, the WPF files may hold no
        // lookup or row key at all, and UiAssetPagesParityTests.Two_namesake_links_open_two_different_units_and_two_rows
        // checks the behaviour itself.
        string pages = PresentationConstantsAntiDriftTests.Source("src", "GridSim.Ui", "Assets", "Pages", "AssetPages.cs");
        string wpfPages = PresentationConstantsAntiDriftTests.Source("src", "GridSim.Wpf", "Views", "Assets", "AssetPages.cs");
        Assert.Contains("public sealed class InterconnectorDetailVm(int index, int unitBus, string? unitName, AssetExplorerVm explorer)", wpfPages, StringComparison.Ordinal);
        Assert.Contains("    : InterconnectorDetailModel(index, unitBus, unitName, explorer)", wpfPages, StringComparison.Ordinal);
        Assert.Contains("public class InterconnectorDetailModel(int index, int unitBus, string? unitName, AssetExplorerModel explorer)", pages, StringComparison.Ordinal);
        Assert.Contains("AssetLookup.FindGenerator(m, index, unitBus, unitName);", pages, StringComparison.Ordinal);
        Assert.Contains("public void OpenInterconnector(int index, int bus, string? name) =>", pages, StringComparison.Ordinal);
        Assert.Contains("explorer.OpenInterconnector(unitIndex, unitBus, modelName)", pages, StringComparison.Ordinal);
        Assert.Contains("string key = $\"{gi}:{g.Bus}:{g.Name}\";", pages, StringComparison.Ordinal);
        foreach (string file in new[] { pages, wpfPages })
        {
            Assert.DoesNotContain("OpenInterconnector(string", file, StringComparison.Ordinal);
            Assert.DoesNotContain("m.Generators.FirstOrDefault(x => x.Name == name)", file, StringComparison.Ordinal);
        }
        Assert.DoesNotContain("AssetLookup", wpfPages, StringComparison.Ordinal);
        Assert.DoesNotContain("string key", wpfPages, StringComparison.Ordinal);

        string subs = PresentationConstantsAntiDriftTests.Source("src", "GridSim.Ui", "Assets", "Pages", "SubstationPages.cs");
        string wpfSubs = PresentationConstantsAntiDriftTests.Source("src", "GridSim.Wpf", "Views", "Assets", "SubstationPages.cs");
        Assert.Contains("string key = $\"{bi}:{br.From}-{br.To}:{br.Name}\";", subs, StringComparison.Ordinal);
        Assert.DoesNotContain("string key = br.Name ??", subs, StringComparison.Ordinal);
        Assert.DoesNotContain("string key", wpfSubs, StringComparison.Ordinal);
        Assert.Contains("    : SubstationDetailModel<PlantRow, ConnectionRow>(busId, explorer)", wpfSubs, StringComparison.Ordinal);

        string smoke = PresentationConstantsAntiDriftTests.Source("tools", "AssetSmoke", "Program.cs");
        Assert.Contains("new InterconnectorDetailVm(icIdx, icBus, icName, explorer)", smoke, StringComparison.Ordinal);
    }

    // ---- N260: pmu-ingest follows the configuration in force ----------------------------------------------------------

    private const uint OldSoc = 1_700_000_000;   // 2023-11-14T22:13:20Z: historical
    private static readonly Pmu A = new(101, "STN_A", new[] { Voltage });
    private static readonly Pmu B = new(202, "STN_B", new[] { Voltage });
    private static readonly (float, float)[] One = { (9_500.0f, 0.0f) };

    private string Capture(params byte[][] frames)
    {
        string path = Path.Combine(_dir, $"cap-{Guid.NewGuid():N}.bin");
        File.WriteAllBytes(path, frames.SelectMany(x => x).ToArray());
        return path;
    }

    [Fact]
    public void N260_a_pmu_removed_mid_stream_keeps_its_row_and_is_not_missing_afterwards()
    {
        // Instant 1: PMUs 101 and 202 report. The configuration then drops 202; instant 2: 101 alone, as configured.
        string cap = Capture(
            Config(1_000_000, OldSoc, A, B), Data(OldSoc, 0, One, One),
            Config(1_000_000, OldSoc, A), Data(OldSoc, 20_000, One));
        CliProcess.Result r = CliProcess.RunBuilt("pmu-ingest", cap);
        AssertExit(0, r);
        // Before: only the last configuration's PMU had a row, so 202's sample was in no row.
        Assert.Contains("| STN_B | 202 | 1 | 1 |", r.StdOut, StringComparison.Ordinal);
        Assert.Contains("| STN_A | 101 | 1 | 2 |", r.StdOut, StringComparison.Ordinal);
        Assert.Contains("aligned instants: 2 (0 with a PMU missing)", r.StdOut, StringComparison.Ordinal);
        Assert.Contains("last configuration: 1 PMU(s)", r.StdOut, StringComparison.Ordinal);
        Assert.Contains("each DATA frame was decoded with the configuration in force when it arrived", r.StdOut, StringComparison.Ordinal);
        Assert.DoesNotContain("the last one is used for later frames", r.StdOut, StringComparison.Ordinal);
    }

    [Fact]
    public void N260_a_pmu_added_mid_stream_is_not_missing_before_it_existed()
    {
        string cap = Capture(
            Config(1_000_000, OldSoc, A), Data(OldSoc, 0, One),
            Config(1_000_000, OldSoc, A, B), Data(OldSoc, 20_000, One, One));
        CliProcess.Result r = CliProcess.RunBuilt("pmu-ingest", cap);
        AssertExit(0, r);
        // Before: 202 was expected at instant 1 too, so it counted as "1 with a PMU missing".
        Assert.Contains("aligned instants: 2 (0 with a PMU missing)", r.StdOut, StringComparison.Ordinal);
        Assert.Contains("| STN_B | 202 | 1 | 1 |", r.StdOut, StringComparison.Ordinal);
    }

    [Fact]
    public void N260_a_capture_with_one_configuration_reads_as_before()
    {
        // One configuration throughout: the output keeps its old shape (the "configuration:" line, no change note) and
        // the counts are the concentrator's own, as before N260.
        string cap = Capture(Config(1_000_000, OldSoc, A, B), Data(OldSoc, 0, One, One), Data(OldSoc, 20_000, One, One));
        CliProcess.Result r = CliProcess.RunBuilt("pmu-ingest", cap);
        AssertExit(0, r);
        Assert.Contains("  configuration: 2 PMU(s)", r.StdOut, StringComparison.Ordinal);
        Assert.Contains("aligned instants: 2 (0 with a PMU missing)", r.StdOut, StringComparison.Ordinal);
        Assert.DoesNotContain("CHANGED", r.StdOut, StringComparison.Ordinal);
    }
}
