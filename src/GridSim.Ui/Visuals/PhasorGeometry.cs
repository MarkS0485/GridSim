using System.Globalization;
using GridSim.Ui.Presentation;
using GridSim.Ui.Schematic;

namespace GridSim.Ui.Visuals;

/// <summary>The machine's reactive state as the phasor shows it.</summary>
public enum PhasorState
{
    /// <summary>No VArs to speak of (|Q| at most 1e-6): unity power factor.</summary>
    Unity,
    /// <summary>Exporting VArs (Q &gt; 1e-6): current lags voltage, drawn below the axis in amber.</summary>
    Lagging,
    /// <summary>Absorbing VArs (Q &lt; -1e-6): current leads voltage, drawn above the axis in cyan.</summary>
    Leading,
}

/// <summary>A phasor drawn from the centre: the shaft to the tip and the two arrowhead barbs swept back from the tip.</summary>
/// <param name="Tail">The centre of the disc.</param>
/// <param name="Tip">The arrow tip.</param>
/// <param name="Barb1">The first barb's end (the barb runs from <see cref="Tip"/>).</param>
/// <param name="Barb2">The second barb's end.</param>
public readonly record struct PhasorArrow(ScreenPoint Tail, ScreenPoint Tip, ScreenPoint Barb1, ScreenPoint Barb2);

/// <summary>
/// Everything the phasor disc draws, worked out from its size and P, Q (see <see cref="PhasorGeometry.Compute"/>).
/// </summary>
public sealed class PhasorScene
{
    /// <summary>The centre of the disc.</summary>
    public ScreenPoint Centre { get; init; }

    /// <summary>The disc radius.</summary>
    public double Radius { get; init; }

    /// <summary>The apparent power |S| = sqrt(P^2 + Q^2).</summary>
    public double ApparentPower { get; init; }

    /// <summary>The electrical angle phi = atan2(Q, P), radians; positive is lagging.</summary>
    public double Phi { get; init; }

    /// <summary>Leading, lagging or unity.</summary>
    public PhasorState State { get; init; }

    /// <summary>The power factor cos phi = |P| / |S| (1 when |S| is at most 1e-6).</summary>
    public double PowerFactor { get; init; }

    /// <summary>The current phasor's length on screen.</summary>
    public double CurrentLength { get; init; }

    /// <summary>The voltage reference phasor (along +x, 0.92 of the radius).</summary>
    public PhasorArrow Voltage { get; init; }

    /// <summary>The current phasor (electrical angle -phi, screen angle +phi: lagging sits below the axis).</summary>
    public PhasorArrow Current { get; init; }

    /// <summary>The top-left of the "V" label.</summary>
    public ScreenPoint VoltageLabelAt { get; init; }

    /// <summary>The top-left of the "I" label (beside the current tip, on its outward side).</summary>
    public ScreenPoint CurrentLabelAt { get; init; }

    /// <summary>The phi arc between V and I near the origin, or null when |phi| is at most 0.02 rad.</summary>
    public VisualArc? PhiArc { get; init; }

    /// <summary>The angle label, "phi 12.3 deg".</summary>
    public string AngleLabel { get; init; } = "";

    /// <summary>The state label, "LAGGING (exporting VArs)" and so on.</summary>
    public string StateLabel { get; init; } = "";

    /// <summary>The power-factor label, "PF 0.980".</summary>
    public string PowerFactorLabel { get; init; } = "";

    /// <summary>Where the angle label is centred (its top edge), above the disc.</summary>
    public ScreenPoint AngleAt { get; init; }

    /// <summary>Where the state label is centred (its top edge), under the disc.</summary>
    public ScreenPoint StateAt { get; init; }

    /// <summary>Where the power-factor label is centred (its top edge), under the state label.</summary>
    public ScreenPoint PowerFactorAt { get; init; }
}

/// <summary>
/// The pure geometry of the phasor disc (UI programme Phase 1.3): a machine's operating point as a unit voltage reference
/// at 0 degrees and the current phasor swung off it by the power-factor angle phi = atan2(Q, P), with the phi arc, the
/// angle, the lead/lag state and the power factor printed on the face.
///
/// <para>This is the arithmetic of the WPF <c>Visuals/Phasor.cs</c> <c>OnRender</c>, moved here operation for operation
/// (same expressions, same order, the same label format strings), so the WPF control and the Avalonia control draw the
/// same numbers. <c>UiVisualsParityTests</c> pins it against a verbatim copy of the old WPF code, bit for bit.</para>
///
/// <para>The angles use <see cref="Math.Atan2"/>, <see cref="Math.Cos"/> and <see cref="Math.Sin"/>, as the WPF phasor
/// always did: cosmetic screen layout, not a solved number (the same note as <see cref="SchematicView"/>). The angle and
/// power-factor labels format with the current culture, exactly as the WPF labels always did.</para>
/// </summary>
public static class PhasorGeometry
{
    /// <summary>Below this width or height (pixels) the disc draws nothing.</summary>
    public const double MinSize = 40;

    /// <summary>|Q| above this counts as leading or lagging.</summary>
    public const double ReactiveDeadband = 1e-6;

    /// <summary>The arrowhead barb length (pixels).</summary>
    public const double ArrowHead = 8;

    /// <summary>The arrowhead barb spread either side of the shaft (radians).</summary>
    public const double ArrowSpread = 0.42;

    /// <summary>Font size of the V and I labels and the angle label.</summary>
    public const double SmallFontSize = 11;

    /// <summary>Font size of the state label.</summary>
    public const double StateFontSize = 10.5;

    /// <summary>Font size of the power-factor label (bold).</summary>
    public const double PowerFactorFontSize = 15;

    // ---- the colour tokens (the same hex values the WPF phasor's pens and brushes were built from) ----

    /// <summary>The disc outline.</summary>
    public static readonly VisualPen DiscPen = new(ColourRamps.FromHex("#2A3742"), 1.4, RoundCaps: true);

    /// <summary>The cross-hair axes.</summary>
    public static readonly VisualPen AxisPen = new(ColourRamps.FromHex("#46535F"), 1.4, RoundCaps: true);

    /// <summary>The voltage reference phasor (the accent), also the current at unity.</summary>
    public static readonly VisualPen VPen = new(ColourRamps.FromHex("#21C7E6"), 3, RoundCaps: true);

    /// <summary>The current phasor, lagging (amber).</summary>
    public static readonly VisualPen LagPen = new(ColourRamps.FromHex("#F3C13A"), 3, RoundCaps: true);

    /// <summary>The current phasor, leading (cyan).</summary>
    public static readonly VisualPen LeadPen = new(ColourRamps.FromHex("#21C7E6"), 3, 0.95, RoundCaps: true);

    /// <summary>The phi arc.</summary>
    public static readonly VisualPen ArcPen = new(ColourRamps.FromHex("#8492A0"), 1.6, RoundCaps: true);

    /// <summary>The power factor and the unity "I" label.</summary>
    public static readonly VisualFill Text = new(ColourRamps.FromHex("#E6EDF3"));

    /// <summary>The angle label and the unity state label.</summary>
    public static readonly VisualFill Muted = new(ColourRamps.FromHex("#8492A0"));

    /// <summary>The lagging labels.</summary>
    public static readonly VisualFill Amber = new(ColourRamps.FromHex("#F3C13A"));

    /// <summary>The "V" label and the leading labels.</summary>
    public static readonly VisualFill Cyan = new(ColourRamps.FromHex("#21C7E6"));

    /// <summary>The current phasor's pen for a state (leading cyan, lagging amber, unity the voltage pen).</summary>
    public static VisualPen CurrentPen(PhasorState s) => s == PhasorState.Leading ? LeadPen : s == PhasorState.Lagging ? LagPen : VPen;

    /// <summary>The "I" label's colour for a state (leading cyan, lagging amber, unity text).</summary>
    public static VisualFill CurrentLabelFill(PhasorState s) => s == PhasorState.Leading ? Cyan : s == PhasorState.Lagging ? Amber : Text;

    /// <summary>The state label's colour (leading cyan, lagging amber, unity muted).</summary>
    public static VisualFill StateFill(PhasorState s) => s == PhasorState.Leading ? Cyan : s == PhasorState.Lagging ? Amber : Muted;

    /// <summary>
    /// A vector from <paramref name="c"/> at screen angle <paramref name="ang"/> (radians) and length
    /// <paramref name="len"/>, with its two arrowhead barbs (the old WPF <c>DrawVector</c>).
    /// </summary>
    public static PhasorArrow Arrow(ScreenPoint c, double ang, double len)
    {
        ScreenPoint tip = new(c.X + Math.Cos(ang) * len, c.Y + Math.Sin(ang) * len);

        // arrowhead: two short barbs swept back from the tip
        const double head = ArrowHead, spread = ArrowSpread;
        ScreenPoint b1 = new(tip.X - Math.Cos(ang - spread) * head, tip.Y - Math.Sin(ang - spread) * head);
        ScreenPoint b2 = new(tip.X - Math.Cos(ang + spread) * head, tip.Y - Math.Sin(ang + spread) * head);
        return new PhasorArrow(c, tip, b1, b2);
    }

    /// <summary>
    /// A small arc from screen angle <paramref name="a0"/> to <paramref name="a1"/> (radians) at radius
    /// <paramref name="r"/> about <paramref name="c"/> (the old WPF <c>DrawArc</c>): never large; counter-clockwise when
    /// <paramref name="a1"/> is below <paramref name="a0"/>.
    /// </summary>
    public static VisualArc PhiArcBetween(ScreenPoint c, double r, double a0, double a1)
    {
        ScreenPoint p0 = new(c.X + Math.Cos(a0) * r, c.Y + Math.Sin(a0) * r);
        ScreenPoint p1 = new(c.X + Math.Cos(a1) * r, c.Y + Math.Sin(a1) * r);
        // a1 < a0 (lagging draws clockwise on screen -> counter-clockwise sweep flag)
        return new VisualArc(p0, p1, r, IsLargeArc: false, Clockwise: !(a1 < a0));
    }

    /// <summary>
    /// Work out the phasor disc for a control of <paramref name="w"/> by <paramref name="h"/> pixels, or null when it is
    /// smaller than <see cref="MinSize"/> either way (the old phasor drew nothing then).
    /// </summary>
    /// <param name="w">Control width.</param>
    /// <param name="h">Control height.</param>
    /// <param name="p">Real power, MW.</param>
    /// <param name="q">Reactive power, MVAr, signed: positive exports VArs (lagging), negative absorbs (leading).</param>
    /// <param name="sMax">Apparent-power scale for the current vector; 0 (or anything at most 1e-6) = auto from |S|.</param>
    public static PhasorScene? Compute(double w, double h, double p, double q, double sMax)
    {
        // NaN fails every comparison, so "w < MinSize" let a NaN size through; written the other way round it is refused
        // (2026-10-24). Every finite size gives the same answer as before.
        if (!(w >= MinSize) || !(h >= MinSize)) return null;

        double r = Math.Min(w, h) / 2 - 14;
        ScreenPoint c = new(w / 2, h / 2);

        // phi = atan2(Q, P), positive for a lagging machine (Q > 0, exporting VArs). The standard convention, with V as
        // the reference along +x and angles counter-clockwise: a lagging current is phi BEHIND V, i.e. at -phi, below the
        // real axis. Screen y grows downward, which mirrors the vertical, so the screen angle of -phi is +phi.
        // (2026-10-24: the control used -phi on screen, which put a lagging current ABOVE the axis, against this
        // convention and against its own comments; both apps draw from here, so both are corrected together.)
        double s = Math.Sqrt(p * p + q * q);
        double phi = Math.Atan2(q, p);                 // electrical angle, +ve = lagging
        bool lagging = q > ReactiveDeadband, leading = q < -ReactiveDeadband;
        double pf = s > 1e-6 ? Math.Abs(p) / s : 1.0;   // power factor cos phi

        // current vector length: scale |S| against SMax (or its own magnitude) onto the disc
        double scale = sMax > 1e-6 ? sMax : Math.Max(s, 1e-6);
        double iLen = Math.Clamp(s / scale, 0.12, 1.0) * r;

        // voltage reference phasor: unit length along +x (0 deg)
        PhasorArrow v = Arrow(c, 0, r * 0.92);

        // current phasor: screen angle +phi (electrical -phi, file comment above), so lagging sits below the axis
        PhasorArrow i = Arrow(c, phi, iLen);
        double ix = c.X + Math.Cos(phi) * iLen, iy = c.Y + Math.Sin(phi) * iLen;

        PhasorState state = leading ? PhasorState.Leading : lagging ? PhasorState.Lagging : PhasorState.Unity;
        return new PhasorScene
        {
            Centre = c,
            Radius = r,
            ApparentPower = s,
            Phi = phi,
            State = state,
            PowerFactor = pf,
            CurrentLength = iLen,
            Voltage = v,
            Current = i,
            VoltageLabelAt = new ScreenPoint(c.X + r * 0.92 + 2, c.Y - 8),
            CurrentLabelAt = new ScreenPoint(ix + (Math.Cos(phi) >= 0 ? 4 : -14), iy - 8),
            // the phi arc between V (0 deg) and I (screen +phi), small radius near the origin
            PhiArc = Math.Abs(phi) > 0.02 ? PhiArcBetween(c, r * 0.34, 0, phi) : null,
            // labels: angle + lead/lag state at top, power factor at the bottom. 2026-10-24: invariant culture, as every
            // other read-out (a German machine printed "PF 0,949"); the formats are the old ones.
            AngleLabel = string.Create(CultureInfo.InvariantCulture, $"phi {Math.Abs(phi) * 180 / Math.PI:F1} deg"),
            StateLabel = leading ? "LEADING (absorbing VArs)"
                       : lagging ? "LAGGING (exporting VArs)"
                       : "UNITY (no VArs)",
            PowerFactorLabel = string.Create(CultureInfo.InvariantCulture, $"PF {pf:F3}"),
            AngleAt = new ScreenPoint(c.X, c.Y - r - 13),
            StateAt = new ScreenPoint(c.X, c.Y + r + 2),
            PowerFactorAt = new ScreenPoint(c.X, c.Y + r + 16),
        };
    }
}
