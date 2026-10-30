using System.ComponentModel;
using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Data;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Styling;
using GridSim.App.Controls;
using GridSim.Core.Dynamics;
using GridSim.Core.Frequency;
using GridSim.Ui.Presentation;
using GridSim.Ui.Schematic;
using GridSim.Ui.Visuals;
using Xunit;

namespace GridSim.App.Tests;

/// <summary>
/// UI programme Phase 1.3: the five Avalonia instrument controls (Gauge, Sparkline, FrequencyTrace, Phasor, PqDiagram),
/// ported from the WPF custom-drawn controls, on the headless platform with the real Skia renderer. Each control:
/// renders without an exception for normal and edge inputs (NaN, infinities, zero or inverted ranges, empty series,
/// below-minimum sizes); has the WPF control's properties, with the WPF defaults, and each of them binds; and draws the
/// right colour where the geometry says the needle, trace, band or dot is (a few sampled pixels, not a whole-image
/// comparison, so a font or anti-aliasing difference between machines cannot fail it).
/// </summary>
public sealed class InstrumentControlTests
{
    // ---------------------------------------------------------------------------------------------------------------
    // Hosting and pixels
    // ---------------------------------------------------------------------------------------------------------------

    /// <summary>A window with the control at its top-left corner at an exact size, so control and frame pixels match.</summary>
    private static Window Host(Control control, double w, double h)
    {
        control.Width = w;
        control.Height = h;
        control.HorizontalAlignment = HorizontalAlignment.Left;
        control.VerticalAlignment = VerticalAlignment.Top;
        var window = new Window { Width = 420, Height = 320, Content = control };
        window.Show();
        return window;
    }

    /// <summary>Render the window and return the frame (the headless Skia renderer draws for real).</summary>
    private static WriteableBitmap Frame(Window window)
    {
        TestShell.Pump();
        WriteableBitmap? frame = window.CaptureRenderedFrame();
        Assert.NotNull(frame);
        return frame;
    }

    /// <summary>The colour of one frame pixel (alpha dropped; the window is opaque).</summary>
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

    /// <summary>
    /// Some pixel within <paramref name="radius"/> of (x, y) is within <paramref name="tolerance"/> (per channel) of the
    /// expected colour. The neighbourhood absorbs sub-pixel placement and anti-aliasing of thin lines.
    /// </summary>
    private static void AssertColourNear(WriteableBitmap frame, double x, double y, Color expected, int tolerance, string what, int radius = 1)
    {
        int cx = (int)Math.Floor(x), cy = (int)Math.Floor(y);
        Color best = default;
        int bestD = int.MaxValue;
        for (int dy = -radius; dy <= radius; dy++)
            for (int dx = -radius; dx <= radius; dx++)
            {
                Color c = PixelAt(frame, cx + dx, cy + dy);
                int d = Distance(c, expected);
                if (d < bestD) { bestD = d; best = c; }
            }
        Assert.True(bestD <= tolerance, $"{what}: expected about {expected} near ({x:0.0}, {y:0.0}), closest was {best} (off by {bestD})");
    }

    private static Color C(UiColour c) => Color.FromArgb(c.A, c.R, c.G, c.B);

    private static ScreenPoint Lerp(ScreenPoint a, ScreenPoint b, double t) => new(a.X + (b.X - a.X) * t, a.Y + (b.Y - a.Y) * t);

    // ---------------------------------------------------------------------------------------------------------------
    // Pixels where the geometry says
    // ---------------------------------------------------------------------------------------------------------------

    [AvaloniaFact]
    public void Gauge_draws_the_needle_track_and_red_band_in_their_colours()
    {
        // Half scale: the needle points straight up from the hub, clear of the hub text below it.
        var gauge = new Gauge { Value = 50, RedFrom = 0.9, Caption = "GRID SPEED", Unit = "RPM" };
        Window w = Host(gauge, 200, 160);
        try
        {
            WriteableBitmap frame = Frame(w);
            GaugeScene s = GaugeGeometry.Compute(200, 160, 50, 0, 100, 1, 0.9, 0, 0, "")!;
            Assert.True(Math.Abs(s.NeedleTip.X - s.Centre.X) < 1e-9 && s.NeedleTip.Y < s.Centre.Y, "the half-scale needle points up");

            AssertColourNear(frame, s.Centre.X, (s.Centre.Y + s.NeedleTip.Y) / 2, C(GaugeGeometry.NeedlePen.Colour), 40, "needle");
            ScreenPoint onTrack = OnArc(s, 0.3);
            AssertColourNear(frame, onTrack.X, onTrack.Y, C(GaugeGeometry.Track.Colour), 30, "track at 30 percent");
            // The red band is drawn over the track at 0.9 opacity: strongly red, little green or blue.
            ScreenPoint onRed = OnArc(s, 0.95);
            Color red = PixelAt(frame, (int)onRed.X, (int)onRed.Y);
            Assert.True(red.R > 200 && red.G < 130 && red.B < 130, $"red band at 95 percent: got {red}");
            // Off the dial (a corner) is not the needle colour.
            Assert.True(Distance(PixelAt(frame, 2, 158), C(GaugeGeometry.NeedlePen.Colour)) > 60, "the corner is not painted");
        }
        finally { w.Close(); }
    }

    /// <summary>The point on the gauge's arc at a fraction of the scale.</summary>
    private static ScreenPoint OnArc(GaugeScene s, double frac)
    {
        double a = GaugeGeometry.Deg(frac) * Math.PI / 180;
        return new ScreenPoint(s.Centre.X + Math.Cos(a) * s.Radius, s.Centre.Y + Math.Sin(a) * s.Radius);
    }

    [AvaloniaFact]
    public void Sparkline_draws_the_trace_in_the_stroke_colour()
    {
        double[] values = Enumerable.Range(0, 11).Select(i => (double)i).ToArray();
        var spark = new Sparkline { Values = values };
        Window w = Host(spark, 200, 60);
        try
        {
            WriteableBitmap frame = Frame(w);
            SparklineScene s = SparklineGeometry.Compute(values, 200, 60)!;
            ScreenPoint mid = s.Points[5];
            AssertColourNear(frame, mid.X, mid.Y, C(SparklineGeometry.DefaultStroke), 60, "trace midpoint");

            // A caller-set stroke follows through.
            spark.Stroke = new SolidColorBrush(Color.Parse("#FF5B5B"));
            frame = Frame(w);
            AssertColourNear(frame, mid.X, mid.Y, Color.Parse("#FF5B5B"), 60, "red trace midpoint");
        }
        finally { w.Close(); }
    }

    private static readonly DateTime T0 = new(2026, 10, 20, 12, 0, 0, DateTimeKind.Utc);

    private static FrequencyResponse Resp(params (double S, double Hz)[] pts)
    {
        var points = pts.Select(p => new FrequencyPoint(T0.AddSeconds(p.S), p.Hz, 0.0)).ToList();
        double nadir = pts.Length == 0 ? double.NaN : pts.Min(p => p.Hz);
        return new FrequencyResponse(0.0, nadir, 0.0, pts.Length == 0 ? double.NaN : pts[^1].Hz, points);
    }

    [AvaloniaFact]
    public void FrequencyTrace_draws_the_background_and_the_baseline_in_their_colours()
    {
        // A flat 49.9 Hz baseline over 20 s: a horizontal grey line between the 50.0 and 49.8 limit lines.
        FrequencyResponse flat = Resp(Enumerable.Range(0, 21).Select(i => ((double)i, 49.9)).ToArray());
        var chart = new FrequencyTrace();
        chart.SetResponses(flat, null);
        Window w = Host(chart, 400, 220);
        try
        {
            WriteableBitmap frame = Frame(w);
            FrequencyTraceScene s = FrequencyTraceGeometry.Compute(400, 220, flat, null, null)!;
            ScreenPoint on = s.Map(7.5, 49.9);                       // between the 5 s and 10 s gridlines
            AssertColourNear(frame, on.X, on.Y, C(FrequencyTraceGeometry.BasePen.Colour), 40, "baseline at 7.5 s");
            AssertColourNear(frame, 5, 215, C(FrequencyTraceGeometry.Background.Colour), 4, "chart background", radius: 0);
            Assert.Same(flat, chart.Baseline);
            Assert.Null(chart.Tsgb);
            Assert.Null(chart.Bands);                                // null = GB
        }
        finally { w.Close(); }
    }

    [AvaloniaFact]
    public void Phasor_draws_the_voltage_reference_and_the_lagging_current_in_their_colours()
    {
        var phasor = new Phasor { P = 100, Q = 50 };                 // exporting VArs: lagging, amber
        Window w = Host(phasor, 160, 160);
        try
        {
            WriteableBitmap frame = Frame(w);
            PhasorScene s = PhasorGeometry.Compute(160, 160, 100, 50, 0)!;
            Assert.Equal(PhasorState.Lagging, s.State);
            ScreenPoint v = Lerp(s.Voltage.Tail, s.Voltage.Tip, 0.6);
            AssertColourNear(frame, v.X, v.Y, C(PhasorGeometry.VPen.Colour), 40, "voltage phasor");
            ScreenPoint i = Lerp(s.Current.Tail, s.Current.Tip, 0.6);
            AssertColourNear(frame, i.X, i.Y, C(PhasorGeometry.LagPen.Colour), 40, "lagging current phasor");
        }
        finally { w.Close(); }
    }

    [AvaloniaFact]
    public void PqDiagram_colours_the_operating_dot_by_stress()
    {
        var pq = new PqDiagram { Pg = 200, Qg = 0, Qmin = -150, Qmax = 200, Pmax = 400 };
        Window w = Host(pq, 200, 140);
        try
        {
            WriteableBitmap frame = Frame(w);
            PqDiagramScene s = PqDiagramGeometry.Compute(200, 140, 200, 0, -150, 200, 400)!;
            Assert.Equal(PqStress.Normal, s.Stress);
            AssertColourNear(frame, s.Dot.X, s.Dot.Y, C(PqDiagramGeometry.DotNormal.Colour), 30, "normal dot");

            pq.Pg = 399;                                             // 99.75 percent of the thermal limit: red
            frame = Frame(w);
            PqDiagramScene hot = PqDiagramGeometry.Compute(200, 140, 399, 0, -150, 200, 400)!;
            Assert.Equal(PqStress.AtLimit, hot.Stress);
            AssertColourNear(frame, hot.Dot.X, hot.Dot.Y, C(PqDiagramGeometry.DotAtLimit.Colour), 30, "at-limit dot");
        }
        finally { w.Close(); }
    }

    // ---------------------------------------------------------------------------------------------------------------
    // Edge inputs render without an exception
    // ---------------------------------------------------------------------------------------------------------------

    private static readonly (double W, double H)[] EdgeSizes = { (1, 1), (19.5, 19.5), (35, 30), (43, 100), (100, 35), (60, 50), (250, 200) };

    /// <summary>Render the control at every edge size; the render must not throw.</summary>
    private static void RendersEverywhere(Control control, string what)
    {
        foreach ((double cw, double ch) in EdgeSizes)
        {
            Window w = Host(control, cw, ch);
            try { Frame(w); }
            catch (Exception ex) when (ex is not Xunit.Sdk.XunitException)
            {
                Assert.Fail($"{what} at {cw} x {ch} threw {ex.GetType().Name}: {ex.Message}");
            }
            finally
            {
                w.Content = null;   // free the control for the next host
                w.Close();
            }
        }
    }

    [AvaloniaFact]
    public void Gauge_renders_edge_inputs_without_an_exception()
    {
        double[] odd = { double.NaN, double.PositiveInfinity, double.NegativeInfinity, -1e308, 1e308, 0 };
        RendersEverywhere(new Gauge(), "default gauge");
        foreach (double v in odd)
        {
            RendersEverywhere(new Gauge { Value = v }, $"gauge value {v}");
            RendersEverywhere(new Gauge { Value = 1, Minimum = v, Maximum = v }, $"gauge range {v}..{v}");
            RendersEverywhere(new Gauge { AmberFrom = v, RedFrom = v, AmberTo = v, RedTo = v }, $"gauge bands {v}");
        }
        RendersEverywhere(new Gauge { Value = 5, Minimum = 10, Maximum = 0 }, "inverted range");
        RendersEverywhere(new Gauge { Value = 5, ValueText = "OFF", Unit = " degC", Caption = "WINDING" }, "texts");
        var nulls = new Gauge();
        nulls.SetValue(Gauge.UnitProperty, null!);
        nulls.SetValue(Gauge.CaptionProperty, null!);
        nulls.SetValue(Gauge.ValueTextProperty, null!);
        RendersEverywhere(nulls, "null texts");
    }

    [AvaloniaFact]
    public void Sparkline_renders_edge_inputs_without_an_exception()
    {
        IReadOnlyList<double>?[] series =
        {
            null, Array.Empty<double>(), new[] { 7.0 }, new[] { 5.0, 5.0, 5.0 }, new[] { 1.0, double.NaN, 3.0 },
            new[] { double.NaN, double.NaN }, new[] { 1.0, double.PositiveInfinity }, new[] { double.NegativeInfinity, 0.0 },
            new[] { -1e308, 1e308 },
        };
        foreach (IReadOnlyList<double>? v in series)
            foreach (bool minMax in new[] { false, true })
                RendersEverywhere(new Sparkline { Values = v, ShowMinMax = minMax }, $"sparkline [{(v is null ? "null" : string.Join(",", v))}]");
        RendersEverywhere(new Sparkline { Values = new[] { 1.0, 2.0 }, Caption = null }, "no caption");
        RendersEverywhere(new Sparkline { Values = new[] { 1.0, 2.0 }, Stroke = new LinearGradientBrush() }, "gradient stroke");
    }

    [AvaloniaFact]
    public void FrequencyTrace_renders_edge_inputs_without_an_exception()
    {
        FrequencyResponse?[] responses =
        {
            null, Resp(), Resp((0, 50)), Resp((0, 50), (0, 49.5)), Resp((0, 50), (1, double.NaN), (2, 49.8)),
            Resp((0, double.PositiveInfinity), (1, double.NegativeInfinity)), Resp((0, 50), (1, 48.0), (2, 51.0)),
            Resp((5, 49.9), (1, 49.8), (9, 49.7)),
        };
        foreach (FrequencyResponse? b in responses)
        {
            var chart = new FrequencyTrace();
            chart.SetResponses(b, b, FrequencyBands.ForNominal(60.0));
            RendersEverywhere(chart, "frequency chart");
            RendersEverywhere(new FrequencyTrace { Baseline = b }, "frequency chart, baseline only");
        }
    }

    [AvaloniaFact]
    public void Phasor_renders_edge_inputs_without_an_exception()
    {
        double[] odd = { double.NaN, double.PositiveInfinity, double.NegativeInfinity, 0, -0.0, 1e-7, -1e-7 };
        foreach (double p in odd)
            foreach (double q in odd)
                RendersEverywhere(new Phasor { P = p, Q = q, SMax = p }, $"phasor P={p} Q={q}");
        RendersEverywhere(new Phasor { P = 300, Q = -120, ShowPowerFactor = false }, "no power factor");
    }

    [AvaloniaFact]
    public void PqDiagram_renders_edge_inputs_without_an_exception()
    {
        double[] odd = { double.NaN, double.PositiveInfinity, double.NegativeInfinity, 0, 1e308 };
        foreach (double v in odd)
        {
            RendersEverywhere(new PqDiagram { Pg = v, Qg = v }, $"pq operating point {v}");
            RendersEverywhere(new PqDiagram { Pg = 5, Qg = 5, Qmin = v, Qmax = v, Pmax = v }, $"pq limits {v}");
        }
        // Between 20 and 44 pixels wide the WPF diagram threw (a negative plot box); the Avalonia one skips the box.
        RendersEverywhere(new PqDiagram { Pg = 300, Qg = 50, Qmin = -150, Qmax = 200, Pmax = 400 }, "normal");
    }

    // ---------------------------------------------------------------------------------------------------------------
    // The WPF properties, with the WPF defaults, and they bind
    // ---------------------------------------------------------------------------------------------------------------

    [AvaloniaFact]
    public void The_controls_have_the_WPF_defaults()
    {
        var g = new Gauge();
        Assert.Equal((0.0, 0.0, 100.0, 1.0, 1.0, 0.0, 0.0), (g.Value, g.Minimum, g.Maximum, g.AmberFrom, g.RedFrom, g.AmberTo, g.RedTo));
        Assert.Equal(("", "", ""), (g.Unit, g.Caption, g.ValueText));
        var s = new Sparkline();
        Assert.Null(s.Values);
        Assert.False(s.ShowMinMax);
        Assert.Equal("SIMULATED", s.Caption);
        Assert.Equal(C(SparklineGeometry.DefaultStroke), Assert.IsAssignableFrom<ISolidColorBrush>(s.Stroke).Color);
        var f = new FrequencyTrace();
        Assert.Null(f.Baseline);
        Assert.Null(f.Tsgb);
        Assert.Null(f.Bands);
        var p = new Phasor();
        Assert.Equal((0.0, 0.0, 0.0, true), (p.P, p.Q, p.SMax, p.ShowPowerFactor));
        var q = new PqDiagram();
        Assert.Equal((0.0, 0.0, -1.0, 1.0, 1.0), (q.Pg, q.Qg, q.Qmin, q.Qmax, q.Pmax));
    }

    /// <summary>A binding source with one property of any type.</summary>
    private sealed class Holder : INotifyPropertyChanged
    {
        private object? _item;
        public event PropertyChangedEventHandler? PropertyChanged;
        public object? Item
        {
            get => _item;
            set { _item = value; PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Item))); }
        }
    }

    /// <summary>Bind the property to a source, change the source, and the control's property follows.</summary>
    private static void Binds(AvaloniaObject control, AvaloniaProperty property, object? first, object? second)
    {
        var holder = new Holder { Item = first };
        using IDisposable binding = control.Bind(property, new Binding(nameof(Holder.Item)) { Source = holder, Mode = BindingMode.OneWay });
        TestShell.Pump();
        Assert.Equal(first, control.GetValue(property));
        holder.Item = second;
        TestShell.Pump();
        Assert.Equal(second, control.GetValue(property));
    }

    [AvaloniaFact]
    public void Every_WPF_property_is_a_styled_property_that_binds()
    {
        var g = new Gauge();
        Binds(g, Gauge.ValueProperty, 12.5, 75.0);
        Binds(g, Gauge.MinimumProperty, -10.0, 5.0);
        Binds(g, Gauge.MaximumProperty, 200.0, 300.0);
        Binds(g, Gauge.AmberFromProperty, 0.7, 0.6);
        Binds(g, Gauge.RedFromProperty, 0.9, 0.8);
        Binds(g, Gauge.AmberToProperty, 0.3, 0.2);
        Binds(g, Gauge.RedToProperty, 0.1, 0.05);
        Binds(g, Gauge.UnitProperty, "RPM", "MNm");
        Binds(g, Gauge.CaptionProperty, "GRID SPEED", "TORQUE");
        Binds(g, Gauge.ValueTextProperty, "OFF", "TRIP");

        var s = new Sparkline();
        Binds(s, Sparkline.ValuesProperty, new[] { 1.0, 2.0 }, new[] { 3.0, 4.0, 5.0 });
        Binds(s, Sparkline.StrokeProperty, Brushes.Red, Brushes.Green);
        Binds(s, Sparkline.ShowMinMaxProperty, true, false);
        Binds(s, Sparkline.CaptionProperty, "REPLAY", "");

        var f = new FrequencyTrace();
        FrequencyResponse a = Resp((0, 50), (1, 49.9)), b = Resp((0, 50), (2, 49.7));
        Binds(f, FrequencyTrace.BaselineProperty, a, b);
        Binds(f, FrequencyTrace.TsgbProperty, b, a);
        Binds(f, FrequencyTrace.BandsProperty, FrequencyBands.Gb, FrequencyBands.ForNominal(60.0));

        var p = new Phasor();
        Binds(p, Phasor.PProperty, 100.0, -50.0);
        Binds(p, Phasor.QProperty, 30.0, -30.0);
        Binds(p, Phasor.SMaxProperty, 150.0, 0.0);
        Binds(p, Phasor.ShowPowerFactorProperty, false, true);

        var q = new PqDiagram();
        Binds(q, PqDiagram.PgProperty, 200.0, 399.0);
        Binds(q, PqDiagram.QgProperty, 10.0, -140.0);
        Binds(q, PqDiagram.QminProperty, -150.0, -100.0);
        Binds(q, PqDiagram.QmaxProperty, 200.0, 250.0);
        Binds(q, PqDiagram.PmaxProperty, 400.0, 500.0);
    }

    [AvaloniaFact]
    public void A_bound_value_change_redraws_the_control()
    {
        // The needle moves when the bound value changes: half scale points up, a quarter points up-left.
        var gauge = new Gauge();
        var holder = new Holder { Item = 50.0 };
        using IDisposable binding = gauge.Bind(Gauge.ValueProperty, new Binding(nameof(Holder.Item)) { Source = holder });
        Window w = Host(gauge, 200, 160);
        try
        {
            GaugeScene half = GaugeGeometry.Compute(200, 160, 50, 0, 100, 1, 1, 0, 0, "")!;
            ScreenPoint upMid = Lerp(half.Centre, half.NeedleTip, 0.6);
            AssertColourNear(Frame(w), upMid.X, upMid.Y, C(GaugeGeometry.NeedlePen.Colour), 40, "needle at half scale");

            holder.Item = 25.0;
            GaugeScene quarter = GaugeGeometry.Compute(200, 160, 25, 0, 100, 1, 1, 0, 0, "")!;
            ScreenPoint q = Lerp(quarter.Centre, quarter.NeedleTip, 0.6);
            WriteableBitmap after = Frame(w);
            AssertColourNear(after, q.X, q.Y, C(GaugeGeometry.NeedlePen.Colour), 40, "needle at quarter scale");
            Assert.True(Distance(PixelAt(after, (int)upMid.X, (int)upMid.Y), C(GaugeGeometry.NeedlePen.Colour)) > 60,
                "the old needle position was repainted");
        }
        finally { w.Close(); }
    }

    // ---------------------------------------------------------------------------------------------------------------
    // The instrument colours that are theme colours are the theme's
    // ---------------------------------------------------------------------------------------------------------------

    private static Color ThemeColour(string key)
    {
        Assert.True(Application.Current!.TryGetResource(key, ThemeVariant.Dark, out object? value), $"theme colour {key} missing");
        return Assert.IsType<Color>(value);
    }

    [AvaloniaFact]
    public void The_instrument_colours_that_are_theme_colours_match_the_ported_theme()
    {
        Assert.Equal(ThemeColour("AccentColor"), C(GaugeGeometry.NeedlePen.Colour));
        Assert.Equal(ThemeColour("AccentColor"), C(SparklineGeometry.DefaultStroke));
        Assert.Equal(ThemeColour("AccentColor"), C(PhasorGeometry.VPen.Colour));
        Assert.Equal(ThemeColour("AccentColor"), C(PqDiagramGeometry.DotNormal.Colour));
        Assert.Equal(ThemeColour("TextColor"), C(GaugeGeometry.Hub.Colour));
        Assert.Equal(ThemeColour("TextColor"), C(PhasorGeometry.Text.Colour));
        Assert.Equal(ThemeColour("MutedColor"), C(GaugeGeometry.Muted.Colour));
        Assert.Equal(ThemeColour("MutedColor"), C(PqDiagramGeometry.Muted.Colour));
        Assert.Equal(ThemeColour("AmberColor"), C(GaugeGeometry.AmberArc.Colour));
        Assert.Equal(ThemeColour("AmberColor"), C(PhasorGeometry.LagPen.Colour));
        Assert.Equal(ThemeColour("RedColor"), C(GaugeGeometry.RedArc.Colour));
        Assert.Equal(ThemeColour("RedColor"), C(PqDiagramGeometry.DotAtLimit.Colour));
    }
}
