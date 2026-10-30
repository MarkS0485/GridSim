using System.IO;
using System.Text.Json;
using GridSim.Core.Presentation;
using GridSim.Ui.Presentation;

namespace GridSim.Ui.Geography;

/// <summary>
/// The DNO licence-area / GSP-region boundary polygons and the geocoded generation-asset
/// markers, loaded from the WGS84 GeoJSON that <c>NetworkModel/export_geo_for_gridsim.py</c>
/// materialises into <c>gda-out/geo/</c> (dno_areas / gsp_regions / assets .geojson).
///
/// Everything is projected up-front into map world coordinates (the WPF <c>FastSchematic</c>'s): the
/// SAME equirectangular GB bounding box that <c>CoastlineGb</c> and
/// <c>GridSim.Gda.Geo.GeoLocator</c> use, so the overlay lines up with the coastline and with
/// buses that were geolocated from real substation lat/lon. World <c>x</c> is 0..1 west->east
/// and world <c>y</c> is north-down (y = 1 - lat-fraction) to match the bus world coords.
///
/// Like the coastline, this is admin geography (not grid data): it only reads whatever local
/// GeoJSON is present and is git-ignored under <c>gda-out/</c>.
///
/// <para>UI programme Phase 0.2: moved here from the WPF <c>Visuals/GeographyData.cs</c> so the planned Avalonia app
/// reads the same overlay. The only change is the two WPF types it used to hold: a world point is now a
/// <see cref="WorldPoint"/> (the same two doubles as the old <c>System.Windows.Point</c>), and an asset's fill is now a
/// neutral <see cref="UiColour"/> (the same four bytes as the brush colour it used to carry: the Core fuel taxonomy's
/// colour for the asset's technology). The WPF map turns the colour into its cached frozen brush.</para>
/// </summary>
public sealed class GeographyData
{
    // GB bounding box - must match CoastlineGb / GridSim.Gda.Geo.GeoLocator.{Lon,Lat}{Min,Max}.
    private const double LonMin = -8.2, LonMax = 1.9, LatMin = 49.8, LatMax = 59.0;

    /// <summary>One boundary polygon: a display label, a secondary tag, and its rings (world coords).</summary>
    public sealed record Region(string Label, string Tag, WorldPoint[][] Rings);

    /// <summary>A geocoded asset marker in world coords, with a fuel colour and capacity-relative size.</summary>
    public sealed record AssetMarker(WorldPoint World, UiColour Fill, double Mw, string Tech, string Name, string Source);

    public IReadOnlyList<Region> Dno { get; }
    public IReadOnlyList<Region> Gsp { get; }
    public IReadOnlyList<AssetMarker> Assets { get; }
    public double MaxAssetMw { get; }

    private GeographyData(List<Region> dno, List<Region> gsp, List<AssetMarker> assets)
    {
        Dno = dno; Gsp = gsp; Assets = assets;
        double mx = 1;
        foreach (AssetMarker a in assets) if (a.Mw > mx) mx = a.Mw;
        MaxAssetMw = mx;
    }

    public bool IsEmpty => Dno.Count == 0 && Gsp.Count == 0 && Assets.Count == 0;

    // world projection, identical in form to CoastlineGb.Build (no clamp: polygons may run
    // a touch past the box edge, which ClipToBounds trims).
    private static WorldPoint Project(double lon, double lat)
    {
        double x = (lon - LonMin) / (LonMax - LonMin);
        double yUp = (lat - LatMin) / (LatMax - LatMin);
        return new WorldPoint(x, 1.0 - yUp);
    }

    /// <summary>
    /// UI programme Phase 5d (2026-10-21): the map world point of a WGS84 position, by the SAME projection the DNO and
    /// GSP layers and the geocoded asset markers are drawn with (the private <c>Project</c> above, unchanged): the
    /// equirectangular GB bounding box, x 0..1 west to east, y north-down. The asset overlay's line route towers are
    /// placed on the map through this, so a route lines up with the geography layers and the coastline. No clamp: a
    /// point outside the box lands outside 0..1, as a region ring may.
    /// </summary>
    /// <param name="longitudeDeg">Longitude, degrees east.</param>
    /// <param name="latitudeDeg">Latitude, degrees north.</param>
    public static WorldPoint WorldOf(double longitudeDeg, double latitudeDeg) => Project(longitudeDeg, latitudeDeg);

    /// <summary>
    /// Find a <c>gda-out/geo</c> directory (see <see cref="FindGeoDir"/>) and load the three GeoJSON files. Returns
    /// null when no geography is present (the overlay simply stays unavailable). A file that cannot be read or parsed
    /// does not stop the app, but it is never silent: its layer stays empty and the reason ("file: message") is added
    /// to <paramref name="problems"/>, which the window writes to the event log. A truncated assets.geojson used to
    /// leave the ASSETS button enabled (because the DNO file loaded) with nothing drawn and nothing said.
    /// </summary>
    public static GeographyData? Load(string baseDir, ICollection<string>? problems = null)
    {
        string? geoDir = FindGeoDir(baseDir);
        if (geoDir is null) return null;

        var dno = LoadRegions(Path.Combine(geoDir, "dno_areas.geojson"), "area", "dno", problems);
        var gsp = LoadRegions(Path.Combine(geoDir, "gsp_regions.geojson"), "gsps", "gspgroup", problems);
        var assets = LoadAssets(Path.Combine(geoDir, "assets.geojson"), problems);

        var data = new GeographyData(dno, gsp, assets);
        return data.IsEmpty ? null : data;
    }

    /// <summary>
    /// The <c>gda-out/geo</c> folder to read: the application folder's own, else one in a parent folder up to and
    /// including the repository root (the first ancestor holding <c>GridSim.slnx</c>, for a build run from a clone),
    /// at most 8 levels up. The walk stops at the repository root, and an installed copy (no repository above it) reads
    /// only its own folder: it used to search up to eight parents whatever they were, so it could pick up geography
    /// from outside the user's tree.
    /// </summary>
    private static string? FindGeoDir(string baseDir)
    {
        string own = Path.Combine(baseDir, "gda-out", "geo");
        if (Directory.Exists(own)) return own;

        // Only walk up when a repository root is actually above the app folder.
        DirectoryInfo? root = null;
        DirectoryInfo? probe = new(baseDir);
        for (int i = 0; i < 8 && probe is not null; i++, probe = probe.Parent)
            if (File.Exists(Path.Combine(probe.FullName, "GridSim.slnx"))) { root = probe; break; }
        if (root is null) return null;

        for (DirectoryInfo? dir = new DirectoryInfo(baseDir).Parent; dir is not null; dir = dir.Parent)
        {
            string g = Path.Combine(dir.FullName, "gda-out", "geo");
            if (Directory.Exists(g)) return g;
            if (string.Equals(dir.FullName, root.FullName, StringComparison.OrdinalIgnoreCase)) break;   // not above the repo
        }
        return null;
    }

    private static List<Region> LoadRegions(string path, string labelProp, string tagProp, ICollection<string>? problems)
    {
        var list = new List<Region>();
        if (!File.Exists(path)) return list;
        try
        {
            using JsonDocument doc = JsonDocument.Parse(File.ReadAllText(path));
            if (!doc.RootElement.TryGetProperty("features", out JsonElement feats)) return list;
            foreach (JsonElement ft in feats.EnumerateArray())
            {
                string label = PropStr(ft, labelProp) ?? "";
                string tag = PropStr(ft, tagProp) ?? "";
                var rings = new List<WorldPoint[]>();
                if (ft.TryGetProperty("geometry", out JsonElement geom))
                    CollectRings(geom, rings);
                if (rings.Count > 0) list.Add(new Region(label, tag, rings.ToArray()));
            }
        }
        catch (Exception ex)
        {
            // A malformed geo file yields no regions for that layer, and the reason is reported, not swallowed.
            list.Clear();
            problems?.Add($"{Path.GetFileName(path)}: {ex.Message}");
        }
        return list;
    }

    // GeoJSON geometry -> flat list of projected rings (Polygon = list of rings,
    // MultiPolygon = list of polygons). Holes are kept as their own rings - for an
    // outline overlay that's exactly what we want to stroke.
    private static void CollectRings(JsonElement geom, List<WorldPoint[]> rings)
    {
        if (!geom.TryGetProperty("type", out JsonElement tEl) ||
            !geom.TryGetProperty("coordinates", out JsonElement coords)) return;
        string t = tEl.GetString() ?? "";
        if (t == "Polygon")
            foreach (JsonElement ring in coords.EnumerateArray()) AddRing(ring, rings);
        else if (t == "MultiPolygon")
            foreach (JsonElement poly in coords.EnumerateArray())
                foreach (JsonElement ring in poly.EnumerateArray()) AddRing(ring, rings);
    }

    private static void AddRing(JsonElement ring, List<WorldPoint[]> rings)
    {
        var pts = new List<WorldPoint>(ring.GetArrayLength());
        foreach (JsonElement pt in ring.EnumerateArray())
        {
            double lon = pt[0].GetDouble();
            double lat = pt[1].GetDouble();
            pts.Add(Project(lon, lat));
        }
        if (pts.Count >= 2) rings.Add(pts.ToArray());
    }

    private static List<AssetMarker> LoadAssets(string path, ICollection<string>? problems)
    {
        var list = new List<AssetMarker>();
        if (!File.Exists(path)) return list;
        try
        {
            using JsonDocument doc = JsonDocument.Parse(File.ReadAllText(path));
            if (!doc.RootElement.TryGetProperty("features", out JsonElement feats)) return list;
            var colourByTech = new Dictionary<string, UiColour>(StringComparer.OrdinalIgnoreCase);
            foreach (JsonElement ft in feats.EnumerateArray())
            {
                if (!ft.TryGetProperty("geometry", out JsonElement geom) ||
                    !geom.TryGetProperty("coordinates", out JsonElement c) ||
                    c.GetArrayLength() < 2) continue;
                WorldPoint w = Project(c[0].GetDouble(), c[1].GetDouble());

                string tech = PropStr(ft, "technology_or_fuel") ?? "";
                if (!colourByTech.TryGetValue(tech, out UiColour fill))
                {
                    fill = ColourRamps.FromHex(FuelTaxonomy.Classify(tech).Color);
                    colourByTech[tech] = fill;
                }
                double mw = PropDouble(ft, "capacity_mw");
                string name = PropStr(ft, "name") ?? "";
                string source = PropStr(ft, "source") ?? "";
                list.Add(new AssetMarker(w, fill, mw, tech, name, source));
            }
        }
        catch (Exception ex)
        {
            // A malformed asset file yields no markers, and the reason is reported, not swallowed.
            list.Clear();
            problems?.Add($"{Path.GetFileName(path)}: {ex.Message}");
        }
        return list;
    }

    private static string? PropStr(JsonElement feature, string name)
    {
        if (feature.TryGetProperty("properties", out JsonElement props) &&
            props.TryGetProperty(name, out JsonElement v) && v.ValueKind == JsonValueKind.String)
            return v.GetString();
        return null;
    }

    private static double PropDouble(JsonElement feature, string name)
    {
        if (feature.TryGetProperty("properties", out JsonElement props) &&
            props.TryGetProperty(name, out JsonElement v) && v.ValueKind == JsonValueKind.Number)
            return v.GetDouble();
        return 0;
    }
}
