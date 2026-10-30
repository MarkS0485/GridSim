using System.Globalization;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;

namespace GridSim.Cli;

// ---------------------------------------------------------------------------------------------------------------------
// F10 (October 2026): ONE shared result writer for the console-only verbs.
//
// Before this, about twenty verbs (dcopf, acopf, cpf, taps, ptdf, lodf, iec60909, fault, protection-check, grade, the
// contingency verbs, cycle, freq, domains, dist3ph, losses, physics-check, experimental, plus dcpf / convert / validate)
// could only print to the console, so a script had to scrape fixed-width text to use their numbers. Each of them now
// accepts the same three flags:
//
//   --json <file.json>          the whole result as one JSON document (schema "gridsim-cli-result/1");
//   --csv <file.csv>            the result as CSV: the verb's main table in the named file, every other table beside it
//                               as <stem>-<table>.csv, and the scalar summary as <stem>-summary.csv;
//   --out <file.json|file.csv>  the same, with the format chosen by the extension.
//
// How it fits together:
//
//   * CliOutput.TakeFlags (called once in Program.cs, before the verb dispatch) removes the three flags from the
//     argument list, checks them, and - only when at least one was given - creates CliOutput.Report. With no flag the
//     report is null, every verb's "rep?.Set(...)" line does nothing (the null-conditional operator does not even
//     evaluate the arguments), and the run is exactly what it was.
//   * Each verb fills the report from the very values it prints: a scalar summary (key, value) and named tables. It
//     never computes anything new for the report, so no number can change, and the console text is untouched.
//   * When the verb has reached the end of its result it calls Complete(). Program.cs then calls CliOutput.Finish
//     after the verb returns, which writes the requested files. A run that stopped early (a usage error, a case that
//     does not load, a solve that did not converge, so there is no result) never calls Complete(), and Finish says on
//     stderr that no file was written. A finished result whose verdict is a failure (a NOT SECURE contingency screen,
//     a physics gate FAIL, an INFEASIBLE DC-OPF) IS written, with the run's exit code inside, so a script can read why.
//   * Every message the writer prints goes to stderr, so stdout is byte-identical with or without the flags.
//
// Numbers: every double is written in round-trip ("R") form, invariant culture, so parsing the file gives back the
// very same bits (no rounding to the console's display precision). JSON has no NaN or Infinity, so those three values
// are written as the strings "NaN", "Infinity" and "-Infinity" (the same spelling System.Text.Json accepts with
// AllowNamedFloatingPointLiterals). CSV writes the same spellings unquoted.
// ---------------------------------------------------------------------------------------------------------------------

/// <summary>
/// The structured result of one verb run: an ordered scalar summary and any number of named tables. Filled by the verb
/// from the values it prints; written by <see cref="CliResultWriter"/>.
/// </summary>
internal sealed class CliResult
{
    private readonly List<KeyValuePair<string, object?>> _summary = new();
    private readonly List<CliResultTable> _tables = new();

    /// <summary>Create an empty result for <paramref name="verb"/> (the canonical verb name, e.g. "nminus1").</summary>
    public CliResult(string verb)
    {
        Verb = verb;
    }

    /// <summary>The canonical verb name written into the JSON document.</summary>
    public string Verb { get; }

    /// <summary>True once the verb has reached the end of its result (see <see cref="Complete"/>).</summary>
    public bool Completed { get; private set; }

    /// <summary>The scalar summary, in the order the verb set it.</summary>
    public IReadOnlyList<KeyValuePair<string, object?>> Summary => _summary;

    /// <summary>The tables, in the order the verb created them. The first one is the CSV main table.</summary>
    public IReadOnlyList<CliResultTable> Tables => _tables;

    /// <summary>
    /// Add one summary value. Keys are snake_case and unique: setting the same key twice is a defect in the verb and
    /// throws, rather than silently keeping one of the two values.
    /// </summary>
    public CliResult Set(string key, object? value)
    {
        CliResultNames.Check(key, "summary key");
        foreach (KeyValuePair<string, object?> kv in _summary)
            if (kv.Key == key) throw new InvalidOperationException($"result summary key '{key}' set twice (a defect in the verb's report code).");
        _summary.Add(new KeyValuePair<string, object?>(key, CliResultValues.Normalise(value)));
        return this;
    }

    /// <summary>
    /// Create a table with the given columns. Table names are snake_case and unique; "summary" is reserved (it is the
    /// name of the CSV summary file).
    /// </summary>
    public CliResultTable Table(string name, params string[] columns)
    {
        CliResultNames.Check(name, "table name");
        if (name == "summary") throw new InvalidOperationException("'summary' is reserved and cannot name a result table.");
        if (columns.Length == 0) throw new InvalidOperationException($"result table '{name}' has no columns.");
        foreach (CliResultTable t in _tables)
            if (t.Name == name) throw new InvalidOperationException($"result table '{name}' created twice (a defect in the verb's report code).");
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (string c in columns)
        {
            CliResultNames.Check(c, "column name");
            if (!seen.Add(c)) throw new InvalidOperationException($"result table '{name}' names column '{c}' twice.");
        }
        var table = new CliResultTable(name, columns);
        _tables.Add(table);
        return table;
    }

    /// <summary>The verb has reached the end of its result: the files may be written.</summary>
    public void Complete() => Completed = true;
}

/// <summary>One named table of a <see cref="CliResult"/>: fixed columns, rows of cells.</summary>
internal sealed class CliResultTable
{
    private readonly List<object?[]> _rows = new();

    internal CliResultTable(string name, string[] columns)
    {
        Name = name;
        Columns = columns;
    }

    /// <summary>The table name (snake_case); also the suffix of its CSV file.</summary>
    public string Name { get; }

    /// <summary>The column names (snake_case), in order.</summary>
    public IReadOnlyList<string> Columns { get; }

    /// <summary>The rows, each with exactly one cell per column.</summary>
    public IReadOnlyList<object?[]> Rows => _rows;

    /// <summary>Add a row. The cell count must equal the column count (a mismatch is a defect and throws).</summary>
    public CliResultTable Row(params object?[] cells)
    {
        if (cells.Length != Columns.Count)
            throw new InvalidOperationException($"result table '{Name}' has {Columns.Count} columns but a row of {cells.Length} cells was added.");
        var row = new object?[cells.Length];
        for (int i = 0; i < cells.Length; i++) row[i] = CliResultValues.Normalise(cells[i]);
        _rows.Add(row);
        return this;
    }
}

/// <summary>Name rules shared by summary keys, table names and column names.</summary>
internal static class CliResultNames
{
    /// <summary>
    /// A name is lower-case snake_case: a letter, then letters, digits or underscores. That keeps JSON keys, CSV headers
    /// and CSV file-name suffixes plain and identical everywhere.
    /// </summary>
    public static void Check(string name, string what)
    {
        bool ok = name.Length > 0 && name[0] is >= 'a' and <= 'z';
        foreach (char ch in name)
            ok &= ch is (>= 'a' and <= 'z') or (>= '0' and <= '9') or '_';
        if (!ok) throw new InvalidOperationException($"result {what} '{name}' is not lower-case snake_case.");
    }
}

/// <summary>The value types a result may hold, and their normalised form.</summary>
internal static class CliResultValues
{
    /// <summary>
    /// Normalise a value to one of: null, string, bool, long, double. Smaller integer types widen to long and float
    /// widens to double (both exact); an enum becomes its name; a date becomes its ISO 8601 round-trip text. Anything
    /// else is a defect in the verb's report code and throws, so an unexpected object is never written as its type name.
    /// </summary>
    public static object? Normalise(object? value) => value switch
    {
        null => null,
        string s => s,
        bool b => b,
        double d => d,
        float f => (double)f,
        int i => (long)i,
        long l => l,
        short sh => (long)sh,
        byte by => (long)by,
        sbyte sb => (long)sb,
        ushort us => (long)us,
        uint ui => (long)ui,
        ulong ul when ul <= long.MaxValue => (long)ul,
        Enum e => e.ToString(),
        DateTime dt => dt.ToString("o", CultureInfo.InvariantCulture),
        DateTimeOffset dto => dto.ToString("o", CultureInfo.InvariantCulture),
        _ => throw new InvalidOperationException($"a result value of type {value.GetType().Name} cannot be written (a defect in the verb's report code)."),
    };
}

/// <summary>Writes a <see cref="CliResult"/> as JSON or CSV. Pure functions of the result: no console output.</summary>
internal static class CliResultWriter
{
    /// <summary>The schema tag of the JSON document.</summary>
    public const string Schema = "gridsim-cli-result/1";

    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    /// <summary>
    /// A double as text: round-trip ("R") form in the invariant culture for a finite value, so double.Parse gives back
    /// the same bits; "NaN", "Infinity" or "-Infinity" otherwise.
    /// </summary>
    public static string Number(double d) =>
        double.IsNaN(d) ? "NaN"
        : double.IsPositiveInfinity(d) ? "Infinity"
        : double.IsNegativeInfinity(d) ? "-Infinity"
        : d.ToString("R", Inv);

    /// <summary>A CSV field: quoted (with doubled quotes) when it holds a comma, a quote or a line break.</summary>
    public static string CsvField(string s) =>
        s.Contains(',') || s.Contains('"') || s.Contains('\n') || s.Contains('\r')
            ? "\"" + s.Replace("\"", "\"\"", StringComparison.Ordinal) + "\""
            : s;

    /// <summary>One normalised cell as CSV text (null is an empty field; booleans are true / false).</summary>
    public static string CsvCell(object? v) => v switch
    {
        null => "",
        string s => CsvField(s),
        bool b => b ? "true" : "false",
        long l => l.ToString(Inv),
        double d => Number(d),
        _ => throw new InvalidOperationException($"unexpected normalised cell type {v.GetType().Name}."),
    };

    /// <summary>
    /// The JSON document, as UTF-8 text:
    /// { "schema", "tool", "verb", "args", "exitCode", "summary": { key: value }, "tables": { name: [ { column: value } ] } }.
    /// </summary>
    public static string JsonText(CliResult r, string tool, IReadOnlyList<string> args, int exitCode)
    {
        using var ms = new MemoryStream();
        var opts = new JsonWriterOptions
        {
            Indented = true,
            // The document is a data file, not HTML: keep characters such as '>' in "R1->R2" readable instead of >.
            Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        };
        using (var w = new Utf8JsonWriter(ms, opts))
        {
            w.WriteStartObject();
            w.WriteString("schema", Schema);
            w.WriteString("tool", tool);
            w.WriteString("verb", r.Verb);
            w.WriteStartArray("args");
            foreach (string a in args) w.WriteStringValue(a);
            w.WriteEndArray();
            w.WriteNumber("exitCode", exitCode);
            w.WriteStartObject("summary");
            foreach (KeyValuePair<string, object?> kv in r.Summary)
            {
                w.WritePropertyName(kv.Key);
                WriteJsonValue(w, kv.Value);
            }
            w.WriteEndObject();
            w.WriteStartObject("tables");
            foreach (CliResultTable t in r.Tables)
            {
                w.WriteStartArray(t.Name);
                foreach (object?[] row in t.Rows)
                {
                    w.WriteStartObject();
                    for (int i = 0; i < row.Length; i++)
                    {
                        w.WritePropertyName(t.Columns[i]);
                        WriteJsonValue(w, row[i]);
                    }
                    w.WriteEndObject();
                }
                w.WriteEndArray();
            }
            w.WriteEndObject();
            w.WriteEndObject();
        }
        return Encoding.UTF8.GetString(ms.ToArray());
    }

    private static void WriteJsonValue(Utf8JsonWriter w, object? v)
    {
        switch (v)
        {
            case null: w.WriteNullValue(); break;
            case string s: w.WriteStringValue(s); break;
            case bool b: w.WriteBooleanValue(b); break;
            case long l: w.WriteNumberValue(l); break;
            case double d when double.IsFinite(d):
                // Written as raw text so the exact round-trip digits are what lands in the file (not re-formatted).
                w.WriteRawValue(Number(d));
                break;
            case double d:
                w.WriteStringValue(Number(d));   // "NaN", "Infinity", "-Infinity": JSON has no literal for them
                break;
            default: throw new InvalidOperationException($"unexpected normalised value type {v.GetType().Name}.");
        }
    }

    /// <summary>Write the JSON document to <paramref name="path"/> (atomically: a temporary file, then a move).</summary>
    public static void WriteJson(CliResult r, string path, string tool, IReadOnlyList<string> args, int exitCode)
    {
        string text = JsonText(r, tool, args, exitCode);
        WriteAtomic(path, w => w.Write(text));
    }

    /// <summary>
    /// The CSV files of a result, as (path, writer action) pairs: the main table at <paramref name="path"/>; every other
    /// table at &lt;stem&gt;-&lt;table&gt;.csv beside it; the summary at &lt;stem&gt;-summary.csv. When the result has
    /// no table at all, the summary is the main file. The summary always starts with the rows verb and exit_code.
    /// </summary>
    public static IReadOnlyList<string> CsvPaths(CliResult r, string path)
    {
        string full = Path.GetFullPath(path);
        string dir = Path.GetDirectoryName(full)!;
        string stem = Path.GetFileNameWithoutExtension(full);
        var paths = new List<string> { full };
        if (r.Tables.Count == 0) return paths;
        for (int i = 1; i < r.Tables.Count; i++) paths.Add(Path.Combine(dir, $"{stem}-{r.Tables[i].Name}.csv"));
        paths.Add(Path.Combine(dir, $"{stem}-summary.csv"));
        return paths;
    }

    /// <summary>Write the CSV files (see <see cref="CsvPaths"/>); returns the full paths written, main file first.</summary>
    public static IReadOnlyList<string> WriteCsv(CliResult r, string path, int exitCode)
    {
        IReadOnlyList<string> paths = CsvPaths(r, path);
        if (r.Tables.Count == 0)
        {
            WriteAtomic(paths[0], w => WriteSummaryCsv(w, r, exitCode));
            return paths;
        }
        for (int i = 0; i < r.Tables.Count; i++)
        {
            CliResultTable t = r.Tables[i];
            WriteAtomic(paths[i], w => WriteTableCsv(w, t));
        }
        WriteAtomic(paths[^1], w => WriteSummaryCsv(w, r, exitCode));
        return paths;
    }

    /// <summary>A table as CSV: a header line, then one line per row. Lines end with the platform newline.</summary>
    public static void WriteTableCsv(TextWriter w, CliResultTable t)
    {
        w.WriteLine(string.Join(',', t.Columns));
        foreach (object?[] row in t.Rows)
            w.WriteLine(string.Join(',', row.Select(CsvCell)));
    }

    /// <summary>The summary as a two-column key,value CSV, starting with verb and exit_code.</summary>
    public static void WriteSummaryCsv(TextWriter w, CliResult r, int exitCode)
    {
        w.WriteLine("key,value");
        w.WriteLine("verb," + CsvField(r.Verb));
        w.WriteLine("exit_code," + exitCode.ToString(Inv));
        foreach (KeyValuePair<string, object?> kv in r.Summary)
            w.WriteLine(kv.Key + "," + CsvCell(kv.Value));
    }

    // Write a file through a temporary sibling and a move, so a failed or interrupted write never leaves a half file
    // under the requested name. The folder is created when missing. UTF-8 without a byte-order mark, as every other
    // GridSim CSV.
    private static void WriteAtomic(string path, Action<TextWriter> write)
    {
        // D-DOC-072: through the one output policy (CliFiles): the folder is created, and a target that is an input of
        // the run (or a folder) is refused before anything is written.
        string full = CliFiles.Output(path, "result file");
        string tmp = full + ".tmp-" + Guid.NewGuid().ToString("N");
        try
        {
            using (var w = new StreamWriter(tmp, append: false, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false)))
                write(w);
            File.Move(tmp, full, overwrite: true);
        }
        finally
        {
            if (File.Exists(tmp)) File.Delete(tmp);
        }
    }
}

/// <summary>
/// The process-wide state of the result writer: which verbs take the flags, the files asked for, and the report the
/// verb fills. Program.cs calls <see cref="TakeFlags"/> once before the verb dispatch and <see cref="Finish"/> after a
/// writer verb returns.
/// </summary>
internal static class CliOutput
{
    /// <summary>
    /// The verbs that accept --json / --csv / --out (aliases included). serve is deliberately NOT here: it is a resident
    /// stream that prints one line per measurement window as it arrives and may run without end, so a single result
    /// document written at exit does not fit it (its per-window estimates are what the estimate exports are for).
    /// </summary>
    public static readonly IReadOnlySet<string> WriterVerbs = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        "dcopf", "acopf", "cpf", "taps", "ptdf", "lodf", "iec60909", "fault", "protection-check", "grade",
        "nminus1", "n-1", "nminus2", "n-2", "n-1-1", "nminus1minus1", "cycle", "freq", "domains", "dist3ph",
        "losses", "physics-check", "experimental", "dcpf", "convert", "validate",
        "zonal",   // S2 / P02 (October 2026): the PyPSA-GB zonal transport model
        "pf",      // N122 (October 2026): the F22 AC power flow verb
        "restore", // R072 (October 2026): the restoration-sequence check
        "pmu-ingest", // N061 (October 2026): the decoded C37.118 capture
        "pmu-estimate", // 1.95.245: the linear PMU state estimate
        "acopf-full", // 2026-10-25: the full AC optimal power flow (AcOpfInteriorPoint)
        "scuc",       // 2026-10-25: security-constrained unit commitment (UnitCommitment)
        "scopf",      // 2026-10-25: preventive security-constrained DC OPF (DcScopf)
        "inertia-placement", // 2026-10-25: the inertia placement study (InertiaPlacementStudy)
        "qsts",       // P2-2: the quasi-static time series (QstsEngine)
        "hosting",    // P2-2: hosting capacity (HostingCapacity)
    };

    /// <summary>
    /// Writer verbs that do NOT take --out: convert, whose second positional argument is already its output (the
    /// converted case), so "--out" next to it would read as a third way to name that output. It takes --json / --csv.
    /// </summary>
    public static readonly IReadOnlySet<string> VerbsWithoutOut = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "convert" };

    /// <summary>The report the running verb fills; null when no result file was asked for (the usual case).</summary>
    public static CliResult? Report { get; private set; }

    private static readonly List<(string Format, string Path)> Requests = new();

    /// <summary>The canonical verb name for an alias (n-1 is nminus1, and so on); other names unchanged, lower-cased.</summary>
    public static string Canonical(string verb) => verb.ToLowerInvariant() switch
    {
        "n-1" => "nminus1",
        "n-2" => "nminus2",
        "nminus1minus1" => "n-1-1",
        var v => v,
    };

    /// <summary>
    /// Pure parse of the writer flags. Removes them from <paramref name="rest"/> and returns the requested files, or
    /// returns an error message (the caller prints it and exits 2). For a verb that is not a writer verb, --json and
    /// --csv are refused (they would otherwise be silently ignored), except modal's own --json (print JSON to the
    /// console) and screen's own --csv (its screening table); --out is left alone there, because many verbs use it for
    /// their own output folder or report path.
    /// </summary>
    public static string? Parse(List<string> rest, List<(string Format, string Path)> requests)
    {
        string verb = rest.Count > 0 ? rest[0].ToLowerInvariant() : "";
        if (!WriterVerbs.Contains(verb))
        {
            string shown = rest.Count > 0 ? rest[0] : "no verb";
            if (rest.Contains("--json") && verb != "modal")
                return $"error: --json writes a verb's result file and applies to {WriterList()} only (got '{shown}').";
            if (rest.Contains("--csv") && verb != "screen")
                return $"error: --csv writes a verb's result file and applies to {WriterList()} only (got '{shown}').";
            return null;
        }

        var flags = new List<string> { "--json", "--csv" };
        if (!VerbsWithoutOut.Contains(verb)) flags.Add("--out");
        foreach (string flag in flags)
        {
            int i = rest.IndexOf(flag);
            if (i < 0) continue;
            if (rest.LastIndexOf(flag) != i) return $"error: {flag} given more than once; name one file per flag.";
            if (i + 1 >= rest.Count || rest[i + 1].StartsWith("--", StringComparison.Ordinal))
                return $"error: {flag} expected a file path but got ''";
            string path = rest[i + 1];
            string format;
            if (flag == "--json") format = path.EndsWith(".json", StringComparison.OrdinalIgnoreCase) ? "json" : "";
            else if (flag == "--csv") format = path.EndsWith(".csv", StringComparison.OrdinalIgnoreCase) ? "csv" : "";
            else format = path.EndsWith(".json", StringComparison.OrdinalIgnoreCase) ? "json"
                        : path.EndsWith(".csv", StringComparison.OrdinalIgnoreCase) ? "csv" : "";
            if (format.Length == 0)
            {
                string expected = flag == "--json" ? "a file name ending in .json"
                    : flag == "--csv" ? "a file name ending in .csv"
                    : "a file name ending in .json or .csv";
                return $"error: {flag} expected {expected} but got '{path}'";
            }
            rest.RemoveRange(i, 2);
            requests.Add((format, path));
        }

        // Two flags naming the same file would overwrite one result with the other.
        var full = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach ((string _, string p) in requests)
            if (!full.Add(Path.GetFullPath(p))) return $"error: two result flags name the same file '{p}'.";
        return null;
    }

    /// <summary>
    /// Called once by Program.cs before the verb dispatch: take the writer flags off the argument list (exit 2 on a bad
    /// one) and, when any file was asked for, create the report the verb fills.
    /// </summary>
    public static void TakeFlags(List<string> rest)
    {
        Requests.Clear();
        Report = null;
        string? error = Parse(rest, Requests);
        if (error is not null)
        {
            Console.Error.WriteLine(error);
            Environment.Exit(2);
        }
        if (Requests.Count > 0) Report = new CliResult(Canonical(rest[0]));
    }

    /// <summary>
    /// Called by Program.cs after a writer verb returns: write the requested files when the verb completed its result,
    /// otherwise say on stderr that none was written. A file that cannot be written is an error: exit 1.
    /// </summary>
    public static void Finish(IReadOnlyList<string> args)
    {
        CliResult? r = Report;
        if (r is null) return;
        int exitCode = Environment.ExitCode;
        if (!r.Completed)
        {
            Console.Error.WriteLine($"[{r.Verb}] no result file written: the run stopped before its result was complete (exit {exitCode}).");
            return;
        }
        string tool = "gridsim " + GridSim.Core.BuildInfo.InformationalVersion;
        foreach ((string format, string path) in Requests)
        {
            try
            {
                if (format == "json")
                {
                    CliResultWriter.WriteJson(r, path, tool, args, exitCode);
                    Console.Error.WriteLine($"[{r.Verb}] wrote {Path.GetFullPath(path)}");
                }
                else
                {
                    IReadOnlyList<string> written = CliResultWriter.WriteCsv(r, path, exitCode);
                    Console.Error.WriteLine($"[{r.Verb}] wrote {string.Join(", ", written)}");
                }
            }
            catch (OutputRefusedException ex)
            {
                // D-DOC-072: the path names an input of the run (or a folder): a usage error, exit 2; nothing written there.
                Console.Error.WriteLine($"error: {r.Verb}: {ex.Message}");
                Environment.ExitCode = CliExit.Usage;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
            {
                Console.Error.WriteLine($"error: {r.Verb}: could not write the result file '{path}': {ex.Message}");
                Environment.ExitCode = 1;
            }
        }
    }

    private static string WriterList() =>
        string.Join(", ", WriterVerbs.Select(Canonical).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal));
}
