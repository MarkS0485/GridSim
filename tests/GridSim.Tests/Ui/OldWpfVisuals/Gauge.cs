// TEST ORACLE (UI programme 1.3): src/GridSim.Wpf/Visuals/Gauge.cs exactly as it was at commit f3559445, before its maths
// TEST ORACLE moved to src/GridSim.Ui/Visuals. Only the using and namespace lines differ (the WPF types are the recording
// TEST ORACLE stand-ins in UiVisualsWpfShim.cs); UiVisualsParityTests checks a SHA-256 of the rest against the original file.
using System.Globalization;
using GridSim.Tests.Ui.WpfShim;

namespace GridSim.Tests.Ui.OldWpfVisuals;

/// <summary>
/// A control-room gauge dial: a 240 deg arc with tick marks, optional amber/red zones,
/// a needle, and the value printed in the hub. One drawing pass, no template  -  the
/// instrument equivalent of <see cref="Sparkline"/>. Used anywhere a number has a
/// meaningful range: RPM, torque, temperatures, loading, stress.
/// </summary>
public sealed class Gauge : FrameworkElement
{
    private const double StartDeg = -210;   // 240 deg sweep, gap at the bottom
    private const double SweepDeg = 240;

    public static readonly DependencyProperty ValueProperty = Dp(nameof(Value), 0.0);
    public static readonly DependencyProperty MinimumProperty = Dp(nameof(Minimum), 0.0);
    public static readonly DependencyProperty MaximumProperty = Dp(nameof(Maximum), 100.0);
    /// <summary>Fraction of range (0..1) where the amber band starts; 1 = no band.</summary>
    public static readonly DependencyProperty AmberFromProperty = Dp(nameof(AmberFrom), 1.0);
    /// <summary>Fraction of range (0..1) where the red band starts; 1 = no band.</summary>
    public static readonly DependencyProperty RedFromProperty = Dp(nameof(RedFrom), 1.0);
    /// <summary>Low-side bands, for symmetric instruments (grid speed): amber from 0 up to this fraction; 0 = none.</summary>
    public static readonly DependencyProperty AmberToProperty = Dp(nameof(AmberTo), 0.0);
    /// <summary>Low-side red band, from 0 up to this fraction; 0 = none.</summary>
    public static readonly DependencyProperty RedToProperty = Dp(nameof(RedTo), 0.0);
    public static readonly DependencyProperty UnitProperty = DpObj(nameof(Unit), "");
    public static readonly DependencyProperty CaptionProperty = DpObj(nameof(Caption), "");
    public static readonly DependencyProperty ValueTextProperty = DpObj(nameof(ValueText), "");

    private static DependencyProperty Dp(string name, double dflt) => DependencyProperty.Register(
        name, typeof(double), typeof(Gauge),
        new FrameworkPropertyMetadata(dflt, FrameworkPropertyMetadataOptions.AffectsRender));

    private static DependencyProperty DpObj(string name, string dflt) => DependencyProperty.Register(
        name, typeof(string), typeof(Gauge),
        new FrameworkPropertyMetadata(dflt, FrameworkPropertyMetadataOptions.AffectsRender));

    public double Value { get => (double)GetValue(ValueProperty); set => SetValue(ValueProperty, value); }
    public double Minimum { get => (double)GetValue(MinimumProperty); set => SetValue(MinimumProperty, value); }
    public double Maximum { get => (double)GetValue(MaximumProperty); set => SetValue(MaximumProperty, value); }
    public double AmberFrom { get => (double)GetValue(AmberFromProperty); set => SetValue(AmberFromProperty, value); }
    public double RedFrom { get => (double)GetValue(RedFromProperty); set => SetValue(RedFromProperty, value); }
    public double AmberTo { get => (double)GetValue(AmberToProperty); set => SetValue(AmberToProperty, value); }
    public double RedTo { get => (double)GetValue(RedToProperty); set => SetValue(RedToProperty, value); }
    /// <summary>Unit line under the value (e.g. "RPM", " degC", "MNm").</summary>
    public string Unit { get => (string)GetValue(UnitProperty); set => SetValue(UnitProperty, value); }
    /// <summary>Small caption above the dial (e.g. "GRID SPEED").</summary>
    public string Caption { get => (string)GetValue(CaptionProperty); set => SetValue(CaptionProperty, value); }
    /// <summary>Override for the hub text; empty = the numeric value, auto-formatted.</summary>
    public string ValueText { get => (string)GetValue(ValueTextProperty); set => SetValue(ValueTextProperty, value); }

    private static readonly Pen Track = MakePen("#2A3742", 7);
    private static readonly Pen AmberArc = MakePen("#F3C13A", 7, 0.85);
    private static readonly Pen RedArc = MakePen("#FF5B5B", 7, 0.9);
    private static readonly Pen TickPen = MakePen("#46535F", 1.6);
    private static readonly Pen NeedlePen = MakePen("#21C7E6", 3);
    private static readonly Brush HubBrush = Palette.Frozen(Palette.FromHex("#E6EDF3"));
    private static readonly Brush MutedBrush = Palette.Frozen(Palette.FromHex("#8492A0"));
    private static readonly Typeface Face =
        new(new FontFamily("Bahnschrift, Segoe UI"), FontStyles.Normal, FontWeights.Bold, FontStretches.Normal);
    private static readonly Typeface SmallFace =
        new(new FontFamily("Bahnschrift, Segoe UI"), FontStyles.Normal, FontWeights.SemiBold, FontStretches.Normal);

    private static Pen MakePen(string hex, double w, double opacity = 1.0)
    {
        Brush b = new SolidColorBrush(Palette.FromHex(hex)) { Opacity = opacity };
        b.Freeze();
        var p = new Pen(b, w) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round };
        p.Freeze();
        return p;
    }

    protected override void OnRender(DrawingContext dc)
    {
        double w = ActualWidth, h = ActualHeight;
        if (w < 30 || h < 30) return;

        double r = Math.Min(w, h * 1.16) / 2 - 10;
        Point c = new(w / 2, h / 2 + r * 0.12);

        // arc track + warning bands (high side toward full scale, low side from zero)
        DrawArc(dc, Track, c, r, 0, 1);
        if (AmberFrom < 1) DrawArc(dc, AmberArc, c, r, Math.Clamp(AmberFrom, 0, 1), Math.Clamp(RedFrom, AmberFrom, 1));
        if (RedFrom < 1) DrawArc(dc, RedArc, c, r, Math.Clamp(RedFrom, 0, 1), 1);
        if (AmberTo > 0) DrawArc(dc, AmberArc, c, r, Math.Clamp(RedTo, 0, AmberTo), Math.Clamp(AmberTo, 0, 1));
        if (RedTo > 0) DrawArc(dc, RedArc, c, r, 0, Math.Clamp(RedTo, 0, 1));

        // ticks (9 across the sweep)
        for (int i = 0; i <= 8; i++)
        {
            double a = Deg(i / 8.0) * Math.PI / 180;
            Point t0 = new(c.X + Math.Cos(a) * (r - 7), c.Y + Math.Sin(a) * (r - 7));
            Point t1 = new(c.X + Math.Cos(a) * (r - 13), c.Y + Math.Sin(a) * (r - 13));
            dc.DrawLine(TickPen, t0, t1);
        }

        // needle + hub
        double range = Math.Max(1e-9, Maximum - Minimum);
        double frac = Math.Clamp((Value - Minimum) / range, 0, 1);
        double na = Deg(frac) * Math.PI / 180;
        dc.DrawLine(NeedlePen, c, new Point(c.X + Math.Cos(na) * (r - 16), c.Y + Math.Sin(na) * (r - 16)));
        dc.DrawEllipse(HubBrush, null, c, 4, 4);

        // text: caption above, value + unit in/under the hub
        double pps = VisualTreeHelper.GetDpi(this).PixelsPerDip;
        if (Caption.Length > 0)
            DrawText(dc, Caption, SmallFace, 9.5, MutedBrush, new Point(c.X, c.Y - r - 4), pps);
        string val = ValueText.Length > 0 ? ValueText : Value.ToString("0.##", CultureInfo.InvariantCulture);
        DrawText(dc, val, Face, Math.Max(13, r * 0.34), HubBrush, new Point(c.X, c.Y + r * 0.30), pps);
        if (Unit.Length > 0)
            DrawText(dc, Unit, SmallFace, 10, MutedBrush, new Point(c.X, c.Y + r * 0.30 + Math.Max(13, r * 0.34) + 2), pps);
    }

    private static double Deg(double frac) => StartDeg + frac * SweepDeg;

    private static void DrawArc(DrawingContext dc, Pen pen, Point c, double r, double f0, double f1)
    {
        if (f1 - f0 < 1e-4) return;
        double a0 = Deg(f0) * Math.PI / 180, a1 = Deg(f1) * Math.PI / 180;
        var geo = new StreamGeometry();
        using (StreamGeometryContext g = geo.Open())
        {
            g.BeginFigure(new Point(c.X + Math.Cos(a0) * r, c.Y + Math.Sin(a0) * r), false, false);
            g.ArcTo(new Point(c.X + Math.Cos(a1) * r, c.Y + Math.Sin(a1) * r),
                new Size(r, r), 0, (f1 - f0) * SweepDeg > 180, SweepDirection.Clockwise, true, false);
        }
        geo.Freeze();
        dc.DrawGeometry(null, pen, geo);
    }

    private static void DrawText(DrawingContext dc, string text, Typeface face, double size, Brush brush, Point centreTop, double pps)
    {
        var ft = new FormattedText(text, CultureInfo.InvariantCulture, FlowDirection.LeftToRight, face, size, brush, pps)
        { TextAlignment = TextAlignment.Center, MaxTextWidth = 400 };
        dc.DrawText(ft, new Point(centreTop.X - 200, centreTop.Y));
    }
}
