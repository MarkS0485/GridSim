using Xunit;

namespace GridSim.Tests;

/// <summary>
/// Source-level anti-drift checks for the October 2026 docs-programme findings on the interpose, serve and GDA verbs.
/// The test project does not reference the CLI (an executable), so, like <c>CliDocsProgrammeFindingsTests</c>, these
/// read src/GridSim.Cli. The behaviour itself was checked by running the CLI (see NOTES-FOR-MAIN.md); each test names
/// the defect it stops coming back.
/// </summary>
public class CliInterposeServeGdaFindingsTests
{
    private static string Root()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "GridSim.slnx"))) dir = dir.Parent;
        Assert.True(dir is not null, $"GridSim.slnx not found above {AppContext.BaseDirectory}");
        return dir!.FullName;
    }

    private static string Src(params string[] parts)
    {
        string path = Path.Combine(new[] { Root(), "src" }.Concat(parts).ToArray());
        Assert.True(File.Exists(path), $"source not found: {path}");
        return File.ReadAllText(path).Replace("\r\n", "\n");
    }

    /// <summary>The body of a local function: from its signature to the first line that is exactly "}".</summary>
    private static string Body(string src, string signature)
    {
        int at = src.IndexOf(signature, StringComparison.Ordinal);
        Assert.True(at >= 0, $"'{signature}' not found");
        int end = src.IndexOf("\n}\n", at, StringComparison.Ordinal);
        Assert.True(end > at, $"end of '{signature}' not found");
        return src[at..end];
    }

    private static string Interpose() => Body(Src("GridSim.Cli", "Program.cs"), "void RunInterpose(List<string> a, string root)");

    [Fact]
    public void Interpose_handles_a_crosswalk_mismatch_per_window()
    {
        // An InvalidDataException from the mapper during a flush killed the verb (exit -532462766), losing the window.
        string body = Interpose();
        Assert.Contains("catch (InvalidDataException ex)", body);
        Assert.Contains("window discarded (crosswalk mismatch)", body);
        Assert.Contains("if (pipeline.MapErrors > 0) Environment.ExitCode = 3;", body);
        // Both the periodic flush and the tail flush go through the handler: no bare FlushWindow call is left.
        Assert.Single(System.Text.RegularExpressions.Regex.Matches(body, @"pipeline\.FlushWindow\("));
    }

    [Fact]
    public void Interpose_refuses_monitor_with_a_sink()
    {
        // --out-store (and --measurements-dir) were silently ignored under --monitor.
        string body = Interpose();
        Assert.Contains("if (monitor && (measDir is not null || outStoreFlag is not null))", body);
        Assert.DoesNotContain("monitor ? null : FlagString(a, \"--measurements-dir\")", body);
        Assert.DoesNotContain("!monitor && FlagString(a, \"--out-store\")", body);
    }

    [Fact]
    public void Interpose_never_overwrites_a_window_file_and_prints_the_new_counters()
    {
        string body = Interpose();
        Assert.Contains("set.SaveWithoutOverwrite(file)", body);
        Assert.DoesNotContain("set.Save(file)", body);
        Assert.Contains("UnsupportedTypeIds", body);           // R089 counts were never printed
        Assert.Contains("client-resets {transport.ClientResets}", body);
        Assert.Contains("map-errors {pipeline.MapErrors}", body);
        // The store's parts are never overwritten either.
        string store = Src("GridSim.Gda", "Interpose", "QuarantineLiveStore.cs");
        Assert.Contains("NeverOverwrite.CreateNew(file", store);
        Assert.DoesNotContain("File.Create(file)", store);
    }

    [Fact]
    public void The_interpose_comment_no_longer_lists_utc_now()
    {
        // The header comment listed [--utc-now <iso>], which interpose never reads.
        string program = Src("GridSim.Cli", "Program.cs");
        int at = program.IndexOf("// interpose [case] --protocol", StringComparison.Ordinal);
        Assert.True(at >= 0);
        int sig = program.IndexOf("void RunInterpose(", at, StringComparison.Ordinal);
        string header = program[at..sig];
        Assert.DoesNotContain("[--utc-now", header);
    }

    [Fact]
    public void Transport_read_failures_other_than_a_timeout_drop_the_client()
    {
        string t = Src("GridSim.Cli", "InterposeTransport.cs");
        Assert.Contains("catch (IOException ex) when (TransportErrors.IsReadTimeout(ex))", t);
        Assert.DoesNotContain("catch (IOException) { return false; }", t);
    }

    [Fact]
    public void Gda_refuses_an_unknown_gsp_and_never_writes_an_empty_model()
    {
        string body = Body(Src("GridSim.Cli", "Program.cs"), "void RunGda(List<string> a)");
        int check = body.IndexOf("GdaModelBuilder.IsKnownGspGroup(ds, opt.GspGroup)", StringComparison.Ordinal);
        int build = body.IndexOf("GdaModelBuilder.Build(ds, opt)", StringComparison.Ordinal);
        Assert.True(check > 0 && build > check, "the --gsp check must run before the build");
        int empty = body.IndexOf("if (m.BusCount == 0)", StringComparison.Ordinal);
        int write = body.IndexOf("GdaModelWriter.WriteDirectory(m, outDir)", StringComparison.Ordinal);
        int solve = body.IndexOf("a.Contains(\"--solve\")", StringComparison.Ordinal);
        Assert.True(empty > 0 && write > empty && solve > empty, "an empty carve must stop before --out and --solve");
    }

    [Fact]
    public void Gda_all_gsps_builds_each_key_once_and_sets_empty_names_aside()
    {
        string body = Body(Src("GridSim.Cli", "Program.cs"), "void RunGdaAllGsps(LtdsDataset ds, GdaBuildOptions baseOpt)");
        Assert.Contains("GroupBy(GdaModelBuilder.GspGroupKey)", body);
        Assert.Contains("if (m.BusCount == 0) { emptyGroups.Add(g); continue; }", body);
        Assert.DoesNotContain("  (empty)\");", body);   // the old Console.WriteLine of an empty group
    }

    [Fact]
    public void Gda_replay_takes_a_case_directory_and_defaults_under_the_current_directory()
    {
        string body = Body(Src("GridSim.Cli", "Program.cs"), "void RunGdaReplay(List<string> a, string root)");
        Assert.Contains("isCaseDir ? Gated.Dir(caseName) : LoadNamed(caseName, root)", body);
        // Moved 2026-10 (N079, Ruling R1, Mark delegated 2026-10-04): every verb's default output now goes through one
        // helper, DefaultResultsPath ("results/..." relative to the current directory), so the pinned text moved from
        // ?? Path.Combine("results", $"gda-replay-{caseLabel}") to ?? DefaultResultsPath($"gda-replay-{caseLabel}"). The
        // folder is the same (results/gda-replay-<case> under the current directory); the intent of the pin is kept.
        Assert.Contains("?? DefaultResultsPath($\"gda-replay-{caseLabel}\")", body);
        Assert.Contains("Path.Combine(new[] { \"results\" }.Concat(parts).ToArray())", Src("GridSim.Cli", "Program.cs"));
        Assert.DoesNotContain("Path.Combine(root, \"results\"", body);
    }

    [Fact]
    public void Stale_horizon_and_chapter_comments_are_gone()
    {
        Assert.DoesNotContain("manual ch. 78", Src("GridSim.Core", "IO", "BridgeProtocol.cs"));
        Assert.DoesNotContain("25 h horizon", Src("GridSim.Wpf", "MainWindow.xaml.cs"));
        string gda = Body(Src("GridSim.Cli", "Program.cs"), "void RunGda(List<string> a)");
        Assert.DoesNotContain(">=25 h", gda);
        Assert.DoesNotContain("now-25h", gda);
    }

    [Fact]
    public void The_web_console_shows_the_estimate_quality_and_labels_the_bridge_card_truthfully()
    {
        string session = Src("GridSim.Web", "Sessions", "SimSession.cs");
        Assert.Contains("c.EstimateReceived += OnBridgeEstimate;", session);
        Assert.Contains("_bridge.EstimateReceived -= OnBridgeEstimate;", session);
        Assert.Contains("est.QualitySummary()", Src("GridSim.Web", "Components", "Pages", "Dashboard.razor"));
        string home = Src("GridSim.Web", "Components", "Pages", "Home.razor");
        Assert.DoesNotContain("live stream</span>", home);
        Assert.Contains("GDA lake: presolved states (gb-full)", home);
    }

    [Fact]
    public void The_shared_estimate_quality_text_matches_the_desktop_pill()
    {
        var pass = new GridSim.Core.IO.BridgeProtocol.EstimateBlock { ChiPassed = true, ObjectiveJ = 41.26, MeasurementsN = 120 };
        Assert.Equal("chi-square PASS  *  J=41.3  n=120", pass.QualitySummary());
        var fail = new GridSim.Core.IO.BridgeProtocol.EstimateBlock { ChiPassed = false, ObjectiveJ = 900.04, MeasurementsN = 98, RemovedN = 2 };
        Assert.Equal("chi-square FAIL  *  J=900.0  n=98  removed=2", fail.QualitySummary());
    }

    [Fact]
    public void Estimate_warns_when_the_solved_prior_leaves_the_voltage_band()
    {
        // The SP02 investigation: a prior solve at 1.26 pu became prior-solved voltage rows and the fit passed chi-square.
        string program = Src("GridSim.Cli", "Program.cs");
        int warn = program.IndexOf("warning: the measured-injection prior solve puts Vm at", StringComparison.Ordinal);
        Assert.True(warn > 0, "the out-of-band prior warning is gone");
        Assert.Contains("if (priorMax > band.VmMax || priorMin < band.VmMin)", program);
    }

    [Fact]
    public void Serve_exits_3_when_no_window_produced_a_usable_estimate()
    {
        // Every window Unobservable used to print "converged 0" and exit 0.
        string body = Body(Src("GridSim.Cli", "Program.cs"), "void RunServe(List<string> a, string root)");
        int at = body.IndexOf("if (summary.WindowsConverged == 0)", StringComparison.Ordinal);
        Assert.True(at >= 0, "serve must test for a run with no converged window");
        Assert.Contains("Environment.ExitCode = 3;", body[at..]);
    }
}
