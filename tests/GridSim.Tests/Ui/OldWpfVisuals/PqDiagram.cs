// TEST ORACLE (UI programme 1.3): src/GridSim.Wpf/Visuals/PqDiagram.cs exactly as it was at commit f3559445, before its maths
// TEST ORACLE moved to src/GridSim.Ui/Visuals. Only the using and namespace lines differ (the WPF types are the recording
// TEST ORACLE stand-ins in UiVisualsWpfShim.cs); UiVisualsParityTests checks a SHA-256 of the rest against the original file.
using System.Globalization;
using GridSim.Tests.Ui.WpfShim;

namespace GridSim.Tests.Ui.OldWpfVisuals;

/// <summary>
/// A simplified P-Q capability diagram for one synchronous machine: the operating point as a dot
/// against its protection boundaries - thermal MW limit (top), reactive export/absorption limits
/// (right/left), and the under-excitation / loss-of-excitation region (bottom-left corner). Lets a
/// plant engineer see at a glance whether a machine is near its reactive-absorption limit (leading,
/// under-excited) or its thermal limit, the way the operator/system view cannot. One render pass.
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
        double w = ActualWidth, h = ActualHeight;
        if (w < 20 || h < 20) return;
        double ml = 30, mr = 14, mt = 14, mb = 22;      // margins (room for axis labels)
        double px0 = ml, px1 = w - mr, py0 = h - mb, py1 = mt;   // plot box; py0 = P=0 (bottom)

        double qmin = System.Math.Min(Qmin, -1), qmax = System.Math.Max(Qmax, 1);
        double pmax = System.Math.Max(Pmax, 1);
        double span = System.Math.Max(qmax - qmin, 1);

        double QtoX(double q) => px0 + (q - qmin) / span * (px1 - px0);
        double PtoY(double p) => py0 - System.Math.Clamp(p / pmax, 0, 1) * (py0 - py1);

        // plot background + axes (Q=0 vertical, P=0 baseline)
        dc.DrawRectangle(Fill, null, new Rect(px0, py1, px1 - px0, py0 - py1));
        double xz = QtoX(0);
        dc.DrawLine(Axis, new Point(xz, py1), new Point(xz, py0));
        dc.DrawLine(Axis, new Point(px0, py0), new Point(px1, py0));

        // capability boundary: thermal top (Pmax) + reactive limits (Qmin/Qmax) + a loss-of-excitation
        // chamfer in the under-excited (Qmin, low-P) corner.
        double xMin = QtoX(qmin), xMax = QtoX(qmax), yTop = PtoY(pmax);
        var g = new StreamGeometry();
        using (var c = g.Open())
        {
            c.BeginFigure(new Point(xMax, py0), false, false);
            c.LineTo(new Point(xMax, yTop), true, false);          // right (over-excited) limit
            c.LineTo(new Point(xMin, yTop), true, false);          // thermal top
            c.LineTo(new Point(xMin, PtoY(pmax * 0.45)), true, false); // down the under-excited limit
            c.LineTo(new Point(QtoX(qmin * 0.5), py0), true, false);   // loss-of-excitation chamfer
        }
        g.Freeze();
        dc.DrawGeometry(null, Boundary, g);

        // operating dot, coloured by proximity to any boundary
        double qUtil = Qg >= 0 ? (qmax > 0 ? Qg / qmax : 0) : (qmin < 0 ? Qg / qmin : 0);
        double stress = System.Math.Max(pmax > 0 ? Pg / pmax : 0, qUtil);
        Brush dot = stress > 0.95 ? Frozen("#FF5B5B") : stress > 0.85 ? Frozen("#F3C13A") : DotBrush;
        double dx = QtoX(Qg), dy = PtoY(Pg);
        dc.DrawEllipse(dot, null, new Point(dx, dy), 5, 5);
        dc.DrawEllipse(null, new Pen(dot, 1.5) { }, new Point(dx, dy), 9, 9);

        // axis labels
        Text(dc, "MW", px0 - 26, py1 - 2, MutedBrush, 9);
        Text(dc, "under-exc", px0, py0 + 4, MutedBrush, 8);
        Text(dc, "over-exc", px1 - 40, py0 + 4, MutedBrush, 8);
    }

    private static void Text(DrawingContext dc, string s, double x, double y, Brush b, double size)
    {
        var ft = new FormattedText(s, CultureInfo.InvariantCulture, FlowDirection.LeftToRight, Face, size, b, 1.0);
        dc.DrawText(ft, new Point(x, y));
    }
}
