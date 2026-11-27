using System.Globalization;
using GridSim.Core.IO;
using GridSim.Core.IO.Cim;
using GridSim.Core.IO.Cim.Profiles;
using GridSim.Core.IO.Psse;
using GridSim.Core.Model;
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
    private static readonly string[] Allowed = { "--dir", "--cgmes-version", "--open", "--close", "--unspecified", "--sv", "--profiles", "--geo", "--dyr", "--model" };

    private sealed class UsageException(string message) : Exception(message);

    public static int Run(List<string> a, Func<string, GridModel> load)
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
            // WP24: --model bus-branch --cgmes-version 3.0 writes a case file as CGMES 3.0 EQ, TP, SSH and SV documents
            // (CgmesBusBranch30Writer), with any --profiles in 3.0 too. Without --model the routes below are unchanged.
            if (flags.TryGetValue("--model", out string? form))
            {
                if (form != "bus-branch") throw new UsageException($"--model expected bus-branch but got '{form}'");
                if (flags.GetValueOrDefault("--cgmes-version", "2.4.15") != "3.0")
                    throw new UsageException("--model bus-branch writes the CGMES 3.0 per-profile documents, so it needs --cgmes-version 3.0 (for CGMES 2.4.15 use --profiles, or convert to a .xml file).");
                return RunProfiles(input, dir, flags, load, v3: true);
            }
            // RC5 WP10: --profiles takes the bus-branch route (a case file, CgmesWriter plus the DL, GL, SC and DY documents).
            if (flags.ContainsKey("--profiles")) return RunProfiles(input, dir, flags, load, v3: false);
            foreach (string only in new[] { "--geo", "--dyr" })
                if (flags.ContainsKey(only)) throw new UsageException($"{only} is read only with --profiles (the bus-branch export with the DL, GL, SC and DY profiles).");
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

    /// <summary>
    /// RC5 WP10: <c>cgmes-export &lt;case&gt; --dir &lt;folder&gt; --profiles dl,gl,sc,dy [--geo &lt;gridsim-geo.json&gt;] [--dyr &lt;file.dyr&gt;]</c>.
    /// The case (any case file the CLI reads) is written by CgmesWriter as <c>&lt;name&gt;_EQ.xml</c> (the same bytes as
    /// <c>convert</c> writes to a .xml file) and each requested profile as its own document (CgmesExtraProfiles). CGMES
    /// 2.4.15 only.
    /// </summary>
    private static int RunProfiles(string input, string dir, Dictionary<string, string> flags, Func<string, GridModel> load, bool v3)
    {
        const string verb = "cgmes-export";
        foreach (string nb in new[] { "--open", "--close", "--unspecified", "--sv" })
            if (flags.ContainsKey(nb)) throw new UsageException($"{nb} is a node-breaker option; --profiles writes a bus-branch case.");
        if (!v3 && flags.GetValueOrDefault("--cgmes-version", "2.4.15") != "2.4.15")
            throw new UsageException("--profiles writes CGMES 2.4.15 only: the bus-branch document the profiles refer to is a CGMES 2.4.15 document.");
        var kinds = new List<CgmesProfileKind>();
        foreach (string p in Split(flags.GetValueOrDefault("--profiles")))
        {
            CgmesProfileKind k = p.ToLowerInvariant() switch
            {
                "dl" => CgmesProfileKind.DL,
                "gl" => CgmesProfileKind.GL,
                "sc" => CgmesProfileKind.SC,
                "dy" => CgmesProfileKind.DY,
                _ => throw new UsageException($"--profiles expected a comma list of dl, gl, sc and dy but got '{p}'"),
            };
            if (kinds.Contains(k)) throw new UsageException($"--profiles names {p} twice");
            kinds.Add(k);
        }
        if (kinds.Count == 0 && (!v3 || flags.ContainsKey("--profiles"))) throw new UsageException("--profiles expected a comma list of dl, gl, sc and dy");
        if (flags.ContainsKey("--geo") && !kinds.Contains(CgmesProfileKind.GL)) throw new UsageException("--geo is read only with --profiles gl.");
        if (flags.ContainsKey("--dyr") && !kinds.Contains(CgmesProfileKind.DY)) throw new UsageException("--dyr is read only with --profiles dy.");
        if (kinds.Contains(CgmesProfileKind.GL) && !flags.ContainsKey("--geo")) throw new UsageException("--profiles gl needs --geo <gridsim-geo.json>.");
        if (kinds.Contains(CgmesProfileKind.DY) && !flags.ContainsKey("--dyr")) throw new UsageException("--profiles dy needs --dyr <file.dyr>.");

        GridModel model;
        try { model = load(input); }
        catch (Exception ex) when (ex is not UsageException)
        {
            Console.Error.WriteLine($"error: {verb}: {ex.Message}");
            return CliExit.CaseLoadFailure(ex);
        }
        try
        {
            GeoSidecar? geo = null;
            if (flags.TryGetValue("--geo", out string? geoPath))
            {
                if (!File.Exists(geoPath)) { Console.Error.WriteLine($"error: {verb}: geographical sidecar '{geoPath}' does not exist."); return CliExit.InputError; }
                geo = GeoSidecar.Read(File.ReadAllText(geoPath));
            }
            IReadOnlyList<PsseDyrRecord>? dyr = null;
            IReadOnlyList<PsseMachineKey>? keys = null;
            IReadOnlyList<double>? ras = null;
            if (flags.TryGetValue("--dyr", out string? dyrPath))
            {
                if (!File.Exists(dyrPath)) { Console.Error.WriteLine($"error: {verb}: DYR file '{dyrPath}' does not exist."); return CliExit.InputError; }
                PsseParseResult? raw = Gated.LastPsse;
                if (raw is null) { Console.Error.WriteLine($"error: {verb}: --dyr names machines by PSS/E bus and ID, so the case must be a PSS/E .raw file."); return CliExit.InputError; }
                keys = CgmesDynamicsProfile.KeysOf(raw);
                ras = CgmesDynamicsProfile.ArmatureResistancesOf(raw);
                if (keys.Count != model.Generators.Count) { Console.Error.WriteLine($"error: {verb}: the case's in-service machines are not the .raw file's."); return CliExit.InputError; }
                dyr = PsseDyrReader.ReadRecords(File.ReadAllText(dyrPath));
            }
            if (v3) return WriteBusBranch30(model, dir, kinds, new CgmesExtraOptions(kinds, CgmesVersion.V3_0, geo, dyr, keys, MachineArmatureResistances: ras));
            CgmesWriteResult w = CgmesWriter.Write(model);
            CgmesExtraResult extra = CgmesExtraProfiles.Write(model, w, new CgmesExtraOptions(kinds, CgmesVersion.V2_4_15, geo, dyr, keys, MachineArmatureResistances: ras));
            string name = model.Name ?? "case";
            string full = Path.GetFullPath(dir);
            var files = new List<(string Kind, string Path, int Bytes)>();
            try
            {
                files.Add(("EQ", NeverOverwrite.WriteAllText(Path.Combine(full, name + "_EQ.xml"), w.Text), System.Text.Encoding.UTF8.GetByteCount(w.Text)));
                foreach (CgmesProfileFile f in extra.Files)
                    files.Add((f.Kind.ToString(), NeverOverwrite.WriteAllText(Path.Combine(full, f.FileName), f.Text), System.Text.Encoding.UTF8.GetByteCount(f.Text)));
            }
            catch (IOException ex) { Console.Error.WriteLine($"error: {verb}: cannot write: {ex.Message}"); return CliExit.InputError; }
            Console.WriteLine($"cgmes-export - {name}: CGMES 2.4.15 bus-branch, {files.Count} document(s) written to {full} (profiles {string.Join(", ", kinds)}).");
            foreach (var f in files) Console.WriteLine("  " + f.Path);
            foreach (CgmesOmission o in extra.Omissions) Console.WriteLine($"  omitted [{o.Profile}] {o.Item}: {o.Reason}");
            foreach (string n in w.Notes) Console.WriteLine("  note: " + n);
            foreach (string n in extra.Notes) Console.WriteLine("  note: " + n);
            CliResult? rep = CliOutput.Report;
            if (rep is not null)
            {
                rep.Set("case", name).Set("cgmes_version", "2.4.15").Set("documents", files.Count).Set("omissions", extra.Omissions.Count)
                   .Set("profiles", string.Join(",", kinds));
                CliResultTable ft = rep.Table("files", "profile", "path", "bytes");
                foreach (var f in files) ft.Row(f.Kind, f.Path, f.Bytes);
                CliResultTable ot = rep.Table("omissions", "profile", "item", "reason");
                foreach (CgmesOmission o in extra.Omissions) ot.Row(o.Profile.ToString(), o.Item, o.Reason);
                rep.Complete();
            }
            return CliExit.Ok;
        }
        catch (InvalidDataException ex) { Console.Error.WriteLine($"error: {verb}: {ex.Message}"); return CliExit.InputError; }
    }

    /// <summary>WP24: the CGMES 3.0 bus-branch documents (EQ, TP, SSH, SV) and any requested profiles, in 3.0.</summary>
    private static int WriteBusBranch30(GridModel model, string dir, List<CgmesProfileKind> kinds, CgmesExtraOptions options)
    {
        const string verb = "cgmes-export";
        CgmesBusBranch30Result r = CgmesBusBranch30Writer.Write(model, options.ScenarioTime);
        CgmesExtraResult? extra = kinds.Count > 0 ? CgmesExtraProfiles.Write(model, r, options) : null;
        string name = model.Name ?? "case";
        string full = Path.GetFullPath(dir);
        var all = r.Files.Concat(extra?.Files ?? Array.Empty<CgmesProfileFile>()).ToList();
        var files = new List<(string Kind, string Path, int Bytes)>();
        try
        {
            foreach (CgmesProfileFile f in all)
                files.Add((f.Kind.ToString(), NeverOverwrite.WriteAllText(Path.Combine(full, f.FileName), f.Text), System.Text.Encoding.UTF8.GetByteCount(f.Text)));
        }
        catch (IOException ex) { Console.Error.WriteLine($"error: {verb}: cannot write: {ex.Message}"); return CliExit.InputError; }
        var omissions = r.Omissions.Concat(extra?.Omissions ?? Array.Empty<CgmesOmission>()).ToList();
        Console.WriteLine($"cgmes-export - {name}: CGMES 3.0 bus-branch, {files.Count} document(s) written to {full}" + (kinds.Count > 0 ? $" (profiles {string.Join(", ", kinds)})." : "."));
        foreach (var f in files) Console.WriteLine("  " + f.Path);
        foreach (CgmesOmission o in omissions) Console.WriteLine($"  omitted [{o.Profile}] {o.Item}: {o.Reason}");
        foreach (string n in r.Notes) Console.WriteLine("  note: " + n);
        foreach (string n in extra?.Notes ?? Array.Empty<string>()) Console.WriteLine("  note: " + n);
        CliResult? rep = CliOutput.Report;
        if (rep is not null)
        {
            rep.Set("case", name).Set("cgmes_version", "3.0").Set("documents", files.Count).Set("omissions", omissions.Count)
               .Set("profiles", string.Join(",", kinds)).Set("model", "bus-branch");
            CliResultTable ft = rep.Table("files", "profile", "path", "bytes");
            foreach (var f in files) ft.Row(f.Kind, f.Path, f.Bytes);
            CliResultTable ot = rep.Table("omissions", "profile", "item", "reason");
            foreach (CgmesOmission o in omissions) ot.Row(o.Profile.ToString(), o.Item, o.Reason);
            rep.Complete();
        }
        return CliExit.Ok;
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
