using System.Text.RegularExpressions;
using GridSim.Core.Model;
using GridSim.Gda.Csv;

namespace GridSim.Gda.Geo;

/// <summary>
/// Gives GDA-materialised buses real geographic coordinates by joining their
/// substation <see cref="Bus.Name"/> against a substation-geolocation table
/// (the NGED "Network Capacity" <c>substations.csv</c>: columns <c>name</c>,
/// <c>longitude</c>, <c>latitude</c>, ...). Matched buses have their layout
/// coordinates replaced by the substation's position projected onto fixed GB
/// bounds; unmatched buses keep whatever <c>X</c>/<c>Y</c> they already had.
/// </summary>
/// <remarks>
/// The projection is a plain equirectangular map onto a unit square over the GB
/// bounding box  -  good enough to lay the network on a WPF map, not a true
/// cartographic projection. <c>Y</c> is north-up (latitude increasing => Y -> 1).
/// </remarks>
public static class GeoLocator
{
    /// <summary>Western edge of the GB bounding box (decimal degrees, WGS84). Maps to X = 0.</summary>
    public const double LonMin = -8.2;
    /// <summary>Eastern edge of the GB bounding box. Maps to X = 1.</summary>
    public const double LonMax = 1.9;
    /// <summary>Southern edge of the GB bounding box. Maps to Y = 0.</summary>
    public const double LatMin = 49.8;
    /// <summary>Northern edge of the GB bounding box. Maps to Y = 1.</summary>
    public const double LatMax = 59.0;

    // The FIRST voltage token and everything after it, e.g. "132KV", "400 KV",
    // "33KV", or a compound "132/33KV" / "132/33/11 KV", plus any trailing bay/
    // circuit identifier ("... 132kV 105", "... 132/33kV Bay 2"). Cutting here folds
    // an LTDS node name down to its substation base name, which is what the
    // substation table is keyed on  -  lifting the join rate from ~2% to ~40%.
    private static readonly Regex VoltageToEnd =
        new(@"\s*\d+(?:\s*/\s*\d+)*\s*KV\b.*$", RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static readonly Regex WhitespaceRun =
        new(@"\s+", RegexOptions.Compiled | RegexOptions.CultureInvariant);

    /// <summary>
    /// Canonical join key for a substation/node name: upper-cased, truncated at the
    /// first voltage token (dropping the voltage and any trailing bay/circuit id),
    /// whitespace collapsed, trimmed. Used on both sides of the geolocation join, and
    /// exposed so tests can mirror the exact rule.
    /// </summary>
    public static string Normalize(string? name)
    {
        if (string.IsNullOrWhiteSpace(name)) return string.Empty;
        string s = name.ToUpperInvariant();
        s = WhitespaceRun.Replace(s, " ").Trim();
        s = VoltageToEnd.Replace(s, string.Empty);
        return s.Trim();
    }

    /// <summary>
    /// Read a substations CSV (or Parquet) into a map of normalised name ->
    /// (longitude, latitude), keeping the first row per name that carries a
    /// longitude/latitude inside the GB bounding box. Rows whose coordinates fall
    /// outside GB (or are blank/unparseable) are skipped.
    /// </summary>
    public static IReadOnlyDictionary<string, (double Lon, double Lat)> ReadSubstations(string substationsPath)
    {
        var map = new Dictionary<string, (double Lon, double Lat)>(StringComparer.Ordinal);
        using DataSourceTable table = DataSourceTable.Open(substationsPath);
        foreach (IRow row in table.Rows())
        {
            string key = Normalize(row["name"]);
            if (key.Length == 0) continue;
            if (map.ContainsKey(key)) continue;          // keep the first row per name
            if (!row.Has("longitude") || !row.Has("latitude")) continue;
            double lon = row.Double("longitude", double.NaN);
            double lat = row.Double("latitude", double.NaN);
            if (!InGb(lon, lat)) continue;               // skip out-of-GB / NaN
            map[key] = (lon, lat);
        }
        return map;
    }

    /// <summary>
    /// Project a substation position onto the unit square over the GB bounding box.
    /// <c>X = (lon - LonMin)/(LonMax - LonMin)</c>, <c>Y = (lat - LatMin)/(LatMax - LatMin)</c>
    /// (north-up), both clamped to [0, 1].
    /// </summary>
    public static (double X, double Y) Project(double lon, double lat)
    {
        double x = (lon - LonMin) / (LonMax - LonMin);
        double y = (lat - LatMin) / (LatMax - LatMin);
        return (Clamp01(x), Clamp01(y));
    }

    /// <summary>
    /// Return a new <see cref="GridModel"/> whose buses get geographic X/Y for every
    /// bus whose normalised <see cref="Bus.Name"/> matches a substation in
    /// <paramref name="substationsCsvPath"/>; unmatched buses keep their existing
    /// X/Y. The input model is not mutated.
    /// </summary>
    /// <returns>The relocated model, the number of buses matched, and the bus total.</returns>
    public static (GridModel model, int matched, int total) Apply(GridModel model, string substationsCsvPath)
    {
        IReadOnlyDictionary<string, (double Lon, double Lat)> subs = ReadSubstations(substationsCsvPath);
        return Apply(model, subs);
    }

    /// <summary>
    /// As <see cref="Apply(GridModel,string)"/>, but against an already-loaded
    /// name -> (lon, lat) map (the projection is the same).
    /// </summary>
    public static (GridModel model, int matched, int total) Apply(
        GridModel model, IReadOnlyDictionary<string, (double Lon, double Lat)> substations, bool spreadUnmatched = true)
    {
        int n = model.Buses.Count;
        var x = new double[n];
        var y = new double[n];
        var placed = new bool[n];          // got a real geographic anchor
        var idToIdx = new Dictionary<int, int>(n);

        int matched = 0;
        for (int i = 0; i < n; i++)
        {
            Bus b = model.Buses[i];
            idToIdx[b.Id] = i;
            string key = Normalize(b.Name);
            if (key.Length > 0 && substations.TryGetValue(key, out (double Lon, double Lat) pos))
            {
                (x[i], y[i]) = Project(pos.Lon, pos.Lat);
                placed[i] = true;
                matched++;
            }
        }

        // Drape the rest of the network over the anchors: each unplaced bus relaxes to
        // the mean of its already-placed branch-neighbours, propagated over several
        // passes. Keeps the graph connected and GB-shaped instead of piling at (0,0).
        if (spreadUnmatched && matched > 0 && matched < n)
        {
            var adj = new List<int>[n];
            for (int i = 0; i < n; i++) adj[i] = new List<int>();
            foreach (Branch br in model.Branches)
                if (idToIdx.TryGetValue(br.From, out int a) && idToIdx.TryGetValue(br.To, out int c))
                { adj[a].Add(c); adj[c].Add(a); }

            for (int pass = 0; pass < 60; pass++)
            {
                bool changed = false;
                for (int i = 0; i < n; i++)
                {
                    if (placed[i]) continue;
                    double sx = 0, sy = 0; int k = 0;
                    foreach (int j in adj[i]) if (placed[j]) { sx += x[j]; sy += y[j]; k++; }
                    if (k == 0) continue;
                    x[i] = sx / k; y[i] = sy / k; placed[i] = true; changed = true;
                }
                if (!changed) break;
            }
        }

        var buses = new List<Bus>(n);
        for (int i = 0; i < n; i++)
        {
            Bus b = model.Buses[i];
            if (!placed[i]) { buses.Add(b); continue; }        // never reached -> keep original X/Y
            double jx = Jitter(b.Id, 1) * 0.004, jy = Jitter(b.Id, 2) * 0.004; // de-overlap co-located buses
            buses.Add(b with { X = Clamp01(x[i] + jx), Y = Clamp01(y[i] + jy) });
        }

        // CopyWith carries asset dynamics and protections too; this site used to drop both (R270).
        GridModel relocated = model.CopyWith(buses: buses);
        return (relocated, matched, n);
    }

    private static double Jitter(int id, int salt)
    {
        unchecked { uint h = (uint)(id * 2654435761u + salt * 40503u); return (h % 1000) / 1000.0 - 0.5; }
    }

    private static bool InGb(double lon, double lat) =>
        !double.IsNaN(lon) && !double.IsNaN(lat) &&
        lon >= LonMin && lon <= LonMax && lat >= LatMin && lat <= LatMax;

    private static double Clamp01(double v) => v < 0 ? 0 : v > 1 ? 1 : v;
}
