using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;
using GridSim.Core.Journal;

namespace GridSim.Cli;

/// <summary>
/// RC5: records a command-line run in the run journal (<c>gridsim-journal/1</c>, technical chapter 42), when the journal
/// is switched on (<c>--journal &lt;file&gt;</c>, <c>GRIDSIM_JOURNAL</c> or the user config file). Off by default.
///
/// The journal never changes a run. <see cref="Take"/> removes <c>--journal</c> and <c>--journal-snapshot</c> from the
/// command line before anything reads it, so the output policy, result files (their <c>args</c>) and crash logs never
/// see them. The entry is written once, when the process exits (a normal end, <c>Environment.Exit</c>, or an unhandled
/// exception with the runtime's crash code). Nothing is printed when it is written; a failure prints one
/// <c>warning: journal: ...</c> line on stderr and leaves the exit code alone.
/// </summary>
internal static class JournalRecorder
{
    /// <summary>Overrides the per-user config file's location (as GRIDSIM_LICENCE_FILE does the licence file).</summary>
    public const string ConfigVariable = "GRIDSIM_JOURNAL_CONFIG";

    /// <summary>The variable that switches the journal on (a path) or off ("off").</summary>
    public const string JournalVariable = "GRIDSIM_JOURNAL";

    private static JournalSettings? _settings;
    private static string[] _argv = Array.Empty<string>();
    private static string _verb = "";
    private static string _cwd = "";
    private static readonly List<JournalToken> Tokens = new();
    private static readonly List<JournalCaseFile> CaseFiles = new();
    private static DateTime _startUtc;
    private static int _done;

    /// <summary>
    /// Take the journal switches off the command line (both <paramref name="args"/> and <paramref name="rest"/>, which
    /// must still be equal) and decide whether this run is journaled. Exits 2 on a bad <c>--journal</c>. Returns the
    /// command line without the switches. <c>breaking-change</c> has a <c>--journal</c> of its own and is never
    /// journaled, so its command line is returned as it is.
    /// </summary>
    public static string[] Take(string[] args, List<string> rest)
    {
        if (args.Length > 0 && args[0].Equals("breaking-change", StringComparison.OrdinalIgnoreCase)) return args;
        string? flagPath = null;
        bool flagGiven = false, snapshot = false;
        var kept = new List<string>(args.Length);
        for (int i = 0; i < args.Length; i++)
        {
            string t = args[i];
            if (t == "--journal" || t.StartsWith("--journal=", StringComparison.Ordinal))
            {
                if (flagGiven) Fail("--journal given more than once; a run is recorded in one journal.");
                flagGiven = true;
                if (t.Length > "--journal".Length) flagPath = t["--journal=".Length..];
                else flagPath = i + 1 < args.Length && !args[i + 1].StartsWith("--", StringComparison.Ordinal) ? args[++i] : "";
                continue;
            }
            if (t == "--journal-snapshot") { snapshot = true; continue; }
            kept.Add(t);
        }
        if (kept.Count == args.Length) args = args.ToArray();
        else args = kept.ToArray();
        rest.Clear();
        rest.AddRange(args);

        string? configPath = Environment.GetEnvironmentVariable(ConfigVariable);
        if (string.IsNullOrWhiteSpace(configPath)) configPath = JournalConfig.DefaultConfigPath();
        JournalSettings? settings = JournalConfig.Resolve(flagGiven ? flagPath ?? "" : null, snapshot,
            Environment.GetEnvironmentVariable(JournalVariable), configPath, out string? error, out string? warning);
        if (error is not null) Fail(error);
        if (warning is not null) Console.Error.WriteLine("warning: journal: " + warning);
        if (settings is null)
        {
            if (snapshot && warning is null)
                Console.Error.WriteLine("warning: --journal-snapshot had no effect: this run is not journaled.");
            return args;
        }
        if (!Journaled(rest)) return args;
        _settings = settings;
        _argv = args;
        return args;
    }

    private static void Fail(string message)
    {
        Console.Error.WriteLine("error: " + message);
        Environment.Exit(CliExit.Usage);
    }

    // Not journaled: the version, help, the licence verb and import, breaking-change itself and the interactive live view.
    private static bool Journaled(List<string> rest)
    {
        if (rest.Count == 0) return false;
        if (rest.Contains("--version") || rest.Contains("--help") || rest.Contains("-h")) return false;
        if (rest.Any(t => t == "--importLicence" || t.StartsWith("--importLicence=", StringComparison.Ordinal))) return false;
        string first = rest[0].ToLowerInvariant();
        return first is not ("help" or "licence" or "breaking-change");
    }

    /// <summary>
    /// The canonical verb of a command line, or "run" for the no-verb case run. Leading global switches are skipped
    /// (<c>gridsim --cpu dcopf case9</c> is a dcopf run).
    /// </summary>
    public static string VerbOf(IReadOnlyList<string> argv)
    {
        int i = 0;
        while (i < argv.Count)
        {
            string t = argv[i];
            if (t is "--cpu" or "--allow-unmodelled" or "--dyr-extended" or "--tap-control" or "--shunt-control" or "--area-control" or "--remote-vreg"
                || t == "--gpu" || t.StartsWith("--gpu=", StringComparison.Ordinal)) { i++; continue; }
            if (t is "--frequency-hz" or "--psse-revision" or "--seq-data") { i += 2; continue; }
            break;
        }
        if (i < argv.Count && !argv[i].StartsWith('-') && VerbHelp.HasTopic(VerbHelp.LoadEmbedded(), argv[i]))
            return CliOutput.Canonical(argv[i]);
        return "run";
    }

    /// <summary>The shared writer's verbs by canonical name.</summary>
    public static IReadOnlySet<string> CanonicalWriterVerbs { get; } =
        CliOutput.WriterVerbs.Select(CliOutput.Canonical).ToHashSet(StringComparer.Ordinal);

    /// <summary>
    /// Called once the process-wide exception handler is in place: hash (and, with the snapshot store, keep) the
    /// run's inputs, and register the writers of the entry. Does nothing when the run is not journaled.
    /// </summary>
    public static void Start(string baseDir)
    {
        JournalSettings? s = _settings;
        if (s is null) return;
        try
        {
            _startUtc = Process.GetCurrentProcess().StartTime.ToUniversalTime();
            _cwd = Directory.GetCurrentDirectory();
            _verb = VerbOf(_argv);
            Collect(baseDir, s);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException or ArgumentException
            or NotSupportedException or System.ComponentModel.Win32Exception)
        {
            Console.Error.WriteLine($"warning: journal: the run's inputs could not be recorded ({ex.Message}); this run is not journaled.");
            _settings = null;
            return;
        }
        AppDomain.CurrentDomain.ProcessExit += (_, _) => Write(Environment.ExitCode);
        // Registered after the process-wide handler: a known exception there exits through ProcessExit (written above);
        // a defect returns from it and reaches this one, which records the runtime's crash code.
        AppDomain.CurrentDomain.UnhandledException += (_, _) => Write(OperatingSystem.IsWindows() ? unchecked((int)0xE0434352) : 134);
    }

    private static string JournalDir => Path.GetDirectoryName(_settings!.Path)!;

    private static string Rel(string abs) => Path.GetRelativePath(JournalDir, abs);

    private static void Collect(string baseDir, JournalSettings s)
    {
        string objects = JournalSnapshot.ObjectsDir(s.Path);
        var classified = new HashSet<int>();
        foreach (CliFiles.ClassifiedToken t in CliFiles.Classify(_argv))
        {
            classified.Add(t.Index);
            var token = new JournalToken { Index = t.Index, Part = t.Part, Kind = t.Kind, Raw = t.Raw, Abs = t.Full, Rel = Rel(t.Full) };
            if (t.Kind == "input")
            {
                byte[] bytes = File.ReadAllBytes(t.Full);
                token.Sha256 = Hash(bytes);
                token.Bytes = bytes.LongLength;
                if (s.Snapshot) Keep(objects, token.Sha256, bytes, skip => { token.Snapshot = skip is null; token.SnapshotSkipped = skip; });
                if (t.Full.EndsWith(".json", StringComparison.OrdinalIgnoreCase)) AddManifestFiles(t.Index, t.Full, objects, s.Snapshot);
                // 2.1.127: the files a dynamics input names inside itself (a study's RAW, DYR, unit protection, devices and
                // curves; a model assignments sidecar's user model files; a plug-in configuration's assemblies). Hashed,
                // and kept in the snapshot store when it is on, except a plug-in assembly, which is code and is never kept.
                foreach (DynamicsInputs.NamedFile f in DynamicsInputs.Expand(t.Full, bytes))
                {
                    if (f.Label == DynamicsInputs.CurvesFolderLabel)
                    {
                        // 2.1.130: the curves folder by its file list (never in the store).
                        if (Directory.Exists(f.Full))
                            CaseFiles.Add(new JournalCaseFile { Token = t.Index, Label = f.Label, Path = f.Written, Abs = f.Full, Sha256 = Hash(DynamicsInputs.FolderListing(f.Full)) });
                    }
                    else if (File.Exists(f.Full)) AddCaseFile(t.Index, f.Label, f.Written, f.Full, objects, s.Snapshot && f.Snapshot);
                }
            }
            else if (t.Kind == "case-folder")
            {
                foreach (string name in CaseIdentity.V1Names)
                {
                    string f = Path.Combine(t.Full, name);
                    if (File.Exists(f)) AddCaseFile(t.Index, name, name, f, objects, s.Snapshot);
                }
            }
            Tokens.Add(token);
        }
        // A bundled case name: part of the build, recorded with the build's bundled-data manifest hash as provenance.
        string manifest = Path.Combine(baseDir, "data", "bundled.sha256");
        string? manifestSha = File.Exists(manifest) ? Hash(File.ReadAllBytes(manifest)) : null;
        for (int i = 0; i < _argv.Length; i++)
        {
            if (classified.Contains(i) || _argv[i].StartsWith('-')) continue;
            if (!BuiltInCases.Names.Contains(_argv[i].ToLowerInvariant())) continue;
            Tokens.Add(new JournalToken { Index = i, Kind = "builtin", Raw = _argv[i], Abs = "", Rel = "", Sha256 = manifestSha });
        }
        Tokens.Sort((a, b) => a.Index != b.Index ? a.Index.CompareTo(b.Index) : (a.Part ?? -1).CompareTo(b.Part ?? -1));
    }

    // A v2 manifest (gridsim-case/2) names its network and asset files relative to its folder: they are inputs too.
    private static void AddManifestFiles(int token, string manifest, string objects, bool snapshot)
    {
        JsonDocument d;
        try { d = JsonDocument.Parse(GridSim.Core.IO.Utf8Json.WithoutBom(File.ReadAllBytes(manifest))); }
        catch (JsonException) { return; }
        using (d)
        {
            JsonElement r = d.RootElement;
            if (r.ValueKind != JsonValueKind.Object || !r.TryGetProperty("schema", out JsonElement sc) || sc.ValueKind != JsonValueKind.String
                || !(sc.GetString() ?? "").StartsWith("gridsim-case/", StringComparison.Ordinal)) return;
            string dir = Path.GetDirectoryName(manifest)!;
            foreach (string kind in new[] { "network", "assets" })
                if (r.TryGetProperty(kind, out JsonElement list) && list.ValueKind == JsonValueKind.Array)
                    foreach (JsonElement item in list.EnumerateArray())
                    {
                        if (item.ValueKind != JsonValueKind.String) continue;
                        string rel = item.GetString()!;
                        string full;
                        try { full = CaseIdentity.Inside(dir, rel); }
                        catch (InvalidDataException) { continue; }
                        if (File.Exists(full)) AddCaseFile(token, kind + "/" + rel, rel, full, objects, snapshot);
                    }
        }
    }

    private static void AddCaseFile(int token, string label, string path, string full, string objects, bool snapshot)
    {
        byte[] bytes = File.ReadAllBytes(full);
        var c = new JournalCaseFile { Token = token, Label = label, Path = Path.IsPathRooted(path) ? path : path.Replace('\\', '/'), Abs = full, Sha256 = Hash(bytes) };
        if (snapshot) Keep(objects, c.Sha256, bytes, skip => c.Snapshot = skip is null);
        CaseFiles.Add(c);
    }

    private static void Keep(string objects, string sha, byte[] bytes, Action<string?> done) => done(JournalSnapshot.Put(objects, sha, bytes));

    internal static string Hash(byte[] bytes) => Convert.ToHexStringLower(SHA256.HashData(bytes));

    private static void Write(int exitCode)
    {
        if (Interlocked.Exchange(ref _done, 1) != 0) return;
        JournalSettings? s = _settings;
        if (s is null) return;
        try
        {
            JournalEntry e = Build(exitCode, s);
            JournalFile.Append(s.Path, seq => { e.Seq = seq; return JournalJson.Line(e); }, s.MaxSegmentBytes);
        }
        catch (Exception ex)
        {
            try { Console.Error.WriteLine("warning: journal: this run was not recorded: " + ex.Message); }
            catch (IOException) { }
        }
    }

    private static JournalEntry Build(int exitCode, JournalSettings s)
    {
        var e = new JournalEntry
        {
            RecordedUtc = JournalJson.Utc(DateTime.UtcNow),
            Version = GridSim.Core.BuildInfo.Version,
            InformationalVersion = GridSim.Core.BuildInfo.InformationalVersion,
            Commit = FullCommit(GridSim.Core.BuildInfo.InformationalVersion),
            BuildStatus = GridSim.Core.Diagnostics.SupplyChain.BuildStatus.Current,
            Verb = _verb,
            Argv = _argv.ToList(),
            Cwd = _cwd,
            ExitCode = exitCode,
            Backend = RunInfo.Backend,
            Gpu = _argv.Any(a => a == "--gpu" || a.StartsWith("--gpu=", StringComparison.Ordinal)),
        };
        e.Tokens.AddRange(Tokens);
        e.CaseFiles.AddRange(CaseFiles);
        using (var p = Process.GetCurrentProcess())
        {
            e.ElapsedMs = (long)Math.Max(0, (DateTime.UtcNow - _startUtc).TotalMilliseconds);
            e.PeakWorkingSetBytes = p.PeakWorkingSet64;
        }
        string? refusal = ReplayPolicy.Refusal(e.Verb, e.Argv, CanonicalWriterVerbs);
        e.ReplayClass = refusal is null ? "safe" : "never";
        e.ReplayReason = refusal;
        foreach (string path in OutputPaths(s.Path))
        {
            var info = new FileInfo(path);
            if (!info.Exists) continue;
            string rel = Path.GetRelativePath(_cwd, path);
            bool inCwd = !Path.IsPathRooted(rel) && rel != ".." && !rel.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal)
                && !rel.StartsWith("../", StringComparison.Ordinal);
            e.Outputs.Add(new JournalOutput
            {
                Abs = path, Rel = Rel(path), CwdRel = inCwd ? rel.Replace('\\', '/') : null,
                Sha256 = Hash(File.ReadAllBytes(path)), Bytes = info.Length,
            });
        }
        return e;
    }

    /// <summary>The commit part of an informational version (the whole sha after '+'), or "unknown".</summary>
    public static string FullCommit(string informationalVersion)
    {
        int plus = informationalVersion.IndexOf('+', StringComparison.Ordinal);
        return plus < 0 || plus == informationalVersion.Length - 1 ? "unknown" : informationalVersion[(plus + 1)..];
    }

    // Every output: the files passed through the output policy (in call order), each output-flag value that is a file,
    // and the files written into an output-flag folder during the run. The journal's own files are never outputs.
    private static IEnumerable<string> OutputPaths(string journalPath)
    {
        var comparer = OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;
        var seen = new HashSet<string>(comparer);
        string journalDir = Path.GetDirectoryName(Path.GetFullPath(journalPath))!;
        var list = new List<string>();
        void Add(string p)
        {
            string full = Path.GetFullPath(p);
            if (Path.GetDirectoryName(full) is string d && comparer.Equals(d, journalDir)
                && Path.GetFileName(full).StartsWith(Path.GetFileNameWithoutExtension(journalPath), StringComparison.Ordinal)) return;
            if (seen.Add(full)) list.Add(full);
        }
        foreach (string w in CliFiles.Written) Add(w);
        foreach (JournalToken t in Tokens.Where(t => t.Kind == "output"))
        {
            if (File.Exists(t.Abs)) Add(t.Abs);
            else if (Directory.Exists(t.Abs))
                foreach (string f in Directory.EnumerateFiles(t.Abs, "*", SearchOption.AllDirectories).Order(StringComparer.Ordinal).Take(10_000))
                    if (File.GetLastWriteTimeUtc(f) >= _startUtc.AddSeconds(-2)) Add(f);
        }
        return list;
    }
}
