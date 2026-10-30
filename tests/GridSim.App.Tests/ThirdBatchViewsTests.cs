using System.Diagnostics;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.VisualTree;
using GridSim.App.Controls;
using GridSim.App.Tabs;
using GridSim.Core.Analysis;
using GridSim.Core.Diagnostics;
using GridSim.Core.IO;
using GridSim.Core.Licensing;
using GridSim.Core.Model;
using GridSim.Core.PowerFlow;
using GridSim.Core.Presentation;
using GridSim.Core.Simulation;
using GridSim.Ui.Commands;
using GridSim.Ui.Dashboard;
using GridSim.App.Run;
using GridSim.Ui.Models;
using GridSim.Ui.Presentation;
using GridSim.Ui.Shell;
using GridSim.Ui.Tabs.AuditLog;
using GridSim.Ui.Tabs.Experimental;
using GridSim.Ui.Tabs.HumanScale;
using GridSim.Ui.Tabs.NesoRp1;
using Xunit;

namespace GridSim.App.Tests;

/// <summary>
/// UI programme Phase 1.6 (third batch): THE HUMAN SCALE, NESO RP1 and EXPERIMENTAL views and the audit-log window in
/// the Avalonia app, on the headless platform with Skia rendering. Each view renders standalone, its shared model fed
/// as the WPF dashboard feeds the tab (the frames of the shipped MATPOWER case9 from the app's run pipeline, with the
/// dashboard's clock); after several frames every text the view shows equals its model's value, and each colour is the
/// token's exact bytes. The NESO RP1 row clicks reach the model's drill-downs and the page hides off 50 Hz; the EXPERIMENTAL
/// models run off the UI thread and come back through the view's poster; the audit-log window shows a fixture log and
/// flags its tampered line as WPF does (only the TAMPERED verdict, in the theme's red, no entries).
/// </summary>
public sealed class ThirdBatchViewsTests
{
    private const int Frames = 6;

    // ---------------------------------------------------------------- helpers

    private static string RepoRoot()
    {
        for (DirectoryInfo? d = new(AppContext.BaseDirectory); d is not null; d = d.Parent)
            if (File.Exists(Path.Combine(d.FullName, "GridSim.slnx"))) return d.FullName;
        throw new InvalidOperationException("The GridSim checkout (GridSim.slnx) was not found above " + AppContext.BaseDirectory);
    }

    private static void Await(Task task)
    {
        PumpUntil(() => task.IsCompleted, "a task to finish");
        task.GetAwaiter().GetResult();
    }

    /// <summary>
    /// Load the shipped MATPOWER case9 through the app's run pipeline, play it, and hand each of the first
    /// <paramref name="n"/> frames to <paramref name="feed"/> with the clock a dashboard shows for it (a dashboard model
    /// takes each frame first, as the WPF dashboard does before it feeds its tabs). Returns the last frame and clock.
    /// </summary>
    private static (ArrivedFrame Last, string Clock) PlayCase9(int n, Action<ArrivedFrame, string> feed)
    {
        string file = Path.Combine(RepoRoot(), "data", "matpower", "lib", "case9.m");
        Assert.True(File.Exists(file), "missing test case " + file);
        var run = new RunViewModel(new LicenceGate(Entitlement.FreeDefault));
        var dash = new DashboardModel();
        ArrivedFrame? last = null;
        int got = 0;
        run.FrameArrived += (_, f) =>
        {
            if (got >= n) return;
            dash.Update(f.Frame, f.Boundaries, f.SolveMs);
            feed(f, dash.Clock);
            last = f;
            got++;
        };
        Await(run.ChooseSpeedAsync(60));
        Task<string?> load = run.LoadAsync(new ModelSource("case9", Path.GetDirectoryName(file)!, file));
        Await(load);
        Assert.Null(load.Result);   // loaded, not refused
        PumpUntil(() => got >= n, $"{n} frames of case9");
        Await(run.StopLoopAsync());
        Assert.False(run.HasError, run.ErrorText);
        return (last!, dash.Clock);
    }

    private static void PumpUntil(Func<bool> done, string what, int timeoutMs = 120_000)
    {
        var clock = Stopwatch.StartNew();
        while (!done())
        {
            if (clock.ElapsedMilliseconds > timeoutMs) throw new TimeoutException($"Timed out after {timeoutMs} ms waiting for {what}");
            Dispatcher.UIThread.RunJobs();
            Thread.Sleep(2);
        }
        Dispatcher.UIThread.RunJobs();
    }

    /// <summary>Show <paramref name="view"/> alone in a window bound to <paramref name="model"/>, rendered once.</summary>
    private static Window Show(Control view, object model)
    {
        view.DataContext = model;
        var w = new Window { Width = 1600, Height = 1000, Content = view };
        w.Show();
        Render(w);
        Assert.True(view.IsEffectivelyVisible, $"{view.GetType().Name} is not showing");
        Assert.True(view.Bounds.Width > 100 && view.Bounds.Height > 100, $"{view.GetType().Name} was not laid out: {view.Bounds}");
        return w;
    }

    private static void Render(TopLevel w)
    {
        Dispatcher.UIThread.RunJobs();
        w.CaptureRenderedFrame()?.Dispose();
        Dispatcher.UIThread.RunJobs();
    }

    private static TextBlock Block(Control view, string name) =>
        view.FindControl<TextBlock>(name) ?? throw new InvalidOperationException($"no TextBlock {name}");

    private static string Text(Control view, string name) => Block(view, name).Text ?? "";

    private static Color Fore(Control view, string name) => Assert.IsAssignableFrom<ISolidColorBrush>(Block(view, name).Foreground).Color;

    private static Color Col(UiColour c) => Color.FromArgb(c.A, c.R, c.G, c.B);

    private static ItemsControl List(Control view, string name) =>
        view.FindControl<ItemsControl>(name) ?? throw new InvalidOperationException($"no ItemsControl {name}");

    /// <summary>Every text shown in the realised row at <paramref name="index"/> of the named items control, in order.</summary>
    private static List<string> RowTexts(Control view, string list, int index)
    {
        Control row = List(view, list).ContainerFromIndex(index) ?? throw new InvalidOperationException($"{list}[{index}] is not realised");
        // A bar's own percentage text (inside the theme's ProgressBar template, never shown here) is not a row text.
        return row.GetVisualDescendants().OfType<TextBlock>().Where(t => t.FindAncestorOfType<ProgressBar>() is null)
            .Select(t => t.Text ?? "").ToList();
    }

    private static void Click(Button b) => b.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));

    private static Button RowButton(Control view, string list, int index) =>
        (List(view, list).ContainerFromIndex(index) ?? throw new InvalidOperationException($"{list}[{index}] is not realised"))
            .GetVisualDescendants().OfType<Button>().First();

    // ---------------------------------------------------------------- THE HUMAN SCALE

    [AvaloniaFact]
    public void HumanScale_view_shows_its_models_texts_and_colours_after_frames_of_case9()
    {
        var model = new HumanScaleModel();
        var view = new HumanScaleView();
        Window w = Show(view, model);
        try
        {
            // Before any frame: the WPF starting texts.
            Assert.Equal("-", Text(view, "HeroValueText"));
            Assert.Equal(Col(DashboardColours.Gray), Fore(view, "TwistTotalText"));

            (_, string clock) = PlayCase9(Frames, (f, c) => model.Update(f.Frame, c));
            Render(w);
            Assert.Equal(clock, model.Clock);
            foreach ((string box, string want) in new[]
                     {
                         ("HeroValueText", model.HeroValue), ("HeroLineText", model.HeroLine),
                         ("Fact1Text", model.Fact1), ("Fact2Text", model.Fact2), ("Fact3Text", model.Fact3),
                         ("Fact4Text", model.Fact4), ("Fact5Text", model.Fact5),
                         ("TwistTotalText", model.TwistTotal), ("TwistToSlipText", model.TwistToSlip),
                         ("TwistCorridorText", model.TwistCorridor), ("TwistLeadText", model.TwistLead),
                         ("TorqueFtLbText", model.TorqueFtLb), ("TorqueNativeText", model.TorqueNative), ("TorqueCarsText", model.TorqueCars),
                         ("ShaftHpText", model.ShaftHp), ("ShaftNativeText", model.ShaftNative), ("ShaftCarsText", model.ShaftCars),
                         ("GridRpmText", model.GridRpm), ("BladeTipText", model.BladeTip),
                         ("SpinKwhText", model.SpinKwh), ("SpinNativeText", model.SpinNative), ("SpinCarsText", model.SpinCars),
                         ("SpinHomesText", model.SpinHomes), ("SpinKettlesText", model.SpinKettles),
                         ("RegenHpText", model.RegenHp), ("RegenDirText", model.RegenDir), ("RegenNoteText", model.RegenNote),
                         ("RocofRpmSText", model.RocofRpmS),
                         ("DemandNativeText", model.DemandNative), ("DemandKettlesText", model.DemandKettles),
                         ("DemandHomesText", model.DemandHomes), ("DemandShowersText", model.DemandShowers),
                         ("LossNativeText", model.LossNative), ("LossHomesText", model.LossHomes), ("LossKettlesText", model.LossKettles),
                     })
                Assert.Equal(want, Text(view, box));
            Assert.NotEqual("-", model.HeroValue);
            Assert.EndsWith(" hp", model.HeroValue, StringComparison.Ordinal);
            Assert.Equal(Col(model.TwistColour), Fore(view, "TwistTotalText"));
            Assert.Equal(Col(model.RegenColour), Fore(view, "RegenHpText"));
            Assert.Equal(Col(model.RegenColour), Fore(view, "RegenDirText"));
            Border bar = view.FindControl<Border>("TwistBar")!;
            Assert.Equal(model.TwistBarPx, bar.Width);
            Assert.Equal(Col(model.TwistColour), Assert.IsAssignableFrom<ISolidColorBrush>(bar.Background).Color);

            // The regional gauge: one row per band, each showing the band's label, lead and detail.
            Assert.NotEmpty(model.Regions);
            Assert.Equal(model.Regions.Count, List(view, "RegionList").ItemCount);
            TwistRegionRowModel r0 = model.Regions[0];
            Assert.Equal(new[] { r0.Label, r0.DegText, r0.Detail }, RowTexts(view, "RegionList", 0));
            Assert.Equal(model.Machines.Count, List(view, "MachineList").ItemCount);
        }
        finally { w.Close(); }
    }

    // ---------------------------------------------------------------- NESO RP1

    private static GridModel AtNominal(GridModel m, double hz) => new()
    {
        BaseMva = m.BaseMva, Buses = m.Buses, Generators = m.Generators, Branches = m.Branches, Oltcs = m.Oltcs,
        Boundaries = m.Boundaries, FrequencyHz = hz, Name = m.Name,
    };

    [AvaloniaFact]
    public void NesoRp1_view_shows_its_models_texts_and_colours_and_its_rows_drill_down()
    {
        var model = new NesoRp1Model();
        var pages = new List<Rp1InspectPage>();
        model.InspectRequested += pages.Add;
        var view = new NesoRp1View();
        Window w = Show(view, model);
        try
        {
            (ArrivedFrame hf, string clock) = PlayCase9(Frames, (f, c) => model.Update(f.Frame, f.Boundaries, c));
            // The bridge's per-tick blocks, as the WPF dashboard hands them over (balancing actions and interconnectors).
            model.SetRp1Data(
                new BridgeProtocol.OperatorBlock
                {
                    SynchronousInertiaGvaS = 120.5, MarketInertiaGvaS = 20.25,
                    BalancingActions = new() { new() { BmUnit = "T_DRAXX-1", Kind = "OFFER", LevelMw = 412.6, FuelType = "BIOMASS", Synchronous = true } },
                    Constraints = new() { new() { Name = "B6 SCOTEX", CostGbp = 123_456, Binding = true } },
                },
                new BridgeProtocol.MarketBlock
                {
                    ImbalancePriceGbpMwh = 72.345,
                    Interconnectors = new() { new() { Name = "IFA", FlowMw = 1999.5 }, new() { Name = "ElecLink", FlowMw = 1000.25 } },
                },
                new DateTime(2026, 7, 1, 12, 0, 0, DateTimeKind.Utc));
            Render(w);

            foreach ((string box, string want) in new[]
                     {
                         ("HeaderLfddText", model.LfddGrade), ("LfddText", model.LfddGrade), ("PurchasedText", model.PurchasedText),
                         ("LfddPurchasedText", model.PurchasedText), ("PurchasedDesignText", model.PurchasedDesign),
                         ("LargestUnitText", model.LargestUnit), ("LargestUnitLabelText", model.LargestUnitLabel),
                         ("LargestGroupText", model.LargestGroup), ("LargestGroupLabelText", model.LargestGroupLabel),
                         ("DesignBasisText", model.DesignBasis), ("FictionText", model.Fiction),
                         ("StabilityTrendTitleText", model.StabilityTrendTitle), ("DynamicMifText", model.DynamicMif),
                         ("RocofText", model.Rocof), ("NarrativeText", model.Narrative), ("ImbalancePriceText", model.ImbalancePrice),
                         ("BmSummaryText", model.BmSummary), ("BoundaryNoteText", model.BoundaryNote),
                         ("SynchronousText", model.SynchronousGw), ("ConverterText", model.ConverterGw),
                     })
                Assert.Equal(want, Text(view, box));
            Assert.Equal("STABILITY TREND  *  last 60 frames", model.StabilityTrendTitle);
            Assert.Equal($"GBP {72.345:F2}", model.ImbalancePrice);   // the WPF format, in the same culture
            Assert.Equal(Col(model.PurchasedColour), Fore(view, "PurchasedText"));
            Assert.Equal(Col(model.UnitColour), Fore(view, "LargestUnitText"));
            Assert.Equal(Col(model.GroupColour), Fore(view, "LargestGroupText"));
            Assert.Equal(Col(model.LfddColour), Assert.IsAssignableFrom<ISolidColorBrush>(view.FindControl<Border>("LfddChip")!.Background).Color);
            Assert.Equal(model.InertiaTotalText, view.FindControl<Gauge>("InertiaTotalGauge")!.ValueText);
            Assert.Equal(model.InertiaIntrinsicValue, view.FindControl<Gauge>("InertiaIntrinsicGauge")!.Value);
            Assert.Equal(model.PurchasedValue, view.FindControl<Gauge>("PurchasedGauge")!.Value);
            Assert.Same(model.StabilityTrace, view.FindControl<Sparkline>("StabilityTraceChart")!.Values);
            Assert.Same(model.PriceTrace, view.FindControl<Sparkline>("PriceTraceChart")!.Values);
            Assert.Equal("STREAMED", view.FindControl<Sparkline>("PriceTraceChart")!.Caption);

            // The lists: one row per model row, each showing the model's texts in the WPF order.
            Assert.NotEmpty(model.GenStack);
            Assert.Equal(model.GenStack.Count, List(view, "GenStackList").ItemCount);
            Rp1GenRowModel g0 = model.GenStack[0];
            Assert.Equal(new[] { g0.Kind, g0.Fuel, g0.Output, g0.Headroom, "  ", g0.Inertia }, RowTexts(view, "GenStackList", 0));
            Assert.Equal(model.InertiaBySource.Count, List(view, "InertiaBySourceList").ItemCount);
            Assert.Equal(model.BmDelta.Count, List(view, "BmDeltaList").ItemCount);
            Rp1BmRowModel b0 = Assert.Single(model.BmDelta);
            Assert.Equal(new[] { b0.Unit, b0.Kind, b0.Mw }, RowTexts(view, "BmDeltaList", 0));
            Assert.Equal(2, model.Interconnectors.Count);
            Assert.Equal(new[] { model.Interconnectors[0].Name, model.Interconnectors[0].Flow }, RowTexts(view, "InterconnectorList", 0));
            Assert.Equal(model.BoundaryBars.Count, List(view, "BoundaryList").ItemCount);
            Assert.Equal(model.ConstraintCosts.Count, List(view, "ConstraintList").ItemCount);
            Assert.Equal(new[] { "B6 SCOTEX", model.ConstraintCosts[0].CostPerHour }, RowTexts(view, "ConstraintList", 0));

            // The WPF commands are the row clicks: each reaches the model's drill-down and raises its page.
            Click(RowButton(view, "GenStackList", 0));
            Rp1InspectPage fuelPage = Assert.Single(pages);
            Assert.Equal(g0.Fuel.ToUpperInvariant(), fuelPage.Title);
            Click(RowButton(view, "BmDeltaList", 0));
            Assert.Equal("T_DRAXX-1", pages[^1].Title);
            Click(RowButton(view, "InterconnectorList", 0));
            Assert.Equal(model.Interconnectors[0].Name.ToUpperInvariant(), pages[^1].Title);
            Assert.Equal("shares a correlated-trip corridor", pages[^1].Subtitle);
            Assert.NotNull(view.FindControl<ContentControl>("InspectorSlot"));

            // N301: off 50 Hz the page body hides and the one-line reason shows (the WPF Visibility pair).
            Assert.True(view.FindControl<Grid>("PageBody")!.IsVisible);
            Assert.False(view.FindControl<Border>("NotApplicableCard")!.IsVisible);
            model.Update(hf.Frame with { Model = AtNominal(hf.Frame.Model, 60.0) }, hf.Boundaries, clock);
            Render(w);
            Assert.False(view.FindControl<Grid>("PageBody")!.IsVisible);
            Assert.False(view.FindControl<StackPanel>("HeaderLfdd")!.IsVisible);
            Assert.True(view.FindControl<Border>("NotApplicableCard")!.IsVisible);
            Assert.Equal(FrequencyRiskPresenter.Rp1NotApplicableReason(60.0), Text(view, "NotApplicableText"));
        }
        finally { w.Close(); }
    }

    // ---------------------------------------------------------------- EXPERIMENTAL

    [AvaloniaFact]
    public async Task Experimental_view_shows_its_models_results_computed_off_the_ui_thread()
    {
        // The poster the feed line gives TabModels: on the UI thread it posts back to it.
        Assert.True(Dispatcher.UIThread.CheckAccess());
        Assert.NotNull(ExperimentalView.UiPoster());
        Assert.Null(await Task.Run(ExperimentalView.UiPoster));   // no UI loop on a worker: inline
        var model = new ExperimentalModel(ExperimentalView.UiPoster);
        var view = new ExperimentalView();
        Window w = Show(view, model);
        try
        {
            Assert.Equal(" - ", Text(view, "GateText"));
            PlayCase9(Frames, (f, _) => model.Update(f.Frame));
            // Hidden: nothing computed (R020); shown: the newest frame is computed on a worker and shown here.
            Assert.Empty(model.Laws);
            model.Active = true;
            Assert.Empty(model.Laws);   // the worker has the frame; nothing is shown until its result is posted back
            PumpUntil(() => model.Laws.Count > 0, "the conformance audit to come back to the UI thread");
            Render(w);

            foreach ((string box, string want) in new[]
                     {
                         ("StatusText", model.Status), ("GateText", model.Gate), ("ReciprocityText", model.Reciprocity),
                         ("WaveText", model.Wave), ("WaveSplitText", model.WaveSplit), ("CarbonText", model.Carbon),
                         ("TraceStatusText", model.TraceStatus), ("ProvenanceText", model.Provenance),
                     })
                Assert.Equal(want, Text(view, box));
            Assert.StartsWith("ADMISSIBLE", model.Gate, StringComparison.Ordinal);
            Assert.Equal(Col(model.GateColour), Fore(view, "GateText"));
            Assert.Equal(model.Laws.Count, List(view, "LawList").ItemCount);
            LawRowModel l0 = model.Laws[0];
            Assert.Equal(new[] { l0.Number, l0.Name, l0.StatusText, l0.Residual, l0.Gated, l0.Unit }, RowTexts(view, "LawList", 0));
            TextBlock status = List(view, "LawList").ContainerFromIndex(0)!.GetVisualDescendants().OfType<TextBlock>()
                .Single(t => t.Text == l0.StatusText && t.FontWeight == FontWeight.SemiBold);
            Assert.Equal(Col(l0.StatusColour), Assert.IsAssignableFrom<ISolidColorBrush>(status.Foreground).Color);
            Assert.Equal(model.TopFlows.Count, List(view, "FlowList").ItemCount);
            Assert.NotEmpty(model.TopFlows);
            TraceRowModel t0 = model.TopFlows[0];
            Assert.Equal(new[] { t0.From, "->", t0.To, t0.Mw }, RowTexts(view, "FlowList", 0));
        }
        finally { w.Close(); }
    }

    // ---------------------------------------------------------------- the audit-log window

    private static string Log(TempFolder temp, string name, bool tamper)
    {
        string path = temp.File(name);
        using (AuditLogWriter writer = AuditLogWriter.Open(path))
        {
            writer.Append(new DateTime(2026, 10, 4, 9, 30, 0, DateTimeKind.Utc), null, ProvenanceTag.DerivedFromRecorded, "actor=a action=estimate outcome=exit=0 SP1");
            writer.Append(new DateTime(2026, 10, 4, 9, 31, 0, DateTimeKind.Utc), null, ProvenanceTag.DerivedFromRecorded, "actor=a action=estimate outcome=exit=3 SP2");
        }
        if (tamper)
        {
            // The fixture's tampered line: the second entry's text is changed after it was chained.
            string[] text = File.ReadAllLines(path);
            text[1] = text[1].Replace("SP2", "SP9", StringComparison.Ordinal);
            File.WriteAllText(path, string.Join("\n", text) + "\n");
        }
        return path;
    }

    private static Color Theme(string key) =>
        Assert.IsAssignableFrom<ISolidColorBrush>(Avalonia.Application.Current!.FindResource(key)).Color;

    [AvaloniaFact]
    public void AuditLog_window_shows_a_fixture_log_and_flags_the_tampered_line_as_wpf_does()
    {
        using var temp = new TempFolder();
        string good = Log(temp, "good.jsonl", tamper: false);
        string bad = Log(temp, "tampered.jsonl", tamper: true);
        var w = new AuditLogWindow();
        string? next = null;
        int asked = 0;
        w.ChooseFile = () => { asked++; return Task.FromResult(next); };
        w.Show();
        try
        {
            Render(w);
            // The WPF window's texts, from the shared model.
            Assert.Equal("GridSim - audit log (verified)", w.Title);
            Assert.Equal(AuditLogModel.Note, w.FindControl<TextBlock>("NoteText")!.Text);
            Assert.Equal(" OPEN LOG... ", w.FindControl<Button>("OpenButton")!.Content);
            TextBlock verdict = w.FindControl<TextBlock>("VerdictText")!;
            TextBox entries = w.FindControl<TextBox>("EntriesBox")!;
            Assert.Equal(Theme("Text"), Assert.IsAssignableFrom<ISolidColorBrush>(verdict.Foreground).Color);

            void Open(string? file)
            {
                next = file;
                Click(w.FindControl<Button>("OpenButton")!);
                PumpUntil(() => true, "the open to run");
                Render(w);
            }

            // A log that verifies: every entry and the OK line, in green.
            Open(good);
            AuditLogDisplayResult ok = AuditLogDisplay.FromFile(good);
            Assert.True(ok.Verified);
            Assert.Equal(ok.Verdict, verdict.Text);
            Assert.Equal(string.Join(Environment.NewLine, ok.EntryLines), entries.Text);
            Assert.Equal(Theme("Green"), Assert.IsAssignableFrom<ISolidColorBrush>(verdict.Foreground).Color);

            // The tampered line: only the TAMPERED verdict, in red, and no entries at all (not even the clean prefix).
            Open(bad);
            AuditLogDisplayResult tampered = AuditLogDisplay.FromFile(bad);
            Assert.False(tampered.Verified);
            Assert.Contains("TAMPERED", verdict.Text, StringComparison.Ordinal);
            Assert.Equal(tampered.Verdict, verdict.Text);
            Assert.True(string.IsNullOrEmpty(entries.Text), $"a tampered log showed entries: {entries.Text}");
            Assert.Equal(Theme("Red"), Assert.IsAssignableFrom<ISolidColorBrush>(verdict.Foreground).Color);

            // A typed anchor that does not parse: NOT CHECKED, and the file is never asked for.
            int before = asked;
            w.FindControl<TextBox>("CountBox")!.Text = "abc";
            Dispatcher.UIThread.RunJobs();
            Assert.Equal("abc", w.Model.AnchorCount);   // the box is bound to the model (the WPF window read it on click)
            Open(good);
            Assert.Equal(before, asked);
            Assert.StartsWith("NOT CHECKED: ", verdict.Text, StringComparison.Ordinal);
            Assert.Equal(Theme("Red"), Assert.IsAssignableFrom<ISolidColorBrush>(verdict.Foreground).Color);

            // The real anchor of the good log verifies it; a cancelled dialog leaves the verdict and clears the entries.
            w.FindControl<TextBox>("CountBox")!.Text = "2";
            w.FindControl<TextBox>("HeadBox")!.Text = ok.Anchor!.HeadHash;
            Dispatcher.UIThread.RunJobs();
            Open(good);
            Assert.Equal(AuditLogDisplay.FromFile(good, ok.Anchor).Verdict, verdict.Text);
            Assert.Equal(Theme("Green"), Assert.IsAssignableFrom<ISolidColorBrush>(verdict.Foreground).Color);
            string shown = verdict.Text!;
            Open(null);
            Assert.Equal(shown, verdict.Text);
            Assert.True(string.IsNullOrEmpty(entries.Text));
        }
        finally { w.Close(); }
    }

    [AvaloniaFact]
    public async Task AuditLog_command_definition_is_ready_to_register_and_opens_the_viewer_over_its_owner()
    {
        var owner = new Window { Width = 400, Height = 300 };
        owner.Show();
        try
        {
            AppCommand cmd = AuditLogWindow.Command(() => owner);
            Assert.Equal("file.auditLog", cmd.Id);
            Assert.Equal(AuditLogWindow.CommandId, cmd.Id);
            Assert.Equal("Audit log...", cmd.Title);
            Assert.Equal("File/Audit log...", cmd.MenuPath);
            Assert.Null(cmd.Gesture);
            Assert.Equal(CommandModes.Both, cmd.Modes);
            Assert.Equal(CommandPlatforms.Desktop, cmd.Platforms);

            // It registers beside the shell's own commands with no clash (nothing in the app registers it yet).
            var registry = new CommandRegistry();
            ShellCommands.Register(registry, new RecordingShellActions());
            registry.Register(cmd);
            Assert.Same(cmd, registry.Get("file.auditLog"));

            // Running it opens a modeless viewer owned by the main window, as the WPF button does.
            await cmd.RunAsync();
            Dispatcher.UIThread.RunJobs();
            AuditLogWindow viewer = Assert.Single(owner.OwnedWindows.OfType<AuditLogWindow>());
            Assert.True(viewer.IsVisible);
            viewer.Close();
            Assert.Throws<ArgumentNullException>(() => AuditLogWindow.Command(null!));
        }
        finally { owner.Close(); }
    }
}
