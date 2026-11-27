using GridSim.Core.IO;
using GridSim.Core.IO.Cim.Profiles;
using GridSim.Core.IO.Cim.Shacl;
using GridSim.Core.IO.Cim.Zip;

namespace GridSim.Cli;

/// <summary>
/// RC5 WP10: <c>cgmes-shacl &lt;bundle.zip|folder&gt; --shapes &lt;folder|file&gt; [--shapes ...] [--rdfs &lt;folder|file&gt;]
/// [--profile-filter eq,tp,ssh,sv,dl,gl,sc,dy] [--report &lt;file.csv|file.json&gt;]</c>: the native SHACL Core subset
/// (<see cref="ShaclValidator"/>, no third-party dependency) run on a CGMES bundle with the shapes the user supplies (for
/// example the ENTSO-E CGMES-AP files; none are bundled). SHACL-SPARQL constraints and targets are reported "not run" by
/// name. Shapes files: .ttl (Turtle) or .rdf / .xml (RDF/XML); RDFS files likewise. Every file list is read in ordinal
/// path order, so the report does not depend on the order the files are named in. Exit codes, as <c>cgmes-conform</c>'s: 0
/// conforms (every constraint that applies was run and none gave a result); 1 the shapes, the RDFS or the bundle cannot be
/// read (or the report cannot be written); 2 usage; 3 no pass: a result (the bundle does not conform), or no result but a
/// constraint that applies was not run (the verdict is "not determined"). The shared --json/--csv writer (RC5 WP11) writes the summary, every result and every
/// constraint not run.
/// </summary>
internal static class CgmesShaclCommand
{
    private const int Unreadable = CliExit.InputError;

    public static int Run(List<string> a)
    {
        const string verb = "cgmes-shacl";
        // WP24: cgmes-shacl --census <folder|file> lists the SHACL features the shapes use and how the checker handles them.
        if (a.Count > 1 && a[1] == "--census") return Census(a);
        string? bundle = null, rdfs = null, filter = null, report = null;
        var shapes = new List<string>();
        for (int i = 1; i < a.Count; i++)
        {
            string t = a[i];
            if (!t.StartsWith("--", StringComparison.Ordinal))
            {
                if (bundle is not null) return Usage($"takes one bundle but got: {bundle} {t}.");
                bundle = t;
                continue;
            }
            if (t is not ("--shapes" or "--rdfs" or "--profile-filter" or "--report"))
                return Usage($"{verb} does not use {t}. {verb} accepts: --shapes, --rdfs, --profile-filter, --report.");
            if (i + 1 >= a.Count || a[i + 1].StartsWith("--", StringComparison.Ordinal)) return Usage($"{t} expected a value but none was given");
            string v = a[++i];
            switch (t)
            {
                case "--shapes": shapes.Add(v); break;
                case "--rdfs": if (rdfs is not null) return Usage("--rdfs was given twice"); rdfs = v; break;
                case "--profile-filter": if (filter is not null) return Usage("--profile-filter was given twice"); filter = v; break;
                default: if (report is not null) return Usage("--report was given twice"); report = v; break;
            }
        }
        if (bundle is null || shapes.Count == 0)
            return Usage("usage: gridsim cgmes-shacl <bundle.zip|folder> --shapes <folder|file> [--shapes ...] [--rdfs <folder|file>] [--profile-filter eq,tp,ssh,...] [--report <file.csv|file.json>]");
        if (report is not null && !(report.EndsWith(".csv", StringComparison.OrdinalIgnoreCase) || report.EndsWith(".json", StringComparison.OrdinalIgnoreCase)))
            return Usage("--report expected a .csv or .json file name");
        HashSet<CgmesProfileKind>? kinds = null;
        if (filter is not null)
        {
            kinds = new HashSet<CgmesProfileKind>();
            foreach (string p in filter.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                if (!Enum.TryParse(p, ignoreCase: true, out CgmesProfileKind k) || k == CgmesProfileKind.Unknown || !Enum.IsDefined(k) || int.TryParse(p, out _))
                    return Usage($"--profile-filter expected a comma list of eq, tp, ssh, sv, dl, gl, sc, dy, eqbd, tpbd but got '{p}'");
                kinds.Add(k);
            }
        }

        var shapesGraph = new RdfGraph();
        RdfGraph? rdfsGraph = null;
        try
        {
            foreach ((string path, string text) in Files(shapes)) ReadInto(shapesGraph, path, text);
            if (rdfs is not null)
            {
                rdfsGraph = new RdfGraph();
                foreach ((string path, string text) in Files(new[] { rdfs })) ReadInto(rdfsGraph, path, text);
            }
        }
        catch (Exception ex) when (ex is InvalidDataException or IOException or UnauthorizedAccessException)
        {
            Console.Error.WriteLine($"error: {verb}: the shapes or the RDFS cannot be read: {ex.Message}");
            return Unreadable;
        }

        var data = new RdfGraph();
        CgmesArchive archive;
        int used = 0;
        try
        {
            if (Directory.Exists(bundle)) archive = CgmesArchiveReader.ReadDirectory(bundle);
            else if (File.Exists(bundle)) archive = CgmesArchiveReader.ReadFile(bundle);
            else { Console.Error.WriteLine($"error: {verb}: '{bundle}' does not exist."); return Unreadable; }
            foreach (CgmesArchiveEntry e in archive.Entries.OrderBy(e => e.Path, StringComparer.Ordinal))
            {
                if (kinds is not null && !kinds.Contains(e.Kind)) continue;
                RdfXmlReader.Read(data, e.Text, e.Path);
                used++;
            }
        }
        catch (Exception ex) when (ex is InvalidDataException or IOException or UnauthorizedAccessException)
        {
            Console.Error.WriteLine($"error: {verb}: {ex.Message}");
            return Unreadable;
        }

        ShaclReport r;
        try { r = ShaclValidator.Validate(data, shapesGraph, rdfsGraph); }
        catch (InvalidDataException ex)
        {
            Console.Error.WriteLine($"error: {verb}: {ex.Message}");
            return Unreadable;
        }
        Console.WriteLine($"cgmes-shacl - {used} document(s), {data.Count} triple(s), {shapesGraph.Count} shape triple(s): {r.Summary}.");
        foreach (ShaclResult x in r.Results.Take(50))
            Console.WriteLine($"  {x.Severity,-9} {x.Component} {x.Focus} {x.Path} {x.Value}  [{x.Shape}] {x.Message}");
        if (r.Results.Count > 50) Console.WriteLine($"  ... and {r.Results.Count - 50} more (see --report)");
        foreach (ShaclNotRun n in r.NotRun.Take(50)) Console.WriteLine($"  NOT RUN {n.What} [{n.Shape}]: {n.Reason}");
        if (r.NotRun.Count > 50) Console.WriteLine($"  ... and {r.NotRun.Count - 50} more not run (see --report)");
        foreach (string n in r.Notes) Console.WriteLine("  note: " + n);
        if (report is not null)
        {
            try
            {
                string written = NeverOverwrite.WriteAllText(report, report.EndsWith(".json", StringComparison.OrdinalIgnoreCase) ? r.ToJson() : r.ToCsv());
                Console.WriteLine("  report: " + written);
            }
            catch (IOException ex) { Console.Error.WriteLine($"error: {verb}: cannot write the report: {ex.Message}"); return CliExit.InputError; }
        }
        CliResult? rep = CliOutput.Report;
        if (rep is not null)
        {
            rep.Set("conforms", r.Conforms).Set("verdict", r.Verdict).Set("summary", r.Summary).Set("documents", used).Set("triples", data.Count).Set("shape_triples", shapesGraph.Count)
               .Set("results", r.Results.Count).Set("not_run", r.NotRun.Count);
            CliResultTable rt = rep.Table("results", "severity", "component", "focus", "path", "value", "shape", "message");
            foreach (ShaclResult x in r.Results) rt.Row(x.Severity, x.Component, x.Focus, x.Path, x.Value, x.Shape, x.Message);
            CliResultTable nt = rep.Table("not_run", "what", "shape", "reason");
            foreach (ShaclNotRun n in r.NotRun) nt.Row(n.What, n.Shape, n.Reason);
            CliResultTable notes = rep.Table("notes", "note");
            foreach (string n in r.Notes) notes.Row(n);
            rep.Complete();
        }
        return r.Conforms ? CliExit.Ok : CliExit.Verdict;
    }

    /// <summary>
    /// WP24: <c>cgmes-shacl --census &lt;folder|file&gt; [&lt;folder|file&gt; ...]</c>: read the shapes (as --shapes reads them) and
    /// list every SHACL feature they use, with its count and whether the native checker runs it, plus the number of
    /// SHACL-SPARQL constraints. Exit 0 when read; 1 unreadable; 2 usage. Writes through the shared --json/--csv writer.
    /// </summary>
    private static int Census(List<string> a)
    {
        const string verb = "cgmes-shacl";
        var paths = new List<string>();
        for (int i = 2; i < a.Count; i++)
        {
            if (a[i].StartsWith("--", StringComparison.Ordinal)) return Usage($"--census takes shapes files or folders only, not {a[i]}.");
            paths.Add(a[i]);
        }
        if (paths.Count == 0) return Usage("usage: gridsim cgmes-shacl --census <folder|file> [<folder|file> ...]");
        var shapesGraph = new RdfGraph();
        try
        {
            foreach ((string path, string text) in Files(paths)) ReadInto(shapesGraph, path, text);
        }
        catch (Exception ex) when (ex is InvalidDataException or IOException or UnauthorizedAccessException)
        {
            Console.Error.WriteLine($"error: {verb}: the shapes cannot be read: {ex.Message}");
            return Unreadable;
        }
        ShaclCensus c = ShaclValidator.Census(shapesGraph);
        Console.WriteLine($"cgmes-shacl --census - {c.Triples} shape triple(s): {c.Shapes} shape(s) ({c.NodeShapes} node, {c.PropertyShapes} property), "
            + $"{c.SparqlConstraints} sh:sparql constraint(s), {c.SparqlTargets} SPARQL target(s), {c.PredicatePaths} predicate path(s), {c.ComplexPaths} path expression(s); "
            + $"{c.NotRunUses} use(s) of features the checker does not run.");
        foreach (ShaclCensusRow r in c.Rows) Console.WriteLine($"  {r.Feature,-28} {r.Count,7}  {r.Handling}");
        CliResult? rep = CliOutput.Report;
        if (rep is not null)
        {
            rep.Set("shape_triples", c.Triples).Set("shapes", c.Shapes).Set("node_shapes", c.NodeShapes).Set("property_shapes", c.PropertyShapes)
               .Set("sparql_constraints", c.SparqlConstraints).Set("sparql_targets", c.SparqlTargets).Set("not_run_uses", c.NotRunUses);
            CliResultTable ft = rep.Table("features", "feature", "count", "handling");
            foreach (ShaclCensusRow r in c.Rows) ft.Row(r.Feature, r.Count, r.Handling);
            rep.Complete();
        }
        return CliExit.Ok;
    }

    private static int Usage(string message)
    {
        Console.Error.WriteLine("error: cgmes-shacl: " + message);
        return CliExit.Usage;
    }

    /// <summary>The files a list of file or folder arguments names (.ttl, .rdf, .xml), as (path, text), in ordinal path order.</summary>
    private static List<(string Path, string Text)> Files(IEnumerable<string> args)
    {
        var paths = new SortedSet<string>(StringComparer.Ordinal);
        foreach (string arg in args)
        {
            if (Directory.Exists(arg))
            {
                foreach (string f in Directory.GetFiles(arg, "*", SearchOption.AllDirectories))
                    if (f.EndsWith(".ttl", StringComparison.OrdinalIgnoreCase) || f.EndsWith(".rdf", StringComparison.OrdinalIgnoreCase) || f.EndsWith(".xml", StringComparison.OrdinalIgnoreCase))
                        paths.Add(Path.GetFullPath(f).Replace('\\', '/'));
            }
            else if (File.Exists(arg)) paths.Add(Path.GetFullPath(arg).Replace('\\', '/'));
            else throw new IOException($"'{arg}' does not exist.");
        }
        return paths.Select(p => (p, File.ReadAllText(p))).ToList();
    }

    private static void ReadInto(RdfGraph g, string path, string text)
    {
        string name = Path.GetFileName(path);
        if (path.EndsWith(".ttl", StringComparison.OrdinalIgnoreCase)) TurtleReader.Read(g, text, name);
        else RdfXmlReader.Read(g, text, name);
    }
}
