using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using GridSim.Ui.Visuals;

namespace GridSim.App.Controls;

/// <summary>
/// A control-room gauge dial (UI programme Phase 1.3, the port of the WPF <c>Visuals/Gauge.cs</c>): a 240 degree arc
/// with tick marks, optional amber and red zones, a needle, and the value printed in the hub. Used anywhere a number has
/// a meaningful range: RPM, torque, temperatures, loading, stress.
///
/// <para>It has the WPF control's properties under the same names and defaults (Value, Minimum, Maximum, AmberFrom,
/// RedFrom, AmberTo, RedTo, Unit, Caption, ValueText), each re-rendering the dial when it changes, so the dashboard port
/// binds it the same way. Every number it draws comes from <see cref="GaugeGeometry"/> in GridSim.Ui, the arithmetic the
/// WPF gauge draws from too; this class only turns those numbers into Avalonia pens, arcs, lines and text.</para>
/// </summary>
public sealed class Gauge : Control
{
    /// <summary>The value the needle shows.</summary>
    public static readonly StyledProperty<double> ValueProperty = AvaloniaProperty.Register<Gauge, double>(nameof(Value), 0.0);

    /// <summary>The value at the start of the scale.</summary>
    public static readonly StyledProperty<double> MinimumProperty = AvaloniaProperty.Register<Gauge, double>(nameof(Minimum), 0.0);

    /// <summary>The value at the end of the scale.</summary>
    public static readonly StyledProperty<double> MaximumProperty = AvaloniaProperty.Register<Gauge, double>(nameof(Maximum), 100.0);

    /// <summary>Fraction of range (0..1) where the amber band starts; 1 = no band.</summary>
    public static readonly StyledProperty<double> AmberFromProperty = AvaloniaProperty.Register<Gauge, double>(nameof(AmberFrom), 1.0);

    /// <summary>Fraction of range (0..1) where the red band starts; 1 = no band.</summary>
    public static readonly StyledProperty<double> RedFromProperty = AvaloniaProperty.Register<Gauge, double>(nameof(RedFrom), 1.0);

    /// <summary>Low-side bands, for symmetric instruments (grid speed): amber from 0 up to this fraction; 0 = none.</summary>
    public static readonly StyledProperty<double> AmberToProperty = AvaloniaProperty.Register<Gauge, double>(nameof(AmberTo), 0.0);

    /// <summary>Low-side red band, from 0 up to this fraction; 0 = none.</summary>
    public static readonly StyledProperty<double> RedToProperty = AvaloniaProperty.Register<Gauge, double>(nameof(RedTo), 0.0);

    /// <summary>Unit line under the value (e.g. "RPM", " degC", "MNm").</summary>
    public static readonly StyledProperty<string> UnitProperty = AvaloniaProperty.Register<Gauge, string>(nameof(Unit), "");

    /// <summary>Small caption above the dial (e.g. "GRID SPEED").</summary>
    public static readonly StyledProperty<string> CaptionProperty = AvaloniaProperty.Register<Gauge, string>(nameof(Caption), "");

    /// <summary>Override for the hub text; empty = the numeric value, auto-formatted.</summary>
    public static readonly StyledProperty<string> ValueTextProperty = AvaloniaProperty.Register<Gauge, string>(nameof(ValueText), "");

    static Gauge()
    {
        // Any property change redraws the dial (WPF: FrameworkPropertyMetadataOptions.AffectsRender on every property).
        AffectsRender<Gauge>(ValueProperty, MinimumProperty, MaximumProperty, AmberFromProperty, RedFromProperty,
            AmberToProperty, RedToProperty, UnitProperty, CaptionProperty, ValueTextProperty);
    }

    /// <inheritdoc cref="ValueProperty"/>
    public double Value { get => GetValue(ValueProperty); set => SetValue(ValueProperty, value); }

    /// <inheritdoc cref="MinimumProperty"/>
    public double Minimum { get => GetValue(MinimumProperty); set => SetValue(MinimumProperty, value); }

    /// <inheritdoc cref="MaximumProperty"/>
    public double Maximum { get => GetValue(MaximumProperty); set => SetValue(MaximumProperty, value); }

    /// <inheritdoc cref="AmberFromProperty"/>
    public double AmberFrom { get => GetValue(AmberFromProperty); set => SetValue(AmberFromProperty, value); }

    /// <inheritdoc cref="RedFromProperty"/>
    public double RedFrom { get => GetValue(RedFromProperty); set => SetValue(RedFromProperty, value); }

    /// <inheritdoc cref="AmberToProperty"/>
    public double AmberTo { get => GetValue(AmberToProperty); set => SetValue(AmberToProperty, value); }

    /// <inheritdoc cref="RedToProperty"/>
    public double RedTo { get => GetValue(RedToProperty); set => SetValue(RedToProperty, value); }

    /// <inheritdoc cref="UnitProperty"/>
    public string Unit { get => GetValue(UnitProperty); set => SetValue(UnitProperty, value); }

    /// <inheritdoc cref="CaptionProperty"/>
    public string Caption { get => GetValue(CaptionProperty); set => SetValue(CaptionProperty, value); }

    /// <inheritdoc cref="ValueTextProperty"/>
    public string ValueText { get => GetValue(ValueTextProperty); set => SetValue(ValueTextProperty, value); }

    /// <inheritdoc/>
    public override void Render(DrawingContext context)
    {
        // A null string (a binding that produced null) reads as empty, so the dial never fails on it.
        GaugeScene? s = GaugeGeometry.Compute(Bounds.Width, Bounds.Height, Value, Minimum, Maximum,
            AmberFrom, RedFrom, AmberTo, RedTo, ValueText ?? "");
        if (s is null) return;

        // arc track + warning bands, in the order the geometry lists them
        foreach (GaugeBand band in s.Bands)
            if (band.Arc is VisualArc arc) VisualDrawing.Arc(context, VisualDrawing.Pen(GaugeGeometry.PenFor(band.Kind)), arc);

        // ticks
        IPen tick = VisualDrawing.Pen(GaugeGeometry.TickPen);
        foreach (VisualLine t in s.Ticks) VisualDrawing.Line(context, tick, t.From, t.To);

        // needle + hub
        VisualDrawing.Line(context, VisualDrawing.Pen(GaugeGeometry.NeedlePen), s.Centre, s.NeedleTip);
        IBrush hub = VisualDrawing.Brush(GaugeGeometry.Hub);
        context.DrawEllipse(hub, null, VisualDrawing.P(s.Centre), GaugeGeometry.HubRadius, GaugeGeometry.HubRadius);

        // text: caption above, value + unit in/under the hub
        IBrush muted = VisualDrawing.Brush(GaugeGeometry.Muted);
        Typeface bold = VisualDrawing.Face(this, FontWeight.Bold), semi = VisualDrawing.Face(this, FontWeight.SemiBold);
        string caption = Caption ?? "", unit = Unit ?? "";
        if (caption.Length > 0)
            VisualDrawing.DrawCentred(context, VisualDrawing.Text(caption, semi, GaugeGeometry.CaptionFontSize, muted), s.CaptionAt);
        VisualDrawing.DrawCentred(context, VisualDrawing.Text(s.ValueLabel, bold, s.ValueFontSize, hub), s.ValueAt);
        if (unit.Length > 0)
            VisualDrawing.DrawCentred(context, VisualDrawing.Text(unit, semi, GaugeGeometry.UnitFontSize, muted), s.UnitAt);
    }
}
