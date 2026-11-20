using System.Globalization;
using GridSim.Core.IO;
using GridSim.Core.IO.Dgs;
using GridSim.Core.IO.Matpower;
using GridSim.Core.Model.Substations;

namespace GridSim.Cli;

/// <summary>
/// RC4 R4-11 part C: <c>dgs-import &lt;file.dgs&gt; --out &lt;case-dir&gt;</c>. Reads an ASCII DGS file with the dialect of
/// <see cref="DgsTableReader"/> (built from public descriptions only; acceptance on real PowerFactory files is AWAITING
/// FILES), imports it through Part A's node-breaker model and writes into the case folder: <c>substations.json</c> (the
/// native node-breaker file), <c>case.m</c> (the bus-branch case at the DGS switch states, ready for <c>gridsim pf</c>) and
/// <c>dgs-import-report.json</c> (every object and what became of it, every unsupported class by name, every UNCONFIRMED
/// mapping used, the identity map and the provenance tag FormatFromPublicDescription). This verb is NOT a result-file
/// verb: <c>--out</c> is the case folder, as for the other folder-writing verbs. Existing files are never overwritten.
/// Exit codes: 0 done; 1 the file is refused, an output exists or cannot be written, or nothing could be imported; 2 usage;
/// 3 with <c>--strict</c> when any object was refused or any class is unsupported (the files are still written).
/// </summary>
internal static class DgsImportCommand
{
    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;
    public const string SubstationsFile = "substations.json", CaseFile = "case.m", ReportFile = "dgs-import-report.json";

    private sealed class UsageException(string message) : Exception(message);

    public static int Run(List<string> a)
    {
        const string verb = "dgs-import";
        try
        {
            string? input = null, outDir = null;
            bool accept = false, strict = false;
            for (int i = 1; i < a.Count; i++)
            {
                string t = a[i];
                switch (t)
                {
                    case "--out":
                        if (i + 1 >= a.Count || a[i + 1].StartsWith("--", StringComparison.Ordinal)) throw new UsageException("--out expected a folder but none was given");
                        if (outDir is not null) throw new UsageException("--out was given twice");
                        outDir = a[++i];
                        break;
                    case "--accept-unconfirmed": accept = true; break;
                    case "--strict": strict = true; break;
                    default:
                        if (t.StartsWith("--", StringComparison.Ordinal)) throw new UsageException($"{verb} does not use {t}. {verb} accepts: --out, --accept-unconfirmed, --strict.");
                        if (input is not null) throw new UsageException($"{verb} takes one positional argument but got: {input} {t}.");
                        input = t;
                        break;
                }
            }
            if (input is null || outDir is null) throw new UsageException("usage: gridsim dgs-import <file.dgs> --out <case-dir> [--accept-unconfirmed] [--strict]");

            if (!File.Exists(input)) { Console.Error.WriteLine($"error: {verb}: file '{input}' does not exist."); return CliExit.InputError; }
            string full = Path.GetFullPath(outDir);
            string[] names = { SubstationsFile, CaseFile, ReportFile };
            foreach (string n in names)
                if (File.Exists(Path.Combine(full, n))) { Console.Error.WriteLine($"error: {verb}: '{Path.Combine(full, n)}' already exists; existing files are never overwritten. Name a new folder."); return CliExit.InputError; }

            DgsImportResult r;
            try
            {
                DgsFile file = DgsTableReader.Read(File.ReadAllText(input));
                r = DgsImporter.Import(file, new DgsImportOptions(accept));
            }
            catch (DgsFormatException ex)
            {
                foreach (string p in ex.Problems) Console.Error.WriteLine($"error: {verb}: {p}");
                return CliExit.InputError;
            }

            SubstationProjectionResult? proj = null;
            string? projectionError = null;
            if (r.Model.Substations.Count > 0)
            {
                try { proj = r.Project(); }
                catch (SubstationTopologyException ex) { projectionError = ex.Message; }
            }

            Directory.CreateDirectory(full);
            string report = DgsImportReport.ToJson(r, proj);
            string rp = NeverOverwrite.WriteAllText(Path.Combine(full, ReportFile), report);
            var written = new List<string> { rp };

            Console.WriteLine($"dgs-import - {Path.GetFileName(input)}: DGS {r.DgsVersion}, provenance {r.Provenance} (format from public description; no real PowerFactory file tested).");
            foreach (var grp in r.Objects.GroupBy(o => o.Class).OrderBy(g => g.Key, StringComparer.Ordinal))
                Console.WriteLine($"  {grp.Key,-10} {grp.Count(),4} object(s): " + string.Join(", ",
                    Enum.GetValues<DgsObjectOutcome>().Select(o => (o, n: grp.Count(x => x.Outcome == o))).Where(x => x.n > 0).Select(x => $"{x.n} {x.o.ToString().ToLowerInvariant()}")));
            foreach (DgsUnsupportedClass u in r.Unsupported)
                Console.WriteLine($"  UNSUPPORTED class {u.Class}: {u.Rows} row(s) from line {u.FirstLine}, {(u.KnownAndRefused ? "known and not implemented" : "not interpreted")}; its objects are NOT in the model.");
            foreach (DgsObjectRecord o in r.Objects.Where(o => o.Outcome == DgsObjectOutcome.Refused))
                Console.WriteLine($"  REFUSED {o.Class} '{o.Id}' (line {o.Line}): {o.Note}");
            foreach (var kv in r.UnreadColumns)
                Console.WriteLine($"  columns of {kv.Key} not used: {string.Join(", ", kv.Value)}");
            if (r.UnconfirmedUsed.Count > 0)
                Console.WriteLine($"  UNCONFIRMED mappings used: {string.Join(", ", r.UnconfirmedUsed)}");
            foreach (string w in r.Warnings) Console.WriteLine($"  warning: {w}");

            if (projectionError is not null) { Console.Error.WriteLine($"error: {verb}: {projectionError}"); Console.WriteLine($"  report written to {rp}"); return CliExit.InputError; }
            if (proj is null || proj.Buses.Count == 0)
            {
                Console.Error.WriteLine($"error: {verb}: nothing could be imported into a bus-branch case (see the report {rp}).");
                return CliExit.InputError;
            }
            written.Add(NeverOverwrite.WriteAllText(Path.Combine(full, SubstationsFile), SubstationJson.Write(r.Model)));
            written.Add(NeverOverwrite.WriteAllText(Path.Combine(full, CaseFile), MatpowerWriter.Write(proj.Model).Text));
            Console.WriteLine($"  {proj.Buses.Count} bus(es), {proj.Model.Branches.Count} branch(es), demand {proj.DemandMwServed.ToString("0.####", Inv)} MW served; wrote {string.Join(", ", written.Select(Path.GetFileName))} in {full}");
            foreach (string w in proj.Warnings) Console.WriteLine($"  warning: {w}");
            if (!r.Complete)
            {
                Console.WriteLine("  the import is INCOMPLETE: objects were refused or classes are unsupported (listed above and in the report).");
                if (strict) return CliExit.Verdict;
            }
            return CliExit.Ok;
        }
        catch (UsageException ex) { Console.Error.WriteLine($"error: {verb}: {ex.Message}"); return CliExit.Usage; }
        catch (IOException ex) { Console.Error.WriteLine($"error: {verb}: cannot write: {ex.Message}"); return CliExit.InputError; }
    }
}
