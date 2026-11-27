namespace GridSim.Cli;

/// <summary>
/// D-DOC-072 (October 2026, docs-session finding): the ONE overwrite policy of the command line, and the one helper every
/// CLI output goes through. Before, two sites used <see cref="GridSim.Core.IO.NeverOverwrite"/> (refuse, write under a
/// "-dupN" name), the convert verb refused an existing file, the shared result writer replaced, and about 37 sites in
/// Program.cs simply opened the file and wrote over whatever was there, including, with the wrong <c>--out</c>, the very
/// case file or measurement set the run had just read. The rule is now written down once (user manual ch. 19, "Output
/// files") and enforced here:
///
/// <list type="number">
/// <item>An output goes where it is asked to go (or under <c>results/</c> by default), and its parent folder is created
/// when it is missing (D-DOC-176: <c>stability --export</c> and <c>trip --export</c> used to fail on a missing folder).</item>
/// <item>An existing OUTPUT file is replaced. A re-run replaces its own outputs, whether default (under
/// <c>results/</c>) or user-named (<c>--out</c>, <c>--export</c>, <c>--csv</c>, ...): naming a file is asking for it,
/// scripts re-run with the same paths, and the shared result writer (F10) has always replaced.</item>
/// <item>NEVER an input. A target is refused, before anything is written, exit 2 (a usage error: the command line names
/// a wrong path), when it is (a) a file the command line names as an input (any existing file named by a token that is
/// not the value of an output flag), (b) an existing file inside a case folder the command line names (a folder holding
/// <c>system.json</c>), (c) an existing file inside the bundled <c>data/</c> folder next to the executable (the
/// built-in cases and fixtures), or (d) an existing folder.</item>
/// </list>
///
/// Two deliberate exceptions keep their own, stricter rules and are documented beside the rule: the interposer's window
/// files go through <see cref="GridSim.Core.IO.NeverOverwrite"/> (each window is distinct evidence, so a clash is kept
/// under a "-dupN" name, never replaced), and the case writers (<c>convert</c> and the JSON case writer) never write over
/// an existing case. <c>synth</c> regenerates its case folder (by default the bundled <c>data/gb-synthetic</c>) by design.
///
/// <para>This file has no dependency on the rest of the CLI, so the test project compiles it in and tests the rule
/// directly (CliFilesTests), beside the process-level tests that run the real binary.</para>
/// </summary>
internal static class CliFiles
{
    /// <summary>
    /// The flags whose value is an OUTPUT path (a file or a run folder). Their values are never taken as inputs, so an
    /// existing output from an earlier run can be replaced. Everything else on the command line that names an existing
    /// file is an input. A flag that is missing from this list errs on the safe side: its existing target is refused
    /// (exit 2) rather than written over.
    /// </summary>
    public static readonly IReadOnlySet<string> OutputFlags = new HashSet<string>(StringComparer.Ordinal)
    {
        "--out", "--export", "--csv", "--json", "--pv-out", "--out-store", "--dump-catalog", "--enableExport",
        "--audit-log", "--audit-anchor",
    };

    /// <summary>How paths compare: case-insensitively on Windows (as its file system does), exactly elsewhere.</summary>
    private static readonly StringComparer PathComparer =
        OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;

    private static readonly object Gate = new();
    private static HashSet<string> _inputFiles = new(PathComparer);
    private static List<(string Folder, string Why)> _protectedFolders = new();
    private static List<string> _written = new();
    private static HashSet<string> _writtenSet = new(PathComparer);

    /// <summary>
    /// RC5 (run journal): set in a re-run started by <c>breaking-change validate</c> to that re-run's scratch folder.
    /// While it is set, <see cref="Output"/> also refuses any target outside it. Unset (every ordinary run) the check
    /// does nothing.
    /// </summary>
    public const string ReplayScratchVariable = "GRIDSIM_REPLAY_SCRATCH";

    /// <summary>
    /// Record the run's inputs from its command line, once, before any verb runs. <paramref name="args"/> is the whole
    /// command line; <paramref name="protectedFolders"/> are folders whose existing files are always inputs (the bundled
    /// <c>data/</c> folder). Calling it again replaces what an earlier call recorded (the tests use that).
    /// </summary>
    public static void Init(IReadOnlyList<string> args, params string[] protectedFolders)
    {
        ArgumentNullException.ThrowIfNull(args);
        var files = new HashSet<string>(PathComparer);
        var folders = new List<(string, string)>();
        foreach (string f in protectedFolders ?? Array.Empty<string>())
            if (!string.IsNullOrWhiteSpace(f))
                folders.Add((WithSeparator(Path.GetFullPath(f)), "it is a file of the bundled data folder (the built-in cases and fixtures)"));

        // RC5 (run journal): the classification is shared with the journal, which records the same inputs, case folders
        // and outputs. The inputs and case folders recorded here are exactly what this method has always recorded.
        foreach (ClassifiedToken t in Classify(args))
        {
            if (t.Kind == "input") files.Add(t.Full);
            else if (t.Kind == "case-folder")
                folders.Add((WithSeparator(t.Full), $"it is a file of the case folder '{t.Full}' that this run reads"));
        }

        lock (Gate)
        {
            _inputFiles = files;
            _protectedFolders = folders;
            _written = new List<string>();
            _writtenSet = new HashSet<string>(PathComparer);
        }
    }

    /// <summary>One classified command-line token (see <see cref="Classify"/>).</summary>
    /// <param name="Index">The token's index in the command line.</param>
    /// <param name="Part">The piece's position in a comma list, or null for a whole token.</param>
    /// <param name="Raw">The token as written (for --flag=value, the value; for a comma list, the piece).</param>
    /// <param name="Full">The token resolved against the working folder.</param>
    /// <param name="Kind">"input" (an existing file), "case-folder" (a folder holding system.json) or "output" (the value
    /// of an output flag).</param>
    internal readonly record struct ClassifiedToken(int Index, int? Part, string Raw, string Full, string Kind);

    /// <summary>
    /// The command line's tokens by what they are to the output policy: every existing file it names (other than the
    /// value of an output flag) is an input, every folder holding <c>system.json</c> is a case folder, and the value of
    /// each output flag is an output. Pure apart from reading the file system; other tokens are not listed.
    /// </summary>
    internal static List<ClassifiedToken> Classify(IReadOnlyList<string> args)
    {
        ArgumentNullException.ThrowIfNull(args);
        var list = new List<ClassifiedToken>();
        for (int i = 0; i < args.Count; i++)
        {
            string tok = args[i];
            if (string.IsNullOrEmpty(tok)) continue;
            int at = i;
            if (tok.StartsWith("--", StringComparison.Ordinal))
            {
                int eq = tok.IndexOf('=', StringComparison.Ordinal);
                string flag = eq >= 0 ? tok[..eq] : tok;
                if (OutputFlags.Contains(flag))
                {
                    // The value of an output flag is an output: skip it ("--out x" or "--out=x"). A flag followed by
                    // another flag has no value here (modal's --json, for example), so nothing is skipped.
                    string? value = null;
                    if (eq >= 0) value = tok[(eq + 1)..];
                    else if (i + 1 < args.Count && !args[i + 1].StartsWith("--", StringComparison.Ordinal)) value = args[++i];
                    if (!string.IsNullOrEmpty(value) && FullOrNull(value) is string outFull)
                        list.Add(new ClassifiedToken(eq >= 0 ? at : i, null, value, outFull, "output"));
                    continue;
                }
                if (eq < 0) continue;   // a plain flag; its value (if any) is the next token, examined on its own
                tok = tok[(eq + 1)..];  // --flag=value: the value may name an input
            }
            // A comma list (bench --cases a,b,c; transient --cases) names several cases.
            string[] pieces = tok.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            for (int k = 0; k < pieces.Length; k++)
            {
                if (FullOrNull(pieces[k]) is not string full) continue;
                int? part = pieces.Length > 1 ? k : null;
                if (File.Exists(full)) list.Add(new ClassifiedToken(at, part, pieces[k], full, "input"));
                else if (Directory.Exists(full) && File.Exists(Path.Combine(full, "system.json")))
                    list.Add(new ClassifiedToken(at, part, pieces[k], full, "case-folder"));
            }
        }
        return list;
    }

    private static string? FullOrNull(string token)
    {
        try { return Path.GetFullPath(token); }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException) { return null; }
    }

    /// <summary>The input files recorded by <see cref="Init"/> (full paths).</summary>
    public static IReadOnlyCollection<string> InputFiles
    {
        get { lock (Gate) return _inputFiles.ToArray(); }
    }

    /// <summary>The protected folders recorded by <see cref="Init"/> (full paths ending in a separator) and why.</summary>
    public static IReadOnlyList<(string Folder, string Why)> ProtectedFolders
    {
        get { lock (Gate) return _protectedFolders.ToArray(); }
    }

    /// <summary>
    /// RC5 (run journal): every output path this run has passed through <see cref="Output"/>, in call order, each once.
    /// The journal hashes them when the run ends. Additive: nothing reads it to decide anything about the run.
    /// </summary>
    public static IReadOnlyList<string> Written
    {
        get { lock (Gate) return _written.ToArray(); }
    }

    private static string WithSeparator(string folder) =>
        folder.EndsWith(Path.DirectorySeparatorChar) || folder.EndsWith(Path.AltDirectorySeparatorChar)
            ? folder
            : folder + Path.DirectorySeparatorChar;

    /// <summary>
    /// A verb whose OUTPUT is a positional token (not the value of an output flag) declares it here once it has checked
    /// that token really is its own earlier output (for example <c>export</c>, whose free <c>.json</c> token is the scene
    /// path and which has already refused any existing file that is not a gridviz scene). Init could not tell, so it had
    /// recorded the existing file as an input and the re-run was refused. Only that one file stops being an input: the
    /// protected folders (a named case folder, the bundled data folder) and the existing-folder rule still apply.
    /// </summary>
    public static void DeclareOutput(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        string full = Path.GetFullPath(path);
        lock (Gate)
        {
            if (!_inputFiles.Contains(full)) return;
            var files = new HashSet<string>(_inputFiles, PathComparer);
            files.Remove(full);
            _inputFiles = files;
        }
    }

    /// <summary>
    /// Why <paramref name="path"/> may not be written by this run, or null when it may (rule 3 above). Pure: it neither
    /// creates nor touches anything.
    /// </summary>
    public static string? WhyRefused(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        string full = Path.GetFullPath(path);
        if (Directory.Exists(full)) return "it is an existing folder, not a file";
        HashSet<string> files;
        List<(string Folder, string Why)> folders;
        lock (Gate) { files = _inputFiles; folders = _protectedFolders; }
        if (files.Contains(full)) return "it is an input of this run (named on the command line)";
        if (File.Exists(full))
            foreach ((string folder, string why) in folders)
                if (full.StartsWith(folder, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))
                    return why;
        return null;
    }

    /// <summary>
    /// Check an output file against the policy and get it ready: the full path, with its parent folder created. Throws
    /// <see cref="OutputRefusedException"/> (exit 2 through the process-wide handler) when the target is refused.
    /// </summary>
    /// <param name="path">The output file, as given or defaulted.</param>
    /// <param name="what">What the file is, in plain words, for the refusal message ("bench report", "solve export").</param>
    public static string Output(string path, string what)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        string full = Path.GetFullPath(path);
        string? why = WhyRefused(full);
        if (why is not null)
            throw new OutputRefusedException(
                $"refusing to write the {what} to '{full}': {why}. An output never replaces an input; name another path.");
        RefuseOutsideReplayScratch(full, what);
        string? dir = Path.GetDirectoryName(full);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
        lock (Gate)
        {
            if (_writtenSet.Add(full)) _written.Add(full);
        }
        return full;
    }

    /// <summary>
    /// Check an output RUN FOLDER and create it: refused only when it is an existing file. The files written into it go
    /// through <see cref="Output"/> one by one, so an existing case file inside it is still never replaced.
    /// </summary>
    public static string OutputFolder(string dir, string what)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(dir);
        string full = Path.GetFullPath(dir);
        if (File.Exists(full))
            throw new OutputRefusedException($"refusing to use '{full}' as the {what}: it is an existing file, not a folder.");
        RefuseOutsideReplayScratch(full, what);
        Directory.CreateDirectory(full);
        return full;
    }

    /// <summary>
    /// RC5 (2.1.127): the re-run guard alone, for a verb that writes its result file itself rather than through
    /// <see cref="Output"/> (dynamics-study, long-term). Inert unless GRIDSIM_REPLAY_SCRATCH is set, so an ordinary run is
    /// exactly as before; in a re-run started by <c>breaking-change validate</c> it refuses a target outside the scratch
    /// folder (<see cref="OutputRefusedException"/>, exit 2), as <see cref="Output"/> does.
    /// </summary>
    public static void ReplayGuard(string path, string what)
    {
        if (string.IsNullOrEmpty(Environment.GetEnvironmentVariable(ReplayScratchVariable))) return;
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        RefuseOutsideReplayScratch(Path.GetFullPath(path), what);
    }

    // RC5 (run journal): defence in depth for a re-run. Inert unless GRIDSIM_REPLAY_SCRATCH is set.
    private static void RefuseOutsideReplayScratch(string full, string what)
    {
        string? scratch = Environment.GetEnvironmentVariable(ReplayScratchVariable);
        if (string.IsNullOrEmpty(scratch)) return;
        string? refusal = GridSim.Core.Security.PathConfinement.Refusal(full, full, scratch);
        if (refusal is not null)
            throw new OutputRefusedException(
                $"refusing to write the {what} to '{full}': a re-run writes inside its scratch folder only ({refusal}).");
    }

    /// <summary>
    /// CLI-04: the report file of a verb that also writes its table beside it as <c>&lt;report&gt;.csv</c>
    /// (<see cref="Path.ChangeExtension(string?, string?)"/>). A report named <c>x.csv</c> would make both writers open the
    /// same file (a sharing violation on Windows, one corrupt file elsewhere), so it is refused (exit 2) before anything is
    /// opened. Otherwise the same as <see cref="Output"/>.
    /// </summary>
    public static string ReportWithCsv(string path, string what)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        string full = Path.GetFullPath(path);
        // R3-07: compared without regard to case on every system (macOS volumes ignore case; refusing more is safe).
        if (string.Equals(full, Path.GetFullPath(Path.ChangeExtension(path, ".csv")), StringComparison.OrdinalIgnoreCase))
            throw new OutputRefusedException($"refusing to write the {what} to '{full}': its table is written beside it as <report>.csv, which is this same file. Name the report with another extension (for example .md).");
        return Output(path, what);
    }

    /// <summary>CLI-02: the output of this run (a path passed through <see cref="Output"/>) that <paramref name="message"/>
    /// names, or null when it names none.</summary>
    internal static string? WrittenPathIn(string message)
    {
        ArgumentNullException.ThrowIfNull(message);
        foreach (string w in Written)
            if (message.Contains(w, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal)) return w;
        return null;
    }

    /// <summary>Open an output text file through the policy (UTF-8 without a BOM, as before).</summary>
    public static StreamWriter CreateText(string path, string what) => new(Output(path, what));

    /// <summary>Write a whole output text file through the policy (UTF-8 without a BOM, as before).</summary>
    public static void WriteAllText(string path, string contents, string what) =>
        File.WriteAllText(Output(path, what), contents);
}

/// <summary>
/// D-DOC-072: an output the policy refuses (<see cref="CliFiles"/>): it would replace an input of the run, or it names a
/// folder. Not an <see cref="IOException"/>, so no "could not write" handler swallows it: it reaches the process-wide
/// handler, which prints <c>error: ...</c> and exits 2 (a usage error) before anything is written to that path.
/// </summary>
internal sealed class OutputRefusedException : Exception
{
    public OutputRefusedException(string message) : base(message) { }
}
