using System.Globalization;
using System.IO;
using System.Text.Json;

namespace GridSim.Ui.Geography;

/// <summary>
/// A case's own map geography: the land outline (its coastline and land shading) and, optionally, its first-level
/// regions (states, provinces, counties), read from a <c>geography.json</c> file (format <c>gda-geography/1</c>) that
/// sits beside the case's <c>buses.json</c>. Without such a file the map draws exactly what it always has: the built-in
/// GB coastline and the GB DNO/GSP layers, in the fixed GB box.
///
/// <para>The file carries its projection, the same lon/lat-to-map affine that placed every bus x/y in that case:
/// <c>x = m + (1-2m)(lon-west)/(east-west)</c>, <c>y = m + (1-2m)(lat-south)/(north-south)</c>, each clamped to 0..1,
/// with north at y = 1. A bus is drawn at world (x, 1 - y), so every point here is turned into world coordinates the
/// same way: x west to east, y north-down.</para>
///
/// <para>Each point is written as four numbers in the order the file's <c>point_layout</c> names, which must be exactly
/// lon, lat, x, y. The world point is computed here from lon/lat by the declared projection (so the outline and the
/// buses follow one formula), and the x/y the file wrote are checked against it: a point whose written x or y differs
/// from the formula by more than <see cref="PointTolerance"/> means the file and its projection disagree, and the whole
/// file is refused.</para>
///
/// <para>Fail loud: a file that is present but cannot be used is never drawn in part. <see cref="TryLoad"/> gives no
/// geography and a reason naming the file and the fault, and the map falls back to the GB drawing with that reason in
/// the status bar, so a bad file can never leave a blank or half-drawn map without a word.</para>
///
/// <para>Like the GB coastline, this is public admin geography, not grid data. It is read from the case folder at run
/// time and nothing of it is bundled.</para>
/// </summary>
public sealed class CaseGeography
{
    /// <summary>The file name looked for in a case folder.</summary>
    public const string FileName = "geography.json";

    /// <summary>The only format this reader accepts.</summary>
    public const string Format = "gda-geography/1";

    /// <summary>The only projection kind this reader accepts.</summary>
    public const string ProjectionKind = "lonlat-affine";

    /// <summary>
    /// The largest difference allowed between a point's written x (or y) and the value its lon/lat give by the declared
    /// projection: 1e-4 of the map's width (or height). Files round x/y to 5 decimals (an error of at most 5e-6), so an
    /// honest file passes with a margin of 20; a file whose points were placed by any other projection fails by far more.
    /// </summary>
    public const double PointTolerance = 1e-4;

    /// <summary>One named area (a country's land, or one region) and its rings in world coordinates.</summary>
    public sealed record Area(string Name, string Code, WorldPoint[][] Rings);

    /// <summary>The declared projection box and margin (degrees; margin a fraction of the map, 0 &lt;= m &lt; 0.5).</summary>
    public sealed record Projection(double South, double West, double North, double East, double Margin);

    /// <summary>The projection every point (and every bus of the case) was placed with.</summary>
    public Projection Box { get; }

    /// <summary>The land areas, at least one, with at least one ring among them (an area may have none).</summary>
    public IReadOnlyList<Area> Land { get; }

    /// <summary>The first-level regions (may be empty: the file need not carry any).</summary>
    public IReadOnlyList<Area> Regions { get; }

    /// <summary>The land names joined with " / " (for a header, e.g. "India").</summary>
    public string LandName { get; }

    /// <summary>The number of land points and region points read (for a status line).</summary>
    public int LandPoints { get; }

    /// <inheritdoc cref="LandPoints"/>
    public int RegionPoints { get; }

    private CaseGeography(Projection box, List<Area> land, List<Area> regions, int landPoints, int regionPoints)
    {
        Box = box;
        Land = land;
        Regions = regions;
        LandPoints = landPoints;
        RegionPoints = regionPoints;
        LandName = string.Join(" / ", land.Select(a => a.Name).Where(n => n.Length > 0));
    }

    /// <summary>
    /// The world point of a lon/lat by this case's projection: the affine, clamped to 0..1, then y turned north-down
    /// (world y = 1 - y), exactly as a bus at that position is drawn.
    /// </summary>
    public WorldPoint WorldOf(double lonDeg, double latDeg)
    {
        (double x, double y) = Project(Box, lonDeg, latDeg);
        return new WorldPoint(x, 1.0 - y);
    }

    /// <summary>
    /// Read <c>geography.json</c> from <paramref name="caseDir"/>. Returns true with the geography when the file is present
    /// and valid; false with a null <paramref name="problem"/> when there is no such file (the case simply has none, and
    /// the GB drawing applies); false with <paramref name="problem"/> set ("geography.json: reason") when the file is
    /// present but malformed in any way. A null or empty folder (a replay, a bridge, a single MATPOWER file) has none.
    /// </summary>
    public static bool TryLoad(string? caseDir, out CaseGeography? geography, out string? problem)
    {
        geography = null;
        problem = null;
        if (string.IsNullOrEmpty(caseDir)) return false;
        string path = Path.Combine(caseDir, FileName);
        if (!File.Exists(path)) return false;
        try
        {
            geography = Parse(File.ReadAllText(path));
            return true;
        }
        catch (Exception ex) when (ex is JsonException or FormatException or IOException or UnauthorizedAccessException
                                       or InvalidOperationException or KeyNotFoundException)
        {
            // Never a partial drawing: the whole file is refused and the reason is handed back to be shown.
            geography = null;
            problem = $"{FileName}: {ex.Message}";
            return false;
        }
    }

    /// <summary>
    /// Parse and check a <c>gda-geography/1</c> document. Throws <see cref="FormatException"/> naming the first fault
    /// (or <see cref="JsonException"/> for text that is not JSON). Public so a test can feed it a document directly.
    /// </summary>
    public static CaseGeography Parse(string json)
    {
        using JsonDocument doc = JsonDocument.Parse(json);
        JsonElement root = doc.RootElement;
        if (root.ValueKind != JsonValueKind.Object) throw new FormatException("the document is not a JSON object");

        string format = RequireString(root, "format", "the document");
        if (!string.Equals(format, Format, StringComparison.Ordinal))
            throw new FormatException($"format is \"{format}\"; only \"{Format}\" can be read");

        Projection box = ReadProjection(Require(root, "projection", "the document"));
        CheckPointLayout(Require(root, "point_layout", "the document"));

        (List<Area> land, int landPoints) = ReadAreas(Require(root, "land", "the document"), "land", box, codeProp: null);
        if (land.Count == 0) throw new FormatException("land has no areas: the file must carry at least one land outline");
        if (land.All(a => a.Rings.Length == 0))
            throw new FormatException("land has no rings in any area: the file must carry at least one land outline");

        // Regions are optional; when present they are checked as strictly as the land.
        List<Area> regions = new();
        int regionPoints = 0;
        if (root.TryGetProperty("regions", out JsonElement regEl) && regEl.ValueKind != JsonValueKind.Null)
            (regions, regionPoints) = ReadAreas(regEl, "regions", box, codeProp: "iso");

        return new CaseGeography(box, land, regions, landPoints, regionPoints);
    }

    // ---- the projection ----

    private static Projection ReadProjection(JsonElement p)
    {
        if (p.ValueKind != JsonValueKind.Object) throw new FormatException("projection is not an object");
        string kind = RequireString(p, "kind", "projection");
        if (!string.Equals(kind, ProjectionKind, StringComparison.Ordinal))
            throw new FormatException($"projection kind is \"{kind}\"; only \"{ProjectionKind}\" can be drawn");

        double south = RequireFinite(p, "south", "projection");
        double west = RequireFinite(p, "west", "projection");
        double north = RequireFinite(p, "north", "projection");
        double east = RequireFinite(p, "east", "projection");
        double margin = RequireFinite(p, "margin", "projection");

        if (south < -90 || north > 90) throw new FormatException($"projection latitudes {Num(south)}..{Num(north)} are outside -90..90");
        if (west < -180 || east > 180) throw new FormatException($"projection longitudes {Num(west)}..{Num(east)} are outside -180..180");
        if (!(north > south)) throw new FormatException($"projection north ({Num(north)}) is not above south ({Num(south)})");
        if (!(east > west)) throw new FormatException($"projection east ({Num(east)}) is not east of west ({Num(west)})");
        if (!(margin >= 0 && margin < 0.5)) throw new FormatException($"projection margin {Num(margin)} is outside 0 <= m < 0.5");
        return new Projection(south, west, north, east, margin);
    }

    // The declared affine, clamped to 0..1 (y north-up, as the file and buses.json write it).
    private static (double X, double Y) Project(Projection b, double lon, double lat)
    {
        double m = b.Margin;
        double x = m + (1 - 2 * m) * (lon - b.West) / (b.East - b.West);
        double y = m + (1 - 2 * m) * (lat - b.South) / (b.North - b.South);
        return (Math.Clamp(x, 0.0, 1.0), Math.Clamp(y, 0.0, 1.0));
    }

    private static void CheckPointLayout(JsonElement layout)
    {
        string[] expected = { "lon", "lat", "x", "y" };
        if (layout.ValueKind != JsonValueKind.Array || layout.GetArrayLength() != expected.Length)
            throw new FormatException("point_layout must be the array [\"lon\", \"lat\", \"x\", \"y\"]");
        for (int i = 0; i < expected.Length; i++)
            if (layout[i].ValueKind != JsonValueKind.String || !string.Equals(layout[i].GetString(), expected[i], StringComparison.Ordinal))
                throw new FormatException("point_layout must be the array [\"lon\", \"lat\", \"x\", \"y\"]; this reader takes no other order");
    }

    // ---- the areas ----

    private static (List<Area> Areas, int Points) ReadAreas(JsonElement arr, string what, Projection box, string? codeProp)
    {
        if (arr.ValueKind != JsonValueKind.Array) throw new FormatException($"{what} is not an array");
        var list = new List<Area>(arr.GetArrayLength());
        int points = 0;
        int ai = 0;
        foreach (JsonElement a in arr.EnumerateArray())
        {
            string where = $"{what}[{ai}]";
            if (a.ValueKind != JsonValueKind.Object) throw new FormatException($"{where} is not an object");
            string name = RequireString(a, "name", where);
            string code = codeProp is not null && a.TryGetProperty(codeProp, out JsonElement c) && c.ValueKind == JsonValueKind.String
                ? c.GetString() ?? "" : "";
            where = $"{what}[{ai}] \"{name}\"";

            // An area may have an empty ring list: simplification can leave out every ring of a small area (island
            // territories such as Lakshadweep), and the file's stats count those rings. Such an area is kept, with
            // nothing to draw. The file as a whole must still carry at least one land ring (checked in Parse).
            JsonElement rings = Require(a, "rings", where);
            if (rings.ValueKind != JsonValueKind.Array)
                throw new FormatException($"{where} rings is not an array");
            var outRings = new WorldPoint[rings.GetArrayLength()][];
            int ri = 0;
            foreach (JsonElement ring in rings.EnumerateArray())
            {
                outRings[ri] = ReadRing(ring, $"{where} ring {ri}", box);
                points += outRings[ri].Length;
                ri++;
            }
            list.Add(new Area(name, code, outRings));
            ai++;
        }
        return (list, points);
    }

    private static WorldPoint[] ReadRing(JsonElement ring, string where, Projection box)
    {
        if (ring.ValueKind != JsonValueKind.Array) throw new FormatException($"{where} is not an array");
        int n = ring.GetArrayLength();
        if (n < 3) throw new FormatException($"{where} has {n} point(s); a ring needs at least 3");
        var pts = new WorldPoint[n];
        int i = 0;
        foreach (JsonElement pt in ring.EnumerateArray())
        {
            if (pt.ValueKind != JsonValueKind.Array || pt.GetArrayLength() != 4)
                throw new FormatException($"{where} point {i} is not [lon, lat, x, y]");
            double lon = Finite(pt[0], $"{where} point {i} lon");
            double lat = Finite(pt[1], $"{where} point {i} lat");
            double fx = Finite(pt[2], $"{where} point {i} x");
            double fy = Finite(pt[3], $"{where} point {i} y");
            if (lon < -180 || lon > 180 || lat < -90 || lat > 90)
                throw new FormatException($"{where} point {i} lon/lat {Num(lon)}, {Num(lat)} is not a position on the Earth");

            (double x, double y) = Project(box, lon, lat);
            if (Math.Abs(x - fx) > PointTolerance || Math.Abs(y - fy) > PointTolerance)
                throw new FormatException(
                    $"{where} point {i} (lon {Num(lon)}, lat {Num(lat)}) is written at x {Num(fx)}, y {Num(fy)} but the declared " +
                    $"projection puts it at x {Num(x)}, y {Num(y)}: the points and the projection disagree");
            pts[i] = new WorldPoint(x, 1.0 - y);   // world y is north-down, as a bus is drawn at (x, 1 - y)
            i++;
        }
        return pts;
    }

    // ---- small strict readers ----

    private static JsonElement Require(JsonElement obj, string name, string where)
    {
        if (!obj.TryGetProperty(name, out JsonElement v)) throw new FormatException($"{where} has no \"{name}\"");
        return v;
    }

    private static string RequireString(JsonElement obj, string name, string where)
    {
        JsonElement v = Require(obj, name, where);
        if (v.ValueKind != JsonValueKind.String) throw new FormatException($"{where} \"{name}\" is not a string");
        return v.GetString() ?? "";
    }

    private static double RequireFinite(JsonElement obj, string name, string where) => Finite(Require(obj, name, where), $"{where} {name}");

    private static double Finite(JsonElement v, string what)
    {
        if (v.ValueKind != JsonValueKind.Number) throw new FormatException($"{what} is not a number");
        double d = v.GetDouble();
        if (!double.IsFinite(d)) throw new FormatException($"{what} is not finite");
        return d;
    }

    private static string Num(double d) => d.ToString("R", CultureInfo.InvariantCulture);
}
