using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;

namespace GridSim.Core.Analysis.Gic;

/// <summary>How a transformer's windings conduct DC (RC5 WP28).</summary>
public enum GicTransformerType
{
    /// <summary>Generator step-up: grounded-wye HV winding to the substation neutral, delta LV (open to DC). Also written
    /// <c>gy-d</c>.</summary>
    Gsu,
    /// <summary>Two windings, both grounded wye, each to the substation neutral.</summary>
    GyGy,
    /// <summary>Autotransformer: series winding from the H bus to the X bus, common winding from the X bus to the
    /// neutral.</summary>
    Auto,
    /// <summary>Delta HV (open to DC), grounded-wye LV winding to the substation neutral.</summary>
    DGy,
    /// <summary>No DC path (for example delta-delta).</summary>
    Open,
}

/// <summary>How the northward and eastward distances of a line are computed from the substation coordinates.</summary>
public enum GicDistanceModel
{
    /// <summary>The NERC application guide's WGS84 expressions (its eqs. A.3 and A.7):
    /// L_N = (111.133 - 0.56 cos 2phi) dlat and L_E = (111.5065 - 0.1872 cos 2phi) cos phi dlon, km, phi the mean latitude.</summary>
    Wgs84,
    /// <summary>A sphere at 111.2 km per degree: L_N = 111.2 dlat, L_E = 111.2 cos phi dlon. The guide's own worked example
    /// (Appendix II, Tables B-4 to B-7) follows this, not its eqs. A.3 and A.7.</summary>
    Spherical,
}

/// <summary>A substation of a <c>gridsim-gic/1</c> file: its location, its ground grid resistance (ohms, the whole
/// grid) and the buses inside it.</summary>
public sealed record GicSubstation(string Name, double Latitude, double Longitude, double GroundOhms, IReadOnlyList<int> Buses);

/// <summary>A transformer of a <c>gridsim-gic/1</c> file. Winding resistances are DC ohms per phase; for an
/// autotransformer the HV value is the series winding and the LV value the common winding. <see cref="KMvarPerA"/> is the
/// reactive loss factor (Mvar per ampere of effective GIC per phase at 1 pu voltage); null means no loss is reported.
/// <see cref="Blocked"/> opens the neutral (a GIC blocking device).</summary>
public sealed record GicTransformer(string Branch, GicTransformerType Type, double HvWindingOhms, double LvWindingOhms, double? KMvarPerA, bool Blocked);

/// <summary>
/// RC5 WP28: the <c>gridsim-gic/1</c> sidecar, the data a GIC study needs that a load-flow case does not hold. Strict: an
/// unknown or repeated key, a missing required key, a bus in two substations, a non-positive resistance and an unknown
/// transformer type are refused by name. The file carries no timestamp (the field is a command-line input), so no
/// forecast horizon applies.
/// <code>
/// { "schema": "gridsim-gic/1", "distanceModel": "wgs84",
///   "substations": [ { "name": "S1", "lat": 33.6135, "lon": -87.3737, "groundOhms": 0.2, "buses": [1, 2] } ],
///   "transformers": [ { "branch": "1-2", "type": "gsu", "hvWindingOhms": 0.5, "kMvarPerA": 1.18, "blocked": false } ],
///   "lineOhmsOverride": { "3-4": 3.525 } }
/// </code>
/// </summary>
public sealed record GicFile(
    IReadOnlyList<GicSubstation> Substations,
    IReadOnlyList<GicTransformer> Transformers,
    IReadOnlyList<KeyValuePair<string, double>> LineOhmsOverride,
    GicDistanceModel DistanceModel,
    string Sha256)
{
    /// <summary>The schema name.</summary>
    public const string Schema = "gridsim-gic/1";

    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    /// <summary>Read and check a file.</summary>
    public static GicFile Load(string path)
    {
        ArgumentNullException.ThrowIfNull(path);
        return Parse(File.ReadAllBytes(path), Path.GetFileName(path));
    }

    /// <summary>Parse and check the bytes of a file (<paramref name="where"/> names it in messages).</summary>
    public static GicFile Parse(byte[] bytes, string where)
    {
        ArgumentNullException.ThrowIfNull(bytes);
        ArgumentNullException.ThrowIfNull(where);
        using JsonDocument doc = Open(bytes, where);
        JsonElement root = doc.RootElement;
        Keys(root, where, ["schema", "distanceModel", "substations", "transformers", "lineOhmsOverride"], ["schema", "substations"]);
        if (root.GetProperty("schema").ValueKind != JsonValueKind.String || root.GetProperty("schema").GetString() != Schema)
            throw new InvalidDataException($"{where}: schema must be \"{Schema}\".");

        GicDistanceModel dm = GicDistanceModel.Wgs84;
        if (root.TryGetProperty("distanceModel", out JsonElement dme))
            dm = Str(dme, where + ".distanceModel") switch
            {
                "wgs84" => GicDistanceModel.Wgs84,
                "spherical" => GicDistanceModel.Spherical,
                string s => throw new InvalidDataException($"{where}.distanceModel: '{s}' is not wgs84 or spherical."),
            };

        var subs = new List<GicSubstation>();
        var names = new HashSet<string>(StringComparer.Ordinal);
        var busOwner = new Dictionary<int, string>();
        foreach ((JsonElement s, string at) in Items(root.GetProperty("substations"), where + ".substations"))
        {
            Keys(s, at, ["name", "lat", "lon", "groundOhms", "buses"], ["name", "lat", "lon", "groundOhms", "buses"]);
            string name = Str(s.GetProperty("name"), at + ".name");
            if (name.Length == 0 || !names.Add(name)) throw new InvalidDataException($"{at}.name: '{name}' is empty or given twice.");
            double lat = Num(s.GetProperty("lat"), at + ".lat"), lon = Num(s.GetProperty("lon"), at + ".lon");
            if (lat is < -90 or > 90) throw new InvalidDataException($"{at}.lat: {lat.ToString("R", Inv)} is outside -90 to 90 degrees.");
            if (lon is < -180 or > 180) throw new InvalidDataException($"{at}.lon: {lon.ToString("R", Inv)} is outside -180 to 180 degrees.");
            double rg = Positive(s.GetProperty("groundOhms"), at + ".groundOhms");
            var buses = new List<int>();
            foreach ((JsonElement b, string bat) in Items(s.GetProperty("buses"), at + ".buses"))
            {
                if (b.ValueKind != JsonValueKind.Number || !b.TryGetInt32(out int id)) throw new InvalidDataException($"{bat} must be a whole bus number.");
                if (!busOwner.TryAdd(id, name)) throw new InvalidDataException($"{bat}: bus {id.ToString(Inv)} is already in substation '{busOwner[id]}'.");
                buses.Add(id);
            }
            subs.Add(new GicSubstation(name, lat, lon, rg, buses));
        }

        var trs = new List<GicTransformer>();
        var keys = new HashSet<string>(StringComparer.Ordinal);
        if (root.TryGetProperty("transformers", out JsonElement tre))
            foreach ((JsonElement t, string at) in Items(tre, where + ".transformers"))
            {
                Keys(t, at, ["branch", "type", "hvWindingOhms", "lvWindingOhms", "kMvarPerA", "blocked"], ["branch", "type"]);
                string key = Str(t.GetProperty("branch"), at + ".branch");
                if (!keys.Add(key)) throw new InvalidDataException($"{at}.branch: '{key}' is given twice.");
                GicTransformerType type = Str(t.GetProperty("type"), at + ".type") switch
                {
                    "gsu" or "gy-d" => GicTransformerType.Gsu,
                    "gy-gy" => GicTransformerType.GyGy,
                    "auto" => GicTransformerType.Auto,
                    "d-gy" => GicTransformerType.DGy,
                    "open" => GicTransformerType.Open,
                    string s => throw new InvalidDataException($"{at}.type: '{s}' is not gsu, gy-d, gy-gy, auto, d-gy or open."),
                };
                bool needHv = type is GicTransformerType.Gsu or GicTransformerType.GyGy or GicTransformerType.Auto;
                bool needLv = type is GicTransformerType.GyGy or GicTransformerType.Auto or GicTransformerType.DGy;
                double hv = Winding(t, "hvWindingOhms", at, needHv);
                double lv = Winding(t, "lvWindingOhms", at, needLv);
                double? k = null;
                if (t.TryGetProperty("kMvarPerA", out JsonElement ke))
                {
                    double kv = Num(ke, at + ".kMvarPerA");
                    if (kv < 0) throw new InvalidDataException($"{at}.kMvarPerA may not be negative.");
                    k = kv;
                }
                bool blocked = false;
                if (t.TryGetProperty("blocked", out JsonElement be))
                    blocked = be.ValueKind switch
                    {
                        JsonValueKind.True => true,
                        JsonValueKind.False => false,
                        _ => throw new InvalidDataException($"{at}.blocked must be true or false."),
                    };
                trs.Add(new GicTransformer(key, type, hv, lv, k, blocked));
            }

        var over = new List<KeyValuePair<string, double>>();
        if (root.TryGetProperty("lineOhmsOverride", out JsonElement oe))
        {
            if (oe.ValueKind != JsonValueKind.Object) throw new InvalidDataException($"{where}.lineOhmsOverride must be an object.");
            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (JsonProperty p in oe.EnumerateObject())
            {
                if (!seen.Add(p.Name)) throw new InvalidDataException($"{where}.lineOhmsOverride: '{p.Name}' is given twice.");
                over.Add(new KeyValuePair<string, double>(p.Name, Positive(p.Value, $"{where}.lineOhmsOverride.{p.Name}")));
            }
        }
        return new GicFile(subs, trs, over, dm, Convert.ToHexStringLower(SHA256.HashData(bytes)));
    }

    private static JsonDocument Open(byte[] bytes, string where)
    {
        try { return JsonDocument.Parse(GridSim.Core.IO.Utf8Json.WithoutBom(bytes)); }
        catch (JsonException ex) { throw new InvalidDataException($"{where}: not valid JSON: {ex.Message}", ex); }
    }

    private static double Winding(JsonElement t, string name, string at, bool needed)
    {
        if (!t.TryGetProperty(name, out JsonElement e))
            return needed ? throw new InvalidDataException($"{at}.{name} is required for this transformer type.") : 0.0;
        return Positive(e, at + "." + name);
    }

    private static IEnumerable<(JsonElement Item, string At)> Items(JsonElement arr, string at)
    {
        if (arr.ValueKind != JsonValueKind.Array) throw new InvalidDataException($"{at} must be an array.");
        int i = 0;
        foreach (JsonElement e in arr.EnumerateArray()) yield return (e, $"{at}[{(i++).ToString(Inv)}]");
    }

    private static void Keys(JsonElement e, string at, string[] allowed, string[] required)
    {
        if (e.ValueKind != JsonValueKind.Object) throw new InvalidDataException($"{at} must be an object.");
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (JsonProperty p in e.EnumerateObject())
        {
            if (Array.IndexOf(allowed, p.Name) < 0) throw new InvalidDataException($"{at}: unknown key '{p.Name}' (allowed: {string.Join(", ", allowed)}).");
            if (!seen.Add(p.Name)) throw new InvalidDataException($"{at}: the key '{p.Name}' is given twice.");
        }
        foreach (string r in required)
            if (!seen.Contains(r)) throw new InvalidDataException($"{at}: the key '{r}' is required.");
    }

    private static string Str(JsonElement e, string at) =>
        e.ValueKind == JsonValueKind.String ? e.GetString()! : throw new InvalidDataException($"{at} must be a string.");

    private static double Num(JsonElement e, string at) =>
        e.ValueKind == JsonValueKind.Number && e.TryGetDouble(out double v) && double.IsFinite(v) ? v
            : throw new InvalidDataException($"{at} must be a finite number.");

    private static double Positive(JsonElement e, string at)
    {
        double v = Num(e, at);
        return v > 0 ? v : throw new InvalidDataException($"{at} must be greater than 0 ohms.");
    }
}
