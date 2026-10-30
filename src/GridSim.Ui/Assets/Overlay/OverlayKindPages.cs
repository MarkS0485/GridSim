using System.Globalization;
using System.Text.Json;
using GridSim.Core.IO.Json.Lossless;
using GridSim.Core.IO.Json.Overlay;
using GridSim.Core.Model;
using GridSim.Core.Simulation;
using GridSim.Ui.Assets.Editing;
using GridSim.Ui.Assets.Keys;
using GridSim.Ui.Assets.Overviews;

namespace GridSim.Ui.Assets.Overlay;

// =====================================================================================================================
// UI programme Phase 5e (2026-10-20; plan Phase 5 "Earth wires/earth return: earthWire, soilZone (ohm.m), earthPath").
//
// The kind-specific part of an overlay record's OVERVIEW page: the values of the record laid out as small tables with
// their units, plus the links to the model assets and the overlay records it points at (or is pointed at by), added
// below the generic part OverlayPages.Record builds for every record (kind, file, parent, children, parameters as
// written, reference links). Each table's caption says plainly that the values are CARRIED, NOT USED BY THE SOLVER.
//
// Everything here reads the overlay as carried (CaseAssetOverlay) and the OverlayIndex's resolved references; nothing
// writes anything, and no value of the model depends on it. Numbers are shown exactly as the overlay file writes them
// (their JSON text), never re-formatted, so what the page shows is what the file holds.
//
// Phase 5e covers the earthing kinds:
//   - soilZone:  its resistivity (ohm.m) and upper-layer depth (m); the earth paths that sit in it (the zone's only
//                "extent": the kind declares no geometry and no references of its own);
//   - earthPath: the bus it earths, its resistance to remote earth (ohm), the soil zone it sits in (and that zone's
//                resistivity), the substation it is under;
//   - earthWire: the line it rides (through its parent line route), its count, bonding and conductor (cross-section and
//                DC resistance from the conductor record); the kind has no per-span earth resistance, and the page says so;
//   - substation: an EARTHING summary of the earth paths under it and the soil zones they sit in.
//
// Phase 5f adds the three-winding transformer (transformer3W): the star bus and the three two-winding legs it groups,
// the nameplate (common MVA base, HV / MV / LV rated voltages), the pairwise impedances as authored, and the star legs:
// the authored r and x of each leg (what the solver uses) beside the values the derive-star-legs helper would write
// (StarLegsHelper, display only here: nothing is written in Run mode). Its CHECK notes (display only, never errors) say
// when the legs cannot be derived from the nameplate, or when the authored legs are not the nameplate's derivation.
//
// Phase 5g adds the HVDC link (hvdcLink): the two converter generators it groups (the converters stay authored as
// converter-interfaced generators), its rating, DC voltage and cable length as written, and, in Run mode, the SOLVED
// output of each end read from the frame's model (read-only, labelled solved; the overlay changes nothing), with the sum
// of the two ends as a display-only loss check. The kind has no loss parameter, and the page says so. Its CHECK notes
// (display only, never errors) say when the two ends' MW have the same sign in the model, or an end is above the rating.
//
// Phase 5d adds the line route kinds (lineRoute, span, towerType, conductorPosition, conductorType) in
// OverlayKindPages.Routes.cs: the route's spans in route order with their towers, the total route length beside the
// line's lengthKm, the tower type's conductor positions, the conductor's values with units; and their display-only CHECK
// notes (route against line length, span continuity, a span with no conductor type).
// =====================================================================================================================

/// <summary>The kind-specific part of an overlay record's OVERVIEW: facts, tables and link rows, added after the generic
/// part (Phase 5e onwards).</summary>
/// <param name="Facts">Labelled values in words (added after the generic facts).</param>
/// <param name="Tables">The kind's tables, units in the column titles.</param>
/// <param name="Rows">Extra link rows (each selects the node of what it names).</param>
public sealed record OverlayKindContent(IReadOnlyList<OverviewFact> Facts, IReadOnlyList<OverviewTable> Tables, IReadOnlyList<OverviewRow> Rows)
{
    /// <summary>No kind-specific content (a kind these slices do not lay out).</summary>
    public static OverlayKindContent None { get; } = new(Array.Empty<OverviewFact>(), Array.Empty<OverviewTable>(), Array.Empty<OverviewRow>());
}

/// <summary>
/// The kind-specific OVERVIEW content of overlay records (Phase 5e: earthing and soil). Pure: the same entry and index
/// give the same content.
/// </summary>
public static partial class OverlayKindPages
{
    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    /// <summary>The words every kind table's caption ends with.</summary>
    public const string TableTag = "  (CARRIED, NOT USED BY THE SOLVER)";

    /// <summary>What a table cell shows for a value the record does not write.</summary>
    public const string Absent = "absent";

    // ---- the table captions (public so the tests and views name them once) ----

    /// <summary>The soil zone's own values.</summary>
    public const string SoilTable = "SOIL ZONE" + TableTag;

    /// <summary>The earth paths that sit in a soil zone.</summary>
    public const string SoilPathsTable = "EARTH PATHS IN THIS SOIL ZONE" + TableTag;

    /// <summary>An earth path's values.</summary>
    public const string EarthPathTable = "EARTH PATH" + TableTag;

    /// <summary>An earth wire's values.</summary>
    public const string EarthWireTable = "EARTH WIRE" + TableTag;

    /// <summary>A substation's earthing summary.</summary>
    public const string EarthingTable = "EARTHING" + TableTag;

    /// <summary>
    /// The kind-specific content of <paramref name="e"/> (an entry of <paramref name="ov"/>), or
    /// <see cref="OverlayKindContent.None"/> for a kind with none.
    /// </summary>
    public static OverlayKindContent For(OverlayEntry e, OverlayIndex ov, SimFrame? frame = null)
    {
        ArgumentNullException.ThrowIfNull(e);
        ArgumentNullException.ThrowIfNull(ov);
        return e.Kind switch
        {
            "hvdcLink" => HvdcLink(e, frame),
            "soilZone" => SoilZone(e, ov),
            "earthPath" => EarthPath(e, ov),
            "earthWire" => EarthWire(e, ov),
            "substation" => SubstationEarthing(e, ov),
            "transformer3W" => Transformer3W(e, ov),
            // Phase 5d: the line route, its spans, the tower and conductor catalogue (OverlayKindPages.Routes.cs).
            "lineRoute" => LineRoute(e, ov),
            "span" => Span(e, ov),
            "towerType" => TowerType(e, ov),
            "conductorPosition" => ConductorPosition(e),
            "conductorType" => ConductorType(e, ov),
            _ => OverlayKindContent.None,
        };
    }

    /// <summary>
    /// The kind-specific consistency notes of <paramref name="e"/> (Phase 5f onwards): DISPLAY ONLY, never errors (the
    /// overlay checker's problems are unchanged, and the case still loads and solves exactly as written). Each note is
    /// at the record's own pointer. Empty for a kind with none.
    /// </summary>
    public static IReadOnlyList<OverlayCheckRow> Notes(OverlayEntry e, OverlayIndex ov)
    {
        ArgumentNullException.ThrowIfNull(e);
        ArgumentNullException.ThrowIfNull(ov);
        var notes = new List<string>();
        if (e.Kind == "hvdcLink") notes.AddRange(HvdcNotes(e, ov.Assets.Model));
        if (e.Kind == "transformer3W")
        {
            StarLegsDerivation d = StarLegsHelper.DeriveOver(e, ov.Assets.Model);
            if (!d.Ok) notes.Add("The star legs cannot be derived from the nameplate over the running model: " + d.Refusal);
            else
            {
                var differ = d.Legs.Where(l => !SameBits(l.Authored.R, l.Derived.R) || !SameBits(l.Authored.X, l.Derived.X))
                    .Select(l => l.Role.ToUpperInvariant() + " leg '" + l.Transformer + "' authored r " + N(l.Authored.R) + ", x " + N(l.Authored.X)
                        + "; derived r " + N(l.Derived.R) + ", x " + N(l.Derived.X)).ToList();
                if (differ.Count > 0)
                    notes.Add("The authored legs are not the nameplate's star derivation (the solver uses the authored values; Derive star legs in Author mode proposes the derived ones): "
                        + string.Join("; ", differ) + ".");
            }
        }
        var rows = notes.Select(n => new OverlayCheckRow(e.Record.Pointer, n)).ToList();
        // Phase 5d: a line route's and a span's notes (each at the pointer of the record it is about).
        if (e.Kind == "lineRoute") rows.AddRange(RouteNotes(e, ov));
        if (e.Kind == "span") rows.AddRange(SpanNotes(e, ov));
        return rows;
    }

    private static bool SameBits(double a, double b) => BitConverter.DoubleToInt64Bits(a) == BitConverter.DoubleToInt64Bits(b);

    // A double as the shortest text that reads back to the same bits (the way the case files write numbers).
    private static string N(double v) => JsonLiteral.FormatNumber(v);

    // =================================================================================================================
    // Reading a record's values as written
    // =================================================================================================================

    /// <summary>
    /// The value of parameter <paramref name="name"/> of <paramref name="r"/> as the file writes it: a string without its
    /// quotes, anything else as its JSON text (a number exactly as written: 0.5 stays "0.5", 1e3 stays "1e3"); the
    /// {v, unit, prov} form gives its v. Null when the record does not write the parameter.
    /// </summary>
    public static string? Written(AssetOverlayRecord r, string name)
    {
        ArgumentNullException.ThrowIfNull(r);
        ArgumentNullException.ThrowIfNull(name);
        if (Value(r, name) is not JsonElement v) return null;
        return v.ValueKind == JsonValueKind.String ? v.GetString() : v.GetRawText();
    }

    /// <summary>The value of number parameter <paramref name="name"/> of <paramref name="r"/> (the {v} form's v), or null
    /// when it is absent or not a number.</summary>
    public static double? Number(AssetOverlayRecord r, string name)
    {
        ArgumentNullException.ThrowIfNull(r);
        ArgumentNullException.ThrowIfNull(name);
        return Value(r, name) is JsonElement { ValueKind: JsonValueKind.Number } v ? v.GetDouble() : null;
    }

    // The parameter's value element (the inner v of the {v, unit, prov} form), or null when absent.
    private static JsonElement? Value(AssetOverlayRecord r, string name)
    {
        if (r.Params.ValueKind != JsonValueKind.Object || !r.Params.TryGetProperty(name, out JsonElement v)) return null;
        if (v.ValueKind == JsonValueKind.Object && v.TryGetProperty("v", out JsonElement inner)) return inner;
        return v;
    }

    // The cell text of a parameter: as written, or "absent".
    private static string Cell(AssetOverlayRecord r, string name) => Written(r, name) ?? Absent;

    // The fact text of a parameter: "100 ohm.m" (as written with the unit), or the absent wording given.
    private static string WithUnit(AssetOverlayRecord r, string name, string unit, string absent) =>
        Written(r, name) is string w ? w + " " + unit : absent;

    // A reference link of the entry by role, or null when the record writes no such role.
    private static OverlayRefLink? Ref(OverlayEntry e, string role) => e.Refs.FirstOrDefault(l => string.Equals(l.Role, role, StringComparison.Ordinal));

    // The overlay entry a record reference names (null when absent, or when it names no record of the overlay).
    private static OverlayEntry? RecordRef(OverlayEntry e, string role, OverlayIndex ov) =>
        Ref(e, role) is { IsModelAsset: false, Target: AssetKey k } ? ov.EntryOf(k) : null;

    // The text a model reference shows in a table: what it names in the running case ("bus 1 'Name'"), or the reference as
    // written with why it names nothing.
    private static string RefCell(OverlayRefLink? link) => link is null ? Absent
        : link.Target is null ? link.Written + " (" + link.Resolved + ")" : link.Resolved;

    // The text of a parent cell: "substation 'ov.sub'", or "none".
    private static string ParentCell(OverlayEntry e) => e.Parent is { } p ? p.Kind + " '" + p.Id + "'" : "none";

    private static string Count(int n) => n.ToString(Inv);

    // =================================================================================================================
    // Phase 5e: soil zone, earth path, earth wire, substation earthing
    // =================================================================================================================

    // Every earth path of the overlay whose 'soil' reference names this zone, in overlay order.
    private static List<OverlayEntry> PathsIn(OverlayEntry zone, OverlayIndex ov) =>
        ov.Entries.Where(p => p.Kind == "earthPath" && RecordRef(p, "soil", ov) == zone).ToList();

    // One earth path as a row of a table: id, bus, resistance, then the caller's own columns.
    private static string[] PathCells(OverlayEntry p, params string[] more) =>
        new[] { p.Id, RefCell(Ref(p, "bus")), Cell(p.Record, "resistanceOhm") }.Concat(more).ToArray();

    private static OverlayKindContent SoilZone(OverlayEntry e, OverlayIndex ov)
    {
        AssetOverlayRecord r = e.Record;
        List<OverlayEntry> paths = PathsIn(e, ov);
        var facts = new List<OverviewFact>
        {
            new("Soil resistivity", WithUnit(r, "resistivityOhmM", "ohm.m", "absent (the kind requires it; see CHECK)")),
            new("Upper layer depth", WithUnit(r, "depthM", "m", "absent (uniform soil)")),
            new("Extent", "not carried: the soilZone kind declares no geometry and no references; the earth paths that point at the zone are its extent"),
            new("Earth paths in this zone", Count(paths.Count)),
        };
        var tables = new List<OverviewTable>
        {
            OverviewTable.Of(SoilTable, new[] { "Quantity", "Value", "Unit" }, new[]
            {
                new[] { "Resistivity", Cell(r, "resistivityOhmM"), "ohm.m" },
                new[] { "Upper layer depth", Cell(r, "depthM"), "m" },
            }, "As written in " + r.Document + " " + r.Pointer + "; absent depth = uniform soil."),
            OverviewTable.Of(SoilPathsTable, new[] { "Earth path", "Bus", "Resistance (ohm)", "Under" },
                paths.Select(p => (IReadOnlyList<string>)PathCells(p, ParentCell(p))),
                paths.Count == 0 ? "No earth path of the overlay points at this soil zone." : ""),
        };
        var rows = new List<OverviewRow>();
        foreach (OverlayEntry p in paths)
        {
            rows.Add(new OverviewRow(p.Id, "earth path in this soil zone  " + p.KindTitle, p.Kind, p.Key));
            if (Ref(p, "bus") is { } bus)
                rows.Add(new OverviewRow(bus.Written, "earthed by '" + p.Id + "':  " + bus.Resolved, "model", bus.Target));
        }
        return new OverlayKindContent(facts, tables, rows);
    }

    private static OverlayKindContent EarthPath(OverlayEntry e, OverlayIndex ov)
    {
        AssetOverlayRecord r = e.Record;
        OverlayEntry? soil = RecordRef(e, "soil", ov);
        OverlayRefLink? soilLink = Ref(e, "soil");
        string soilCell = soilLink is null ? "none given" : soil is null ? soilLink.Written + " (" + soilLink.Resolved + ")" : soil.Id;
        string rhoCell = soil is null ? Absent : Cell(soil.Record, "resistivityOhmM");
        var facts = new List<OverviewFact>
        {
            new("Earths bus", RefCell(Ref(e, "bus"))),
            new("Earth resistance", WithUnit(r, "resistanceOhm", "ohm", "absent (the kind requires it; see CHECK)")),
            new("Soil zone", soil is null ? soilCell : soil.Id + "  (" + WithUnit(soil.Record, "resistivityOhmM", "ohm.m", "resistivity absent") + ")"),
            new("Neutral earthing", "the authored transformers' neutralXPu stay the earthing the fault studies use; this record does not change them"),
        };
        var tables = new List<OverviewTable>
        {
            OverviewTable.Of(EarthPathTable, new[] { "Bus", "Resistance (ohm)", "Soil zone", "Soil resistivity (ohm.m)", "Under" }, new[]
            {
                new[] { RefCell(Ref(e, "bus")), Cell(r, "resistanceOhm"), soilCell, rhoCell, ParentCell(e) },
            }, "As written in " + r.Document + " " + r.Pointer + "."),
        };
        return new OverlayKindContent(facts, tables, Array.Empty<OverviewRow>());
    }

    private static OverlayKindContent EarthWire(OverlayEntry e, OverlayIndex ov)
    {
        AssetOverlayRecord r = e.Record;
        // The line the earth wire rides is its parent line route's 'line' reference (the earthWire kind has none itself).
        OverlayEntry? route = e.Parent is { Kind: "lineRoute" } p ? p : null;
        OverlayRefLink? line = route is null ? null : Ref(route, "line");
        string lineCell = route is null ? "unknown: the earth wire is not under a line route" : RefCell(line);
        OverlayEntry? conductor = RecordRef(e, "conductor", ov);
        OverlayRefLink? conductorLink = Ref(e, "conductor");
        string conductorCell = conductorLink is null ? "none given" : conductor is null ? conductorLink.Written + " (" + conductorLink.Resolved + ")"
            : conductor.Id + (Written(conductor.Record, "code") is string code ? " (" + code + ")" : "");
        var facts = new List<OverviewFact>
        {
            new("Rides line", route is null ? lineCell : lineCell + "  (through line route '" + route.Id + "')"),
            new("Earth wires", Cell(r, "count")),
            new("Bonded at both ends", Written(r, "bondedAtBothEnds") ?? "absent (not stated)"),
            new("Conductor", conductorCell),
            new("Earth resistance per span", "not carried: the earthWire kind has no per-span earth resistance parameter"),
            new("Zero sequence", "the line's authored r0Pu and x0Pu stay what the fault studies use; this record does not change them"),
        };
        var tables = new List<OverviewTable>
        {
            OverviewTable.Of(EarthWireTable,
                new[] { "Line", "Route", "Earth wires (count)", "Bonded at both ends", "Conductor", "Cross-section (mm2)", "DC resistance (ohm/km)" }, new[]
                {
                    new[]
                    {
                        lineCell, route?.Id ?? "none", Cell(r, "count"), Cell(r, "bondedAtBothEnds"), conductorCell,
                        conductor is null ? Absent : Cell(conductor.Record, "crossSectionMm2"),
                        conductor is null ? Absent : Cell(conductor.Record, "dcResistanceOhmPerKm"),
                    },
                }, "As written in " + r.Document + " " + r.Pointer + "; the conductor columns are the conductor record's own values."),
        };
        var rows = new List<OverviewRow>();
        if (line is { } l)
            rows.Add(new OverviewRow(l.Written, "rides this line, through line route '" + route!.Id + "':  " + l.Resolved, "model", l.Target));
        return new OverlayKindContent(facts, tables, rows);
    }

    // =================================================================================================================
    // Phase 5f: the three-winding transformer
    // =================================================================================================================

    /// <summary>The nameplate table of a transformer3W record.</summary>
    public const string NameplateTable = "THREE-WINDING NAMEPLATE" + TableTag;

    /// <summary>The pairwise impedances of a transformer3W record, as authored.</summary>
    public const string PairwiseTable = "PAIRWISE IMPEDANCES AS AUTHORED (PER UNIT ON baseMva)" + TableTag;

    /// <summary>The star legs: authored (the solver's) beside derived (display only).</summary>
    public const string StarLegsTable = "STAR LEGS  (AUTHORED: WHAT THE SOLVER USES; DERIVED: CARRIED, NOT USED BY THE SOLVER)";

    private static OverlayKindContent Transformer3W(OverlayEntry e, OverlayIndex ov)
    {
        AssetOverlayRecord r = e.Record;
        GridModel m = ov.Assets.Model;
        StarLegsDerivation d = StarLegsHelper.DeriveOver(e, m);
        string Kv(string p) => WithUnit(r, p, "kV", "absent");
        var facts = new List<OverviewFact>
        {
            new("Star bus", RefCell(Ref(e, "starBus"))),
            new("Common MVA base", Written(r, "baseMva") is string b ? b + " MVA (the pairwise impedances are on it, as the kind spec states)" : "absent (the kind requires it; see CHECK)"),
            new("Rated voltages", "HV " + Kv("hvKv") + ", MV " + Kv("mvKv") + ", LV " + Kv("lvKv")),
            new("Leg values", "the solver uses the legs' authored r and x; Derive star legs (Author mode) proposes the derived values below and writes them only on confirm"),
            new("Derived star legs", d.Ok ? "derived from the nameplate on the case base " + N(d.SystemMva) + " MVA (display only)" : d.Refusal!),
        };
        var tables = new List<OverviewTable>
        {
            OverviewTable.Of(NameplateTable, new[] { "Winding", "Rated voltage (kV)", "Leg transformer", "Leg resolves to" }, new[]
            {
                new[] { "HV", Cell(r, "hvKv"), Ref(e, "hv")?.Written ?? Absent, RefCell(Ref(e, "hv")) },
                new[] { "MV", Cell(r, "mvKv"), Ref(e, "mv")?.Written ?? Absent, RefCell(Ref(e, "mv")) },
                new[] { "LV", Cell(r, "lvKv"), Ref(e, "lv")?.Written ?? Absent, RefCell(Ref(e, "lv")) },
            }, "Common MVA base baseMva = " + (Written(r, "baseMva") ?? Absent) + " MVA. As written in " + r.Document + " " + r.Pointer + "."),
            OverviewTable.Of(PairwiseTable, new[] { "Pair", "r (pu on baseMva)", "x (pu on baseMva)" }, new[]
            {
                new[] { "HV-MV (12)", Cell(r, "r12Pu"), Cell(r, "x12Pu") },
                new[] { "HV-LV (13)", Cell(r, "r13Pu"), Cell(r, "x13Pu") },
                new[] { "MV-LV (23)", Cell(r, "r23Pu"), Cell(r, "x23Pu") },
            }),
        };
        // The legs as the running model holds them (what the solver uses), beside the derived values when there are any.
        var legRows = new List<IReadOnlyList<string>>();
        foreach ((string role, _) in StarLegsHelper.Roles)
        {
            string? name = r.Refs.FirstOrDefault(x => x.Role == role && x.Target == AssetRefTarget.Transformer)?.Key;
            List<Branch> hits = name is null ? new List<Branch>() : m.Branches.Where(x => x.IsTransformer && string.Equals(x.Name, name, StringComparison.Ordinal)).ToList();
            Branch? t = hits.Count == 1 ? hits[0] : null;
            StarLegResult? leg = d.Ok ? d.Legs.Single(l => l.Role == role) : null;
            legRows.Add(new[]
            {
                role.ToUpperInvariant(), name ?? Absent,
                t is null ? Absent : t.From.ToString(Inv) + " -> " + t.To.ToString(Inv),
                t is null ? Absent : N(t.R), t is null ? Absent : N(t.X),
                leg is null ? "not derived" : N(leg.Derived.R), leg is null ? "not derived" : N(leg.Derived.X),
            });
        }
        tables.Add(OverviewTable.Of(StarLegsTable,
            new[] { "Leg", "Transformer", "Buses (from -> to)", "Authored r (pu, case base)", "Authored x (pu, case base)", "Derived r (pu, case base)", "Derived x (pu, case base)" },
            legRows, d.Ok ? string.Join(" ", d.Lines) : d.Refusal!));
        return new OverlayKindContent(facts, tables, Array.Empty<OverviewRow>());
    }

    // =================================================================================================================
    // Phase 5g: the HVDC link
    // =================================================================================================================

    /// <summary>The HVDC link's own values.</summary>
    public const string HvdcTable = "HVDC LINK" + TableTag;

    /// <summary>The two converter ends with their solved output (the model generators' values from the frame).</summary>
    public const string HvdcEndsTable = "CONVERTER ENDS  (SOLVED OUTPUT OF THE AUTHORED GENERATORS, READ-ONLY)";

    /// <summary>The display-only sum / loss check over the two ends.</summary>
    public const string HvdcCheckTable = "SUM AND LOSS CHECK  (DISPLAY ONLY, FROM THE SOLVED ENDS)";

    /// <summary>The roles of an HVDC link's two ends, in order.</summary>
    public static IReadOnlyList<string> HvdcRoles { get; } = new[] { "sendingConverter", "receivingConverter" };

    /// <summary>
    /// One end of an HVDC link in one frame: the role, the generator it names, the bus it sits on in the frame's model
    /// and its SOLVED output (the frame model's Pg of that generator, exactly), or why there is none.
    /// </summary>
    /// <param name="Role">"sendingConverter" or "receivingConverter".</param>
    /// <param name="Generator">The generator name as the overlay writes it, or null when the role is absent.</param>
    /// <param name="Bus">The generator's bus in the frame's model, or null.</param>
    /// <param name="SolvedMw">The frame model's Pg of the generator, MW (bit for bit), or null when there is none.</param>
    /// <param name="State">"solved", or why there is no solved value.</param>
    public sealed record HvdcEnd(string Role, string? Generator, int? Bus, double? SolvedMw, string State);

    /// <summary>
    /// The two ends of HVDC link <paramref name="e"/> in <paramref name="frame"/> (sending, then receiving): each end's
    /// generator found in the frame's model by the rule the overlay uses (a name exactly one in-service generator
    /// carries), its solved output read from that model (read-only, nothing is fed back). Before any frame every end
    /// says so.
    /// </summary>
    public static IReadOnlyList<HvdcEnd> HvdcEnds(OverlayEntry e, SimFrame? frame)
    {
        ArgumentNullException.ThrowIfNull(e);
        var ends = new List<HvdcEnd>(HvdcRoles.Count);
        foreach (string role in HvdcRoles)
        {
            string? name = e.Record.Refs.FirstOrDefault(x => x.Role == role && x.Target == AssetRefTarget.Generator)?.Key;
            if (name is null) { ends.Add(new HvdcEnd(role, null, null, null, role == "receivingConverter" ? "absent (the far end is outside the case)" : "absent")); continue; }
            if (frame is null) { ends.Add(new HvdcEnd(role, name, null, null, "no solved frame yet")); continue; }
            List<Generator> hits = frame.Model.Generators.Where(g => string.Equals(g.Name, name, StringComparison.Ordinal)).ToList();
            if (hits.Count == 1) { ends.Add(new HvdcEnd(role, name, hits[0].Bus, hits[0].Pg, "solved")); continue; }
            bool off = hits.Count == 0 && frame.Model.AllGenerators.Any(g => !g.InService && string.Equals(g.Name, name, StringComparison.Ordinal));
            ends.Add(new HvdcEnd(role, name, null, null, off ? "out of service in this frame (no solved output)"
                : hits.Count == 0 ? "not a generator of the solved model" : "ambiguous: " + hits.Count.ToString(Inv) + " generators of the solved model carry this name"));
        }
        return ends;
    }

    // A solved MW figure as the page shows it: one decimal place, invariant (the exact double is HvdcEnds' SolvedMw).
    private static string Mw(double mw) => mw.ToString("F1", Inv);

    private static OverlayKindContent HvdcLink(OverlayEntry e, SimFrame? frame)
    {
        AssetOverlayRecord r = e.Record;
        IReadOnlyList<HvdcEnd> ends = HvdcEnds(e, frame);
        var facts = new List<OverviewFact>
        {
            new("Sending converter", RefCell(Ref(e, "sendingConverter"))),
            new("Receiving converter", Ref(e, "receivingConverter") is { } rc ? RefCell(rc) : "absent (the far end is outside the case)"),
            new("Technology", Written(r, "technology") ?? "absent (not stated)"),
            new("Rated transfer", WithUnit(r, "ratedMw", "MW", "absent")),
            new("DC voltage", WithUnit(r, "dcVoltageKv", "kV (pole to earth)", "absent")),
            new("Cable length", WithUnit(r, "cableLengthKm", "km", "absent")),
            new("Losses", "not carried: the hvdcLink kind has no loss parameter; the sum of the two ends' solved outputs below is a display-only check"),
            new("Converters", "the authored converter generators stay what the solver uses; this record groups them and changes nothing"),
        };
        var tables = new List<OverviewTable>
        {
            OverviewTable.Of(HvdcTable, new[] { "Technology", "Rated transfer (MW)", "DC voltage (kV)", "Cable length (km)" }, new[]
            {
                new[] { Cell(r, "technology"), Cell(r, "ratedMw"), Cell(r, "dcVoltageKv"), Cell(r, "cableLengthKm") },
            }, "As written in " + r.Document + " " + r.Pointer + "."),
            OverviewTable.Of(HvdcEndsTable, new[] { "End", "Generator", "Bus", "Solved output (MW)", "State" },
                ends.Select(x => (IReadOnlyList<string>)new[]
                {
                    x.Role == "sendingConverter" ? "sending" : "receiving", x.Generator ?? Absent, x.Bus?.ToString(Inv) ?? "-",
                    x.SolvedMw is double mw ? Mw(mw) : "-", x.State,
                }),
                frame is null ? "No solved frame yet: the solved outputs appear once the case runs."
                    : "Solved: each end's output is its generator's Pg in the frame's model (positive = injecting into the AC network), read-only."),
        };
        // The sum / loss check: only when both ends have a solved value.
        if (ends.All(x => x.SolvedMw is not null))
        {
            double send = ends[0].SolvedMw!.Value, recv = ends[1].SolvedMw!.Value;
            double sum = send + recv;
            bool opposite = (send < 0 && recv > 0) || (send > 0 && recv < 0);
            tables.Add(OverviewTable.Of(HvdcCheckTable, new[] { "Quantity", "Value (MW)" }, new[]
            {
                new[] { "Sending end, solved", Mw(send) },
                new[] { "Receiving end, solved", Mw(recv) },
                new[] { "Sum of the two ends", Mw(sum) },
                new[] { "Implied DC loss (minus the sum)", opposite ? Mw(-sum) : "not meaningful: the ends do not have opposite signs" },
            }, "Display only: a link moving power has one end importing and the other exporting, and the sum of the two is minus the DC losses. Nothing here is fed back to the solver."));
        }
        else
        {
            tables.Add(OverviewTable.Of(HvdcCheckTable, new[] { "Quantity", "Value (MW)" }, Array.Empty<IReadOnlyList<string>>(),
                "No check: " + string.Join("; ", ends.Where(x => x.SolvedMw is null).Select(x => (x.Role == "sendingConverter" ? "the sending end" : "the receiving end") + " is " + x.State)) + "."));
        }
        return new OverlayKindContent(facts, tables, Array.Empty<OverviewRow>());
    }

    // The display-only consistency notes of an HVDC link over the model's generators (their Pg as the model holds them).
    private static IEnumerable<string> HvdcNotes(OverlayEntry e, GridModel m)
    {
        string? send = e.Record.Refs.FirstOrDefault(x => x.Role == "sendingConverter" && x.Target == AssetRefTarget.Generator)?.Key;
        string? recv = e.Record.Refs.FirstOrDefault(x => x.Role == "receivingConverter" && x.Target == AssetRefTarget.Generator)?.Key;
        if (send is not null && recv is not null && string.Equals(send, recv, StringComparison.Ordinal))
            yield return "Both ends of the link name the same generator '" + send + "'.";
        Generator? One(string? name)
        {
            if (name is null) return null;
            List<Generator> hits = m.Generators.Where(g => string.Equals(g.Name, name, StringComparison.Ordinal)).ToList();
            return hits.Count == 1 ? hits[0] : null;
        }
        Generator? gs = One(send), gr = One(recv);
        if (gs is not null && gr is not null && !ReferenceEquals(gs, gr) && ((gs.Pg > 0 && gr.Pg > 0) || (gs.Pg < 0 && gr.Pg < 0)))
            yield return "Both ends have the same MW sign in the model (sending '" + send + "' " + Mw(gs.Pg) + " MW, receiving '" + recv + "' " + Mw(gr.Pg)
                + " MW): a link moving power has one end importing and the other exporting. Display only: the generators are solved exactly as authored.";
        if (Number(e.Record, "ratedMw") is double rated && double.IsFinite(rated))
            foreach ((string? name, Generator? g) in new[] { (send, gs), (recv, gr) })
                if (g is not null && Math.Abs(g.Pg) > rated)
                    yield return "The end '" + name + "' is at " + Mw(g.Pg) + " MW in the model, above the link's rated transfer " + Written(e.Record, "ratedMw") + " MW. Display only.";
    }

    private static OverlayKindContent SubstationEarthing(OverlayEntry e, OverlayIndex ov)
    {
        // The substation's earth paths are its child earthPath records (the kind's parentKinds put them there).
        List<OverlayEntry> paths = e.Children.Where(c => c.Kind == "earthPath").ToList();
        var zones = new List<OverlayEntry>();
        foreach (OverlayEntry p in paths)
            if (RecordRef(p, "soil", ov) is { } z && !zones.Contains(z)) zones.Add(z);
        string summary = paths.Count == 0 ? "no earth path under this substation"
            : Count(paths.Count) + (paths.Count == 1 ? " earth path" : " earth paths") + ", "
              + Count(zones.Count) + (zones.Count == 1 ? " soil zone" : " soil zones");
        var facts = new List<OverviewFact> { new("Earthing", summary) };
        var tables = new List<OverviewTable>
        {
            OverviewTable.Of(EarthingTable, new[] { "Earth path", "Bus", "Resistance (ohm)", "Soil zone", "Soil resistivity (ohm.m)" },
                paths.Select(p =>
                {
                    OverlayEntry? z = RecordRef(p, "soil", ov);
                    return (IReadOnlyList<string>)PathCells(p, z?.Id ?? "none given", z is null ? Absent : Cell(z.Record, "resistivityOhmM"));
                }),
                paths.Count == 0 ? "No earth path record sits under this substation." : "The earth path records under this substation, in overlay order."),
        };
        var rows = new List<OverviewRow>();
        foreach (OverlayEntry z in zones)
            rows.Add(new OverviewRow(z.Id, "earthing: soil zone of " + string.Join(", ", paths.Where(p => RecordRef(p, "soil", ov) == z).Select(p => "'" + p.Id + "'")),
                z.Kind, z.Key));
        return new OverlayKindContent(facts, tables, rows);
    }
}
