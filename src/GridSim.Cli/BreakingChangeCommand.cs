using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using GridSim.Core.Journal;
using GridSim.Core.Security;

namespace GridSim.Cli;

/// <summary>
/// RC5: <c>gridsim breaking-change validate | import | list</c> (manual ch. 19, technical ch. 42).
///
/// <c>validate</c> re-runs the journaled jobs on this build, each as a child process of this same binary in a scratch
/// folder of its own, with every output path rewritten into that folder, and compares each output with the recorded
/// one bit for bit (build and run identity fields left out). Each entry is IDENTICAL, CHANGED (with the fields that
/// moved, by how many bits, and the breaking change that explains it, or UNEXPLAINED), CANNOT-REPLAY with the reason, or
/// SKIPPED-SAME-BUILD. The report is a pure function of the entries, their inputs and this build: it is the same for
/// every <c>--parallel</c>, and lists the entries in journal order.
/// </summary>
internal static class BreakingChangeCommand
{
    private const string Usage =
        "usage: gridsim breaking-change validate [--journal <file.jsonl>] [--since <version>] [--verb <verb>] [--case <glob>] " +
        "[--parallel N] [--scratch <dir>] [--keep-scratch] [--accept <Bn>]\n" +
        "       gridsim breaking-change import <dir|file.json>... [--journal <file.jsonl>]\n" +
        "       gridsim breaking-change list [--since <version>]";

    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    private sealed class UsageError(string message) : Exception(message);

    /// <summary>The verb, with <paramref name="loadCase"/> the CLI's case loader (a path or a built-in name, as the analysis
    /// verbs resolve their case argument), which validate uses to decide B33 on a job's case.</summary>
    public static int Run(List<string> rest, string baseDir, Func<string, GridSim.Core.Model.GridModel> loadCase)
    {
        if (rest.Count < 2)
        {
            Console.Error.WriteLine(Usage);
            return CliExit.Usage;
        }
        try
        {
            return rest[1].ToLowerInvariant() switch
            {
                "validate" => Validate(rest.Skip(2).ToList(), baseDir, loadCase),
                "import" => Import(rest.Skip(2).ToList()),
                "list" => List(rest.Skip(2).ToList()),
                _ => throw new UsageError($"breaking-change has the subcommands validate, import and list (got '{rest[1]}')."),
            };
        }
        catch (UsageError ex)
        {
            Console.Error.WriteLine("error: " + ex.Message);
            Console.Error.WriteLine(Usage);
            return CliExit.Usage;
        }
    }

    // ---- flags -----------------------------------------------------------------------------------------------------

    private sealed class Flags
    {
        public readonly Dictionary<string, string> Values = new(StringComparer.Ordinal);
        public readonly HashSet<string> Switches = new(StringComparer.Ordinal);
        public readonly List<string> Positionals = new();
    }

    private static Flags Parse(List<string> a, string[] valueFlags, string[] switches)
    {
        var f = new Flags();
        for (int i = 0; i < a.Count; i++)
        {
            string t = a[i];
            if (!t.StartsWith("--", StringComparison.Ordinal)) { f.Positionals.Add(t); continue; }
            string name = t;
            string? value = null;
            int eq = t.IndexOf('=', StringComparison.Ordinal);
            if (eq > 0) { name = t[..eq]; value = t[(eq + 1)..]; }
            if (switches.Contains(name) && value is null)
            {
                if (!f.Switches.Add(name)) throw new UsageError($"{name} given more than once.");
                continue;
            }
            if (!valueFlags.Contains(name))
                throw new UsageError($"breaking-change does not use {name}. It accepts: {string.Join(", ", valueFlags.Concat(switches))} (and --json/--csv/--out).");
            if (value is null)
            {
                if (i + 1 >= a.Count || a[i + 1].StartsWith("--", StringComparison.Ordinal)) throw new UsageError($"{name} expected a value but none was given.");
                value = a[++i];
            }
            if (!f.Values.TryAdd(name, value)) throw new UsageError($"{name} given more than once.");
        }
        return f;
    }

    // The journal to read or append to: --journal, else the configured journal.
    private static string? JournalPath(Flags f, out string? why)
    {
        why = null;
        if (f.Values.TryGetValue("--journal", out string? given))
        {
            if (string.IsNullOrWhiteSpace(given)) throw new UsageError("--journal expected a journal file path.");
            if (PathConfinement.IsNetworkOrDevice(given)) throw new UsageError($"--journal '{given}': a network or device path is refused.");
            return Path.GetFullPath(given);
        }
        string? configPath = Environment.GetEnvironmentVariable(JournalRecorder.ConfigVariable);
        if (string.IsNullOrWhiteSpace(configPath)) configPath = JournalConfig.DefaultConfigPath();
        JournalSettings? s = JournalConfig.Resolve(null, false, Environment.GetEnvironmentVariable(JournalRecorder.JournalVariable),
            configPath, out _, out string? warning);
        if (s is null) why = warning ?? "no journal is configured: give --journal <file.jsonl>, or switch the journal on (GRIDSIM_JOURNAL or the journal config file)";
        return s?.Path;
    }

    // ---- validate --------------------------------------------------------------------------------------------------

    private sealed record Prepared(JournalLine Line, int Position, ReplayOutcome Outcome, Plan? Plan);

    /// <summary>What a re-run needs: its folder, its rewritten command line and where each recorded output should land.</summary>
    private sealed class Plan
    {
        public JournalEntry Entry = null!;
        public string Dir = "";
        public List<string> Argv = new();
        public List<(JournalOutput Recorded, string Expected)> Outputs = new();
        public Dictionary<string, string> MapRecorded = new(StringComparer.Ordinal);
        public Dictionary<string, string> MapReplay = new(StringComparer.Ordinal);
        public long TimeoutMs;
        /// <summary>The job's DYR inputs with their recorded bytes, for the condition of an unbounded change (B3).</summary>
        public List<UnboundedChanges.DyrInput> Dyr = new();
        /// <summary>2.1.127: the job's plug-in configurations, where the re-run reads them and their bytes.</summary>
        public List<(string Path, byte[] Bytes)> PluginConfigs = new();
        /// <summary>B33's condition: whether the job's case has a term MATPOWER's makeBdc adds (null when B33 cannot
        /// explain the job, so the case was not read).</summary>
        public bool? CaseUsesMakeBdcTerms;
    }

    // 2.1.127: a study's curves folder (read whole by the curve library) holds exactly the recorded files, when read in
    // place from the folder <paramref name="home"/>. 2.1.130: a journal that records the folder itself (its file list)
    // is checked against that list, so files added to a folder that was empty when the run was recorded are seen too; an
    // absolute folder is re-listed where it is.
    private static bool CurvesUnchanged(List<JournalCaseFile> named, string home)
    {
        JournalCaseFile? folderRecord = named.FirstOrDefault(c => c.Label == DynamicsInputs.CurvesFolderLabel);
        if (folderRecord is not null)
        {
            string at = Path.GetFullPath(folderRecord.Path, home);
            return Directory.Exists(at) && JournalRecorder.Hash(DynamicsInputs.FolderListing(at)) == folderRecord.Sha256;
        }
        var curves = named.Where(c => c.Label.StartsWith(DynamicsInputs.CurvesLabel, StringComparison.Ordinal)).ToList();
        if (curves.Count == 0) return true;
        string inside = curves[0].Label[DynamicsInputs.CurvesLabel.Length..];
        if (curves[0].Path.Length < inside.Length) return false;
        string folder = Path.GetFullPath(curves[0].Path[..^inside.Length], home);
        if (!Directory.Exists(folder)) return false;
        var now = Directory.GetFiles(folder, "*.json", SearchOption.AllDirectories)
            .Select(f => Path.GetRelativePath(folder, f).Replace('\\', '/')).Order(StringComparer.Ordinal).ToList();
        var recorded = curves.Select(c => c.Label[DynamicsInputs.CurvesLabel.Length..]).Order(StringComparer.Ordinal).ToList();
        return now.SequenceEqual(recorded, StringComparer.Ordinal);
    }

    private sealed record ChildResult(int ExitCode, bool Crashed, bool TimedOut, string FirstErrorLine);

    private static int Validate(List<string> a, string baseDir, Func<string, GridSim.Core.Model.GridModel> loadCase)
    {
        Flags f = Parse(a, ["--journal", "--since", "--verb", "--case", "--parallel", "--scratch", "--accept"], ["--keep-scratch"]);
        if (f.Positionals.Count > 0) throw new UsageError($"validate takes no positional argument (got '{f.Positionals[0]}').");
        string? journal = JournalPath(f, out string? noJournal);
        if (journal is null) { Console.Error.WriteLine("error: breaking-change validate: " + noJournal + "."); return CliExit.InputError; }
        if (!File.Exists(journal) && JournalFile.Segments(journal).Count == 0)
        {
            Console.Error.WriteLine($"error: breaking-change validate: the journal '{journal}' does not exist.");
            return CliExit.InputError;
        }
        string? since = f.Values.GetValueOrDefault("--since");
        if (since is not null && VersionOrder.Parse(since) is null) throw new UsageError($"--since expected a release number such as 2.1.090 but got '{since}'.");
        string? verbFilter = f.Values.TryGetValue("--verb", out string? vf) ? CliOutput.Canonical(vf) : null;
        Regex? caseGlob = f.Values.TryGetValue("--case", out string? cg) ? Glob(cg) : null;
        int parallel = 0;
        if (f.Values.TryGetValue("--parallel", out string? pv) && (!int.TryParse(pv, NumberStyles.None, Inv, out parallel) || parallel < 0))
            throw new UsageError($"--parallel expected a whole number, 0 for automatic, but got '{pv}'.");
        string scratch;
        bool ownScratch;
        if (f.Values.TryGetValue("--scratch", out string? sv))
        {
            if (PathConfinement.IsNetworkOrDevice(sv)) throw new UsageError($"--scratch '{sv}': a network or device path is refused.");
            scratch = Path.GetFullPath(sv);
            if (File.Exists(scratch) || (Directory.Exists(scratch) && Directory.EnumerateFileSystemEntries(scratch).Any()))
                throw new UsageError($"--scratch '{sv}' must be a new or empty folder.");
            string jdir = Path.GetDirectoryName(journal)!;
            if (PathConfinement.Refusal(scratch, scratch, jdir) is null)
                throw new UsageError($"--scratch '{sv}' is inside the journal's folder; name a folder outside it.");
            ownScratch = false;
        }
        else
        {
            scratch = Path.Combine(Path.GetTempPath(), "gridsim-bcv-" + Guid.NewGuid().ToString("N")[..12]);
            ownScratch = true;
        }

        string buildVersion = GridSim.Core.BuildInfo.Version;
        string buildCommit = JournalRecorder.FullCommit(GridSim.Core.BuildInfo.InformationalVersion);
        BreakingChangeMap map = BreakingChangeMap.Default;

        // --accept Bn[,Bm]: acknowledge the jobs that moved under these unbounded changes. Only a change with no bound can
        // be accepted; a bounded one is checked against its bound.
        var acceptNow = new List<string>();
        if (f.Values.TryGetValue("--accept", out string? acceptValue))
            foreach (string id in acceptValue.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                BreakingChange? b = map.Entries.FirstOrDefault(x => string.Equals(x.Id, id, StringComparison.OrdinalIgnoreCase));
                if (b is null) throw new UsageError($"--accept '{id}' names no breaking change; 'gridsim breaking-change list' prints them.");
                if (!UnboundedChanges.IsUnbounded(b))
                    throw new UsageError($"--accept {b.Id}: {b.Id} has a bound, which validate checks; only a breaking change with no bound ({string.Join(", ", map.Entries.Where(UnboundedChanges.IsUnbounded).Select(x => x.Id))}) can be accepted.");
                if (!acceptNow.Contains(b.Id)) acceptNow.Add(b.Id);
            }
        if (f.Values.ContainsKey("--accept") && acceptNow.Count == 0) throw new UsageError("--accept expected a breaking-change ID such as B3.");
        string acceptPath = AcceptFile.PathFor(journal);
        AcceptFile accepted;
        try { accepted = File.Exists(acceptPath) ? AcceptFile.Parse(File.ReadAllText(acceptPath)) : new AcceptFile(); }
        catch (Exception ex) when (ex is InvalidDataException or IOException or UnauthorizedAccessException)
        {
            Console.Error.WriteLine($"error: breaking-change validate: the accept file '{acceptPath}' cannot be read: {ex.Message}");
            return CliExit.InputError;
        }
        IReadOnlyList<JournalLine> lines = JournalFile.Read(journal);

        // Selection, in journal (sequence) order. A damaged line is always reported.
        var selected = new List<JournalLine>();
        bool filtered = since is not null || verbFilter is not null || caseGlob is not null;
        foreach (JournalLine l in lines)
        {
            JournalEntry? e = l.Entry;
            if (e is null || l.Error is not null) { selected.Add(l); continue; }
            if (since is not null && VersionOrder.Compare(e.Version, since) < 0) continue;
            if (verbFilter is not null && e.Verb != verbFilter) continue;
            if (caseGlob is not null && !e.Tokens.Any(t => t.Kind is "input" or "case-folder" or "builtin"
                    && (caseGlob.IsMatch(t.Abs) || caseGlob.IsMatch(Path.GetFileName(t.Abs)) || caseGlob.IsMatch(t.Raw)))) continue;
            selected.Add(l);
        }
        if (filtered && !selected.Any(l => l.Entry is not null && l.Error is null))
        {
            Console.Error.WriteLine("error: breaking-change validate: the filters name nothing in the journal.");
            return CliExit.Usage;
        }

        string md = VerbHelp.LoadEmbedded();
        var prepared = new List<Prepared>();
        for (int i = 0; i < selected.Count; i++) prepared.Add(Prepare(selected[i], i, scratch, journal, md, buildVersion, buildCommit, map, baseDir, loadCase));

        var jobs = prepared.Where(p => p.Plan is not null)
            .Select(p => new ReplayJob(p.Position, p.Line.Entry!.Seq, p.Line.Entry.ElapsedMs, p.Line.Entry.PeakWorkingSetBytes)).ToList();
        long installed = GC.GetGCMemoryInfo().TotalAvailableMemoryBytes;
        int n = parallel > 0 ? parallel : ReplayScheduler.AutoParallel(Environment.ProcessorCount, installed, Math.Max(1, jobs.Count));
        var results = new ChildResult?[prepared.Count];
        try
        {
            if (jobs.Count > 0)
            {
                Directory.CreateDirectory(scratch);
                ChildResult[] run = ReplayScheduler.Run(jobs, n, (long)(0.8 * installed),
                    job => RunChild(prepared[job.Position].Plan!),
                    r => r.Crashed || r.TimedOut,
                    (job, done, total) => Console.Error.WriteLine(string.Create(Inv, $"[{done}/{total}] seq {job.Seq} done")));
                for (int i = 0; i < jobs.Count; i++) results[jobs[i].Position] = run[i];
            }
            foreach (Prepared p in prepared)
                if (p.Plan is not null)
                {
                    JournalEntry pe = p.Line.Entry!;
                    IReadOnlyList<string> exitCandidates = map.Explain(pe.Version, buildVersion, pe.Verb, pe.Argv,
                        pe.Tokens.Where(t => t.Kind == "input").Select(t => t.Abs).Concat(pe.CaseFiles.Select(c => c.Abs)).ToList());
                    Classify(p.Outcome, p.Plan, results[p.Position]!, scratch, (from, to) => map.ExitClaims(exitCandidates, from, to));
                }
        }
        finally
        {
            if (!f.Switches.Contains("--keep-scratch") && Directory.Exists(scratch))
            {
                try
                {
                    if (ownScratch) Directory.Delete(scratch, recursive: true);
                    else foreach (string d in Directory.GetFileSystemEntries(scratch))
                        {
                            if (Directory.Exists(d)) Directory.Delete(d, recursive: true);
                            else File.Delete(d);
                        }
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    Console.Error.WriteLine($"warning: breaking-change validate: the scratch folder '{scratch}' could not be removed: {ex.Message}");
                }
            }
            else if (Directory.Exists(scratch)) Console.Error.WriteLine($"[breaking-change] scratch kept: {scratch}");
        }

        // Explanation, then the report: built from the outcome array, in sequence order, after every job finished.
        var outcomes = prepared.Select(p => p.Outcome).ToList();
        foreach (Prepared p in prepared)
        {
            ReplayOutcome o = p.Outcome;
            if (o.Status != ReplayStatus.Changed) continue;
            JournalEntry e = p.Line.Entry!;
            IReadOnlyList<string> candidates = map.Explain(e.Version, buildVersion, e.Verb, e.Argv,
                e.Tokens.Where(t => t.Kind == "input").Select(t => t.Abs).Concat(e.CaseFiles.Select(c => c.Abs)).ToList());
            UnboundedChanges.Judge(map, o, candidates, e.Verb, e.Argv, p.Plan?.Dyr ?? new List<UnboundedChanges.DyrInput>(), p.Plan?.CaseUsesMakeBdcTerms);
        }
        IReadOnlyList<UnboundedChanges.ReviewRow> reviews = UnboundedChanges.Review(outcomes, accepted, acceptNow, buildVersion);
        if (acceptNow.Count > 0)
        {
            if (reviews.Any(r => r.Outcome == "accepted now"))
            {
                try
                {
                    string tmp = acceptPath + ".tmp";
                    File.WriteAllText(tmp, accepted.Json(), new UTF8Encoding(false));
                    File.Move(tmp, acceptPath, overwrite: true);
                    Console.Error.WriteLine(string.Create(Inv, $"[breaking-change] accepted {string.Join(", ", acceptNow)} for {reviews.Where(r => r.Outcome == "accepted now").Select(r => r.Seq).Distinct().Count()} job(s), recorded in {acceptPath}"));
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    Console.Error.WriteLine($"error: breaking-change validate: the accept file '{acceptPath}' cannot be written: {ex.Message}");
                    return CliExit.InputError;
                }
            }
            else Console.Error.WriteLine($"[breaking-change] --accept {string.Join(",", acceptNow)}: no job moved under {string.Join(", ", acceptNow)} in this run; nothing recorded.");
        }
        Console.Write(ReplayReport.ConsoleText(outcomes, buildVersion));
        FillReport(outcomes, lines.Count, buildVersion, map, acceptNow, reviews);
        return ReplayReport.ExitCode(outcomes);
    }

    private static Regex Glob(string glob)
    {
        string pattern = "^" + Regex.Escape(glob).Replace(@"\*", ".*", StringComparison.Ordinal).Replace(@"\?", ".", StringComparison.Ordinal) + "$";
        return new Regex(pattern, (OperatingSystem.IsWindows() ? RegexOptions.IgnoreCase : RegexOptions.None) | RegexOptions.CultureInvariant);
    }

    private static string InputsText(JournalEntry e) => string.Join(";", e.Tokens
        .Where(t => t.Kind is "input" or "case-folder" or "builtin")
        .Select(t => t.Kind == "builtin" ? t.Raw : Path.GetFileName(Path.TrimEndingDirectorySeparator(t.Abs))));

    private static Prepared Prepare(JournalLine line, int position, string scratch, string journal, string md, string buildVersion, string buildCommit,
        BreakingChangeMap map, string root, Func<string, GridSim.Core.Model.GridModel> loadCase)
    {
        JournalEntry? e = line.Entry;
        if (e is null || line.Error is not null)
        {
            var bad = new ReplayOutcome
            {
                Seq = e?.Seq ?? 0, RecordedVersion = e?.Version ?? "", Verb = e?.Verb ?? "",
                Status = ReplayStatus.CannotReplay,
                Reason = line.Error == "entry schema unknown"
                    ? "entry schema unknown"
                    : string.Create(Inv, $"damaged entry (journal line {line.LineNumber} of {Path.GetFileName(line.File)} unreadable)"),
            };
            return new Prepared(line, position, bad, null);
        }
        var o = new ReplayOutcome { Seq = e.Seq, RecordedVersion = e.Version, RecordedUtc = e.RecordedUtc, Verb = e.Verb, Inputs = InputsText(e), RecordedExit = e.ExitCode };
        Prepared Cannot(string reason) { o.Status = ReplayStatus.CannotReplay; o.Reason = reason; return new Prepared(line, position, o, null); }

        if (e.Version == buildVersion && e.Commit == buildCommit)
        {
            o.Status = ReplayStatus.SkippedSameBuild;
            o.Reason = "recorded by this build (same version and commit): by determinism it cannot change";
            return new Prepared(line, position, o, null);
        }
        if (VersionOrder.Compare(e.Version, buildVersion) > 0) return Cannot($"recorded by a newer build ({e.Version} > {buildVersion})");
        if (e.Verb != "run" && VerbFlags.Accepted(md, e.Verb, writerVerb: false) is null) return Cannot("verb removed");
        if (ReplayPolicy.Refusal(e.Verb, e.Argv, JournalRecorder.CanonicalWriterVerbs) is string why) return Cannot("not replayable: " + why);
        if (e.Gpu || e.Backend.StartsWith("gpu/", StringComparison.Ordinal)) return Cannot("GPU backend (outside the bit-identity contract)");

        string dir = Path.Combine(scratch, e.Seq.ToString(Inv));
        string objects = JournalSnapshot.ObjectsDir(journal);
        string journalDir = Path.GetDirectoryName(journal)!;
        var plan = new Plan { Entry = e, Dir = dir, Argv = e.Argv.ToList(), TimeoutMs = Math.Max(10 * e.ElapsedMs, 60_000) };
        var materialise = new List<(string Path, byte[] Bytes)>();
        var materialiseDirs = new List<string>();

        // Inputs: the recorded file when it is still there with its recorded bytes, else the snapshot copy.
        string? Resolve(string abs, string rel, string? sha, bool snap, string inDir, string name, out string? problem)
        {
            problem = null;
            string? found = File.Exists(abs) ? abs : File.Exists(Path.GetFullPath(rel, journalDir)) ? Path.GetFullPath(rel, journalDir) : null;
            if (found is not null && sha is not null && JournalRecorder.Hash(File.ReadAllBytes(found)) == sha) return found;
            if (sha is not null && snap)
            {
                byte[]? bytes = JournalSnapshot.Get(objects, sha, out string? why);
                if (bytes is not null)
                {
                    string target = Path.Combine(inDir, name);
                    materialise.Add((target, bytes));
                    return target;
                }
                if (why is not null && why.StartsWith("snapshot object refused", StringComparison.Ordinal)) { problem = why; return null; }
            }
            if (found is null) { problem = $"input missing: {abs}"; return null; }
            problem = sha is null ? $"input missing: {abs}" : $"input changed: {abs} sha256 {sha} != {JournalRecorder.Hash(File.ReadAllBytes(found))}";
            return null;
        }

        // A file's bytes when it is still there with its recorded hash (at its recorded path, or relative to the journal).
        string? InPlace(string abs, string rel, string? sha)
        {
            if (sha is null) return null;
            foreach (string at in new[] { abs, rel.Length > 0 ? Path.GetFullPath(rel, journalDir) : abs })
                if (File.Exists(at) && JournalRecorder.Hash(File.ReadAllBytes(at)) == sha) return at;
            return null;
        }
        // The bytes a re-run will read at a path: the copy about to be placed there, else the file there.
        byte[]? BytesAt(string path, out string? problem)
        {
            problem = null;
            foreach ((string p, byte[] b) in Enumerable.Reverse(materialise))
                if (p == path) return b;
            try { return File.ReadAllBytes(path); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { problem = ex.Message; return null; }
        }

        // 2.1.127: an input that names files inside itself (a v2 manifest; a dynamics study, model assignments or plug-in
        // configuration, technical ch. 42). Read in place when the input and every file it names still have their recorded
        // bytes where the run would find them (and a study's curves folder holds exactly the recorded files); else the
        // whole set is placed under in/<key>/, the input deep enough below it that every relative reference (../ included)
        // stays inside, each file from its recorded path when its bytes are unchanged, else from the snapshot store. A
        // reference written as an absolute path is always read where it points, so it must still be there unchanged. A
        // plug-in assembly is never taken from the snapshot store.
        string? ResolveNamed(JournalToken t, List<JournalCaseFile> listed, string inDir, out string? problem)
        {
            problem = null;
            // 2.1.130: the curves folder record (its file list) is checked by CurvesUnchanged, not read as a file.
            JournalCaseFile? curvesFolder = listed.FirstOrDefault(c => c.Label == DynamicsInputs.CurvesFolderLabel);
            List<JournalCaseFile> named = listed.Where(c => c.Label != DynamicsInputs.CurvesFolderLabel).ToList();
            string? Missing(JournalCaseFile c, string? at)
            {
                bool assembly = c.Label.StartsWith(DynamicsInputs.PluginAssemblyLabel, StringComparison.Ordinal);
                string what = assembly ? "plug-in assembly" : "input";
                return at is null || !File.Exists(at) ? $"{what} missing: {c.Abs}"
                    : $"{what} changed: {c.Abs} sha256 {c.Sha256} != {JournalRecorder.Hash(File.ReadAllBytes(at))}";
            }
            string? tokenAt = InPlace(t.Abs, t.Rel, t.Sha256);
            if (tokenAt is not null)
            {
                string home0 = Path.GetDirectoryName(tokenAt)!;
                bool all = named.All(c => Path.GetFullPath(c.Path, home0) is string at && File.Exists(at) && JournalRecorder.Hash(File.ReadAllBytes(at)) == c.Sha256)
                    && CurvesUnchanged(listed, home0);
                if (all) return tokenAt;
            }
            int up = listed.Where(c => !Path.IsPathRooted(c.Path)).Select(c => c.Path.Split('/', '\\').Count(x => x == "..")).DefaultIfEmpty(0).Max();
            string home = inDir;
            for (int k = 1; k <= up; k++) home = Path.Combine(home, string.Create(Inv, $"up{k}"));
            byte[]? tokenBytes = tokenAt is not null ? File.ReadAllBytes(tokenAt) : null;
            if (tokenBytes is null && t.Sha256 is not null && t.Snapshot == true)
            {
                tokenBytes = JournalSnapshot.Get(objects, t.Sha256, out string? why);
                if (tokenBytes is null && why is not null && why.StartsWith("snapshot object refused", StringComparison.Ordinal)) { problem = why; return null; }
            }
            if (tokenBytes is null)
            {
                string? there = File.Exists(t.Abs) ? t.Abs : null;
                problem = there is null || t.Sha256 is null ? $"input missing: {t.Abs}" : $"input changed: {t.Abs} sha256 {t.Sha256} != {JournalRecorder.Hash(File.ReadAllBytes(there))}";
                return null;
            }
            string target = Path.Combine(home, Path.GetFileName(t.Abs));
            materialise.Add((target, tokenBytes));
            // The curves folder: an absolute one is read where it points, so it must still list exactly the recorded files; a
            // relative one is placed under in/<key>/ holding exactly the recorded files (made even when it was empty).
            bool curvesRooted = curvesFolder is not null ? Path.IsPathRooted(curvesFolder.Path)
                : named.Any(c => c.Label.StartsWith(DynamicsInputs.CurvesLabel, StringComparison.Ordinal) && Path.IsPathRooted(c.Path));
            if (curvesRooted && !CurvesUnchanged(listed, home))
            {
                string folderAbs = curvesFolder?.Abs ?? "curves folder";
                problem = $"input changed: {folderAbs} (the curves folder no longer holds exactly the recorded files)";
                return null;
            }
            if (curvesFolder is not null && !curvesRooted) materialiseDirs.Add(Path.GetFullPath(curvesFolder.Path, home));
            foreach (JournalCaseFile c in named)
            {
                if (Path.IsPathRooted(c.Path))
                {
                    if (!(File.Exists(c.Path) && JournalRecorder.Hash(File.ReadAllBytes(c.Path)) == c.Sha256)) { problem = Missing(c, c.Path); return null; }
                    continue;
                }
                byte[]? b = null;
                foreach (string? at in new[] { c.Abs, tokenAt is null ? null : Path.GetFullPath(c.Path, Path.GetDirectoryName(tokenAt)!) })
                    if (b is null && at is not null && File.Exists(at))
                    {
                        byte[] read = File.ReadAllBytes(at);
                        if (JournalRecorder.Hash(read) == c.Sha256) b = read;
                    }
                // A plug-in assembly is code: it is never taken from the snapshot store, whatever the journal says (a
                // hand-edited "snapshot": true and a planted store object must not supply one).
                if (b is null && c.Snapshot == true && !c.Label.StartsWith(DynamicsInputs.PluginAssemblyLabel, StringComparison.Ordinal))
                {
                    b = JournalSnapshot.Get(objects, c.Sha256, out string? why);
                    if (b is null && why is not null && why.StartsWith("snapshot object refused", StringComparison.Ordinal)) { problem = why; return null; }
                }
                if (b is null) { problem = Missing(c, c.Abs); return null; }
                materialise.Add((Path.GetFullPath(c.Path, home), b));
            }
            return target;
        }

        // The synthetic output tokens: the value of an option that names an output path outside the output policy's own
        // output flags (stability --events-out), rewritten into the scratch folder like an output-flag value.
        var tokens = e.Tokens.ToList();
        foreach ((string verb, string option) in ReplayPolicy.OutputPathFlags)
        {
            if (verb != e.Verb) continue;
            for (int i = 0; i + 1 < e.Argv.Count; i++)
                if (e.Argv[i] == option && !e.Argv[i + 1].StartsWith("--", StringComparison.Ordinal) && !tokens.Any(t => t.Index == i + 1))
                {
                    string abs;
                    try { abs = Path.GetFullPath(e.Argv[i + 1], e.Cwd.Length > 0 ? e.Cwd : dir); }
                    catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException) { return Cannot($"not replayable: {option} '{e.Argv[i + 1]}' is not a path"); }
                    tokens.Add(new JournalToken { Index = i + 1, Kind = "output", Raw = e.Argv[i + 1], Abs = abs, Rel = Path.GetRelativePath(journalDir, abs) });
                }
        }
        tokens.Sort((a, b) => a.Index != b.Index ? a.Index.CompareTo(b.Index) : (a.Part ?? -1).CompareTo(b.Part ?? -1));

        var replacement = new Dictionary<(int, int?), string>();
        int outputNo = 0;
        foreach (JournalToken t in tokens)
        {
            string key = t.Part is int part ? $"{t.Index}.{part}" : t.Index.ToString(Inv);
            if (t.Kind == "input")
            {
                string inDir = Path.Combine(dir, "in", key);
                List<JournalCaseFile> named = e.CaseFiles.Where(c => c.Token == t.Index).ToList();
                string? path;
                string? problem;
                if (named.Count == 0) path = Resolve(t.Abs, t.Rel, t.Sha256, t.Snapshot == true, inDir, Path.GetFileName(t.Abs), out problem);
                else path = ResolveNamed(t, named, inDir, out problem);
                if (path is null) return Cannot(problem!);
                replacement[(t.Index, t.Part)] = path;
                Map(plan, t.Raw, t.Abs, path, $"<input:{key}>");
                // 2.1.130: a result that names an input by its file name alone (dynamics-study's
                // provenance.modelAssignments.file) names it the same way on both sides. The recorded token is often that
                // bare name, so it maps to the placeholder; the re-run's file name maps to it too, as an output's does.
                Map(plan, Path.GetFileName(t.Abs), Path.GetFileName(t.Abs), Path.GetFileName(path), $"<input:{key}>");
                // A DYR input (a .dyr file, a --dyn-data file that is not JSON, or a DYR a study file names): its recorded
                // bytes, for the condition of an unbounded change. The bytes are the recorded ones: the file passed its
                // hash check, or came from the snapshot store. One that cannot be read now makes the entry CANNOT-REPLAY
                // (2.1.127), so the condition is always decided on the DYR itself, never assumed.
                bool dynData = t.Index > 0 && t.Index - 1 < e.Argv.Count && e.Argv[t.Index - 1] == "--dyn-data";
                if (dynData || Path.GetExtension(t.Abs).Equals(".dyr", StringComparison.OrdinalIgnoreCase))
                    if (UnboundedChanges.AddDyr(t.Abs, path, skipJson: true, BytesAt, plan.Dyr) is string unreadable) return Cannot(unreadable);
                foreach (JournalCaseFile c in named.Where(c => c.Label == "study:dyr" || Path.GetExtension(c.Abs).Equals(".dyr", StringComparison.OrdinalIgnoreCase)))
                    if (UnboundedChanges.AddDyr(c.Abs, Path.GetFullPath(c.Path, Path.GetDirectoryName(path)!), skipJson: false, BytesAt, plan.Dyr) is string unreadable)
                        return Cannot(unreadable);
                // A plug-in configuration: its assemblies are checked against their pins before the re-run starts.
                byte[]? own = BytesAt(path, out _);
                if (own is not null && DynamicsInputs.IsPluginConfiguration(own)) plan.PluginConfigs.Add((path, own));
            }
            else if (t.Kind == "case-folder")
            {
                string inDir = Path.Combine(dir, "in", key);
                bool allThere = true;
                var copies = new List<(string, byte[])>();
                foreach (JournalCaseFile c in e.CaseFiles.Where(c => c.Token == t.Index))
                {
                    string? cp = Resolve(c.Abs, Path.GetRelativePath(journalDir, c.Abs), c.Sha256, c.Snapshot == true, inDir, c.Path, out string? problem);
                    if (cp is null) return Cannot(problem!);
                    if (cp.StartsWith(inDir, StringComparison.Ordinal)) allThere = false;
                    else copies.Add((Path.Combine(inDir, c.Path), File.ReadAllBytes(cp)));
                }
                string folder = t.Abs;
                if (!allThere || !Directory.Exists(t.Abs))
                {
                    // Mixed sources: the whole folder is assembled under the scratch folder.
                    materialise.AddRange(copies);
                    folder = inDir;
                }
                replacement[(t.Index, t.Part)] = folder;
                Map(plan, t.Raw, t.Abs, folder, $"<case:{key}>");
            }
            else if (t.Kind == "output")
            {
                outputNo++;
                string target = Path.Combine(dir, "out", string.Create(Inv, $"{outputNo}-{Path.GetFileName(Path.TrimEndingDirectorySeparator(t.Abs))}"));
                replacement[(t.Index, null)] = target;
                Map(plan, t.Raw, t.Abs, target, $"<output:{key}>");
                Map(plan, Path.GetFileName(t.Abs), Path.GetFileName(t.Abs), Path.GetFileName(target), $"<output:{key}>");
            }
        }
        plan.MapRecorded.TryAdd(e.Cwd, "<cwd>");
        plan.MapReplay.TryAdd(dir, "<cwd>");

        // Rewrite the command line.
        foreach (int index in replacement.Keys.Select(k => k.Item1).Distinct().Order())
        {
            string tok = plan.Argv[index];
            string prefix = tok.StartsWith("--", StringComparison.Ordinal) && tok.Contains('=', StringComparison.Ordinal)
                ? tok[..(tok.IndexOf('=', StringComparison.Ordinal) + 1)] : "";
            string value = tok[prefix.Length..];
            if (replacement.TryGetValue((index, null), out string? whole)) { plan.Argv[index] = prefix + whole; continue; }
            string[] pieces = value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            for (int k = 0; k < pieces.Length; k++)
                if (replacement.TryGetValue((index, k), out string? piece)) pieces[k] = piece;
            plan.Argv[index] = prefix + string.Join(",", pieces);
        }

        // Where each recorded output should land in the re-run.
        if (e.Outputs.Count == 0) return Cannot("no result file recorded (console-only run; re-run it with --json to make it replayable)");
        var outTokens = tokens.Where(t => t.Kind == "output").ToList();
        foreach (JournalOutput rec in e.Outputs)
        {
            string? expected = ExpectedPath(rec, outTokens, replacement, dir);
            if (expected is null) return Cannot($"not replayable: an output was written outside the paths the command line names ({rec.Abs})");
            if (!File.Exists(rec.Abs)) return Cannot($"recorded result missing: {rec.Abs}");
            if (JournalRecorder.Hash(File.ReadAllBytes(rec.Abs)) != rec.Sha256) return Cannot($"recorded result changed since the run: {rec.Abs}");
            plan.Outputs.Add((rec, expected));
        }

        // Confinement: everything the re-run writes or is handed must lie in its scratch folder.
        foreach (string p in replacement.Where(r => tokens.Any(t => t.Index == r.Key.Item1 && t.Kind == "output")).Select(r => r.Value)
                     .Concat(plan.Outputs.Select(x => x.Expected)).Concat(materialise.Select(m => m.Path)).Concat(materialiseDirs))
            if (PathConfinement.Refusal(p, Path.GetFullPath(p), dir) is not null) return Cannot("refused: writes outside the scratch folder");

        foreach (string folder in materialiseDirs) Directory.CreateDirectory(folder);
        foreach ((string path, byte[] bytes) in materialise)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllBytes(path, bytes);
        }

        // SEC-27: a re-run never loads code. ReplayPolicy refuses --model-plugins and user-model check-plugin by name, before
        // this point; an input that is a plug-in configuration however it was named is refused here too, before the re-run
        // starts.
        if (plan.PluginConfigs.Count > 0)
            return Cannot("not replayable: an input is a plug-in configuration, and a re-run never loads code");
        // Kept as a second barrier; unreachable by policy since SEC-27 (the policy and the check above refuse every plug-in
        // job before this point).
        // 2.1.127: a plug-in job. Every assembly its configuration names must be where the re-run will look, with the bytes
        // its pin names, before the re-run starts; else the entry is CANNOT-REPLAY naming the assembly, and nothing is
        // loaded (validate never loads an assembly; the re-run's own loader checks the pins again before it loads one).
        foreach ((string configPath, byte[] configBytes) in plan.PluginConfigs)
            if (DynamicsInputs.VerifyPluginPins(configBytes, configPath, p => File.Exists(p) ? File.ReadAllBytes(p) : null) is string pinProblem)
                return Cannot(pinProblem.Replace(dir, "<scratch>", StringComparison.Ordinal));

        // B33 (2.1.144) is decided on the job's case. When B33 can explain this job (an exact match of its window and scope)
        // and the job runs the DC model, the case is loaded where the re-run will load it, by the CLI's own loader, and
        // B33 is raised only when the case has a term MATPOWER's makeBdc adds (DcPowerFlow.UsesMakeBdcTerms): a case with
        // none is computed by the earlier instructions bit for bit. A case that cannot be read makes the job
        // CANNOT-REPLAY, as an unreadable DYR does, so the condition is never assumed.
        IReadOnlyList<string> exact = map.Explain(e.Version, buildVersion, e.Verb, e.Argv,
            e.Tokens.Where(t => t.Kind == "input").Select(t => t.Abs).Concat(e.CaseFiles.Select(c => c.Abs)).ToList());
        if (exact.Contains(UnboundedChanges.DcModelChange) && UnboundedChanges.RunsDcModel(e.Verb, e.Argv))
        {
            bool? terms = DcCaseTerms(e, tokens, replacement, root, loadCase, out string? caseProblem);
            if (terms is null) return Cannot(caseProblem!.Replace(dir, "<scratch>", StringComparison.Ordinal));
            plan.CaseUsesMakeBdcTerms = terms;
        }
        return new Prepared(line, position, o, plan);
    }

    // The verbs whose case is named inside their study file ("case": {"path": ...} or {"builtIn": ...}), a path being read
    // relative to the study file; a folder or a .json case there is read by the native loader, anything else by the CLI's.
    private static readonly string[] CaseInStudyVerbs = ["transmission-security", "transfer-capability", "corrective-sequence", "probabilistic"];

    private static bool IsCaseFileName(string path) =>
        path.EndsWith(".m", StringComparison.OrdinalIgnoreCase) || path.EndsWith(".raw", StringComparison.OrdinalIgnoreCase)
        || path.EndsWith(".rawx", StringComparison.OrdinalIgnoreCase) || path.EndsWith(".uct", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// B33's condition on a job: whether the case its re-run will load has a term MATPOWER's makeBdc adds (an in-service tap
    /// ratio other than 0 or 1, a phase shift, or a bus Gs). The case is the job's one case argument (a case file, a case
    /// folder or a built-in name; a .json after an option is that option's settings file, not the case), or for a study verb
    /// the case its study file names (a study that names no case has no network, so false). It is read from the bytes the
    /// re-run reads (the recorded file or its snapshot copy), with the job's own <c>--allow-unmodelled</c> and
    /// <c>--psse-revision</c>. Null, with the CANNOT-REPLAY reason in
    /// <paramref name="problem"/>, when the case cannot be found or read.
    /// <c>planning-study</c> is not narrowed: its projects can add or change branches, so its master case alone does not
    /// decide B33, and B33 stays raised for it.
    /// </summary>
    private static bool? DcCaseTerms(JournalEntry e, List<JournalToken> tokens, Dictionary<(int, int?), string> replacement, string root,
        Func<string, GridSim.Core.Model.GridModel> loadCase, out string? problem)
    {
        problem = null;
        if (e.Verb == "planning-study") return true;
        bool study = CaseInStudyVerbs.Contains(e.Verb);
        bool AfterOption(JournalToken t) => t.Index > 0 && t.Index - 1 < e.Argv.Count && e.Argv[t.Index - 1].StartsWith("--", StringComparison.Ordinal);
        static bool Json(JournalToken t) => t.Abs.EndsWith(".json", StringComparison.OrdinalIgnoreCase);
        List<JournalToken> cases = study
            ? tokens.Where(t => t.Kind == "input" && t.Part is null && Json(t) && !AfterOption(t)).ToList()
            : tokens.Where(t => t.Kind is "builtin" or "case-folder"
                || (t.Kind == "input" && t.Part is null && (IsCaseFileName(t.Abs) || (Json(t) && !AfterOption(t))))).ToList();
        string what = study ? "study file" : "case";
        // n-1-1 with no case and no --dir runs its documented default, the reduced GB model (built-in name uk).
        bool defaultUk = cases.Count == 0 && e.Verb == "n-1-1";
        if (cases.Count != 1 && !defaultUk)
        {
            problem = cases.Count == 0
                ? $"input unreadable: no {what} on the command line, and B33 is decided on the job's case"
                : string.Create(Inv, $"input unreadable: {cases.Count} {what} arguments on the command line, and B33 is decided on the job's one case");
            return null;
        }
        JournalToken c = defaultUk ? new JournalToken { Kind = "builtin", Raw = "uk" } : cases[0];
        string shown = c.Kind == "builtin" ? c.Raw : c.Abs;
        string argument = c.Kind == "builtin" ? c.Raw : replacement.TryGetValue((c.Index, c.Part), out string? at) ? at : c.Abs;
        bool allowBefore = Gated.AllowUnmodelled;
        int? revisionBefore = Gated.PsseRevision;
        try
        {
            Gated.AllowUnmodelled = e.Argv.Contains("--allow-unmodelled");
            int r = e.Argv.IndexOf("--psse-revision");
            Gated.PsseRevision = r >= 0 && r + 1 < e.Argv.Count && int.TryParse(e.Argv[r + 1], NumberStyles.None, Inv, out int revision) ? revision : null;
            GridSim.Core.Model.GridModel model;
            if (!study) model = loadCase(argument);
            else
            {
                using JsonDocument d = JsonDocument.Parse(GridSim.Core.IO.Utf8Json.WithoutBom(File.ReadAllBytes(argument)));
                if (d.RootElement.ValueKind != JsonValueKind.Object) throw new InvalidDataException("the study file is not a JSON object");
                // A study with no network (a probabilistic generation adequacy study) has no DC model for B33 to move.
                if (!d.RootElement.TryGetProperty("case", out JsonElement caseEl)) return false;
                if (caseEl.ValueKind != JsonValueKind.Object) throw new InvalidDataException("the study file's case is not an object");
                if (caseEl.TryGetProperty("path", out JsonElement p) && p.ValueKind == JsonValueKind.String)
                {
                    string full = Path.GetFullPath(p.GetString()!, Path.GetDirectoryName(argument)!);
                    shown = full;
                    model = Directory.Exists(full) || full.EndsWith(".json", StringComparison.OrdinalIgnoreCase)
                        ? Gated.TransmissionNative(full, root) : loadCase(full);
                }
                else if (caseEl.TryGetProperty("builtIn", out JsonElement n) && n.ValueKind == JsonValueKind.String)
                {
                    shown = n.GetString()!;
                    model = loadCase(shown);
                }
                else throw new InvalidDataException("the study file's case gives neither a path nor a builtIn name");
            }
            return GridSim.Core.Analysis.DcPowerFlow.UsesMakeBdcTerms(model);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            problem = $"input unreadable: {shown} ({ex.Message})";
            return null;
        }
        finally
        {
            Gated.AllowUnmodelled = allowBefore;
            Gated.PsseRevision = revisionBefore;
        }
    }

    private static void Map(Plan plan, string raw, string abs, string replay, string placeholder)
    {
        if (raw.Length > 0) plan.MapRecorded.TryAdd(raw, placeholder);
        if (abs.Length > 0) plan.MapRecorded.TryAdd(abs, placeholder);
        plan.MapReplay.TryAdd(replay, placeholder);
        plan.MapReplay.TryAdd(Path.GetFullPath(replay), placeholder);
    }

    // The re-run's path of a recorded output: the rewritten output token itself, a file in a rewritten output folder, a
    // sibling named from the token's stem (the shared writer's CSV tables, a run-metadata sidecar), or a default output
    // under the working folder. Null when none applies.
    private static string? ExpectedPath(JournalOutput rec, List<JournalToken> outTokens, Dictionary<(int, int?), string> replacement, string dir)
    {
        StringComparison cmp = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        foreach (JournalToken t in outTokens)
            if (string.Equals(rec.Abs, t.Abs, cmp)) return replacement[(t.Index, null)];
        foreach (JournalToken t in outTokens)
        {
            string folder = Path.TrimEndingDirectorySeparator(t.Abs) + Path.DirectorySeparatorChar;
            if (rec.Abs.StartsWith(folder, cmp)) return Path.Combine(replacement[(t.Index, null)], rec.Abs[folder.Length..]);
        }
        // A sibling belongs to the output token of its own format first (--json r.json and --csv r.csv share the stem r).
        foreach (JournalToken t in outTokens.OrderBy(t => string.Equals(Path.GetExtension(t.Abs), Path.GetExtension(rec.Abs), cmp) ? 0 : 1))
        {
            string target = replacement[(t.Index, null)];
            string? tokenDir = Path.GetDirectoryName(t.Abs);
            string stem = Path.GetFileNameWithoutExtension(t.Abs);
            string name = Path.GetFileName(rec.Abs);
            if (tokenDir is not null && stem.Length > 0 && string.Equals(Path.GetDirectoryName(rec.Abs), tokenDir, cmp)
                && name.Length > stem.Length && name.StartsWith(stem, cmp) && name[stem.Length] is '-' or '.')
                return Path.Combine(Path.GetDirectoryName(target)!, Path.GetFileNameWithoutExtension(target) + name[stem.Length..]);
        }
        if (rec.CwdRel is not null) return Path.GetFullPath(Path.Combine(dir, rec.CwdRel));
        return null;
    }

    private static ChildResult RunChild(Plan plan)
    {
        Directory.CreateDirectory(plan.Dir);
        // The rewritten outputs live in out/; a study verb that writes its own --out does not create the folder itself.
        Directory.CreateDirectory(Path.Combine(plan.Dir, "out"));
        string exe = Environment.ProcessPath ?? throw new InvalidOperationException("the running executable is not known.");
        var psi = new ProcessStartInfo(exe)
        {
            WorkingDirectory = plan.Dir,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        if (Path.GetFileNameWithoutExtension(exe).Equals("dotnet", StringComparison.OrdinalIgnoreCase))
            psi.ArgumentList.Add(Path.Combine(AppContext.BaseDirectory, typeof(BreakingChangeCommand).Assembly.GetName().Name + ".dll"));
        foreach (string a in plan.Argv) psi.ArgumentList.Add(a);
        psi.Environment["DOTNET_PROCESSOR_COUNT"] = "1";
        psi.Environment["DOTNET_gcServer"] = "0";
        // The re-run keeps a journal of its own in its scratch folder (never the user's): it names the files it wrote.
        psi.Environment[JournalRecorder.JournalVariable] = Path.Combine(plan.Dir, "replay-journal.jsonl");
        psi.Environment[CliFiles.ReplayScratchVariable] = plan.Dir;
        psi.Environment["GRIDSIM_CRASH_DIR"] = Path.Combine(plan.Dir, "crash");
        using var p = Process.Start(psi) ?? throw new InvalidOperationException("the re-run could not be started.");
        p.StandardInput.Close();
        Task<string> stdout = p.StandardOutput.ReadToEndAsync();
        Task<string> stderr = p.StandardError.ReadToEndAsync();
        if (!p.WaitForExit((int)Math.Min(int.MaxValue, plan.TimeoutMs)))
        {
            try { p.Kill(entireProcessTree: true); } catch (InvalidOperationException) { }
            p.WaitForExit();
            return new ChildResult(-1, false, true, "");
        }
        p.WaitForExit();
        File.WriteAllText(Path.Combine(plan.Dir, "stdout.txt"), stdout.Result);
        File.WriteAllText(Path.Combine(plan.Dir, "stderr.txt"), stderr.Result);
        int code = p.ExitCode;
        bool crashed = code == unchecked((int)0xE0434352) || (!OperatingSystem.IsWindows() && code is 134 or 137 or 139);
        string first = stderr.Result.Split('\n').Select(l => l.TrimEnd('\r')).FirstOrDefault(l => l.Length > 0) ?? "";
        return new ChildResult(code, crashed, false, first);
    }

    private static void Classify(ReplayOutcome o, Plan plan, ChildResult r, string scratch, Func<int, int, IReadOnlyList<string>> exitClaims)
    {
        string Clean(string s) => s.Replace(plan.Dir, "<scratch>", StringComparison.Ordinal).Replace(scratch, "<scratch>", StringComparison.Ordinal);
        JournalEntry e = plan.Entry;
        o.ReplayExit = r.TimedOut ? null : r.ExitCode;
        void Cannot(string reason) { o.Status = ReplayStatus.CannotReplay; o.Reason = reason; }
        if (r.TimedOut) { Cannot("replay timed out"); return; }
        if (r.Crashed) { Cannot("replay crashed"); return; }
        // A re-run that newly exits 1 or 2: first the exit-code claims of the breaking changes that can explain the run (a
        // change that now refuses such a run explains it), and only then CANNOT-REPLAY. Exit 2 is a usage error for most
        // verbs, but some (stability) also report a refused run with it, so the reason does not say which.
        if (r.ExitCode is CliExit.InputError or CliExit.Usage && e.ExitCode != r.ExitCode)
        {
            IReadOnlyList<string> claimed = exitClaims(e.ExitCode, r.ExitCode);
            if (claimed.Count > 0) { BreakingChangeMap.MarkClaimedRefusal(o, e.ExitCode, r.ExitCode, claimed, Clean(r.FirstErrorLine)); return; }
            Cannot(r.ExitCode == CliExit.InputError
                ? "replay exit 1 (input): " + Clean(r.FirstErrorLine)
                : "replay exit 2 (usage error or refused run): " + Clean(r.FirstErrorLine));
            return;
        }

        VolatileFields mask = VolatileFields.Default;
        string MapA(string s) => plan.MapRecorded.TryGetValue(s, out string? v) ? v : s;
        string MapB(string s) => plan.MapReplay.TryGetValue(s, out string? v) ? v : s;
        var expected = new HashSet<string>(plan.Outputs.Select(x => Path.GetFullPath(x.Expected)), OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);
        foreach ((JournalOutput rec, string path) in plan.Outputs)
        {
            string name = Path.GetFileName(rec.Abs);
            if (!File.Exists(path))
            {
                o.Fields.Add(new FieldDifference { File = name, Field = "(file)", Kind = "removed", MinAgreeingBits = 0, Count = 1 });
                continue;
            }
            FileComparison c = ResultCompare.Compare(name, File.ReadAllBytes(rec.Abs), File.ReadAllBytes(path), e.Verb, MapA, MapB, mask);
            if (c.SchemaNotReadable is not null) { Cannot($"recorded result schema not readable by this build ({c.SchemaNotReadable})"); o.Fields.Clear(); return; }
            if (c.IndirectInputChanged is not null)
            {
                Cannot($"indirect input changed: {c.IndirectInputChanged} sha256 differs");
                o.Fields.Clear();
                return;
            }
            o.Fields.AddRange(c.Fields);
        }
        // Files the re-run wrote that the recorded run did not (from the re-run's own scratch journal).
        foreach (string extra in ReplayOutputs(plan.Dir).Where(x => !expected.Contains(x)))
            o.Fields.Add(new FieldDifference { File = Path.GetFileName(extra), Field = "(file)", Kind = "added", MinAgreeingBits = 0, Count = 1 });
        if (r.ExitCode != e.ExitCode)
        {
            o.Fields.Insert(0, new FieldDifference { File = "(run)", Field = "(exit code)", Kind = "exit", MinAgreeingBits = 0, Count = 1,
                Transition = string.Create(Inv, $"{e.ExitCode}->{r.ExitCode}") });
            o.Reason = string.Create(Inv, $"exit {e.ExitCode} -> {r.ExitCode}");
        }
        o.Status = o.Fields.Count == 0 ? ReplayStatus.Identical : ReplayStatus.Changed;
    }

    private static IEnumerable<string> ReplayOutputs(string dir)
    {
        string j = Path.Combine(dir, "replay-journal.jsonl");
        if (!File.Exists(j)) return Array.Empty<string>();
        return JournalFile.Read(j).Where(l => l.Entry is not null && l.Error is null)
            .SelectMany(l => l.Entry!.Outputs.Select(x => Path.GetFullPath(x.Abs))).ToList();
    }

    private static object? Ulps(ulong? u) => u is ulong x ? (x <= long.MaxValue ? (long)x : (double)x) : null;

    private static object? Num(double? d) => d;

    private static void FillReport(List<ReplayOutcome> outcomes, int journalEntries, string buildVersion, BreakingChangeMap map,
        IReadOnlyList<string> acceptNow, IReadOnlyList<UnboundedChanges.ReviewRow> reviews)
    {
        CliResult? rep = CliOutput.Report;
        if (rep is null) return;
        string oldest = outcomes.Where(o => o.RecordedVersion.Length > 0).Select(o => o.RecordedVersion)
            .OrderBy(v => v, Comparer<string>.Create(VersionOrder.Compare)).FirstOrDefault() ?? "";
        int changed = outcomes.Count(o => o.Status == ReplayStatus.Changed);
        int unexplained = outcomes.Count(o => o.Unexplained);
        int exceeds = outcomes.Count(o => o.Status == ReplayStatus.Changed && o.Verdict == ClaimVerdict.ExceedsClaim);
        int ambiguous = outcomes.Count(o => o.Status == ReplayStatus.Changed && o.Verdict == ClaimVerdict.Ambiguous);
        rep.Set("changed_unexplained", unexplained)
            .Set("changed_exceeds_claim", exceeds)
            .Set("changed_ambiguous", ambiguous)
            .Set("journal_entries", journalEntries)
            .Set("selected", outcomes.Count)
            .Set("identical", outcomes.Count(o => o.Status == ReplayStatus.Identical))
            .Set("changed", changed)
            .Set("changed_explained", outcomes.Count(o => o.Status == ReplayStatus.Changed && o.Verdict == ClaimVerdict.Explained))
            .Set("cannot_replay", outcomes.Count(o => o.Status == ReplayStatus.CannotReplay))
            .Set("skipped_same_build", outcomes.Count(o => o.Status == ReplayStatus.SkippedSameBuild))
            .Set("build_version", buildVersion)
            .Set("oldest_recorded_version", oldest)
            .Set("map_sha256", map.Sha256)
            .Set("changed_unbounded", outcomes.Count(o => o.Status == ReplayStatus.Changed && o.Verdict == ClaimVerdict.Unbounded))
            .Set("changed_reviewed", outcomes.Count(o => o.Status == ReplayStatus.Changed && o.Verdict == ClaimVerdict.Reviewed))
            .Set("accepted_now", string.Join(";", acceptNow));
        CliResultTable entries = rep.Table("entries", "seq", "recorded_version", "verb", "inputs", "status", "claim_check", "explained_by", "unexplained",
            "reason", "recorded_exit", "replay_exit", "fields_changed", "worst_field", "max_abs_diff", "max_rel_diff", "max_ulps", "min_agreeing_bits");
        CliResultTable fields = rep.Table("field_differences", "seq", "file", "field", "kind", "max_abs_diff", "max_rel_diff", "max_ulps", "min_agreeing_bits", "count",
            "transition", "claim_check", "explained_by", "claimed", "observed");
        foreach (ReplayOutcome o in outcomes)
        {
            FieldDifference? w = o.Worst;
            entries.Row(o.Seq, o.RecordedVersion, o.Verb, o.Inputs, o.Status, o.Status == ReplayStatus.Changed ? o.Verdict : "", string.Join(";", o.ExplainedBy), o.Unexplained,
                o.Reason ?? "", (object?)o.RecordedExit, (object?)o.ReplayExit, o.Fields.Count,
                w is null ? "" : w.File + " " + w.Field, Num(w?.MaxAbsDiff), Num(w?.MaxRelDiff), Ulps(w?.MaxUlps),
                o.Status == ReplayStatus.Changed ? w?.MinAgreeingBits : null);
            foreach (FieldDifference d in o.Fields)
                fields.Row(o.Seq, d.File, d.Field, d.Kind, Num(d.MaxAbsDiff), Num(d.MaxRelDiff), Ulps(d.MaxUlps), d.MinAgreeingBits, d.Count,
                    d.Transition ?? "", d.Verdict, d.ExplainedBy, d.Claimed, d.Observed);
        }
        CliResultTable bc = rep.Table("breaking_changes", "id", "version", "title", "entries_explained", "entries_exceeding_claim");
        foreach (BreakingChange b in map.Entries)
            bc.Row(b.Id, b.Version, b.Title,
                outcomes.Count(o => o.Status == ReplayStatus.Changed && o.Verdict == ClaimVerdict.Explained && o.ExplainedBy.Contains(b.Id)),
                outcomes.Count(o => o.Status == ReplayStatus.Changed && o.Fields.Any(f => f.Verdict is ClaimVerdict.ExceedsClaim or ClaimVerdict.Ambiguous
                    && f.ExplainedBy.Split(';').Contains(b.Id))));
        CliResultTable rv = rep.Table("reviews", "seq", "id", "outcome", "detail");
        foreach (UnboundedChanges.ReviewRow r in reviews) rv.Row(r.Seq, r.Id, r.Outcome, r.Detail);
        rep.Complete();
    }

    // ---- import ----------------------------------------------------------------------------------------------------

    private static int Import(List<string> a)
    {
        Flags f = Parse(a, ["--journal"], []);
        if (f.Positionals.Count == 0) throw new UsageError("import needs at least one result file or folder.");
        string? journal = JournalPath(f, out string? noJournal);
        if (journal is null) { Console.Error.WriteLine("error: breaking-change import: " + noJournal + "."); return CliExit.InputError; }
        var files = new List<string>();
        foreach (string p in f.Positionals)
        {
            if (PathConfinement.IsNetworkOrDevice(p)) throw new UsageError($"'{p}': a network or device path is refused.");
            string full = Path.GetFullPath(p);
            if (Directory.Exists(full)) files.AddRange(Directory.GetFiles(full, "*.json", SearchOption.AllDirectories).Order(StringComparer.Ordinal));
            else if (File.Exists(full)) files.Add(full);
            else { Console.Error.WriteLine($"error: breaking-change import: '{p}' does not exist."); return CliExit.InputError; }
        }
        CliResult? rep = CliOutput.Report;
        CliResultTable? table = rep?.Table("imported", "file", "seq", "verb", "recorded_version", "inputs_missing");
        int imported = 0, skipped = 0;
        string startCwd = Directory.GetCurrentDirectory();
        foreach (string file in files)
        {
            JournalEntry? e;
            try { e = FromResultFile(file, journal); }
            finally { Directory.SetCurrentDirectory(startCwd); }
            if (e is null) { skipped++; continue; }
            long seq = JournalFile.Append(journal, s => { e.Seq = s; return JournalJson.Line(e); });
            int missing = e.Tokens.Count(t => t.Kind == "input" && t.Sha256 is null);
            Console.WriteLine(string.Create(Inv, $"  seq {seq} {e.Verb} ({e.Version}) from {file}{(missing > 0 ? $": {missing} input(s) missing, it cannot be re-run" : "")}"));
            table?.Row(file, seq, e.Verb, e.Version, missing);
            imported++;
        }
        Console.WriteLine(string.Create(Inv, $"breaking-change import: {imported} result file(s) imported into {journal}, {skipped} file(s) skipped (not a gridsim-cli-result/1 document with its command line). Inputs are hashed now, at import, not when the run happened."));
        rep?.Set("imported", imported).Set("skipped", skipped).Complete();
        return imported > 0 ? CliExit.Ok : CliExit.InputError;
    }

    // An entry from a gridsim-cli-result/1 document: its verb, command line and exit code, with the inputs found at the
    // recorded paths hashed now. The working folder of the run is not recorded in the document; it is taken as the folder
    // that makes the document's own --json (or --out) path land on the document, else the document's folder.
    private static JournalEntry? FromResultFile(string file, string journal)
    {
        JsonDocument d;
        try { d = JsonDocument.Parse(GridSim.Core.IO.Utf8Json.WithoutBom(File.ReadAllBytes(file))); }
        catch (JsonException) { return null; }
        using (d)
        {
            JsonElement r = d.RootElement;
            if (r.ValueKind != JsonValueKind.Object || !r.TryGetProperty("schema", out JsonElement s) || s.GetString() != CliResultWriter.Schema) return null;
            if (!r.TryGetProperty("args", out JsonElement argsEl) || argsEl.ValueKind != JsonValueKind.Array) return null;
            string tool = r.TryGetProperty("tool", out JsonElement te) ? te.GetString() ?? "" : "";
            string info = tool.StartsWith("gridsim ", StringComparison.Ordinal) ? tool["gridsim ".Length..] : tool;
            var argv = argsEl.EnumerateArray().Select(x => x.GetString() ?? "").ToList();
            if (argv.Count == 0) return null;

            string cwd = Path.GetDirectoryName(file)!;
            for (int i = 0; i + 1 < argv.Count; i++)
                if (argv[i] is "--json" or "--out" && !Path.IsPathRooted(argv[i + 1]))
                {
                    string relPath = argv[i + 1].Replace('/', Path.DirectorySeparatorChar).Replace('\\', Path.DirectorySeparatorChar);
                    if (file.EndsWith(Path.DirectorySeparatorChar + relPath.TrimStart('.', Path.DirectorySeparatorChar), StringComparison.OrdinalIgnoreCase))
                        cwd = file[..^(relPath.TrimStart('.', Path.DirectorySeparatorChar).Length + 1)];
                    break;
                }
            Directory.SetCurrentDirectory(cwd);
            string journalDir = Path.GetDirectoryName(journal)!;
            var e = new JournalEntry
            {
                RecordedUtc = JournalJson.Utc(File.GetLastWriteTimeUtc(file)),
                Version = GridSim.Core.BuildInfo.SplitVersion(info),
                InformationalVersion = info,
                Commit = JournalRecorder.FullCommit(info),
                Verb = JournalRecorder.VerbOf(argv),
                Argv = argv,
                Cwd = cwd,
                ExitCode = r.TryGetProperty("exitCode", out JsonElement ec) && ec.TryGetInt32(out int x) ? x : 0,
                Gpu = argv.Any(a => a == "--gpu" || a.StartsWith("--gpu=", StringComparison.Ordinal)),
                Imported = "inputs hashed at import, not at run",
            };
            var classified = new HashSet<int>();
            foreach (CliFiles.ClassifiedToken t in CliFiles.Classify(argv))
            {
                classified.Add(t.Index);
                var token = new JournalToken { Index = t.Index, Part = t.Part, Kind = t.Kind, Raw = t.Raw, Abs = t.Full, Rel = Path.GetRelativePath(journalDir, t.Full) };
                if (t.Kind == "input")
                {
                    byte[] bytes = File.ReadAllBytes(t.Full);
                    token.Sha256 = JournalRecorder.Hash(bytes);
                    token.Bytes = bytes.LongLength;
                    // 2.1.127: the files a dynamics input names inside itself, hashed now as the input is.
                    foreach (DynamicsInputs.NamedFile nf in DynamicsInputs.Expand(t.Full, bytes))
                        if (nf.Label == DynamicsInputs.CurvesFolderLabel)
                        {
                            if (Directory.Exists(nf.Full))
                                e.CaseFiles.Add(new JournalCaseFile { Token = t.Index, Label = nf.Label, Path = nf.Written, Abs = nf.Full, Sha256 = JournalRecorder.Hash(DynamicsInputs.FolderListing(nf.Full)) });
                        }
                        else if (File.Exists(nf.Full))
                            e.CaseFiles.Add(new JournalCaseFile { Token = t.Index, Label = nf.Label, Path = nf.Written, Abs = nf.Full, Sha256 = JournalRecorder.Hash(File.ReadAllBytes(nf.Full)) });
                }
                else if (t.Kind == "case-folder")
                    foreach (string name in CaseIdentity.V1Names)
                    {
                        string cf = Path.Combine(t.Full, name);
                        if (File.Exists(cf)) e.CaseFiles.Add(new JournalCaseFile { Token = t.Index, Label = name, Path = name, Abs = cf, Sha256 = JournalRecorder.Hash(File.ReadAllBytes(cf)) });
                    }
                e.Tokens.Add(token);
            }
            // A token that looks like a file but is gone: an input that can no longer be hashed.
            for (int i = 1; i < argv.Count; i++)
            {
                string t = argv[i];
                if (classified.Contains(i) || t.StartsWith('-') || BuiltInCases.Names.Contains(t.ToLowerInvariant())) continue;
                if (i > 0 && argv[i - 1].StartsWith("--", StringComparison.Ordinal) && CliFiles.OutputFlags.Contains(argv[i - 1])) continue;
                bool looksLikeFile = Path.HasExtension(t) || t.Contains('/') || t.Contains('\\');
                if (!looksLikeFile || double.TryParse(t, NumberStyles.Float, Inv, out _)) continue;
                string full = Path.GetFullPath(t);
                e.Tokens.Add(new JournalToken { Index = i, Kind = "input", Raw = t, Abs = full, Rel = Path.GetRelativePath(journalDir, full) });
            }
            for (int i = 0; i < argv.Count; i++)
                if (!argv[i].StartsWith('-') && BuiltInCases.Names.Contains(argv[i].ToLowerInvariant()))
                    e.Tokens.Add(new JournalToken { Index = i, Kind = "builtin", Raw = argv[i] });
            e.Tokens.Sort((p, q) => p.Index != q.Index ? p.Index.CompareTo(q.Index) : (p.Part ?? -1).CompareTo(q.Part ?? -1));
            // Outputs: the document itself and every other output-flag file that is still there.
            var outs = new List<string> { Path.GetFullPath(file) };
            foreach (JournalToken t in e.Tokens.Where(t => t.Kind == "output"))
            {
                if (File.Exists(t.Abs)) outs.Add(t.Abs);
                string? dir = Path.GetDirectoryName(t.Abs);
                string stem = Path.GetFileNameWithoutExtension(t.Abs);
                if (dir is not null && Directory.Exists(dir) && t.Abs.EndsWith(".csv", StringComparison.OrdinalIgnoreCase))
                    outs.AddRange(Directory.GetFiles(dir, stem + "-*.csv").Order(StringComparer.Ordinal));
            }
            foreach (string o in outs.Distinct(OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal))
            {
                string rel = Path.GetRelativePath(cwd, o);
                bool inCwd = !Path.IsPathRooted(rel) && !rel.StartsWith("..", StringComparison.Ordinal);
                e.Outputs.Add(new JournalOutput
                {
                    Abs = o, Rel = Path.GetRelativePath(journalDir, o), CwdRel = inCwd ? rel.Replace('\\', '/') : null,
                    Sha256 = JournalRecorder.Hash(File.ReadAllBytes(o)), Bytes = new FileInfo(o).Length,
                });
            }
            string? refusal = ReplayPolicy.Refusal(e.Verb, e.Argv, JournalRecorder.CanonicalWriterVerbs);
            e.ReplayClass = refusal is null ? "safe" : "never";
            e.ReplayReason = refusal;
            return e;
        }
    }

    // ---- list ------------------------------------------------------------------------------------------------------

    private static int List(List<string> a)
    {
        Flags f = Parse(a, ["--since"], []);
        if (f.Positionals.Count > 0) throw new UsageError($"list takes no positional argument (got '{f.Positionals[0]}').");
        string? since = f.Values.GetValueOrDefault("--since");
        if (since is not null && VersionOrder.Parse(since) is null) throw new UsageError($"--since expected a release number such as 2.1.090 but got '{since}'.");
        BreakingChangeMap map = BreakingChangeMap.Default;
        var rows = map.Entries.Where(b => since is null || VersionOrder.Compare(b.Version, since) > 0).ToList();
        var sb = new StringBuilder();
        sb.Append(string.Create(Inv, $"breaking-change map {map.Sha256[..12]}: {rows.Count} breaking change(s){(since is null ? "" : " after " + since)}\n"));
        foreach (BreakingChange b in rows)
        {
            string scope = string.Join(" ", new[]
            {
                b.Verbs.Count > 0 ? "verbs " + string.Join(",", b.Verbs) : null,
                b.Options.Count > 0 ? "options " + string.Join(",", b.Options) : null,
                b.InputFormats.Count > 0 ? "formats " + string.Join(",", b.InputFormats) : null,
            }.Where(x => x is not null));
            sb.Append(string.Create(Inv, $"  {b.Id,-4} {b.Version} {b.Kind,-9} {b.Title} [{(scope.Length == 0 ? "no command-line route" : scope)}]\n"));
        }
        Console.Write(sb.ToString());
        CliResult? rep = CliOutput.Report;
        if (rep is not null)
        {
            rep.Set("map_sha256", map.Sha256).Set("entries", rows.Count);
            CliResultTable t = rep.Table("breaking_changes", "id", "version", "kind", "title", "verbs", "options", "input_formats", "condition", "magnitude", "changelog");
            foreach (BreakingChange b in rows)
                t.Row(b.Id, b.Version, b.Kind, b.Title, string.Join(";", b.Verbs), string.Join(";", b.Options), string.Join(";", b.InputFormats), b.Condition, b.Magnitude, b.Changelog);
            rep.Complete();
        }
        return CliExit.Ok;
    }
}
