using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace GridSim.Core.IO.Cim.Shacl;

/// <summary>One validation result (SHACL §3.6): the focus node, the path (empty for a node shape), the value node (empty
/// for a count), the constraint component, the shape, the severity and the message.</summary>
public sealed record ShaclResult(string Severity, string Focus, string Path, string Component, string Shape, string Value, string Message);

/// <summary>A constraint the native checker did not run, named: the shape, what was not run and why.</summary>
public sealed record ShaclNotRun(string Shape, string What, string Reason);

/// <summary>
/// The three outcomes of a SHACL check. <see cref="Pass"/>: every constraint that applies was run and none gave a result.
/// <see cref="Fail"/>: a constraint that was run gave a result. <see cref="NotRun"/>: no run constraint gave a result, but a
/// constraint that applies (or a shape inside one) was not run, so no verdict is given.
/// </summary>
public enum ShaclOutcome { Pass, Fail, NotRun }

/// <summary>
/// The report of <see cref="ShaclValidator.Validate"/>. <see cref="Outcome"/> is <see cref="ShaclOutcome.Fail"/> when any
/// result (of any severity, as SHACL's sh:conforms) was found, <see cref="ShaclOutcome.NotRun"/> when none was found but a
/// constraint that applies to a focus node was not run (or a SPARQL-based target left the focus nodes incomplete), and
/// <see cref="ShaclOutcome.Pass"/> only when every constraint that applies was run and none gave a result.
/// <see cref="NotRun"/> lists every constraint that was not run, by name. Results are sorted by focus, path, component,
/// shape and value (ordinal), and numbers are written in the invariant culture.
/// </summary>
public sealed record ShaclReport(ShaclOutcome Outcome, IReadOnlyList<ShaclResult> Results, IReadOnlyList<ShaclNotRun> NotRun,
    int ShapesRun, int FocusNodes, int SparqlShapesNotRun, IReadOnlyList<string> Notes)
{
    /// <summary>True only for <see cref="ShaclOutcome.Pass"/>: a check with a part not run never conforms.</summary>
    public bool Conforms => Outcome == ShaclOutcome.Pass;

    public int Violations => Results.Count(r => r.Severity == "Violation");

    /// <summary>The verdict in words: "conforms", "does not conform" or "not determined".</summary>
    public string Verdict => Outcome switch { ShaclOutcome.Pass => "conforms", ShaclOutcome.Fail => "does not conform", _ => "not determined" };

    /// <summary>One line: the verdict, the counts and the subset statement.</summary>
    public string Summary =>
        Verdict + string.Create(CultureInfo.InvariantCulture,
            $" (SHACL Core subset: {Results.Count} result(s), {Violations} violation(s); {ShapesRun} shape(s) run on {FocusNodes} focus node(s); {SparqlShapesNotRun} SPARQL shape(s) not run; {NotRun.Count} constraint(s) not run)");

    /// <summary>The report as CSV: a header, the results (kind "result"), then the constraints not run (kind "not-run").</summary>
    public string ToCsv()
    {
        var sb = new StringBuilder("kind,severity,focus,path,component,shape,value,message\n");
        foreach (ShaclResult r in Results)
            sb.Append("result,").Append(Csv(r.Severity)).Append(',').Append(Csv(r.Focus)).Append(',').Append(Csv(r.Path)).Append(',')
              .Append(Csv(r.Component)).Append(',').Append(Csv(r.Shape)).Append(',').Append(Csv(r.Value)).Append(',').Append(Csv(r.Message)).Append('\n');
        foreach (ShaclNotRun n in NotRun)
            sb.Append("not-run,,,,").Append(Csv(n.What)).Append(',').Append(Csv(n.Shape)).Append(",,").Append(Csv(n.Reason)).Append('\n');
        return sb.ToString();
    }

    /// <summary>The report as JSON (format gridsim-shacl-report/1).</summary>
    public string ToJson()
    {
        using var ms = new MemoryStream();
        using (var w = new Utf8JsonWriter(ms, new JsonWriterOptions { Indented = true }))
        {
            w.WriteStartObject();
            w.WriteString("format", "gridsim-shacl-report/1");
            w.WriteBoolean("conforms", Conforms);
            w.WriteString("verdict", Verdict);
            w.WriteString("scope", "SHACL Core subset (W3C SHACL 2017, sections 2 to 4); SHACL-SPARQL constraints and targets are not run");
            w.WriteNumber("shapesRun", ShapesRun);
            w.WriteNumber("focusNodes", FocusNodes);
            w.WriteNumber("sparqlShapesNotRun", SparqlShapesNotRun);
            w.WriteStartArray("results");
            foreach (ShaclResult r in Results)
            {
                w.WriteStartObject();
                w.WriteString("severity", r.Severity); w.WriteString("focus", r.Focus); w.WriteString("path", r.Path);
                w.WriteString("component", r.Component); w.WriteString("shape", r.Shape); w.WriteString("value", r.Value); w.WriteString("message", r.Message);
                w.WriteEndObject();
            }
            w.WriteEndArray();
            w.WriteStartArray("notRun");
            foreach (ShaclNotRun n in NotRun)
            {
                w.WriteStartObject();
                w.WriteString("shape", n.Shape); w.WriteString("what", n.What); w.WriteString("reason", n.Reason);
                w.WriteEndObject();
            }
            w.WriteEndArray();
            w.WriteStartArray("notes");
            foreach (string s in Notes) w.WriteStringValue(s);
            w.WriteEndArray();
            w.WriteEndObject();
        }
        return Encoding.UTF8.GetString(ms.ToArray()).Replace("\r\n", "\n", StringComparison.Ordinal) + "\n";
    }

    // A string cell: the formula-injection guard (CWE-1236), then the usual quoting.
    private static string Csv(string s) => Quote(GridSim.Core.IO.CsvFormulaGuard.Neutralise(s));

    private static string Quote(string s) =>
        s.Contains(',', StringComparison.Ordinal) || s.Contains('"', StringComparison.Ordinal) || s.Contains('\n', StringComparison.Ordinal) || s.Contains('\r', StringComparison.Ordinal) ? "\""+ s.Replace("\"", "\"\"", StringComparison.Ordinal) + "\"" : s;
}

/// <summary>WP24: one SHACL feature of a census: the predicate, how many times shapes use it, and how the checker handles it
/// ("run", "inert" or "not run").</summary>
public sealed record ShaclCensusRow(string Feature, int Count, string Handling);

/// <summary>WP24: what <see cref="ShaclValidator.Census"/> found in a shapes graph.</summary>
/// <param name="Rows">Every SHACL predicate used on a shape, by name.</param>
/// <param name="NodeShapes">Subjects typed sh:NodeShape.</param>
/// <param name="PropertyShapes">Subjects typed sh:PropertyShape.</param>
/// <param name="Shapes">All shapes, typed or nested.</param>
/// <param name="SparqlConstraints">sh:sparql uses (SHACL-SPARQL constraints).</param>
/// <param name="SparqlTargets">sh:target uses (SHACL-AF targets).</param>
/// <param name="PredicatePaths">sh:path values that are a plain predicate.</param>
/// <param name="ComplexPaths">sh:path values that are a path expression (a blank node: inverse, sequence and so on).</param>
/// <param name="Triples">Triples in the shapes graph.</param>
public sealed record ShaclCensus(IReadOnlyList<ShaclCensusRow> Rows, int NodeShapes, int PropertyShapes, int Shapes, int SparqlConstraints,
    int SparqlTargets, int PredicatePaths, int ComplexPaths, int Triples)
{
    /// <summary>Uses of features the checker does not run (sh:sparql included).</summary>
    public int NotRunUses => Rows.Where(r => r.Handling == "not run").Sum(r => r.Count);
}

/// <summary>
/// RC5 WP10: a native checker for the SHACL Core subset (W3C Shapes Constraint Language, Recommendation 2017, §§2-4), with no
/// third-party dependency. <b>Targets</b>: sh:targetClass (with the rdfs:subClassOf closure of the RDFS graph when one is
/// given; without it the exact rdf:type only, and the report says so), sh:targetNode, sh:targetSubjectsOf,
/// sh:targetObjectsOf. <b>Paths</b>: a predicate, sh:inversePath and a sequence. <b>Constraints</b>: sh:class,
/// sh:datatype, sh:nodeKind, sh:minCount, sh:maxCount, sh:minInclusive, sh:maxInclusive, sh:minExclusive,
/// sh:maxExclusive, sh:minLength, sh:maxLength, sh:pattern (with sh:flags; .NET Regex, culture invariant, a 1 s match
/// timeout), sh:in, sh:hasValue, sh:node, sh:property, sh:and, sh:or, sh:not, sh:xone, sh:equals and sh:disjoint, with
/// sh:severity, sh:message and sh:deactivated. <b>Not run, by name</b>: every sh:sparql constraint and SPARQL-based target
/// (sh:target), every constraint component defined in the shapes graph (sh:ConstraintComponent with its parameters), the
/// other SHACL Core components (sh:closed, sh:languageIn, sh:uniqueLang, sh:lessThan, sh:lessThanOrEquals,
/// sh:qualifiedValueShape) and the other path forms.
/// <b>Three outcomes</b>: a shape checked on a value node passes, fails or is not run (<see cref="ShaclOutcome"/>). A
/// shape with a part not run never passes: when none of its run constraints gives a result, it is not run. sh:node,
/// sh:not, sh:and, sh:or and sh:xone follow the three values. They give a definite answer only when the shapes that were not
/// run cannot change it (sh:or passes when one member passes, sh:and fails when one member fails, sh:not of a failing shape
/// passes). Otherwise the outer constraint is itself not run, and it is listed by name. A part that was not run is never
/// turned into a pass or into a violation.
/// </summary>
/// <remarks>
/// <para>Literals without a datatype (CGMES RDF/XML) satisfy sh:datatype when their text is a valid lexical form of it
/// (xsd:float, double, decimal, integer and its subtypes, boolean, string, dateTime, date, anyURI), because CGMES leaves the
/// datatype to the profile; and in sh:in and sh:hasValue they equal a literal of the same text whatever its datatype.
/// Numbers compare as doubles; a value that is not a number is not comparable, which is a result.</para>
/// <para>Determinism: shapes are validated in term order, focus nodes in term order, and the results are sorted.</para>
/// </remarks>
public static class ShaclValidator
{
    private const string Sh = "http://www.w3.org/ns/shacl#";
    private const string Xsd = "http://www.w3.org/2001/XMLSchema#";
    private static RdfTerm S(string local) => RdfTerm.Iri(Sh + local);
    private static readonly RdfTerm NodeShape = S("NodeShape"), PropertyShape = S("PropertyShape"), PathP = S("path"), PropertyP = S("property");

    private static readonly HashSet<string> Supported = new(StringComparer.Ordinal)
    {
        "class", "datatype", "nodeKind", "minCount", "maxCount", "minInclusive", "maxInclusive", "minExclusive", "maxExclusive",
        "minLength", "maxLength", "pattern", "flags", "in", "hasValue", "node", "property", "and", "or", "not", "xone", "equals", "disjoint",
    };

    /// <summary>Predicates of a shape that state nothing to check (labels, grouping, targets, the path).</summary>
    private static readonly HashSet<string> Inert = new(StringComparer.Ordinal)
    {
        "name", "description", "message", "severity", "group", "order", "defaultValue", "deactivated", "path", "targetClass", "targetNode",
        "targetSubjectsOf", "targetObjectsOf", "prefixes", "declare", "suggestedShapesGraph", "shapesGraph", "intersection", "union",
    };

    /// <summary>
    /// WP24: the census of a shapes graph. Every predicate in the SHACL namespace that appears on a shape (any subject typed
    /// sh:NodeShape or sh:PropertyShape, or nested under one through sh:property, sh:node, sh:not, sh:and, sh:or or sh:xone),
    /// counted by use, with how <see cref="Validate"/> handles it: "run" (a constraint of the Core subset), "inert" (a label,
    /// grouping, target or the path: nothing to check) or "not run" (reported NOT RUN by name when it applies). Also counted:
    /// node and property shapes, sh:sparql constraints, SPARQL-based (sh:target) targets, and path forms. Rows are sorted by
    /// feature name (ordinal), so the census does not depend on the order of the files.
    /// </summary>
    public static ShaclCensus Census(RdfGraph shapes)
    {
        ArgumentNullException.ThrowIfNull(shapes);
        var all = new SortedSet<RdfTerm>();
        foreach (RdfTerm t in new[] { NodeShape, PropertyShape }) foreach (RdfTerm sh in shapes.Subjects(RdfGraph.Type, t)) all.Add(sh);
        int nodeShapes = shapes.Subjects(RdfGraph.Type, NodeShape).Count, propertyShapes = shapes.Subjects(RdfGraph.Type, PropertyShape).Count;
        // Nested (untyped) shapes: reached through the shape-valued parameters.
        var queue = new Queue<RdfTerm>(all);
        while (queue.Count > 0)
        {
            RdfTerm s = queue.Dequeue();
            foreach (RdfTriple t in shapes.About(s))
            {
                if (!t.P.IsIri || !t.P.Value.StartsWith(Sh, StringComparison.Ordinal)) continue;
                string local = t.P.Value[Sh.Length..];
                var nested = new List<RdfTerm>();
                if (local is "property" or "node" or "not" or "qualifiedValueShape") nested.Add(t.O);
                if (local is "and" or "or" or "xone" && shapes.ListOf(t.O) is IReadOnlyList<RdfTerm> members) nested.AddRange(members);
                foreach (RdfTerm n in nested) if (!n.IsLiteral && all.Add(n)) queue.Enqueue(n);
            }
        }
        var counts = new SortedDictionary<string, int>(StringComparer.Ordinal);
        int sparql = 0, sparqlTargets = 0, predicatePaths = 0, otherPaths = 0;
        foreach (RdfTerm s in all)
            foreach (RdfTriple t in shapes.About(s))
            {
                if (!t.P.IsIri || !t.P.Value.StartsWith(Sh, StringComparison.Ordinal)) continue;
                string local = t.P.Value[Sh.Length..];
                counts[local] = counts.GetValueOrDefault(local) + 1;
                if (local == "sparql") sparql++;
                if (local == "target") sparqlTargets++;
                if (local == "path") { if (t.O.IsIri) predicatePaths++; else otherPaths++; }
            }
        var rows = counts.Select(kv => new ShaclCensusRow("sh:" + kv.Key, kv.Value,
            Supported.Contains(kv.Key) ? "run" : Inert.Contains(kv.Key) ? "inert" : "not run")).ToList();
        return new ShaclCensus(rows, nodeShapes, propertyShapes, all.Count, sparql, sparqlTargets, predicatePaths, otherPaths, shapes.Count);
    }

    /// <summary>Validate <paramref name="data"/> against <paramref name="shapes"/>; <paramref name="rdfs"/> (optional) gives
    /// the rdfs:subClassOf closure for sh:targetClass and sh:class.</summary>
    public static ShaclReport Validate(RdfGraph data, RdfGraph shapes, RdfGraph? rdfs = null)
    {
        ArgumentNullException.ThrowIfNull(data);
        ArgumentNullException.ThrowIfNull(shapes);
        return new Run(data, shapes, rdfs).Go();
    }

    private sealed class Run(RdfGraph data, RdfGraph shapes, RdfGraph? rdfs)
    {
        private readonly List<ShaclResult> _results = new();
        private readonly SortedDictionary<(string, string), ShaclNotRun> _notRun = new(Comparer<(string, string)>.Create((a, b) =>
        {
            int c = string.CompareOrdinal(a.Item1, b.Item1);
            return c != 0 ? c : string.CompareOrdinal(a.Item2, b.Item2);
        }));
        private readonly Dictionary<RdfTerm, SortedSet<RdfTerm>> _supers = new();
        private readonly HashSet<string> _customParams = new(StringComparer.Ordinal);
        private readonly HashSet<RdfTerm> _sparqlShapes = new();
        private readonly Dictionary<(string, string), Regex> _regex = new();
        private int _depth;
        private bool _incomplete;

        public ShaclReport Go()
        {
            var notes = new List<string>();
            if (rdfs is null) notes.Add("no RDFS was given: sh:targetClass and sh:class match the exact rdf:type only (no rdfs:subClassOf closure).");
            foreach (RdfTerm cc in shapes.Subjects(RdfGraph.Type, S("ConstraintComponent")))
                foreach (RdfTerm par in shapes.Objects(cc, S("parameter")))
                    foreach (RdfTerm p in shapes.Objects(par, PathP))
                        if (p.IsIri) _customParams.Add(p.Value);

            // Every shape: typed, targeted, or referenced as a shape. Those with targets are run, in term order.
            var all = new SortedSet<RdfTerm>();
            foreach (RdfTerm t in new[] { NodeShape, PropertyShape }) foreach (RdfTerm sh in shapes.Subjects(RdfGraph.Type, t)) all.Add(sh);
            foreach (string tp in new[] { "targetClass", "targetNode", "targetSubjectsOf", "targetObjectsOf", "target" })
                foreach (RdfTerm sh in shapes.SubjectsOf(S(tp))) all.Add(sh);
            foreach (RdfTerm sh in all) Survey(sh);
            int run = 0;
            var focusAll = new SortedSet<RdfTerm>();
            foreach (RdfTerm shape in all)
            {
                if (Deactivated(shape)) continue;
                SortedSet<RdfTerm> focus = Targets(shape);
                if (shapes.Objects(shape, S("target")).Count > 0)
                {
                    NotRunAdd(shape, "sh:target", "a SPARQL-based (SHACL-AF) target is not run; the shape's other targets are");
                    _incomplete = true;   // the shape's focus nodes are not all known
                }
                if (focus.Count == 0 && !HasTarget(shape)) continue;
                run++;
                foreach (RdfTerm f in focus) { focusAll.Add(f); if (!Check(shape, f, _results)) _incomplete = true; }
            }
            _results.Sort((a, b) => Cmp(a, b));
            int sparql = _sparqlShapes.Count;
            var dedup = new List<ShaclResult>();
            foreach (ShaclResult r in _results) if (dedup.Count == 0 || dedup[^1] != r) dedup.Add(r);
            ShaclOutcome outcome = dedup.Count > 0 ? ShaclOutcome.Fail : _incomplete ? ShaclOutcome.NotRun : ShaclOutcome.Pass;
            return new ShaclReport(outcome, dedup, _notRun.Values.ToList(), run, focusAll.Count, sparql, notes);
        }

        private static int Cmp(ShaclResult a, ShaclResult b)
        {
            int c = string.CompareOrdinal(a.Focus, b.Focus);
            if (c == 0) c = string.CompareOrdinal(a.Path, b.Path);
            if (c == 0) c = string.CompareOrdinal(a.Component, b.Component);
            if (c == 0) c = string.CompareOrdinal(a.Shape, b.Shape);
            if (c == 0) c = string.CompareOrdinal(a.Value, b.Value);
            if (c == 0) c = string.CompareOrdinal(a.Severity, b.Severity);
            if (c == 0) c = string.CompareOrdinal(a.Message, b.Message);
            return c;
        }

        private void NotRunAdd(RdfTerm shape, string what, string reason) => _notRun.TryAdd((shape.ToString(), what), new ShaclNotRun(shape.ToString(), what, reason));

        /// <summary>Record the constraints of <paramref name="shape"/> (and its nested shapes) that are not run.</summary>
        private void Survey(RdfTerm shape, int depth = 0)
        {
            if (depth > 32) return;
            foreach (RdfTriple t in shapes.About(shape))
            {
                if (!t.P.IsIri) continue;
                string p = t.P.Value;
                if (p == Sh + "sparql") { _sparqlShapes.Add(shape); NotRunAdd(shape, "sh:sparql " + t.O, "NOT CHECKED: a SHACL-SPARQL constraint is not evaluated (GridSim has no SPARQL engine); it is never counted as a pass"); continue; }
                if (_customParams.Contains(p)) { _sparqlShapes.Add(shape); NotRunAdd(shape, "<" + p + ">", "a constraint component defined in the shapes graph (SPARQL-based) is not run"); continue; }
                if (!p.StartsWith(Sh, StringComparison.Ordinal)) continue;
                string local = p[Sh.Length..];
                if (local == "target") _sparqlShapes.Add(shape);
                if (Supported.Contains(local) || Inert.Contains(local) || local == "target") { }
                else NotRunAdd(shape, "sh:" + local, "not in the SHACL Core subset this checker runs");
                if (local is "property" or "node" or "not") Survey(t.O, depth + 1);
                if (local is "and" or "or" or "xone" && shapes.ListOf(t.O) is IReadOnlyList<RdfTerm> members) foreach (RdfTerm m in members) Survey(m, depth + 1);
            }
            foreach (RdfTerm path in shapes.Objects(shape, PathP))
                if (!PathSupported(path)) NotRunAdd(shape, "sh:path " + path, "this path form is not in the subset (a predicate, sh:inversePath and a sequence are)");
        }

        private bool PathSupported(RdfTerm path)
        {
            if (path.IsIri) return true;
            if (shapes.Objects(path, S("inversePath")) is { Count: 1 } inv) return inv[0].IsIri;
            return shapes.ListOf(path) is IReadOnlyList<RdfTerm> seq && seq.Count > 0 && seq.All(PathSupported);
        }

        private bool Deactivated(RdfTerm shape) => shapes.Objects(shape, S("deactivated")).Any(o => o.IsLiteral && o.Value == "true");

        private bool HasTarget(RdfTerm shape) =>
            new[] { "targetClass", "targetNode", "targetSubjectsOf", "targetObjectsOf" }.Any(t => shapes.Objects(shape, S(t)).Count > 0);

        private SortedSet<RdfTerm> Targets(RdfTerm shape)
        {
            var f = new SortedSet<RdfTerm>();
            foreach (RdfTerm c in shapes.Objects(shape, S("targetClass"))) foreach (RdfTerm i in Instances(c)) f.Add(i);
            foreach (RdfTerm n in shapes.Objects(shape, S("targetNode"))) f.Add(n);
            foreach (RdfTerm p in shapes.Objects(shape, S("targetSubjectsOf"))) foreach (RdfTerm s in data.SubjectsOf(p)) f.Add(s);
            foreach (RdfTerm p in shapes.Objects(shape, S("targetObjectsOf"))) foreach (RdfTerm o in data.ObjectsOf(p)) f.Add(o);
            return f;
        }

        private SortedSet<RdfTerm> Supers(RdfTerm cls)
        {
            if (_supers.TryGetValue(cls, out var s)) return s;
            s = new SortedSet<RdfTerm> { cls };
            _supers[cls] = s;
            if (rdfs is null) return s;
            var queue = new Queue<RdfTerm>();
            queue.Enqueue(cls);
            while (queue.Count > 0)
                foreach (RdfTerm sup in rdfs.Objects(queue.Dequeue(), RdfGraph.SubClassOf))
                    if (s.Add(sup)) queue.Enqueue(sup);
            return s;
        }

        private IEnumerable<RdfTerm> Instances(RdfTerm cls)
        {
            var r = new SortedSet<RdfTerm>();
            foreach (RdfTerm t in data.ObjectsOf(RdfGraph.Type))
                if (Supers(t).Contains(cls)) foreach (RdfTerm s in data.Subjects(RdfGraph.Type, t)) r.Add(s);
            return r;
        }

        private bool IsInstance(RdfTerm v, RdfTerm cls) => !v.IsLiteral && data.Objects(v, RdfGraph.Type).Any(t => Supers(t).Contains(cls));

        private SortedSet<RdfTerm> Values(RdfTerm focus, RdfTerm path)
        {
            var r = new SortedSet<RdfTerm>();
            if (path.IsIri) { foreach (RdfTerm o in data.Objects(focus, path)) r.Add(o); return r; }
            IReadOnlyList<RdfTerm> inv = shapes.Objects(path, S("inversePath"));
            if (inv.Count == 1) { foreach (RdfTerm s in data.Subjects(inv[0], focus)) r.Add(s); return r; }
            IReadOnlyList<RdfTerm>? seq = shapes.ListOf(path);
            if (seq is null) return r;
            var current = new SortedSet<RdfTerm> { focus };
            foreach (RdfTerm step in seq)
            {
                var next = new SortedSet<RdfTerm>();
                foreach (RdfTerm c in current) next.UnionWith(Values(c, step));
                current = next;
            }
            return current;
        }

        private string PathText(RdfTerm path)
        {
            if (path.IsIri) return "<" + path.Value + ">";
            IReadOnlyList<RdfTerm> inv = shapes.Objects(path, S("inversePath"));
            if (inv.Count == 1) return "^" + PathText(inv[0]);
            return shapes.ListOf(path) is IReadOnlyList<RdfTerm> seq ? string.Join("/", seq.Select(PathText)) : path.ToString();
        }

        /// <summary>Validate <paramref name="shape"/> on <paramref name="focus"/>, adding the definite results to
        /// <paramref name="into"/>. Returns false when a part of the shape was not run (an unsupported path or component, or
        /// a nested shape not fully run), so the absence of a result is not a pass.</summary>
        private bool Check(RdfTerm shape, RdfTerm focus, List<ShaclResult> into)
        {
            if (Deactivated(shape)) return true;
            if (++_depth > 64) { _depth--; throw new InvalidDataException($"SHACL: the shapes nest more than 64 deep at {shape} (a cycle of sh:node or sh:property?)."); }
            try
            {
                IReadOnlyList<RdfTerm> paths = shapes.Objects(shape, PathP);
                bool property = paths.Count > 0;
                if (property && !PathSupported(paths[0])) return false;
                bool complete = true;
                RdfTerm? path = property ? paths[0] : null;
                SortedSet<RdfTerm> values = path is RdfTerm pt ? Values(focus, pt) : new SortedSet<RdfTerm> { focus };
                string pathText = path is RdfTerm p2 ? PathText(p2) : "";
                IReadOnlyList<RdfTerm> sevs = shapes.Objects(shape, S("severity"));
                string severity = sevs.Count > 0 && sevs[0].IsIri && sevs[0].Value.StartsWith(Sh, StringComparison.Ordinal) ? sevs[0].Value[Sh.Length..] : "Violation";
                string? message = FirstLiteral(shapes.Objects(shape, S("message")));
                void Add(string component, RdfTerm? value, string defaultMessage) =>
                    into.Add(new ShaclResult(severity, focus.ToString(), pathText, "sh:" + component, shape.ToString(), value?.ToString() ?? "", message ?? defaultMessage));

                void Undecided(string what, RdfTerm arg)
                {
                    complete = false;
                    NotRunAdd(shape, what + " " + arg, "a shape inside this constraint was not fully run, so the constraint is not decided");
                }

                foreach (RdfTriple t in shapes.About(shape).OrderBy(t => t.P).ThenBy(t => t.O))
                {
                    if (!t.P.IsIri) continue;
                    if (_customParams.Contains(t.P.Value)) { complete = false; continue; }
                    if (!t.P.Value.StartsWith(Sh, StringComparison.Ordinal)) continue;
                    string c = t.P.Value[Sh.Length..];
                    if (!Supported.Contains(c) && !Inert.Contains(c) && c != "target") { complete = false; continue; }
                    RdfTerm arg = t.O;
                    switch (c)
                    {
                        case "class":
                            foreach (RdfTerm v in values) if (!IsInstance(v, arg)) Add("ClassConstraintComponent", v, $"the value is not an instance of {arg}");
                            break;
                        case "datatype":
                            foreach (RdfTerm v in values) if (!DatatypeOk(v, arg.Value)) Add("DatatypeConstraintComponent", v, $"the value is not a literal of datatype {arg}");
                            break;
                        case "nodeKind":
                            foreach (RdfTerm v in values) if (!NodeKindOk(v, arg)) Add("NodeKindConstraintComponent", v, $"the value is not of node kind {arg}");
                            break;
                        case "minCount":
                            if (property && Int(arg) is long min && values.Count < min) Add("MinCountConstraintComponent", null, $"fewer than {min} value(s)");
                            break;
                        case "maxCount":
                            if (property && Int(arg) is long max && values.Count > max) Add("MaxCountConstraintComponent", null, $"more than {max} value(s)");
                            break;
                        case "minInclusive" or "maxInclusive" or "minExclusive" or "maxExclusive":
                            foreach (RdfTerm v in values) if (!RangeOk(v, arg, c)) Add(char.ToUpperInvariant(c[0]) + c[1..] + "ConstraintComponent", v, $"the value is not {c} {arg}");
                            break;
                        case "minLength" or "maxLength":
                            foreach (RdfTerm v in values)
                            {
                                long? n = Int(arg);
                                bool ok = !v.IsBlank && n is long lim && (c == "minLength" ? v.Value.Length >= lim : v.Value.Length <= lim);
                                if (!ok) Add(char.ToUpperInvariant(c[0]) + c[1..] + "ConstraintComponent", v, $"the value's length is not {c} {arg}");
                            }
                            break;
                        case "pattern":
                        {
                            string flags = FirstLiteral(shapes.Objects(shape, S("flags"))) ?? "";
                            Regex re = Pattern(arg.Value, flags, shape);
                            foreach (RdfTerm v in values)
                            {
                                bool ok;
                                try { ok = !v.IsBlank && re.IsMatch(v.Value); }
                                catch (RegexMatchTimeoutException) { throw new InvalidDataException($"SHACL: the sh:pattern of {shape} took more than 1 s on {v}."); }
                                if (!ok) Add("PatternConstraintComponent", v, $"the value does not match {arg.Value}");
                            }
                            break;
                        }
                        case "in":
                        {
                            IReadOnlyList<RdfTerm> allowed = shapes.ListOf(arg) ?? throw new InvalidDataException($"SHACL: the sh:in of {shape} is not a well-formed list.");
                            foreach (RdfTerm v in values) if (!allowed.Any(a => Same(a, v))) Add("InConstraintComponent", v, "the value is not one of the sh:in values");
                            break;
                        }
                        case "hasValue":
                            if (!values.Any(v => Same(arg, v))) Add("HasValueConstraintComponent", null, $"no value is {arg}");
                            break;
                        case "node":
                            foreach (RdfTerm v in values)
                                switch (Conforms(arg, v))
                                {
                                    case ShaclOutcome.Fail: Add("NodeConstraintComponent", v, $"the value does not conform to {arg}"); break;
                                    case ShaclOutcome.NotRun: Undecided("sh:node", arg); break;
                                }
                            break;
                        case "property":
                            foreach (RdfTerm v in values) if (!Check(arg, v, into)) complete = false;
                            break;
                        case "not":
                            foreach (RdfTerm v in values)
                                switch (Conforms(arg, v))
                                {
                                    case ShaclOutcome.Pass: Add("NotConstraintComponent", v, $"the value conforms to {arg}"); break;
                                    case ShaclOutcome.NotRun: Undecided("sh:not", arg); break;
                                }
                            break;
                        case "and" or "or" or "xone":
                        {
                            IReadOnlyList<RdfTerm> members = shapes.ListOf(arg) ?? throw new InvalidDataException($"SHACL: the sh:{c} of {shape} is not a well-formed list.");
                            foreach (RdfTerm v in values)
                            {
                                int pass = 0, fail = 0, notRun = 0;
                                foreach (RdfTerm m in members)
                                    switch (Conforms(m, v)) { case ShaclOutcome.Pass: pass++; break; case ShaclOutcome.Fail: fail++; break; default: notRun++; break; }
                                // Three-valued (Kleene) logic: a definite answer only when the members not run cannot change it.
                                bool? ok = c switch
                                {
                                    "and" => fail > 0 ? false : notRun > 0 ? null : true,
                                    "or" => pass > 0 ? true : notRun > 0 ? null : false,
                                    _ => pass > 1 ? false : notRun > 0 ? null : pass == 1,
                                };
                                if (ok is null) Undecided("sh:" + c, arg);
                                else if (ok == false) Add(char.ToUpperInvariant(c[0]) + c[1..] + "ConstraintComponent", v, $"the value does not satisfy sh:{c}");
                            }
                            break;
                        }
                        case "equals" or "disjoint":
                        {
                            var other = new SortedSet<RdfTerm>(data.Objects(focus, arg));
                            if (c == "equals")
                            {
                                foreach (RdfTerm v in values) if (!other.Contains(v)) Add("EqualsConstraintComponent", v, $"the value is not a value of {arg}");
                                foreach (RdfTerm o in other) if (!values.Contains(o)) Add("EqualsConstraintComponent", o, $"a value of {arg} is not a value of the path");
                            }
                            else foreach (RdfTerm v in values) if (other.Contains(v)) Add("DisjointConstraintComponent", v, $"the value is also a value of {arg}");
                            break;
                        }
                    }
                }
                return complete;
            }
            finally { _depth--; }
        }

        private static string? FirstLiteral(IReadOnlyList<RdfTerm> terms)
        {
            foreach (RdfTerm t in terms) if (t.IsLiteral) return t.Value;
            return null;
        }

        /// <summary>Does <paramref name="v"/> conform to <paramref name="shape"/>? Fail when a run constraint gives a result,
        /// NotRun when none does but a part of the shape was not run, Pass otherwise.</summary>
        private ShaclOutcome Conforms(RdfTerm shape, RdfTerm v)
        {
            var tmp = new List<ShaclResult>();
            bool complete = Check(shape, v, tmp);
            return tmp.Count > 0 ? ShaclOutcome.Fail : complete ? ShaclOutcome.Pass : ShaclOutcome.NotRun;
        }

        private Regex Pattern(string pattern, string flags, RdfTerm shape)
        {
            if (_regex.TryGetValue((pattern, flags), out Regex? re)) return re;
            RegexOptions o = RegexOptions.CultureInvariant;
            foreach (char f in flags)
                o |= f switch
                {
                    'i' => RegexOptions.IgnoreCase, 'm' => RegexOptions.Multiline, 's' => RegexOptions.Singleline, 'x' => RegexOptions.IgnorePatternWhitespace,
                    'q' => throw new InvalidDataException($"SHACL: the sh:flags 'q' of {shape} is not read."),
                    _ => throw new InvalidDataException($"SHACL: the sh:flags '{f}' of {shape} is not a regular-expression flag."),
                };
            try { re = new Regex(pattern, o, TimeSpan.FromSeconds(1)); }
            catch (ArgumentException ex) { throw new InvalidDataException($"SHACL: the sh:pattern of {shape} is not a .NET regular expression: {ex.Message}"); }
            _regex[(pattern, flags)] = re;
            return re;
        }

        private static long? Int(RdfTerm t) => t.IsLiteral && long.TryParse(t.Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out long v) ? v : null;

        private static bool Same(RdfTerm a, RdfTerm b)
        {
            if (a == b) return true;
            if (!a.IsLiteral || !b.IsLiteral || a.Value != b.Value) return false;
            if (a.Datatype is null || b.Datatype is null) return true;
            return false;
        }

        private static bool NodeKindOk(RdfTerm v, RdfTerm kind) => kind.Value[Sh.Length..] switch
        {
            "IRI" => v.IsIri,
            "BlankNode" => v.IsBlank,
            "Literal" => v.IsLiteral,
            "BlankNodeOrIRI" => !v.IsLiteral,
            "BlankNodeOrLiteral" => !v.IsIri,
            "IRIOrLiteral" => !v.IsBlank,
            _ => false,
        };

        private static double? Number(RdfTerm v) =>
            v.IsLiteral && double.TryParse(v.Value, NumberStyles.Float, CultureInfo.InvariantCulture, out double d) && !double.IsNaN(d) ? d : null;

        private static bool RangeOk(RdfTerm v, RdfTerm bound, string c)
        {
            double? x = Number(v), b = Number(bound);
            if (x is null || b is null) return false;
            return c switch
            {
                "minInclusive" => x >= b,
                "maxInclusive" => x <= b,
                "minExclusive" => x > b,
                _ => x < b,
            };
        }

        private static readonly Regex DecimalLex = new(@"^[+-]?(\d+(\.\d*)?|\.\d+)$", RegexOptions.CultureInvariant);
        private static readonly Regex Floating = new(@"^([+-]?(\d+(\.\d*)?|\.\d+)([eE][+-]?\d+)?|[+-]?INF|NaN)$", RegexOptions.CultureInvariant);
        private static readonly Regex Integer = new(@"^[+-]?\d+$", RegexOptions.CultureInvariant);
        private static readonly Regex DateTimeLex = new(@"^-?\d{4,}-\d{2}-\d{2}T\d{2}:\d{2}:\d{2}(\.\d+)?(Z|[+-]\d{2}:\d{2})?$", RegexOptions.CultureInvariant);
        private static readonly Regex DateLex = new(@"^-?\d{4,}-\d{2}-\d{2}(Z|[+-]\d{2}:\d{2})?$", RegexOptions.CultureInvariant);

        /// <summary>Is <paramref name="lex"/> a valid lexical form of <paramref name="dt"/>? Null for a datatype not checked lexically.</summary>
        private static bool? Lexical(string lex, string dt)
        {
            if (!dt.StartsWith(Xsd, StringComparison.Ordinal)) return null;
            switch (dt[Xsd.Length..])
            {
                case "string" or "normalizedString" or "token" or "anyURI": return true;
                case "boolean": return lex is "true" or "false" or "1" or "0";
                case "float" or "double": return Floating.IsMatch(lex);
                case "decimal": return DecimalLex.IsMatch(lex);
                case "integer" or "long" or "int" or "short" or "byte": return Integer.IsMatch(lex);
                case "nonNegativeInteger" or "unsignedInt" or "unsignedLong": return Integer.IsMatch(lex) && !lex.StartsWith('-');
                case "positiveInteger": return Integer.IsMatch(lex) && !lex.StartsWith('-') && lex.TrimStart('+').TrimStart('0').Length > 0;
                case "dateTime": return DateTimeLex.IsMatch(lex);
                case "date": return DateLex.IsMatch(lex);
                default: return null;
            }
        }

        private static bool DatatypeOk(RdfTerm v, string dt)
        {
            if (!v.IsLiteral) return false;
            if (v.Datatype is null) return Lexical(v.Value, dt) ?? false;
            if (v.Datatype != dt) return false;
            return Lexical(v.Value, dt) ?? true;
        }
    }
}
