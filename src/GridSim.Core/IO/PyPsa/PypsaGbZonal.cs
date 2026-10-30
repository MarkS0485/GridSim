using System.Globalization;
using GridSim.Core.IO.Geo;
using GridSim.Core.Model;

namespace GridSim.Core.IO.PyPsa;

/// <summary>One zone of the PyPSA-GB zonal model: its name, representative point and its place on the GB map layout.</summary>
/// <param name="Name">The zone name (Z1_1 .. Z17).</param>
/// <param name="LatDeg">WGS84 latitude of the zone's representative point (buses.csv column y).</param>
/// <param name="LonDeg">WGS84 longitude (buses.csv column x).</param>
/// <param name="VNomKv">The nominal voltage the file gives (400 kV for every zone; nothing in the model uses it).</param>
/// <param name="X">GB map layout x (<see cref="OsgbGrid.ToGbLayout"/>).</param>
/// <param name="Y">GB map layout y.</param>
public sealed record PypsaGbZone(string Name, double LatDeg, double LonDeg, double VNomKv, double X, double Y)
{
    /// <summary>Bug hunt 2026-10-29, finding 56: true when the zone's point lies outside the GB layout box, so
    /// <see cref="X"/> / <see cref="Y"/> are the box edge it was clamped to, not its place (Z1_1, Shetland, is drawn on
    /// the 59.0 N edge). Not part of the network data; the zonal verb marks such a zone.</summary>
    public bool LayoutClamped { get; init; }
}

/// <summary>The transfer capacity across one boundary of the zonal model: the links that cross it and their summed
/// capacities each way.</summary>
/// <param name="Name">The boundary name (the gb-full name, for example "B6 Scotland-England (SCOTEX)").</param>
/// <param name="Links">The crossing links; Direction +1 when the link's bus0 is on the north side.</param>
/// <param name="NorthToSouthMw">The most the crossing links can carry north to south (sum of each link's capacity in
/// that direction: p_nom forwards, -p_min_pu x p_nom backwards).</param>
/// <param name="SouthToNorthMw">The same, south to north.</param>
public sealed record PypsaGbZonalBoundary(string Name, IReadOnlyList<BoundaryBranch> Links, double NorthToSouthMw, double SouthToNorthMw);

/// <summary>
/// The PyPSA-GB zonal model as GridSim imports it. It is a TRANSPORT model: zones joined by links that have a MW
/// capacity and no impedance. It has no AC (or DC) power flow, so it is not a <see cref="GridModel"/> and no solver
/// verb can run on it (<see cref="IsAcSolvable"/> is false); the CLI's <c>zonal</c> verb shows it.
/// </summary>
public sealed record PypsaGbZonalNetwork(
    string Name, IReadOnlyList<PypsaGbZone> Zones, IReadOnlyList<PypsaGbLink> Links,
    IReadOnlyList<PypsaGbZonalBoundary> Boundaries, IReadOnlyList<string> Notes)
{
    /// <summary>Always false: a transport model cannot be AC-solved.</summary>
    public static bool IsAcSolvable => false;

    /// <summary>Why it cannot be solved, in words a refusal message can quote.</summary>
    public const string NotAcSolvableReason =
        "it is a zonal transport model (zones joined by links that have a MW capacity and no impedance), so it has no AC or DC power flow";
}

/// <summary>
/// Importer for the PyPSA-GB zonal model (andrewlyden/PyPSA-GB, data/network/zonal: buses.csv, links.csv; MIT
/// licence). S2 / P02, October 2026.
/// </summary>
/// <remarks>
/// buses.csv lists the 20 zones (name, latitude y, longitude x, v_nom, carrier); links.csv the 37 links (name, bus0,
/// bus1, carrier, p_nom, p_min_pu): 31 between zones and 6 interconnectors to foreign nodes. The importer checks the
/// headers exactly, every number, every zone name's uniqueness and every link end (a link must touch at least one
/// zone), and reads the boundary cuts from boundaries.json when present (derived from the official ETYS boundary GIS
/// lines by tools/gb-reference/derive_boundaries.py), turning each into a two-way transfer capacity.
/// </remarks>
public static class PypsaGbZonal
{
    /// <summary>The zone file.</summary>
    public const string BusesFile = "buses.csv";
    /// <summary>The link file.</summary>
    public const string LinksFile = "links.csv";

    private static readonly string[] BusesHeader = { "name", "y", "x", "v_nom", "carrier" };
    private static readonly string[] LinksHeader = { "name", "bus0", "bus1", "carrier", "p_nom", "p_min_pu" };

    /// <summary>
    /// Import the zonal model in <paramref name="folder"/>.
    /// </summary>
    /// <exception cref="FileNotFoundException">buses.csv or links.csv is missing.</exception>
    /// <exception cref="InvalidDataException">A file is malformed, a link touches no zone, or a boundary names a link
    /// the model does not have.</exception>
    public static PypsaGbZonalNetwork Load(string folder, string name = "pypsa-gb-zonal")
    {
        ArgumentNullException.ThrowIfNull(folder);
        if (!Directory.Exists(folder))
            throw new DirectoryNotFoundException($"PyPSA-GB zonal model folder not found: '{folder}' (full path {Path.GetFullPath(folder)}).");

        var zones = new List<PypsaGbZone>();
        var zoneNames = new HashSet<string>(StringComparer.Ordinal);
        foreach (PypsaCsv.Row r in PypsaCsv.Read(Path.Combine(folder, BusesFile), BusesHeader))
        {
            string zn = r.Fields[0];
            if (zn.Length == 0) throw new InvalidDataException($"{BusesFile} line {r.Line}: the zone name is empty.");
            if (!zoneNames.Add(zn)) throw new InvalidDataException($"{BusesFile} line {r.Line}: zone '{zn}' is listed twice.");
            double lat = PypsaCsv.Number(r, 1, "y", BusesFile), lon = PypsaCsv.Number(r, 2, "x", BusesFile);
            if (lat is < -90 or > 90 || lon is < -180 or > 180)
                throw new InvalidDataException($"{BusesFile} line {r.Line}: zone '{zn}' is at latitude {lat.ToString("R", CultureInfo.InvariantCulture)}, longitude {lon.ToString("R", CultureInfo.InvariantCulture)}, which is not a place on Earth (degrees expected).");
            double kv = PypsaCsv.Number(r, 3, "v_nom", BusesFile);
            (double x, double y) = OsgbGrid.ToGbLayout(lat, lon);
            zones.Add(new PypsaGbZone(zn, lat, lon, kv, x, y) { LayoutClamped = !OsgbGrid.IsInsideGbLayout(lat, lon) });
        }
        if (zones.Count == 0) throw new InvalidDataException($"{BusesFile}: no zones.");

        var links = new List<PypsaGbLink>();
        var linkNames = new HashSet<string>(StringComparer.Ordinal);
        foreach (PypsaCsv.Row r in PypsaCsv.Read(Path.Combine(folder, LinksFile), LinksHeader))
        {
            string ln = r.Fields[0];
            if (!linkNames.Add(ln)) throw new InvalidDataException($"{LinksFile} line {r.Line}: link '{ln}' is listed twice.");
            bool in0 = zoneNames.Contains(r.Fields[1]), in1 = zoneNames.Contains(r.Fields[2]);
            if (!in0 && !in1)
                throw new InvalidDataException($"{LinksFile} line {r.Line}: link '{ln}' joins '{r.Fields[1]}' and '{r.Fields[2]}', neither of which is a zone of {BusesFile}.");
            if (r.Fields[1] == r.Fields[2])
                throw new InvalidDataException($"{LinksFile} line {r.Line}: link '{ln}' starts and ends at '{r.Fields[1]}'.");
            if (r.Fields[3] is not ("AC" or "DC"))
                throw new InvalidDataException($"{LinksFile} line {r.Line}: link '{ln}' has carrier '{r.Fields[3]}'; AC or DC expected.");
            double p = PypsaCsv.Number(r, 4, "p_nom", LinksFile), pmin = PypsaCsv.Number(r, 5, "p_min_pu", LinksFile);
            if (p < 0) throw new InvalidDataException($"{LinksFile} line {r.Line}: link '{ln}' has p_nom {p.ToString("R", CultureInfo.InvariantCulture)} MW; a capacity cannot be negative.");
            if (pmin is < -1 or > 1)
                throw new InvalidDataException($"{LinksFile} line {r.Line}: link '{ln}' has p_min_pu {pmin.ToString("R", CultureInfo.InvariantCulture)}; PyPSA's p_min_pu lies in [-1, 1].");
            links.Add(new PypsaGbLink(ln, r.Fields[1], r.Fields[2], r.Fields[3], p, pmin, External: !(in0 && in1)));
        }

        // Boundary cuts: each crossing link adds its capacity in each direction (forwards p_nom; backwards -p_min_pu x
        // p_nom, which is p_nom for the bundled links, all p_min_pu = -1). Direction +1 means bus0 is north, so the
        // link's forward capacity counts north to south.
        IReadOnlyList<Boundary> cuts = PypsaGbReduced.ReadBoundaries(Path.Combine(folder, PypsaGbReduced.BoundariesFile), linkNames);
        var byName = links.ToDictionary(l => l.Name, StringComparer.Ordinal);
        var boundaries = new List<PypsaGbZonalBoundary>(cuts.Count);
        foreach (Boundary b in cuts)
        {
            double ns = 0, sn = 0;
            foreach (BoundaryBranch bb in b.Branches)
            {
                PypsaGbLink l = byName[bb.Branch];
                double forward = l.PNomMw, backward = -l.PMinPu * l.PNomMw;
                if (bb.Direction == 1) { ns += forward; sn += backward; }
                else { ns += backward; sn += forward; }
            }
            boundaries.Add(new PypsaGbZonalBoundary(b.Name, b.Branches, ns, sn));
        }

        int external = links.Count(l => l.External);
        var notes = new List<string>
        {
            $"{zones.Count} zone(s), {links.Count} link(s): {links.Count - external} between zones, {external} interconnector(s) to a foreign node.",
            $"not AC solvable: {PypsaGbZonalNetwork.NotAcSolvableReason}.",
        };
        if (boundaries.Count > 0)
            notes.Add($"{boundaries.Count} boundary cut(s) read from {PypsaGbReduced.BoundariesFile} (derived from the ETYS boundary GIS lines): capacities only, no flows.");
        return new PypsaGbZonalNetwork(name, zones, links, boundaries, notes);
    }
}
