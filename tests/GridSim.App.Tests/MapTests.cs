using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Threading;
using GridSim.App.Map;
using GridSim.App.Run;
using GridSim.App.Shell;
using GridSim.Core.Licensing;
using GridSim.Core.Model;
using GridSim.Core.Simulation;
using GridSim.Ui.Commands;
using GridSim.Ui.Geography;
using GridSim.Ui.Models;
using GridSim.Ui.Presentation;
using GridSim.Ui.Schematic;
using GridSim.Ui.Shell;
using Xunit;

namespace GridSim.App.Tests;

/// <summary>
/// UI programme Phase 1.5: the network map in the Avalonia app, on the headless platform with Skia rendering. Cases are
/// loaded through the run pipeline (the shipped MATPOWER case9 and the reduced GB case under data/uk); the map lays out
/// each loaded model and takes each frame's state exactly as SchematicView does; the rendered pixels carry the node and
/// branch colours at the places SchematicView puts them; a click selects the exact bus or branch SchematicView's hit
/// tests name; the wheel and a drag move the view exactly as SchematicView's own arithmetic (and FastSchematic's)
/// would; every map key fires its command and stands aside while a text box or combo box has the focus; and the bus
/// context menu comes from the registry and trips through the WPF trip rule.
/// </summary>
public sealed class MapTests
{
    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    // ---------------------------------------------------------------- helpers

    /// <summary>The checkout root (the folder with GridSim.slnx), found from the test's own folder.</summary>
    private static string RepoRoot()
    {
        for (DirectoryInfo? d = new(AppContext.BaseDirectory); d is not null; d = d.Parent)
            if (File.Exists(Path.Combine(d.FullName, "GridSim.slnx"))) return d.FullName;
        throw new InvalidOperationException("The GridSim checkout (GridSim.slnx) was not found above " + AppContext.BaseDirectory);
    }

    /// <summary>A case as the catalogue lists it: "case9" is the shipped MATPOWER file, "uk" the reduced GB case.</summary>
    private static ModelSource Case(string name)
    {
        if (name == "uk")
        {
            string dir = Path.Combine(RepoRoot(), "data", "uk");
            Assert.True(File.Exists(Path.Combine(dir, "system.json")), "missing test case " + dir);
            return new ModelSource("GB reduced  10-zone", dir);
        }
        string file = Path.Combine(RepoRoot(), "data", "matpower", "lib", name + ".m");
        Assert.True(File.Exists(file), "missing test case " + file);
        return new ModelSource(name, Path.GetDirectoryName(file)!, file);
    }

    private static ShellOptions Options(TempFolder temp, ModelSource source, Func<ICollection<string>, GeographyData?>? geography = null) => new()
    {
        SettingsStore = ShellSettingsStore.ForFile(temp.File("settings.json")),
        DiscoverCases = (_, _) => new List<ModelSource> { source },
        LicenceDescription = () => "Test licence",
        Licence = () => new LicenceGate(Entitlement.FreeDefault),
        UseNativeMenu = false,
        // Test retargeted 2026-10-20 (UI programme 1.8): the window now loads a source by itself when it opens, as
        // the WPF window does; these tests were written for the window that opens empty, so they turn that off
        // (ShellOptions.AutoLoadAtStart). Every assertion is unchanged.
        AutoLoadAtStart = false,
        LoadGeography = geography ?? (_ => null),
    };

    private static void PumpUntil(Func<bool> done, string what, int timeoutMs = 60_000)
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

    /// <summary>
    /// Open <paramref name="source"/> in a new main window through the run pipeline, wait for two accepted frames, then
    /// pause (so the map holds still) and let any frame already in flight land.
    /// </summary>
    private static MainWindow OpenAndSettle(TempFolder temp, ModelSource source, Func<ICollection<string>, GeographyData?>? geography = null)
    {
        var w = new MainWindow(Options(temp, source, geography));
        w.Show();
        RunViewModel run = w.ViewModel.Run;
        int frames = 0;
        run.FrameShown += (_, _) => frames++;
        Await(w.ViewModel.LoadCaseAsync(source));
        Assert.False(run.HasError, run.ErrorText);
        Await(w.ViewModel.Registry.Get(RunCommands.SpeedId(60)).RunAsync());
        PumpUntil(() => frames >= 2, "two frames");
        Await(w.ViewModel.Registry.Get(RunCommands.PlayPause).RunAsync());
        PumpFor(150);
        Assert.NotNull(run.LastFrame);
        w.CaptureRenderedFrame()?.Dispose();   // one full render, so the fit and the minimap are in place
        Dispatcher.UIThread.RunJobs();
        return w;
    }

    private static void Press(Window w, Key key, RawInputModifiers mods = RawInputModifiers.None)
    {
        w.KeyPress(key, mods, PhysicalKey.None, null);
        w.KeyRelease(key, mods, PhysicalKey.None, null);
        Dispatcher.UIThread.RunJobs();
    }

    /// <summary>
    /// A trip of <paramref name="g"/> was taken, with the WPF lines. Test retargeted 2026-10-20 (UI programme
    /// integration): the trip now runs the GENERATION tab's swing study on the window's dashboard, as WPF does, so the
    /// log line is no longer the short one WPF wrote only when the study gave no response: it carries the study's
    /// baseline RoCoF and nadir (the WPF text, TripSwingStudy.LogLine, written out here). The status-bar check is
    /// unchanged in kind (the exact whole line); strengthened: the same line must be the dashboard's newest event-log
    /// line at the dashboard clock (WPF writes it there), and the TRIP alarm must be raised with the WPF text and colour.
    /// </summary>
    private static void AssertTripLogged(MainWindowViewModel vm, Generator g)
    {
        GridSim.Core.Dynamics.FrequencyResponse resp = vm.Dashboard.BaselineResponse
            ?? throw new Xunit.Sdk.XunitException("the trip ran no swing study (no baseline response on the dashboard)");
        string line = $" TRIP {g.Name} ({g.Pg.ToString("F0", Inv)} MW)  RoCoF {resp.InitialRocofHzPerS.ToString("F2", Inv)} Hz/s  " +
                      $"nadir {resp.NadirHz.ToString("F2", Inv)} Hz";
        Assert.Equal(line, vm.StatusText);
        Assert.Equal($"{vm.Dashboard.Clock}  {line}", vm.Dashboard.Events[0]);
        GridSim.Ui.Dashboard.AlarmEntry alarm = vm.Dashboard.Alarms[0];
        Assert.Equal($"TRIP {g.Name} -{g.Pg.ToString("F0", Inv)} MW", alarm.Text);
        Assert.Equal(ColourRamps.Loading(1.1), alarm.Colour);
    }

    /// <summary>
    /// A second trip of the same unit was refused, with the WPF words. Test retargeted 2026-10-20 (UI programme
    /// integration): unchanged (the status line starts with the WPF refusal); strengthened: the same line is the
    /// dashboard's newest event-log line at the dashboard clock, as WPF logs it.
    /// </summary>
    private static void AssertRefusalLogged(MainWindowViewModel vm)
    {
        Assert.StartsWith("TRIP refused: ", vm.StatusText, StringComparison.Ordinal);
        Assert.Equal($"{vm.Dashboard.Clock}  {vm.StatusText}", vm.Dashboard.Events[0]);
    }

    /// <summary>The window point for map-local point <paramref name="local"/>.</summary>
    private static Point ToWindow(MainWindow w, Point local)
        => w.MapControl.TranslatePoint(local, w) ?? throw new InvalidOperationException("the map is not in the window");

    /// <summary>The map-local point the map sees for window point <paramref name="windowPt"/>.</summary>
    private static Point ToMap(MainWindow w, Point windowPt)
        => w.TranslatePoint(windowPt, w.MapControl) ?? throw new InvalidOperationException("the map is not in the window");

    private static Point P(ScreenPoint s) => new(s.X, s.Y);

    /// <summary>A fresh SchematicView holding the map's model, viewport and view transform (the reference).</summary>
    private static SchematicView Reference(SchematicControl map, GridModel model)
    {
        var v = new SchematicView();
        v.Load(model);
        v.ViewportWidth = map.Bounds.Width;
        v.ViewportHeight = map.Bounds.Height;
        v.Zoom = map.View.Zoom;
        v.OffsetX = map.View.OffsetX;
        v.OffsetY = map.View.OffsetY;
        return v;
    }

    /// <summary>
    /// A bus whose roundel is well clear of the minimap, the fly-out corner and every other roundel, as a map-local
    /// point and its node index.
    /// </summary>
    private static (int Index, Point Local) ClearNode(SchematicControl map)
    {
        SchematicView v = map.View;
        Rect mm = map.MinimapRect.Inflate(30);
        for (int i = 0; i < v.Nodes.Count; i++)
        {
            Point s = P(v.NodeScreen(i));
            if (s.X < 40 || s.Y < 60 || s.X > map.Bounds.Width - 300 || s.Y > map.Bounds.Height - 60 || mm.Contains(s)) continue;
            bool alone = true;
            for (int j = 0; j < v.Nodes.Count && alone; j++)
            {
                if (j == i) continue;
                Point o = P(v.NodeScreen(j));
                if (Math.Abs(o.X - s.X) < 40 && Math.Abs(o.Y - s.Y) < 40) alone = false;
            }
            if (alone) return (i, s);
        }
        throw new InvalidOperationException("no roundel clear of the overlays");
    }

    // ---------------------------------------------------------------- pixels

    private static (byte R, byte G, byte B) Pixel(ILockedFramebuffer fb, int x, int y)
    {
        int off = y * fb.RowBytes + x * 4;
        byte c0 = Marshal.ReadByte(fb.Address, off), c1 = Marshal.ReadByte(fb.Address, off + 1), c2 = Marshal.ReadByte(fb.Address, off + 2);
        if (fb.Format == PixelFormats.Bgra8888) return (c2, c1, c0);
        if (fb.Format == PixelFormats.Rgba8888) return (c0, c1, c2);
        throw new InvalidOperationException($"unexpected frame format {fb.Format}");
    }

    private static int Distance((byte R, byte G, byte B) a, (byte R, byte G, byte B) b)
        => Math.Max(Math.Abs(a.R - b.R), Math.Max(Math.Abs(a.G - b.G), Math.Abs(a.B - b.B)));

    // The closest pixel to `want` in the (2n+1) square about (x, y), and how far it is from it.
    private static int Best(ILockedFramebuffer fb, double x, double y, (byte, byte, byte) want, int n = 2)
    {
        int best = int.MaxValue;
        for (int dy = -n; dy <= n; dy++)
            for (int dx = -n; dx <= n; dx++)
            {
                int px = (int)Math.Floor(x) + dx, py = (int)Math.Floor(y) + dy;
                if (px < 0 || py < 0 || px >= fb.Size.Width || py >= fb.Size.Height) continue;
                best = Math.Min(best, Distance(Pixel(fb, px, py), want));
            }
        return best;
    }

    private static (byte R, byte G, byte B) Rgb(UiColour c) => (c.R, c.G, c.B);

    // A colour of `alpha` opacity over an opaque background, as the rasteriser composites it.
    private static (byte R, byte G, byte B) Over(UiColour c, double alpha, (byte R, byte G, byte B) bg)
        => ((byte)Math.Round(c.R * alpha + bg.R * (1 - alpha)), (byte)Math.Round(c.G * alpha + bg.G * (1 - alpha)),
            (byte)Math.Round(c.B * alpha + bg.B * (1 - alpha)));

    // ---------------------------------------------------------------- tests: the pipeline

    [AvaloniaTheory]
    [InlineData("case9")]
    [InlineData("uk")]
    public void A_case_loaded_through_the_run_pipeline_is_on_the_map_with_each_frames_state_and_renders(string name)
    {
        using var temp = new TempFolder();
        ModelSource source = Case(name);
        MainWindow w = OpenAndSettle(temp, source);
        try
        {
            SchematicControl map = w.MapControl;
            RunViewModel run = w.ViewModel.Run;
            SimFrame last = run.LastFrame!;

            // The model laid out is the loaded one, bus for bus and branch for branch.
            Assert.True(map.HasModel);
            GridModel loaded = run.LoadedModel!;
            Assert.Equal(loaded.Buses.Select(b => b.Id), map.View.Nodes.Select(n => n.Id));
            Assert.Equal(loaded.Branches.Count, map.View.Edges.Count);

            // The state on the map is the last accepted frame's, applied by SchematicView exactly: a fresh view fed the
            // same model and result holds the same voltages, flows and colour bins, bit for bit.
            var expected = new SchematicView();
            expected.Load(loaded);
            Assert.True(expected.ApplyState(last.Result));
            Assert.True(map.View.HasState);
            Assert.Equal(expected.VmByNode, map.View.VmByNode);
            Assert.Equal(expected.Edges, map.View.Edges);
            expected.RebuildEdgeBuckets(map.ColorMode);
            for (int b = 0; b < SchematicView.LoadingBuckets; b++)
                Assert.Equal(expected.EdgesByBucket[b], map.View.EdgesByBucket[b]);

            // The view was fitted to the map, as FitToView fits it.
            SchematicView fit = Reference(map, loaded);
            Assert.True(fit.FitToView());
            Assert.Equal(fit.Zoom, map.View.Zoom);
            Assert.Equal(fit.OffsetX, map.View.OffsetX);
            Assert.Equal(fit.OffsetY, map.View.OffsetY);

            // It renders without an exception at rest, zoomed in (labels and fan-out) and with every layer on, and the
            // dashes advance without one.
            using (WriteableBitmap? frame = w.CaptureRenderedFrame()) Assert.NotNull(frame);
            map.ShowCoast = map.ShowBoundaries = map.ShowGspAreas = map.ShowFleet = true;
            map.ZoomAt(new Point(map.Bounds.Width / 2, map.Bounds.Height / 2), 8);
            map.Animate(0.07);
            using (WriteableBitmap? frame = w.CaptureRenderedFrame()) Assert.NotNull(frame);
            Assert.False(run.HasError, run.ErrorText);
            Assert.Equal("LINE LOADING", w.ViewModel.Dashboard.LegendTitle);   // the legend follows the (default) loading tint
        }
        finally { w.Close(); }
    }

    [AvaloniaTheory]
    [InlineData("case9")]
    [InlineData("uk")]
    public void The_rendered_map_has_each_nodes_and_branchs_colour_where_SchematicView_puts_it(string name)
    {
        using var temp = new TempFolder();
        MainWindow w = OpenAndSettle(temp, Case(name));
        SimFrame last;
        try { last = w.ViewModel.Run.LastFrame!; }
        finally { w.Close(); }

        // The same control alone in a window of a known size and background, fed the pipeline's frame. No animation
        // runs here, so the dashes sit at phase 0 and the gaps between them are known.
        var map = new SchematicControl();
        var host = new Window { Width = 900, Height = 700, Background = new SolidColorBrush(Color.FromRgb(0x0A, 0x0E, 0x13)), Content = map };
        host.Show();
        try
        {
            map.Load(last.Model);
            map.SetState(last.Result);
            Dispatcher.UIThread.RunJobs();
            using WriteableBitmap bmp = host.CaptureRenderedFrame() ?? throw new InvalidOperationException("no frame");
            Assert.Equal(new PixelSize(900, 700), bmp.PixelSize);
            using ILockedFramebuffer fb = bmp.Lock();
            (byte R, byte G, byte B) bg = Pixel(fb, 2, 2);
            Assert.True(Distance(bg, (0x0A, 0x0E, 0x13)) <= 1, $"background {bg}");

            SchematicView v = map.View;
            Rect mm = map.MinimapRect.Inflate(8);
            double[] vm = v.VmByNode!;

            // ---- nodes: the ring in the voltage colour at the roundel's radius, the dark fill at its centre ----
            int nodesProbed = 0;
            for (int i = 0; i < v.Nodes.Count; i++)
            {
                SchematicNode nd = v.Nodes[i];
                Point s = P(v.NodeScreen(i));
                double r = v.RoundelRadius(nd.DisplayKv);
                if (s.X < r + 4 || s.Y < r + 4 || s.X > 900 - r - 4 || s.Y > 700 - r - 4 || mm.Contains(s)) continue;
                bool alone = true;
                for (int j = 0; j < v.Nodes.Count && alone; j++)
                {
                    if (j == i) continue;
                    Point o = P(v.NodeScreen(j));
                    double rj = v.RoundelRadius(v.Nodes[j].DisplayKv);
                    if (Math.Sqrt((o.X - s.X) * (o.X - s.X) + (o.Y - s.Y) * (o.Y - s.Y)) < r + rj + 6) alone = false;
                }
                if (!alone) continue;

                (byte, byte, byte) ring = Rgb(ColourRamps.Voltage(vm[i]));
                int dRing = Best(fb, s.X + r, s.Y, ring);
                Assert.True(dRing <= 8, $"{name} bus {nd.Id}: ring colour {ring} not found at its radius (closest off by {dRing})");
                int dFill = Best(fb, s.X, s.Y, Rgb(ColourRamps.FromHex("#0E141B")), 0);
                Assert.True(dFill <= 2, $"{name} bus {nd.Id}: the roundel's centre is not the dark fill (off by {dFill})");
                nodesProbed++;
            }
            Assert.True(nodesProbed >= 3, $"{name}: only {nodesProbed} roundels could be probed");

            // ---- branches: the corridor in its colour bin's colour (at the pen's opacity), in a gap between dashes ----
            int branchesProbed = 0;
            MapColourMode mode = map.ColorMode;
            foreach (SchematicEdge e in v.Edges)
            {
                if (!e.Valid || e.IsTransformer) continue;
                Point pa = P(v.NodeScreen(e.A)), pb = P(v.NodeScreen(e.B));
                double dx = pb.X - pa.X, dy = pb.Y - pa.Y, len = Math.Sqrt(dx * dx + dy * dy);
                double ra = v.RoundelRadius(v.Nodes[e.A].DisplayKv), rb = v.RoundelRadius(v.Nodes[e.B].DisplayKv);
                // Dashes run from A at phase 0: [0, 5), [12, 17) ... with round caps 1.4 px long, so 8.5 + 12k is the
                // middle of a clean gap.
                int k = (int)Math.Round((len / 2 - 8.5) / 12);
                double at = 8.5 + 12 * k;
                if (at < ra + 8 || at > len - rb - 8) continue;
                var q = new Point(pa.X + dx / len * at, pa.Y + dy / len * at);
                if (q.X < 4 || q.Y < 4 || q.X > 896 || q.Y > 696 || mm.Contains(q)) continue;
                // Nothing else nearby: no other branch within 6 px, no roundel within its radius plus 6 px.
                bool clear = true;
                foreach (SchematicEdge o in v.Edges)
                {
                    if (!o.Valid || o.BranchIndex == e.BranchIndex) continue;
                    if (SchematicView.DistSqPointToSegment(new ScreenPoint(q.X, q.Y), v.NodeScreen(o.A), v.NodeScreen(o.B)) < 36) { clear = false; break; }
                }
                for (int j = 0; j < v.Nodes.Count && clear; j++)
                {
                    Point o = P(v.NodeScreen(j));
                    if (Math.Sqrt((o.X - q.X) * (o.X - q.X) + (o.Y - q.Y) * (o.Y - q.Y)) < v.RoundelRadius(v.Nodes[j].DisplayKv) + 6) clear = false;
                }
                if (!clear) continue;

                int bucket = v.BucketFor(e, mode);
                (byte, byte, byte) want = Over(MapPaint.CorridorColour(mode, bucket), MapPaint.CorridorOpacity(mode, bucket), bg);
                int d = Best(fb, q.X, q.Y, want, 1);
                Assert.True(d <= 6, $"{name} branch {e.BranchIndex}: corridor colour {want} (bin {bucket}) not found on it (closest off by {d})");
                // And no other colour bin fits that pixel better: the map shows this branch's bin, not a neighbour's.
                int bestBin = Enumerable.Range(0, SchematicView.LoadingBuckets)
                    .OrderBy(bb => Best(fb, q.X, q.Y, Over(MapPaint.CorridorColour(mode, bb), MapPaint.CorridorOpacity(mode, bb), bg), 1))
                    .First();
                Assert.Equal(bucket, bestBin);
                branchesProbed++;
            }
            Assert.True(branchesProbed >= 2, $"{name}: only {branchesProbed} corridors could be probed");
        }
        finally { host.Close(); }
    }

    // ---------------------------------------------------------------- tests: interaction

    [AvaloniaFact]
    public void A_click_on_a_roundel_selects_that_bus_and_a_click_on_a_corridor_selects_that_branch()
    {
        using var temp = new TempFolder();
        MainWindow w = OpenAndSettle(temp, Case("case9"));
        try
        {
            SchematicControl map = w.MapControl;
            MapViewModel mapVm = w.ViewModel.Map;
            GridModel model = w.ViewModel.Run.LoadedModel!;

            (int index, Point local) = ClearNode(map);
            int busId = map.View.Nodes[index].Id;
            Point wp = ToWindow(w, local);
            Point seen = ToMap(w, wp);
            Assert.Equal(busId, map.View.HitTestNode(new ScreenPoint(seen.X, seen.Y)));   // SchematicView names this bus here
            w.MouseDown(wp, MouseButton.Left);
            w.MouseUp(wp, MouseButton.Left);
            Dispatcher.UIThread.RunJobs();

            Assert.Equal(new MapSelection(MapSelectionKind.Bus, busId), mapVm.Selection);
            Bus bus = model.Buses.Single(b => b.Id == busId);
            Assert.True(mapVm.Dashboard.SelectionVisible);
            Assert.Equal(bus.Name ?? $"Bus {busId}", mapVm.Dashboard.SelName);
            Assert.Equal(bus.BaseKv >= 200 ? "EHV SUBSTATION" : "SUBSTATION", mapVm.Dashboard.SelKind);

            // A corridor: the midpoint of a branch well away from every roundel, the minimap and the fly-out.
            SchematicView v = map.View;
            Rect mm = map.MinimapRect.Inflate(30);
            int? wanted = null;
            Point mid = default;
            foreach (SchematicEdge e in v.Edges)
            {
                if (!e.Valid) continue;
                Point pa = P(v.NodeScreen(e.A)), pb = P(v.NodeScreen(e.B));
                var m = new Point((pa.X + pb.X) / 2, (pa.Y + pb.Y) / 2);
                if (m.X < 40 || m.Y < 60 || m.X > map.Bounds.Width - 300 || mm.Contains(m)) continue;
                if (v.HitTestNode(new ScreenPoint(m.X, m.Y)) is not null) continue;
                if (v.HitTestBranch(new ScreenPoint(m.X, m.Y)) != e.BranchIndex) continue;
                wanted = e.BranchIndex;
                mid = m;
                break;
            }
            Assert.NotNull(wanted);
            Point wm = ToWindow(w, mid);
            w.MouseDown(wm, MouseButton.Left);
            w.MouseUp(wm, MouseButton.Left);
            Dispatcher.UIThread.RunJobs();
            Assert.Equal(new MapSelection(MapSelectionKind.Branch, wanted!.Value), mapVm.Selection);
            Assert.Equal("CORRIDOR", mapVm.Dashboard.SelKind);
            Branch br = model.Branches[wanted.Value];
            Assert.Equal(br.Name ?? $"{br.From}-{br.To}", mapVm.Dashboard.SelName);

            // Escape (the WPF key) clears it.
            Press(w, Key.Escape);
            Assert.Null(mapVm.Selection);
            Assert.False(mapVm.Dashboard.SelectionVisible);
        }
        finally { w.Close(); }
    }

    [AvaloniaFact]
    public void The_wheel_zooms_and_a_drag_pans_exactly_as_SchematicView_and_FastSchematic_compute_it()
    {
        using var temp = new TempFolder();
        MainWindow w = OpenAndSettle(temp, Case("case9"));
        try
        {
            SchematicControl map = w.MapControl;
            GridModel model = w.ViewModel.Run.LoadedModel!;
            var cursor = new Point(map.Bounds.Width * 0.4 + 0.25, map.Bounds.Height * 0.45 + 0.5);
            Point wp = ToWindow(w, cursor);
            Point seen = ToMap(w, wp);

            // Wheel up: SchematicView.ZoomAt(cursor, 1.15), bit for bit.
            SchematicView expected = Reference(map, model);
            Assert.True(expected.ZoomAt(new ScreenPoint(seen.X, seen.Y), SchematicControl.WheelZoomFactor));
            w.MouseWheel(wp, new Vector(0, 1));
            Dispatcher.UIThread.RunJobs();
            Assert.Equal(expected.Zoom, map.View.Zoom);
            Assert.Equal(expected.OffsetX, map.View.OffsetX);
            Assert.Equal(expected.OffsetY, map.View.OffsetY);

            // Wheel down: ZoomAt(cursor, 1 / 1.15).
            Assert.True(expected.ZoomAt(new ScreenPoint(seen.X, seen.Y), 1 / SchematicControl.WheelZoomFactor));
            w.MouseWheel(wp, new Vector(0, -1));
            Dispatcher.UIThread.RunJobs();
            Assert.Equal(expected.Zoom, map.View.Zoom);
            Assert.Equal(expected.OffsetX, map.View.OffsetX);
            Assert.Equal(expected.OffsetY, map.View.OffsetY);

            // A drag from empty space: the offset at the press plus the pointer's travel (FastSchematic.OnMouseMove),
            // with the zoom untouched and no click (the selection stays empty).
            Point? found = null;
            for (double y = 70; y < map.Bounds.Height - 260 && found is null; y += 9)
                for (double x = 60; x < map.Bounds.Width - 320; x += 9)
                {
                    var c = new Point(x, y);
                    if (map.View.HitTestNode(new ScreenPoint(x, y)) is null && map.View.HitTestBranch(new ScreenPoint(x, y)) is null
                        && !map.MinimapRect.Inflate(10).Contains(c)) { found = c; break; }
                }
            Point from = found ?? throw new InvalidOperationException("no empty map space to drag from");
            Point w0 = ToWindow(w, from), w1 = w0 + new Vector(37.5, -23.25), w2 = w0 + new Vector(61, 18);
            double zoom = map.View.Zoom, ox = map.View.OffsetX, oy = map.View.OffsetY;
            int buildsBefore = map.StaticBuilds;
            w.MouseDown(w0, MouseButton.Left);
            w.MouseMove(w1);
            Dispatcher.UIThread.RunJobs();
            w.CaptureRenderedFrame()?.Dispose();   // a frame mid-drag (drawn through the translation)
            w.MouseMove(w2);
            Dispatcher.UIThread.RunJobs();
            w.CaptureRenderedFrame()?.Dispose();
            int buildsMidDrag = map.StaticBuilds;
            w.MouseUp(w2, MouseButton.Left);
            Dispatcher.UIThread.RunJobs();
            Point s0 = ToMap(w, w0), s2 = ToMap(w, w2);
            Assert.Equal(zoom, map.View.Zoom);
            Assert.Equal(ox + (s2.X - s0.X), map.View.OffsetX);
            Assert.Equal(oy + (s2.Y - s0.Y), map.View.OffsetY);
            Assert.Null(w.ViewModel.Map.Selection);
            // The cached layer was built at most once for the whole drag (on the first move, with the drag margin), and
            // is rebuilt exactly at rest once the drag ends.
            Assert.True(buildsMidDrag - buildsBefore <= 1, $"{buildsMidDrag - buildsBefore} rebuilds during one short drag");
            w.CaptureRenderedFrame()?.Dispose();
            Assert.Equal(buildsMidDrag + 1, map.StaticBuilds);
        }
        finally { w.Close(); }
    }

    // ---------------------------------------------------------------- tests: keys and commands

    [AvaloniaFact]
    public void The_View_menu_holds_the_WPF_map_keys_and_each_key_fires_its_command()
    {
        using var temp = new TempFolder();
        MainWindow w = OpenAndSettle(temp, Case("case9"));
        try
        {
            SchematicControl map = w.MapControl;
            MainWindowViewModel vm = w.ViewModel;

            // their keys, after the File and Run menus and before Help.
            // Test retargeted 2026-10-20 (UI programme integration): Help now stays the last menu (MenuOrders.Last); the
            // assertion is unchanged in strength (the whole bar, in order), with the WPF-sensible order Mark asked for.
            // their keys, after the File, Help and Run menus.
            Assert.Equal(new[] { "_File", "_Run", "_View", "_Help" },
                w.FindControl<Menu>("MenuBar")!.Items.OfType<MenuItem>().Select(m => (string?)m.Header));
            MenuItem view = w.FindControl<Menu>("MenuBar")!.Items.OfType<MenuItem>().Single(m => (string?)m.Header == "_View");
            List<MenuItem> items = view.Items.OfType<MenuItem>().ToList();
            // Test retargeted 2026-10-21 (Phase 5d): the View menu gains "Line routes" (the asset overlay's line route
            // layer, a toggle with no key) after Fleet; the assertion is unchanged in strength (the whole menu, in order,
            // with every item's key), and stronger: the new item must carry no key, and the layer must start off.
            Assert.Equal(new[] { "Fit to view", "Minimap", "Coast", "Boundaries", "GSP areas", "DNO areas", "GSP regions", "Assets",
                    "Fleet", "Line routes", "Colour by", "Trip largest infeed", "Clear selection" },
                items.Select(i => (string?)i.Header));
            Key?[] keys = { Key.F, Key.M, Key.C, Key.B, Key.G, Key.D, Key.R, Key.A, null, null, null, Key.T, Key.Escape };
            Assert.Equal(keys.Length, items.Count);
            for (int i = 0; i < keys.Length; i++)
                Assert.Equal(keys[i] is Key k ? new KeyGesture(k) : null, items[i].InputGesture);
            Assert.False(map.ShowLineRoutes);
            Assert.Equal(new[] { "LOADING", "VOLTAGE", "CONDITION", "STRESS" }, items[10].Items.OfType<MenuItem>().Select(i => (string?)i.Header));

            // F: back to the fitted view after a zoom.
            map.ZoomAt(new Point(100, 100), 3);
            SchematicView fit = Reference(map, vm.Run.LoadedModel!);
            Assert.True(fit.FitToView());
            Press(w, Key.F);
            Assert.Equal(fit.Zoom, map.View.Zoom);
            Assert.Equal(fit.OffsetX, map.View.OffsetX);
            Assert.Equal(fit.OffsetY, map.View.OffsetY);

            // M, C, B, G: each flips its layer and records it in the settings (WPF saves the same flags on exit).
            (Key Key, Func<bool> Layer, Func<bool> Setting)[] toggles =
            {
                (Key.M, () => map.ShowMinimap, () => vm.Settings.Minimap),
                (Key.C, () => map.ShowCoast, () => vm.Settings.Coast),
                (Key.B, () => map.ShowBoundaries, () => vm.Settings.Boundaries),
                (Key.G, () => map.ShowGspAreas, () => vm.Settings.GspAreas),
            };
            foreach ((Key key, Func<bool> layer, Func<bool> setting) in toggles)
            {
                bool before = layer();
                Press(w, key);
                Assert.Equal(!before, layer());
                Assert.Equal(layer(), setting());
                Press(w, key);
                Assert.Equal(before, layer());
            }
            Assert.True(vm.SettingsChanged);

            // D, R, A: no local geography here, so (as in WPF) the keys do nothing and the items are disabled.
            Assert.False(map.HasGeography);
            foreach ((Key key, string id) in new[] { (Key.D, MapCommands.DnoAreas), (Key.R, MapCommands.GspRegions), (Key.A, MapCommands.Assets) })
            {
                Assert.False(vm.Registry.Get(id).IsEnabled);
                Press(w, key);
            }
            Assert.False(map.ShowDnoAreas || map.ShowGspRegions || map.ShowAssets);

            // The colour-mode items set the tint and the legend (the WPF COLOR button's modes).
            Await(vm.Registry.Get(MapCommands.ColourById(MapColourMode.Voltage)).RunAsync());
            Assert.Equal(MapColourMode.Voltage, map.ColorMode);
            Assert.Equal("VOLTAGE (p.u.)", vm.Dashboard.LegendTitle);
            Assert.True(vm.Settings.ColorVoltage);
            Await(vm.Registry.Get(MapCommands.ColourById(MapColourMode.Stress)).RunAsync());
            Assert.Equal(MapColourMode.Stress, map.ColorMode);
            Assert.Equal("BRANCH STRESS", vm.Dashboard.LegendTitle);
            Assert.False(vm.Settings.ColorVoltage);

            // The Fleet item flips the fleet layer.
            Await(vm.Registry.Get(MapCommands.Fleet).RunAsync());
            Assert.True(map.ShowFleet);
            Assert.True(vm.Settings.Fleet);

            // T: the largest online unit in the model is tripped at its bus, with the WPF log line; a second T refuses it.
            SimFrame last = vm.Run.LastFrame!;
            Generator g = InfeedSelection.LargestOnline(last.Model)!.Value.Unit;
            Press(w, Key.T);
            AssertTripLogged(vm, g);
            Press(w, Key.T);
            AssertRefusalLogged(vm);
        }
        finally { w.Close(); }
    }

    [AvaloniaFact]
    public void With_local_geography_the_D_R_and_A_keys_toggle_their_layers()
    {
        using var temp = new TempFolder();
        string geo = Path.Combine(temp.Path, "gda-out", "geo");
        Directory.CreateDirectory(geo);
        const string square = "{\"type\":\"FeatureCollection\",\"features\":[{\"type\":\"Feature\",\"properties\":{\"area\":\"A\",\"dno\":\"D\",\"gsps\":\"G\",\"gspgroup\":\"_A\"},"
            + "\"geometry\":{\"type\":\"Polygon\",\"coordinates\":[[[-3,52],[-2,52],[-2,53],[-3,53],[-3,52]]]}}]}";
        File.WriteAllText(Path.Combine(geo, "dno_areas.geojson"), square);
        File.WriteAllText(Path.Combine(geo, "gsp_regions.geojson"), square);
        File.WriteAllText(Path.Combine(geo, "assets.geojson"),
            "{\"type\":\"FeatureCollection\",\"features\":[{\"type\":\"Feature\",\"properties\":{\"technology_or_fuel\":\"Wind Onshore\",\"capacity_mw\":50,\"name\":\"W\",\"source\":\"REPD\"},"
            + "\"geometry\":{\"type\":\"Point\",\"coordinates\":[-2.5,52.5]}}]}");

        MainWindow w = OpenAndSettle(temp, Case("uk"), problems => GeographyData.Load(temp.Path, problems));
        try
        {
            SchematicControl map = w.MapControl;
            Assert.True(map.HasGeography);
            Assert.False(w.ViewModel.HasProblem, w.ViewModel.ProblemText);
            foreach ((Key key, Func<bool> layer) in new (Key, Func<bool>)[] { (Key.D, () => map.ShowDnoAreas), (Key.R, () => map.ShowGspRegions), (Key.A, () => map.ShowAssets) })
            {
                Assert.False(layer());
                Press(w, key);
                Assert.True(layer());
            }
            using (WriteableBitmap? frame = w.CaptureRenderedFrame()) Assert.NotNull(frame);   // the layers and their key draw
        }
        finally { w.Close(); }
    }

    [AvaloniaFact]
    public void The_single_key_shortcuts_stand_aside_while_a_text_box_or_combo_box_has_the_focus()
    {
        // The rule itself: plain and Shift keys (and '.', ',', Escape) need the guard; Ctrl and Alt chords and function
        // keys never type a character, so they do not.
        Assert.True(TypingGuard.NeedsGuard("F"));
        Assert.True(TypingGuard.NeedsGuard("T"));
        Assert.True(TypingGuard.NeedsGuard("Period"));
        Assert.True(TypingGuard.NeedsGuard("Comma"));
        Assert.True(TypingGuard.NeedsGuard("Escape"));
        Assert.True(TypingGuard.NeedsGuard("Shift+T"));
        Assert.False(TypingGuard.NeedsGuard("F5"));
        Assert.False(TypingGuard.NeedsGuard("F6"));
        Assert.False(TypingGuard.NeedsGuard("Ctrl+O"));
        Assert.False(TypingGuard.NeedsGuard("Alt+M"));

        using var temp = new TempFolder();
        MainWindow w = OpenAndSettle(temp, Case("case9"));
        try
        {
            SchematicControl map = w.MapControl;
            MainWindowViewModel vm = w.ViewModel;
            var box = new TextBox { Name = "FutureSearchBox" };
            // Test retargeted 2026-10-20 (UI programme integration): the build line's side panel (the temporary live
            // panel's column) is gone, so the future search box is put beside the licence tier in the window header,
            // where WPF has its search box; every assertion below is unchanged.
            // Test retargeted 2026-10-20 (UI programme 1.7): the licence tier left the header for the window title (WPF), so
            // the extra text box goes beside the header's real substation search box instead; same panel, every assertion
            // below unchanged (HeaderTests checks the guard with the real search box focused).
            ((Panel)w.FindControl<TextBox>("SearchBox")!.Parent!).Children.Add(box);
            Dispatcher.UIThread.RunJobs();
            Assert.True(box.Focus());
            Dispatcher.UIThread.RunJobs();
            Assert.True(w.IsTyping());

            bool minimap = map.ShowMinimap;
            string status = vm.StatusText;
            var step = vm.Run.SelectedStep;
            map.ZoomAt(new Point(100, 100), 3);
            double zoom = map.View.Zoom;
            foreach (Key key in new[] { Key.M, Key.T, Key.F, Key.OemPeriod, Key.OemComma })
                Press(w, key);
            Assert.Equal(minimap, map.ShowMinimap);       // M did not toggle the minimap
            Assert.Equal(status, vm.StatusText);          // T did not trip
            Assert.Equal(zoom, map.View.Zoom);            // F did not refit
            Assert.Same(step, vm.Run.SelectedStep);       // '.' and ',' did not move the step

            // A function key is not guarded: F5 still plays and pauses from the text box.
            bool paused = vm.Run.IsPaused;
            Press(w, Key.F5);
            Assert.NotEqual(paused, vm.Run.IsPaused);
            Press(w, Key.F5);

            // A combo box is guarded too (letters drive its type-ahead).
            ComboBox picker = w.FindControl<ComboBox>("StepPicker")!;
            Assert.True(picker.Focus());
            Dispatcher.UIThread.RunJobs();
            Press(w, Key.M);
            Assert.Equal(minimap, map.ShowMinimap);

            // With the focus back on the map the same keys work.
            Assert.True(map.Focus());
            Dispatcher.UIThread.RunJobs();
            Assert.False(w.IsTyping());
            Press(w, Key.M);
            Assert.Equal(!minimap, map.ShowMinimap);
        }
        finally { w.Close(); }
    }

    [AvaloniaFact]
    public void A_right_click_on_a_bus_builds_the_registrys_bus_context_menu_and_its_trip_uses_the_WPF_rule()
    {
        using var temp = new TempFolder();
        MainWindow w = OpenAndSettle(temp, Case("case9"));
        try
        {
            SchematicControl map = w.MapControl;
            MainWindowViewModel vm = w.ViewModel;
            GridModel model = vm.Run.LastFrame!.Model;

            // A bus with a unit generating on it (not the slack balancer: the shared rule skips power-flow-only units).
            int target = -1;
            Point at = default;
            for (int i = 0; i < map.View.Nodes.Count; i++)
            {
                int id = map.View.Nodes[i].Id;
                if (InfeedSelection.LargestOnline(model, id) is null) continue;
                Point s = P(map.View.NodeScreen(i));
                if (map.MinimapRect.Inflate(20).Contains(s) || s.X > map.Bounds.Width - 300) continue;
                if (map.View.HitTestNode(new ScreenPoint(s.X, s.Y)) != id) continue;
                target = id;
                at = s;
                break;
            }
            Assert.NotEqual(-1, target);

            // The real right-click reaches the map and remembers the bus for the menu's command.
            Point wp = ToWindow(w, at);
            w.MouseDown(wp, MouseButton.Right);
            w.MouseUp(wp, MouseButton.Right);
            Dispatcher.UIThread.RunJobs();
            Assert.Equal(target, w.MapActions.ContextBusId);

            // The menu is the registry's for a bus: exactly the bus-target commands, the trip first.
            // Test retargeted 2026-10-20 (UI programme ASSETS A3): the ASSETS tab adds "Open in Assets" to the map's bus
            // and branch context menus (plan section 3.5), so the bus menu now holds two commands where it held one. The
            // assertion is unchanged in kind (the whole menu, exactly, in registry order, against the registry's bus
            // list) and stronger: the trip keeps its place, text and enabled state, and the second item is pinned too
            // (its id, its text, enabled on a bus).
            ContextMenu menu = w.MapContextMenuAt(at) ?? throw new InvalidOperationException("no context menu on a bus");
            MenuItem[] items = menu.Items.OfType<MenuItem>().ToArray();
            Assert.Equal(new[] { MapCommands.TripAtBus, GridSim.Ui.Assets.Navigation.AssetCommands.OpenInAssets }, items.Select(i => (string?)i.Tag));
            MenuItem item = items[0];
            Assert.Equal(MapCommands.TripAtBus, (string?)item.Tag);
            Assert.Equal("Trip the largest infeed at this bus", (string?)item.Header);
            Assert.Equal("Open in Assets", (string?)items[1].Header);
            Assert.True(items[1].Command!.CanExecute(null));
            Assert.Equal(new[] { MapCommands.TripAtBus, GridSim.Ui.Assets.Navigation.AssetCommands.OpenInAssets },
                vm.Registry.ForTarget(CommandTargets.Bus, CommandModes.Run, CommandPlatforms.Desktop).Select(c => c.Id));
            Assert.True(item.Command!.CanExecute(null));

            // Choosing it trips the largest unit at that bus (InfeedSelection, as WPF), with the WPF log line; the same
            // unit cannot be lost twice.
            Generator g = InfeedSelection.LargestOnline(model, target)!.Value.Unit;
            item.Command.Execute(null);
            Dispatcher.UIThread.RunJobs();
            AssertTripLogged(vm, g);
            item.Command.Execute(null);
            Dispatcher.UIThread.RunJobs();
            AssertRefusalLogged(vm);

            // Nothing under the pointer: no menu (WPF offers nothing there on a local case), and no bus remembered.
            Point empty = new(5, map.Bounds.Height / 2);
            Assert.Null(map.View.HitTestNode(new ScreenPoint(empty.X, empty.Y)));
            Assert.Null(w.MapContextMenuAt(empty));
            Assert.Null(w.MapActions.ContextBusId);
            Assert.False(vm.Registry.Get(MapCommands.TripAtBus).IsEnabled);
        }
        finally { w.Close(); }
    }

    [AvaloniaFact]
    public void The_map_commands_take_the_WPF_keys_without_clashing_with_the_shell_or_the_Run_menu()
    {
        using var temp = new TempFolder();
        MainWindow w = OpenAndSettle(temp, Case("case9"));
        try
        {
            IReadOnlyDictionary<string, AppCommand> keys = w.ViewModel.Registry.Gestures(CommandModes.Run, CommandPlatforms.Desktop);
            var expected = new Dictionary<string, string>
            {
                ["F"] = MapCommands.FitToView, ["M"] = MapCommands.Minimap, ["C"] = MapCommands.Coast,
                ["B"] = MapCommands.Boundaries, ["G"] = MapCommands.GspAreas, ["D"] = MapCommands.DnoAreas,
                ["R"] = MapCommands.GspRegions, ["A"] = MapCommands.Assets, ["T"] = MapCommands.TripLargestInfeed,
                ["Escape"] = MapCommands.ClearSelection,
                // Already there before the map, unchanged by it.
                ["F5"] = RunCommands.PlayPause, ["F6"] = RunCommands.StepOnce, ["Period"] = RunCommands.FinerStep,
                ["Comma"] = RunCommands.CoarserStep, ["Ctrl+O"] = ShellCommands.OpenCase,
            };
            foreach ((string gesture, string id) in expected) Assert.Equal(id, keys[gesture].Id);
            // Every registry shortcut is bound once on the window (one key binding each).
            Assert.Equal(keys.Count, w.KeyBindings.Count);
        }
        finally { w.Close(); }
    }

    // ---------------------------------------------------------------- the coastline copy

    [Fact]
    public void The_apps_coastline_is_the_WPF_coastline_point_for_point()
    {
        // The WPF table is read from its source (this app cannot reference the WPF project): the same lon/lat pairs
        // and the same bounding box, so the backdrop lines up exactly where the WPF one does.
        string wpf = File.ReadAllText(Path.Combine(RepoRoot(), "src", "GridSim.Wpf", "Visuals", "CoastlineGb.cs"));
        string app = File.ReadAllText(Path.Combine(RepoRoot(), "src", "GridSim.App", "Map", "CoastlineGb.cs"));
        static string Table(string src) => Regex.Match(src, @"LonLat\s*=\s*\{(?<t>.*?)\};", RegexOptions.Singleline).Groups["t"].Value;
        static string Box(string src) => Regex.Match(src, @"const double (?<b>LonMin = [^;]*);").Groups["b"].Value;
        static List<double> Numbers(string t) => Regex.Matches(t, @"-?\d+\.\d+").Select(m => double.Parse(m.Value, Inv)).ToList();
        List<double> a = Numbers(Table(wpf)), b = Numbers(Table(app));
        Assert.True(a.Count > 100, $"WPF coastline table not found ({a.Count} numbers)");
        Assert.Equal(a, b);
        Assert.Equal(Box(wpf), Box(app));
        Assert.Equal(a.Count / 2, CoastlineGb.World.Count);
        // The projection: x = (lon - LonMin) / (LonMax - LonMin), y = 1 - (lat - LatMin) / (LatMax - LatMin).
        Assert.Equal((a[0] - -8.2) / (1.9 - -8.2), CoastlineGb.World[0].X);
        Assert.Equal(1.0 - (a[1] - 49.8) / (59.0 - 49.8), CoastlineGb.World[0].Y);
    }
}
