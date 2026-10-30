using Avalonia.Media;
using Avalonia.Media.Immutable;
using GridSim.Ui.Presentation;
using GridSim.Ui.Schematic;

namespace GridSim.App.Map;

/// <summary>
/// The pens, brushes and typefaces the map draws with (UI programme Phase 1.5): the WPF <c>FastSchematic</c>'s frozen
/// pens and brushes, value for value, as immutable Avalonia ones. Every colour comes from the shared
/// <see cref="ColourRamps"/> (the same functions and bucket centres the WPF pens are built from), or is one of the WPF
/// hex literals, so the two maps tint every corridor, ring and marker with the same four bytes.
/// <para>
/// One translation note: a WPF pen has separate start and end caps; an Avalonia pen has one cap for both ends. Every
/// WPF map pen that sets caps sets both to Round, so nothing is lost.
/// </para>
/// </summary>
public static class MapPaint
{
    // ---- conversion from the neutral colour token ----

    /// <summary>The Avalonia colour for a neutral colour token: the same alpha, red, green and blue bytes.</summary>
    public static Color ToColor(UiColour c) => Color.FromArgb(c.A, c.R, c.G, c.B);

    /// <summary>Parse <c>#RRGGBB</c> / <c>#AARRGGBB</c> with the shared parser (as the WPF <c>Palette.FromHex</c>).</summary>
    public static Color FromHex(string hex) => ToColor(ColourRamps.FromHex(hex));

    private static readonly Dictionary<Color, ImmutableSolidColorBrush> FrozenCache = new();

    /// <summary>
    /// The cached immutable brush for <paramref name="c"/>: repeated calls return the SAME instance, as the WPF
    /// <c>Palette.Frozen</c> does, so markers batch into one geometry per brush.
    /// </summary>
    public static ImmutableSolidColorBrush Frozen(Color c)
    {
        lock (FrozenCache)
        {
            if (FrozenCache.TryGetValue(c, out ImmutableSolidColorBrush? cached)) return cached;
            var b = new ImmutableSolidColorBrush(c);
            FrozenCache[c] = b;
            return b;
        }
    }

    /// <summary>The cached immutable brush for a neutral colour token.</summary>
    public static ImmutableSolidColorBrush Frozen(UiColour c) => Frozen(ToColor(c));

    /// <summary>
    /// The WPF <c>FastSchematic.MakePen</c>: a brush of <paramref name="c"/> at <paramref name="opacity"/>, round caps
    /// and a round join.
    /// </summary>
    public static ImmutablePen MakePen(Color c, double thickness, double opacity)
        => new(new ImmutableSolidColorBrush(c, opacity), thickness, null, PenLineCap.Round, PenLineJoin.Round);

    /// <summary>A plain pen (flat caps, mitre join: the WPF <c>new Pen(brush, thickness)</c> defaults).</summary>
    public static ImmutablePen PlainPen(IImmutableBrush brush, double thickness) => new(brush, thickness);

    // ---- the WPF FastSchematic's frozen pens and brushes, values unchanged ----

    /// <summary>Roundel fill.</summary>
    public static readonly ImmutableSolidColorBrush DarkFill = Frozen(FromHex("#0E141B"));

    /// <summary>Transformer ties (thin grey).</summary>
    public static readonly ImmutablePen TxPen = MakePen(FromHex("#3A4756"), 1.4, 0.6);

    /// <summary>Label text.</summary>
    public static readonly ImmutableSolidColorBrush LabelBrush = Frozen(FromHex("#C7D2DD"));

    /// <summary>The WPF label typeface: "Bahnschrift, Segoe UI", semi-bold (the app's default font when neither is present).</summary>
    public static readonly Typeface LabelFace = new(new FontFamily("Bahnschrift, Segoe UI"), FontStyle.Normal, FontWeight.SemiBold);

    /// <summary>The WPF boundary-label typeface: the same families, bold.</summary>
    public static readonly Typeface BoundaryFace = new(new FontFamily("Bahnschrift, Segoe UI"), FontStyle.Normal, FontWeight.Bold);

    /// <summary>The ring thickness of an EHV roundel (WPF RingPenEhv) and of a lower-voltage one (WPF RingPenLv).</summary>
    public const double RingThicknessEhv = 2.6, RingThicknessLv = 1.8;

    /// <summary>Generation fleet diamond outline.</summary>
    public static readonly ImmutablePen FleetOutlinePen = MakePen(FromHex("#0B0F14"), 0.8, 0.9);

    /// <summary>The GB coastline backdrop: a faint fill and outline.</summary>
    public static readonly ImmutableSolidColorBrush CoastFill = Frozen(Color.FromArgb(36, 70, 92, 110));

    /// <summary>The coastline's outline brush (the WPF CoastPen's: #33485C at opacity 0.8).</summary>
    public static readonly ImmutableSolidColorBrush CoastPenBrush = new(FromHex("#33485C"), 0.8);

    /// <summary>The coastline's outline (the WPF CoastPen: 1.0 px, round caps and join).</summary>
    public static readonly ImmutablePen CoastPen = new(CoastPenBrush, 1.0, null, PenLineCap.Round, PenLineJoin.Round);

    /// <summary>The boundary label panel.</summary>
    public static readonly ImmutableSolidColorBrush BoundaryLabelBg = Frozen(Color.FromArgb(214, 12, 18, 26));

    /// <summary>DNO outline colour (amber) and GSP-region outline colour (cyan).</summary>
    public static readonly Color DnoColor = FromHex("#E0B341"), GspColor = FromHex("#4FC3F7");

    /// <summary>The outline brushes carry their own alpha, as in WPF.</summary>
    public static readonly ImmutableSolidColorBrush DnoPenBrush = Frozen(Color.FromArgb(217, DnoColor.R, DnoColor.G, DnoColor.B));

    /// <summary>The GSP-region outline brush.</summary>
    public static readonly ImmutableSolidColorBrush GspPenBrush = Frozen(Color.FromArgb(150, GspColor.R, GspColor.G, GspColor.B));

    /// <summary>Asset marker outline.</summary>
    public static readonly ImmutablePen AssetOutlinePen = MakePen(FromHex("#0B0F14"), 0.7, 0.85);

    /// <summary>The geography key panel and its border.</summary>
    public static readonly ImmutableSolidColorBrush GeoLegendBg = Frozen(Color.FromArgb(210, 12, 18, 26));

    /// <summary>The geography key panel's border.</summary>
    public static readonly ImmutablePen GeoLegendBorder = MakePen(FromHex("#33414F"), 1.0, 1.0);

    /// <summary>The minimap panel, its border, its dots and its viewport rectangle.</summary>
    public static readonly ImmutableSolidColorBrush MinimapBg = Frozen(Color.FromArgb(196, 10, 16, 22));

    /// <summary>The minimap border.</summary>
    public static readonly ImmutablePen MinimapBorder = MakePen(FromHex("#33414F"), 1.0, 1.0);

    /// <summary>The minimap's node dots.</summary>
    public static readonly ImmutableSolidColorBrush MinimapDot = Frozen(FromHex("#5A6B7B"));

    /// <summary>The minimap's viewport rectangle.</summary>
    public static readonly ImmutablePen MinimapViewPen = MakePen(FromHex("#E0B341"), 1.4, 1.0);

    /// <summary>The stacked-asset cluster halo and its badge.</summary>
    public static readonly ImmutableSolidColorBrush FanHaloFill = Frozen(Color.FromArgb(28, 90, 120, 150));

    /// <summary>The cluster halo outline.</summary>
    public static readonly ImmutablePen FanHaloPen = MakePen(Color.FromArgb(150, 120, 150, 175), 1.0, 1.0);

    /// <summary>The cluster badge background.</summary>
    public static readonly ImmutableSolidColorBrush FanBadgeBg = Frozen(Color.FromArgb(220, 14, 20, 28));

    // ---- the corridor and flow pens, one per colour bin, per colour mode ----

    private const int Buckets = SchematicView.LoadingBuckets;

    /// <summary>Loading corridors: subtle base lines (the bright marching dashes carry the eye).</summary>
    public static readonly ImmutablePen[] CorridorPens = BuildCorridorPens();

    /// <summary>Loading flow dashes.</summary>
    public static readonly ImmutablePen[] FlowPens = BuildFlowPens();

    /// <summary>Voltage-mode corridors and flow dashes.</summary>
    public static readonly ImmutablePen[] VoltageCorridorPens = BuildVoltagePens(2.4, 0.5), VoltageFlowPens = BuildVoltagePens(2.8, 0.95);

    /// <summary>Condition-mode corridors and flow dashes (GDA-bridge overlay).</summary>
    public static readonly ImmutablePen[] ConditionCorridorPens = BuildRampPens(ColourRamps.Condition, 2.4, 0.5),
        ConditionFlowPens = BuildRampPens(ColourRamps.Condition, 2.8, 0.95);

    /// <summary>Stress-mode corridors and flow dashes (GDA-bridge overlay).</summary>
    public static readonly ImmutablePen[] StressCorridorPens = BuildRampPens(ColourRamps.Stress, 2.4, 0.5),
        StressFlowPens = BuildRampPens(ColourRamps.Stress, 2.8, 0.95);

    /// <summary>The corridor pens for <paramref name="mode"/> (the WPF CorridorPensFor).</summary>
    public static ImmutablePen[] CorridorPensFor(MapColourMode mode) => mode switch
    {
        MapColourMode.Voltage => VoltageCorridorPens,
        MapColourMode.Condition => ConditionCorridorPens,
        MapColourMode.Stress => StressCorridorPens,
        _ => CorridorPens,
    };

    /// <summary>The flow-dash pens for <paramref name="mode"/> (the WPF FlowPensFor).</summary>
    public static ImmutablePen[] FlowPensFor(MapColourMode mode) => mode switch
    {
        MapColourMode.Voltage => VoltageFlowPens,
        MapColourMode.Condition => ConditionFlowPens,
        MapColourMode.Stress => StressFlowPens,
        _ => FlowPens,
    };

    /// <summary>
    /// The colour a corridor of colour bin <paramref name="bucket"/> is drawn in for <paramref name="mode"/> (before its
    /// pen's opacity): the same ramp and bin centre the pens above are built from. Tests use it to read the map back.
    /// </summary>
    public static UiColour CorridorColour(MapColourMode mode, int bucket) => mode switch
    {
        MapColourMode.Voltage => ColourRamps.Voltage(SchematicView.VoltageOfBucket(bucket)),
        MapColourMode.Condition => ColourRamps.Condition(bucket / (double)(Buckets - 1)),
        MapColourMode.Stress => ColourRamps.Stress(bucket / (double)(Buckets - 1)),
        _ => ColourRamps.Loading(SchematicView.BucketFrac(bucket)),
    };

    /// <summary>The opacity of a corridor pen of bin <paramref name="bucket"/> for <paramref name="mode"/>.</summary>
    public static double CorridorOpacity(MapColourMode mode, int bucket)
        => mode == MapColourMode.Loading ? 0.42 + 0.22 * (bucket / (double)(Buckets - 1)) : 0.5;

    // ---- UI programme Phase 5d (2026-10-21): the line route layer ----

    /// <summary>The line route layer's neutral colour (no solved state, or the carried line is not on the map).</summary>
    public static readonly Color LineRouteNeutral = FromHex("#C39BD3");

    /// <summary>The line route pen's width, pixels: thin, under the 2.4 px corridors.</summary>
    public const double LineRouteThickness = 1.5;

    /// <summary>The line route dash (in pen widths, as Avalonia scales a dash): 4 on, 3 off.</summary>
    public static readonly ImmutableDashStyle LineRouteDash = new(new double[] { 4, 3 }, 0);

    private static readonly ImmutablePen LineRouteNeutralPen = new(Frozen(LineRouteNeutral), LineRouteThickness, LineRouteDash, PenLineCap.Flat, PenLineJoin.Round);

    private static readonly ImmutablePen[] LineRoutePens = Enumerable.Range(0, Buckets)
        .Select(i => new ImmutablePen(Frozen(ToColor(CorridorColour(MapColourMode.Loading, i))), LineRouteThickness, LineRouteDash, PenLineCap.Flat, PenLineJoin.Round))
        .ToArray();

    /// <summary>The colour of a route of loading bin <paramref name="bucket"/> (null: the neutral colour), opaque.</summary>
    public static Color LineRouteColour(int? bucket) => bucket is int b ? ToColor(CorridorColour(MapColourMode.Loading, b)) : LineRouteNeutral;

    /// <summary>The dashed pen of a route of loading bin <paramref name="bucket"/> (null: neutral).</summary>
    public static ImmutablePen LineRoutePen(int? bucket) => bucket is int b ? LineRoutePens[b] : LineRouteNeutralPen;

    private static ImmutablePen[] BuildCorridorPens()
    {
        var pens = new ImmutablePen[Buckets];
        for (int i = 0; i < Buckets; i++)
            pens[i] = MakePen(ToColor(ColourRamps.Loading(SchematicView.BucketFrac(i))), 2.4, 0.42 + 0.22 * (i / (double)(Buckets - 1)));
        return pens;
    }

    private static ImmutablePen[] BuildFlowPens()
    {
        var pens = new ImmutablePen[Buckets];
        for (int i = 0; i < Buckets; i++)
            pens[i] = MakePen(ToColor(ColourRamps.Loading(SchematicView.BucketFrac(i))), 2.8, 0.95);
        return pens;
    }

    private static ImmutablePen[] BuildVoltagePens(double thickness, double opacity)
    {
        var pens = new ImmutablePen[Buckets];
        for (int i = 0; i < Buckets; i++)
            pens[i] = MakePen(ToColor(ColourRamps.Voltage(SchematicView.VoltageOfBucket(i))), thickness, opacity);
        return pens;
    }

    private static ImmutablePen[] BuildRampPens(Func<double, UiColour> ramp, double thickness, double opacity)
    {
        var pens = new ImmutablePen[Buckets];
        for (int i = 0; i < Buckets; i++)
            pens[i] = MakePen(ToColor(ramp(i / (double)(Buckets - 1))), thickness, opacity);
        return pens;
    }
}
