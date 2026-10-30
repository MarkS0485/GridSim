using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.Json;
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
using GridSim.Core.IO.Json;
using GridSim.Core.Licensing;
using GridSim.Core.Model;
using GridSim.Ui.Assets.Navigation;
using GridSim.Ui.Assets.Overlay;
using GridSim.Ui.Commands;
using GridSim.Ui.Models;
using GridSim.Ui.Schematic;
using GridSim.Ui.Shell;
using Xunit;

namespace GridSim.App.Tests;

/// <summary>
/// UI programme Phase 5d (2026-10-21) in the Avalonia window (headless, Skia): the NETWORK map's "Line routes" layer. The
/// case is data/uk written as a JSON v2 case with an asset overlay of two routes: 'rt.geo' (line L3-4, three spans with
/// geographic far towers on longitude -6.5, west of every bus of the case, so the polyline is clear of every roundel and
/// corridor) and 'rt.chain' (line L1-2, placed by chainage only, so not drawn). Pinned: the layer is off by default and
/// draws nothing then; View / Line routes turns it on with its tick and setting, and the pixels along a route segment then
/// change to the route colour (the carried line's loading bin); a click on the polyline selects the route's record in
/// ASSETS; and Show on map from the route page switches to NETWORK and frames the route exactly as LineRouteMap.Frame does
/// (turning the layer on when it was off).
/// </summary>
public sealed class LineRouteMapViewTests
{
    private static string RepoRoot()
    {
        for (DirectoryInfo? d = new(AppContext.BaseDirectory); d is not null; d = d.Parent)
            if (File.Exists(Path.Combine(d.FullName, "GridSim.slnx"))) return d.FullName;
        throw new InvalidOperationException("The GridSim checkout (GridSim.slnx) was not found above " + AppContext.BaseDirectory);
    }

    private const string FileA = "routes.assets.json";

    private const string Overlay = """
        { "schema": "gridsim-assets/2", "records": [
          { "id": "ov.zebra", "kind": "conductorType", "refs": {}, "params": { "code": "Zebra", "crossSectionMm2": 428.9 } },
          { "id": "rt.geo", "kind": "lineRoute", "refs": { "line": { "line": "L3-4 N England-Yorks" } }, "params": { "name": "Geo route" } },
          { "id": "rt.g1", "kind": "span", "parent": "rt.geo", "refs": { "conductor": { "record": "ov.zebra" } }, "params": { "lengthM": 166800, "chainageM": 0, "latitudeDeg": 57.5, "longitudeDeg": -6.5 } },
          { "id": "rt.g2", "kind": "span", "parent": "rt.geo", "refs": { "conductor": { "record": "ov.zebra" } }, "params": { "lengthM": 166800, "chainageM": 166800, "latitudeDeg": 56.0, "longitudeDeg": -6.5 } },
          { "id": "rt.g3", "kind": "span", "parent": "rt.geo", "refs": { "conductor": { "record": "ov.zebra" } }, "params": { "lengthM": 166800, "chainageM": 333600, "latitudeDeg": 54.5, "longitudeDeg": -6.5 } },
          { "id": "rt.chain", "kind": "lineRoute", "refs": { "line": { "line": "L1-2 N-S Scotland" } }, "params": {} },
          { "id": "rt.c1", "kind": "span", "parent": "rt.chain", "refs": { "conductor": { "record": "ov.zebra" } }, "params": { "lengthM": 1000, "chainageM": 0 } }
        ] }
        """;

    private static string WriteCase(TempFolder temp)
    {
        GridModel uk = JsonGridLoader.LoadDirectory(Path.Combine(RepoRoot(), "data", "uk"));
        string manifest = temp.File("uk.case.json");
        V2CaseWriter.Write(uk, manifest);
        File.WriteAllText(temp.File(FileA), Overlay);
        using JsonDocument m = JsonDocument.Parse(File.ReadAllBytes(manifest));
        using var ms = new MemoryStream();
        using (var w = new Utf8JsonWriter(ms, new JsonWriterOptions { Indented = true }))
        {
            w.WriteStartObject();
            foreach (JsonProperty p in m.RootElement.EnumerateObject()) p.WriteTo(w);
            w.WriteStartArray("assets");
            w.WriteStringValue(FileA);
            w.WriteEndArray();
            w.WriteEndObject();
        }
        File.WriteAllBytes(manifest, ms.ToArray());
        return manifest;
    }

    private static ShellOptions Options(TempFolder temp, ModelSource source) => new()
    {
        SettingsStore = ShellSettingsStore.ForFile(temp.File("settings.json")),
        DiscoverCases = (_, _) => new List<ModelSource> { source },
        LicenceDescription = () => "Test licence",
        Licence = () => new LicenceGate(Entitlement.FreeDefault),
        UseNativeMenu = false,
        AutoLoadAtStart = false,
        LoadGeography = _ => null,
    };

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

    private static void Close(MainWindow w)
    {
        w.Close();
        if (w.StopLoop is { } stop) Await(stop);
    }

    private static MainWindow OpenAndSettle(TempFolder temp, ModelSource source)
    {
        var w = new MainWindow(Options(temp, source));
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
        w.CaptureRenderedFrame()?.Dispose();
        Dispatcher.UIThread.RunJobs();
        return w;
    }

    private static Point ToWindow(MainWindow w, Point local)
        => w.MapControl.TranslatePoint(local, w) ?? throw new InvalidOperationException("the map is not in the window");

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

    // The window pixels in the 5 x 5 squares about the given window points, in order.
    private static List<(byte R, byte G, byte B)> Around(MainWindow w, IReadOnlyList<Point> windowPts)
    {
        using WriteableBitmap bmp = w.CaptureRenderedFrame() ?? throw new InvalidOperationException("no frame");
        using ILockedFramebuffer fb = bmp.Lock();
        var list = new List<(byte, byte, byte)>();
        foreach (Point p in windowPts)
            for (int dy = -2; dy <= 2; dy++)
                for (int dx = -2; dx <= 2; dx++)
                {
                    int x = (int)Math.Floor(p.X) + dx, y = (int)Math.Floor(p.Y) + dy;
                    list.Add(x < 0 || y < 0 || x >= fb.Size.Width || y >= fb.Size.Height ? ((byte)0, (byte)0, (byte)0) : Pixel(fb, x, y));
                }
        return list;
    }

    private static MenuItem ViewItem(MainWindow w, string header)
    {
        MenuItem view = w.FindControl<Menu>("MenuBar")!.Items.OfType<MenuItem>().Single(m => (string?)m.Header == "_View");
        return view.Items.OfType<MenuItem>().Single(i => (string?)i.Header == header);
    }

    private static string? SelectedTab(MainWindow w) => (w.TabStrip.SelectedItem as TabItem)?.Header as string;

    [AvaloniaFact]
    public void The_line_route_layer_is_off_by_default_draws_with_its_tick_selects_in_Assets_on_a_click_and_Show_on_map_frames_the_route()
    {
        using var temp = new TempFolder();
        MainWindow w = OpenAndSettle(temp, ModelCatalog.V2CaseSource(WriteCase(temp)));
        try
        {
            SchematicControl map = w.MapControl;
            MainWindowViewModel vm = w.ViewModel;
            Assert.Equal(MainWindow.NetworkTabHeader, SelectedTab(w));

            // The loaded case's routes are on the map control, the layer off by default (setting and tick off).
            LineRoutePolyline geo = Assert.Single(map.LineRoutes.Drawn);
            Assert.Equal("rt.geo", geo.RouteId);
            Assert.Equal(new[] { "rt.chain" }, map.LineRoutes.NotDrawn.Select(n => n.RouteId));
            Assert.False(map.ShowLineRoutes);
            Assert.False(vm.Settings.LineRoutes);
            AppCommand toggle = vm.Registry.Get(MapCommands.LineRoutes);
            MenuItem item = ViewItem(w, "Line routes");
            Assert.Equal(MenuItemToggleType.CheckBox, item.ToggleType);
            Assert.False(item.IsChecked);

            // The probe: points along the first two segments of the polyline (map-local, then window), clear of roundels.
            SchematicView v = map.View;
            IReadOnlyList<ScreenPoint> s = LineRouteMap.ToScreen(geo, v);
            var probes = new List<Point>();
            for (int seg = 0; seg + 1 < s.Count; seg++)
                for (int k = 1; k < 10; k++)
                {
                    double t = k / 10.0;
                    var local = new Point(s[seg].X + t * (s[seg + 1].X - s[seg].X), s[seg].Y + t * (s[seg + 1].Y - s[seg].Y));
                    Assert.Null(v.HitTestNode(new ScreenPoint(local.X, local.Y)));
                    Assert.True(new Rect(map.Bounds.Size).Contains(local), "probe " + local + " is off the map");
                    probes.Add(ToWindow(w, local));
                }
            List<(byte R, byte G, byte B)> off = Around(w, probes);

            // On: the command flips the layer, its setting and its tick; the pixels along the segments change, and the
            // route's colour (the carried line's loading bin, the map has a solved state) is among them.
            Await(toggle.RunAsync());
            Dispatcher.UIThread.RunJobs();
            Assert.True(map.ShowLineRoutes);
            Assert.True(vm.Settings.LineRoutes);
            Assert.True(toggle.Checked);
            Assert.True(item.IsChecked);
            List<(byte R, byte G, byte B)> on = Around(w, probes);
            int changed = off.Zip(on).Count(p => Distance(p.First, p.Second) > 24);
            Assert.True(changed >= 10, $"only {changed} pixels changed along the route");
            int? bucket = LineRouteMap.LoadingBucketOf(geo, map.View);
            Assert.NotNull(bucket);   // the line L3-4 is on the map with the frame's state
            Color want = MapPaint.LineRouteColour(bucket);
            int best = on.Min(p => Distance(p, (want.R, want.G, want.B)));
            Assert.True(best <= 40, $"the route colour {want} is not drawn along the route (closest off by {best})");
            Assert.True(off.Min(p => Distance(p, (want.R, want.G, want.B))) > best, "the route colour was there before the layer was on");

            // Off again: exactly the pixels of before.
            Await(toggle.RunAsync());
            Dispatcher.UIThread.RunJobs();
            Assert.False(map.ShowLineRoutes || vm.Settings.LineRoutes || toggle.Checked || item.IsChecked);
            Assert.Equal(off, Around(w, probes));

            // A click on the polyline (layer on) selects the route's record in ASSETS.
            Await(toggle.RunAsync());
            Dispatcher.UIThread.RunJobs();
            var midLocal = new Point((s[0].X + s[1].X) / 2, (s[0].Y + s[1].Y) / 2);
            Assert.Same(geo, map.LineRoutes.HitTest(map.View, new ScreenPoint(midLocal.X, midLocal.Y)));
            Point mid = ToWindow(w, midLocal);
            w.MouseDown(mid, MouseButton.Left);
            w.MouseUp(mid, MouseButton.Left);
            Dispatcher.UIThread.RunJobs();
            Assert.Equal(MainWindow.AssetsTabHeader, SelectedTab(w));
            AssetNavigatorModel nav = vm.Tabs.Assets;
            Assert.Equal(OverlayIndex.KeyOf("rt.geo"), nav.Selected!.Key);
            Assert.True(nav.CanShowOnMap);
            Assert.True(w.AssetsView.FindControl<Button>("ShowOnMapButton")!.IsEnabled);

            // Show on map with the layer off: back to NETWORK, the layer on (setting and tick), the route framed exactly as
            // LineRouteMap.Frame frames it, and the carried line selected.
            Await(toggle.RunAsync());
            Assert.False(map.ShowLineRoutes);
            Assert.True(nav.ShowOnMap());
            Dispatcher.UIThread.RunJobs();
            Assert.Equal(MainWindow.NetworkTabHeader, SelectedTab(w));
            Assert.True(map.ShowLineRoutes);
            Assert.True(vm.Settings.LineRoutes);
            Assert.True(item.IsChecked);
            var reference = new SchematicView { ViewportWidth = map.Bounds.Width, ViewportHeight = map.Bounds.Height };
            reference.Load(vm.Run.LoadedModel!);
            Assert.True(LineRouteMap.Frame(reference, geo));
            Assert.Equal(reference.Zoom, map.View.Zoom);
            Assert.Equal(reference.OffsetX, map.View.OffsetX);
            Assert.Equal(reference.OffsetY, map.View.OffsetY);
            int line = vm.Run.LoadedModel!.Branches.Select((b, i) => (b, i)).Single(x => !x.b.IsTransformer && x.b.Name == "L3-4 N England-Yorks").i;
            Assert.Equal(new MapSelection(MapSelectionKind.Branch, line), vm.Map.Selection);
            w.CaptureRenderedFrame()?.Dispose();   // the framed map renders
            foreach (ScreenPoint p in LineRouteMap.ToScreen(geo, map.View))
                Assert.True(new Rect(map.Bounds.Size).Contains(new Point(p.X, p.Y)), "tower " + p + " is off the framed map");
        }
        finally
        {
            Close(w);
        }
    }
}
