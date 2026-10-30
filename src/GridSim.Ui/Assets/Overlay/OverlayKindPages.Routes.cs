using GridSim.Core.IO.Json.Overlay;
using GridSim.Core.Model;
using GridSim.Ui.Assets.Keys;
using GridSim.Ui.Assets.Overviews;

namespace GridSim.Ui.Assets.Overlay;

// =====================================================================================================================
// UI programme Phase 5d (2026-10-21): the OVERVIEW content and the CHECK notes of the line route kinds.
//
//   - lineRoute:         the line it carries (a link), its spans in route order with their towers, lengths, positions
//                        (geographic or chainage, whichever each span writes), tower types and conductors; the total
//                        route length (the sum of the span lengths, display only) beside the line record's lengthKm
//                        where written; its earth wires (the Phase 5e records under it); where it is on the map.
//   - span:              its route, its place along it (from and to tower), its length, chainage, sag and far-tower
//                        position, and the conductor strung in it (the conductor record's own values).
//   - towerType:         its conductor attachment points (the conductorPosition children) as a table; the spans using it.
//   - conductorPosition: its own values and the tower type it belongs to.
//   - conductorType:     cross-section, diameter, DC resistance and current rating with their units; what uses it.
//
// The CHECK notes are DISPLAY ONLY, never errors (the overlay checker is unchanged; the case still loads and solves
// exactly as written): the route's length differs from the line's by more than 1%; the spans do not form a continuous
// chain by chainage; a span names no conductor type. The span order, the tower numbering, the length sum and the
// continuity rule are LineRoutes' (shared with the map layer), so the page and the map can never disagree.
// =====================================================================================================================

public static partial class OverlayKindPages
{
    // ---- the table captions (public so the tests and views name them once) ----

    /// <summary>A line route's spans, in route order.</summary>
    public const string SpansTable = "SPANS IN ROUTE ORDER" + TableTag;

    /// <summary>A line route's total length beside the line's.</summary>
    public const string RouteLengthTable = "ROUTE LENGTH  (DISPLAY ONLY)" + TableTag;

    /// <summary>The earth wires strung on a route.</summary>
    public const string RouteEarthWiresTable = "EARTH WIRES ON THIS ROUTE" + TableTag;

    /// <summary>A span's own values.</summary>
    public const string SpanTable = "SPAN" + TableTag;

    /// <summary>The conductor strung in a span (the conductor record's values).</summary>
    public const string SpanConductorTable = "CONDUCTOR IN THIS SPAN" + TableTag;

    /// <summary>A tower type's conductor attachment points.</summary>
    public const string PositionsTable = "CONDUCTOR POSITIONS" + TableTag;

    /// <summary>A tower type's own values.</summary>
    public const string TowerTypeTable = "TOWER TYPE" + TableTag;

    /// <summary>One conductor position's values.</summary>
    public const string PositionTable = "CONDUCTOR POSITION" + TableTag;

    /// <summary>A conductor type's own values.</summary>
    public const string ConductorTable = "CONDUCTOR TYPE" + TableTag;

    /// <summary>The words of the tower numbering, said under every span table.</summary>
    public const string TowerNumbering =
        "Towers are numbered along the route in span order: T0 is the route's start and Tn the tower at the far end of span n (a span places its far tower).";

    // The text of a record reference in a table or fact: "ov.zebra (Zebra)" (id and code), the reference as written with
    // why it names nothing, or the words given for an absent one.
    private static string RecordCell(OverlayEntry e, string role, OverlayIndex ov, string absent)
    {
        OverlayRefLink? link = Ref(e, role);
        if (link is null) return absent;
        OverlayEntry? target = RecordRef(e, role, ov);
        if (target is null) return link.Written + " (" + link.Resolved + ")";
        return target.Id + (Written(target.Record, "code") is string code ? " (" + code + ")" : "");
    }

    // The authored line a route carries in the running model (by the resolved 'line' reference), or null.
    private static Branch? CarriedLine(OverlayEntry route, OverlayIndex ov) =>
        Ref(route, "line") is { Target.Model: ModelRef mr } ? mr.FindBranch(ov.Assets.Model)?.Branch : null;

    // The route's spans in route order, over the entries (the children in overlay order are the route's records).
    private static LineRouteSpans SpansOf(OverlayEntry route) => LineRoutes.Order(route.Children.Select(c => c.Record));

    // A position cell: "55.9" as written, or "-" when this span does not write it (a span may be placed by chainage).
    private static string PositionCell(AssetOverlayRecord r, string name) => Written(r, name) ?? "-";

    // =================================================================================================================
    // lineRoute
    // =================================================================================================================

    private static OverlayKindContent LineRoute(OverlayEntry e, OverlayIndex ov)
    {
        AssetOverlayRecord r = e.Record;
        LineRouteSpans spans = SpansOf(e);
        Branch? line = CarriedLine(e, ov);
        double? lineKm = LineRoutes.LineLengthKm(line);
        double? routeKm = LineRoutes.RouteLengthKm(r, spans, out string routeSource);
        double? diff = LineRoutes.Difference(routeKm, lineKm);
        double? totalM = spans.TotalM;
        List<OverlayEntry> earthWires = e.Children.Where(c => c.Kind == "earthWire").ToList();
        string? notDrawn = LineRoutes.NotDrawnReason(spans);

        string lineCell = RefCell(Ref(e, "line"));
        string lineLength = Ref(e, "line") is null ? "unknown: the route names no line"
            : line is null ? "unknown: the line is not in the running model"
            : lineKm is double k ? N(k) + " km (the line record's lengthKm)"
            : "not written in the line record (lengthKm 0 = unknown)";
        string comparison = diff is double d
            ? (LineRoutes.Beyond(d) ? "differs by " + LineRoutes.Percent(d) + "% (more than 1%; see CHECK)" : "within 1% (" + LineRoutes.Percent(d) + "%)")
              + ": " + routeSource + " against the line's lengthKm"
            : "not compared: " + (routeKm is null ? "the route has no length (" + routeSource + ")" : "the line's length is not written");

        var facts = new List<OverviewFact>
        {
            new("Carries line", lineCell),
            new("Route name", Written(r, "name") ?? "absent"),
            new("Spans", Count(spans.Spans.Count)),
            new("Span order", LineRoutes.OrderWords(spans)),
            new("Tower positions", LineRoutes.PositionWords(spans)),
            new("Total route length", totalM is double t
                ? N(t) + " m (" + N(t / 1000.0) + " km): the sum of the span lengths, display only"
                : "no span writes a length"),
            new("Route length as written", WithUnit(r, "routeLengthKm", "km", "absent (routeLengthKm not written)")),
            new("Line length", lineLength),
            new("Route against line", comparison),
            new("Earth wires", Count(earthWires.Count)),
            new("Map", notDrawn is null
                ? "drawn on the NETWORK map's Line routes layer (View / Line routes) through " + Count(spans.WithGeo) + " towers"
                : "not drawn on the NETWORK map: " + notDrawn),
            new("Line values", "the solver uses the line's authored r, x, b and lengthKm; this route changes none of them"),
        };

        var spanRows = new List<IReadOnlyList<string>>();
        foreach (LineRouteSpan s in spans.Spans)
        {
            OverlayEntry se = ov.Find(s.Id)!;
            spanRows.Add(new[]
            {
                s.Id, s.FromTower, s.ToTower, Cell(s.Record, "lengthM"), PositionCell(s.Record, "chainageM"),
                PositionCell(s.Record, "latitudeDeg"), PositionCell(s.Record, "longitudeDeg"),
                RecordCell(se, "tower", ov, "none given"), RecordCell(se, "conductor", ov, "none given"), Cell(s.Record, "sagM"),
            });
        }
        string spansNote = spans.Spans.Count == 0 ? "No span record sits under this route."
            : TowerNumbering + " Spans " + LineRoutes.OrderWords(spans) + "; '-' = not written on that span.";

        string pct = diff is double dd ? LineRoutes.Percent(dd) : "not compared";
        var tables = new List<OverviewTable>
        {
            OverviewTable.Of(SpansTable,
                new[] { "Span", "From tower", "To tower", "Length (m)", "Chainage at start (m)", "Latitude (deg)", "Longitude (deg)", "Tower type", "Conductor", "Sag (m)" },
                spanRows, spansNote),
            OverviewTable.Of(RouteLengthTable, new[] { "Quantity", "Value", "Unit" }, new[]
            {
                new[] { "Sum of the span lengths", totalM is double t1 ? N(t1) : "none", "m" },
                new[] { "Sum of the span lengths", totalM is double t2 ? N(t2 / 1000.0) : "none", "km" },
                new[] { "Route length as written (routeLengthKm)", Cell(r, "routeLengthKm"), "km" },
                new[] { "Line length as written (lengthKm)", lineKm is double l ? N(l) : "not written", "km" },
                new[] { "Route against line", pct, "%" },
            }, "Display only: the route's length is " + routeSource + "; a difference of more than 1% of the line's length is a CHECK note, never an error."),
            OverviewTable.Of(RouteEarthWiresTable, new[] { "Earth wire", "Earth wires (count)", "Bonded at both ends", "Conductor" },
                earthWires.Select(w => (IReadOnlyList<string>)new[]
                {
                    w.Id, Cell(w.Record, "count"), Cell(w.Record, "bondedAtBothEnds"), RecordCell(w, "conductor", ov, "none given"),
                }),
                earthWires.Count == 0 ? "No earth wire record sits under this route." : "The earth wire records under this route, in overlay order."),
        };

        // Links: each earth wire on the route (its record), after the generic children and references.
        var rows = earthWires.Select(w => new OverviewRow(w.Id, "earth wire on this route  " + w.KindTitle, w.Kind, w.Key)).ToList();
        return new OverlayKindContent(facts, tables, rows);
    }

    // The display-only notes of a line route: its length against the line's, the continuity of its spans, and each span
    // that names no conductor type (that note at the span's own pointer).
    private static IEnumerable<OverlayCheckRow> RouteNotes(OverlayEntry e, OverlayIndex ov)
    {
        AssetOverlayRecord r = e.Record;
        LineRouteSpans spans = SpansOf(e);
        Branch? line = CarriedLine(e, ov);
        double? lineKm = LineRoutes.LineLengthKm(line);
        double? routeKm = LineRoutes.RouteLengthKm(r, spans, out string source);
        double? diff = LineRoutes.Difference(routeKm, lineKm);
        if (LineRoutes.Beyond(diff))
            yield return new OverlayCheckRow(r.Pointer, "Route length against line length: " + source + " is " + N(routeKm!.Value) + " km but the line '"
                + line!.Name + "' writes lengthKm " + N(lineKm!.Value) + " km, a difference of " + LineRoutes.Percent(diff!.Value)
                + "% (more than 1%). Display only: the solver uses the line's authored values.");
        // The route's own routeLengthKm against its spans (both written, beyond 1% of the spans' sum).
        if (spans.TotalM is double m && m > 0 && Number(r, "routeLengthKm") is double written)
        {
            double d = (written - m / 1000.0) / (m / 1000.0);
            if (LineRoutes.Beyond(d))
                yield return new OverlayCheckRow(r.Pointer, "The route's routeLengthKm " + N(written) + " km differs from the sum of its span lengths "
                    + N(m / 1000.0) + " km by " + LineRoutes.Percent(d) + "% (more than 1%). Display only.");
        }
        foreach (string p in LineRoutes.ContinuityProblems(spans))
            yield return new OverlayCheckRow(r.Pointer, "The spans do not form a continuous chain: " + p + ". Display only.");
        foreach (LineRouteSpan s in spans.Spans)
            if (ConductorProblem(ov.Find(s.Id)!, ov) is string c)
                yield return new OverlayCheckRow(s.Record.Pointer, c);
    }

    // The note of a span that names no conductor type, or null.
    private static string? ConductorProblem(OverlayEntry span, OverlayIndex ov)
    {
        OverlayRefLink? link = Ref(span, "conductor");
        if (link is null)
            return "Span '" + span.Id + "' names no conductor type (its 'conductor' reference is absent). Display only.";
        OverlayEntry? c = RecordRef(span, "conductor", ov);
        if (c is null || c.Kind != "conductorType")
            return "Span '" + span.Id + "' names " + link.Written + ", which is not a conductor type of the overlay (" + link.Resolved + "). Display only.";
        return null;
    }

    private static IEnumerable<OverlayCheckRow> SpanNotes(OverlayEntry e, OverlayIndex ov)
    {
        if (ConductorProblem(e, ov) is string c) yield return new OverlayCheckRow(e.Record.Pointer, c);
    }

    // =================================================================================================================
    // span
    // =================================================================================================================

    private static OverlayKindContent Span(OverlayEntry e, OverlayIndex ov)
    {
        AssetOverlayRecord r = e.Record;
        OverlayEntry? route = e.Parent is { Kind: "lineRoute" } p ? p : null;
        LineRouteSpans? spans = route is null ? null : SpansOf(route);
        LineRouteSpan? me = spans?.Spans.FirstOrDefault(s => string.Equals(s.Id, e.Id, StringComparison.Ordinal));
        OverlayEntry? conductor = RecordRef(e, "conductor", ov);
        if (conductor is not null && conductor.Kind != "conductorType") conductor = null;

        string from = me is null ? "unknown: the span is not under a line route"
            : me.Number == 1 ? me.FromTower + " (the route's start)"
            : me.FromTower + " (the far tower of span '" + spans!.Spans[me.Number - 2].Id + "')";
        string to = me is null ? "unknown: the span is not under a line route" : me.ToTower + " (this span's far tower)";
        string position = me is { HasGeo: true }
            ? "latitude " + Written(r, "latitudeDeg") + " deg, longitude " + Written(r, "longitudeDeg") + " deg (WGS84)"
            : Written(r, "chainageM") is not null ? "not given (the span is placed by chainage)" : "not given";
        double? chainage = me?.ChainageM ?? Number(r, "chainageM");
        double? length = Number(r, "lengthM");

        var facts = new List<OverviewFact>
        {
            new("Route", route is null ? "none: the span is not under a line route" : "lineRoute '" + route.Id + "'  (carries " + RefCell(Ref(route, "line")) + ")"),
            new("Span number", me is null ? "not numbered" : Count(me.Number) + " of " + Count(spans!.Spans.Count) + " (spans " + LineRoutes.OrderWords(spans) + ")"),
            new("From tower", from),
            new("To tower", to),
            new("Length", WithUnit(r, "lengthM", "m", "absent (the kind requires it; see CHECK)")),
            new("Chainage at start", WithUnit(r, "chainageM", "m", "absent")),
            new("Far tower position", position),
            new("Tower type", RecordCell(e, "tower", ov, "none given")),
            new("Conductor", RecordCell(e, "conductor", ov, "none given (see CHECK)")),
            new("Sag", WithUnit(r, "sagM", "m", "absent")),
            new("Clearance", "not carried: the span kind has no clearance parameter"),
        };
        var tables = new List<OverviewTable>
        {
            OverviewTable.Of(SpanTable, new[] { "Quantity", "Value", "Unit" }, new[]
            {
                new[] { "Length", Cell(r, "lengthM"), "m" },
                new[] { "Chainage at start", Cell(r, "chainageM"), "m" },
                new[] { "Chainage at end (start + length)", chainage is double c0 && length is double l0 ? N(c0 + l0) : Absent, "m" },
                new[] { "Sag at the design temperature", Cell(r, "sagM"), "m" },
                new[] { "Far tower latitude (WGS84)", Cell(r, "latitudeDeg"), "deg" },
                new[] { "Far tower longitude (WGS84)", Cell(r, "longitudeDeg"), "deg" },
            }, "As written in " + r.Document + " " + r.Pointer + "; the chainage at end is display only. " + TowerNumbering),
            OverviewTable.Of(SpanConductorTable,
                new[] { "Conductor", "Code", "Material", "Cross-section (mm2)", "DC resistance (ohm/km)", "Rated current (A)" },
                conductor is null ? Array.Empty<IReadOnlyList<string>>() : new[]
                {
                    (IReadOnlyList<string>)new[]
                    {
                        conductor.Id, Cell(conductor.Record, "code"), Cell(conductor.Record, "material"), Cell(conductor.Record, "crossSectionMm2"),
                        Cell(conductor.Record, "dcResistanceOhmPerKm"), Cell(conductor.Record, "ratedCurrentA"),
                    },
                },
                conductor is null ? "No conductor type is named for this span (see CHECK)." : "The conductor record's own values."),
        };
        return new OverlayKindContent(facts, tables, Array.Empty<OverviewRow>());
    }

    // =================================================================================================================
    // towerType, conductorPosition, conductorType
    // =================================================================================================================

    // Every record of the overlay whose reference 'role' names this entry, in overlay order.
    private static List<OverlayEntry> PointedAtBy(OverlayEntry target, string kind, string role, OverlayIndex ov) =>
        ov.Entries.Where(x => x.Kind == kind && RecordRef(x, role, ov) == target).ToList();

    private static string UnderRoute(OverlayEntry x) => x.Parent is { } p ? "under route '" + p.Id + "'" : "under no route";

    private static OverlayKindContent TowerType(OverlayEntry e, OverlayIndex ov)
    {
        AssetOverlayRecord r = e.Record;
        List<OverlayEntry> positions = e.Children.Where(c => c.Kind == "conductorPosition").ToList();
        List<OverlayEntry> spans = PointedAtBy(e, "span", "tower", ov);
        var facts = new List<OverviewFact>
        {
            new("Design code", Written(r, "code") ?? "absent"),
            new("Height to peak", WithUnit(r, "heightM", "m", "absent")),
            new("Circuits", Written(r, "circuits") ?? "absent"),
            new("Conductor positions", Count(positions.Count)),
            new("Spans using this tower type", Count(spans.Count)),
        };
        var tables = new List<OverviewTable>
        {
            OverviewTable.Of(TowerTypeTable, new[] { "Quantity", "Value", "Unit" }, new[]
            {
                new[] { "Design code", Cell(r, "code"), "" },
                new[] { "Height to peak", Cell(r, "heightM"), "m" },
                new[] { "Circuits", Cell(r, "circuits"), "" },
            }, "As written in " + r.Document + " " + r.Pointer + "."),
            OverviewTable.Of(PositionsTable, new[] { "Position", "Phase", "Circuit", "x (m)", "y (m)" },
                positions.Select(x => (IReadOnlyList<string>)new[]
                {
                    x.Id, Cell(x.Record, "phase"), Cell(x.Record, "circuit"), Cell(x.Record, "xM"), Cell(x.Record, "yM"),
                }),
                positions.Count == 0 ? "No conductor position record sits under this tower type."
                    : "x is the horizontal offset from the tower centre line, y the height of the attachment point above ground; in overlay order."),
        };
        var rows = spans.Select(s => new OverviewRow(s.Id, "span using this tower type, " + UnderRoute(s) + "  " + s.KindTitle, s.Kind, s.Key)).ToList();
        return new OverlayKindContent(facts, tables, rows);
    }

    private static OverlayKindContent ConductorPosition(OverlayEntry e)
    {
        AssetOverlayRecord r = e.Record;
        string tower = e.Parent is { } p ? p.Id + (Written(p.Record, "code") is string code ? " (" + code + ")" : "") : "none (not under a tower type)";
        var facts = new List<OverviewFact>
        {
            new("Tower type", tower),
            new("Phase", Written(r, "phase") ?? "absent (the kind requires it; see CHECK)"),
            new("Circuit", Written(r, "circuit") ?? "absent"),
            new("Offset from centre line", WithUnit(r, "xM", "m", "absent (the kind requires it; see CHECK)")),
            new("Height above ground", WithUnit(r, "yM", "m", "absent (the kind requires it; see CHECK)")),
        };
        var tables = new List<OverviewTable>
        {
            OverviewTable.Of(PositionTable, new[] { "Tower type", "Phase", "Circuit", "x (m)", "y (m)" }, new[]
            {
                new[] { tower, Cell(r, "phase"), Cell(r, "circuit"), Cell(r, "xM"), Cell(r, "yM") },
            }, "As written in " + r.Document + " " + r.Pointer + "."),
        };
        return new OverlayKindContent(facts, tables, Array.Empty<OverviewRow>());
    }

    private static OverlayKindContent ConductorType(OverlayEntry e, OverlayIndex ov)
    {
        AssetOverlayRecord r = e.Record;
        List<OverlayEntry> spans = PointedAtBy(e, "span", "conductor", ov);
        List<OverlayEntry> wires = PointedAtBy(e, "earthWire", "conductor", ov);
        var facts = new List<OverviewFact>
        {
            new("Code", Written(r, "code") ?? "absent"),
            new("Material", Written(r, "material") ?? "absent"),
            new("Cross-section", WithUnit(r, "crossSectionMm2", "mm2", "absent (the kind requires it; see CHECK)")),
            new("Diameter", WithUnit(r, "diameterMm", "mm", "absent")),
            new("DC resistance at 20 degC", WithUnit(r, "dcResistanceOhmPerKm", "ohm/km", "absent")),
            new("Rated current", WithUnit(r, "ratedCurrentA", "A", "absent")),
            new("Used by", Count(spans.Count) + (spans.Count == 1 ? " span, " : " spans, ") + Count(wires.Count) + (wires.Count == 1 ? " earth wire" : " earth wires")),
            new("Line impedance", "the lines' authored per-unit r, x and b stay what the solver uses; this record does not change them"),
        };
        var tables = new List<OverviewTable>
        {
            OverviewTable.Of(ConductorTable, new[] { "Quantity", "Value", "Unit" }, new[]
            {
                new[] { "Code", Cell(r, "code"), "" },
                new[] { "Material", Cell(r, "material"), "" },
                new[] { "Cross-section", Cell(r, "crossSectionMm2"), "mm2" },
                new[] { "Diameter", Cell(r, "diameterMm"), "mm" },
                new[] { "DC resistance at 20 degC", Cell(r, "dcResistanceOhmPerKm"), "ohm/km" },
                new[] { "Rated current (continuous)", Cell(r, "ratedCurrentA"), "A" },
            }, "As written in " + r.Document + " " + r.Pointer + "."),
        };
        var rows = new List<OverviewRow>();
        foreach (OverlayEntry s in spans) rows.Add(new OverviewRow(s.Id, "span strung with this conductor, " + UnderRoute(s) + "  " + s.KindTitle, s.Kind, s.Key));
        foreach (OverlayEntry w in wires) rows.Add(new OverviewRow(w.Id, "earth wire of this conductor, " + UnderRoute(w) + "  " + w.KindTitle, w.Kind, w.Key));
        return new OverlayKindContent(facts, tables, rows);
    }
}
