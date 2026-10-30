using System.Globalization;
using GridSim.Core.IO.Json.CaseEditing;
using GridSim.Core.IO.Json.Lossless;
using GridSim.Core.IO.Json.Schema;
using GridSim.Core.Layout;
using GridSim.Core.Model;
using GridSim.Ui.Assets.Keys;

namespace GridSim.Ui.Assets.Editing;

// UI programme Phase 4.2 (plan: "Map editing: place a bus (writes x/y), connect two buses to make a line/transformer,
// drag, delete. A refused delete shows its referrers."; ASSETS plan 4.1 slice 4.2: "map editing on the NETWORK map,
// selection synced with the ASSETS tree"; Q6: no second map inside ASSETS). This file is the UI-neutral half of map
// editing in AUTHOR mode:
//
//   1. AuthorMap: the map of the case AS IT IS BEING EDITED, read from the authoring session's documents (never from a
//      live frame, so it can show no live value: plan Q5). Its buses and branches are the case's own records, each
//      remembered with its record reference, so a click on the map names a record and a record names a map element.
//   2. MapEditing: the four map edits, each ONE engine transaction and so ONE undo step:
//        - PlaceBus:  an Add record of a bus (the engine's CaseOps.AddRecord) with the clicked point as its x and y;
//        - Connect:   an Add record of a line or a transformer whose "from" and "to" are the two clicked buses;
//        - MoveBus:   the bus's x and y set together (SetBusCoordinates below, one transaction for both members);
//        - delete is RecordActions.Delete itself (refused, listing the referrers, while anything refers to the record).
//      Every integrity rule is the engine's: a key that collides, a reference that does not resolve, a value the
//      schema refuses, a delete with referrers are all refused by CaseOps with its own words, and nothing changes.
//
// THE COORDINATE CONVENTION. A case's bus "x" and "y" are the map's layout coordinates (v1 buses.schema.json: "Layout
// coordinate (0..1) for the visualiser. Absent: 0."; v2 network.schema.json: "Layout coordinate for the visualiser.").
// Both loaders copy them unchanged into Bus.X and Bus.Y, and the map (SchematicView.Load) draws a bus at the WORLD point
// (Wx, Wy) = (X, 1 - Y): north up, world y growing downward like the screen. So a point clicked on the map, turned into
// world coordinates by the map's own inverse projection (SchematicView.ScreenToWorld), is written back as
//   x = Wx,   y = 1 - Wy,
// rounded to CoordinateDecimals (6) decimal places (a millionth of the 0..1 layout box: about a metre on the GB map,
// finer than a pixel at the map's deepest zoom) and written in the engine's shortest round-trip number form. A
// coordinate is never clamped to 0..1: the box is the map's usual extent, not a rule the schema or the loader makes.
//
// A CASE WITH NO LAYOUT. A case whose buses all sit at x = y = 0 (absent or written as 0: a converted MATPOWER or IEEE
// case) has no layout of its own; the run pipeline draws it with GraphLayout.EnsureLayout, a deterministic automatic
// layout, and so does this map. That layout is only used while EVERY bus is at the origin (GraphLayout.HasNoLayout): the
// first bus given a real coordinate would switch it off and every other bus would collapse onto the origin. So the
// first placed or moved bus of such a case writes, in the SAME undo step, the automatic layout's position of every other
// bus too (rounded the same way), and the status line says so: what the map showed before the edit is exactly what the
// saved case then shows.
//
// The coordinate operation itself is the engine's: CaseOps.SetBusCoordinates (moved into Core on 2026-10-24; it was
// first written here on the engine's internals). It is the engine's multi-record pattern (CaseOps.SetOnUnits, a station
// rename): typed-value parsing, lossless patches at the schema-order position, the "no new schema error in any touched
// record" check and one CaseTransaction (one undo step). This file only turns map points into the rounded numbers
// (SetBusCoordinates below formats them and hands them over); it only ever touches the "x" and "y" members of bus records.

/// <summary>The tool the map uses for a left click in AUTHOR mode (UI programme Phase 4.2).</summary>
public enum MapEditTool
{
    /// <summary>Select: a click selects a bus or a branch (and its record in the ASSETS tree); a bus can be dragged.</summary>
    Select,

    /// <summary>Place bus: a click on the map adds a bus record at that point (its required fields are asked for).</summary>
    PlaceBus,

    /// <summary>Connect: a click on bus A, then on bus B, adds a line or a transformer from A to B.</summary>
    Connect,
}

/// <summary>What Connect adds between two buses.</summary>
public enum MapBranchKind
{
    /// <summary>A line (v1 transmission.json, v2 "lines").</summary>
    Line,

    /// <summary>A two-winding transformer (v1 transformer.json, v2 "transformers").</summary>
    Transformer,
}

/// <summary>One bus of the Author map: its record, its id, name and base kV as written, and where the map draws it.</summary>
/// <param name="Record">The bus record.</param>
/// <param name="Id">The bus id as written.</param>
/// <param name="Name">The name as written (null when absent).</param>
/// <param name="BaseKv">The base kV as written (0 when absent or not a number).</param>
/// <param name="X">The layout x the map draws it at (as written; the automatic layout's for a case with no layout).</param>
/// <param name="Y">The layout y the map draws it at (as written; the automatic layout's for a case with no layout).</param>
public sealed record AuthorMapBus(CaseRef Record, int Id, string? Name, double BaseKv, double X, double Y);

/// <summary>One branch of the Author map: its record, its ends and name as written, and whether it is a transformer.</summary>
/// <param name="Record">The line or transformer record.</param>
/// <param name="From">The "from" bus id as written.</param>
/// <param name="To">The "to" bus id as written.</param>
/// <param name="Name">The name as written (null when absent).</param>
/// <param name="IsTransformer">True for a transformer record, or a v1 line marked "isTransformer": true.</param>
public sealed record AuthorMapBranch(CaseRef Record, int From, int To, string? Name, bool IsTransformer);

/// <summary>
/// The map of the case being edited (UI programme Phase 4.2): a model built from the authoring session's bus, line and
/// transformer records (no frame, no solved value), in the session's collection order, with each map element's record.
/// <see cref="Model"/> is what the map control loads; its bus i is <see cref="Buses"/>[i] and its branch i is
/// <see cref="Branches"/>[i], so the map's branch index is an index into <see cref="Branches"/>.
/// </summary>
public sealed class AuthorMap
{
    private readonly Dictionary<int, int> _busById;
    private readonly Dictionary<CaseRef, int> _busByRecord;
    private readonly Dictionary<CaseRef, int> _branchByRecord;

    internal AuthorMap(GridModel model, IReadOnlyList<AuthorMapBus> buses, IReadOnlyList<AuthorMapBranch> branches, bool autoLayout, IReadOnlyList<string> notDrawn)
    {
        Model = model;
        Buses = buses;
        Branches = branches;
        AutoLayout = autoLayout;
        NotDrawn = notDrawn;
        // A duplicated bus id (a case Check reports) is drawn twice; the map's own id index keeps the LAST bus of an id
        // (SchematicView.Load: IdToIndex[b.Id] = ...), so this index keeps the last too: a click names the bus drawn on top.
        _busById = new Dictionary<int, int>();
        for (int i = 0; i < buses.Count; i++) _busById[buses[i].Id] = i;
        _busByRecord = new Dictionary<CaseRef, int>();
        for (int i = 0; i < buses.Count; i++) _busByRecord[buses[i].Record] = i;
        _branchByRecord = new Dictionary<CaseRef, int>();
        for (int i = 0; i < branches.Count; i++) _branchByRecord[branches[i].Record] = i;
    }

    /// <summary>The model the map draws: the buses (id, name, base kV, x, y) and branches (ends, name, transformer or not)
    /// of the case as it is now, and nothing else (no generation, no demand, no solved state).</summary>
    public GridModel Model { get; }

    /// <summary>The buses, aligned with <see cref="Model"/>'s buses.</summary>
    public IReadOnlyList<AuthorMapBus> Buses { get; }

    /// <summary>The branches (lines, then transformers, per collection in the session's order), aligned with
    /// <see cref="Model"/>'s branches.</summary>
    public IReadOnlyList<AuthorMapBranch> Branches { get; }

    /// <summary>True when the case has no layout of its own (every bus at x = y = 0) and the map shows the automatic
    /// layout (GraphLayout.EnsureLayout), as the run pipeline does.</summary>
    public bool AutoLayout { get; }

    /// <summary>The records the map cannot draw (a bus with no integer id, a branch whose "from" or "to" is not an
    /// integer), each named; the case's Check reports them.</summary>
    public IReadOnlyList<string> NotDrawn { get; }

    /// <summary>The bus record of id <paramref name="busId"/> (the last one, when the id is duplicated), or null.</summary>
    public AuthorMapBus? Bus(int busId) => _busById.TryGetValue(busId, out int i) ? Buses[i] : null;

    /// <summary>The bus of <paramref name="record"/>, or null when it is not a bus record drawn on the map.</summary>
    public AuthorMapBus? BusOf(CaseRef record)
    {
        ArgumentNullException.ThrowIfNull(record);
        return _busByRecord.TryGetValue(record, out int i) ? Buses[i] : null;
    }

    /// <summary>The map branch index of <paramref name="record"/>, or null when it is not a branch drawn on the map.</summary>
    public int? BranchIndexOf(CaseRef record)
    {
        ArgumentNullException.ThrowIfNull(record);
        return _branchByRecord.TryGetValue(record, out int i) ? i : null;
    }
}

/// <summary>
/// The map edits of AUTHOR mode (UI programme Phase 4.2), each one engine transaction and one undo step, and the
/// Author map they act on. See the file header for the coordinate convention and the rule for a case with no layout.
/// </summary>
public static class MapEditing
{
    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    /// <summary>The decimal places a written layout coordinate is rounded to (a millionth of the 0..1 layout box).</summary>
    public const int CoordinateDecimals = 6;

    /// <summary>The bus layout members this file writes (and the only members SetBusCoordinates touches).</summary>
    public const string XField = CaseOps.BusXField;

    /// <inheritdoc cref="XField"/>
    public const string YField = CaseOps.BusYField;

    // ===================================================================================================================
    // The coordinate convention
    // ===================================================================================================================

    /// <summary>
    /// The case coordinates (x, y) of the map WORLD point (<paramref name="worldX"/>, <paramref name="worldY"/>):
    /// x = worldX, y = 1 - worldY (the inverse of SchematicView.Load's (X, 1 - Y)), each rounded to
    /// <see cref="CoordinateDecimals"/> places (a negative zero written as 0). Refused (throws) for a non-finite point.
    /// </summary>
    public static (double X, double Y) CaseCoordinates(double worldX, double worldY)
    {
        if (!double.IsFinite(worldX) || !double.IsFinite(worldY))
            throw new ArgumentOutOfRangeException(nameof(worldX), $"a map point must be finite (got {worldX.ToString("R", Inv)}, {worldY.ToString("R", Inv)})");
        return (Round(worldX), Round(1.0 - worldY));
    }

    /// <summary>A layout coordinate as it is written: rounded to <see cref="CoordinateDecimals"/> places, shortest
    /// round-trip, invariant culture ("0.4", "-0.125", "0").</summary>
    public static string Format(double coordinate)
    {
        if (!double.IsFinite(coordinate)) throw new ArgumentOutOfRangeException(nameof(coordinate), "a layout coordinate must be finite");
        return Round(coordinate).ToString("R", Inv);
    }

    // Round half to even at CoordinateDecimals places; a negative zero becomes 0 (so "-0" is never written).
    private static double Round(double v)
    {
        double r = Math.Round(v, CoordinateDecimals, MidpointRounding.ToEven);
        return r == 0 ? 0.0 : r;
    }

    // ===================================================================================================================
    // The Author map
    // ===================================================================================================================

    /// <summary>
    /// The map of the case as it is now in <paramref name="authoring"/>: every bus record with an integer id (its
    /// name, base kV, x and y as written), every line and transformer record whose "from" and "to" are integers, in the
    /// session's collection order. When every bus sits at the origin the automatic layout is applied (as the run
    /// pipeline applies it) and <see cref="AuthorMap.AutoLayout"/> is true.
    /// </summary>
    public static AuthorMap Build(CaseAuthoring authoring)
    {
        ArgumentNullException.ThrowIfNull(authoring);
        CaseSession s = authoring.Session;
        var buses = new List<AuthorMapBus>();
        var branches = new List<AuthorMapBranch>();
        var notDrawn = new List<string>();

        foreach (RecordCollection c in s.CollectionsOf(CaseRoles.Buses))
            foreach ((CaseRef at, JsonSourceNode rec, CaseDocument d) in RecordsOf(s, c))
            {
                if (RecordRead.Int(rec, "id", d.KeyComparer) is not int id)
                {
                    notDrawn.Add(RecordRead.Describe(d, rec, at.Index, d.Spec is null ? at.ArrayPointer : "") + ": no integer id");
                    continue;
                }
                buses.Add(new AuthorMapBus(at, id, RecordRead.Str(rec, "name", d.KeyComparer),
                    RecordRead.Num(rec, "baseKv", d.KeyComparer) ?? 0,
                    RecordRead.Num(rec, XField, d.KeyComparer) ?? 0,
                    RecordRead.Num(rec, YField, d.KeyComparer) ?? 0));
            }

        // Lines then transformers, as the v1 loader orders the branches (transmission.json, then transformer.json).
        foreach (string role in new[] { CaseRoles.Lines, CaseRoles.Transformers })
            foreach (RecordCollection c in s.CollectionsOf(role))
                foreach ((CaseRef at, JsonSourceNode rec, CaseDocument d) in RecordsOf(s, c))
                {
                    int? from = RecordRead.Int(rec, "from", d.KeyComparer), to = RecordRead.Int(rec, "to", d.KeyComparer);
                    if (from is null || to is null)
                    {
                        notDrawn.Add(RecordRead.Describe(d, rec, at.Index, d.Spec is null ? at.ArrayPointer : "") + ": \"from\" or \"to\" is not an integer bus id");
                        continue;
                    }
                    bool tx = role == CaseRoles.Transformers
                        || RecordRead.Member(rec, "isTransformer", d.KeyComparer) is { Kind: JsonSourceKind.True };
                    branches.Add(new AuthorMapBranch(at, from.Value, to.Value, RecordRead.Str(rec, "name", d.KeyComparer), tx));
                }

        // The model the map draws. Only what the map reads is filled in: the bus id, type (a slack bus seeds the display
        // tiers of a flat case, SchematicView.AssignDisplayTiers), name, base kV and layout; the branch ends, name,
        // transformer flag and service state (the automatic layout uses in-service branches only). R and X are not
        // drawn and are left 0: this model is never solved.
        Dictionary<int, BusType> types = BusTypes(s, buses);
        var modelBuses = new List<Bus>(buses.Count);
        foreach (AuthorMapBus b in buses)
            modelBuses.Add(new Bus(b.Id, types[b.Id], BaseKv: b.BaseKv, Name: b.Name, X: b.X, Y: b.Y));
        var modelBranches = new List<Branch>(branches.Count);
        foreach (AuthorMapBranch br in branches)
            modelBranches.Add(new Branch(br.From, br.To, 0, 0, Name: br.Name, IsTransformer: br.IsTransformer, InService: InServiceOf(s, br.Record)));
        var model = new GridModel
        {
            BaseMva = GridModel.DefaultBaseMva,
            Buses = modelBuses,
            Generators = Array.Empty<Generator>(),
            Branches = modelBranches,
            Name = authoring.CaseName,
        };

        bool auto = buses.Count > 0 && GraphLayout.HasNoLayout(model);
        if (auto)
        {
            // The run pipeline's own layout of a case with none (SimHost: GraphLayout.EnsureLayout), so the Author map
            // and the RUN map agree. The laid-out copy keeps the bus order, so bus i is still record i.
            model = GraphLayout.EnsureLayout(model);
            var laid = new List<AuthorMapBus>(buses.Count);
            for (int i = 0; i < buses.Count; i++) laid.Add(buses[i] with { X = model.Buses[i].X, Y = model.Buses[i].Y });
            buses = laid;
        }
        return new AuthorMap(model, buses, branches, auto, notDrawn);
    }

    // The records of one collection, each with its public reference and its document.
    private static IEnumerable<(CaseRef At, JsonSourceNode Rec, CaseDocument Doc)> RecordsOf(CaseSession s, RecordCollection c)
    {
        CaseDocument d = s.Documents[c.Document];
        if (s.ArrayOf(c) is not JsonSourceNode array) yield break;
        for (int i = 0; i < array.Elements.Count; i++)
            if (array.Elements[i].Kind == JsonSourceKind.Object)
                yield return (new CaseRef(d.Id, c.ArrayPointer, i, d.FileName), array.Elements[i], d);
    }

    // The bus types as the loaders derive them (JsonGridLoader: a bus's own "type", then each in-service generator: role
    // "slack" makes its bus the slack, "pq" leaves it, anything else, the default "pv" included, raises a non-slack bus
    // to PV). The map needs them for two things the RUN map also does with them: the automatic layout of a case with no
    // layout starts from the first slack bus (GraphLayout.EnsureLayout), and a flat case's display tiers too
    // (SchematicView.AssignDisplayTiers). With the same types the AUTHOR map of such a case is the RUN map, bus for bus.
    // A word the loader refuses counts as PQ here (Check reports it); the map never refuses to draw.
    private static Dictionary<int, BusType> BusTypes(CaseSession s, IReadOnlyList<AuthorMapBus> buses)
    {
        var type = new Dictionary<int, BusType>();
        foreach (AuthorMapBus b in buses)
        {
            CaseDocument d = s.Documents[b.Record.Document];
            type[b.Id] = RecordRead.Str(d.Text.Resolve(b.Record.Pointer)!, "type", d.KeyComparer)?.ToLowerInvariant() switch
            {
                "slack" or "ref" or "swing" => BusType.Slack,
                "pv" or "gen" => BusType.Pv,
                _ => BusType.Pq,
            };
        }
        foreach (RecordCollection c in s.CollectionsOf(CaseRoles.Generators))
            foreach ((CaseRef _, JsonSourceNode g, CaseDocument d) in RecordsOf(s, c))
            {
                if (RecordRead.Int(g, "bus", d.KeyComparer) is not int bus || !type.TryGetValue(bus, out BusType now)) continue;
                if (RecordRead.Member(g, "inService", d.KeyComparer) is { Kind: JsonSourceKind.False }) continue;   // F16: holds no voltage
                string role = RecordRead.Str(g, "role", d.KeyComparer) ?? "pv";
                if (role.Equals("slack", StringComparison.OrdinalIgnoreCase)) type[bus] = BusType.Slack;
                else if (role.Equals("pq", StringComparison.OrdinalIgnoreCase)) { /* a fixed injection: the bus keeps its type */ }
                else if (now != BusType.Slack) type[bus] = BusType.Pv;
            }
        return type;
    }

    // A branch record's service state: false only when "inService" is written as false.
    private static bool InServiceOf(CaseSession s, CaseRef r)
    {
        CaseDocument d = s.Documents[r.Document];
        return RecordRead.Member(d.Text.Resolve(r.Pointer)!, "inService", d.KeyComparer) is not { Kind: JsonSourceKind.False };
    }

    // ===================================================================================================================
    // Place a bus
    // ===================================================================================================================

    /// <summary>The bus collection a placed bus is added to: the first bus collection of the case (v1 buses.json; v2 the
    /// first network file's "buses"), or null when the case has none.</summary>
    public static AuthoringRecordSet? BusCollection(CaseAuthoring authoring)
    {
        ArgumentNullException.ThrowIfNull(authoring);
        return authoring.Collections().FirstOrDefault(c => c.Role == CaseRoles.Buses);
    }

    /// <summary>What the Place bus prompt asks for: the bus collection's required fields (id and base kV), never x or y
    /// (they are the clicked point).</summary>
    public static IReadOnlyList<NewRecordField> PlaceBusFields(CaseAuthoring authoring)
    {
        ArgumentNullException.ThrowIfNull(authoring);
        AuthoringRecordSet set = BusCollection(authoring) ?? throw new InvalidOperationException("This case has no bus collection to place a bus in.");
        return RecordActions.RequiredFields(authoring, set).Where(f => f.Field is not (XField or YField)).ToList();
    }

    /// <summary>
    /// Place a bus at the map WORLD point (<paramref name="worldX"/>, <paramref name="worldY"/>): one Add record of a bus
    /// from the prompt's <paramref name="values"/> with x and y from the point (see <see cref="CaseCoordinates"/>), at the
    /// end of the bus collection. In a case with no layout the other buses' automatic-layout positions are written in
    /// the same undo step (file header). Refused, changing nothing, when the engine refuses the add (a missing or
    /// colliding id, a value the schema refuses) or the values already name x or y.
    /// </summary>
    public static AuthoringResult PlaceBus(CaseAuthoring authoring, IReadOnlyList<KeyValuePair<string, string>> values, double worldX, double worldY)
    {
        ArgumentNullException.ThrowIfNull(authoring);
        ArgumentNullException.ThrowIfNull(values);
        if (values.Any(kv => kv.Key is XField or YField))
            return AuthoringResult.Refused("Place bus refused: x and y are the clicked point, so they are not typed in the prompt.");
        if (BusCollection(authoring) is not AuthoringRecordSet set)
            return AuthoringResult.Refused("Place bus refused: this case has no bus collection.");
        (double x, double y) = CaseCoordinates(worldX, worldY);
        var all = values.Concat(new[] { new KeyValuePair<string, string>(XField, Format(x)), new KeyValuePair<string, string>(YField, Format(y)) }).ToList();
        string at = " at x " + Format(x) + ", y " + Format(y);

        AuthorMap map = Build(authoring);
        if (!map.AutoLayout)
        {
            AuthoringResult r = RecordActions.Add(authoring, set, all);
            return r.Ok && r.Changed ? r with { Message = "Placed" + at + ": " + r.Message } : r;
        }

        // No layout yet: the add, then the automatic layout of every other bus written in, as ONE undo step.
        List<(CaseRef Bus, double X, double Y)> pins = map.Buses.Select(b => (b.Record, Round(b.X), Round(b.Y))).ToList();
        AuthoringResult both = authoring.ApplyAsOne("place a bus" + at + " and write the automatic layout of " + pins.Count.ToString(Inv) + " bus(es)",
            new Func<CaseSession, OpResult>[]
            {
                s => CaseOps.AddRecord(s, set.Document, set.ArrayPointer, int.MaxValue, all),
                s => SetBusCoordinates(s, pins, "write the automatic layout", null),
            });
        return both.Ok && both.Changed
            ? both with { Message = "Placed" + at + ". The case had no layout coordinates, so the map's automatic layout of the other "
                + pins.Count.ToString(Inv) + " bus(es) was written as their x and y in the same step (what the map showed is what the case now holds). " + both.Message }
            : both;
    }

    // ===================================================================================================================
    // Move a bus
    // ===================================================================================================================

    /// <summary>
    /// Move bus <paramref name="busId"/> to the map WORLD point (<paramref name="worldX"/>, <paramref name="worldY"/>):
    /// its x and y set together in ONE undo step (see <see cref="CaseCoordinates"/>). In a case with no layout the other
    /// buses' automatic-layout positions are written in the same step (file header). Refused when the id is not a bus
    /// of the map; nothing to do when the bus is already there.
    /// </summary>
    public static AuthoringResult MoveBus(CaseAuthoring authoring, int busId, double worldX, double worldY)
    {
        ArgumentNullException.ThrowIfNull(authoring);
        AuthorMap map = Build(authoring);
        if (map.Bus(busId) is not AuthorMapBus bus)
            return AuthoringResult.Refused("Move refused: bus " + busId.ToString(Inv) + " is not a bus record of this case.");
        (double x, double y) = CaseCoordinates(worldX, worldY);
        List<(CaseRef Bus, double X, double Y)> moves = map.AutoLayout
            ? map.Buses.Select(b => b.Record == bus.Record ? (b.Record, x, y) : (b.Record, Round(b.X), Round(b.Y))).ToList()
            : new List<(CaseRef Bus, double X, double Y)> { (bus.Record, x, y) };
        string what = "bus " + busId.ToString(Inv) + (bus.Name is null ? "" : " '" + bus.Name + "'");
        string description = "move " + what + " to x " + Format(x) + ", y " + Format(y)
            + (map.AutoLayout ? " (and write the automatic layout of the other " + (moves.Count - 1).ToString(Inv) + " bus(es))" : "");
        AuthoringResult r = authoring.Apply(s => SetBusCoordinates(s, moves, description, bus.Record.ToRecordRef()));
        if (!r.Ok || !r.Changed) return r;
        return r with
        {
            Message = "Moved " + what + " to x " + Format(x) + ", y " + Format(y) + "."
                + (map.AutoLayout ? " The case had no layout coordinates, so the map's automatic layout of the other " + (moves.Count - 1).ToString(Inv)
                    + " bus(es) was written as their x and y in the same step." : "")
                + " (Edit > Undo reverses it.)",
        };
    }

    // ===================================================================================================================
    // Connect two buses
    // ===================================================================================================================

    /// <summary>The collection Connect adds a <paramref name="kind"/> to: v1 transmission.json or transformer.json; v2 the
    /// "lines" or "transformers" of the network file holding bus <paramref name="fromBus"/> (else the first network file).
    /// Null when the case has no such collection.</summary>
    public static AuthoringRecordSet? BranchCollection(CaseAuthoring authoring, MapBranchKind kind, int fromBus)
    {
        ArgumentNullException.ThrowIfNull(authoring);
        string role = kind == MapBranchKind.Line ? CaseRoles.Lines : CaseRoles.Transformers;
        List<AuthoringRecordSet> sets = authoring.Collections().Where(c => c.Role == role).ToList();
        int? doc = Build(authoring).Bus(fromBus)?.Record.Document;
        return sets.FirstOrDefault(c => c.Document == doc) ?? sets.FirstOrDefault();
    }

    /// <summary>What the Connect prompt asks for after the kind: the collection's required fields other than "from" and
    /// "to" (they are the clicked buses). Empty for a v1 line or transformer (only from and to are required there).</summary>
    public static IReadOnlyList<NewRecordField> ConnectFields(CaseAuthoring authoring, MapBranchKind kind, int fromBus)
    {
        ArgumentNullException.ThrowIfNull(authoring);
        AuthoringRecordSet set = BranchCollection(authoring, kind, fromBus)
            ?? throw new InvalidOperationException("This case has no " + KindText(kind) + " collection.");
        return RecordActions.RequiredFields(authoring, set).Where(f => f.Field is not ("from" or "to")).ToList();
    }

    /// <summary>"line" or "transformer".</summary>
    public static string KindText(MapBranchKind kind) => kind == MapBranchKind.Line ? "line" : "transformer";

    /// <summary>
    /// Connect bus <paramref name="fromBus"/> to bus <paramref name="toBus"/> with a new <paramref name="kind"/>: one Add
    /// record (its "from" and "to" the two buses, the rest from the prompt's <paramref name="values"/>). Refused, changing
    /// nothing, for the same bus twice, a bus that is not a record of the case, values that name from or to, or anything
    /// the engine refuses (a missing required field, a name that collides, a reference that does not resolve).
    /// </summary>
    public static AuthoringResult Connect(CaseAuthoring authoring, MapBranchKind kind, int fromBus, int toBus, IReadOnlyList<KeyValuePair<string, string>> values)
    {
        ArgumentNullException.ThrowIfNull(authoring);
        ArgumentNullException.ThrowIfNull(values);
        if (fromBus == toBus)
            return AuthoringResult.Refused("Connect refused: a " + KindText(kind) + " joins two different buses; both clicks were on bus " + fromBus.ToString(Inv) + ".");
        AuthorMap map = Build(authoring);
        foreach (int b in new[] { fromBus, toBus })
            if (map.Bus(b) is null) return AuthoringResult.Refused("Connect refused: bus " + b.ToString(Inv) + " is not a bus record of this case.");
        if (values.Any(kv => kv.Key is "from" or "to"))
            return AuthoringResult.Refused("Connect refused: from and to are the clicked buses, so they are not typed in the prompt.");
        if (BranchCollection(authoring, kind, fromBus) is not AuthoringRecordSet set)
            return AuthoringResult.Refused("Connect refused: this case has no " + KindText(kind) + " collection.");
        var all = new List<KeyValuePair<string, string>>
        {
            new("from", fromBus.ToString(Inv)),
            new("to", toBus.ToString(Inv)),
        };
        all.AddRange(values);
        AuthoringResult r = RecordActions.Add(authoring, set, all);
        return r.Ok && r.Changed
            ? r with { Message = "Connected bus " + fromBus.ToString(Inv) + " to bus " + toBus.ToString(Inv) + " with a new " + KindText(kind) + ": " + r.Message }
            : r;
    }

    // ===================================================================================================================
    // The coordinate operation (the engine's CaseOps.SetBusCoordinates)
    // ===================================================================================================================

    /// <summary>
    /// Set "x" and "y" of every bus record in <paramref name="moves"/> as ONE transaction (one undo step) described as
    /// <paramref name="description"/>: each coordinate written in this file's number form (<see cref="Format"/>), the
    /// change itself made by the engine (CaseOps.SetBusCoordinates: parsed, lossless, every touched record checked for
    /// new schema errors, refused changing nothing if any). <paramref name="select"/> is the record the selection follows.
    /// </summary>
    internal static OpResult SetBusCoordinates(CaseSession s, IReadOnlyList<(CaseRef Bus, double X, double Y)> moves, string description, RecordRef? select)
    {
        ArgumentNullException.ThrowIfNull(moves);
        if (moves.Select(m => m.Bus).Distinct().Count() != moves.Count)
            throw new ArgumentException("a bus record is listed twice in one coordinate change", nameof(moves));
        return CaseOps.SetBusCoordinates(s, moves.Select(m => new CaseOps.CoordinateEdit(m.Bus.ToRecordRef(), Format(m.X), Format(m.Y))).ToList(),
            description, select);
    }
}
