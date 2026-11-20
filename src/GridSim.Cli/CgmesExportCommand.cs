using System.Globalization;
using GridSim.Core.IO;
using GridSim.Core.IO.Cim.Profiles;
using GridSim.Core.IO.Cim.Zip;
using GridSim.Core.Model.Substations;
using GridSim.Core.PowerFlow;

namespace GridSim.Cli;

/// <summary>
/// RC4 R4-11 part B: <c>cgmes-export &lt;substations.json&gt; --dir &lt;folder&gt;</c> writes a node-breaker file
/// (<c>gridsim-substations/1</c>) as separate CGMES EQ, TP, SSH (and, with <c>--sv solve</c>, SV) documents through
/// <see cref="CgmesSubstationExporter"/>, and lists every omitted class (the structured omissions record). Existing files are never
/// overwritten. Exit codes: 0 done; 1 the input cannot be read, is refused, or a file cannot be written; 2 usage.
/// <c>cgmes-conform &lt;bundle.zip|folder&gt;</c> runs <see cref="CgmesConformance"/>: exit 0 when no check fails, 1 when one does.
/// </summary>
internal static class CgmesExportCommand
{
    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;
    private static readonly string[] Allowed = { "--dir", "--cgmes-version", "--open", "--close", "--unspecified", "--sv" };

    private sealed class UsageException(string message) : Exception(message);

    public static int Run(List<string> a)
    {
        const string verb = "cgmes-export";
        try
        {
            string? input = null;
            var flags = new Dictionary<string, string>(StringComparer.Ordinal);
            for (int i = 1; i < a.Count; i++)
            {
                string t = a[i];
                if (!t.StartsWith("--", StringComparison.Ordinal))
                {
                    if (input is not null) throw new UsageException($"{verb} takes one positional argument but got: {input} {t}.");
                    input = t;
                    continue;
                }
                if (Array.IndexOf(Allowed, t) < 0) throw new UsageException($"{verb} does not use {t}. {verb} accepts: {string.Join(", ", Allowed)} (and --json/--csv/--out).");
                if (i + 1 >= a.Count || a[i + 1].StartsWith("--", StringComparison.Ordinal)) throw new UsageException($"{t} expected a value but none was given");
                if (!flags.TryAdd(t, a[++i])) throw new UsageException($"{t} was given twice");
            }
            if (input is null || !flags.TryGetValue("--dir", out string? dir))
                throw new UsageException("usage: gridsim cgmes-export <substations.json> --dir <folder> [--cgmes-version 2.4.15|3.0] [--open a,b] [--close c] [--unspecified refuse|normal] [--sv none|solve]");
            CgmesVersion version = flags.GetValueOrDefault("--cgmes-version", "2.4.15") switch
            {
                "2.4.15" => CgmesVersion.V2_4_15,
                "3.0" => CgmesVersion.V3_0,
                var v => throw new UsageException($"--cgmes-version expected 2.4.15 or 3.0 but got '{v}'"),
            };
            UnspecifiedStatePolicy policy = flags.GetValueOrDefault("--unspecified", "normal") switch
            {
                "refuse" => UnspecifiedStatePolicy.Refuse,
                "normal" => UnspecifiedStatePolicy.UseNormalState,
                var v => throw new UsageException($"--unspecified expected refuse or normal but got '{v}'"),
            };
            bool solve = flags.GetValueOrDefault("--sv", "none") switch
            {
                "none" => false,
                "solve" => true,
                var v => throw new UsageException($"--sv expected none or solve but got '{v}'"),
            };
            string[] open = Split(flags.GetValueOrDefault("--open")), close = Split(flags.GetValueOrDefault("--close"));
            string? both = open.Intersect(close, StringComparer.Ordinal).FirstOrDefault();
            if (both is not null) throw new UsageException($"switch '{both}' is named in both --open and --close");

            if (!File.Exists(input)) { Console.Error.WriteLine($"error: {verb}: case file '{input}' does not exist."); return CliExit.InputError; }
            SubstationModel model;
            try { model = SubstationJson.Read(File.ReadAllText(input)); }
            catch (InvalidDataException ex) { Console.Error.WriteLine($"error: {verb}: {ex.Message}"); return CliExit.InputError; }

            var states = new Dictionary<string, bool>(StringComparer.Ordinal);
            foreach (string s in open) states[s] = true;
            foreach (string s in close) states[s] = false;
            IReadOnlyDictionary<int, (double Vm, double VaDeg)>? volts = null;
            string solveNote = "";
            try
            {
                if (solve)
                {
                    SubstationProjectionResult p = SubstationProjection.Project(model, states, new SubstationProjectionOptions(policy));
                    PowerFlowResult r = SolverBackend.Select(null).NewtonSolver(p.Model).Solve(p.Model);   // shared engine rule (CliEngineChoiceScan)
                    if (!r.Converged) { Console.Error.WriteLine($"error: {verb}: --sv solve: the projected case did not converge, so there is no state to write."); return CliExit.InputError; }
                    var d = new Dictionary<int, (double, double)>();
                    for (int i = 0; i < r.BusIds.Count; i++) d[r.BusIds[i]] = (r.Vm[i], r.VaDeg[i]);
                    volts = d;
                    solveNote = $"Newton-Raphson, {r.Iterations} iteration(s)";
                }
                CgmesExportResult e = CgmesSubstationExporter.Export(model, states, new CgmesExportOptions(version, policy, volts));

                string full = Path.GetFullPath(dir);
                var written = new List<string>();
                try
                {
                    foreach (CgmesProfileFile f in e.Files) written.Add(NeverOverwrite.WriteAllText(Path.Combine(full, f.FileName), f.Text));
                }
                catch (IOException ex) { Console.Error.WriteLine($"error: {verb}: cannot write: {ex.Message}"); return CliExit.InputError; }

                Console.WriteLine($"cgmes-export - {model.Name}: CGMES {(version == CgmesVersion.V3_0 ? "3.0" : "2.4.15")}, {e.Files.Count} document(s) written to {full}" + (solve ? $" (SV from {solveNote})" : "") + ".");
                foreach (string w in written) Console.WriteLine("  " + w);
                foreach (CgmesOmission o in e.Omissions) Console.WriteLine($"  omitted [{o.Profile}] {o.Item}: {o.Reason}");
                foreach (string n in e.Notes) Console.WriteLine("  note: " + n);
                CliResult? rep = CliOutput.Report;
                if (rep is not null)
                {
                    rep.Set("case", model.Name).Set("cgmes_version", version == CgmesVersion.V3_0 ? "3.0" : "2.4.15")
                       .Set("documents", e.Files.Count).Set("omissions", e.Omissions.Count).Set("sv", solve ? "solve" : "none");
                    CliResultTable ft = rep.Table("files", "profile", "path", "bytes");
                    for (int i = 0; i < e.Files.Count; i++) ft.Row(e.Files[i].Kind.ToString(), written[i], System.Text.Encoding.UTF8.GetByteCount(e.Files[i].Text));
                    CliResultTable ot = rep.Table("omissions", "profile", "item", "reason");
                    foreach (CgmesOmission o in e.Omissions) ot.Row(o.Profile.ToString(), o.Item, o.Reason);
                    rep.Complete();
                }
                return CliExit.Ok;
            }
            catch (SubstationTopologyException ex) { Console.Error.WriteLine($"error: {verb}: {ex.Message}"); return CliExit.InputError; }
        }
        catch (UsageException ex) { Console.Error.WriteLine($"error: {verb}: {ex.Message}"); return CliExit.Usage; }
    }

    public static int RunConform(List<string> a)
    {
        const string verb = "cgmes-conform";
        string? bundle = null;
        for (int i = 1; i < a.Count; i++)
        {
            if (a[i].StartsWith("--", StringComparison.Ordinal)) { Console.Error.WriteLine($"error: {verb}: {verb} does not use {a[i]} (it accepts --json/--csv/--out)."); return CliExit.Usage; }
            if (bundle is not null) { Console.Error.WriteLine($"error: {verb}: takes one positional argument but got: {bundle} {a[i]}."); return CliExit.Usage; }
            bundle = a[i];
        }
        if (bundle is null) { Console.Error.WriteLine("usage: gridsim cgmes-conform <bundle.zip|folder>"); return CliExit.Usage; }
        CgmesArchive archive;
        try
        {
            if (Directory.Exists(bundle)) archive = CgmesArchiveReader.ReadDirectory(bundle);
            else if (File.Exists(bundle)) archive = CgmesArchiveReader.ReadFile(bundle);
            else { Console.Error.WriteLine($"error: {verb}: '{bundle}' does not exist."); return CliExit.InputError; }
        }
        catch (Exception ex) when (ex is InvalidDataException or IOException)
        {
            Console.Error.WriteLine($"error: {verb}: {ex.Message}");
            return CliExit.InputError;
        }
        CgmesConformanceResult r = CgmesConformance.Run(archive);
        Console.WriteLine($"cgmes-conform - {r.Edition}: {r.Checks.Count(c => c.Status == CgmesCheckStatus.Pass)} passed, {r.Failures} failed, {r.Checks.Count(c => c.Status == CgmesCheckStatus.NotRun)} not run.");
        foreach (CgmesCheck c in r.Checks)
        {
            Console.WriteLine($"  {c.Id} {c.Status.ToString().ToUpperInvariant(),-7} {c.Name}  [{c.Basis}]");
            foreach (string f in c.Findings.Take(20)) Console.WriteLine("      - " + f);
            if (c.Findings.Count > 20) Console.WriteLine($"      ... and {c.Findings.Count - 20} more");
        }
        foreach (string n in r.Notes) Console.WriteLine("  note: " + n);
        CliResult? rep = CliOutput.Report;
        if (rep is not null)
        {
            rep.Set("edition", r.Edition).Set("documents", r.Documents.Count).Set("failures", r.Failures).Set("passed", r.Passed);
            CliResultTable ct = rep.Table("checks", "id", "name", "basis", "status", "findings");
            foreach (CgmesCheck c in r.Checks) ct.Row(c.Id, c.Name, c.Basis, c.Status.ToString().ToLowerInvariant(), string.Join(" | ", c.Findings));
            rep.Complete();
        }
        return r.Passed ? CliExit.Ok : CliExit.Verdict;
    }

    private static string[] Split(string? list) =>
        string.IsNullOrEmpty(list) ? Array.Empty<string>() : list.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
}
