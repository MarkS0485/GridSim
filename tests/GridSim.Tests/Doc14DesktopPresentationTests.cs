using System.Text.RegularExpressions;
using GridSim.Core.Presentation;
using Xunit;

namespace GridSim.Tests;

/// <summary>
/// DOC-14 (docs-session findings, stream 14: desktop and presentation). The test project does not reference
/// GridSim.Wpf (a Windows-only WinExe), so the rules each fix needs are moved into Core where they can be driven
/// directly, and the WPF wiring is pinned by source-contract checks in the style of
/// <see cref="WpfPresentationSourceContractTests"/>. Each section names its D-DOC item.
/// </summary>
public class Doc14DesktopPresentationTests
{
    private static string Wpf(string file) => PresentationConstantsAntiDriftTests.Source("src", "GridSim.Wpf", file);

    // The pickers exactly as MainWindow offers them (zone cases, preset days, sources), and a realistic tab strip
    // and model list, so the resolution is tested against the shapes the app passes in.
    private static readonly string[] Zones =
        { "zone-nged", "zone-ukpn", "zone-ssen", "zone-spen", "zone-npg", "zone-enwl", "gb-full", "gb-spine" };
    private static readonly int[] BackDays = { 9, 12, 14, 21, 30, 60 };
    private static readonly string[] Sources = { "presolved", "estimated", "live" };
    private static readonly string[] Tabs =
    {
        "NETWORK", "GENERATION & INERTIA", "THE MACHINE", "SCADA", "OPERATOR", "MARKET", "NESO RP1", "MECHANICAL",
        "HUMAN SCALE", "SPEED DOMAINS", "INVERTER FLEET", "EXPERIMENTAL", "HARMONICS", "CYCLE", "ASSETS", "ABOUT",
    };
    private static readonly string[] Models =
        { "GB reduced (data/uk)", "case9 (MATPOWER)", "case30 (MATPOWER)", "case118 (MATPOWER)", "Live bridge" };

    private static DesktopLaunchPlan Plan(params string[] args) =>
        DesktopLaunchArgs.Parse(args).Resolve(Zones, BackDays, Sources, Tabs, Models);

    // ================================ D-DOC-074: a flag's value is never the model ================================

    [Fact]
    public void D074_back_30_does_not_boot_case30()
    {
        // Before: the model text was the first word not starting with '-', so "--back 30" made "30" the model and the
        // picker opened "case30 (MATPOWER)". Now "30" is --back's value and there is no model text.
        DesktopLaunchArgs a = DesktopLaunchArgs.Parse(new[] { "--back", "30" });
        Assert.True(a.Ok);
        Assert.Null(a.ModelText);
        Assert.Equal(30, a.BackDays);

        DesktopLaunchPlan p = Plan("--back", "30");
        Assert.True(p.Ok);
        Assert.Null(p.ModelIndex);            // no model text: the app keeps its last-used / first default
        Assert.Equal(4, p.BackIndex);         // the 30-day preset
    }

    [Fact]
    public void D074_tab_value_is_not_searched_for_in_the_model_picker()
    {
        DesktopLaunchPlan p = Plan("--tab", "OPERATOR");
        Assert.True(p.Ok);
        Assert.Null(p.ModelIndex);
        Assert.Equal(4, p.TabIndex);
    }

    [Fact]
    public void D074_model_text_may_sit_anywhere_and_flag_values_are_skipped()
    {
        DesktopLaunchPlan p = Plan("--zone", "ukpn", "bridge", "--back", "30", "--bridge-source", "estimated");
        Assert.True(p.Ok, string.Join(" | ", p.Errors));
        Assert.Equal(4, p.ModelIndex);        // "Live bridge"
        Assert.Equal(1, p.ZoneIndex);         // zone-ukpn
        Assert.Equal(4, p.BackIndex);
        Assert.Equal(1, p.BridgeSourceIndex); // estimated

        DesktopLaunchPlan q = Plan("case118", "--tab", "assets");
        Assert.True(q.Ok);
        Assert.Equal(3, q.ModelIndex);
        Assert.Equal(14, q.TabIndex);
    }

    [Fact]
    public void D074_an_empty_command_line_is_fine_and_selects_nothing()
    {
        DesktopLaunchPlan p = Plan();
        Assert.True(p.Ok);
        Assert.Null(p.ModelIndex);
        Assert.Null(p.ZoneIndex);
        Assert.Null(p.BackIndex);
        Assert.Null(p.BridgeSourceIndex);
        Assert.Null(p.TabIndex);
    }

    [Fact]
    public void D074_two_model_words_are_refused()
    {
        DesktopLaunchPlan p = Plan("case9", "case30");
        Assert.False(p.Ok);
        Assert.Contains(p.Errors, e => e.Contains("More than one model text", StringComparison.Ordinal)
                                       && e.Contains("'case9'", StringComparison.Ordinal) && e.Contains("'case30'", StringComparison.Ordinal));
    }

    // ================================ D-DOC-142: unmatched values are refused ================================

    [Theory]
    [InlineData(new[] { "--zone", "nowhere" }, "--zone 'nowhere' matches no bridge zone")]
    [InlineData(new[] { "--back", "31" }, "--back 31 is not a start preset")]
    [InlineData(new[] { "--back", "thirty" }, "--back 'thirty' is not a whole number of days")]
    [InlineData(new[] { "--back", "-9" }, "--back '-9' is not a whole number of days")]
    [InlineData(new[] { "--back", "9.0" }, "--back '9.0' is not a whole number of days")]
    [InlineData(new[] { "--bridge-source", "solves" }, "--bridge-source 'solves' is not a bridge source")]
    [InlineData(new[] { "--tab", "nosuchtab" }, "--tab 'nosuchtab' matches no tab")]
    [InlineData(new[] { "case999" }, "The model text 'case999' matches no entry")]
    [InlineData(new[] { "--zone" }, "--zone needs a value")]
    [InlineData(new[] { "--zone", "--back", "30" }, "--zone needs a value")]
    [InlineData(new[] { "--zone=ukpn" }, "give the value as the next word (--zone ukpn)")]
    [InlineData(new[] { "--Zone", "ukpn" }, "Options are case-sensitive: did you mean --zone?")]
    [InlineData(new[] { "--verbose" }, "Unknown option '--verbose'")]
    [InlineData(new[] { "--zone", "ukpn", "--zone", "npg" }, "--zone is given more than once")]
    public void D142_every_value_that_cannot_be_honoured_is_refused_with_a_message_naming_it(string[] args, string expected)
    {
        DesktopLaunchPlan p = Plan(args);
        Assert.False(p.Ok);
        Assert.Contains(p.Errors, e => e.Contains(expected, StringComparison.Ordinal));
        // The refusal text lists every problem and says how to recover.
        string text = p.RefusalText();
        Assert.Contains(expected, text, StringComparison.Ordinal);
        Assert.Contains("manual chapter 8", text, StringComparison.Ordinal);
    }

    [Fact]
    public void D142_matching_keeps_the_documented_rules()
    {
        // Zone, tab and model: first entry CONTAINING the text, ignoring case. Source: equal, ignoring case.
        Assert.Equal(1, Plan("--zone", "UKPN").ZoneIndex);
        Assert.Equal(6, Plan("--zone", "gb").ZoneIndex);              // first of gb-full / gb-spine, left to right
        Assert.Equal(2, Plan("--bridge-source", "LIVE").BridgeSourceIndex);
        Assert.Equal(1, Plan("--tab", "generation").TabIndex);
        Assert.Equal(1, Plan("CASE9").ModelIndex);
        // A source must be equal, not merely contained ("est" is not a source).
        Assert.False(Plan("--bridge-source", "est").Ok);
    }

    [Fact]
    public void D142_all_problems_are_reported_together()
    {
        DesktopLaunchPlan p = Plan("--zone", "nowhere", "--back", "31", "--bridge-source", "x", "--tab", "qq", "zzz");
        Assert.Equal(5, p.Errors.Count);
    }

    [Fact]
    public void D074_D142_the_window_reads_its_launch_arguments_through_core_and_closes_on_a_refusal()
    {
        string src = Wpf("MainWindow.xaml.cs");
        // The old ad hoc reads are gone: no "first non-dash word" model pick, no ArgVal helper.
        Assert.DoesNotContain("!a.StartsWith('-')", src, StringComparison.Ordinal);
        Assert.DoesNotContain("ArgVal(", src, StringComparison.Ordinal);
        Assert.Contains("DesktopLaunchArgs.Parse(cmdArgs.Skip(1).ToArray()).Resolve(", src, StringComparison.Ordinal);
        Assert.Matches(new Regex(@"if \(!plan\.Ok\)\s*\{[^}]*MessageBox\.Show\(plan\.RefusalText\(\)[^}]*Shutdown\(LaunchArgumentsRefusedExitCode\);\s*return;"), src);
        Assert.Contains("private const int LaunchArgumentsRefusedExitCode = 2;", src, StringComparison.Ordinal);
    }

    // ================================ D-DOC-073: tab jumps by name ================================

    [Fact]
    public void D073_no_tab_jump_or_gate_uses_the_last_tab_as_ASSETS()
    {
        string src = Wpf("MainWindow.xaml.cs");
        // (StepPicker.Items.Count - 1 is the STEP picker's clamp, unrelated; the tab-strip forms are what must be gone.)
        Assert.DoesNotContain("Tabs.Items.Count - 1", src, StringComparison.Ordinal);
        Assert.DoesNotContain("tc.Items.Count - 1", src, StringComparison.Ordinal);
        Assert.Contains("private const string AssetsTabHeader = \"ASSETS\";", src, StringComparison.Ordinal);
        // The explorer gate reads the selected tab's header.
        Assert.Contains("_vm.Assets.Active = tc.SelectedItem is System.Windows.Controls.TabItem { Header: AssetsTabHeader };", src, StringComparison.Ordinal);
        // OPEN IN EXPLORER and the SCADA click-through both select the tab by name.
        Assert.Equal(2, Regex.Count(src, @"SelectTab\(AssetsTabHeader\);"));
        // The header the code names exists in the XAML, and ABOUT (the reason the index test broke) sits after it.
        string xaml = Wpf("MainWindow.xaml");
        int assets = xaml.IndexOf("<TabItem Header=\"ASSETS\"", StringComparison.Ordinal);
        int about = xaml.IndexOf("<TabItem Header=\"ABOUT\"", StringComparison.Ordinal);
        Assert.True(assets > 0, "MainWindow.xaml must carry the ASSETS tab");
        Assert.True(about > assets, "the test guards the case where ASSETS is not the last tab");
    }

    // ================================ D-DOC-138: invariant culture ================================

    [Fact]
    public void D138_the_desktop_app_pins_the_invariant_culture_before_any_window_exists()
    {
        string src = Wpf("App.xaml.cs");
        Assert.Contains("CultureInfo.DefaultThreadCurrentCulture = CultureInfo.InvariantCulture;", src, StringComparison.Ordinal);
        Assert.Contains("CultureInfo.CurrentCulture = CultureInfo.InvariantCulture;", src, StringComparison.Ordinal);
        // XAML StringFormat bindings use the element language, not the thread culture: pinned too.
        Assert.Contains("FrameworkElement.LanguageProperty.OverrideMetadata(", src, StringComparison.Ordinal);
        Assert.Contains("XmlLanguage.GetLanguage(CultureInfo.InvariantCulture.IetfLanguageTag)", src, StringComparison.Ordinal);
        // Pinned in OnStartup before base.OnStartup (which creates the StartupUri window).
        int pin = src.IndexOf("PinInvariantCulture();", StringComparison.Ordinal);
        int baseStart = src.IndexOf("base.OnStartup(e);", StringComparison.Ordinal);
        Assert.True(pin > 0 && baseStart > pin, "the culture must be pinned before the first window is built");
    }

    [Fact]
    public void D138_the_invariant_language_tag_is_empty_which_wpf_maps_to_the_invariant_culture()
    {
        // The pin relies on this .NET fact: InvariantCulture's IETF tag is "" (XmlLanguage.Empty, invariant formatting).
        Assert.Equal("", System.Globalization.CultureInfo.InvariantCulture.IetfLanguageTag);
    }

    // ================================ D-DOC-141: bridge PLAY / PAUSE holds the display ================================

    /// <summary>The body of the first method or lambda block that follows <paramref name="anchor"/>.</summary>
    private static string BlockAfter(string src, string anchor)
    {
        int at = src.IndexOf(anchor, StringComparison.Ordinal);
        Assert.True(at >= 0, $"not found: {anchor}");
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
    public void D141_bridge_mode_opens_playing_and_pause_holds_the_display_not_only_the_animation()
    {
        string src = Wpf("MainWindow.xaml.cs");

        // LoadBridge opens playing (label PAUSE), not as PLAY over a stream that already renders.
        string load = BlockAfter(src, "private async void LoadBridge(ModelSource s)");
        // Test retargeted 2026-10-20 (UI programme 0.2): the window's DispatcherTimer moved into
        // src/GridSim.Ui/Hosting/SimHost.cs (the host loop), so "stop the local step driver" is now _host.Playing = false
        // where it was _timer?.Stop(); the line still says, in the same order, not paused, local driver off, label PAUSE.
        // Strengthened: the host is also detached in LoadBridge (no local feed is left to play).
        Assert.Contains("_paused = false; _host.Playing = false; PauseBtn.Content = \" PAUSE\";", load, StringComparison.Ordinal);
        Assert.Contains("_host.Detach();", load, StringComparison.Ordinal);
        Assert.DoesNotContain("PauseBtn.Content = \" PLAY\"", load, StringComparison.Ordinal);

        // The state handler holds the frame, HUD and overlays while paused; the market handler holds its blocks.
        string state = BlockAfter(src, "client.StateReceived += (f, scalars, overlays) => Dispatcher.BeginInvoke(() =>");
        int hold = state.IndexOf("if (_paused) { _heldHud = scalars; return; }", StringComparison.Ordinal);
        int hud = state.IndexOf("_vm.UpdateHud(", StringComparison.Ordinal);
        int render = state.IndexOf("RenderReplayFrame(f);", StringComparison.Ordinal);
        Assert.True(hold >= 0 && hud > hold && render > hold, "a paused display must return before the HUD and the map are touched");
        string market = BlockAfter(src, "client.MarketDataReceived += (f, op, mk) => Dispatcher.BeginInvoke(() =>");
        Assert.Contains("if (_paused) { _heldMarket = (op, mk, _bridgeTickUtc); return; }", market, StringComparison.Ordinal);

        // PAUSE on a bridge feed goes to SetBridgePaused, never to the local timer toggle.
        string pause = BlockAfter(src, "private void Pause_Click(object sender, RoutedEventArgs e)");
        int bridgeBranch = pause.IndexOf("if (_bridge is not null) { SetBridgePaused(!_paused); return; }", StringComparison.Ordinal);
        int toggle = pause.IndexOf("_paused = !_paused;", StringComparison.Ordinal);
        Assert.True(bridgeBranch >= 0 && toggle > bridgeBranch, "the bridge branch must come first");

        // PLAY releases the newest held frame (unless a what-if is on the map) and applies the held side channels.
        string release = BlockAfter(src, "private void SetBridgePaused(bool pause)");
        Assert.Contains("PAUSED  *  {_bridgeCase}  *  display held, stream still running", release, StringComparison.Ordinal);
        Assert.Contains("_vm.UpdateHud(hud.InertiaMwS, hud.ConditionIndex, hud.RocofHeadroomHzS);", release, StringComparison.Ordinal);
        Assert.Contains("_vm.UpdateMarketData(held.Op, held.Mk, held.TickUtc);", release, StringComparison.Ordinal);
        int whatIf = release.IndexOf("if (_whatIf)", StringComparison.Ordinal);
        int show = release.IndexOf("RenderReplayFrame(f);", StringComparison.Ordinal);
        Assert.True(whatIf >= 0 && show > whatIf, "a what-if result keeps the live frame off the map, as before");

        // A restarted stream and RETURN TO LIVE both play again.
        Assert.Contains("_paused = false; PauseBtn.Content = \" PAUSE\"; _heldHud = null; _heldMarket = null;",
            BlockAfter(src, "private async void ApplyBridgeStream()"), StringComparison.Ordinal);
        Assert.Contains("if (_bridge is not null && _paused) { SetBridgePaused(false); return; }",
            BlockAfter(src, "private void ReturnToLive()"), StringComparison.Ordinal);
    }

    // ================================ D-DOC-143: MarketVm.Reset ================================

    [Fact]
    public void D143_market_reset_clears_every_value_the_lake_writes_and_runs_on_every_timeline_break()
    {
        // Test retargeted 2026-10-20 (UI programme 1.6): src/GridSim.Wpf/Views/MarketVm.cs moved to
        // src/GridSim.Ui/Tabs/Market/MarketModel.cs; assertions unchanged (the colour tokens that replaced the brushes
        // are bound properties SetMarketData writes, so the rule below now requires Reset to clear them too), and
        // strengthened: the WPF view-model must derive from that model and override neither method.
        string vm = PresentationConstantsAntiDriftTests.Source("src", "GridSim.Ui", "Tabs", "Market", "MarketModel.cs");
        string marketAdapter = Wpf("Views/MarketVm.cs");
        Assert.Contains("public sealed class MarketVm : MarketModel", marketAdapter, StringComparison.Ordinal);
        Assert.DoesNotContain("SetMarketData(", marketAdapter, StringComparison.Ordinal);
        Assert.DoesNotContain("Reset()", marketAdapter, StringComparison.Ordinal);
        string set = BlockAfter(vm, "public void SetMarketData(");
        // The interconnector rows are rebuilt from scratch (their object initialisers are not tab properties); the
        // scalar properties are everything written before that.
        set = set[..set.IndexOf("Interconnectors.Clear();", StringComparison.Ordinal)];
        string reset = BlockAfter(vm, "public void Reset()");

        // Every bound property SetMarketData assigns ("Name = ...") must be assigned in Reset too, so no lake value can
        // outlive the bridge. Read from the source, so a property added to SetMarketData later is caught here.
        var written = Regex.Matches(set, @"(?m)(?:^|[\s{;])([A-Z][A-Za-z]+) = (?!new\b)")
            .Select(m => m.Groups[1].Value)
            .Distinct(StringComparer.Ordinal)
            .ToList();
        Assert.Contains("ImbalancePrice", written);
        Assert.Contains("Niv", written);
        Assert.Contains("PriceTrace", written);
        foreach (string p in written)
            Assert.Matches(new Regex(@"\b" + p + @" = "), reset);
        Assert.Contains("Interconnectors.Clear();", reset, StringComparison.Ordinal);
        Assert.Contains("_priceHist.Clear();", reset, StringComparison.Ordinal);
        Assert.Contains("_pricePeriod = null;", reset, StringComparison.Ordinal);

        // ResetTimeline (model switch, replay, bridge connect and restart) calls it beside the RP1 and OPERATOR resets.
        // Test retargeted 2026-10-20 (UI programme 0.2): ResetTimeline moved to src/GridSim.Ui/Dashboard/DashboardModel.cs,
        // which calls the WPF tab resets through its ResetTabs hook; the three resets are asserted unchanged, in the WPF
        // ResetTabs override; strengthened: ResetTimeline must call ResetTabs, as its first statement, so the resets run
        // on every timeline break before anything else, as they did.
        string dash = Wpf("DashboardViewModel.cs");
        string timeline = BlockAfter(dash, "protected override void ResetTabs()");
        Assert.Contains("Market.Reset();", timeline, StringComparison.Ordinal);
        Assert.Contains("NesoRp1.Reset();", timeline, StringComparison.Ordinal);
        Assert.Contains("Operator.Reset();", timeline, StringComparison.Ordinal);
        string timelineUi = BlockAfter(PresentationConstantsAntiDriftTests.Source("src", "GridSim.Ui", "Dashboard", "DashboardModel.cs"), "public void ResetTimeline()");
        Assert.Matches(new Regex(@"public void ResetTimeline\(\)\s*\{\s*ResetTabs\(\);"), timelineUi);
    }

    // ================================ D-DOC-200: RP1 price trace per settlement period ================================

    private static readonly DateTime T0 = new(2026, 7, 1, 12, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void D200_the_capacity_overloads_keep_the_given_length_and_the_period_rule()
    {
        // One RP1 day (48 periods) of 1 s ticks: 30 min of ticks is ONE sample, as on MARKET.
        var h = new Queue<double>();
        long? period = null;
        for (int s = 0; s < 1800; s++) ImbalancePriceTrace.Append(h, 55.0 + s, T0.AddSeconds(s), ref period, 48);
        Assert.Single(h);
        Assert.Equal(55.0 + 1799, h.Peek());   // the period's latest price

        // 200 periods into a 48-period trace keeps the newest 48.
        h.Clear(); period = null;
        for (int k = 0; k < 200; k++) ImbalancePriceTrace.Append(h, k, T0.AddMinutes(30 * k), ref period, 48);
        Assert.Equal(48, h.Count);
        Assert.Equal(152.0, h.Peek());
        Assert.Equal(199.0, h.Last());

        // Undated fallback with a capacity.
        var u = new Queue<double>();
        for (int k = 0; k < 60; k++) ImbalancePriceTrace.Append(u, k, 48);
        Assert.Equal(48, u.Count);
        Assert.Equal(12.0, u.Peek());

        Assert.Throws<ArgumentOutOfRangeException>(() => ImbalancePriceTrace.Append(new Queue<double>(), 1.0, 0));
        long? p0 = null;
        Assert.Throws<ArgumentOutOfRangeException>(() => ImbalancePriceTrace.Append(new Queue<double>(), 1.0, T0, ref p0, 0));
    }

    [Fact]
    public void D200_the_default_overloads_are_unchanged_96_periods()
    {
        // The MARKET overloads (no capacity) keep HistoryLength = 96, exactly as before the capacity parameter.
        var a = new Queue<double>();
        var b = new Queue<double>();
        long? pa = null, pb = null;
        for (int k = 0; k < 300; k++)
        {
            ImbalancePriceTrace.Append(a, k * 1.5, T0.AddMinutes(30 * k), ref pa);
            ImbalancePriceTrace.Append(b, k * 1.5, T0.AddMinutes(30 * k), ref pb, ImbalancePriceTrace.HistoryLength);
        }
        Assert.Equal(96, a.Count);
        Assert.Equal(b.ToArray(), a.ToArray());
    }

    [Fact]
    public void D200_the_rp1_trace_uses_the_settlement_period_rule_with_its_one_day_length()
    {
        // Test retargeted 2026-10-20 (UI programme 1.6): src/GridSim.Wpf/Views/NesoRp1Vm.cs moved to
        // src/GridSim.Ui/Tabs/NesoRp1/NesoRp1Model.cs; assertions unchanged, and strengthened: the WPF view-model must
        // derive from that model and may not keep a price trace of its own.
        string rp1 = PresentationConstantsAntiDriftTests.Source("src", "GridSim.Ui", "Tabs", "NesoRp1", "NesoRp1Model.cs");
        string rp1Adapter = Wpf("Views/NesoRp1Vm.cs");
        Assert.Contains("public sealed class NesoRp1Vm : NesoRp1Model", rp1Adapter, StringComparison.Ordinal);
        Assert.DoesNotContain("ImbalancePriceTrace.", rp1Adapter, StringComparison.Ordinal);
        Assert.DoesNotContain("Queue<double>", rp1Adapter, StringComparison.Ordinal);
        Assert.Contains("private readonly Queue<double> _price = new();", rp1, StringComparison.Ordinal);
        Assert.Contains("ImbalancePriceTrace.Append(_price, px, at, ref _pricePeriod, PriceTraceLen)", rp1, StringComparison.Ordinal);
        Assert.Contains("ImbalancePriceTrace.Append(_price, px, PriceTraceLen)", rp1, StringComparison.Ordinal);
        Assert.DoesNotContain("_price.Add(px);", rp1, StringComparison.Ordinal);
        Assert.Contains("_pricePeriod = null;", BlockAfter(rp1, "public void Reset()"), StringComparison.Ordinal);
        // The dashboard hands RP1 the tick's instant, as it does MARKET.
        Assert.Contains("NesoRp1.SetRp1Data(op, mk, tickUtc);", Wpf("DashboardViewModel.cs"), StringComparison.Ordinal);
    }

    // ================================ D-DOC-144: shard coverage labelled honestly ================================

    [Fact]
    public void D144_shard_coverage_is_labelled_cumulative_since_load_on_both_shells()
    {
        // ShardsCovered only grows (a HashSet of every shard ever completed), so "rolling" was false. Both shells now
        // say what it is, with one wording.
        const string label = "N-1 shards covered since load";
        string wpf = Wpf("Views/CycleView.xaml");
        string web = PresentationConstantsAntiDriftTests.Source("src", "GridSim.Web", "Components", "Tabs", "CycleTab.razor");
        Assert.Contains($"<TextBlock Text=\"{label}\"", wpf, StringComparison.Ordinal);
        Assert.Contains($"<span>{label}</span>", web, StringComparison.Ordinal);
        Assert.DoesNotContain("rolling coverage", wpf, StringComparison.Ordinal);
        Assert.DoesNotContain("rolling coverage", web, StringComparison.Ordinal);
        // Test retargeted 2026-10-20 (UI programme 1.6): src/GridSim.Wpf/Views/CycleView.xaml was ported to
        // src/GridSim.App/Tabs/CycleView.axaml; the assertions on the WPF XAML are unchanged (it is still shipped), and
        // strengthened: the Avalonia view carries the same label and refuses the same wording, so both desktop shells say it.
        string axaml = PresentationConstantsAntiDriftTests.Source("src", "GridSim.App", "Tabs", "CycleView.axaml");
        Assert.Contains($"<TextBlock Text=\"{label}\"", axaml, StringComparison.Ordinal);
        Assert.DoesNotContain("rolling coverage", axaml, StringComparison.Ordinal);
        // The Core property says so too.
        string core = PresentationConstantsAntiDriftTests.Source("src", "GridSim.Core", "Scheduling", "ResidentCycleService.cs");
        Assert.Contains("CUMULATIVE: it only grows", core, StringComparison.Ordinal);
        Assert.DoesNotContain("rolling coverage numerator", core, StringComparison.Ordinal);
    }

    // ================================ D-DOC-148: STABILITY TREND label ================================

    [Fact]
    public void D148_the_stability_trend_is_labelled_by_its_real_span_in_frames()
    {
        Assert.Equal(60, FrequencyRiskPresenter.TraceCapacity);
        Assert.Equal("STABILITY TREND  *  last 60 frames", FrequencyRiskPresenter.StabilityTrendTitle);

        // The trace really holds TraceCapacity points, whatever time they span.
        var trace = new FrequencyRiskPresenter.PurchasedTimeTrace();
        for (int i = 0; i < 500; i++) trace.Add(i % 10);
        Assert.Equal(FrequencyRiskPresenter.TraceCapacity, trace.Points.Count);

        string xaml = Wpf("Views/NesoRp1View.xaml");
        Assert.DoesNotContain("30 min", xaml, StringComparison.Ordinal);
        Assert.Contains("Text=\"{Binding StabilityTrendTitle}\"", xaml, StringComparison.Ordinal);
        // Test retargeted 2026-10-20 (UI programme 1.6): src/GridSim.Wpf/Views/NesoRp1Vm.cs moved to
        // src/GridSim.Ui/Tabs/NesoRp1/NesoRp1Model.cs (the WPF XAML above is unchanged and still read); the property
        // assertion is unchanged, and strengthened: the Avalonia port of the view (src/GridSim.App/Tabs/NesoRp1View.axaml)
        // binds the same title and refuses the same wording, so both desktop shells label the trend in frames.
        Assert.Contains("public string StabilityTrendTitle { get; } = FrequencyRiskPresenter.StabilityTrendTitle;",
            PresentationConstantsAntiDriftTests.Source("src", "GridSim.Ui", "Tabs", "NesoRp1", "NesoRp1Model.cs"), StringComparison.Ordinal);
        string axaml = PresentationConstantsAntiDriftTests.Source("src", "GridSim.App", "Tabs", "NesoRp1View.axaml");
        Assert.DoesNotContain("30 min", axaml, StringComparison.Ordinal);
        Assert.Contains("Text=\"{Binding StabilityTrendTitle}\"", axaml, StringComparison.Ordinal);
    }

    // ================================ D-DOC-145: STATIC DEMO tags on the desktop asset pages ================================

    [Theory]
    [InlineData("Views/Assets/TransformerTemplates.xaml", "THERMAL")]
    [InlineData("Views/Assets/TransformerTemplates.xaml", "DISSOLVED GAS ANALYSIS")]
    [InlineData("Views/Assets/TransformerTemplates.xaml", "PROTECTION")]
    [InlineData("Views/Assets/TransformerTemplates.xaml", "EVENT LOG")]
    [InlineData("Views/Assets/CircuitTemplates.xaml", "ROUTE INTEGRITY")]
    [InlineData("Views/Assets/CircuitTemplates.xaml", "THERMAL ENVELOPE")]
    [InlineData("Views/Assets/CircuitTemplates.xaml", "ACCESS &amp; CIVIL")]
    [InlineData("Views/Assets/CircuitTemplates.xaml", "EVENT LOG")]
    [InlineData("Views/Assets/GeneratorTemplates.xaml", "SUBSYSTEMS")]
    [InlineData("Views/Assets/GeneratorTemplates.xaml", "EVENT LOG")]
    public void D145_every_fixed_value_panel_header_carries_the_static_demo_tag(string file, string header)
    {
        // The header and the tag share one DockPanel (the tag docked right, or after the header in the generator rows).
        string xaml = Wpf(file);
        var m = Regex.Match(xaml,
            @"<DockPanel[^>]*>\s*(?:<TextBlock Style=""\{StaticResource StaticDemoTag\}""[^>]*/>\s*<TextBlock Text=""" + Regex.Escape(header)
            + @"""[^>]*/>|<TextBlock Text=""" + Regex.Escape(header) + @"""[^>]*/>\s*<TextBlock Style=""\{StaticResource StaticDemoTag\}""[^>]*/>)\s*</DockPanel>");
        Assert.True(m.Success, $"{file}: the {header} panel header must carry the STATIC DEMO tag");
    }

    [Fact]
    public void D145_under_S7_the_protection_tag_shows_only_while_the_panel_is_the_demo()
    {
        // Test added 2026-10-20 (ASSETS fix S7, Mark approved; the plan's D145 retarget: "a panel fed by real data drops
        // its tag only when the data is real, and the test asserts both directions"). The PROTECTION panel lists the
        // case's own protection settings when the case has any for the transformer, and then it is not a demo: its tag
        // (still present, still beside the header: the theory above) is shown by the page's ProtectionIsDemo flag. Every
        // other STATIC DEMO tag on the WPF asset pages stays unconditional (no Visibility at all). Both directions of the
        // flag are asserted by behaviour in UiAssetFixesTests (S7_...: demo rows with the flag on, real rows with it off)
        // and on every transformer page the parity test compares (FixedS7ProtectionTag).
        string tx = Wpf("Views/Assets/TransformerTemplates.xaml");
        Assert.Matches(new Regex(@"<TextBlock Style=""\{StaticResource StaticDemoTag\}"" DockPanel\.Dock=""Right"" Visibility=""\{Binding ProtectionIsDemo, Converter=\{StaticResource BoolToVis\}\}"" />\s*<TextBlock Text=""PROTECTION"""), tx);
        Assert.Contains("<BooleanToVisibilityConverter x:Key=\"BoolToVis\" />", tx, StringComparison.Ordinal);
        foreach (string file in new[] { "Views/Assets/TransformerTemplates.xaml", "Views/Assets/CircuitTemplates.xaml", "Views/Assets/GeneratorTemplates.xaml" })
        {
            List<string> tags = Regex.Matches(Wpf(file), @"<TextBlock Style=""\{StaticResource StaticDemoTag\}""[^>]*/>").Select(m => m.Value).ToList();
            Assert.NotEmpty(tags);
            int bound = tags.Count(t => t.Contains("Visibility", StringComparison.Ordinal));
            Assert.True(bound == (file.Contains("Transformer", StringComparison.Ordinal) ? 1 : 0), $"{file}: {bound} STATIC DEMO tags carry a Visibility");
        }
        Assert.Contains("public bool ProtectionIsDemo", PresentationConstantsAntiDriftTests.Source("src", "GridSim.Ui", "Assets", "Pages", "TransformerPages.cs"), StringComparison.Ordinal);
    }

    [Fact]
    public void D145_the_tag_style_says_STATIC_DEMO_and_the_fixed_dials_say_DEMO()
    {
        string theme = Wpf("Theme.xaml");
        Assert.Matches(new Regex(@"<Style x:Key=""StaticDemoTag"" TargetType=""TextBlock"">\s*<Setter Property=""Text"" Value=""STATIC DEMO"" />"), theme);
        // The old small grey "demo data" note is gone; the generator page uses the shared tag.
        Assert.DoesNotContain("Text=\"demo data\"", Wpf("Views/Assets/GeneratorTemplates.xaml"), StringComparison.Ordinal);
        // Dials and the transformer schematic that draw the fixed temperatures say so.
        string tx = Wpf("Views/Assets/TransformerTemplates.xaml");
        Assert.Contains("Caption=\"TOP-OIL  DEMO\"", tx, StringComparison.Ordinal);
        Assert.Contains("Caption=\"HOT-SPOT  DEMO\"", tx, StringComparison.Ordinal);
        Assert.Contains("Text=\"TEMPERATURES: STATIC DEMO\"", tx, StringComparison.Ordinal);
        Assert.Contains("Caption=\"CONDUCTOR  DEMO\"", Wpf("Views/Assets/CircuitTemplates.xaml"), StringComparison.Ordinal);
    }

    // ================================ D-DOC-146: circuits open by index, not by name ================================

    [Fact]
    public void D146_repeated_circuit_names_and_index_identity_tells_the_namesakes_apart()
    {
        // gb-full's bundled names were made unique (N169 / N190), but a materialised GDA zone or a user case can repeat a
        // circuit name (two circuits of one double-circuit route, or a parallel line and a different corridor sharing a
        // label). This case has both: two parallel circuits "Route A" on 1-2, and a "Route A" on 2-3.
        var m = new GridSim.Core.Model.GridModel
        {
            BaseMva = 100,
            Buses = new[]
            {
                new GridSim.Core.Model.Bus(1, GridSim.Core.Model.BusType.Slack, BaseKv: 400),
                new GridSim.Core.Model.Bus(2, GridSim.Core.Model.BusType.Pq, Pd: 100, BaseKv: 400),
                new GridSim.Core.Model.Bus(3, GridSim.Core.Model.BusType.Pq, Pd: 50, BaseKv: 400),
            },
            Branches = new[]
            {
                new GridSim.Core.Model.Branch(1, 2, 0.01, 0.10, Name: "Route A"),
                new GridSim.Core.Model.Branch(1, 2, 0.02, 0.20, Name: "Route A"),
                new GridSim.Core.Model.Branch(2, 3, 0.01, 0.10, Name: "Route A"),
            },
            Generators = new[] { new GridSim.Core.Model.Generator(1, 150) },
        };
        // Circuits (not transformers) whose name is shared by another circuit: the pages the old name lookup got wrong.
        var groups = m.Branches.Select((b, i) => (b, i))
            .Where(x => !x.b.IsTransformer && x.b.Name is not null)
            .GroupBy(x => x.b.Name!, StringComparer.Ordinal)
            .Where(g => g.Count() > 1)
            .ToList();
        Assert.NotEmpty(groups);

        int checkedRows = 0;
        foreach (var g in groups)
        {
            var members = g.ToList();
            // The old lookup ("first branch with this name") gave the same index for every member.
            int firstByName = -1;
            for (int k = 0; k < m.Branches.Count; k++) if (m.Branches[k].Name == g.Key) { firstByName = k; break; }
            foreach ((GridSim.Core.Model.Branch b, int i) in members)
            {
                // The new identity: each row's own index, confirmed by its ends and name, resolves to itself.
                (GridSim.Core.Model.Branch Branch, int Index)? hit = AssetLookup.FindBranch(m, i, b.From, b.To, b.Name);
                Assert.NotNull(hit);
                Assert.Equal(i, hit!.Value.Index);
                Assert.Same(b, hit.Value.Branch);
                checkedRows++;
            }
            // The second and third members were opened as the first before.
            Assert.NotEqual(firstByName, members[1].i);
            Assert.NotEqual(firstByName, members[2].i);
        }
        Assert.Equal(3, checkedRows);
    }

    [Fact]
    public void D146_an_unnamed_circuit_resolves_by_index_where_the_name_lookup_never_did()
    {
        var m = new GridSim.Core.Model.GridModel
        {
            BaseMva = 100,
            Buses = new[] { new GridSim.Core.Model.Bus(1, GridSim.Core.Model.BusType.Slack), new GridSim.Core.Model.Bus(2, GridSim.Core.Model.BusType.Pq, Pd: 10) },
            Branches = new[] { new GridSim.Core.Model.Branch(1, 2, 0.01, 0.1) },   // no name
            Generators = new[] { new GridSim.Core.Model.Generator(1, 10) },
        };
        Assert.Equal(0, AssetLookup.FindBranch(m, 0, 1, 2, null)!.Value.Index);
    }

    [Fact]
    public void D146_the_circuit_page_and_every_route_to_it_use_the_index()
    {
        // Test retargeted 2026-10-20 (UI programme ASSETS A2): src/GridSim.Wpf/Views/Assets/CircuitPages.cs and
        // AssetPages.cs moved to src/GridSim.Ui/Assets/Pages/CircuitPages.cs and AssetPages.cs; assertions unchanged (the
        // lookup, the row click-through and the explorer's index routes are asserted, string for string, in the moved
        // files), and strengthened: the WPF page keeps the asserted index-first declaration, word for word, and must
        // derive from the moved model, whose own declaration (the same parameters, with the neutral explorer type) is
        // asserted too; neither file may match a circuit by name or offer a name route, the WPF files may hold no lookup,
        // and UiAssetPagesParityTests.An_unnamed_and_a_repeated_name_circuit_each_open_their_own_index checks the
        // behaviour itself.
        string pages = PresentationConstantsAntiDriftTests.Source("src", "GridSim.Ui", "Assets", "Pages", "CircuitPages.cs");
        string wpfPages = Wpf("Views/Assets/CircuitPages.cs");
        Assert.Contains("public sealed class CircuitDetailVm(int index, int fromBus, int toBus, string? branchName, AssetExplorerVm explorer)", wpfPages, StringComparison.Ordinal);
        Assert.Contains("    : CircuitDetailModel<CircuitStatusRow>(index, fromBus, toBus, branchName, explorer)", wpfPages, StringComparison.Ordinal);
        Assert.Contains("public class CircuitDetailModel<TStatus>(int index, int fromBus, int toBus, string? branchName, AssetExplorerModel explorer)", pages, StringComparison.Ordinal);
        Assert.Contains("AssetLookup.FindBranch(m, index, fromBus, toBus, branchName);", pages, StringComparison.Ordinal);
        Assert.Contains("_explorer.OpenCircuit(captured, b)", pages, StringComparison.Ordinal);
        foreach (string file in new[] { pages, wpfPages })
            Assert.DoesNotContain("m.Branches[k].Name == name", file, StringComparison.Ordinal);
        Assert.DoesNotContain("AssetLookup", wpfPages, StringComparison.Ordinal);

        string explorer = PresentationConstantsAntiDriftTests.Source("src", "GridSim.Ui", "Assets", "Pages", "AssetPages.cs");
        string wpfExplorer = Wpf("Views/Assets/AssetPages.cs");
        Assert.Contains("public void OpenCircuit(int index, GridSim.Core.Model.Branch br) =>", explorer, StringComparison.Ordinal);
        Assert.Contains("else OpenCircuit(index, br);", explorer, StringComparison.Ordinal);
        foreach (string file in new[] { explorer, wpfExplorer })
            Assert.DoesNotContain("OpenCircuit(string", file, StringComparison.Ordinal);
        Assert.Contains("public sealed class AssetExplorerVm : AssetExplorerModel", wpfExplorer, StringComparison.Ordinal);
    }

    // ================================ D-DOC-147: one SCADA priority rule ================================

    [Fact]
    public void D147_the_priorities_are_one_set_of_core_constants_with_frequency_at_P2()
    {
        Assert.Equal("P1", ScadaThresholds.PriorityVoltageOutOfBand);
        Assert.Equal("P2", ScadaThresholds.PriorityFrequencyOutOfBand);
        Assert.Equal("P2", ScadaThresholds.PriorityCorridorAlarm);
        Assert.Equal("P3", ScadaThresholds.PriorityInformation);

        // Neither console writes a priority literal any more: both read the constants.
        string web = PresentationConstantsAntiDriftTests.Source("src", "GridSim.Web", "Sessions", "ScadaService.cs");
        // Test retargeted 2026-10-20 (UI programme 1.6): src/GridSim.Wpf/Views/ScadaVm.cs moved to
        // src/GridSim.Ui/Tabs/Scada/ScadaModel.cs, where the WPF brush AmberBrush became the colour token AmberColour
        // (the same four bytes): the call is pinned with the token's name, the constant, the source tag and their
        // order unchanged. Strengthened: the token is pinned to the old brush's colour (#F3C13A), and the WPF view-model
        // must derive from that model and carry no priority literal either.
        string wpf = PresentationConstantsAntiDriftTests.Source("src", "GridSim.Ui", "Tabs", "Scada", "ScadaModel.cs");
        Assert.DoesNotMatch(new Regex(@"""P[123]"""), web);
        Assert.DoesNotMatch(new Regex(@"""P[123]"""), wpf);
        Assert.Contains("Kind.SystemFreq => ScadaThresholds.PriorityFrequencyOutOfBand,", web, StringComparison.Ordinal);
        Assert.Contains("Add(clock, ScadaThresholds.PriorityFrequencyOutOfBand, AmberColour, \"SYS.F_HZ\"", wpf, StringComparison.Ordinal);
        Assert.Contains("private static readonly UiColour AmberColour = ColourRamps.FromHex(\"#F3C13A\");", wpf, StringComparison.Ordinal);
        string scadaAdapter = Wpf("Views/ScadaVm.cs");
        Assert.Contains("public sealed class ScadaVm : ScadaModel", scadaAdapter, StringComparison.Ordinal);
        Assert.DoesNotMatch(new Regex(@"""P[123]"""), scadaAdapter);
    }

    [Fact]
    public void D147_a_web_frequency_excursion_now_logs_P2()
    {
        // Drive the real web console off a frame that is outside the 49.9-50.1 Hz band (the frequency is the frame's,
        // so build one from a solved fixture with the frequency moved).
        GridSim.Core.Simulation.SimFrame solved = WebProjectionFixtures.Solve(WebProjectionFixtures.MixedFleet());
        GridSim.Core.Simulation.SimFrame low = solved with { FrequencyHz = 49.85 };
        var console = new GridSim.Web.Sessions.ScadaService();
        GridSim.Web.Projection.ScadaDto d = console.Project(low);
        Assert.Contains(d.Events, e => e.Tag == "SYS.FREQ" && e.Priority == "P2");
        Assert.DoesNotContain(d.Events, e => e.Tag == "SYS.FREQ" && e.Priority == "P1");
    }

    // ================================ D-DOC-078: bridge replies pair by id only ================================

    private static string Reply(string? id, string op, string payload = """{"verdict":"FEASIBLE","violations":[]}""") =>
        "{\"type\":\"result\",\"op\":\"" + op + "\"" + (id is null ? "" : ",\"id\":\"" + id + "\"")
        + ",\"at\":\"2026-09-01T11:00:00Z\",\"payload\":" + payload + "}";

    private static (GridSim.Core.Net.GridBridgeClient Client, List<GridSim.Core.Net.BridgeMessageException> Rejected) Client()
    {
        var c = new GridSim.Core.Net.GridBridgeClient();
        var rejected = new List<GridSim.Core.Net.BridgeMessageException>();
        c.MessageRejected += rejected.Add;
        return (c, rejected);
    }

    [Fact]
    public async Task D078_a_reply_with_no_id_pairs_with_no_query_even_when_one_op_matches()
    {
        (var c, var rejected) = Client();
        Task<GridSim.Core.IO.QueryResult> first = c.AddPendingForTest("1", "feasibility");
        Task<GridSim.Core.IO.QueryResult> second = c.AddPendingForTest("2", "feasibility");

        // Before: this completed "1" (the oldest same-op query) with an answer that may have been meant for "2".
        c.DispatchGuarded(Reply(null, "feasibility"));
        Assert.False(first.IsCompleted);
        Assert.False(second.IsCompleted);
        Assert.Single(rejected);
        Assert.Contains("carries no 'id'", rejected[0].Message, StringComparison.Ordinal);
        Assert.Equal(1, c.RejectedMessages);

        // An empty id is the same as none.
        c.DispatchGuarded(Reply("", "feasibility"));
        Assert.False(first.IsCompleted);
        Assert.Equal(2, c.RejectedMessages);

        // The exact replies still pair, each with its own query, in any order.
        c.DispatchGuarded(Reply("2", "feasibility", """{"verdict":"INFEASIBLE","violations":[]}"""));
        c.DispatchGuarded(Reply("1", "feasibility"));
        Assert.True(first.IsCompletedSuccessfully && second.IsCompletedSuccessfully);
        Assert.Equal("INFEASIBLE", (await second).Verdict);
        Assert.Equal("FEASIBLE", (await first).Verdict);
        Assert.Equal(2, c.RejectedMessages);
    }

    [Fact]
    public void D078_a_reply_whose_id_matches_nothing_is_reported_not_guessed()
    {
        (var c, var rejected) = Client();
        Task<GridSim.Core.IO.QueryResult> pending = c.AddPendingForTest("7", "inertia");
        c.DispatchGuarded(Reply("8", "inertia"));
        Assert.False(pending.IsCompleted);
        Assert.Single(rejected);
        Assert.Contains("id '8'", rejected[0].Message, StringComparison.Ordinal);
        Assert.Contains("matches no pending query", rejected[0].Message, StringComparison.Ordinal);
    }

    [Fact]
    public void D078_a_reply_for_the_right_id_but_another_op_fails_that_query_at_once()
    {
        (var c, var rejected) = Client();
        Task<GridSim.Core.IO.QueryResult> pending = c.AddPendingForTest("3", "scenario");
        c.DispatchGuarded(Reply("3", "feasibility"));
        Assert.True(pending.IsFaulted);
        var ex = Assert.IsType<InvalidDataException>(pending.Exception!.InnerException);
        Assert.Contains("answers op 'feasibility', but that query asked op 'scenario'", ex.Message, StringComparison.Ordinal);
        Assert.Single(rejected);
    }

    [Fact]
    public void D078_a_paired_reply_with_an_unreadable_payload_fails_its_query_now_not_after_the_timeout()
    {
        (var c, var rejected) = Client();
        Task<GridSim.Core.IO.QueryResult> pending = c.AddPendingForTest("4", "scenario");
        c.DispatchGuarded(Reply("4", "scenario", """{"verdict":"x","violations":[],"solveExport":{"schema":"nope"}}"""));
        Assert.True(pending.IsFaulted);
        Assert.IsType<InvalidDataException>(pending.Exception!.InnerException);
        Assert.Single(rejected);   // the receive-loop guard reported the message too
    }

    [Fact]
    public void D078_the_legacy_op_fallback_is_gone_from_the_source()
    {
        string src = PresentationConstantsAntiDriftTests.Source("src", "GridSim.Core", "Net", "GridBridgeClient.cs");
        Assert.DoesNotContain("best-effort match on op", src, StringComparison.Ordinal);
        Assert.DoesNotContain("kv.Value.Op == op", src, StringComparison.Ordinal);
    }

    // ================================ D-DOC-079: protections-only cases arm the integrator ================================

    /// <summary>A two-bus case with a protections bundle and NO asset-dynamics bundle: one under-voltage device at the
    /// load bus whose pickup (1.05 pu) sits above the solved voltage, so it operates as soon as it is evaluated.</summary>
    private static GridSim.Core.Model.GridModel ProtectionsOnlyCase()
    {
        var uvHigh = new GridSim.Core.Assets.AssetClass("uv-high", GridSim.Core.Assets.AssetKind.Protection, "protection",
            new Dictionary<string, double> { [GridSim.Core.Assets.AssetParams.UvTripPu] = 1.05, [GridSim.Core.Assets.AssetParams.UvTripDwellS] = 0.0 },
            new Dictionary<string, GridSim.Core.Assets.AssetClassProvenance>
            {
                [GridSim.Core.Assets.AssetClass.DefaultKey] = new(GridSim.Core.Diagnostics.ProvenanceTag.CuratedPlaceholder, "test [VERIFY]"),
            });
        return new GridSim.Core.Model.GridModel
        {
            BaseMva = 100,
            Buses = new[]
            {
                new GridSim.Core.Model.Bus(1, GridSim.Core.Model.BusType.Slack, BaseKv: 132),
                new GridSim.Core.Model.Bus(2, GridSim.Core.Model.BusType.Pq, Pd: 40, Qd: 10, BaseKv: 132),
            },
            Branches = new[] { new GridSim.Core.Model.Branch(1, 2, 0.01, 0.05, Name: "L12") },
            Generators = new[] { new GridSim.Core.Model.Generator(1, Pg: 40, MBase: 100, H: 4.0, Name: "G1") },
            Protections = new GridSim.Core.Assets.Protection.ProtectionModel(
                new GridSim.Core.Assets.AssetClassLibrary(new[] { uvHigh }),
                new[] { new GridSim.Core.Assets.Protection.ProtectionInstance("uv", "uv-high", 2, 25) }),
        };
    }

    [Fact]
    public void D079_the_shared_rule_arms_on_either_bundle()
    {
        GridSim.Core.Model.GridModel prot = ProtectionsOnlyCase();
        Assert.Null(prot.AssetDynamics);
        Assert.True(GridSim.Core.Simulation.LiveSim.HasAuthoredBundle(prot));
        GridSim.Core.Dynamics.DynamicsConfig? cfg = GridSim.Core.Simulation.LiveSim.AuthoredBundleConfig(prot);
        Assert.NotNull(cfg);
        Assert.True(cfg!.EnableAssetDynamics);
        Assert.False(cfg.EnablePassivePlant);   // only the asset switch, as the shells always set

        // A case with neither bundle keeps the plain integrator (null config), as before.
        GridSim.Core.Model.GridModel plain = AssetTestHelpers.Model(null);
        Assert.Null(GridSim.Core.Simulation.LiveSim.AuthoredBundleConfig(plain));
    }

    [Fact]
    public void D079_a_protections_only_case_now_runs_its_protections_in_the_live_loop()
    {
        GridSim.Core.Model.GridModel m = ProtectionsOnlyCase();

        // Before (the shells' old rule: AssetDynamics only): no config, no fleet, the device never acts.
        GridSim.Core.Dynamics.DynamicsConfig? oldRule = m.AssetDynamics is not null
            ? new GridSim.Core.Dynamics.DynamicsConfig { EnableAssetDynamics = true } : null;
        var before = new GridSim.Core.Simulation.LiveSim(m, holdSteady: true, dynamicsConfig: oldRule);
        Assert.Null(before.AssetFleet);
        GridSim.Core.Simulation.SimFrame fb = before.Step(1, 1.0 / 60.0);
        Assert.Equal(0.0, fb.AssetTrippedMw);

        // After: the shared rule arms the fleet and the under-voltage device operates on the solved voltage.
        var after = new GridSim.Core.Simulation.LiveSim(m, holdSteady: true,
            dynamicsConfig: GridSim.Core.Simulation.LiveSim.AuthoredBundleConfig(m));
        Assert.NotNull(after.AssetFleet);
        GridSim.Core.Simulation.SimFrame fa = after.Step(1, 1.0 / 60.0);
        Assert.True(fa.Result.Converged);
        Assert.Equal(25.0, fa.AssetTrippedMw);
    }

    [Fact]
    public void D079_no_bundled_case_changes_every_protections_bundle_ships_with_an_asset_dynamics_bundle()
    {
        // The old and new rules differ only for a case with protections.json and no asset-classes.json. No bundled case
        // is like that, so every bundled number the desktop, web and cycle runner produce is unchanged.
        string data = Path.Combine(AppContext.BaseDirectory, "data");
        string[] withProtections = Directory.GetFiles(data, "protections.json", SearchOption.AllDirectories);
        Assert.NotEmpty(withProtections);
        foreach (string p in withProtections)
            Assert.True(File.Exists(Path.Combine(Path.GetDirectoryName(p)!, "asset-classes.json")),
                $"{p}: a bundled protections-only case would now arm the integrator; record its before / after");
        GridSim.Core.Model.GridModel synth = GridSim.Core.IO.Json.JsonGridLoader.LoadDirectory(Path.Combine(data, "gb-synthetic"));
        Assert.NotNull(synth.AssetDynamics);
        Assert.NotNull(synth.Protections);
    }

    [Fact]
    public void D079_the_desktop_web_and_cycle_runner_use_the_shared_rule()
    {
        // Test retargeted 2026-10-20 (UI programme 0.2): the desktop's LiveSim construction moved from
        // src/GridSim.Wpf/MainWindow.xaml.cs to src/GridSim.Ui/Hosting/SimHost.cs (LoadLive), which the window now drives;
        // the assertions read it there, unchanged. Strengthened: the window itself must build no LiveSim at all, so no
        // desktop path can bypass the shared rule.
        string wpf = PresentationConstantsAntiDriftTests.Source("src", "GridSim.Ui", "Hosting", "SimHost.cs");
        string web = PresentationConstantsAntiDriftTests.Source("src", "GridSim.Web", "Sessions", "SimSession.cs");
        string cycle = PresentationConstantsAntiDriftTests.Source("src", "GridSim.Core", "Scheduling", "CycleRunner.cs");
        foreach (string src in new[] { wpf, web, cycle })
        {
            Assert.Contains("LiveSim.AuthoredBundleConfig(model)", src, StringComparison.Ordinal);
            Assert.DoesNotMatch(new Regex(@"AssetDynamics is not null\s*\?\s*new"), src);
        }
        Assert.DoesNotContain("new LiveSim(", Wpf("MainWindow.xaml.cs"), StringComparison.Ordinal);
    }

    // ================================ D-DOC-081: cycle --jobs refuses unknown keys ================================

    [Theory]
    [InlineData("bogus", "Unknown analysis job 'bogus'")]
    [InlineData("n-1,bogus,zzz", "Unknown analysis jobs 'bogus', 'zzz'")]
    [InlineData(",", "No analysis job selected")]
    [InlineData("", "No analysis job selected")]
    [InlineData(" , ", "No analysis job selected")]
    public void D081_unknown_or_empty_job_lists_are_refused_naming_the_valid_keys(string csv, string expected)
    {
        var ex = Assert.Throws<ArgumentException>(() => GridSim.Core.Scheduling.AnalysisJobLibrary.ParseJobKeys(csv));
        Assert.Contains(expected, ex.Message, StringComparison.Ordinal);
        Assert.Contains("watch, n-1, nminus1, harmonics, rocof, physics, conformance", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void D081_known_keys_parse_ignoring_case_and_build_the_same_jobs_as_before()
    {
        IReadOnlySet<string> keys = GridSim.Core.Scheduling.AnalysisJobLibrary.ParseJobKeys(" N-1 ,Harmonics,watch ");
        Assert.Equal(3, keys.Count);
        GridSim.Core.Model.GridModel m = GridSim.Core.IO.IeeeCases.Case9();
        var opt = new GridSim.Core.Scheduling.CycleOptions { NMinus1Shards = 2 };
        IReadOnlyList<GridSim.Core.Scheduling.AnalysisJob> jobs = GridSim.Core.Scheduling.AnalysisJobLibrary.Build(m, opt, keys);
        Assert.Equal(new[] { "watch", "n-1:hot", "n-1:lp:0", "n-1:lp:1", "harmonics" }, jobs.Select(j => j.Id).ToArray());

        // Null still means every job, in the documented order (unchanged).
        IReadOnlyList<GridSim.Core.Scheduling.AnalysisJob> all = GridSim.Core.Scheduling.AnalysisJobLibrary.Build(m, opt);
        Assert.Equal(new[] { "watch", "n-1:hot", "n-1:lp:0", "n-1:lp:1", "harmonics", "rocof", "physics" },
            all.Select(j => j.Id).ToArray());
    }

    [Fact]
    public void D081_build_refuses_an_unknown_key_or_a_set_that_builds_nothing()
    {
        GridSim.Core.Model.GridModel m = GridSim.Core.IO.IeeeCases.Case9();
        var opt = new GridSim.Core.Scheduling.CycleOptions();
        Assert.Throws<ArgumentException>(() => GridSim.Core.Scheduling.AnalysisJobLibrary.Build(m, opt, new HashSet<string> { "bogus" }));
        Assert.Throws<ArgumentException>(() => GridSim.Core.Scheduling.AnalysisJobLibrary.Build(m, opt, new HashSet<string>()));
        // A known key in a case-SENSITIVE set that matches no job ("N-1" against "n-1") is refused, not run empty.
        var ex = Assert.Throws<ArgumentException>(() =>
            GridSim.Core.Scheduling.AnalysisJobLibrary.Build(m, opt, new HashSet<string>(StringComparer.Ordinal) { "N-1" }));
        Assert.Contains("built no analysis job", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void D081_cycle_jobs_bogus_exits_2_with_the_message_and_never_claims_HP_met()
    {
        CliProcess.Result r = CliProcess.RunBuilt("cycle", "case9", "--jobs", "bogus", "--cycles", "1");
        Assert.True(r.ExitCode == 2, $"expected exit 2\n{r}");
        Assert.Contains("error: cycle --jobs: Unknown analysis job 'bogus'. Choose from: watch, n-1, nminus1, harmonics, rocof, physics, conformance.",
            r.StdErr, StringComparison.Ordinal);
        Assert.DoesNotContain("(Parameter", r.StdErr, StringComparison.Ordinal);
        Assert.DoesNotContain("HP met every cycle", r.StdOut, StringComparison.Ordinal);
    }

    // ================================ D-DOC-201: tools/AssetSmoke builds with the solution ================================

    [Fact]
    public void D201_asset_smoke_is_in_the_solution_and_uses_current_namespaces()
    {
        string slnx = PresentationConstantsAntiDriftTests.Source("GridSim.slnx");
        Assert.Contains("<Project Path=\"tools/AssetSmoke/AssetSmoke.csproj\" />", slnx, StringComparison.Ordinal);
        string csproj = PresentationConstantsAntiDriftTests.Source("tools", "AssetSmoke", "AssetSmoke.csproj");
        Assert.Contains("<EnableWindowsTargeting>true</EnableWindowsTargeting>", csproj, StringComparison.Ordinal);   // Linux CI builds it
        Assert.DoesNotContain("<Version>", csproj, StringComparison.Ordinal);   // the one version lives in Directory.Build.props
        string src = PresentationConstantsAntiDriftTests.Source("tools", "AssetSmoke", "Program.cs");
        Assert.DoesNotContain("using GridSim.Wpf.Sim;", src, StringComparison.Ordinal);
        Assert.Contains("using GridSim.Core.Simulation;", src, StringComparison.Ordinal);
    }

    // ================================ D-DOC-202: the dead priority parameter ================================

    [Fact]
    public void D202_harmonics_and_watch_list_take_no_priority_and_are_high_tier()
    {
        var opt = new GridSim.Core.Scheduling.CycleOptions();
        Assert.Equal(GridSim.Core.Scheduling.JobPriority.High, GridSim.Core.Scheduling.AnalysisJobLibrary.Harmonics(opt).Priority);
        Assert.Equal(GridSim.Core.Scheduling.JobPriority.High, GridSim.Core.Scheduling.AnalysisJobLibrary.WatchList().Priority);
        // The parameter is gone, not merely unused: neither factory has a JobPriority parameter.
        foreach (string name in new[] { "Harmonics", "WatchList" })
        {
            System.Reflection.MethodInfo mi = typeof(GridSim.Core.Scheduling.AnalysisJobLibrary).GetMethod(name)!;
            Assert.DoesNotContain(mi.GetParameters(), p => p.ParameterType == typeof(GridSim.Core.Scheduling.JobPriority));
        }
    }
}
