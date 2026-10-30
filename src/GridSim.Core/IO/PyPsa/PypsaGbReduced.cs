using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using GridSim.Core.IO.Geo;
using GridSim.Core.Model;

namespace GridSim.Core.IO.PyPsa;

/// <summary>One PyPSA-GB substation (reduced network bus): its name, nominal voltage and position.</summary>
/// <param name="Name">The PyPSA-GB bus name (for example "Beauly").</param>
/// <param name="VNomKv">The nominal voltage from buses.csv (kV).</param>
/// <param name="EastingM">OSGB36 National Grid easting from buses.csv (metres).</param>
/// <param name="NorthingM">OSGB36 National Grid northing from buses.csv (metres).</param>
/// <param name="LatDeg">WGS84 latitude converted from the easting / northing (<see cref="OsgbGrid.ToWgs84"/>).</param>
/// <param name="LonDeg">WGS84 longitude, likewise.</param>
public sealed record PypsaGbBus(string Name, double VNomKv, double EastingM, double NorthingM, double LatDeg, double LonDeg);

/// <summary>One PyPSA link (a DC interconnector in the reduced network; a zone-to-zone transfer in the zonal one).</summary>
/// <param name="Name">The link name from links.csv.</param>
/// <param name="Bus0">The bus (or zone) at the link's "0" end.</param>
/// <param name="Bus1">The bus (or zone) at the link's "1" end.</param>
/// <param name="Carrier">"AC" or "DC" as the file states it (a zonal "AC" link is still a transport link: it has no
/// impedance, only a capacity).</param>
/// <param name="PNomMw">The link capacity p_nom (MW).</param>
/// <param name="PMinPu">p_min_pu: the lowest flow as a fraction of p_nom (-1 = the same capacity both ways; 0 = one way
/// only). The reduced network's links.csv has no such column; it is then 0, PyPSA's default.</param>
/// <param name="External">True when one end is not a bus (or zone) of the network: an interconnector to a foreign node.</param>
public sealed record PypsaGbLink(string Name, string Bus0, string Bus1, string Carrier, double PNomMw, double PMinPu, bool External);

/// <summary>
/// The PyPSA-GB reduced network as GridSim imports it: an AC-solvable <see cref="GridModel"/> plus the source facts the
/// model cannot hold (the substation coordinates, the interconnector links) and the import notes.
/// </summary>
public sealed record PypsaGbReducedNetwork(GridModel Model, IReadOnlyList<PypsaGbBus> Buses, IReadOnlyList<PypsaGbLink> Links, IReadOnlyList<string> Notes);

/// <summary>
/// Importer for the PyPSA-GB "reduced" network (andrewlyden/PyPSA-GB, data/network/reduced_network: buses.csv,
/// lines.csv, links.csv; MIT licence). S2 / P02, October 2026.
/// </summary>
/// <remarks>
/// <para>
/// What the CSV files hold, and what they do not. PyPSA-GB's reduced network is the 29-substation Strathclyde
/// representation of GB (the same buses as GBreducednetwork.m, in the same order) with 99 AC lines given as per-unit
/// r, x, b on a 100 MVA base and a rating s_nom (MVA), the substations' OSGB coordinates, and six DC interconnector
/// links to foreign nodes. It holds NO demand and NO generation: PyPSA-GB attaches those from its own time-series
/// pipelines. A power flow needs both, so this importer takes them from an "operating point" model whose bus i is
/// buses.csv row i (the bundled GBreducednetwork.m, by the shared bus order) and says so in the notes.
/// </para>
/// <para>
/// What the importer builds: buses from buses.csv (name, nominal kV, map position) carrying the operating point's
/// type, demand, shunts, starting voltage and voltage limits; the operating point's generators unchanged; the 99 lines
/// from lines.csv in file order (plain lines: lines.csv has no tap ratio or phase-shift column), named
/// <see cref="BranchName"/>; the boundary cuts from boundaries.json when present (derived from the official ETYS
/// boundary GIS lines by tools/gb-reference/derive_boundaries.py). The six links are carried in the result and NOT
/// modelled: no GridSim solver has an HVDC element and the files give no transfer setpoint.
/// </para>
/// <para>
/// The import is checked against the operating point's own branch list and every difference is written into the notes
/// (it does not stop the import: PyPSA-GB is a different model, and the notes say how). On the bundled data the
/// differences are: PyPSA-GB routes the two Keadby lines that GBreducednetwork.m runs to Walpole to Feckenham instead,
/// drops the Errochty-Peterhead line and adds a near-zero-impedance Neilston-Deeside line (x = 0.0001 pu, PyPSA-GB's
/// stand-in for the Western HVDC link), carries none of the .m file's four 2-degree phase shifters, and gives Errochty
/// 275 kV where the .m file says 132 kV.
/// </para>
/// </remarks>
public static class PypsaGbReduced
{
    /// <summary>The bus file and its exact header.</summary>
    public const string BusesFile = "buses.csv";
    /// <summary>The line file and its exact header.</summary>
    public const string LinesFile = "lines.csv";
    /// <summary>The interconnector-link file and its exact header.</summary>
    public const string LinksFile = "links.csv";
    /// <summary>The optional boundary file (GridSim boundary format, written by tools/gb-reference/derive_boundaries.py).</summary>
    public const string BoundariesFile = "boundaries.json";

    private static readonly string[] BusesHeader = { "name", "v_nom", "carrier", "x", "y" };
    private static readonly string[] LinesHeader = { "name", "bus0", "bus1", "r", "x", "b", "s_nom" };
    private static readonly string[] LinksHeader = { "name", "bus0", "bus1", "carrier", "p_nom" };

    /// <summary>The per-unit base of lines.csv: PyPSA-GB's reduced lines are the Strathclyde data's per-unit values on
    /// 100 MVA (96 of the 99 equal GBreducednetwork.m's numbers exactly), so the operating point must use the same base.</summary>
    public const double LineBaseMva = 100.0;

    /// <summary>The name a lines.csv row gets as a GridSim branch: "&lt;bus0&gt;-&lt;bus1&gt; #&lt;name&gt;", for example
    /// "Eccles-Stella West #23". tools/gb-reference/derive_boundaries.py writes boundary branches with the same rule.</summary>
    public static string BranchName(string bus0, string bus1, string name) => $"{bus0}-{bus1} #{name}";

    /// <summary>
    /// Import the reduced network in <paramref name="folder"/>, taking demand and generation from
    /// <paramref name="operatingPoint"/> (bus i = buses.csv row i).
    /// </summary>
    /// <param name="folder">A folder holding buses.csv, lines.csv and links.csv (boundaries.json optional).</param>
    /// <param name="operatingPoint">The model supplying bus types, demand, shunts, starting voltages, voltage limits and
    /// generators: it must have as many buses as buses.csv, numbered 1..N in order, on a 100 MVA base.</param>
    /// <param name="name">The model name.</param>
    /// <exception cref="FileNotFoundException">A required file is missing.</exception>
    /// <exception cref="InvalidDataException">A file is malformed, a line names an unknown bus, the operating point
    /// does not line up with buses.csv, or a boundary names a branch the network does not have.</exception>
    public static PypsaGbReducedNetwork Load(string folder, GridModel operatingPoint, string name = "pypsa-gb-reduced")
    {
        ArgumentNullException.ThrowIfNull(folder);
        ArgumentNullException.ThrowIfNull(operatingPoint);
        if (!Directory.Exists(folder))
            throw new DirectoryNotFoundException($"PyPSA-GB reduced network folder not found: '{folder}' (full path {Path.GetFullPath(folder)}).");
        var notes = new List<string>();

        // ---- buses.csv ------------------------------------------------------------------------------------------------
        var buses = new List<PypsaGbBus>();
        var busIndex = new Dictionary<string, int>(StringComparer.Ordinal);   // name -> 0-based row
        foreach (PypsaCsv.Row r in PypsaCsv.Read(Path.Combine(folder, BusesFile), BusesHeader))
        {
            string bn = r.Fields[0];
            if (bn.Length == 0) throw new InvalidDataException($"{BusesFile} line {r.Line}: the bus name is empty.");
            if (!busIndex.TryAdd(bn, buses.Count)) throw new InvalidDataException($"{BusesFile} line {r.Line}: bus '{bn}' is listed twice.");
            if (r.Fields[2] != "AC")
                throw new InvalidDataException($"{BusesFile} line {r.Line}: bus '{bn}' has carrier '{r.Fields[2]}'; the reduced network's buses are all AC.");
            double kv = PypsaCsv.Number(r, 1, "v_nom", BusesFile);
            if (!(kv > 0)) throw new InvalidDataException($"{BusesFile} line {r.Line}: bus '{bn}' has v_nom {kv.ToString("R", CultureInfo.InvariantCulture)} kV; it must be above 0.");
            double e = PypsaCsv.Number(r, 3, "x", BusesFile), n = PypsaCsv.Number(r, 4, "y", BusesFile);
            (double lat, double lon) = OsgbGrid.ToWgs84(e, n);
            buses.Add(new PypsaGbBus(bn, kv, e, n, lat, lon));
        }
        if (buses.Count == 0) throw new InvalidDataException($"{BusesFile}: no buses.");

        // ---- the operating point must line up with buses.csv -----------------------------------------------------------
        if (operatingPoint.Buses.Count != buses.Count)
            throw new InvalidDataException(
                $"the operating point '{operatingPoint.Name}' has {operatingPoint.Buses.Count} buses but {BusesFile} has {buses.Count}; bus i of the operating point must be row i of {BusesFile}.");
        for (int i = 0; i < buses.Count; i++)
            if (operatingPoint.Buses[i].Id != i + 1)
                throw new InvalidDataException(
                    $"the operating point '{operatingPoint.Name}' numbers its bus {i + 1} as {operatingPoint.Buses[i].Id}; buses must be numbered 1..{buses.Count} in {BusesFile} order.");
        if (operatingPoint.BaseMva != LineBaseMva)
            throw new InvalidDataException(
                $"the operating point '{operatingPoint.Name}' is on a {operatingPoint.BaseMva.ToString("R", CultureInfo.InvariantCulture)} MVA base; {LinesFile} is per unit on {LineBaseMva:0} MVA.");

        // ---- lines.csv ------------------------------------------------------------------------------------------------
        var branches = new List<Branch>();
        var lineNames = new HashSet<string>(StringComparer.Ordinal);
        foreach (PypsaCsv.Row r in PypsaCsv.Read(Path.Combine(folder, LinesFile), LinesHeader))
        {
            string ln = r.Fields[0];
            if (!lineNames.Add(ln)) throw new InvalidDataException($"{LinesFile} line {r.Line}: line name '{ln}' is listed twice.");
            int f = BusRow(busIndex, r.Fields[1], LinesFile, r.Line), t = BusRow(busIndex, r.Fields[2], LinesFile, r.Line);
            if (f == t) throw new InvalidDataException($"{LinesFile} line {r.Line}: line '{ln}' starts and ends at bus '{r.Fields[1]}'.");
            double rr = PypsaCsv.Number(r, 3, "r", LinesFile), x = PypsaCsv.Number(r, 4, "x", LinesFile);
            double b = PypsaCsv.Number(r, 5, "b", LinesFile), s = PypsaCsv.Number(r, 6, "s_nom", LinesFile);
            if (rr < 0) throw new InvalidDataException($"{LinesFile} line {r.Line}: line '{ln}' has r {rr.ToString("R", CultureInfo.InvariantCulture)} pu; resistance cannot be negative.");
            if (x == 0) throw new InvalidDataException($"{LinesFile} line {r.Line}: line '{ln}' has x = 0 pu; a line needs a series reactance.");
            if (s < 0) throw new InvalidDataException($"{LinesFile} line {r.Line}: line '{ln}' has s_nom {s.ToString("R", CultureInfo.InvariantCulture)} MVA; a rating cannot be negative.");
            branches.Add(new Branch(
                From: f + 1, To: t + 1, R: rr, X: x, B: b,
                TapRatio: 1.0, PhaseShiftDeg: 0.0, RateAMva: s,
                Name: BranchName(r.Fields[1], r.Fields[2], ln), IsTransformer: false));
        }
        if (branches.Count == 0) throw new InvalidDataException($"{LinesFile}: no lines.");

        // ---- links.csv (carried, not modelled) ------------------------------------------------------------------------
        var links = new List<PypsaGbLink>();
        foreach (PypsaCsv.Row r in PypsaCsv.Read(Path.Combine(folder, LinksFile), LinksHeader))
        {
            bool in0 = busIndex.ContainsKey(r.Fields[1]), in1 = busIndex.ContainsKey(r.Fields[2]);
            if (!in0 && !in1)
                throw new InvalidDataException($"{LinksFile} line {r.Line}: link '{r.Fields[0]}' joins '{r.Fields[1]}' and '{r.Fields[2]}', neither of which is a bus of {BusesFile}.");
            double p = PypsaCsv.Number(r, 4, "p_nom", LinksFile);
            if (p < 0) throw new InvalidDataException($"{LinksFile} line {r.Line}: link '{r.Fields[0]}' has p_nom {p.ToString("R", CultureInfo.InvariantCulture)} MW; a capacity cannot be negative.");
            links.Add(new PypsaGbLink(r.Fields[0], r.Fields[1], r.Fields[2], r.Fields[3], p, 0.0, External: !(in0 && in1)));
        }

        // ---- the model --------------------------------------------------------------------------------------------------
        var modelBuses = new List<Bus>(buses.Count);
        var clamped = new List<string>();   // finding 56: buses drawn on the layout box edge, not at their place
        for (int i = 0; i < buses.Count; i++)
        {
            Bus op = operatingPoint.Buses[i];
            (double lx, double ly) = OsgbGrid.ToGbLayout(buses[i].LatDeg, buses[i].LonDeg);
            if (!OsgbGrid.IsInsideGbLayout(buses[i].LatDeg, buses[i].LonDeg)) clamped.Add($"bus {i + 1} '{buses[i].Name}'");
            modelBuses.Add(op with { BaseKv = buses[i].VNomKv, Name = buses[i].Name, X = lx, Y = ly });
            if (op.BaseKv != buses[i].VNomKv)
                notes.Add($"bus {i + 1} '{buses[i].Name}': {BusesFile} gives {buses[i].VNomKv.ToString("R", CultureInfo.InvariantCulture)} kV where the operating point '{operatingPoint.Name}' gives {op.BaseKv.ToString("R", CultureInfo.InvariantCulture)} kV; {BusesFile} is used (base kV does not enter the per-unit power flow).");
        }

        if (clamped.Count > 0)
            notes.Add($"{clamped.Count} bus(es) lie outside the GB layout box and are drawn on its edge: {string.Join(", ", clamped)}.");

        var lineBranchNames = new HashSet<string>(branches.Select(x => x.Name!), StringComparer.Ordinal);
        IReadOnlyList<Boundary> boundaries = ReadBoundaries(Path.Combine(folder, BoundariesFile), lineBranchNames);

        var model = new GridModel
        {
            Name = name,
            BaseMva = operatingPoint.BaseMva,
            FrequencyHz = operatingPoint.FrequencyHz,
            Buses = modelBuses,
            Generators = operatingPoint.AllGenerators,   // the whole fleet: the init setter holds out-of-service units back
            Branches = branches,
            Boundaries = boundaries,
        };
        var errors = model.Validate().Where(d => d.Severity == ValidationSeverity.Error).Select(d => d.Message).ToList();
        if (errors.Count > 0)
            throw new InvalidDataException($"PyPSA-GB reduced network '{name}' is invalid:" + Environment.NewLine + "  " + string.Join(Environment.NewLine + "  ", errors));

        // ---- notes: how this differs from the operating point's own network --------------------------------------------
        notes.Insert(0, $"demand, generation, bus types, starting voltages and voltage limits are taken from '{operatingPoint.Name}' (bus i = {BusesFile} row i); PyPSA-GB's network files carry none of them.");
        CompareBranches(branches, operatingPoint.Branches, operatingPoint.Name, notes);
        int external = links.Count(l => l.External);
        if (links.Count > 0)
            notes.Add($"{links.Count} link(s) in {LinksFile} ({external} to a foreign node) are carried in the import result but NOT modelled: GridSim has no HVDC element and the files give no transfer setpoint.");
        if (boundaries.Count > 0)
            notes.Add($"{boundaries.Count} boundary cut(s) read from {BoundariesFile} (derived from the ETYS boundary GIS lines; no transfer limit is set).");
        return new PypsaGbReducedNetwork(model, buses, links, notes);
    }

    // The 0-based buses.csv row of a bus name, refusing an unknown one.
    private static int BusRow(Dictionary<string, int> index, string bus, string file, int line)
        => index.TryGetValue(bus, out int i) ? i : throw new InvalidDataException($"{file} line {line}: bus '{bus}' is not in {BusesFile}.");

    /// <summary>
    /// Match each imported line to an unused branch of the operating point with the same two ends (either orientation)
    /// and the same r, x, b and rating, taking the first such branch in the operating point's order (deterministic), and
    /// note every line with no match, every operating-point branch left unmatched, and every matched branch whose tap
    /// ratio or phase shift lines.csv cannot carry.
    /// </summary>
    private static void CompareBranches(IReadOnlyList<Branch> lines, IReadOnlyList<Branch> op, string opName, List<string> notes)
    {
        var used = new bool[op.Count];
        var unmatched = new List<string>();
        var shifted = new List<string>();
        foreach (Branch l in lines)
        {
            int k = -1;
            for (int j = 0; j < op.Count && k < 0; j++)
            {
                Branch o = op[j];
                bool ends = (o.From == l.From && o.To == l.To) || (o.From == l.To && o.To == l.From);
                if (!used[j] && ends && o.R == l.R && o.X == l.X && o.B == l.B && o.RateAMva == l.RateAMva) k = j;
            }
            if (k < 0) { unmatched.Add($"'{l.Name}' ({l.From}-{l.To})"); continue; }
            used[k] = true;
            Branch m = op[k];
            if (m.IsTransformer || m.PhaseShiftDeg != 0.0)
                shifted.Add($"'{l.Name}' = branch {k + 1} ({m.From}-{m.To}, ratio {m.TapRatio.ToString("R", CultureInfo.InvariantCulture)}, shift {m.PhaseShiftDeg.ToString("R", CultureInfo.InvariantCulture)} deg)");
        }
        var left = Enumerable.Range(0, op.Count).Where(j => !used[j]).Select(j => $"branch {j + 1} ({op[j].From}-{op[j].To})").ToList();
        int matched = lines.Count - unmatched.Count;
        notes.Add($"{matched} of {lines.Count} line(s) equal a branch of '{opName}' (same ends, r, x, b and rating); {unmatched.Count} do not, and {left.Count} of its {op.Count} branch(es) have no counterpart here.");
        if (unmatched.Count > 0) notes.Add($"lines with no counterpart in '{opName}': {string.Join(", ", unmatched)}.");
        if (left.Count > 0) notes.Add($"'{opName}' branches with no counterpart here: {string.Join(", ", left)}.");
        if (shifted.Count > 0)
            notes.Add($"{shifted.Count} matched line(s) are a tap / phase-shifting transformer in '{opName}', which {LinesFile} cannot express (no tap or shift column); they are plain lines here: {string.Join("; ", shifted)}.");
    }

    // ---- boundaries.json ------------------------------------------------------------------------------------------------

    // Phase 0.1: the wire DTOs below are internal rather than private only so the source-generated JSON context, a
    // separate class, can reach them; nothing else changes.
    internal sealed class BoundaryFile
    {
        public string? Name { get; set; }
        public double? LimitMw { get; set; }
        public List<BoundaryBranchFile>? Branches { get; set; }
    }

    internal sealed class BoundaryBranchFile
    {
        public string? Branch { get; set; }
        public int? Direction { get; set; }
    }

    // Phase 0.1 (source-generated JSON, so the core can run under NativeAOT): exactly the options this file has always
    // used, now bound to the source-generated PypsaGbReducedJsonContext (below) so no runtime reflection is needed.
    // Internal, not private, so JsonSourceGenEquivalenceTests reads and writes through this very instance.
    internal static readonly PypsaGbReducedJsonContext StrictJson = new(new JsonSerializerOptions
    {
        PropertyNameCaseInsensitive = true,
        // An unknown key is an error, the same rule as every GridSim case file (R268).
        UnmappedMemberHandling = System.Text.Json.Serialization.JsonUnmappedMemberHandling.Disallow,
    });

    /// <summary>
    /// Read a GridSim-format boundary file ([{name, limitMw, branches: [{branch, direction}]}]) and check every branch
    /// it names is one of <paramref name="known"/>, every direction is +1 or -1 and every limit is finite and not
    /// negative (0 = no limit authored). An absent file gives no boundaries. Shared with the zonal importer, whose
    /// "branches" are link names.
    /// </summary>
    internal static IReadOnlyList<Boundary> ReadBoundaries(string path, IReadOnlySet<string> known)
    {
        if (!File.Exists(path)) return Array.Empty<Boundary>();
        string file = Path.GetFileName(path);
        List<BoundaryFile>? raw;
        try { raw = JsonSerializer.Deserialize(File.ReadAllText(path), StrictJson.ListBoundaryFile); }
        catch (JsonException ex) { throw new InvalidDataException($"{file}: {ex.Message}", ex); }
        if (raw is null) throw new InvalidDataException($"{file}: the file holds null; it must be a list of boundaries.");

        var result = new List<Boundary>(raw.Count);
        var names = new HashSet<string>(StringComparer.Ordinal);
        foreach (BoundaryFile b in raw)
        {
            if (string.IsNullOrEmpty(b.Name)) throw new InvalidDataException($"{file}: a boundary has no name.");
            if (!names.Add(b.Name)) throw new InvalidDataException($"{file}: boundary '{b.Name}' is listed twice.");
            if (b.LimitMw is not double lim || !double.IsFinite(lim) || lim < 0)
                throw new InvalidDataException($"{file}: boundary '{b.Name}' needs a finite, non-negative limitMw (0 = no limit set).");
            if (b.Branches is null || b.Branches.Count == 0) throw new InvalidDataException($"{file}: boundary '{b.Name}' lists no branches.");
            var cut = new List<BoundaryBranch>(b.Branches.Count);
            foreach (BoundaryBranchFile x in b.Branches)
            {
                if (string.IsNullOrEmpty(x.Branch) || !known.Contains(x.Branch))
                    throw new InvalidDataException($"{file}: boundary '{b.Name}' names branch '{x.Branch}', which this network does not have.");
                int dir = x.Direction ?? 0;   // absent counts as invalid: a cut must say which way is positive
                if (dir is not (1 or -1))
                    throw new InvalidDataException($"{file}: boundary '{b.Name}' gives branch '{x.Branch}' direction {(x.Direction is null ? "(none)" : dir.ToString(CultureInfo.InvariantCulture))}; it must be 1 or -1.");
                cut.Add(new BoundaryBranch(x.Branch, dir));
            }
            result.Add(new Boundary(b.Name, lim, cut));
        }
        return result;
    }
}

/// <summary>
/// Phase 0.1: the source-generated JSON metadata for a GridSim-format boundary file and every type it reaches, so it is read and
/// written without runtime reflection (NativeAOT). Metadata mode only: the bytes come from the same converters the
/// reflection path used, never from a separately generated fast path.
/// The runtime options (naming, key case, null and number handling, indentation) are passed in by the call site.
/// </summary>
[JsonSourceGenerationOptions(GenerationMode = JsonSourceGenerationMode.Metadata)]
[JsonSerializable(typeof(List<PypsaGbReduced.BoundaryFile>))]
internal sealed partial class PypsaGbReducedJsonContext : JsonSerializerContext;
