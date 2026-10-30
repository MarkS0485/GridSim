using GridSim.Ui.Presentation;
using GridSim.Ui.Schematic;

namespace GridSim.Ui.Visuals;

/// <summary>
/// The live values the transformer schematic shows (the WPF control's dependency properties, with the same defaults).
/// </summary>
public readonly record struct TransformerSchematicInput
{
    /// <summary>The input with every WPF default (LoadingPct 0, 132 / 33 kV, tap 1.0 in 0.9..1.1, 58 / 71 degC, no OLTC).</summary>
    public TransformerSchematicInput() { }

    /// <summary>Loading in percent of the rating.</summary>
    public double LoadingPct { get; init; } = 0.0;

    /// <summary>Nominal HV line voltage, kV.</summary>
    public double HvKvNom { get; init; } = 132.0;

    /// <summary>Nominal LV line voltage, kV.</summary>
    public double LvKvNom { get; init; } = 33.0;

    /// <summary>Live HV line voltage, kV (0 or less = not known yet: the nominal is shown).</summary>
    public double HvKvLive { get; init; } = 0.0;

    /// <summary>Live LV line voltage, kV (0 or less = not known yet: the nominal is shown).</summary>
    public double LvKvLive { get; init; } = 0.0;

    /// <summary>The real power through the unit, MW.</summary>
    public double FlowMw { get; init; } = 0.0;

    /// <summary>The rating, MVA.</summary>
    public double RatingMva { get; init; } = 0.0;

    /// <summary>The tap ratio.</summary>
    public double TapRatio { get; init; } = 1.0;

    /// <summary>Top-oil temperature, degC (a demo value on today's pages).</summary>
    public double TopOilC { get; init; } = 58.0;

    /// <summary>Winding hot-spot temperature, degC (a demo value on today's pages).</summary>
    public double HotSpotC { get; init; } = 71.0;

    /// <summary>True when an on-load tap changer is fitted (the OLTC box is drawn).</summary>
    public bool HasOltc { get; init; } = false;

    /// <summary>The lowest tap ratio.</summary>
    public double TapMin { get; init; } = 0.9;

    /// <summary>The highest tap ratio.</summary>
    public double TapMax { get; init; } = 1.1;
}

/// <summary>
/// The pure layout and colours of the front-elevation transformer schematic (UI programme ASSETS A1): tank outline,
/// three-limb core with HV / LV windings, porcelain bushings at the top (HV) and bottom (LV), cooling radiator banks, the
/// OLTC box with its tap pointer, and the live data overlays. It is laid out on a fixed 920 by 485 canvas (the original
/// SVG viewBox) and scaled uniformly to fit the control, centred.
///
/// <para>This is the arithmetic of the WPF <c>Visuals/TransformerSchematic.cs</c> <c>OnRender</c>, moved here
/// operation for operation and call for call: <see cref="Compute"/> returns the very drawing calls the old code made,
/// in the same order, with the same numbers, colours, layers and text, so the WPF control (which now only replays them)
/// draws the same pixels. <c>UiAssetVisualsParityTests</c> pins this against a verbatim copy of the old WPF code, bit
/// for bit. The text is formatted with the current culture, as the old interpolated strings were.</para>
///
/// <para>No trigonometry beyond one square root (the phase voltage, V_LL / sqrt(3)).</para>
/// </summary>
public static class TransformerSchematicGeometry
{
    /// <summary>Below this width (pixels) the schematic draws nothing.</summary>
    public const double MinWidth = 200;

    /// <summary>Below this height (pixels) the schematic draws nothing.</summary>
    public const double MinHeight = 150;

    /// <summary>The canvas width the drawing is laid out on (the original SVG viewBox).</summary>
    public const double CanvasWidth = 920;

    /// <summary>The canvas height the drawing is laid out on.</summary>
    public const double CanvasHeight = 485;

    // ---- the colours (the same hex values the WPF control's pens and brushes were built from) ----

    private static readonly UiColour TankStroke = ColourRamps.FromHex("#3B5EA6");
    private static readonly UiColour TankFillColour = ColourRamps.FromHex("#0C1A35");
    private static readonly UiColour CoreBlue = ColourRamps.FromHex("#1D4ED8");
    private static readonly UiColour PhaseA = ColourRamps.FromHex("#3B82F6");
    private static readonly UiColour PhaseB = ColourRamps.FromHex("#F59E0B");
    private static readonly UiColour PhaseC = ColourRamps.FromHex("#A855F7");
    private static readonly UiColour SlateLight = ColourRamps.FromHex("#94A3B8");
    private static readonly UiColour SlateDim = ColourRamps.FromHex("#64748B");
    private static readonly UiColour SlateDark = ColourRamps.FromHex("#334155");
    private static readonly UiColour DarkBg = ColourRamps.FromHex("#0F172A");
    private static readonly UiColour CyanColour = ColourRamps.FromHex("#22D3EE");
    private static readonly UiColour AmberColour = ColourRamps.FromHex("#F59E0B");
    private static readonly UiColour RedColour = ColourRamps.FromHex("#EF4444");

    /// <summary>The tank outline.</summary>
    public static readonly VisualPen TankPen = new(TankStroke, 2.5);

    /// <summary>The core yokes and limbs outline.</summary>
    public static readonly VisualPen CorePen = new(CoreBlue, 1.2);

    /// <summary>The fan circle outline.</summary>
    public static readonly VisualPen SlatePen = new(SlateDark, 1.5);

    /// <summary>The LV bushing stems.</summary>
    public static readonly VisualPen BushingStem = new(SlateLight, 5);

    /// <summary>The HV bushing stems.</summary>
    public static readonly VisualPen BushingStemHv = new(SlateLight, 6);

    /// <summary>The radiator fin outlines.</summary>
    public static readonly VisualPen FinStroke = new(ColourRamps.FromHex("#2D4A6F"), 1);

    /// <summary>The radiator header and footer pipes.</summary>
    public static readonly VisualPen PipePen = new(SlateDark, 1.5);

    /// <summary>The legs from a radiator bank to the tank.</summary>
    public static readonly VisualPen ConnectPen = new(SlateDark, 2.5);

    /// <summary>The outline of the info boxes.</summary>
    public static readonly VisualPen InfoBoxPen = new(ColourRamps.FromHex("#1E3A5F"), 1.5);

    /// <summary>The neutral earth symbol (the dim text colour, 2 px).</summary>
    public static readonly VisualPen EarthPen = new(SlateDim, 2);

    /// <summary>The OLTC box border: the cyan at alpha 0x55.</summary>
    public static readonly VisualPen OltcBorderPen = new(new UiColour(0x55, CyanColour.R, CyanColour.G, CyanColour.B), 2);

    /// <summary>The OLTC tap scale.</summary>
    public static readonly VisualPen OltcScalePen = new(ColourRamps.FromHex("#1E3A5F"), 2);

    /// <summary>The OLTC tap ticks.</summary>
    public static readonly VisualPen OltcTickPen = new(SlateDark, 1);

    /// <summary>The OLTC link to the tank (dashed, drawn at 0.6 opacity).</summary>
    public static readonly VisualPen OltcLinkPen = new(CyanColour, 1.5, Pattern: VisualDashPattern.Dash);

    /// <summary>The tank fill.</summary>
    public static readonly VisualFill TankFill = new(TankFillColour);

    /// <summary>The core fill.</summary>
    public static readonly VisualFill CoreFill = new(ColourRamps.FromHex("#132A57"));

    /// <summary>The radiator fin and pipe fill.</summary>
    public static readonly VisualFill FinFill = new(ColourRamps.FromHex("#1A2741"));

    /// <summary>The dark fill of the boxes, badges and the fan circle.</summary>
    public static readonly VisualFill DarkBgFill = new(DarkBg);

    /// <summary>The porcelain sheds.</summary>
    public static readonly VisualFill ShedFill = new(ColourRamps.FromHex("#CBD5E1"));

    /// <summary>Unused by the drawing; kept as the old control declared it.</summary>
    public static readonly VisualFill TextWhite = new(ColourRamps.FromHex("#E6EDF3"));

    /// <summary>Values in slate (the OLTC caption, the tap line, the side kV labels).</summary>
    public static readonly VisualFill TextSlate = new(SlateLight);

    /// <summary>Captions.</summary>
    public static readonly VisualFill TextDim = new(SlateDim);

    /// <summary>Normal state, the rating, the OLTC pointer.</summary>
    public static readonly VisualFill Cyan = new(CyanColour);

    /// <summary>Loading above 75 percent, hot-spot above 85 degC.</summary>
    public static readonly VisualFill Amber = new(AmberColour);

    /// <summary>Loading above 90 percent, hot-spot above 98 degC.</summary>
    public static readonly VisualFill Red = new(RedColour);

    /// <summary>The three phase colours, A, B, C (blue, amber, purple).</summary>
    public static readonly IReadOnlyList<UiColour> PhaseColours = [PhaseA, PhaseB, PhaseC];

    /// <summary>The x of the three limbs and bushings on the canvas.</summary>
    public static readonly IReadOnlyList<double> LimbX = [320, 440, 560];

    /// <summary>The loading colour: red above 90 percent, amber above 75 percent, cyan otherwise (NaN is cyan).</summary>
    public static VisualFill LoadingFill(double loadingPct) => loadingPct > 90 ? Red : loadingPct > 75 ? Amber : Cyan;

    /// <summary>The thermal colour: red above 98 degC, amber above 85 degC, cyan otherwise (NaN is cyan).</summary>
    public static VisualFill ThermalFill(double hotSpotC) => hotSpotC > 98 ? Red : hotSpotC > 85 ? Amber : Cyan;

    /// <summary>
    /// The phase-to-neutral voltage a bushing badge prints, V_LL / sqrt(3): the live line voltage when known (above 0),
    /// else the nominal, identical on all three phases because the power flow is balanced. (It used to print V_LL / 3
    /// with an invented +/-0.3 % spread between the phases; that was removed before this move and stays removed.)
    /// </summary>
    public static double PhaseVoltageKv(double liveKv, double nominalKv) => (liveKv > 0 ? liveKv : nominalKv) / Math.Sqrt(3.0);

    /// <summary>
    /// The OLTC pointer position as a fraction of the tap range (0 = TapMin at the top of the scale, 1 = TapMax),
    /// clamped to 0..1; 0.5 when the range is empty or inverted. NaN when the tap ratio is NaN (Math.Clamp keeps NaN).
    /// </summary>
    public static double TapFraction(double tapRatio, double tapMin, double tapMax)
    {
        double tapFrac = tapMax > tapMin ? (tapRatio - tapMin) / (tapMax - tapMin) : 0.5;
        return Math.Clamp(tapFrac, 0, 1);
    }

    /// <summary>
    /// The drawing calls of the schematic for a control of <paramref name="aw"/> by <paramref name="ah"/> pixels, or
    /// null when it is smaller than <see cref="MinWidth"/> by <see cref="MinHeight"/> (the old control drew nothing
    /// then). A NaN size passes the size check, as it did in the old control.
    /// </summary>
    /// <param name="aw">Control width.</param>
    /// <param name="ah">Control height.</param>
    /// <param name="v">The live values.</param>
    public static VisualDisplayList? Compute(double aw, double ah, TransformerSchematicInput v)
    {
        if (aw < MinWidth || ah < MinHeight) return null;
        var dc = new VisualDisplayList();

        // The SVG viewBox was 920x485; we scale uniformly to fit the element, centered.
        double vw = CanvasWidth, vh = CanvasHeight;
        double scale = Math.Min(aw / vw, ah / vh);
        double ox = (aw - vw * scale) / 2, oy = (ah - vh * scale) / 2;

        dc.PushTranslate(ox, oy);
        dc.PushScale(scale, scale);

        DrawCoolingBank(dc, 58, false);  // left
        DrawCoolingBank(dc, 692, true);  // right (standby)
        DrawTank(dc);
        DrawCore(dc);
        DrawBushings(dc, v);
        if (v.HasOltc) DrawOltcBox(dc, v);
        DrawInfoOverlays(dc, v);
        DrawSideLabels(dc, v);

        dc.Pop(); // scale
        dc.Pop(); // translate
        return dc;
    }

    /// <summary>A plain phase-coloured pen (flat caps), optionally dashed, as the old <c>MakePhase</c> built it.</summary>
    private static VisualPen MakePhase(UiColour c, double w, VisualDashPattern dash = VisualDashPattern.None) =>
        new(c, w, Pattern: dash);

    private static void DrawCoolingBank(VisualDisplayList dc, double x, bool standby)
    {
        double opacity = standby ? 0.6 : 0.92;
        dc.PushOpacity(opacity);

        // Header / footer pipes
        dc.Rect(FinFill, PipePen, x, 146, 170, 7, 3, 3);
        dc.Rect(FinFill, PipePen, x, 330, 170, 7, 3, 3);

        // 11 fin tubes
        for (int i = 0; i < 11; i++)
            dc.Rect(FinFill, FinStroke, x + 4 + i * 14, 153, 8, 177, 3, 3);

        // Fan circle
        double fc = x + 85;
        dc.Ellipse(DarkBgFill, SlatePen, new ScreenPoint(fc, 355), 24, 24);
        VisualPen dashPen = MakePhase(standby ? SlateDark : CyanColour, 1.5, VisualDashPattern.Dash);
        dc.Ellipse(null, dashPen, new ScreenPoint(fc, 355), 16, 16);
        VisualFill label = standby ? TextDim : Cyan;
        Txt(dc, standby ? "STANDBY" : "ACTIVE", VisualTextWeight.SemiBold, 7, label, fc, 360, devicePps: false);

        // Connecting legs to tank
        bool left = x < 400;
        double tankEdge = left ? 240 : 680;
        double pipeEnd = left ? x + 170 : x;
        dc.Line(ConnectPen, new ScreenPoint(pipeEnd, 149), new ScreenPoint(tankEdge, 172));
        dc.Line(ConnectPen, new ScreenPoint(pipeEnd, 337), new ScreenPoint(tankEdge, 320));

        dc.Pop(); // opacity
    }

    private static void DrawTank(VisualDisplayList dc)
    {
        dc.Rect(TankFill, TankPen, 240, 112, 440, 246, 16, 16);
    }

    private static void DrawCore(VisualDisplayList dc)
    {
        // Top and bottom yokes
        dc.Rect(CoreFill, CorePen, 284, 145, 352, 16, 4, 4);
        dc.Rect(CoreFill, CorePen, 284, 309, 352, 16, 4, 4);

        string[] labels = ["A", "B", "C"];

        for (int i = 0; i < 3; i++)
        {
            double cx = LimbX[i];
            // Limb
            dc.Rect(CoreFill, CorePen, cx - 10, 145, 20, 180, 4, 4);

            // HV winding (outer ellipse, dash-dot)
            VisualPen hvPen = MakePhase(PhaseColours[i], 2.5, VisualDashPattern.DashDot);
            dc.Ellipse(null, hvPen, new ScreenPoint(cx, 234), 38, 76);

            // LV winding (inner ellipse, dashed, at 0.55 opacity)
            VisualPen lvPen = MakePhase(PhaseColours[i], 1.8, VisualDashPattern.Dash);
            dc.PushOpacity(0.55);
            dc.Ellipse(null, lvPen, new ScreenPoint(cx, 234), 22, 56);
            dc.Pop();

            // Phase label inside tank
            dc.PushOpacity(0.8);
            Txt(dc, labels[i], VisualTextWeight.Bold, 10, new VisualFill(PhaseColours[i]), cx, 170, devicePps: false);
            dc.Pop();

            // Bushing entry lines into tank
            VisualPen entryPen = MakePhase(PhaseColours[i], 2.5);
            dc.Line(entryPen, new ScreenPoint(cx, 112), new ScreenPoint(cx, 145));
            dc.Line(entryPen, new ScreenPoint(cx, 325), new ScreenPoint(cx, 358));
        }
    }

    private static void DrawBushings(VisualDisplayList dc, TransformerSchematicInput v)
    {
        string[] phases = ["A", "B", "C"];

        for (int i = 0; i < 3; i++)
        {
            double cx = LimbX[i];
            var phase = new VisualFill(PhaseColours[i]);
            // --- HV bushing (top) ---
            dc.Line(BushingStemHv, new ScreenPoint(cx, 112), new ScreenPoint(cx, 12));

            // Porcelain sheds
            double[] shedY = [32, 50, 65, 79, 91, 101];
            double[] shedRx = [24, 22, 19, 17, 15, 12];
            double[] shedRy = [6, 5, 5, 4, 4, 3];
            for (int s = 0; s < shedY.Length; s++)
                dc.Ellipse(ShedFill, null, new ScreenPoint(cx, shedY[s]), shedRx[s], shedRy[s]);

            // Terminal cap
            dc.Ellipse(phase, null, new ScreenPoint(cx, 9), 7, 7);

            // Phase label
            Txt(dc, phases[i], VisualTextWeight.Bold, 10, phase, cx + 16, 8, VisualTextAlign.Left);

            // Voltage badge: the phase-to-neutral voltage, V_LL / sqrt(3), identical on all three phases because the
            // power flow is balanced; before the first frame, the nominal phase voltage, in the same unit.
            double liveV = PhaseVoltageKv(v.HvKvLive, v.HvKvNom);
            dc.Rect(DarkBgFill, null, cx - 28, -20, 56, 16, 4, 4);
            Txt(dc, $"{liveV:F1} kV", VisualTextWeight.Bold, 9, phase, cx, -11);

            // --- LV bushing (bottom) ---
            dc.Line(BushingStem, new ScreenPoint(cx, 358), new ScreenPoint(cx, 446));
            double[] lvShedY = [375, 389, 401, 413];
            double[] lvShedRx = [18, 16, 14, 12];
            double[] lvShedRy = [4, 4, 3, 3];
            for (int s = 0; s < lvShedY.Length; s++)
                dc.Ellipse(ShedFill, null, new ScreenPoint(cx, lvShedY[s]), lvShedRx[s], lvShedRy[s]);

            dc.Ellipse(phase, null, new ScreenPoint(cx, 449), 6, 6);
            Txt(dc, phases[i].ToLowerInvariant(), VisualTextWeight.Bold, 10, phase, cx + 13, 448, VisualTextAlign.Left);

            double lvV = PhaseVoltageKv(v.LvKvLive, v.LvKvNom);   // phase-to-neutral, as the HV badge
            dc.Rect(DarkBgFill, null, cx - 24, 455, 48, 15, 4, 4);
            Txt(dc, $"{lvV:F1} kV", VisualTextWeight.Bold, 9, phase, cx, 464);
        }

        // Neutral earth symbol at centre LV
        dc.Line(EarthPen, new ScreenPoint(440, 358), new ScreenPoint(440, 378));
        dc.Line(EarthPen, new ScreenPoint(428, 378), new ScreenPoint(452, 378));
        dc.Line(EarthPen, new ScreenPoint(432, 384), new ScreenPoint(448, 384));
        dc.Line(EarthPen, new ScreenPoint(436, 390), new ScreenPoint(444, 390));
        Txt(dc, "N", VisualTextWeight.SemiBold, 8, TextDim, 454, 387, VisualTextAlign.Left);
    }

    private static void DrawOltcBox(VisualDisplayList dc, TransformerSchematicInput v)
    {
        dc.Rect(DarkBgFill, OltcBorderPen, 648, 162, 90, 125, 8, 8);
        Txt(dc, "OLTC", VisualTextWeight.Bold, 9, TextSlate, 693, 176);

        // Tap scale
        dc.Line(OltcScalePen, new ScreenPoint(668, 188), new ScreenPoint(668, 270));

        for (int i = 0; i < 17; i++)
        {
            double y = 188 + i * 5.1;
            double half = i % 4 == 0 ? 4 : 2;
            dc.Line(OltcTickPen, new ScreenPoint(668 - half, y), new ScreenPoint(668 + half, y));
        }

        // Tap pointer position
        double tapFrac = TapFraction(v.TapRatio, v.TapMin, v.TapMax);
        double tapY = 188 + tapFrac * 82;

        dc.Polygon(Cyan, new ScreenPoint(672, tapY), new ScreenPoint(685, tapY - 5), new ScreenPoint(685, tapY + 5));

        Txt(dc, $"{v.TapRatio:F3}", VisualTextWeight.Bold, 10, Cyan, 710, tapY, VisualTextAlign.Left);

        // OLTC connection to tank (dashed, at 0.6 opacity)
        dc.PushOpacity(0.6);
        dc.Line(OltcLinkPen, new ScreenPoint(680, 218), new ScreenPoint(648, 218));
        dc.Pop();
    }

    private static void DrawInfoOverlays(VisualDisplayList dc, TransformerSchematicInput v)
    {
        VisualFill loadColor = LoadingFill(v.LoadingPct);
        VisualFill thermalColor = ThermalFill(v.HotSpotC);

        // Top-right: load + rating
        dc.Rect(DarkBgFill, InfoBoxPen, 760, 54, 148, 55, 8, 8);
        Txt(dc, "LOAD", VisualTextWeight.SemiBold, 8, TextDim, 769, 66, VisualTextAlign.Left);
        Txt(dc, $"{v.LoadingPct:F1} %", VisualTextWeight.Bold, 14, loadColor, 769, 79, VisualTextAlign.Left);
        Txt(dc, "RATING", VisualTextWeight.SemiBold, 8, TextDim, 845, 66, VisualTextAlign.Left);
        Txt(dc, $"{v.RatingMva:F0} MVA", VisualTextWeight.Bold, 14, Cyan, 845, 79, VisualTextAlign.Left);

        // Bottom-right: temperatures
        dc.Rect(DarkBgFill, InfoBoxPen, 760, 392, 148, 82, 8, 8);
        Txt(dc, "TOP OIL", VisualTextWeight.SemiBold, 8, TextDim, 769, 404, VisualTextAlign.Left);
        Txt(dc, $"{v.TopOilC:F1} degC", VisualTextWeight.Bold, 13, thermalColor, 769, 417, VisualTextAlign.Left);
        Txt(dc, "HOT-SPOT", VisualTextWeight.SemiBold, 8, TextDim, 769, 437, VisualTextAlign.Left);
        Txt(dc, $"{v.HotSpotC:F1} degC", VisualTextWeight.Bold, 13, thermalColor, 769, 450, VisualTextAlign.Left);

        // Bottom-centre: power
        dc.Rect(DarkBgFill, InfoBoxPen, 302, 392, 200, 82, 8, 8);
        Txt(dc, "ACTIVE POWER", VisualTextWeight.SemiBold, 8, TextDim, 402, 406);
        Txt(dc, $"{v.FlowMw:F1} MW", VisualTextWeight.Bold, 18, loadColor, 402, 422);
        Txt(dc, $"TAP {v.TapRatio:F3}", VisualTextWeight.SemiBold, 9, TextSlate, 402, 450);
        string hvLv = $"{v.HvKvNom:F0} / {v.LvKvNom:F0} kV";
        Txt(dc, hvLv, VisualTextWeight.SemiBold, 8, TextDim, 402, 462);
    }

    private static void DrawSideLabels(VisualDisplayList dc, TransformerSchematicInput v)
    {
        Txt(dc, $"{v.HvKvNom:F0} kV", VisualTextWeight.Bold, 10, TextSlate, 14, 91, VisualTextAlign.Left);
        Txt(dc, "PRIMARY (HV)", VisualTextWeight.SemiBold, 8, TextDim, 14, 104, VisualTextAlign.Left);
        Txt(dc, $"{v.LvKvNom:F0} kV", VisualTextWeight.Bold, 10, TextSlate, 14, 371, VisualTextAlign.Left);
        Txt(dc, "SECONDARY (LV)", VisualTextWeight.SemiBold, 8, TextDim, 14, 384, VisualTextAlign.Left);
    }

    /// <summary>
    /// A text call, as the old <c>Txt</c> helper made it: centred unless told otherwise; the device scale unless the old
    /// call passed a literal 1 (<paramref name="devicePps"/> false: the cooling-bank and core labels).
    /// </summary>
    private static void Txt(VisualDisplayList dc, string text, VisualTextWeight weight, double size, VisualFill fill,
        double x, double y, VisualTextAlign align = VisualTextAlign.Centre, bool devicePps = true) =>
        dc.Text(text, weight, size, fill, new ScreenPoint(x, y), align, devicePps);
}
