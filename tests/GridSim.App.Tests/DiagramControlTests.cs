using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Threading;
using GridSim.App.Controls.Diagram;
using GridSim.Ui.Diagrams;
using GridSim.Ui.Presentation;
using GridSim.Ui.Schematic;
using GridSim.Ui.Visuals;
using Xunit;

namespace GridSim.App.Tests;

/// <summary>
/// UI programme ASSETS A1b: the Avalonia <see cref="DiagramControl"/> on the headless platform with the real Skia
/// renderer. It draws every symbol of the toolkit in every rotation and switch state without an exception, paints a
/// breaker's blade in its closed or open colour where the geometry says the blade is (a few sampled pixels, so a font
/// or anti-aliasing difference cannot fail it), redraws when the model's state changes, and reports the symbol under
/// the pointer on hover and on a click.
/// </summary>
public sealed class DiagramControlTests
{
    /// <summary>A window with the control at its top-left corner at an exact size, so control and frame pixels match.</summary>
    private static Window Host(Control control, double w, double h)
    {
        control.Width = w;
        control.Height = h;
        control.HorizontalAlignment = HorizontalAlignment.Left;
        control.VerticalAlignment = VerticalAlignment.Top;
        var window = new Window { Width = 640, Height = 480, Content = control };
        window.Show();
        return window;
    }

    private static WriteableBitmap Frame(Window window)
    {
        TestShell.Pump();
        WriteableBitmap? frame = window.CaptureRenderedFrame();
        Assert.NotNull(frame);
        return frame;
    }

    private static Color PixelAt(WriteableBitmap frame, int x, int y)
    {
        using ILockedFramebuffer fb = frame.Lock();
        Assert.InRange(x, 0, fb.Size.Width - 1);
        Assert.InRange(y, 0, fb.Size.Height - 1);
        int at = y * fb.RowBytes + x * 4;
        byte c0 = Marshal.ReadByte(fb.Address, at), c1 = Marshal.ReadByte(fb.Address, at + 1), c2 = Marshal.ReadByte(fb.Address, at + 2);
        if (fb.Format == PixelFormats.Bgra8888) return Color.FromRgb(c2, c1, c0);
        Assert.True(fb.Format == PixelFormats.Rgba8888, $"unexpected frame format {fb.Format}");
        return Color.FromRgb(c0, c1, c2);
    }

    private static int Distance(Color a, Color b) =>
        Math.Max(Math.Abs(a.R - b.R), Math.Max(Math.Abs(a.G - b.G), Math.Abs(a.B - b.B)));

    /// <summary>The smallest per-channel distance to <paramref name="expected"/> within a pixel of (x, y).</summary>
    private static int Nearest(WriteableBitmap frame, ScreenPoint p, Color expected)
    {
        int cx = (int)Math.Floor(p.X), cy = (int)Math.Floor(p.Y), best = int.MaxValue;
        for (int dy = -1; dy <= 1; dy++)
            for (int dx = -1; dx <= 1; dx++)
                best = Math.Min(best, Distance(PixelAt(frame, cx + dx, cy + dy), expected));
        return best;
    }

    private static Color C(UiColour c) => Color.FromRgb(c.R, c.G, c.B);

    // ---------------------------------------------------------------------------------------------------------------

    [AvaloniaFact]
    public void The_control_renders_every_symbol_in_every_rotation_and_state_without_an_exception()
    {
        var m = new DiagramModel();
        int i = 0;
        foreach (DiagramSymbolKind kind in Enum.GetValues<DiagramSymbolKind>())
            foreach (DiagramRotation r in Enum.GetValues<DiagramRotation>())
                foreach (DiagramSwitchState sw in Enum.GetValues<DiagramSwitchState>())
                {
                    bool bar = kind is DiagramSymbolKind.Busbar or DiagramSymbolKind.BusSection;
                    var at = new ScreenPoint(60 * (i % 24), 80 * (i / 24));
                    var state = new DiagramState(i % 5 != 0, sw, i % 3 == 0 ? null : i % 7 == 0 ? double.NaN : (i % 11) / 10.0);
                    m.Add(bar
                        ? new DiagramSymbol("S" + i, kind, at, r, "BAR " + i, 50, [10.0, 40.0]) { State = state }
                        : new DiagramSymbol("S" + i, kind, at, r, kind == DiagramSymbolKind.ProtectionRelay ? "51" : "L" + i) { State = state });
                    i++;
                }
        m.Connect("S0", "T0", "S24", "B");   // a busbar tap to a breaker
        var control = new DiagramControl { Model = m };
        Window w = Host(control, 600, 400);
        try
        {
            Frame(w);
            Assert.True(control.CurrentDrawing()!.Count > 500);
            foreach ((double cw, double ch) in new[] { (0.0, 0.0), (1.0, 1.0), (47.0, 47.0), (48.5, 100.0), (2000.0, 30.0) })
            {
                control.Width = cw;
                control.Height = ch;
                Frame(w);
            }
            control.Model = new DiagramModel();
            Frame(w);
            control.Model = null;
            Frame(w);
            Assert.Null(control.CurrentDrawing());
        }
        finally { w.Close(); }
    }

    /// <summary>A diagram of one breaker at the origin.</summary>
    private static DiagramModel OneBreaker(DiagramSwitchState sw)
    {
        var m = new DiagramModel();
        m.Add(new DiagramSymbol("Q1", DiagramSymbolKind.CircuitBreaker, new ScreenPoint(0, 0)) { State = new DiagramState(true, sw) });
        return m;
    }

    [AvaloniaFact]
    public void A_pixel_probe_finds_a_breaker_blade_in_its_closed_or_open_colour_and_a_state_change_redraws()
    {
        DiagramModel m = OneBreaker(DiagramSwitchState.Closed);
        var control = new DiagramControl { Model = m };
        Window w = Host(control, 240, 240);
        try
        {
            WriteableBitmap frame = Frame(w);
            DiagramViewport view = control.EffectiveViewport;
            Assert.True(view.Scale > 3, $"the fitted scale {view.Scale} is too small to probe a blade");
            // Closed: the blade runs straight up the middle (local (0, 10) to (0, -10)); its middle is the origin.
            ScreenPoint closedMid = view.ToScreen(new ScreenPoint(0, 0));
            Assert.True(Nearest(frame, closedMid, C(DiagramStyle.Closed)) <= 40, "closed blade colour");
            ScreenPoint lead = view.ToScreen(new ScreenPoint(0, 15));
            Assert.True(Nearest(frame, lead, C(DiagramStyle.Energised)) <= 40, "energised lead colour");

            // Open it: the model's change redraws; the blade now stands off to the left, in the open colour.
            m.SetState("Q1", new DiagramState(true, DiagramSwitchState.Open));
            frame = Frame(w);
            ScreenPoint a = view.ToScreen(new ScreenPoint(0, SymbolLibrary.ContactY)), b = view.ToScreen(SymbolLibrary.OpenBladeTip);
            var openMid = new ScreenPoint((a.X + b.X) / 2, (a.Y + b.Y) / 2);
            Assert.True(Nearest(frame, openMid, C(DiagramStyle.Open)) <= 40, "open blade colour");
            Assert.True(Nearest(frame, closedMid, C(DiagramStyle.Closed)) > 60, "the closed position is empty once open");
            Assert.True(Nearest(frame, closedMid, C(DiagramStyle.Open)) > 60, "the open blade does not cross the closed position");
        }
        finally { w.Close(); }
    }

    [AvaloniaFact]
    public void A_click_reports_the_symbol_under_it_and_hover_reports_changes()
    {
        var m = new DiagramModel();
        DiagramLayout.BusbarRow(m, "BB", new ScreenPoint(0, 0), 2, label: "400 kV");
        DiagramLayout.FeederBay(m, "BB", 0, "L1", FeederEnd.Line);
        DiagramLayout.FeederBay(m, "BB", 1, "L2", FeederEnd.Cable);
        var control = new DiagramControl { Model = m };
        var clicks = new List<DiagramHit?>();
        var hovers = new List<DiagramHit?>();
        control.SymbolClicked += (_, e) => clicks.Add(e.Hit);
        control.SymbolHovered += (_, e) => hovers.Add(e.Hit);
        Window w = Host(control, 400, 400);
        try
        {
            Frame(w);
            DiagramViewport view = control.EffectiveViewport;
            Point At(string id) { ScreenPoint s = view.ToScreen(m.Symbol(id).Position); return new Point(s.X, s.Y); }

            w.MouseDown(At("L2/CB"), MouseButton.Left);
            w.MouseUp(At("L2/CB"), MouseButton.Left);
            w.MouseDown(At("L1/END"), MouseButton.Left);
            w.MouseUp(At("L1/END"), MouseButton.Left);
            w.MouseDown(new Point(395, 5), MouseButton.Left);     // empty: no click event
            w.MouseUp(new Point(395, 5), MouseButton.Left);
            Dispatcher.UIThread.RunJobs();
            Assert.Equal(new DiagramHit?[]
            {
                new DiagramHit(DiagramHitKind.Symbol, "L2/CB", DiagramSymbolKind.CircuitBreaker),
                new DiagramHit(DiagramHitKind.Symbol, "L1/END", DiagramSymbolKind.LineEnd),
            }, clicks);

            // Hover: onto a disconnector, onto the busbar, then off everything.
            hovers.Clear();
            w.MouseMove(At("L1/DS"));
            w.MouseMove(At("L1/DS"));                              // no change: no second event
            ScreenPoint onBar = view.ToScreen(new ScreenPoint(10, 0));
            w.MouseMove(new Point(onBar.X, onBar.Y));
            w.MouseMove(new Point(395, 395));
            Dispatcher.UIThread.RunJobs();
            Assert.Equal(new DiagramHit?[]
            {
                new DiagramHit(DiagramHitKind.Symbol, "L1/DS", DiagramSymbolKind.Disconnector),
                new DiagramHit(DiagramHitKind.Symbol, "BB", DiagramSymbolKind.Busbar),
                null,
            }, hovers);
            Assert.Equal("L1/DS", control.HitTestAt(At("L1/DS"))!.Id);
        }
        finally { w.Close(); }
    }

    [AvaloniaFact]
    public void The_drawing_is_cached_per_state_and_the_wheel_zooms_about_the_pointer()
    {
        DiagramModel m = OneBreaker(DiagramSwitchState.Closed);
        var control = new DiagramControl { Model = m };
        Window w = Host(control, 240, 240);
        try
        {
            Frame(w);
            VisualDisplayList first = control.CurrentDrawing()!;
            Assert.Same(first, control.CurrentDrawing());          // nothing changed: the same list
            m.SetState("Q1", new DiagramState(false, DiagramSwitchState.Open));
            Assert.NotSame(first, control.CurrentDrawing());       // a state change rebuilds it

            DiagramViewport before = control.EffectiveViewport;
            var at = new Point(100, 90);
            w.MouseWheel(at, new Vector(0, 1));
            Dispatcher.UIThread.RunJobs();
            Assert.False(control.AutoFit);
            Assert.Equal(before.Scale * DiagramControl.WheelZoomFactor, control.Viewport.Scale, 12);
            ScreenPoint d0 = before.ToDiagram(new ScreenPoint(at.X, at.Y)), d1 = control.Viewport.ToDiagram(new ScreenPoint(at.X, at.Y));
            Assert.Equal(d0.X, d1.X, 9);
            Assert.Equal(d0.Y, d1.Y, 9);
        }
        finally { w.Close(); }
    }

    [AvaloniaFact]
    public void Avalonia_stock_dash_styles_are_the_shared_named_patterns()
    {
        // The named patterns state WPF's stock arrays; Avalonia's stock styles must be the same, so a front end that
        // builds its pen from VisualDashPatterns draws the dashes either framework would.
        Assert.Equal(VisualDashPatterns.Dashes(VisualDashPattern.Dash), DashStyle.Dash.Dashes);
        Assert.Equal(VisualDashPatterns.Offset(VisualDashPattern.Dash), DashStyle.Dash.Offset);
        Assert.Equal(VisualDashPatterns.Dashes(VisualDashPattern.DashDot), DashStyle.DashDot.Dashes);
        Assert.Equal(VisualDashPatterns.Offset(VisualDashPattern.DashDot), DashStyle.DashDot.Offset);
        Assert.Null(VisualDashPatterns.Dashes(VisualDashPattern.None));
    }
}
