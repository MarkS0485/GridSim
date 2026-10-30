using System.Globalization;
using System.Text.Json;
using GridSim.Core.IO.Json.Lossless;
using GridSim.Core.IO.Json.Overlay;
using GridSim.Core.Model;

namespace GridSim.Ui.Assets.Overlay;

// =====================================================================================================================
// UI programme Phase 5d (2026-10-21; plan Phase 5 "Towers/pylons/conductors: conductorType, towerType
// (+conductorPosition), lineRoute -> span/tower children (geo or chainage)", ASSETS plan 3.3 "route/tower/span tables").
//
// The one place that reads a line route's spans the same way for its OVERVIEW page (OverlayKindPages.Routes.cs), its
// CHECK notes and the NETWORK map's "Line routes" layer (LineRouteMap). It works on the overlay records exactly as
// carried (AssetOverlayRecord), never on anything the solver holds, and writes nothing.
//
// THE SPAN ORDER. A route's spans are its child span records. When EVERY span writes its start chainage (chainageM) the
// spans are taken in chainage order (a stable sort: two spans at the same chainage keep the order the file writes
// them). Otherwise they are taken in the order the file writes them, and the page says so: without a chainage on every
// span there is nothing else to order them by.
//
// THE TOWERS. The overlay has no tower record of its own: a span runs from one tower to the next and places only the
// tower at its FAR end (its latitudeDeg / longitudeDeg, and its tower design through the 'tower' reference). So towers
// are numbered along the route in span order: T0 is the route's start, and Tn is the far tower of span n. Span n runs
// from T(n-1) to Tn. A route is drawn on the map through the far towers that have a geographic position (both latitude
// and longitude), in span order; it needs at least two of them.
//
// THE LENGTHS. The total route length is the sum of the span lengths (lengthM), added in span order, display only. It
// is compared with the carried line's lengthKm when the line record writes one (the loaders keep lengthKm 0 as
// "unknown", so 0 means not written); a difference of more than 1% of the line's length is a display-only CHECK note,
// never an error: the solver uses the line's authored r, x and b, and nothing here changes them.
// =====================================================================================================================

/// <summary>One span of a line route, in route order, with its towers and its values as the file writes them.</summary>
/// <param name="Record">The span record, exactly as carried.</param>
/// <param name="Number">The span's place along the route, 1 for the first.</param>
/// <param name="FromTower">The tower it starts at ("T0" for the route's start).</param>
/// <param name="ToTower">The tower at its far end ("T1" for the first span).</param>
/// <param name="LengthM">Its length, metres, or null when the record writes none.</param>
/// <param name="ChainageM">The chainage of its start, metres, or null when not written.</param>
/// <param name="LatitudeDeg">The latitude of its far tower, degrees north, or null when not written.</param>
/// <param name="LongitudeDeg">The longitude of its far tower, degrees east, or null when not written.</param>
public sealed record LineRouteSpan(AssetOverlayRecord Record, int Number, string FromTower, string ToTower,
    double? LengthM, double? ChainageM, double? LatitudeDeg, double? LongitudeDeg)
{
    /// <summary>True when the far tower has a geographic position (both latitude and longitude).</summary>
    public bool HasGeo => LatitudeDeg is not null && LongitudeDeg is not null;

    /// <summary>The span record id.</summary>
    public string Id => Record.Id;
}

/// <summary>The spans of one line route in route order, and how they were ordered.</summary>
/// <param name="Spans">The spans, in route order.</param>
/// <param name="ByChainage">True when every span writes chainageM and the order is by chainage; false when the order
/// is the order the file writes them.</param>
public sealed record LineRouteSpans(IReadOnlyList<LineRouteSpan> Spans, bool ByChainage)
{
    /// <summary>How many spans write a chainage.</summary>
    public int WithChainage => Spans.Count(s => s.ChainageM is not null);

    /// <summary>How many spans place their far tower geographically.</summary>
    public int WithGeo => Spans.Count(s => s.HasGeo);

    /// <summary>How many spans write a length.</summary>
    public int WithLength => Spans.Count(s => s.LengthM is not null);

    /// <summary>The sum of the written span lengths, metres, added in route order (null when no span writes one).</summary>
    public double? TotalM
    {
        get
        {
            if (WithLength == 0) return null;
            double sum = 0;
            foreach (LineRouteSpan s in Spans)
                if (s.LengthM is double l) sum += l;
            return sum;
        }
    }
}

/// <summary>
/// The line route rules shared by the route's page, its CHECK notes and the map layer (Phase 5d). Pure: the same records
/// give the same answer.
/// </summary>
public static class LineRoutes
{
    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    /// <summary>The overlay kind of a line route.</summary>
    public const string RouteKind = "lineRoute";

    /// <summary>The overlay kind of a span.</summary>
    public const string SpanKind = "span";

    /// <summary>The fraction of the line's length by which the route's length may differ before a CHECK note: 1%.</summary>
    public const double LengthTolerance = 0.01;

    /// <summary>The gap or overlap between consecutive spans' chainages above which they do not chain: 0.01 m (a
    /// centimetre, well above the rounding of sums of metre values written to a few decimals).</summary>
    public const double ChainageToleranceM = 0.01;

    /// <summary>
    /// The spans of a route in route order (see the file header): <paramref name="children"/> are the route's child
    /// records in the order the overlay holds them; only span records count.
    /// </summary>
    public static LineRouteSpans Order(IEnumerable<AssetOverlayRecord> children)
    {
        ArgumentNullException.ThrowIfNull(children);
        List<AssetOverlayRecord> spans = children.Where(c => string.Equals(c.Kind, SpanKind, StringComparison.Ordinal)).ToList();
        bool byChainage = spans.Count > 0 && spans.All(s => Number(s, "chainageM") is not null);
        // OrderBy is a stable sort: equal chainages keep the order the file writes them.
        IEnumerable<AssetOverlayRecord> ordered = byChainage ? spans.OrderBy(s => Number(s, "chainageM")!.Value) : spans;
        var list = new List<LineRouteSpan>(spans.Count);
        int n = 0;
        foreach (AssetOverlayRecord s in ordered)
        {
            n++;
            list.Add(new LineRouteSpan(s, n, Tower(n - 1), Tower(n), Number(s, "lengthM"), Number(s, "chainageM"),
                Number(s, "latitudeDeg"), Number(s, "longitudeDeg")));
        }
        return new LineRouteSpans(list, byChainage);
    }

    /// <summary>The spans of route <paramref name="routeId"/> of <paramref name="overlay"/>, in route order.</summary>
    public static LineRouteSpans Order(CaseAssetOverlay overlay, string routeId)
    {
        ArgumentNullException.ThrowIfNull(overlay);
        ArgumentNullException.ThrowIfNull(routeId);
        return Order(overlay.Records.Where(r => string.Equals(r.Parent, routeId, StringComparison.Ordinal)));
    }

    /// <summary>The name of tower <paramref name="n"/> along a route: "T0" (the start), "T1", ...</summary>
    public static string Tower(int n) => "T" + n.ToString(Inv);

    /// <summary>The words of the span order ("by chainage (every span writes chainageM)", ...).</summary>
    public static string OrderWords(LineRouteSpans s)
    {
        ArgumentNullException.ThrowIfNull(s);
        if (s.Spans.Count == 0) return "no spans";
        return s.ByChainage ? "by chainage (every span writes chainageM)"
            : "as the file writes them (chainageM is written on " + s.WithChainage.ToString(Inv) + " of " + s.Spans.Count.ToString(Inv) + " spans)";
    }

    /// <summary>How the route's towers are positioned, in words: geographic, chainage, both, mixed or none.</summary>
    public static string PositionWords(LineRouteSpans s)
    {
        ArgumentNullException.ThrowIfNull(s);
        int n = s.Spans.Count, g = s.WithGeo, c = s.WithChainage;
        if (n == 0) return "none (the route has no spans)";
        if (g == n && c == n) return "geographic (latitude and longitude of every far tower) and chainage on every span";
        if (g == n) return "geographic (latitude and longitude of every far tower)";
        if (c == n && g == 0) return "chainage along the route only (no geographic position)";
        if (g == 0 && c == 0) return "none (no span writes a chainage or a geographic position)";
        return "mixed: a geographic position on " + g.ToString(Inv) + " of " + n.ToString(Inv) + " spans, a chainage on "
            + c.ToString(Inv) + " of " + n.ToString(Inv) + " spans";
    }

    /// <summary>
    /// Why the route is not drawn on the map's Line routes layer, or null when it is (at least two far towers have a
    /// geographic position).
    /// </summary>
    public static string? NotDrawnReason(LineRouteSpans s)
    {
        ArgumentNullException.ThrowIfNull(s);
        int g = s.WithGeo;
        if (g >= 2) return null;
        if (s.Spans.Count == 0) return "no spans";
        if (g == 1) return "only one tower has a geographic position (a polyline needs two)";
        return s.WithChainage > 0 ? "chainage only: no tower has a geographic position" : "no tower has a position";
    }

    /// <summary>
    /// The carried line's length as its record writes it, km, or null when it writes none (the loaders keep lengthKm 0
    /// as "unknown", so a length whose bits are those of 0 counts as not written).
    /// </summary>
    public static double? LineLengthKm(Branch? line) =>
        line is null || BitConverter.DoubleToInt64Bits(line.LengthKm) == 0 ? null : line.LengthKm;

    /// <summary>
    /// The route's length as compared with the line, km: the sum of its span lengths when any span writes a length, else
    /// the route's own routeLengthKm when written, else null. <paramref name="source"/> says which ("the sum of the 3
    /// span lengths", "the route's routeLengthKm").
    /// </summary>
    public static double? RouteLengthKm(AssetOverlayRecord route, LineRouteSpans s, out string source)
    {
        ArgumentNullException.ThrowIfNull(route);
        ArgumentNullException.ThrowIfNull(s);
        if (s.TotalM is double m)
        {
            source = "the sum of the " + s.WithLength.ToString(Inv) + (s.WithLength == 1 ? " span length" : " span lengths");
            return m / 1000.0;
        }
        if (Number(route, "routeLengthKm") is double k)
        {
            source = "the route's routeLengthKm";
            return k;
        }
        source = "nothing (no span length and no routeLengthKm)";
        return null;
    }

    /// <summary>The relative difference (route - line) / line, or null when either is unknown or the line length is 0.</summary>
    public static double? Difference(double? routeKm, double? lineKm) =>
        routeKm is double r && lineKm is double l && l != 0 ? (r - l) / l : null;

    /// <summary>True when a relative difference is beyond <see cref="LengthTolerance"/> (strictly more than 1%).</summary>
    public static bool Beyond(double? difference) => difference is double d && Math.Abs(d) > LengthTolerance;

    /// <summary>A relative difference as a signed percentage with two decimals ("+25.00", "-0.50").</summary>
    public static string Percent(double difference) => (difference >= 0 ? "+" : "") + (difference * 100).ToString("F2", Inv);

    /// <summary>
    /// The continuity problems of a route's spans, in words (display only): with every span chained by chainage, each pair
    /// of consecutive spans whose first does not end (chainageM + lengthM) where the next starts, to within
    /// <see cref="ChainageToleranceM"/>; with chainage on some spans only, one problem saying so. Empty when the spans
    /// chain, or when no span writes a chainage (the spans are then taken as a chain in the order written).
    /// </summary>
    public static IReadOnlyList<string> ContinuityProblems(LineRouteSpans s)
    {
        ArgumentNullException.ThrowIfNull(s);
        var list = new List<string>();
        if (s.Spans.Count == 0) return list;
        if (!s.ByChainage)
        {
            if (s.WithChainage > 0)
                list.Add("chainageM is written on " + s.WithChainage.ToString(Inv) + " of " + s.Spans.Count.ToString(Inv)
                    + " spans, so the spans cannot be chained by chainage and are taken in the order the file writes them");
            return list;
        }
        for (int i = 0; i + 1 < s.Spans.Count; i++)
        {
            LineRouteSpan a = s.Spans[i], b = s.Spans[i + 1];
            if (a.LengthM is not double len) continue;   // a span with no length (refused by the checker) cannot be chained
            double end = a.ChainageM!.Value + len, next = b.ChainageM!.Value;
            double gap = next - end;
            if (Math.Abs(gap) <= ChainageToleranceM) continue;
            list.Add("span '" + a.Id + "' (" + a.FromTower + " to " + a.ToTower + ") ends at chainage " + N(end) + " m (chainageM "
                + N(a.ChainageM!.Value) + " + lengthM " + N(len) + ") but span '" + b.Id + "' starts at chainage " + N(next) + " m: "
                + (gap > 0 ? "a gap of " + N(gap) : "an overlap of " + N(-gap)) + " m");
        }
        return list;
    }

    // A double as the shortest text that reads back to the same bits (the way the case files write numbers).
    internal static string N(double v) => JsonLiteral.FormatNumber(v);

    // The value of number parameter 'name' (the {v, unit, prov} form's v), or null when absent or not a number.
    private static double? Number(AssetOverlayRecord r, string name)
    {
        if (r.Params.ValueKind != JsonValueKind.Object || !r.Params.TryGetProperty(name, out JsonElement v)) return null;
        if (v.ValueKind == JsonValueKind.Object && v.TryGetProperty("v", out JsonElement inner)) v = inner;
        return v.ValueKind == JsonValueKind.Number ? v.GetDouble() : null;
    }
}
