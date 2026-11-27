using System.Globalization;
using System.Text;
using System.Text.Json;
using GridSim.Core.Model;

namespace GridSim.Core.IO.Cim.Profiles;

/// <summary>A bus's geographical position (WGS 84, degrees).</summary>
public sealed record GeoBusSite(int Bus, double LatDeg, double LonDeg);

/// <summary>A branch's route: its interior points (WGS 84, degrees), from the from bus towards the to bus.</summary>
/// <param name="Branch">The branch number, 1-based, in the model's branch order (CgmesWriter writes branch k as L<i>k</i>).</param>
public sealed record GeoRoute(int Branch, IReadOnlyList<(double LatDeg, double LonDeg)> Points);

/// <summary>
/// RC5 WP10: the geographical sidecar of a case, <c>gridsim-geo/1</c> (strict JSON):
/// <c>{"format":"gridsim-geo/1","crs":"EPSG:4326","sites":[{"bus":1,"latDeg":51.5,"lonDeg":-0.13}],"lines":[{"branch":2,"points":[[lat,lon],...]}]}</c>.
/// GridModel holds no geographical coordinate, so GL data travels in this file. The CRS is WGS 84 (EPSG:4326) only; no
/// reprojection is made. Unknown keys, a missing key, a duplicate bus or branch and a latitude or longitude out of range are
/// refused by name.
/// </summary>
public sealed record GeoSidecar(IReadOnlyList<GeoBusSite> Sites, IReadOnlyList<GeoRoute> Lines)
{
    public const string FormatVersion = "gridsim-geo/1";
    public const string Crs = "EPSG:4326";

    public static GeoSidecar Read(string json)
    {
        ArgumentNullException.ThrowIfNull(json);
        JsonDocument doc;
        try { doc = JsonDocument.Parse(json, new JsonDocumentOptions { AllowTrailingCommas = false, CommentHandling = JsonCommentHandling.Disallow }); }
        catch (JsonException ex) { throw new InvalidDataException($"{FormatVersion}: not valid JSON: {ex.Message}"); }
        using (doc)
        {
            JsonElement root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object) throw new InvalidDataException($"{FormatVersion}: the document must be a JSON object.");
            Keys(root, "the document", "format", "crs", "sites", "lines");
            string format = Str(root, "format", "the document");
            if (format != FormatVersion) throw new InvalidDataException($"{FormatVersion}: format must be \"{FormatVersion}\", not \"{format}\".");
            string crs = Str(root, "crs", "the document");
            if (crs != Crs) throw new InvalidDataException($"{FormatVersion}: crs is \"{crs}\"; only \"{Crs}\" (WGS 84 latitude and longitude) is read, and no reprojection is made.");
            var sites = new List<GeoBusSite>();
            var seenBus = new HashSet<int>();
            foreach (JsonElement s in Arr(root, "sites", "the document"))
            {
                Keys(s, "a site", "bus", "latDeg", "lonDeg");
                int bus = Int(s, "bus", "a site");
                if (!seenBus.Add(bus)) throw new InvalidDataException($"{FormatVersion}: bus {bus} has two sites.");
                sites.Add(new GeoBusSite(bus, Lat(Num(s, "latDeg", $"site of bus {bus}"), $"site of bus {bus}"), Lon(Num(s, "lonDeg", $"site of bus {bus}"), $"site of bus {bus}")));
            }
            var lines = new List<GeoRoute>();
            var seenBranch = new HashSet<int>();
            foreach (JsonElement l in Arr(root, "lines", "the document"))
            {
                Keys(l, "a line", "branch", "points");
                int br = Int(l, "branch", "a line");
                if (br < 1) throw new InvalidDataException($"{FormatVersion}: a line names branch {br}; branches are numbered from 1.");
                if (!seenBranch.Add(br)) throw new InvalidDataException($"{FormatVersion}: branch {br} has two routes.");
                var pts = new List<(double, double)>();
                foreach (JsonElement p in Arr(l, "points", $"the route of branch {br}"))
                {
                    if (p.ValueKind != JsonValueKind.Array || p.GetArrayLength() != 2 || p[0].ValueKind != JsonValueKind.Number || p[1].ValueKind != JsonValueKind.Number)
                        throw new InvalidDataException($"{FormatVersion}: a point of the route of branch {br} must be [latDeg, lonDeg].");
                    pts.Add((Lat(p[0].GetDouble(), $"route of branch {br}"), Lon(p[1].GetDouble(), $"route of branch {br}")));
                }
                lines.Add(new GeoRoute(br, pts));
            }
            return new GeoSidecar(sites, lines);
        }
    }

    /// <summary>The sidecar as JSON: sites by bus, lines by branch, numbers in round-trip form.</summary>
    public string ToJson()
    {
        using var ms = new MemoryStream();
        using (var w = new Utf8JsonWriter(ms, new JsonWriterOptions { Indented = true }))
        {
            w.WriteStartObject();
            w.WriteString("format", FormatVersion);
            w.WriteString("crs", Crs);
            w.WriteStartArray("sites");
            foreach (GeoBusSite s in Sites.OrderBy(s => s.Bus))
            {
                w.WriteStartObject();
                w.WriteNumber("bus", s.Bus);
                w.WriteNumber("latDeg", s.LatDeg);
                w.WriteNumber("lonDeg", s.LonDeg);
                w.WriteEndObject();
            }
            w.WriteEndArray();
            w.WriteStartArray("lines");
            foreach (GeoRoute l in Lines.OrderBy(l => l.Branch))
            {
                w.WriteStartObject();
                w.WriteNumber("branch", l.Branch);
                w.WriteStartArray("points");
                foreach ((double lat, double lon) in l.Points) { w.WriteStartArray(); w.WriteNumberValue(lat); w.WriteNumberValue(lon); w.WriteEndArray(); }
                w.WriteEndArray();
                w.WriteEndObject();
            }
            w.WriteEndArray();
            w.WriteEndObject();
        }
        return Encoding.UTF8.GetString(ms.ToArray()).Replace("\r\n", "\n", StringComparison.Ordinal) + "\n";
    }

    private static void Keys(JsonElement e, string what, params string[] allowed)
    {
        if (e.ValueKind != JsonValueKind.Object) throw new InvalidDataException($"{FormatVersion}: {what} must be a JSON object.");
        foreach (JsonProperty p in e.EnumerateObject())
            if (Array.IndexOf(allowed, p.Name) < 0) throw new InvalidDataException($"{FormatVersion}: {what} has the unknown key \"{p.Name}\" (allowed: {string.Join(", ", allowed)}).");
        foreach (string k in allowed)
            if (!e.TryGetProperty(k, out _)) throw new InvalidDataException($"{FormatVersion}: {what} has no \"{k}\".");
    }

    private static string Str(JsonElement e, string k, string what) =>
        e.GetProperty(k).ValueKind == JsonValueKind.String ? e.GetProperty(k).GetString()! : throw new InvalidDataException($"{FormatVersion}: \"{k}\" of {what} must be a string.");

    private static JsonElement.ArrayEnumerator Arr(JsonElement e, string k, string what) =>
        e.GetProperty(k).ValueKind == JsonValueKind.Array ? e.GetProperty(k).EnumerateArray() : throw new InvalidDataException($"{FormatVersion}: \"{k}\" of {what} must be an array.");

    private static int Int(JsonElement e, string k, string what) =>
        e.GetProperty(k).ValueKind == JsonValueKind.Number && e.GetProperty(k).TryGetInt32(out int v) ? v : throw new InvalidDataException($"{FormatVersion}: \"{k}\" of {what} must be an integer.");

    private static double Num(JsonElement e, string k, string what) =>
        e.GetProperty(k).ValueKind == JsonValueKind.Number ? e.GetProperty(k).GetDouble() : throw new InvalidDataException($"{FormatVersion}: \"{k}\" of {what} must be a number.");

    private static double Lat(double v, string what) =>
        double.IsFinite(v) && v >= -90 && v <= 90 ? v : throw new InvalidDataException($"{FormatVersion}: the latitude {v.ToString("R", CultureInfo.InvariantCulture)} of the {what} is outside -90..90 degrees.");

    private static double Lon(double v, string what) =>
        double.IsFinite(v) && v >= -180 && v <= 180 ? v : throw new InvalidDataException($"{FormatVersion}: the longitude {v.ToString("R", CultureInfo.InvariantCulture)} of the {what} is outside -180..180 degrees.");
}

/// <summary>What <see cref="CgmesGeographicalLocation.Read"/> found: the sidecar and the choices made.</summary>
public sealed record CgmesGeoResult(GeoSidecar Geo, IReadOnlyList<string> Notes);

/// <summary>
/// RC5 WP10: the CGMES geographical location (GL) profile, WGS 84 only (CoordinateSystem.crsUrn
/// "urn:ogc:def:crs:EPSG::4326"; PositionPoint xPosition = longitude, yPosition = latitude). <b>Export</b> (bus-branch): a
/// Location needs a PowerSystemResource, and the bus-branch document has no Substation, so each ACLineSegment whose two
/// buses have sites gets a Location whose points are its from site, its route's interior points and its to site. A site
/// at a bus that no such line touches is refused by name, and so is a route of a branch that is not written as an
/// ACLineSegment. <b>Import</b> (<see cref="Read"/>): the Locations of ACLineSegments (first point = the from bus, last =
/// the to bus, the rest the route), of Substations and VoltageLevels (every bus they contain) and of other equipment (the
/// buses of its terminals). A bus given two different points keeps the one of the Location first in mRID order, noted.
/// Another CRS is refused by name (no reprojection).
/// </summary>
public static class CgmesGeographicalLocation
{
    private const string Wgs84Urn = "urn:ogc:def:crs:EPSG::4326";
    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    internal static void WriteInto(ExtraDocument doc, GridModel model, CgmesWriteResult written, GeoSidecar? geo, List<string> refused, List<CgmesOmission> omissions, List<string> notes)
    {
        if (geo is null) { refused.Add("--profiles gl needs the geographical sidecar (--geo <file>, gridsim-geo/1)."); return; }
        var site = geo.Sites.ToDictionary(s => s.Bus);
        var busIds = model.Buses.Select(b => b.Id).ToHashSet();
        foreach (GeoBusSite s in geo.Sites.Where(s => !busIds.Contains(s.Bus)))
            refused.Add($"--profiles gl: the sidecar places bus {s.Bus}, which the case does not hold.");
        // CgmesWriter's lines: CimBranch "L<k+1>" between "N<from>" and "N<to>", in BranchOrder (lines first).
        var lineOfBranch = new Dictionary<int, CimBranch>();
        foreach (CimBranch br in written.Network.Branches) lineOfBranch[int.Parse(br.MRID[1..], Inv)] = br;
        foreach (GeoRoute l in geo.Lines.Where(l => !lineOfBranch.ContainsKey(l.Branch)))
            refused.Add($"--profiles gl: the sidecar gives a route for branch {l.Branch}, which is not written as an ACLineSegment (a transformer, out of service, or not in the case).");
        var touched = new HashSet<int>();
        var located = new List<(CimBranch Br, int From, int To)>();
        foreach (CimBranch br in written.Network.Branches)
        {
            int f = int.Parse(br.FromNode[1..], Inv), t = int.Parse(br.ToNode[1..], Inv);
            if (site.ContainsKey(f) && site.ContainsKey(t)) { located.Add((br, f, t)); touched.Add(f); touched.Add(t); }
        }
        foreach (GeoBusSite s in geo.Sites.Where(s => busIds.Contains(s.Bus) && !touched.Contains(s.Bus)))
            refused.Add($"--profiles gl: bus {s.Bus} has a site but no ACLineSegment with sites at both ends touches it; the bus-branch export has no Substation to carry a bus's location.");
        foreach (GeoRoute l in geo.Lines)
            if (lineOfBranch.TryGetValue(l.Branch, out CimBranch? br) && !located.Any(x => x.Br.MRID == br.MRID))
                refused.Add($"--profiles gl: branch {l.Branch} has a route but its buses do not both have sites.");
        if (refused.Count > 0) return;

        string crs = doc.NewId("crs");
        doc.New("CoordinateSystem", crs, ("IdentifiedObject.name", "WGS 84"), ("CoordinateSystem.crsUrn", Wgs84Urn));
        var route = geo.Lines.ToDictionary(l => l.Branch);
        foreach ((CimBranch br, int f, int t) in located)
        {
            string loc = doc.NewId(br.MRID);
            doc.New("Location", loc, ("IdentifiedObject.name", br.Name), ("Location.CoordinateSystem", ExtraDocument.Ref(crs)),
                ("Location.PowerSystemResources", doc.RefObject(br.MRID)));
            var pts = new List<(double Lat, double Lon)> { (site[f].LatDeg, site[f].LonDeg) };
            if (route.TryGetValue(int.Parse(br.MRID[1..], Inv), out GeoRoute? r)) pts.AddRange(r.Points);
            pts.Add((site[t].LatDeg, site[t].LonDeg));
            for (int i = 0; i < pts.Count; i++)
                doc.New("PositionPoint", doc.NewId(br.MRID + "/point/" + (i + 1).ToString(Inv)), ("PositionPoint.Location", ExtraDocument.Ref(loc)),
                    ("PositionPoint.sequenceNumber", (i + 1).ToString(Inv)),
                    ("PositionPoint.xPosition", CgmesExtraProfiles.Num(pts[i].Lon)), ("PositionPoint.yPosition", CgmesExtraProfiles.Num(pts[i].Lat)));
        }
        omissions.Add(new CgmesOmission(CgmesProfileKind.GL, "Location of a bus as such",
            "the bus-branch export has no Substation; each bus's site is carried as an end point of the ACLineSegments that join it to another located bus."));
        notes.Add($"GL: {located.Count} ACLineSegment location(s) written (WGS 84).");
    }

    /// <summary>The geographical data the GL documents among <paramref name="documents"/> give for <paramref name="bridge"/>'s
    /// buses and branches (the EQ and TP documents are read for containment and terminals).</summary>
    public static CgmesGeoResult Read(IEnumerable<string> documents, CimBridgeResult bridge)
    {
        ArgumentNullException.ThrowIfNull(documents);
        ArgumentNullException.ThrowIfNull(bridge);
        CgmesObjectSet set = CgmesObjectSet.Parse(documents);
        var notes = new List<string>();
        var crsOk = new HashSet<string>(StringComparer.Ordinal);
        foreach (CgmesObject c in set.OfClass("CoordinateSystem"))
        {
            string urn = c.Text("CoordinateSystem.crsUrn") ?? "";
            if (urn is not (Wgs84Urn or "urn:ogc:def:crs:EPSG:4326"))
                throw new InvalidDataException($"CoordinateSystem '{c.Mrid}' is '{urn}'; only WGS 84 ({Wgs84Urn}) is read, and no reprojection is made.");
            crsOk.Add(c.Mrid);
        }
        var pointsOf = new Dictionary<string, List<(int? Seq, int Order, double Lat, double Lon)>>(StringComparer.Ordinal);
        int order = 0;
        foreach (CgmesObject p in set.OfClass("PositionPoint"))
        {
            string? loc = p.Ref("PositionPoint.Location");
            if (loc is null) continue;
            double lon = CgmesObjectSet.Double(p, "PositionPoint.xPosition") ?? throw new InvalidDataException($"PositionPoint '{p.Mrid}' has no xPosition.");
            double lat = CgmesObjectSet.Double(p, "PositionPoint.yPosition") ?? throw new InvalidDataException($"PositionPoint '{p.Mrid}' has no yPosition.");
            string? seqText = p.Text("PositionPoint.sequenceNumber");
            int? seq = seqText is not null && int.TryParse(seqText, NumberStyles.Integer, Inv, out int s) ? s : null;
            if (!pointsOf.TryGetValue(loc, out var list)) pointsOf[loc] = list = new();
            list.Add((seq, order++, lat, lon));
        }

        var lineIndex = new Dictionary<string, CimBranch>(StringComparer.Ordinal);
        foreach (CimBranch br in bridge.Network.Branches) lineIndex[br.MRID] = br;
        var sites = new Dictionary<int, (double Lat, double Lon, string Loc)>();
        var lines = new List<GeoRoute>();
        int conflicts = 0, unplaced = 0;
        void Site(int bus, double lat, double lon, string loc)
        {
            if (!sites.TryGetValue(bus, out var had)) { sites[bus] = (lat, lon, loc); return; }
            if (!CgmesExtraProfiles.Same(had.Lat, lat) || !CgmesExtraProfiles.Same(had.Lon, lon)) conflicts++;
        }
        IEnumerable<int> BusesOfContainer(string mrid)
        {
            var levels = new HashSet<string>(StringComparer.Ordinal) { mrid };
            foreach (CgmesObject v in set.OfClass("VoltageLevel")) if (v.Ref("VoltageLevel.Substation") == mrid) levels.Add(v.Mrid);
            var found = new SortedSet<int>();
            foreach (CgmesObject tn in set.OfClass("TopologicalNode"))
                if (tn.Ref("TopologicalNode.ConnectivityNodeContainer") is string c && levels.Contains(c) && bridge.BusIdOfNode.TryGetValue(tn.Mrid, out int b)) found.Add(b);
            foreach (CgmesObject cn in set.OfClass("ConnectivityNode"))
                if (cn.Ref("ConnectivityNode.ConnectivityNodeContainer") is string c && levels.Contains(c) && cn.Ref("ConnectivityNode.TopologicalNode") is string tnr
                    && bridge.BusIdOfNode.TryGetValue(tnr, out int b)) found.Add(b);
            return found;
        }
        IEnumerable<int> BusesOfEquipment(string mrid)
        {
            var found = new SortedSet<int>();
            foreach (CgmesObject t in set.OfClass("Terminal"))
            {
                if (t.Ref("Terminal.ConductingEquipment") != mrid) continue;
                string? node = t.Ref("Terminal.TopologicalNode");
                if (node is null && t.Ref("Terminal.ConnectivityNode") is string cn) node = set.Get(cn)?.Ref("ConnectivityNode.TopologicalNode");
                if (node is not null && bridge.BusIdOfNode.TryGetValue(node, out int b)) found.Add(b);
            }
            return found;
        }

        foreach (CgmesObject loc in set.OfClass("Location").OrderBy(l => l.Mrid, StringComparer.Ordinal))
        {
            string? crs = loc.Ref("Location.CoordinateSystem");
            if (crs is null || !crsOk.Contains(crs)) throw new InvalidDataException($"Location '{loc.Mrid}' names no WGS 84 CoordinateSystem of the documents.");
            if (!pointsOf.TryGetValue(loc.Mrid, out var raw) || raw.Count == 0) continue;
            var pts = raw.OrderBy(p => p.Seq ?? int.MaxValue).ThenBy(p => p.Order).ToList();
            string? psr = loc.Ref("Location.PowerSystemResources");
            if (psr is null) { unplaced++; continue; }
            if (lineIndex.TryGetValue(psr, out CimBranch? br) && bridge.BranchIndexOfMrid.TryGetValue(psr, out int bi))
            {
                Site(bridge.BusIdOfNode[br.FromNode], pts[0].Lat, pts[0].Lon, loc.Mrid);
                Site(bridge.BusIdOfNode[br.ToNode], pts[^1].Lat, pts[^1].Lon, loc.Mrid);
                if (pts.Count > 2) lines.Add(new GeoRoute(bi + 1, pts.Skip(1).Take(pts.Count - 2).Select(p => (p.Lat, p.Lon)).ToList()));
                continue;
            }
            string? cls = set.Get(psr)?.Class;
            IEnumerable<int> buses = cls is "Substation" or "VoltageLevel" ? BusesOfContainer(psr) : BusesOfEquipment(psr);
            bool any = false;
            foreach (int b in buses) { Site(b, pts[0].Lat, pts[0].Lon, loc.Mrid); any = true; }
            if (!any) unplaced++;
        }
        if (conflicts > 0) notes.Add($"{conflicts} bus position(s) given twice with different points; each bus keeps the point of the Location first in mRID order.");
        if (unplaced > 0) notes.Add($"{unplaced} Location(s) name no bus or line of the case and are left out.");
        var geo = new GeoSidecar(sites.OrderBy(kv => kv.Key).Select(kv => new GeoBusSite(kv.Key, kv.Value.Lat, kv.Value.Lon)).ToList(),
            lines.OrderBy(l => l.Branch).ToList());
        notes.Add($"GL: {geo.Sites.Count} bus site(s) and {geo.Lines.Count} route(s) read.");
        return new CgmesGeoResult(geo, notes);
    }
}
