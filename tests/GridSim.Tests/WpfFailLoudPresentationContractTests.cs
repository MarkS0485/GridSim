using System.Text.RegularExpressions;
using Xunit;

namespace GridSim.Tests;

/// <summary>
/// Source contracts for the desktop app's fail-loud and identity rules. The test project does not reference
/// GridSim.Wpf (a Windows-only WPF assembly), so, like <see cref="WpfPresentationSourceContractTests"/>, these read the
/// source and fail if a later edit brings back a pattern that showed an invalid state as valid, keyed a row by a name
/// that repeats, or printed a wrong number.
/// </summary>
public class WpfFailLoudPresentationContractTests
{
    private static string Wpf(string file) => PresentationConstantsAntiDriftTests.Source("src", "GridSim.Wpf", file);

    /// <summary>The text of the member whose declaration contains <paramref name="signature"/>, brace-matched.</summary>
    private static string Body(string src, string signature)
    {
        int at = src.IndexOf(signature, StringComparison.Ordinal);
        Assert.True(at >= 0, $"not found: {signature}");
        int open = src.IndexOf('{', at);
        int depth = 0;
        for (int i = open; i < src.Length; i++)
        {
            if (src[i] == '{') depth++;
            else if (src[i] == '}' && --depth == 0) return src.Substring(at, i - at + 1);
        }
        throw new InvalidOperationException("unbalanced braces");
    }

    [Fact]
    public void A_settings_file_that_could_not_be_read_is_reported_and_never_overwritten()
    {
        // Test retargeted 2026-10-20 (UI programme 0.2): AppSettings.cs moved to src/GridSim.Ui/Settings/AppSettings.cs
        // (same class, same settings file, still loaded by the window); assertions unchanged.
        string src = PresentationConstantsAntiDriftTests.Source("src", "GridSim.Ui", "Settings", "AppSettings.cs");
        Assert.DoesNotContain("catch { /* fall through to defaults */ }", src, StringComparison.Ordinal);
        string save = Body(src, "public void Save()");
        int guard = save.IndexOf("if (LoadProblem is not null)", StringComparison.Ordinal);
        int write = save.IndexOf("File.WriteAllText(", StringComparison.Ordinal);
        Assert.True(guard >= 0 && write > guard, "Save must refuse to write before it writes");
        Assert.Contains("_settings.LoadProblem is { } settingsProblem", Wpf("MainWindow.xaml.cs"), StringComparison.Ordinal);
    }

    [Fact]
    public void A_non_converged_frame_is_marked_invalid_on_the_SCADA_register_and_every_tab()
    {
        // Test retargeted 2026-10-20 (UI programme 1.6): src/GridSim.Wpf/Views/ScadaVm.cs moved to
        // src/GridSim.Ui/Tabs/Scada/ScadaModel.cs; assertions unchanged, and strengthened: the WPF view-model must derive
        // from that model and set no point quality of its own.
        string scada = PresentationConstantsAntiDriftTests.Source("src", "GridSim.Ui", "Tabs", "Scada", "ScadaModel.cs");
        Assert.Contains("bool solveValid = r.Converged;", scada, StringComparison.Ordinal);
        Assert.Contains("pt.Quality = \"INVALID\";", scada, StringComparison.Ordinal);
        Assert.Contains("POWER FLOW DID NOT CONVERGE", scada, StringComparison.Ordinal);
        string scadaAdapter = Wpf("Views/ScadaVm.cs");
        Assert.Contains("public sealed class ScadaVm : ScadaModel", scadaAdapter, StringComparison.Ordinal);
        Assert.DoesNotContain("Quality = \"", scadaAdapter, StringComparison.Ordinal);

        // Test retargeted 2026-10-20 (UI programme 0.2): the dashboard's Update moved to
        // src/GridSim.Ui/Dashboard/DashboardModel.cs, where the WPF Visibility is a bool; strengthened: the bool
        // expression is pinned there AND the WPF adapter that turns it back into the bound Visibility (true = Visible,
        // false = Collapsed), its change notification and the two XAML bindings are pinned in the WPF app.
        string dash = PresentationConstantsAntiDriftTests.Source("src", "GridSim.Ui", "Dashboard", "DashboardModel.cs");
        string update = Body(dash, "public void Update(SimFrame f, IReadOnlyList<BoundaryFlow> boundaries)");
        Assert.Contains("bool converged = f.Result.Converged;", update, StringComparison.Ordinal);
        Assert.Contains("InvalidState = !converged;", update, StringComparison.Ordinal);
        Assert.True(update.IndexOf("bool converged = f.Result.Converged;", StringComparison.Ordinal)
            < update.IndexOf("InvalidState = !converged;", StringComparison.Ordinal), "the flag is set from this frame's converged state");
        Assert.Contains("InvalidState = false;   // the next frame decides again", Body(dash, "public void ResetTimeline()"), StringComparison.Ordinal);
        string wpf = Wpf("DashboardViewModel.cs");
        Assert.Contains("public Visibility InvalidStateVis => InvalidState ? Visibility.Visible : Visibility.Collapsed;", wpf, StringComparison.Ordinal);
        Assert.Contains("[nameof(InvalidState)] = nameof(InvalidStateVis),", wpf, StringComparison.Ordinal);
        Assert.Equal(2, Regex.Count(Wpf("MainWindow.xaml"), @"Visibility=""\{Binding InvalidStateVis\}"""));   // map veil + tab banner
    }

    [Fact]
    public void A_bridge_what_if_that_did_not_converge_is_never_drawn_and_a_converged_one_goes_through_the_full_frame_path()
    {
        string run = Body(Wpf("MainWindow.xaml.cs"), "private async void RunQuery(string op, object @params, string title)");
        int check = run.IndexOf("if (!snap.Result.Converged)", StringComparison.Ordinal);
        int render = run.IndexOf("RenderReplayFrame(ToWhatIfFrame(snap))", StringComparison.Ordinal);
        Assert.True(check >= 0 && render > check, "the convergence test must come before the what-if is rendered");
        Assert.DoesNotContain("_renderer.SetState(snap.Result);", run, StringComparison.Ordinal);
    }

    [Fact]
    public void A_frame_that_fails_to_render_never_becomes_the_current_frame()
    {
        string render = Body(Wpf("MainWindow.xaml.cs"), "private bool RenderReplayFrame(SimFrame f)");
        int boundaries = render.IndexOf("_renderer.SetBoundaries(boundaries);", StringComparison.Ordinal);
        int last = render.IndexOf("_last = f;", StringComparison.Ordinal);
        Assert.True(boundaries >= 0 && last > boundaries, "_last is assigned only after the views accepted the frame");
    }

    [Fact]
    public void The_experimental_tab_shows_a_model_failure_and_guards_the_wave_model_on_large_cases()
    {
        // Test retargeted 2026-10-20 (UI programme 1.6): src/GridSim.Wpf/Views/ExperimentalVm.cs moved to
        // src/GridSim.Ui/Tabs/Experimental/ExperimentalModel.cs; assertions unchanged, and strengthened: the WPF
        // view-model must derive from that model and may not refresh, compute or rethrow anything itself.
        string vm = PresentationConstantsAntiDriftTests.Source("src", "GridSim.Ui", "Tabs", "Experimental", "ExperimentalModel.cs");
        string adapter = Wpf("Views/ExperimentalVm.cs");
        Assert.Contains("public sealed class ExperimentalVm : ExperimentalModel", adapter, StringComparison.Ordinal);
        foreach (string own in new[] { "void Refresh(", "Compute(", ".Throw();", "ContinuumWaveModel." })
            Assert.DoesNotContain(own, adapter, StringComparison.Ordinal);
        string refresh = Body(vm, "private void Refresh(SimFrame f)");
        Assert.Contains("ShowModelFailure(", refresh, StringComparison.Ordinal);
        Assert.DoesNotContain(".Throw();", refresh, StringComparison.Ordinal);   // a throw here faulted an unobserved task
        string compute = Body(vm, "static Computed Compute(SimFrame frame)");
        int guard = compute.IndexOf("bool waveSkipped = model.BusCount > HeavyModelBusLimit;", StringComparison.Ordinal);
        int wave = compute.IndexOf("ContinuumWaveModel.FromSolvedCase(model, r)", StringComparison.Ordinal);
        Assert.True(guard >= 0 && wave > guard, "the bus-count guard must precede the wave model");
    }

    [Fact]
    public void An_overloaded_circuit_always_draws_in_the_red_bucket()
    {
        string map = Wpf("Visuals/FastSchematic.cs");
        // Test retargeted 2026-10-20 (UI programme 0.2): the map's colour-bin rule LoadingBucketOf moved to
        // src/GridSim.Ui/Schematic/SchematicView.cs (now public there, for the ports); its assertions are unchanged;
        // strengthened: FastSchematic may not declare a LoadingBucketOf of its own, and SchematicView.ApplyState must
        // bucket every rated branch through it (an unrated one is capped below the red bucket, as before). The
        // de-duplication checks below still read FastSchematic, where the drawing stayed.
        string view = PresentationConstantsAntiDriftTests.Source("src", "GridSim.Ui", "Schematic", "SchematicView.cs");
        string bucket = Body(view, "public static int LoadingBucketOf(double frac)");
        Assert.Contains("if (!(frac < 1.0)) return LoadingBuckets - 1;", bucket, StringComparison.Ordinal);
        Assert.DoesNotContain("+ 0.5", bucket, StringComparison.Ordinal);   // no rounding across the 1.0 edge
        Assert.DoesNotContain("int LoadingBucketOf(", map, StringComparison.Ordinal);
        Assert.Contains("e.LoadBucket = lf.RatingMva > 0 ? LoadingBucketOf(frac) : Math.Min(LoadingBucketOf(frac), LoadingBuckets - 2);",
            Body(view, "public bool ApplyState(PowerFlowResult result)"), StringComparison.Ordinal);
        // De-duplicated corridors and roundels take their worst member, not the first.
        Assert.DoesNotContain("first edge on a segment wins its colour", map, StringComparison.Ordinal);
        Assert.Contains("BucketSeverity(bkt) > BucketSeverity(drawn.Bucket)", map, StringComparison.Ordinal);
        Assert.Contains("VoltSeverity(ni) > VoltSeverity(shown.NodeIndex)", map, StringComparison.Ordinal);
    }

    [Fact]
    public void Transformer_bushings_show_the_phase_voltage_without_an_invented_imbalance()
    {
        // Test retargeted 2026-10-20 (UI programme ASSETS A1): the drawing of src/GridSim.Wpf/Visuals/TransformerSchematic.cs
        // moved to src/GridSim.Ui/Visuals/TransformerSchematicGeometry.cs (the WPF control now only replays its calls).
        // The "/ Math.Sqrt(3.0)" assertion moves with the code; the two "no invented imbalance" assertions are kept on
        // the WPF file and applied to the geometry too; strengthened: the badges are checked by behaviour as well (a
        // 400 kV unit prints 230.9 kV on each of the three HV bushings, never the old V_LL / 3 figure of 133.3 kV, and
        // the nominal phase voltage before the first frame).
        string tx = Wpf("Visuals/TransformerSchematic.cs");
        string geo = PresentationConstantsAntiDriftTests.Source("src", "GridSim.Ui", "Visuals", "TransformerSchematicGeometry.cs");
        Assert.Contains("/ Math.Sqrt(3.0)", geo, StringComparison.Ordinal);
        foreach (string src in new[] { tx, geo })
        {
            Assert.DoesNotContain("0.003", src, StringComparison.Ordinal);
            Assert.DoesNotContain("HvKvLive / 3 ", src, StringComparison.Ordinal);
        }
        System.Globalization.CultureInfo saved = System.Globalization.CultureInfo.CurrentCulture;
        try
        {
            System.Globalization.CultureInfo.CurrentCulture = System.Globalization.CultureInfo.InvariantCulture;
            List<string> Badges(GridSim.Ui.Visuals.TransformerSchematicInput input) =>
                GridSim.Ui.Visuals.TransformerSchematicGeometry.Compute(920, 485, input)!.Commands
                    .OfType<GridSim.Ui.Visuals.VisualTextCommand>().Select(t => t.Text).Where(t => t.EndsWith(" kV", StringComparison.Ordinal)).ToList();
            List<string> live = Badges(new GridSim.Ui.Visuals.TransformerSchematicInput { HvKvNom = 400, HvKvLive = 400, LvKvNom = 132, LvKvLive = 132 });
            Assert.Equal(3, live.Count(t => t == "230.9 kV"));
            Assert.Equal(3, live.Count(t => t == "76.2 kV"));
            Assert.DoesNotContain("133.3 kV", live);
            List<string> before = Badges(new GridSim.Ui.Visuals.TransformerSchematicInput { HvKvNom = 400, HvKvLive = 0, LvKvNom = 132 });
            Assert.Equal(3, before.Count(t => t == "230.9 kV"));
            Assert.DoesNotContain("400.0 kV", before);
        }
        finally { System.Globalization.CultureInfo.CurrentCulture = saved; }
    }

    [Fact]
    public void Generator_page_carbon_reads_the_one_carbon_table()
    {
        // Test retargeted 2026-10-20 (UI programme ASSETS A2): src/GridSim.Wpf/Views/Assets/GeneratorPages.cs moved to
        // src/GridSim.Ui/Assets/Pages/GeneratorPages.cs; assertions unchanged, and strengthened: the literals are refused
        // in the WPF file left behind too, and the avoided-carbon benchmark must be the table's CCGT figure.
        string gen = PresentationConstantsAntiDriftTests.Source("src", "GridSim.Ui", "Assets", "Pages", "GeneratorPages.cs");
        Assert.Contains("CarbonFactors.CombustionTonnesPerMwh(fi.Name)", gen, StringComparison.Ordinal);
        Assert.Contains("const double ccgtCo2PerMwh = CarbonIntensityTable.CcgtTPerMwh;", gen, StringComparison.Ordinal);
        foreach (string file in new[] { gen, Wpf("Views/Assets/GeneratorPages.cs") })
        {
            Assert.DoesNotContain("ccgtCo2PerMwh = 0.35", file, StringComparison.Ordinal);
            Assert.DoesNotContain("mw * 0.49", file, StringComparison.Ordinal);
            Assert.DoesNotContain("mw * 0.95", file, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void Rows_and_lookups_are_keyed_by_index_not_by_a_name_that_repeats()
    {
        Assert.DoesNotContain("flowByName", Wpf("Views/Assets/AssetPages.cs"), StringComparison.Ordinal);
        // Test retargeted 2026-10-20 (UI programme ASSETS A2): src/GridSim.Wpf/Views/Assets/AssetPages.cs and
        // TransformerPages.cs moved to src/GridSim.Ui/Assets/Pages/AssetPages.cs and TransformerPages.cs; the assertions on
        // the WPF files are kept (unchanged), and strengthened: every file under src/GridSim.Ui/Assets is checked for the
        // same name-keyed lookups, and the moved pages must read flows by index and OLTCs through the solver's mapping.
        string uiAssets = string.Concat(Directory.GetFiles(Path.Combine(PresentationConstantsAntiDriftTests.RepoRoot(), "src", "GridSim.Ui", "Assets"), "*.cs", SearchOption.AllDirectories)
            .OrderBy(p => p, StringComparer.Ordinal).Select(File.ReadAllText));
        Assert.DoesNotContain("flowByName", uiAssets, StringComparison.Ordinal);
        Assert.DoesNotContain("oltcByTx", uiAssets, StringComparison.Ordinal);
        Assert.DoesNotContain("m.Oltcs.FirstOrDefault(o => o.Transformer == name)", uiAssets, StringComparison.Ordinal);
        Assert.Contains("LineFlow? f = AssetLookup.FlowAt(r, bi);", PresentationConstantsAntiDriftTests.Source("src", "GridSim.Ui", "Assets", "Pages", "AssetPages.cs"), StringComparison.Ordinal);
        Assert.Contains("int[] map = SteadyStateSolver.MapOltcRecordsToBranches(m);", PresentationConstantsAntiDriftTests.Source("src", "GridSim.Ui", "Assets", "Pages", "TransformerPages.cs"), StringComparison.Ordinal);
        // Test retargeted 2026-10-20 (UI programme 1.6): src/GridSim.Wpf/Views/HumanScaleVm.cs moved to
        // src/GridSim.Ui/Tabs/HumanScale/HumanScaleModel.cs; the assertion is kept on the WPF file (unchanged) and
        // strengthened: the model it now derives from must not key by name either, and must key its rows by
        // "index:bus:name" as the WPF code did.
        Assert.DoesNotContain("_machineByName", Wpf("Views/HumanScaleVm.cs"), StringComparison.Ordinal);
        string humanScale = PresentationConstantsAntiDriftTests.Source("src", "GridSim.Ui", "Tabs", "HumanScale", "HumanScaleModel.cs");
        Assert.DoesNotContain("_machineByName", humanScale, StringComparison.Ordinal);
        Assert.Contains("mach.Add(($\"{gi}:{g.Bus}:{g.Name}\"", humanScale, StringComparison.Ordinal);
        // Test retargeted 2026-10-20 (UI programme 1.6): Views/MachineVm.cs moved to
        // src/GridSim.Ui/Tabs/Machine/MachineModel.cs; assertions unchanged; strengthened: the WPF adapter left behind is
        // checked too, so neither file may key rows by a name.
        string machine = PresentationConstantsAntiDriftTests.Source("src", "GridSim.Ui", "Tabs", "Machine", "MachineModel.cs");
        Assert.DoesNotContain("branchByName", machine, StringComparison.Ordinal);
        Assert.Contains("SteadyStateSolver.MapOltcRecordsToBranches(m)", machine, StringComparison.Ordinal);
        Assert.Contains("Row($\"bus:{b.Id}\"", machine, StringComparison.Ordinal);
        Assert.DoesNotContain("branchByName", Wpf("Views/MachineVm.cs"), StringComparison.Ordinal);
        string tx = Wpf("Views/Assets/TransformerPages.cs");
        Assert.DoesNotContain("oltcByTx", tx, StringComparison.Ordinal);
        Assert.DoesNotContain("m.Oltcs.FirstOrDefault(o => o.Transformer == name)", tx, StringComparison.Ordinal);
        // Test retargeted 2026-10-20 (UI programme 1.6): src/GridSim.Wpf/Views/ScadaVm.cs moved to
        // src/GridSim.Ui/Tabs/Scada/ScadaModel.cs; assertion unchanged, and strengthened: the WPF view-model must derive
        // from that model and map no OLTC itself.
        Assert.Contains("SteadyStateSolver.MapOltcRecordsToBranches(m)",
            PresentationConstantsAntiDriftTests.Source("src", "GridSim.Ui", "Tabs", "Scada", "ScadaModel.cs"), StringComparison.Ordinal);
        Assert.Contains("public sealed class ScadaVm : ScadaModel", Wpf("Views/ScadaVm.cs"), StringComparison.Ordinal);
        Assert.DoesNotContain("Oltc", Wpf("Views/ScadaVm.cs"), StringComparison.Ordinal);
        // Test retargeted 2026-10-20 (UI programme 0.2): the voltage-band dials moved to
        // src/GridSim.Ui/Dashboard/DashboardModel.cs; assertion unchanged, and strengthened: checked in both files.
        Assert.DoesNotContain("$\"{kv:F0} kV\"", Wpf("DashboardViewModel.cs"), StringComparison.Ordinal);
        Assert.DoesNotContain("$\"{kv:F0} kV\"", PresentationConstantsAntiDriftTests.Source("src", "GridSim.Ui", "Dashboard", "DashboardModel.cs"), StringComparison.Ordinal);
    }

    [Fact]
    public void Shared_thresholds_replace_local_copies_and_edges_match_the_shared_rules()
    {
        string assets = Wpf("Views/Assets/AssetPages.cs");
        Assert.DoesNotContain("icNet > 5", assets, StringComparison.Ordinal);
        Assert.DoesNotContain("g.Pg > 5", assets, StringComparison.Ordinal);
        Assert.DoesNotContain("r.Vm[i] < 0.94", assets, StringComparison.Ordinal);
        // Test retargeted 2026-10-20 (UI programme ASSETS A2): src/GridSim.Wpf/Views/Assets/AssetPages.cs moved to
        // src/GridSim.Ui/Assets/Pages/AssetPages.cs; the assertions on the WPF file are kept (unchanged), and
        // strengthened: the same literals are refused in every file under src/GridSim.Ui/Assets, and the moved landing
        // and interconnector pages must call the shared idle rule and the shared voltage band.
        string uiAssets = string.Concat(Directory.GetFiles(Path.Combine(PresentationConstantsAntiDriftTests.RepoRoot(), "src", "GridSim.Ui", "Assets"), "*.cs", SearchOption.AllDirectories)
            .OrderBy(p => p, StringComparer.Ordinal).Select(File.ReadAllText));
        Assert.DoesNotContain("icNet > 5", uiAssets, StringComparison.Ordinal);
        Assert.DoesNotContain("g.Pg > 5", uiAssets, StringComparison.Ordinal);
        Assert.DoesNotContain("r.Vm[i] < 0.94", uiAssets, StringComparison.Ordinal);
        string uiPages = PresentationConstantsAntiDriftTests.Source("src", "GridSim.Ui", "Assets", "Pages", "AssetPages.cs");
        Assert.Contains("bool icIdle = StatusLampRules.IsIdleInterconnector(icNet);", uiPages, StringComparison.Ordinal);
        Assert.Contains("bool linkIdle = StatusLampRules.IsIdleInterconnector(g.Pg);", uiPages, StringComparison.Ordinal);
        Assert.Contains("if (r.Vm[i] < ScadaThresholds.VoltageBandLowPu || r.Vm[i] > ScadaThresholds.VoltageBandHighPu) outOfBand++;", uiPages, StringComparison.Ordinal);
        // Test retargeted 2026-10-20 (UI programme 1.6): Views/MachineEngineerVm.cs moved to
        // src/GridSim.Ui/Tabs/MachineEngineer/MachineEngineerModel.cs; assertion unchanged; strengthened: the NEAR edge is
        // pinned as AT OR ABOVE too, and the WPF adapter left behind may not grade risk itself.
        string mech = PresentationConstantsAntiDriftTests.Source("src", "GridSim.Ui", "Tabs", "MachineEngineer", "MachineEngineerModel.cs");
        Assert.Contains("stress >= StressLimitFraction", mech, StringComparison.Ordinal);
        Assert.Contains("stress >= StressNearFraction", mech, StringComparison.Ordinal);
        Assert.DoesNotContain("StressLimitFraction", Wpf("Views/MachineEngineerVm.cs"), StringComparison.Ordinal);
        // Test retargeted 2026-10-20 (UI programme 1.6): src/GridSim.Wpf/Views/OperatorVm.cs moved to
        // src/GridSim.Ui/Tabs/Operator/OperatorModel.cs; assertion unchanged, and strengthened: checked in both files,
        // and the shared lamp rule the subtraction form gave way to is pinned in the moved code.
        Assert.DoesNotContain("Math.Abs(devHz) > 0.2", Wpf("Views/OperatorVm.cs"), StringComparison.Ordinal);
        string operatorModel = PresentationConstantsAntiDriftTests.Source("src", "GridSim.Ui", "Tabs", "Operator", "OperatorModel.cs");
        Assert.DoesNotContain("Math.Abs(devHz) > 0.2", operatorModel, StringComparison.Ordinal);
        Assert.Contains("StatusLampRules.IsFrequencyDeviationEvent(f.FrequencyHz, GridSim.Core.Dynamics.FrequencyBands.ForModel(f.Model))",
            operatorModel, StringComparison.Ordinal);
    }
}
