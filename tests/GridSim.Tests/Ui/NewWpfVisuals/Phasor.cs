// TEST COPY (UI programme 1.3): src/GridSim.Wpf/Visuals/Phasor.cs as it is now, drawing from src/GridSim.Ui/Visuals. Only the
// TEST COPY using and namespace lines differ (the WPF types are the recording stand-ins in UiVisualsWpfShim.cs);
// TEST COPY UiVisualsParityTests checks the rest equals the real file, line for line.
using System.Globalization;
using GridSim.Tests.Ui.WpfShim;
using GridSim.Ui.Schematic;
using GridSim.Ui.Visuals;

namespace GridSim.Tests.Ui.NewWpfVisuals;

/// <summary>
/// A machine's operating point drawn as a phasor disc: a unit voltage reference at 0 deg
/// and the current phasor swung off it by the power-factor angle phi = atan2(Q, P).
/// A generator exporting VArs (Q &gt; 0) runs <i>lagging</i>  -  current lags voltage, the
/// phasor drops below the axis (amber); absorbing VArs (Q &lt; 0) it runs <i>leading</i>,
/// the phasor rides above (cyan). The phi arc, its angle, the lead/lag label and the
/// power factor cos phi are all printed on the face. One drawing pass, no template  -  the
/// instrument sibling of <see cref="Gauge"/> and <see cref="Sparkline"/>.
/// UI programme Phase 1.3: the disc's arithmetic (radius, centre, phi, the power factor, the vector and arrowhead ends,
/// the phi arc, the label texts and positions) moved to the platform-neutral <see cref="PhasorGeometry"/>
/// (src/GridSim.Ui/Visuals), operation for operation, so the Avalonia phasor draws from the same numbers; this control
/// now only draws them and its pixels are unchanged (UiVisualsParityTests pins the moved maths against a verbatim copy
/// of the old code, bit for bit).
/// </summary>
public sealed class Phasor : FrameworkElement
{
    public static readonly DependencyProperty PProperty = Dp(nameof(P), 0.0);
    public static readonly DependencyProperty QProperty = Dp(nameof(Q), 0.0);
    /// <summary>Apparent-power scale for the current vector's length; 0 = auto from |S|.</summary>
    public static readonly DependencyProperty SMaxProperty = Dp(nameof(SMax), 0.0);
    public static readonly DependencyProperty ShowPowerFactorProperty = DependencyProperty.Register(
        nameof(ShowPowerFactor), typeof(bool), typeof(Phasor),
        new FrameworkPropertyMetadata(true, FrameworkPropertyMetadataOptions.AffectsRender));

    private static DependencyProperty Dp(string name, double dflt) => DependencyProperty.Register(
        name, typeof(double), typeof(Phasor),
        new FrameworkPropertyMetadata(dflt, FrameworkPropertyMetadataOptions.AffectsRender));

    /// <summary>Real power, MW (active component along the reference axis).</summary>
    public double P { get => (double)GetValue(PProperty); set => SetValue(PProperty, value); }
    /// <summary>Reactive power, MVAr  -  signed: positive exports VArs (lagging), negative absorbs (leading).</summary>
    public double Q { get => (double)GetValue(QProperty); set => SetValue(QProperty, value); }
    /// <summary>Apparent-power scale for the current vector; leave 0 to auto-scale to |S|.</summary>
    public double SMax { get => (double)GetValue(SMaxProperty); set => SetValue(SMaxProperty, value); }
    public bool ShowPowerFactor { get => (bool)GetValue(ShowPowerFactorProperty); set => SetValue(ShowPowerFactorProperty, value); }

    private static readonly Pen DiscPen = MakePen("#2A3742", 1.4);
    private static readonly Pen AxisPen = MakePen("#46535F", 1.4);
    private static readonly Pen VPen = MakePen("#21C7E6", 3);          // voltage reference (accent)
    private static readonly Pen LagPen = MakePen("#F3C13A", 3);        // current, lagging (amber)
    private static readonly Pen LeadPen = MakePen("#21C7E6", 3, 0.95); // current, leading (cyan)
    private static readonly Pen ArcPen = MakePen("#8492A0", 1.6);
    private static readonly Brush TextBrush = Palette.Frozen(Palette.FromHex("#E6EDF3"));
    private static readonly Brush MutedBrush = Palette.Frozen(Palette.FromHex("#8492A0"));
    private static readonly Brush AmberBrush = Palette.Frozen(Palette.FromHex("#F3C13A"));
    private static readonly Brush CyanBrush = Palette.Frozen(Palette.FromHex("#21C7E6"));
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
        // The whole disc worked out by the shared geometry (null = too small to draw, as before: under 40 x 40).
        PhasorScene? s = PhasorGeometry.Compute(ActualWidth, ActualHeight, P, Q, SMax);
        if (s is null) return;
        double r = s.Radius;
        Point c = Pt(s.Centre);

        // the disc and the +/- reference axis through it
        dc.DrawEllipse(null, DiscPen, c, r, r);
        dc.DrawLine(AxisPen, new Point(c.X - r, c.Y), new Point(c.X + r, c.Y));
        dc.DrawLine(AxisPen, new Point(c.X, c.Y - r), new Point(c.X, c.Y + r));

        bool lagging = s.State == PhasorState.Lagging, leading = s.State == PhasorState.Leading;

        // voltage reference phasor: unit length along +x (0 deg)
        DrawVector(dc, VPen, s.Voltage);
        DrawText(dc, "V", SmallFace, 11, CyanBrush, Pt(s.VoltageLabelAt), pps: Pps(), align: TextAlignment.Left);

        // current phasor: electrical -phi, screen +phi (PhasorGeometry), so lagging sits below the axis
        Pen iPen = leading ? LeadPen : lagging ? LagPen : VPen;
        DrawVector(dc, iPen, s.Current);
        DrawText(dc, "I", SmallFace, 11, leading ? CyanBrush : lagging ? AmberBrush : TextBrush,
            Pt(s.CurrentLabelAt), Pps(), TextAlignment.Left);

        // the phi arc between V (0 deg) and I (screen +phi), small radius near the origin
        if (s.PhiArc is VisualArc arc)
            DrawArc(dc, arc);

        // labels: angle + lead/lag state at top, power factor at the bottom
        double pps = Pps();
        Brush stateBrush = leading ? CyanBrush : lagging ? AmberBrush : MutedBrush;

        DrawText(dc, s.AngleLabel, SmallFace, 11, MutedBrush, Pt(s.AngleAt), pps);
        DrawText(dc, s.StateLabel, SmallFace, 10.5, stateBrush, Pt(s.StateAt), pps);
        if (ShowPowerFactor)
            DrawText(dc, s.PowerFactorLabel, Face, 15, TextBrush, Pt(s.PowerFactorAt), pps);
    }

    private double Pps() => VisualTreeHelper.GetDpi(this).PixelsPerDip;

    /// <summary>The WPF point for a neutral screen point: the same two doubles.</summary>
    private static Point Pt(ScreenPoint p) => new(p.X, p.Y);

    /// <summary>Draws a vector from the centre (screen frame), with its arrowhead: the shaft, then the two barbs.</summary>
    private static void DrawVector(DrawingContext dc, Pen pen, PhasorArrow a)
    {
        Point tip = Pt(a.Tip);
        dc.DrawLine(pen, Pt(a.Tail), tip);
        dc.DrawLine(pen, tip, Pt(a.Barb1));
        dc.DrawLine(pen, tip, Pt(a.Barb2));
    }

    /// <summary>Sweeps the small phi arc the geometry worked out.</summary>
    private static void DrawArc(DrawingContext dc, VisualArc a)
    {
        var geo = new StreamGeometry();
        using (StreamGeometryContext g = geo.Open())
        {
            g.BeginFigure(Pt(a.Start), false, false);
            // a1 < a0 (lagging draws clockwise on screen -> counter-clockwise sweep flag)
            g.ArcTo(Pt(a.End), new Size(a.Radius, a.Radius), 0, a.IsLargeArc,
                a.Clockwise ? SweepDirection.Clockwise : SweepDirection.Counterclockwise, true, false);
        }
        geo.Freeze();
        dc.DrawGeometry(null, ArcPen, geo);
    }

    private static void DrawText(DrawingContext dc, string text, Typeface face, double size, Brush brush,
        Point at, double pps, TextAlignment align = TextAlignment.Center)
    {
        var ft = new FormattedText(text, CultureInfo.InvariantCulture, FlowDirection.LeftToRight, face, size, brush, pps)
        { TextAlignment = align, MaxTextWidth = 400 };
        double x = align == TextAlignment.Center ? at.X - 200 : at.X;
        dc.DrawText(ft, new Point(x, at.Y));
    }
}
