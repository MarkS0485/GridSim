using System.Text.RegularExpressions;
using Xunit;

namespace GridSim.Tests;

/// <summary>
/// Source-level anti-drift checks for the WPF presentation fixes of the October 2026 review (the test project does not
/// reference GridSim.Wpf, so these read src/GridSim.Wpf in the style of <see cref="CliExitCodeContractTests"/>). Each
/// fails if a later edit quietly undoes the fix.
/// </summary>
public class WpfPresentationSourceContractTests
{
    private static string Wpf(string file) => PresentationConstantsAntiDriftTests.Source("src", "GridSim.Wpf", file);

    /// <summary>The platform-neutral dashboard the WPF view-model now derives from (UI programme 0.2).</summary>
    private static string UiDashboard() => PresentationConstantsAntiDriftTests.Source("src", "GridSim.Ui", "Dashboard", "DashboardModel.cs");

    /// <summary>The body of method <paramref name="name"/> (from its signature to the next member at the same indent).</summary>
    private static string MethodBody(string src, string signatureFragment)
    {
        int at = src.IndexOf(signatureFragment, StringComparison.Ordinal);
        Assert.True(at >= 0, $"method not found: {signatureFragment}");
        int open = src.IndexOf('{', at);
        int depth = 0;
        for (int i = open; i < src.Length; i++)
        {
            if (src[i] == '{') depth++;
            else if (src[i] == '}' && --depth == 0) return src.Substring(at, i - at + 1);
        }
        throw new InvalidOperationException("unbalanced braces");
    }

    // ---------------- R051 ----------------

    [Fact]
    public void R051_wpf_dashboard_reads_Converged_and_shows_NOT_CONVERGED_before_any_other_verdict()
    {
        // Test retargeted 2026-10-20 (UI programme 0.2): the dashboard's Update moved to
        // src/GridSim.Ui/Dashboard/DashboardModel.cs; assertions unchanged, and strengthened: the WPF view-model must
        // derive from that model and must hold no Update and no lamp verdict of its own.
        string update = MethodBody(UiDashboard(), "public void Update(SimFrame f, IReadOnlyList<BoundaryFlow> boundaries)");
        int conv = update.IndexOf("f.Result.Converged", StringComparison.Ordinal);
        int notConv = update.IndexOf("Status = \"NOT CONVERGED\"", StringComparison.Ordinal);
        int normal = update.IndexOf("Status = \"SYSTEM NORMAL\"", StringComparison.Ordinal);
        Assert.True(conv >= 0, "Update must read f.Result.Converged");
        Assert.True(notConv > conv, "NOT CONVERGED must be set from the converged flag");
        Assert.True(normal > notConv, "SYSTEM NORMAL must only be reachable after the NOT CONVERGED check");
        // SYSTEM NORMAL sits in the else-branch of the !converged test, not beside it.
        Assert.Matches(new Regex(@"if \(!converged\)\s*\{[^}]*NOT CONVERGED[^}]*\}\s*else if \(!FreqLocked\)"), update);
        Assert.Contains("POWER FLOW DID NOT CONVERGE", update);   // the alarm on onset
        string wpf = Wpf("DashboardViewModel.cs");
        Assert.Contains("public sealed class DashboardViewModel : DashboardModel", wpf);
        Assert.DoesNotContain("public void Update(SimFrame f", wpf);
        Assert.DoesNotContain("\"SYSTEM NORMAL\"", wpf);
        Assert.DoesNotContain("\"NOT CONVERGED\"", wpf);
    }

    [Fact]
    public void R209_torque_uses_the_live_frequency_not_3183()
    {
        // Test retargeted 2026-10-20 (UI programme 0.2): the torque read-out moved to
        // src/GridSim.Ui/Dashboard/DashboardModel.cs; assertions unchanged, and strengthened: the WPF file must not carry a
        // 3183 factor either.
        string src = UiDashboard();
        Assert.DoesNotContain("3183.0", src.Replace("3183.0989", ""));
        Assert.Contains("Torque = FormatTorqueMnm(total, freqHz);", src);
        Assert.Contains("double omegaRadPerS = 2.0 * Math.PI * freqHz;", src);
        Assert.DoesNotContain("3183.0", Wpf("DashboardViewModel.cs").Replace("3183.0989", ""));
    }

    // ---------------- R020 ----------------

    [Fact]
    public void R020_experimental_tab_only_computes_while_it_is_showing_and_guards_the_wave_model()
    {
        // Test retargeted 2026-10-20 (UI programme 1.6): src/GridSim.Wpf/Views/ExperimentalVm.cs moved to
        // src/GridSim.Ui/Tabs/Experimental/ExperimentalModel.cs; the assertions on Update and Refresh read it there,
        // unchanged; the MainWindow assertions are unchanged. Strengthened: the WPF view-model must derive from the model
        // and may not override or bypass the gate (no Update or Active of its own).
        string vm = PresentationConstantsAntiDriftTests.Source("src", "GridSim.Ui", "Tabs", "Experimental", "ExperimentalModel.cs");
        string adapter = Wpf("Views/ExperimentalVm.cs");
        Assert.Contains("public sealed class ExperimentalVm : ExperimentalModel", adapter);
        Assert.DoesNotContain("void Update(", adapter);
        Assert.DoesNotContain("bool Active", adapter);
        string update = MethodBody(vm, "public void Update(SimFrame f)");
        Assert.Contains("_pending = f;", update);
        Assert.Contains("if (_active) Refresh(f);", update);
        Assert.DoesNotContain("PhysicsLaws.Audit", update);
        Assert.DoesNotContain("FromSolvedCase", update);

        string refresh = MethodBody(vm, "private void Refresh(SimFrame f)");
        int guard = refresh.IndexOf("WaveModelCanForm(model, r)", StringComparison.Ordinal);
        int call = refresh.IndexOf("ContinuumWaveModel.FromSolvedCase", StringComparison.Ordinal);
        Assert.True(guard >= 0 && call > guard, "the wave model must be guarded by WaveModelCanForm");

        Assert.Contains("_vm.Experimental.Active = tc.SelectedItem is System.Windows.Controls.TabItem { Header: \"EXPERIMENTAL\" };",
            Wpf("MainWindow.xaml.cs"));
        Assert.Contains("<TabItem Header=\"EXPERIMENTAL\"", Wpf("MainWindow.xaml"));
    }

    [Fact]
    public void R020_wpf_guard_is_the_same_predicate_as_the_tested_web_guard()
    {
        // The web FrameProjector.WaveModelCanForm carries the behavioural tests (WaveModelGuardTests); the WPF copy
        // must test exactly the same three conditions.
        string Norm(string s) => Regex.Replace(Regex.Replace(s, @"//[^\n]*", ""), @"\s+", "");
        // Test retargeted 2026-10-20 (UI programme 1.6): src/GridSim.Wpf/Views/ExperimentalVm.cs moved to
        // src/GridSim.Ui/Tabs/Experimental/ExperimentalModel.cs; the comparison is unchanged, and strengthened: the WPF
        // view-model may not declare a second copy of the guard.
        Assert.DoesNotContain("WaveModelCanForm", Wpf("Views/ExperimentalVm.cs"));
        string wpf = Norm(MethodBody(PresentationConstantsAntiDriftTests.Source("src", "GridSim.Ui", "Tabs", "Experimental", "ExperimentalModel.cs"),
            "public static bool WaveModelCanForm"));
        string web = Norm(MethodBody(PresentationConstantsAntiDriftTests.Source("src", "GridSim.Web", "Projection", "FrameProjector.cs"),
            "public static bool WaveModelCanForm"));
        Assert.Equal(web, wpf);
    }

    // ---------------- R112 ----------------

    [Fact]
    public void R112_fleet_pages_use_per_refresh_lookup_tables_not_linear_scans()
    {
        // Test retargeted 2026-10-20 (UI programme ASSETS A2): src/GridSim.Wpf/Views/Assets/TransformerPages.cs moved to
        // src/GridSim.Ui/Assets/Pages/TransformerPages.cs, where the fleet page is the class TransformerFleetModel<TRow>
        // (the WPF TransformerFleetVm derives from it): the fleet body is found by that class name, every assertion below
        // is otherwise unchanged; strengthened: the WPF file left behind may hold no bus lookup or scan at all, and the
        // first-wins tables are checked by behaviour too (UiAssetPagesParityTests compares every fleet row on gb-synthetic).
        string wpfTx = Wpf("Views/Assets/TransformerPages.cs");
        Assert.DoesNotContain("TransformerInfo", wpfTx);
        Assert.DoesNotContain("FirstOrDefault", wpfTx);
        Assert.DoesNotContain("BusIds", wpfTx);
        string src = PresentationConstantsAntiDriftTests.Source("src", "GridSim.Ui", "Assets", "Pages", "TransformerPages.cs");
        string fleet = MethodBody(src, "public class TransformerFleetModel<TRow>");
        Assert.Contains("TransformerInfo.BusById(m)", fleet);
        Assert.DoesNotContain("TransformerInfo.Band(m,", fleet);
        Assert.DoesNotContain("FirstOrDefault", fleet);

        string tapFleetStart = src[src.IndexOf("int inBand = 0; double targetSum = 0;", StringComparison.Ordinal)..];
        string tapFleet = tapFleetStart[..tapFleetStart.IndexOf("InBand =", StringComparison.Ordinal)];
        Assert.Contains("TransformerInfo.Vpu(r, resultIdx, o.ControlsBus)", tapFleet);
        Assert.DoesNotContain("TransformerInfo.NameOf(m,", tapFleet);
        // The tables keep the FIRST bus per id, the same bus FirstOrDefault returned.
        Assert.Contains("byId.TryAdd(b.Id, b);", src);
        // Test retargeted 2026-10-20 (ASSETS fix S3, Mark approved): the result-index table moved, unchanged, into the one
        // shared per-frame lookup every ASSETS page now uses (src/GridSim.Ui/Assets/Pages/FrameLookup.cs), so the
        // assertion "idx.TryAdd(r.BusIds[i], i);" in TransformerPages.cs became: ResultIndexById is exactly a call of the
        // shared lookup, and the shared lookup holds the same first-wins TryAdd lines (for the index and for the values).
        // Strengthened: no page under GridSim.Ui/Assets/Pages builds a last-wins table from the result's bus ids (the
        // indexer assignment the substation fleet used), and the first-wins rule is also checked by behaviour
        // (UiAssetFixesTests.S3_the_shared_lookup_keeps_the_first_entry_for_a_repeated_id).
        Assert.Contains("public static Dictionary<int, int> ResultIndexById(PowerFlowResult r) => FrameLookup.IndexByBusId(r.BusIds);", src);
        string lookup = PresentationConstantsAntiDriftTests.Source("src", "GridSim.Ui", "Assets", "Pages", "FrameLookup.cs");
        Assert.Contains("idx.TryAdd(busIds[i], i);", lookup);
        Assert.Contains("byId.TryAdd(busIds[i], values[i]);", lookup);
        foreach (string page in new[] { "AssetPages.cs", "GeneratorPages.cs", "TransformerPages.cs", "CircuitPages.cs", "SubstationPages.cs" })
        {
            // Code only: the comments that describe the old last-wins line are stripped first.
            string text = Regex.Replace(PresentationConstantsAntiDriftTests.Source("src", "GridSim.Ui", "Assets", "Pages", page), @"//[^\n]*", "");
            Assert.False(Regex.IsMatch(text, @"\[\s*\w+\.BusIds\[\w+\]\s*\]\s*=[^=]"), $"{page}: a last-wins table built from the result's bus ids");
        }
    }

    // ---------------- R133 ----------------

    [Fact]
    public void R133_labels_are_laid_out_once_per_view_not_every_render()
    {
        string src = Wpf("Visuals/FastSchematic.cs");
        string draw = MethodBody(src, "private void DrawLabels(DrawingContext dc)");
        Assert.DoesNotContain("new FormattedText", draw);
        Assert.Contains("dc.DrawDrawing(_labelDrawing);", draw);
        string rebuild = MethodBody(src, "private void RebuildStatic()");
        Assert.Contains("_labelDrawing = null;", rebuild);
        string load = MethodBody(src, "public void Load(GridModel model)");
        Assert.Contains("_labelDrawing = null;", load);
        Assert.Contains("group.Freeze();", MethodBody(src, "private DrawingGroup BuildLabelDrawing(double pps)"));
    }

    // ---------------- N016 ----------------

    [Fact]
    public void N016_wpf_fuels_delegate_to_the_core_taxonomy()
    {
        string src = Wpf("Visuals/Fuels.cs");
        Assert.Contains("FuelTaxonomy.Classify(type)", src);
        // No keyword table of its own any more.
        Assert.DoesNotContain("Has(\"nuclear\")", src);
        Assert.DoesNotContain("\"#7E57C2\"", src);
    }

    // ---------------- R210 ----------------

    [Fact]
    public void R210_bridge_back_summary_matches_the_default()
    {
        string src = Wpf("MainWindow.xaml.cs");
        Assert.DoesNotContain("defaults to 2 days", src);
        Assert.Contains("private static readonly TimeSpan DefaultBridgeBack = TimeSpan.FromDays(9);", src);
        Assert.Contains("BridgeStartPicker.SelectedItem is BridgeStart s ? s.Back : DefaultBridgeBack;", src);
    }

    // ---------------- R111 (web map, also source-level: the test project cannot run JavaScript) ----------------

    [Fact]
    public void R111_web_map_replays_cached_layers_and_caps_the_animation_rate()
    {
        string js = PresentationConstantsAntiDriftTests.Source("src", "GridSim.Web", "wwwroot", "js", "map-interop.js");
        string draw = MethodBody(js, "function draw(m)");
        Assert.Contains("if (!layersFresh(m)) buildLayers(", draw);
        Assert.DoesNotContain("new Set(", draw);
        Assert.DoesNotContain("new Path2D(", draw);
        // Every input the cached layers depend on invalidates them.
        Assert.Contains("k.ver === m.stateVer", js);
        Assert.Equal(3, Regex.Count(js, @"m\.stateVer\+\+;"));   // setTopology, setState, setOverlays
        string tick = MethodBody(js, "function tick(m, ts)");
        Assert.Contains("ts - m.lastT >= ANIM_MIN_INTERVAL_MS", tick);
    }
}
