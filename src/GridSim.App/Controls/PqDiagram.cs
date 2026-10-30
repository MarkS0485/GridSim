using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Media.Immutable;
using GridSim.Ui.Visuals;

namespace GridSim.App.Controls;

/// <summary>
/// A simplified P-Q capability diagram for one synchronous machine (UI programme Phase 1.3, the port of the WPF
/// <c>Visuals/PqDiagram.cs</c>): the operating point as a dot against its protection boundaries (thermal MW limit at the
/// top, reactive export and absorption limits right and left, and the loss-of-excitation corner bottom left), coloured
/// by how close it is to any of them.
///
/// <para>It has the WPF control's properties under the same names and defaults (Pg, Qg, Qmin, Qmax, Pmax), so the
/// dashboard port binds it the same way. Every number it draws comes from <see cref="PqDiagramGeometry"/> in GridSim.Ui,
/// the arithmetic the WPF diagram draws from too.</para>
/// </summary>
public sealed class PqDiagram : Control
{
    /// <summary>The machine's real power output.</summary>
    public static readonly StyledProperty<double> PgProperty = AvaloniaProperty.Register<PqDiagram, double>(nameof(Pg), 0.0);

    /// <summary>The machine's reactive power output (signed).</summary>
    public static readonly StyledProperty<double> QgProperty = AvaloniaProperty.Register<PqDiagram, double>(nameof(Qg), 0.0);

    /// <summary>The reactive absorption limit (negative; the scale never starts above -1).</summary>
    public static readonly StyledProperty<double> QminProperty = AvaloniaProperty.Register<PqDiagram, double>(nameof(Qmin), -1.0);

    /// <summary>The reactive export limit (positive; the scale never ends below 1).</summary>
    public static readonly StyledProperty<double> QmaxProperty = AvaloniaProperty.Register<PqDiagram, double>(nameof(Qmax), 1.0);

    /// <summary>The thermal real-power limit (the scale never tops out below 1).</summary>
    public static readonly StyledProperty<double> PmaxProperty = AvaloniaProperty.Register<PqDiagram, double>(nameof(Pmax), 1.0);

    static PqDiagram()
    {
        AffectsRender<PqDiagram>(PgProperty, QgProperty, QminProperty, QmaxProperty, PmaxProperty);
    }

    /// <inheritdoc cref="PgProperty"/>
    public double Pg { get => GetValue(PgProperty); set => SetValue(PgProperty, value); }

    /// <inheritdoc cref="QgProperty"/>
    public double Qg { get => GetValue(QgProperty); set => SetValue(QgProperty, value); }

    /// <inheritdoc cref="QminProperty"/>
    public double Qmin { get => GetValue(QminProperty); set => SetValue(QminProperty, value); }

    /// <inheritdoc cref="QmaxProperty"/>
    public double Qmax { get => GetValue(QmaxProperty); set => SetValue(QmaxProperty, value); }

    /// <inheritdoc cref="PmaxProperty"/>
    public double Pmax { get => GetValue(PmaxProperty); set => SetValue(PmaxProperty, value); }

    /// <inheritdoc/>
    public override void Render(DrawingContext context)
    {
        PqDiagramScene? s = PqDiagramGeometry.Compute(Bounds.Width, Bounds.Height, Pg, Qg, Qmin, Qmax, Pmax);
        if (s is null) return;
        double px0 = s.PlotLeft, px1 = s.PlotRight, py0 = s.PlotBottom, py1 = s.PlotTop;

        // plot background + axes (Q=0 vertical, P=0 baseline). The box is drawn only when it has a size: between 20 and
        // 44 pixels wide (or 20 and 36 high) its width (height) is negative, which the WPF Rect refused with an exception.
        if (px1 - px0 >= 0 && py0 - py1 >= 0)
            context.DrawRectangle(VisualDrawing.Brush(PqDiagramGeometry.Fill), null, new Rect(px0, py1, px1 - px0, py0 - py1));
        IPen axis = VisualDrawing.Pen(PqDiagramGeometry.Axis);
        VisualDrawing.Line(context, axis, new(s.ZeroQX, py1), new(s.ZeroQX, py0));
        VisualDrawing.Line(context, axis, new(px0, py0), new(px1, py0));

        // capability boundary (an open polyline)
        StreamGeometry? g = VisualDrawing.Poly(s.Boundary, closed: false);
        if (g is not null) context.DrawGeometry(null, VisualDrawing.Pen(PqDiagramGeometry.Boundary), g);

        // operating dot and its ring, coloured by proximity to any boundary
        if (GridSim.Ui.Visuals.VisualMaths.IsFinite(s.Dot))
        {
            ImmutableSolidColorBrush dot = VisualDrawing.Brush(PqDiagramGeometry.DotFill(s.Stress));
            Point d = VisualDrawing.P(s.Dot);
            context.DrawEllipse(dot, null, d, PqDiagramGeometry.DotRadius, PqDiagramGeometry.DotRadius);
            context.DrawEllipse(null, new ImmutablePen(dot, PqDiagramGeometry.RingThickness), d,
                PqDiagramGeometry.RingRadius, PqDiagramGeometry.RingRadius);
        }

        // axis labels
        Typeface face = VisualDrawing.Face(this, FontWeight.SemiBold);
        IBrush muted = VisualDrawing.Brush(PqDiagramGeometry.Muted);
        VisualDrawing.DrawAt(context, VisualDrawing.Text("MW", face, PqDiagramGeometry.MwFontSize, muted), s.MwLabelAt);
        VisualDrawing.DrawAt(context, VisualDrawing.Text("under-exc", face, PqDiagramGeometry.ExcitationFontSize, muted), s.UnderExcitedLabelAt);
        VisualDrawing.DrawAt(context, VisualDrawing.Text("over-exc", face, PqDiagramGeometry.ExcitationFontSize, muted), s.OverExcitedLabelAt);
    }
}
