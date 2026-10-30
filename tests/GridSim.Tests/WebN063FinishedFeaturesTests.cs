using GridSim.Core.IO;
using GridSim.Core.PowerFlow;
using GridSim.Core.Presentation;
using GridSim.Web.Model;
using GridSim.Web.Projection;
using GridSim.Web.Sessions;
using Microsoft.Extensions.Configuration;
using Xunit;

namespace GridSim.Tests;

/// <summary>
/// N063: the web features that were placeholders are finished. The solver switch (SolverChoice, SessionManager,
/// the dashboard picker and the /ws/sim "solver" field), the bridge condition / stress overlays and HUD scalars on the
/// frame envelope, and the GDA per-asset card rows (shared with WPF through Core's AssetInsightFormatter).
/// </summary>
public sealed class WebN063FinishedFeaturesTests
{
    // ---- solver switch -------------------------------------------------------------------------------------

    [Fact]
    public void Solver_names_map_both_ways_and_unknown_names_fail_loud()
    {
        Assert.Equal(SolverKind.Auto, SolverChoice.Parse(null));
        Assert.Equal(SolverKind.Auto, SolverChoice.Parse(""));
        Assert.Equal(SolverKind.Auto, SolverChoice.Parse("  "));
        Assert.Equal(SolverKind.Auto, SolverChoice.Parse("auto"));
        Assert.Equal(SolverKind.DenseNewton, SolverChoice.Parse("dense"));
        Assert.Equal(SolverKind.SparseNewton, SolverChoice.Parse("SPARSE"));
        Assert.Equal(SolverKind.DistributionSweep, SolverChoice.Parse(" sweep "));
        foreach (var o in SolverChoice.Options) Assert.Equal(o.Name, SolverChoice.NameOf(o.Kind));
        Assert.Equal(new[] { "auto", "dense", "sparse", "sweep" }, SolverChoice.Options.Select(o => o.Name));
        var ex = Assert.Throws<ArgumentException>(() => SolverChoice.Parse("gpu"));
        Assert.Contains("Unknown solver 'gpu'. Use one of: auto, dense, sparse, sweep.", ex.Message, StringComparison.Ordinal);
    }

    private static SessionManager Manager() => new(
        new CaseCatalog(AppContext.BaseDirectory),
        new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["Cycle:Enabled"] = "false" }).Build());

    [Fact]
    public async Task A_live_session_runs_on_the_chosen_engine()
    {
        SessionManager m = Manager();
        await using (SimSession auto = m.Create("case9"))
        {
            Assert.Equal(SolverKind.Auto, auto.RequestedSolver);
            Assert.Equal("Newton-Raphson (dense)", auto.ActiveSolver);   // Auto on a 9-bus mesh, unchanged
        }
        await using (SimSession sparse = m.Create("case9", SolverKind.SparseNewton))
        {
            Assert.Equal(SolverKind.SparseNewton, sparse.RequestedSolver);
            Assert.Equal("Newton-Raphson (sparse)", sparse.ActiveSolver);
        }
    }

    [Fact]
    public void A_solver_is_refused_for_a_bridge_feed()
    {
        // A bridge (or replay) feed runs no solver here: asking for one must not be silently ignored. The refusal
        // comes before the bridge-endpoint check, so it holds with or without a bridge configured.
        var ex = Assert.Throws<ArgumentException>(() => Manager().Create("bridge:gb-full@presolved", SolverKind.DenseNewton));
        Assert.Contains("runs no solver here, so solver 'dense' cannot be chosen", ex.Message, StringComparison.Ordinal);
    }

    // ---- bridge overlays and HUD ---------------------------------------------------------------------------

    [Fact]
    public void Bridge_overlays_keep_finite_entries_and_drop_the_rest()
    {
        Assert.Null(MapProjection.BuildOverlays(null));
        Assert.Null(MapProjection.BuildOverlays(new BridgeProtocol.OverlaysBlock()));
        Assert.Null(MapProjection.BuildOverlays(new BridgeProtocol.OverlaysBlock
        {
            StressByBranch = new() { ["L1"] = double.NaN },
            ConditionByBus = new() { [3] = double.PositiveInfinity },
        }));

        MapOverlaysDto? ov = MapProjection.BuildOverlays(new BridgeProtocol.OverlaysBlock
        {
            StressByBranch = new() { ["L1"] = 0.25, ["L2"] = double.NaN, ["L3"] = 1.5 },
            ConditionByBus = new() { [1] = 0.75, [2] = double.NegativeInfinity },
        });
        Assert.NotNull(ov);
        Assert.Equal(new Dictionary<string, double> { ["L1"] = 0.25, ["L3"] = 1.5 }, ov!.StressByBranch);
        Assert.Equal(new Dictionary<int, double> { [1] = 0.75 }, ov.ConditionByBus);
    }

    [Fact]
    public void Bridge_hud_carries_condition_index_and_rocof_headroom()
    {
        Assert.Null(MapProjection.BuildHud(null));
        Assert.Equal(new MapHudDto(0.42, 0.125),
            MapProjection.BuildHud(new BridgeProtocol.ScalarsBlock { ConditionIndex = 0.42, RocofHeadroomHzS = 0.125 }));
        Assert.Equal(new MapHudDto(null, null),
            MapProjection.BuildHud(new BridgeProtocol.ScalarsBlock { ConditionIndex = double.NaN }));
    }

    [Fact]
    public void Map_hud_and_overlays_default_to_absent_on_the_envelope()
    {
        var env = new FrameEnvelope("state", 0, 0, "00:00", null,
            new HeadlineDto(50, 0, 0, 0, 0, 0, 1, true, 1, 1),
            Array.Empty<NodeStateDto>(), Array.Empty<EdgeStateDto>(), Array.Empty<BoundaryStateDto>());
        Assert.Null(env.MapHud);
        Assert.Null(env.MapOverlays);
    }

    // ---- GDA per-asset rows (Core formatter shared with WPF) -----------------------------------------------

    private static AssetEntry E(params (string K, string V)[] f) => new(f.Select(x => new AssetField(x.K, x.V)).ToList());

    [Fact]
    public void Asset_rows_are_the_desktop_text()
    {
        var asset = new AssetData("T_DRAXX-1", 42,
            Envelope: new[] { E(("bmUnit", "T_DRAXX-1"), ("p_max", "645"), ("p_min_stable_msg", "250"), ("msg_pct_of_pmax", "38.8"), ("psrType", "Biomass")) },
            BessSoc: new[] { E(("unit_id", "B1"), ("rated_power_mw", "50"), ("rated_energy_mwh", "100"), ("duration_hours", "2"), ("tech", "Li-ion"), ("source", "REPD")) },
            TapWear: new[] { E(("gsp", "DRAX"), ("sgt", "SGT1"), ("vnom_kv", "132"), ("is_oltc", "yes"), ("step_pct", "1.25"), ("n_visible_steps", "7"), ("span_steps", "19"), ("ltds_tap_span_pct", "10")) },
            Consumers: new[] { E(("name", "Works"), ("type", "industrial"), ("import_mw", "12"), ("energy_tech", "CHP")) });

        IReadOnlyList<AssetInsightFormatter.Row> rows = AssetInsightFormatter.Rows(asset);
        Assert.Equal(new[]
        {
            new AssetInsightFormatter.Row("GENERATOR ENVELOPE  (MSG / Pmax)", "T_DRAXX-1   Pmax 645 MW", "MSG 250 MW (38.8% of Pmax)   *   Biomass"),
            new AssetInsightFormatter.Row("BATTERY STORAGE  (SoC / ratings)", "B1   50 MW", "Li-ion   *   100 MWh   *   2 h   *   REPD"),
            new AssetInsightFormatter.Row("TAP-CHANGER WEAR", "DRAX SGT1   132 kV", "OLTC   *   step 1.25%   *   7/19 steps used   *   LTDS +/-10%"),
            new AssetInsightFormatter.Row("CONSUMERS / DEMAND PLACEMENT", "Works   (industrial)", "import 12 MW   *   CHP"),
        }, rows);
        Assert.True(rows[0].HasGroup);
        Assert.Empty(AssetInsightFormatter.Rows(null));
    }

    [Fact]
    public void Asset_blocks_cap_at_ten_rows_with_a_count_of_the_rest()
    {
        var many = Enumerable.Range(1, 13).Select(i => E(("name", $"C{i}"), ("type", "t"))).ToArray();
        var asset = new AssetData(null, 1, Array.Empty<AssetEntry>(), Array.Empty<AssetEntry>(), Array.Empty<AssetEntry>(), many);
        IReadOnlyList<AssetInsightFormatter.Row> rows = AssetInsightFormatter.Rows(asset);
        Assert.Equal(11, rows.Count);
        Assert.Equal("CONSUMERS / DEMAND PLACEMENT", rows[0].Group);
        Assert.All(rows.Skip(1), r => Assert.False(r.HasGroup));
        Assert.Equal(new AssetInsightFormatter.Row("", "...and 3 more", ""), rows[10]);
    }

    [Fact]
    public void Wpf_substation_page_reads_the_shared_formatter()
    {
        // The desktop page keeps no private copy of the row text (it would drift from the web copy).
        // Test retargeted 2026-10-20 (UI programme ASSETS A2): src/GridSim.Wpf/Views/Assets/SubstationPages.cs moved to
        // src/GridSim.Ui/Assets/Pages/SubstationPages.cs; assertions unchanged, and strengthened: no file anywhere under
        // src/GridSim.Ui/Assets keeps a private row text, nor does the WPF page left behind, which must derive from the
        // moved model (so the bridge's ApplyAsset it inherits is the one that reads the formatter).
        string src = File.ReadAllText(Path.Combine(RepoRoot(), "src", "GridSim.Ui", "Assets", "Pages", "SubstationPages.cs"));
        Assert.Contains("AssetInsightFormatter.Rows(asset)", src, StringComparison.Ordinal);
        Assert.DoesNotContain("private static (string, string) EnvelopeRow", src, StringComparison.Ordinal);
        foreach (string file in Directory.GetFiles(Path.Combine(RepoRoot(), "src", "GridSim.Ui", "Assets"), "*.cs", SearchOption.AllDirectories))
            Assert.DoesNotContain("EnvelopeRow", File.ReadAllText(file), StringComparison.Ordinal);
        string wpf = File.ReadAllText(Path.Combine(RepoRoot(), "src", "GridSim.Wpf", "Views", "Assets", "SubstationPages.cs"));
        Assert.DoesNotContain("EnvelopeRow", wpf, StringComparison.Ordinal);
        Assert.Contains("    : SubstationDetailModel<PlantRow, ConnectionRow>(busId, explorer)", wpf, StringComparison.Ordinal);
    }

    [Fact]
    public void Web_sources_carry_no_parity_todo_left_by_n063()
    {
        string root = RepoRoot();
        foreach (string rel in new[] { "src/GridSim.Web/Components/Assets/SubstationDetail.razor", "src/GridSim.Web/Components/Visuals/NetworkMap.razor" })
        {
            string text = File.ReadAllText(Path.Combine(root, rel));
            Assert.DoesNotContain("TODO(parity)", text, StringComparison.Ordinal);
            Assert.DoesNotContain("TODO(bridge-hud)", text, StringComparison.Ordinal);
        }
    }

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "GridSim.slnx"))) dir = dir.Parent;
        Assert.NotNull(dir);
        return dir!.FullName;
    }
}
