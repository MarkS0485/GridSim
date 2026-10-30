using System.Globalization;
using System.Windows;
using System.Windows.Media;
using GridSim.Ui.Schematic;
using GridSim.Ui.Visuals;

namespace GridSim.Wpf.Visuals;

/// <summary>
/// A simplified P-Q capability diagram for one synchronous machine: the operating point as a dot
/// against its protection boundaries - thermal MW limit (top), reactive export/absorption limits
/// (right/left), and the under-excitation / loss-of-excitation region (bottom-left corner). Lets a
/// plant engineer see at a glance whether a machine is near its reactive-absorption limit (leading,
/// under-excited) or its thermal limit, the way the operator/system view cannot. One render pass.
/// UI programme Phase 1.3: the diagram's arithmetic (the plot box, the scales, the boundary polyline, the stress and the
/// dot position) moved to the platform-neutral <see cref="PqDiagramGeometry"/> (src/GridSim.Ui/Visuals), operation for
/// operation, so the Avalonia diagram draws from the same numbers; this control now only draws them and its pixels are
/// unchanged (UiVisualsParityTests pins the moved maths against a verbatim copy of the old code, bit for bit).
/// </summary>
public sealed class PqDiagram : FrameworkElement
{
    public static readonly DependencyProperty PgProperty = Dp(nameof(Pg), 0.0);
    public static readonly DependencyProperty QgProperty = Dp(nameof(Qg), 0.0);
    public static readonly DependencyProperty QminProperty = Dp(nameof(Qmin), -1.0);
    public static readonly DependencyProperty QmaxProperty = Dp(nameof(Qmax), 1.0);
    public static readonly DependencyProperty PmaxProperty = Dp(nameof(Pmax), 1.0);

    private static DependencyProperty Dp(string name, double dflt) => DependencyProperty.Register(
        name, typeof(double), typeof(PqDiagram),
        new FrameworkPropertyMetadata(dflt, FrameworkPropertyMetadataOptions.AffectsRender));

    public double Pg { get => (double)GetValue(PgProperty); set => SetValue(PgProperty, value); }
    public double Qg { get => (double)GetValue(QgProperty); set => SetValue(QgProperty, value); }
    public double Qmin { get => (double)GetValue(QminProperty); set => SetValue(QminProperty, value); }
    public double Qmax { get => (double)GetValue(QmaxProperty); set => SetValue(QmaxProperty, value); }
    public double Pmax { get => (double)GetValue(PmaxProperty); set => SetValue(PmaxProperty, value); }

    private static readonly Pen Boundary = MakePen("#46535F", 1.6);
    private static readonly Pen Axis = MakePen("#2A3742", 1.0);
    private static readonly Pen LoeArc = MakePen("#F3C13A", 1.4, 0.8);
    private static readonly Brush Fill = Frozen("#12FFFFFF");
    private static readonly Brush DotBrush = Frozen("#21C7E6");
    private static readonly Brush MutedBrush = Frozen("#8492A0");
    private static readonly Typeface Face =
        new(new FontFamily("Bahnschrift, Segoe UI"), FontStyles.Normal, FontWeights.SemiBold, FontStretches.Normal);

    private static Brush Frozen(string hex) { Brush b = new SolidColorBrush(Palette.FromHex(hex)); b.Freeze(); return b; }
    private static Pen MakePen(string hex, double w, double op = 1.0)
    {
        Brush b = new SolidColorBrush(Palette.FromHex(hex)) { Opacity = op }; b.Freeze();
        var p = new Pen(b, w); p.Freeze(); return p;
    }

    protected override void OnRender(DrawingContext dc)
    {
        // The whole diagram worked out by the shared geometry (null = too small to draw, as before: under 20 x 20).
        PqDiagramScene? s = PqDiagramGeometry.Compute(ActualWidth, ActualHeight, Pg, Qg, Qmin, Qmax, Pmax);
        if (s is null) return;
        double px0 = s.PlotLeft, px1 = s.PlotRight, py0 = s.PlotBottom, py1 = s.PlotTop;   // plot box; py0 = P=0 (bottom)

        // plot background + axes (Q=0 vertical, P=0 baseline)
        dc.DrawRectangle(Fill, null, new Rect(px0, py1, px1 - px0, py0 - py1));
        double xz = s.ZeroQX;
        dc.DrawLine(Axis, new Point(xz, py1), new Point(xz, py0));
        dc.DrawLine(Axis, new Point(px0, py0), new Point(px1, py0));

        // capability boundary: thermal top (Pmax) + reactive limits (Qmin/Qmax) + a loss-of-excitation
        // chamfer in the under-excited (Qmin, low-P) corner.
        IReadOnlyList<ScreenPoint> b = s.Boundary;
        var g = new StreamGeometry();
        using (var c = g.Open())
        {
            c.BeginFigure(Pt(b[0]), false, false);
            c.LineTo(Pt(b[1]), true, false);          // right (over-excited) limit
            c.LineTo(Pt(b[2]), true, false);          // thermal top
            c.LineTo(Pt(b[3]), true, false);          // down the under-excited limit
            c.LineTo(Pt(b[4]), true, false);          // loss-of-excitation chamfer
        }
        g.Freeze();
        dc.DrawGeometry(null, Boundary, g);

        // operating dot, coloured by proximity to any boundary
        Brush dot = s.Stress == PqStress.AtLimit ? Frozen("#FF5B5B") : s.Stress == PqStress.Near ? Frozen("#F3C13A") : DotBrush;
        Point d = Pt(s.Dot);
        dc.DrawEllipse(dot, null, d, 5, 5);
        dc.DrawEllipse(null, new Pen(dot, 1.5) { }, d, 9, 9);

        // axis labels
        Text(dc, "MW", s.MwLabelAt.X, s.MwLabelAt.Y, MutedBrush, 9);
        Text(dc, "under-exc", s.UnderExcitedLabelAt.X, s.UnderExcitedLabelAt.Y, MutedBrush, 8);
        Text(dc, "over-exc", s.OverExcitedLabelAt.X, s.OverExcitedLabelAt.Y, MutedBrush, 8);
    }

    /// <summary>The WPF point for a neutral screen point: the same two doubles.</summary>
    private static Point Pt(ScreenPoint p) => new(p.X, p.Y);

    private static void Text(DrawingContext dc, string s, double x, double y, Brush b, double size)
    {
        var ft = new FormattedText(s, CultureInfo.InvariantCulture, FlowDirection.LeftToRight, Face, size, b, 1.0);
        dc.DrawText(ft, new Point(x, y));
    }
}
