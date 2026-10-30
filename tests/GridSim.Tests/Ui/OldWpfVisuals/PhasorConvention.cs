// TEST ORACLE (2026-10-24): OldWpfVisuals/Phasor.cs (the original WPF control at commit f3559445) with ONE change, the
// TEST ORACLE phasor convention: the current is drawn at screen angle +phi (electrical -phi), so a lagging current sits
// TEST ORACLE below the axis as the original's own comments say; the original drew it at screen -phi, above the axis.
// TEST ORACLE UiVisualsParityTests pins that this file differs from the original in exactly the lines that change.
using System.Globalization;
using GridSim.Tests.Ui.WpfShim;

namespace GridSim.Tests.Ui.OldWpfVisuals;

/// <summary>
/// A machine's operating point drawn as a phasor disc: a unit voltage reference at 0 deg
/// and the current phasor swung off it by the power-factor angle phi = atan2(Q, P).
/// A generator exporting VArs (Q &gt; 0) runs <i>lagging</i>  -  current lags voltage, the
/// phasor drops below the axis (amber); absorbing VArs (Q &lt; 0) it runs <i>leading</i>,
/// the phasor rides above (cyan). The phi arc, its angle, the lead/lag label and the
/// power factor cos phi are all printed on the face. One drawing pass, no template  -  the
/// instrument sibling of <see cref="Gauge"/> and <see cref="Sparkline"/>.
/// </summary>
public sealed class PhasorConvention : FrameworkElement
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
        double w = ActualWidth, h = ActualHeight;
        if (w < 40 || h < 40) return;

        double r = Math.Min(w, h) / 2 - 14;
        Point c = new(w / 2, h / 2);

        // the disc and the +/- reference axis through it
        dc.DrawEllipse(null, DiscPen, c, r, r);
        dc.DrawLine(AxisPen, new Point(c.X - r, c.Y), new Point(c.X + r, c.Y));
        dc.DrawLine(AxisPen, new Point(c.X, c.Y - r), new Point(c.X, c.Y + r));

        // phi = atan2(Q, P). Screen y grows downward, so a lagging machine (Q>0) must
        // draw below the axis: negate the angle when mapping to the canvas.
        double p = P, q = Q;
        double s = Math.Sqrt(p * p + q * q);
        double phi = Math.Atan2(q, p);                 // electrical angle, +ve = lagging
        bool lagging = q > 1e-6, leading = q < -1e-6;
        double pf = s > 1e-6 ? Math.Abs(p) / s : 1.0;   // power factor cos phi

        // current vector length: scale |S| against SMax (or its own magnitude) onto the disc
        double scale = SMax > 1e-6 ? SMax : Math.Max(s, 1e-6);
        double iLen = Math.Clamp(s / scale, 0.12, 1.0) * r;

        // voltage reference phasor: unit length along +x (0 deg)
        DrawVector(dc, c, VPen, 0, r * 0.92);
        DrawText(dc, "V", SmallFace, 11, CyanBrush,
            new Point(c.X + r * 0.92 + 2, c.Y - 8), pps: Pps(), align: TextAlignment.Left);

        // current phasor: screen angle is -phi so lagging sits below the axis
        Pen iPen = leading ? LeadPen : lagging ? LagPen : VPen;
        DrawVector(dc, c, iPen, phi, iLen);
        double ix = c.X + Math.Cos(phi) * iLen, iy = c.Y + Math.Sin(phi) * iLen;
        DrawText(dc, "I", SmallFace, 11, leading ? CyanBrush : lagging ? AmberBrush : TextBrush,
            new Point(ix + (Math.Cos(phi) >= 0 ? 4 : -14), iy - 8), Pps(), TextAlignment.Left);

        // the phi arc between V (0 deg) and I (-phi), small radius near the origin
        if (Math.Abs(phi) > 0.02)
            DrawArc(dc, c, r * 0.34, 0, phi);

        // labels: angle + lead/lag state at top, power factor at the bottom
        double pps = Pps();
        string deg = $"phi {Math.Abs(phi) * 180 / Math.PI:F1} deg";
        string state = leading ? "LEADING (absorbing VArs)"
                     : lagging ? "LAGGING (exporting VArs)"
                     : "UNITY (no VArs)";
        Brush stateBrush = leading ? CyanBrush : lagging ? AmberBrush : MutedBrush;

        DrawText(dc, deg, SmallFace, 11, MutedBrush, new Point(c.X, c.Y - r - 13), pps);
        DrawText(dc, state, SmallFace, 10.5, stateBrush, new Point(c.X, c.Y + r + 2), pps);
        if (ShowPowerFactor)
            DrawText(dc, $"PF {pf:F3}", Face, 15, TextBrush, new Point(c.X, c.Y + r + 16), pps);
    }

    private double Pps() => VisualTreeHelper.GetDpi(this).PixelsPerDip;

    /// <summary>Draws a vector from the centre at <paramref name="ang"/> radians (screen frame), with an arrowhead.</summary>
    private static void DrawVector(DrawingContext dc, Point c, Pen pen, double ang, double len)
    {
        Point tip = new(c.X + Math.Cos(ang) * len, c.Y + Math.Sin(ang) * len);
        dc.DrawLine(pen, c, tip);

        // arrowhead: two short barbs swept back from the tip
        const double head = 8, spread = 0.42;
        Point b1 = new(tip.X - Math.Cos(ang - spread) * head, tip.Y - Math.Sin(ang - spread) * head);
        Point b2 = new(tip.X - Math.Cos(ang + spread) * head, tip.Y - Math.Sin(ang + spread) * head);
        dc.DrawLine(pen, tip, b1);
        dc.DrawLine(pen, tip, b2);
    }

    /// <summary>Sweeps a small arc from screen angle a0 to a1 (radians) at radius r about c.</summary>
    private static void DrawArc(DrawingContext dc, Point c, double r, double a0, double a1)
    {
        Point p0 = new(c.X + Math.Cos(a0) * r, c.Y + Math.Sin(a0) * r);
        Point p1 = new(c.X + Math.Cos(a1) * r, c.Y + Math.Sin(a1) * r);
        var geo = new StreamGeometry();
        using (StreamGeometryContext g = geo.Open())
        {
            g.BeginFigure(p0, false, false);
            // a1 < a0 (lagging draws clockwise on screen -> counter-clockwise sweep flag)
            g.ArcTo(p1, new Size(r, r), 0, false,
                a1 < a0 ? SweepDirection.Counterclockwise : SweepDirection.Clockwise, true, false);
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
