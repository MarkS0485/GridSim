using System.Globalization;
using System.Windows;
using System.Windows.Media;
using GridSim.Ui.Schematic;
using GridSim.Ui.Visuals;

namespace GridSim.Wpf.Visuals;

/// <summary>
/// A control-room gauge dial: a 240 deg arc with tick marks, optional amber/red zones,
/// a needle, and the value printed in the hub. One drawing pass, no template  -  the
/// instrument equivalent of <see cref="Sparkline"/>. Used anywhere a number has a
/// meaningful range: RPM, torque, temperatures, loading, stress.
/// UI programme Phase 1.3: the dial's arithmetic (radius, centre, band fractions, arc ends, ticks, needle, label text and
/// positions) moved to the platform-neutral <see cref="GaugeGeometry"/> (src/GridSim.Ui/Visuals), operation for operation,
/// so the Avalonia gauge draws from the same numbers. This control now only draws them; its pixels are unchanged
/// (UiVisualsParityTests pins the moved maths against a verbatim copy of the old code, bit for bit).
/// </summary>
public sealed class Gauge : FrameworkElement
{
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
        // The whole dial worked out by the shared geometry (null = too small to draw, as before: under 30 x 30).
        GaugeScene? s = GaugeGeometry.Compute(ActualWidth, ActualHeight, Value, Minimum, Maximum,
            AmberFrom, RedFrom, AmberTo, RedTo, ValueText);
        if (s is null) return;
        Point c = P(s.Centre);

        // arc track + warning bands (high side toward full scale, low side from zero), in the geometry's order
        foreach (GaugeBand band in s.Bands)
            if (band.Arc is VisualArc arc)
                DrawArc(dc, band.Kind == GaugeBandKind.Track ? Track : band.Kind == GaugeBandKind.Amber ? AmberArc : RedArc, arc);

        // ticks (9 across the sweep)
        foreach (VisualLine t in s.Ticks) dc.DrawLine(TickPen, P(t.From), P(t.To));

        // needle + hub
        dc.DrawLine(NeedlePen, c, P(s.NeedleTip));
        dc.DrawEllipse(HubBrush, null, c, 4, 4);

        // text: caption above, value + unit in/under the hub
        double pps = VisualTreeHelper.GetDpi(this).PixelsPerDip;
        if (Caption.Length > 0)
            DrawText(dc, Caption, SmallFace, 9.5, MutedBrush, P(s.CaptionAt), pps);
        DrawText(dc, s.ValueLabel, Face, s.ValueFontSize, HubBrush, P(s.ValueAt), pps);
        if (Unit.Length > 0)
            DrawText(dc, Unit, SmallFace, 10, MutedBrush, P(s.UnitAt), pps);
    }

    /// <summary>The WPF point for a neutral screen point: the same two doubles.</summary>
    private static Point P(ScreenPoint p) => new(p.X, p.Y);

    private static void DrawArc(DrawingContext dc, Pen pen, VisualArc a)
    {
        var geo = new StreamGeometry();
        using (StreamGeometryContext g = geo.Open())
        {
            g.BeginFigure(P(a.Start), false, false);
            g.ArcTo(P(a.End), new Size(a.Radius, a.Radius), 0, a.IsLargeArc,
                a.Clockwise ? SweepDirection.Clockwise : SweepDirection.Counterclockwise, true, false);
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
