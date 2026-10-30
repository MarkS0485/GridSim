using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using GridSim.Ui.Visuals;

namespace GridSim.App.Controls;

/// <summary>
/// A machine's operating point drawn as a phasor disc (UI programme Phase 1.3, the port of the WPF
/// <c>Visuals/Phasor.cs</c>): a unit voltage reference at 0 degrees and the current phasor swung off it by the
/// power-factor angle phi = atan2(Q, P). A generator exporting VArs (Q &gt; 0) runs lagging (the phasor drops below the
/// axis, amber); absorbing VArs (Q &lt; 0) it runs leading (the phasor rides above, cyan). The phi arc, its angle, the
/// lead/lag label and the power factor are printed on the face.
///
/// <para>It has the WPF control's properties under the same names and defaults (P, Q, SMax, ShowPowerFactor), so the
/// dashboard port binds it the same way. Every number it draws comes from <see cref="PhasorGeometry"/> in GridSim.Ui,
/// the arithmetic the WPF phasor draws from too.</para>
/// </summary>
public sealed class Phasor : Control
{
    /// <summary>Real power, MW (active component along the reference axis).</summary>
    public static readonly StyledProperty<double> PProperty = AvaloniaProperty.Register<Phasor, double>(nameof(P), 0.0);

    /// <summary>Reactive power, MVAr, signed: positive exports VArs (lagging), negative absorbs (leading).</summary>
    public static readonly StyledProperty<double> QProperty = AvaloniaProperty.Register<Phasor, double>(nameof(Q), 0.0);

    /// <summary>Apparent-power scale for the current vector's length; 0 = auto from |S|.</summary>
    public static readonly StyledProperty<double> SMaxProperty = AvaloniaProperty.Register<Phasor, double>(nameof(SMax), 0.0);

    /// <summary>Whether the power factor is printed under the disc (default true).</summary>
    public static readonly StyledProperty<bool> ShowPowerFactorProperty = AvaloniaProperty.Register<Phasor, bool>(nameof(ShowPowerFactor), true);

    static Phasor()
    {
        AffectsRender<Phasor>(PProperty, QProperty, SMaxProperty, ShowPowerFactorProperty);
    }

    /// <inheritdoc cref="PProperty"/>
    public double P { get => GetValue(PProperty); set => SetValue(PProperty, value); }

    /// <inheritdoc cref="QProperty"/>
    public double Q { get => GetValue(QProperty); set => SetValue(QProperty, value); }

    /// <inheritdoc cref="SMaxProperty"/>
    public double SMax { get => GetValue(SMaxProperty); set => SetValue(SMaxProperty, value); }

    /// <inheritdoc cref="ShowPowerFactorProperty"/>
    public bool ShowPowerFactor { get => GetValue(ShowPowerFactorProperty); set => SetValue(ShowPowerFactorProperty, value); }

    /// <inheritdoc/>
    public override void Render(DrawingContext context)
    {
        PhasorScene? s = PhasorGeometry.Compute(Bounds.Width, Bounds.Height, P, Q, SMax);
        if (s is null) return;
        Point c = VisualDrawing.P(s.Centre);
        double r = s.Radius;

        // the disc and the +/- reference axes through it
        context.DrawEllipse(null, VisualDrawing.Pen(PhasorGeometry.DiscPen), c, r, r);
        IPen axis = VisualDrawing.Pen(PhasorGeometry.AxisPen);
        context.DrawLine(axis, new Point(c.X - r, c.Y), new Point(c.X + r, c.Y));
        context.DrawLine(axis, new Point(c.X, c.Y - r), new Point(c.X, c.Y + r));

        Typeface bold = VisualDrawing.Face(this, FontWeight.Bold), semi = VisualDrawing.Face(this, FontWeight.SemiBold);

        // voltage reference phasor and its label
        DrawArrow(context, VisualDrawing.Pen(PhasorGeometry.VPen), s.Voltage);
        VisualDrawing.DrawAt(context, VisualDrawing.Text("V", semi, PhasorGeometry.SmallFontSize,
            VisualDrawing.Brush(PhasorGeometry.Cyan)), s.VoltageLabelAt);

        // current phasor (electrical -phi, screen +phi: lagging sits below the axis) and its label
        DrawArrow(context, VisualDrawing.Pen(PhasorGeometry.CurrentPen(s.State)), s.Current);
        VisualDrawing.DrawAt(context, VisualDrawing.Text("I", semi, PhasorGeometry.SmallFontSize,
            VisualDrawing.Brush(PhasorGeometry.CurrentLabelFill(s.State))), s.CurrentLabelAt);

        // the phi arc between V and I
        if (s.PhiArc is VisualArc arc) VisualDrawing.Arc(context, VisualDrawing.Pen(PhasorGeometry.ArcPen), arc);

        // labels: angle + lead/lag state, power factor at the bottom
        VisualDrawing.DrawCentred(context, VisualDrawing.Text(s.AngleLabel, semi, PhasorGeometry.SmallFontSize,
            VisualDrawing.Brush(PhasorGeometry.Muted)), s.AngleAt);
        VisualDrawing.DrawCentred(context, VisualDrawing.Text(s.StateLabel, semi, PhasorGeometry.StateFontSize,
            VisualDrawing.Brush(PhasorGeometry.StateFill(s.State))), s.StateAt);
        if (ShowPowerFactor)
            VisualDrawing.DrawCentred(context, VisualDrawing.Text(s.PowerFactorLabel, bold, PhasorGeometry.PowerFactorFontSize,
                VisualDrawing.Brush(PhasorGeometry.Text)), s.PowerFactorAt);
    }

    /// <summary>The shaft and the two arrowhead barbs.</summary>
    private static void DrawArrow(DrawingContext dc, IPen pen, PhasorArrow a)
    {
        VisualDrawing.Line(dc, pen, a.Tail, a.Tip);
        VisualDrawing.Line(dc, pen, a.Tip, a.Barb1);
        VisualDrawing.Line(dc, pen, a.Tip, a.Barb2);
    }
}
