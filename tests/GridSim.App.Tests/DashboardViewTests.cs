using System.Collections;
using System.Diagnostics;
using System.Reflection;
using System.Runtime.InteropServices;
using Avalonia.Controls;
using Avalonia.Controls.Shapes;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Media.Immutable;
using Avalonia.Platform;
using Avalonia.Styling;
using Avalonia.Threading;
using Avalonia.VisualTree;
using GridSim.App.Controls;
using GridSim.App.Dashboard;
using GridSim.App.Run;
using GridSim.Core.Licensing;
using GridSim.Ui.Dashboard;
using GridSim.Ui.Models;
using GridSim.Ui.Presentation;
using GridSim.Ui.Schematic;
using Xunit;

namespace GridSim.App.Tests;

/// <summary>
/// UI programme Phase 1.4: the dashboard tiles (src/GridSim.App/Dashboard) on the headless platform, fed by a real
/// <see cref="RunViewModel"/> playing the shipped case9 and GB reduced (data/uk) cases.
/// <list type="bullet">
/// <item><description>Feed parity: an oracle <see cref="DashboardModel"/>, driven by this test with the calls the WPF
/// window's code-behind makes at each point (written out here from src/GridSim.Wpf/MainWindow.xaml.cs), ends every
/// frame in exactly the state of the dashboard's own model: every display property and every list row, compared
/// exactly.</description></item>
/// <item><description>View parity: on every frame, every tile's text is the model's string (exact), every colour is
/// the brush for the model's token (the four bytes, and the theme's own brush for a theme colour), and every panel's
/// visibility is the model's bool.</description></item>
/// <item><description>A non-converged frame raises the WPF STATE NOT VALID banner, veil, lamp, alarm and log line; a
/// model switch resets the dashboard as WPF does; Detach stops updates; one smoke render draws the tiles.</description></item>
/// </list>
/// Every value checked comes from the shared DashboardModel; nothing is recomputed here except where the oracle itself
/// is driven, with the same calls on the same frames.
/// </summary>
public sealed class DashboardViewTests
{
    // ---------------------------------------------------------------- cases and pumping

    private static string RepoRoot()
    {
        for (DirectoryInfo? d = new(AppContext.BaseDirectory); d is not null; d = d.Parent)
            if (File.Exists(System.IO.Path.Combine(d.FullName, "GridSim.slnx"))) return d.FullName;
        throw new InvalidOperationException("The GridSim checkout (GridSim.slnx) was not found above " + AppContext.BaseDirectory);
    }

    private static ModelSource Case9()
    {
        string file = System.IO.Path.Combine(RepoRoot(), "data", "matpower", "lib", "case9.m");
        Assert.True(File.Exists(file), "missing test case " + file);
        return new ModelSource("case9", System.IO.Path.GetDirectoryName(file)!, file);
    }

    /// <summary>The bundled GB reduced case, labelled as the model catalogue labels it.</summary>
    private static ModelSource Uk()
    {
        string dir = System.IO.Path.Combine(RepoRoot(), "data", "uk");
        Assert.True(File.Exists(System.IO.Path.Combine(dir, "system.json")), "missing test case " + dir);
        return new ModelSource("GB reduced  10-zone", dir);
    }

    /// <summary>
    /// case9 with every load (P and Q) multiplied by <paramref name="factor"/>, written into <paramref name="temp"/>.
    /// Measured on this build: at x3 the case is past the nose of its PV curve, so every frame's power flow stops without
    /// converging while its frequency stays finite (the STATE NOT VALID path); at x100 the iterate blows up and the
    /// frame's frequency is NaN, which the dashboard model refuses (the SOLVE FAULT path). Each test asserts the
    /// property it relies on, so a solver change that moved either boundary fails loud instead of testing nothing.
    /// </summary>
    private static ModelSource Case9Loaded(TempFolder temp, int factor)
    {
        string text = File.ReadAllText(Case9().MFile!);
        // The three load rows of case9's bus table (Pd, Qd in MW / MVAr), exactly as the shipped file writes them.
        (int P, int Q)[] loads = { (90, 30), (100, 35), (125, 50) };
        foreach ((int p, int q) in loads)
        {
            string row = $"\t{p}\t{q}\t";
            Assert.Contains(row, text, StringComparison.Ordinal);   // fail loud if the shipped case ever changes
            text = text.Replace(row, $"\t{p * factor}\t{q * factor}\t", StringComparison.Ordinal);
        }
        string name = $"case9x{factor}";
        string file = temp.File(name + ".m");
        File.WriteAllText(file, text.Replace("function mpc = case9", "function mpc = " + name, StringComparison.Ordinal));
        return new ModelSource(name, temp.Path, file);
    }

    private static LicenceGate FreeGate() => new(Entitlement.FreeDefault);

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

    private static void PumpFor(int ms)
    {
        var clock = Stopwatch.StartNew();
        while (clock.ElapsedMilliseconds < ms)
        {
            Dispatcher.UIThread.RunJobs();
            Thread.Sleep(2);
        }
    }

    private static void Await(Task task)
    {
        PumpUntil(() => task.IsCompleted, "a task to finish");
        task.GetAwaiter().GetResult();
    }

    // ---------------------------------------------------------------- the rig

    /// <summary>
    /// A dashboard in a window, attached to a run pipeline playing at x60, with an oracle model fed the WPF calls. Each
    /// frame the dashboard takes is checked (model against oracle, view against model) right after the dashboard took
    /// it; a failure is kept and rethrown by <see cref="ThrowIfFailed"/>, because a throw inside the run pipeline's
    /// frame event would be caught there and shown as a SOLVE FAULT instead of failing the test.
    /// </summary>
    private sealed class Rig : IDisposable
    {
        public readonly RunViewModel Run = new(FreeGate());
        public readonly DashboardView View = new();
        public readonly Window Window;
        public readonly DashboardModel Oracle = new();
        public int Frames;
        public int CheckedFrames;
        public bool CheckEveryFrame = true;
        // 2.0.192: true when every frame of the run must be a solver fault (the live engine refused the state). The
        // default (false) keeps the strict rule that every live frame is a solved one, so no other test can let a
        // fault through by accident.
        public bool ExpectFaulted;
        public Exception? Failure;

        public Rig()
        {
            Window = new Window { Width = 1580, Height = 940, Content = View };
            Window.Show();
            View.Attach(Run);
            // WPF window start-up, in its order: the licence line, then the legend follows the map's starting mode.
            Oracle.Log("08:00", Run.LicenceText);
            Oracle.SetColorLegend(MapColourMode.Loading);

            // The WPF window's dashboard calls at each point, applied to the oracle. These handlers are added after
            // Attach, so each runs after the dashboard's own handler for the same event.
            Run.FeedLoading += (_, _) => { Oracle.HideHud(); Oracle.ClearBridgeStatus(); };          // WPF SwitchTo
            Run.LoadRefused += (_, refusal) => Oracle.Log("--:--", refusal);
            Run.ModelSwitched += (_, e) =>
            {
                Oracle.MapHint = e.IsReplay ? DashboardModel.MapHintReplay : DashboardModel.MapHintLocal;
                Oracle.ClearSelection();
                Oracle.ModelChanged();
            };
            Run.FeedLoaded += (_, e) => Oracle.Log(e.IsReplay ? "00:00" : "08:00", e.LoadedText);
            Run.FrameArriving += OnFrame;
            Await(Run.ChooseSpeedAsync(60));   // play at x60, as the SPEED button does (the pace only, never a number)
        }

        private void OnFrame(object? sender, RunFrameEventArgs e)
        {
            Frames++;
            if (Failure is not null) return;
            try
            {
                if (ExpectFaulted)
                {
                    // WPF ShowHostFrame's solver-fault branch: the live engine threw, so there is no frame to show,
                    // only the SOLVE FAULT lamp, one log line and one alarm, with the WPF texts and colour.
                    Assert.Equal(RunFrameKind.LiveFaulted, e.Kind);
                    string message = Assert.IsAssignableFrom<Exception>(e.Fault).Message;
                    UiColour fault = ColourRamps.Loading(1.2);
                    Oracle.Status = " SOLVE FAULT";
                    Oracle.StatusColour = fault;
                    Oracle.Log(Oracle.Clock, $"Solve failed at tick {e.Tick}: {message}");
                    Oracle.AddAlarm($"SOLVE FAULT: {message}", fault);
                }
                else
                {
                    // WPF ShowHostFrame: a solved live frame is Update(f, boundaries, solveMs); if that throws, the SOLVE
                    // FAULT path, with the WPF texts and colour (Palette.Loading(1.2)).
                    Assert.Equal(RunFrameKind.LiveSolved, e.Kind);
                    try
                    {
                        Oracle.Update(e.Frame!, e.Boundaries, e.SolveMs);
                    }
#pragma warning disable CA1031 // The WPF path being mirrored catches everything here.
                    catch (Exception ex)
#pragma warning restore CA1031
                    {
                        UiColour fault = ColourRamps.Loading(1.2);
                        Oracle.Status = " SOLVE FAULT";
                        Oracle.StatusColour = fault;
                        Oracle.Log(Oracle.Clock, $"Render failed at tick {e.Tick}: {ex.Message}");
                        Oracle.AddAlarm($"SOLVE FAULT: {ex.Message}", fault);
                    }
                }
                if (!CheckEveryFrame) return;
                AssertSameState(Oracle, View.Model);
                AssertViewShows(View);
                CheckedFrames++;
            }
#pragma warning disable CA1031 // Kept and rethrown by ThrowIfFailed.
            catch (Exception ex) { Failure = ex; }
#pragma warning restore CA1031
        }

        public void ThrowIfFailed()
        {
            if (Failure is not null) throw new Xunit.Sdk.XunitException("A dashboard frame check failed: " + Failure);
        }

        /// <summary>Load <paramref name="source"/> and play until <paramref name="n"/> more frames have been taken.</summary>
        public void LoadAndPlay(ModelSource source, int n)
        {
            int before = Frames;
            Task<string?> load = Run.LoadAsync(source);
            Await(load);
            Assert.Null(load.Result);   // loaded, not refused
            PumpUntil(() => Frames >= before + n || Failure is not null, $"{n} frames of {source.Label}");
            ThrowIfFailed();
        }

        public void Dispose()
        {
            Task stop = Run.StopLoopAsync();
            Await(stop);
            View.Detach();
            Window.Close();
        }
    }

    // ---------------------------------------------------------------- model against oracle

    /// <summary>
    /// Every display value of <paramref name="actual"/> equals <paramref name="expected"/>'s, exactly: each public
    /// string, number, bool, colour token and mode property, and each row of each bound list (by its public properties).
    /// </summary>
    private static void AssertSameState(DashboardModel expected, DashboardModel actual)
    {
        foreach (PropertyInfo p in typeof(DashboardModel).GetProperties(BindingFlags.Public | BindingFlags.Instance))
        {
            if (p.GetIndexParameters().Length != 0) continue;
            object? e = p.GetValue(expected), a = p.GetValue(actual);
            if (e is IEnumerable eList && e is not string)
            {
                var eItems = eList.Cast<object?>().ToList();
                var aItems = ((IEnumerable)a!).Cast<object?>().ToList();
                Assert.True(eItems.Count == aItems.Count, $"{p.Name}: {aItems.Count} rows, expected {eItems.Count}");
                for (int i = 0; i < eItems.Count; i++) AssertSameRow($"{p.Name}[{i}]", eItems[i], aItems[i]);
            }
            else
            {
                Assert.True(Equals(e, a), $"{p.Name}: '{a}', expected '{e}'");
            }
        }
    }

    private static void AssertSameRow(string where, object? e, object? a)
    {
        if (e is null || e is string || e.GetType().IsPrimitive)
        {
            Assert.True(Equals(e, a), $"{where}: '{a}', expected '{e}'");
            return;
        }
        foreach (PropertyInfo p in e.GetType().GetProperties(BindingFlags.Public | BindingFlags.Instance))
        {
            object? ev = p.GetValue(e), av = p.GetValue(a);
            Assert.True(Equals(ev, av), $"{where}.{p.Name}: '{av}', expected '{ev}'");
        }
    }

    // ---------------------------------------------------------------- view against model

    private static T Find<T>(DashboardView v, string name) where T : Control =>
        v.FindControl<T>(name) ?? throw new Xunit.Sdk.XunitException($"No {typeof(T).Name} named {name} in DashboardView.");

    /// <summary>The single-value tiles: control name and the exact text the model gives it.</summary>
    private static readonly (string Name, Func<DashboardModel, string> Text)[] TextTiles =
    {
        ("StatusText", m => m.Status),
        ("ClockText", m => m.Clock),
        ("TickText", m => m.Tick),
        ("FreqDialLowText", m => m.FreqDialLowText),
        ("FreqDialHighText", m => m.FreqDialHighText),
        ("FreqText", m => m.Freq),
        ("FreqDevText", m => m.FreqDev + " Hz dev"),   // WPF StringFormat {}{0} Hz dev
        ("DemandText", m => m.Demand),
        ("GenerationText", m => m.Generation),
        ("InertiaText", m => m.Inertia),
        ("LossesText", m => m.Losses),
        ("RpmText", m => m.Rpm),
        ("TorqueText", m => m.Torque),
        ("VoltageText", m => m.Voltage),
        ("BridgeStatusText", m => m.BridgeStatus),
        ("WhatIfTitleText", m => m.QueryTitle),
        ("WhatIfVerdictText", m => m.QueryVerdict),
        ("MapHintText", m => m.MapHint),
        ("LegendTitleText", m => m.LegendTitle),
        ("LegendMinText", m => m.LegendMin),
        ("LegendMaxText", m => m.LegendMax),
        ("SelKindText", m => m.SelKind),
        ("SelNameText", m => m.SelName),
        ("SelSubText", m => m.SelSub),
        ("SelConnectionsText", m => m.SelConnections),
        ("SelLinesText", m => m.SelLines),
        ("HudInertiaText", m => m.InertiaGvaS),
        ("HudFragilityText", m => m.FragilityIndex),
        ("HudFragilityTrendText", m => m.FragilityTrend),
        ("HudRocofText", m => m.RocofHeadroomHzS),
        ("QueryTitleText", m => m.QueryTitle),
        ("QuerySubText", m => m.QuerySub),
        ("QueryVerdictText", m => m.QueryVerdict),
        ("QueryBindingText", m => m.QueryBinding),
    };

    /// <summary>The coloured parts: control name, how to read its brush, and the model token it must show.</summary>
    private static readonly (string Name, Func<Control, IBrush?> Brush, Func<DashboardModel, UiColour> Token)[] ColourTiles =
    {
        ("StatusLamp", c => ((Ellipse)c).Fill, m => m.StatusColour),
        ("FreqText", c => ((TextBlock)c).Foreground, m => m.FreqColour),
        ("FreqNeedle", c => ((Line)c).Stroke, m => m.FreqColour),
        ("BridgePill", c => ((Border)c).BorderBrush, m => m.BridgeStatusColour),
        ("BridgePillLamp", c => ((Ellipse)c).Fill, m => m.BridgeStatusColour),
        ("HudFragilityText", c => ((TextBlock)c).Foreground, m => m.FragilityColour),
        ("HudFragilityTrendText", c => ((TextBlock)c).Foreground, m => m.FragilityTrendColour),
        ("HudFragilityBar", c => ((Border)c).Background, m => m.FragilityColour),
        ("QueryVerdictBadge", c => ((Border)c).Background, m => m.QueryVerdictColour),
    };

    /// <summary>The panels a model bool shows or hides (each WPF "...Vis").</summary>
    private static readonly (string Name, Func<DashboardModel, bool> Visible)[] VisibilityRules =
    {
        ("InvalidVeil", m => m.InvalidState),
        ("InvalidBanner", m => m.InvalidState),
        ("BridgePill", m => m.BridgeStatusVisible),
        ("WhatIfBanner", m => m.QueryVisible),
        ("QueryPanel", m => m.QueryVisible),
        ("SelectionPanel", m => m.SelectionVisible),
        ("HudPanel", m => m.HudVisible),
        ("QueryNoneText", m => m.QueryViolationsEmpty),
        ("AlarmsEmptyText", m => m.AlarmsEmpty),
    };

    /// <summary>A brush shows a token when it is the converter's brush for it and carries the token's four bytes.</summary>
    private static void AssertShowsToken(string where, IBrush? brush, UiColour token)
    {
        Assert.True(brush is ISolidColorBrush, $"{where}: not a solid brush ({brush})");
        Color c = ((ISolidColorBrush)brush!).Color;
        Assert.True(c.A == token.A && c.R == token.R && c.G == token.G && c.B == token.B,
            $"{where}: #{c.A:X2}{c.R:X2}{c.G:X2}{c.B:X2}, expected #{token.A:X2}{token.R:X2}{token.G:X2}{token.B:X2}");
        Assert.Equal(1.0, ((ISolidColorBrush)brush).Opacity);
        Assert.Same(UiColourBrushConverter.BrushFor(token), brush);
    }

    /// <summary>The descendants of item <paramref name="i"/> of <paramref name="list"/> that carry style class <paramref name="cls"/>.</summary>
    private static T Part<T>(ItemsControl list, int i, string cls) where T : Control
    {
        Control container = list.ContainerFromIndex(i) ?? throw new Xunit.Sdk.XunitException($"{list.Name}: no row {i}");
        return container.GetVisualDescendants().OfType<T>().Single(c => c.Classes.Contains(cls));
    }

    /// <summary>Everything the view shows equals what its model says, on this frame.</summary>
    private static void AssertViewShows(DashboardView v)
    {
        v.UpdateLayout();   // materialise list rows for the model's current collections (layout is synchronous)
        DashboardModel m = v.Model;
        Assert.Same(m, v.DataContext);

        foreach ((string name, Func<DashboardModel, string> text) in TextTiles)
            Assert.True(Find<TextBlock>(v, name).Text == text(m), $"{name}: '{Find<TextBlock>(v, name).Text}', expected '{text(m)}'");

        foreach ((string name, Func<Control, IBrush?> brush, Func<DashboardModel, UiColour> token) in ColourTiles)
            AssertShowsToken(name, brush(Find<Control>(v, name)), token(m));

        foreach ((string name, Func<DashboardModel, bool> visible) in VisibilityRules)
            Assert.True(Find<Control>(v, name).IsVisible == visible(m), $"{name}: IsVisible {!visible(m)}, expected {visible(m)}");

        // The fixed WPF texts of the two invalid-state markings.
        Assert.Equal(RunViewModel.StateNotValidBanner, Find<TextBlock>(v, "InvalidBannerText").Text);
        Assert.Equal("STATE NOT VALID  -  the power flow did not converge; these colours are not an operating point",
            Find<TextBlock>(v, "InvalidVeilText").Text);

        // The frequency needle turns by FreqAngle about the WPF pivot (75, 62).
        Line needle = Find<Line>(v, "FreqNeedle");
        var rot = Assert.IsType<RotateTransform>(needle.RenderTransform);
        Assert.Equal(m.FreqAngle, rot.Angle);
        Assert.Equal(75.0, rot.CenterX);
        Assert.Equal(62.0, rot.CenterY);

        // The legend swatch is the gradient of the model's mode, stop for stop.
        IBrush? swatch = Find<Border>(v, "LegendSwatch").Background;
        Assert.Same(LegendBrushConverter.BrushFor(m.Legend), swatch);
        var grad = Assert.IsAssignableFrom<ILinearGradientBrush>(swatch);
        IReadOnlyList<(UiColour Colour, double Offset)> stops = DashboardModel.LegendRampStops(m.Legend);
        Assert.Equal(stops.Count, grad.GradientStops.Count);
        for (int i = 0; i < stops.Count; i++)
        {
            Assert.Equal(stops[i].Offset, grad.GradientStops[i].Offset);
            UiColour t = stops[i].Colour;
            Assert.Equal(Color.FromArgb(t.A, t.R, t.G, t.B), grad.GradientStops[i].Color);
        }

        // Boundary corridors: one row per model row, in order, every text and the bar's width and colour.
        ItemsControl boundaries = Find<ItemsControl>(v, "BoundaryList");
        Assert.Equal(m.Boundaries.Count, boundaries.ItemCount);
        for (int i = 0; i < m.Boundaries.Count; i++)
        {
            BoundaryRowModel r = m.Boundaries[i];
            Assert.Equal(r.Name, Part<TextBlock>(boundaries, i, "BoundaryName").Text);
            Assert.Equal(r.Pct, Part<TextBlock>(boundaries, i, "BoundaryPct").Text);
            Assert.Equal(r.Flow, Part<TextBlock>(boundaries, i, "BoundaryFlow").Text);
            Border bar = Part<Border>(boundaries, i, "BoundaryBar");
            Assert.Equal(r.BarWidth, bar.Width);
            AssertShowsToken($"BoundaryBar[{i}]", bar.Background, r.BarColour);
        }

        // Voltage-band dials: one gauge per level, in order, with the model's caption, text and value.
        ItemsControl bands = Find<ItemsControl>(v, "VoltageBandList");
        Assert.Equal(m.VoltageBands.Count, bands.ItemCount);
        for (int i = 0; i < m.VoltageBands.Count; i++)
        {
            VoltageBandDialModel d = m.VoltageBands[i];
            Gauge g = bands.ContainerFromIndex(i)!.GetVisualDescendants().OfType<Gauge>().Single();
            Assert.Equal(d.Label, g.Caption);
            Assert.Equal(d.Dev, g.ValueText);
            Assert.Equal(d.Vpu, g.Value);
            Assert.Equal("dev", g.Unit);
            Assert.Equal(0.94, g.Minimum);
            Assert.Equal(1.06, g.Maximum);
        }

        // Alarms: time, text and lamp colour of every row, newest first as the model holds them.
        ItemsControl alarms = Find<ItemsControl>(v, "AlarmList");
        Assert.Equal(m.Alarms.Count, alarms.ItemCount);
        for (int i = 0; i < m.Alarms.Count; i++)
        {
            AlarmEntry a = m.Alarms[i];
            Assert.Equal(a.Time, Part<TextBlock>(alarms, i, "AlarmTime").Text);
            Assert.Equal(a.Text, Part<TextBlock>(alarms, i, "AlarmText").Text);
            AssertShowsToken($"AlarmLamp[{i}]", Part<Ellipse>(alarms, i, "AlarmLamp").Fill, a.Colour);
        }

        // Event log: every line, newest first.
        ItemsControl events = Find<ItemsControl>(v, "EventList");
        Assert.Equal(m.Events.Count, events.ItemCount);
        for (int i = 0; i < m.Events.Count; i++)
            Assert.Equal(m.Events[i], Part<TextBlock>(events, i, "EventLine").Text);
    }


    // ---------------------------------------------------------------- tests

    [AvaloniaFact]
    public void Case9_then_uk_every_frame_the_dashboard_equals_the_WPF_fed_oracle_and_every_tile_shows_the_model()
    {
        using var rig = new Rig();
        // Before any frame: the model's starting values are already on the tiles (INITIALISING, " - ").
        AssertViewShows(rig.View);
        Assert.Equal("INITIALISING", rig.View.Model.Status);
        Assert.Equal("08:00  " + rig.Run.LicenceText, Assert.Single(rig.View.Model.Events));
        AssertSameState(rig.Oracle, rig.View.Model);

        const int n = 8;
        rig.LoadAndPlay(Case9(), n);
        Assert.Contains(rig.View.Model.Events, e => e.StartsWith("08:00  Loaded case9  -  9 buses, ", StringComparison.Ordinal));
        Assert.True(rig.View.Model.VoltageBands.Count >= 1);

        rig.LoadAndPlay(Uk(), n);   // a model switch: the oracle takes WPF's switch calls and the comparison goes on
        Assert.True(rig.CheckedFrames >= 2 * n, $"only {rig.CheckedFrames} frames were checked");
        rig.ThrowIfFailed();

        // GB reduced has transmission boundaries and several voltage levels, so the lists were really exercised.
        Assert.NotEmpty(rig.View.Model.Boundaries);
        Assert.True(rig.View.Model.VoltageBands.Count >= 2, "GB reduced should show more than one voltage level");
        Assert.NotEqual("INITIALISING", rig.View.Model.Status);
        Assert.NotEqual(DashboardHost.SolveFaultStatus, rig.View.Model.Status);
        Assert.Contains(rig.View.Model.Events, e => e.StartsWith("08:00  Loaded GB reduced  10-zone  -  ", StringComparison.Ordinal));
    }

    [AvaloniaFact]
    public void A_non_converged_frame_shows_the_WPF_state_not_valid_banner_veil_lamp_alarm_and_log_line()
    {
        using var temp = new TempFolder();
        using var rig = new Rig();
        rig.LoadAndPlay(Case9Loaded(temp, 3), 3);
        rig.ThrowIfFailed();
        rig.View.UpdateLayout();
        DashboardModel m = rig.View.Model;
        Assert.False(rig.Run.StateValid);   // the run pipeline agrees: the frames shown did not converge

        // The model's verdict (from the shared DashboardModel) ...
        Assert.True(m.InvalidState);
        Assert.Equal("NOT CONVERGED", m.Status);
        Assert.Equal(ColourRamps.Loading(1.1), m.StatusColour);
        // ... and what WPF shows for it: the banner with its exact text, the map veil, the red lamp.
        Border banner = Find<Border>(rig.View, "InvalidBanner");
        Assert.True(banner.IsVisible);
        Assert.Equal("STATE NOT VALID  -  the power flow did not converge; every value on this tab comes from an unconverged iterate",
            Find<TextBlock>(rig.View, "InvalidBannerText").Text);
        Assert.True(Find<Border>(rig.View, "InvalidVeil").IsVisible);
        Assert.Equal("NOT CONVERGED", Find<TextBlock>(rig.View, "StatusText").Text);
        AssertShowsToken("StatusLamp", Find<Ellipse>(rig.View, "StatusLamp").Fill, ColourRamps.Loading(1.1));
        // One alarm per onset (not one per frame), and one log line for the change.
        Assert.Single(m.Alarms, a => a.Text == "POWER FLOW DID NOT CONVERGE - state not valid");
        Assert.Single(m.Events, e => e.EndsWith("  Power flow did not converge - displayed state is not a valid operating point", StringComparison.Ordinal));
        Assert.False(Find<TextBlock>(rig.View, "AlarmsEmptyText").IsVisible);
        AssertViewShows(rig.View);

        // A converged case afterwards takes the banner and the veil down again (ModelChanged lets the next frame decide).
        rig.LoadAndPlay(Case9(), 2);
        rig.View.UpdateLayout();
        Assert.False(m.InvalidState);
        Assert.False(Find<Border>(rig.View, "InvalidBanner").IsVisible);
        Assert.False(Find<Border>(rig.View, "InvalidVeil").IsVisible);
        rig.ThrowIfFailed();
    }

    [AvaloniaFact]
    public void A_frame_the_model_refuses_lands_on_the_WPF_solve_fault_path()
    {
        using var temp = new TempFolder();
        using var rig = new Rig();
        // 2.0.192: case 9 at 100x load drives the live swing non-finite. Since 2.0.188 the live frequency integrator
        // refuses that state itself (it never publishes a NaN frequency), so each frame arrives as a solver fault
        // rather than as a solved frame the dashboard then refuses. The dashboard's outcome is the same SOLVE FAULT
        // lamp, colour and one alarm per frame; the log line is WPF's solver-fault text.
        rig.ExpectFaulted = true;
        rig.LoadAndPlay(Case9Loaded(temp, 100), 2);
        rig.ThrowIfFailed();
        rig.View.UpdateLayout();
        DashboardModel m = rig.View.Model;

        // WPF ShowHostFrame's catch: the lamp text and colour, one log line and one alarm per frame refused.
        Assert.Equal(" SOLVE FAULT", m.Status);
        Assert.Equal(DashboardHost.SolveFaultStatus, m.Status);
        Assert.Equal(ColourRamps.Loading(1.2), m.StatusColour);
        AssertShowsToken("StatusLamp", Find<Ellipse>(rig.View, "StatusLamp").Fill, ColourRamps.Loading(1.2));
        Assert.Equal(" SOLVE FAULT", Find<TextBlock>(rig.View, "StatusText").Text);
        Assert.True(m.Alarms.Count >= 2, $"{m.Alarms.Count} alarms");
        Assert.All(m.Alarms, a => Assert.StartsWith("SOLVE FAULT: ", a.Text, StringComparison.Ordinal));
        Assert.All(m.Alarms, a => Assert.Equal(ColourRamps.Loading(1.2), a.Colour));
        Assert.Contains(m.Events, e => e.Contains("  Solve failed at tick 1: ", StringComparison.Ordinal));
        Assert.Contains(m.Events, e => e.Contains("  Solve failed at tick 2: ", StringComparison.Ordinal));
        AssertViewShows(rig.View);
    }

    [AvaloniaFact]
    public void A_model_switch_resets_the_dashboard_as_WPF_does()
    {
        using var rig = new Rig();
        rig.LoadAndPlay(Uk(), 4);
        DashboardModel m = rig.View.Model;
        var ukLevels = m.VoltageBands.Select(b => b.BaseKv).ToHashSet();
        var ukBoundaries = m.Boundaries.Select(b => b.Name).ToHashSet();
        Assert.NotEmpty(ukBoundaries);

        // Open the detail fly-out on both models (the map's click does this in WPF), then switch to case9.
        foreach (DashboardModel d in new[] { m, rig.Oracle })
            d.Select("SUBSTATION", "Test bus", "400 kV", new[] { new KeyVal("V", "1.000 pu") }, "1 connection", "a-b");
        rig.View.UpdateLayout();
        Assert.True(Find<Border>(rig.View, "SelectionPanel").IsVisible);

        bool checkedAtSwitch = false;
        rig.Run.ModelSwitched += (_, _) =>
        {
            // Right after the switch and before case9's first frame: the WPF SwitchTo calls have been made.
            checkedAtSwitch = !m.SelectionVisible && !m.InvalidState && m.MapHint == DashboardModel.MapHintLocal;
        };
        rig.LoadAndPlay(Case9(), 4);
        Assert.True(checkedAtSwitch, "the fly-out, the invalid state and the map hint were not reset at the switch");
        rig.View.UpdateLayout();
        Assert.False(Find<Border>(rig.View, "SelectionPanel").IsVisible);

        // Nothing of GB reduced is left on the tiles: case9 has no boundaries and only its own 345 kV level.
        Assert.Empty(m.Boundaries);
        Assert.All(m.VoltageBands, b => Assert.Equal(345.0, b.BaseKv));
        Assert.DoesNotContain(m.VoltageBands, b => ukLevels.Contains(b.BaseKv) && b.BaseKv != 345.0);
        Assert.Equal(0, Find<ItemsControl>(rig.View, "BoundaryList").ItemCount);
        rig.ThrowIfFailed();
    }

    [AvaloniaFact]
    public void Detach_stops_updates_and_attach_twice_is_refused()
    {
        using var rig = new Rig();
        rig.LoadAndPlay(Case9(), 3);
        DashboardModel m = rig.View.Model;
        Assert.Throws<InvalidOperationException>(() => rig.View.Attach(rig.Run));

        rig.CheckEveryFrame = false;
        rig.View.Detach();
        string tick = m.Tick, tickText = Find<TextBlock>(rig.View, "TickText").Text!;
        int events = m.Events.Count;
        int shown = 0;
        rig.Run.FrameShown += (_, _) => shown++;
        PumpUntil(() => shown >= 3, "three more frames after Detach");

        // The run went on (its own panel advanced), the dashboard did not.
        Assert.NotEqual(tick, rig.Run.Panel!.Tick);
        Assert.Equal(tick, m.Tick);
        Assert.Equal(tickText, Find<TextBlock>(rig.View, "TickText").Text);
        Assert.Equal(events, m.Events.Count);
        Assert.Null(rig.View.Host.Run);

        // Attached again, it follows the run once more.
        rig.View.Attach(rig.Run);
        int seen = rig.Frames;
        PumpUntil(() => rig.Frames >= seen + 2, "two frames after re-attaching");
        rig.View.UpdateLayout();
        Assert.Equal(rig.Run.Panel!.Tick, m.Tick);
        Assert.Equal(m.Tick, Find<TextBlock>(rig.View, "TickText").Text);
    }

    [AvaloniaFact]
    public void A_theme_colour_token_paints_with_the_theme_brush_itself_and_any_other_token_with_its_own_bytes()
    {
        Avalonia.Application app = Avalonia.Application.Current!;
        foreach (string key in UiColourBrushConverter.ThemeBrushKeys)
        {
            Assert.True(app.TryGetResource(key, ThemeVariant.Dark, out object? r), $"theme brush {key} missing");
            var theme = Assert.IsAssignableFrom<ISolidColorBrush>(r);
            var token = new UiColour(theme.Color.A, theme.Color.R, theme.Color.G, theme.Color.B);
            Assert.Same(theme, UiColourBrushConverter.BrushFor(token));
        }
        // The steady fragility trend's slate is the theme's Muted.
        Assert.True(app.TryGetResource("Muted", ThemeVariant.Dark, out object? muted));
        Assert.Same(muted, UiColourBrushConverter.BrushFor(DashboardColours.Muted));
        // A ramp colour is not a theme colour: its own cached brush, its own four bytes.
        UiColour amber = ColourRamps.Loading(0.8);
        IBrush b = UiColourBrushConverter.BrushFor(amber);
        Assert.Same(b, UiColourBrushConverter.BrushFor(amber));
        var solid = Assert.IsType<ImmutableSolidColorBrush>(b);
        Assert.Equal(Color.FromArgb(amber.A, amber.R, amber.G, amber.B), solid.Color);
        // The transparent trip-verdict token keeps its zero alpha.
        Assert.Equal(0, ((ISolidColorBrush)UiColourBrushConverter.BrushFor(DashboardColours.Transparent)).Color.A);
        // Fail loud on a wrong value; nothing for a missing one.
        Assert.Throws<ArgumentException>(() => UiColourBrushConverter.Instance.Convert("red", typeof(IBrush), null, System.Globalization.CultureInfo.InvariantCulture));
        Assert.Null(UiColourBrushConverter.Instance.Convert(null, typeof(IBrush), null, System.Globalization.CultureInfo.InvariantCulture));
    }

    [AvaloniaFact]
    public void Smoke_render_draws_the_tiles_with_the_status_lamp_in_the_models_colour()
    {
        using var rig = new Rig();
        rig.LoadAndPlay(Case9(), 3);
        Await(rig.Run.TogglePlayAsync());   // pause, so the frame holds still
        PumpFor(150);
        rig.View.UpdateLayout();

        WriteableBitmap? frame = rig.Window.CaptureRenderedFrame();
        Assert.NotNull(frame);
        using ILockedFramebuffer fb = frame!.Lock();
        Assert.True(fb.Size.Width >= 1000 && fb.Size.Height >= 600, $"frame {fb.Size}");
        Assert.True(fb.Format == PixelFormats.Bgra8888 || fb.Format == PixelFormats.Rgba8888, $"unexpected frame format {fb.Format}");

        // One pixel's colour (alpha dropped; the window is opaque), in either of the two byte orders.
        Color PixelAt(int x, int y)
        {
            int at = y * fb.RowBytes + x * 4;
            byte c0 = Marshal.ReadByte(fb.Address, at), c1 = Marshal.ReadByte(fb.Address, at + 1), c2 = Marshal.ReadByte(fb.Address, at + 2);
            return fb.Format == PixelFormats.Bgra8888 ? Color.FromRgb(c2, c1, c0) : Color.FromRgb(c0, c1, c2);
        }

        // The pixel at the status lamp's centre is the lamp's colour (an opaque ramp colour; the ellipse covers it).
        Ellipse lamp = Find<Ellipse>(rig.View, "StatusLamp");
        Avalonia.Point centre = Avalonia.VisualExtensions.TranslatePoint(lamp, new Avalonia.Point(6, 6), rig.Window)!.Value;
        Color px = PixelAt((int)centre.X, (int)centre.Y);
        UiColour want = rig.View.Model.StatusColour;
        Assert.True(Math.Abs(px.R - want.R) <= 2 && Math.Abs(px.G - want.G) <= 2 && Math.Abs(px.B - want.B) <= 2,
            $"lamp pixel ({px.R},{px.G},{px.B}), expected ({want.R},{want.G},{want.B})");

        // And the dashboard is not a blank page: many distinct colours across the frame.
        var colours = new HashSet<Color>();
        for (int yy = 0; yy < fb.Size.Height; yy += 7)
            for (int xx = 0; xx < fb.Size.Width; xx += 7) colours.Add(PixelAt(xx, yy));
        Assert.True(colours.Count > 20, $"only {colours.Count} distinct colours: the tiles did not draw");
    }
}
