using System.Diagnostics;
using System.Globalization;
using System.Text;

namespace GridSim.Cli;

// ---------------------------------------------------------------------------------------------------------------------
// Per-stage timing (2.1.154), the global switch --timings.
//
// With --timings, the run measures wall-clock milliseconds per stage and reports them: as a "timings" block at the end of
// the gridsim-cli-result/1 document (written with --json), and as a short table on stderr when the process ends. Without
// the switch Enabled is false, every call below returns at once, and no output changes by a single byte.
//
// The stopwatches live here, in the CLI layer, around calls the verbs already make. Nothing inside GridSim.Core is
// touched: where a verb's code path calls one solver entry point that builds, assembles, factors and solves inside it,
// the verb reports one combined "solve" and names the stages folded into it (Merged), rather than reaching into the kernel.
//
// Stages (a stage a verb cannot report is simply absent, and the block lists it under stages_not_measured):
//   startup   process start to the first statement of the CLI (runtime start, assembly load, JIT of the entry point);
//   parse     case load (and the case's own model construction, which the loaders do while reading);
//   build     model / Y-bus construction, when a verb builds it in a call of its own;
//   assemble  Jacobian / matrix assembly, when separable from the factorisation;
//   factor    the factorisation, when separable;
//   solve     the numerical solve (a combined figure when stages_merged says so);
//   write     the end of the last measured stage to the end of the run: the console report, the result document being
//             filled and rendered. For a --json run it ends when the document has been rendered (the file write itself
//             follows and is not inside the figure, because the document carries the figure); for other runs, at exit;
//   total     process start to that same instant: startup plus the time since the CLI started.
// Counts: iterations (Newton iterations of the verb's main solve) and q_limit_passes, where the verb exposes them.
//
// Adding a verb is cheap: wrap its solver call in "using var t = StageTimer.Begin("solve");", say what the call folds in
// with StageTimer.Merged(...), and pass counts with StageTimer.Count(...). Case loading is already timed centrally
// (LoadCaseArgument, LoadSelected). Spans nest exclusively: an inner span pauses the outer one, so no millisecond is
// counted twice, and a span on any thread but the main one is ignored (the enclosing main-thread span covers it).
// ---------------------------------------------------------------------------------------------------------------------

/// <summary>The --timings switch: per-stage wall-clock timing of one CLI run.</summary>
internal static class StageTimer
{
    /// <summary>The stage names, in report order.</summary>
    public static readonly IReadOnlyList<string> StageNames = new[] { "startup", "parse", "build", "assemble", "factor", "solve", "write", "total" };

    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;
    private static readonly Stopwatch Run = new();
    private static readonly Dictionary<string, double> Stages = new(StringComparer.Ordinal);
    private static readonly List<(string Name, double Start)> Open = new();
    private static readonly List<string> MergedStages = new();
    private static readonly List<KeyValuePair<string, long>> Counts = new();
    private static int _mainThread;
    private static double _startupMs;
    private static double _lastEndMs;
    private static Snapshot? _sealed;
    private static bool _exitHooked;

    /// <summary>True when --timings was given.</summary>
    public static bool Enabled { get; private set; }

    /// <summary>The fixed figures of one run, as reported.</summary>
    internal sealed record Snapshot(
        double StartupMs, IReadOnlyDictionary<string, double> Stages, double WriteMs, double TotalMs,
        IReadOnlyList<string> Merged, IReadOnlyList<KeyValuePair<string, long>> Counts);

    /// <summary>
    /// Take --timings off the command line (both lists) and, when it was there, start the clock. Called first thing in
    /// Program.cs, so the switch is never seen by the journal, the output policy or any verb's flag check.
    /// </summary>
    public static string[] Take(string[] args, List<string> rest)
    {
        if (!args.Contains("--timings", StringComparer.Ordinal)) return args;
        string[] kept = args.Where(t => t != "--timings").ToArray();
        rest.Clear();
        rest.AddRange(kept);
        Start();
        return kept;
    }

    /// <summary>Start timing (tests call this directly; Take calls it for --timings).</summary>
    internal static void Start()
    {
        Reset();
        Enabled = true;
        _mainThread = Environment.CurrentManagedThreadId;
        try
        {
            using Process self = Process.GetCurrentProcess();
            _startupMs = Math.Max(0.0, (DateTime.UtcNow - self.StartTime.ToUniversalTime()).TotalMilliseconds);
        }
        catch (Exception ex) when (ex is InvalidOperationException or PlatformNotSupportedException or System.ComponentModel.Win32Exception or NotSupportedException)
        {
            _startupMs = 0.0;
        }
        Run.Restart();
        if (!_exitHooked)
        {
            _exitHooked = true;
            AppDomain.CurrentDomain.ProcessExit += (_, _) => PrintTable();
        }
    }

    /// <summary>Forget everything (a new run, or a test).</summary>
    internal static void Reset()
    {
        Enabled = false;
        Run.Reset();
        Stages.Clear();
        Open.Clear();
        MergedStages.Clear();
        Counts.Clear();
        _startupMs = 0.0;
        _lastEndMs = 0.0;
        _sealed = null;
    }

    /// <summary>A running stage; dispose it to stop. The default value (switch off, other thread) does nothing.</summary>
    internal readonly struct Span : IDisposable
    {
        private readonly bool _live;
        internal Span(bool live) { _live = live; }
        public void Dispose() { if (_live) End(); }
    }

    /// <summary>Start timing <paramref name="stage"/>. Wrap the existing call: <c>using var t = StageTimer.Begin("solve");</c>.</summary>
    public static Span Begin(string stage)
    {
        if (!Enabled || Environment.CurrentManagedThreadId != _mainThread || _sealed is not null) return default;
        double now = Run.Elapsed.TotalMilliseconds;
        if (Open.Count > 0)
        {
            (string outer, double start) = Open[^1];
            Credit(outer, now - start);   // the outer stage pauses while the inner one runs
        }
        Open.Add((stage, now));
        return new Span(true);
    }

    private static void End()
    {
        if (Open.Count == 0) return;
        double now = Run.Elapsed.TotalMilliseconds;
        (string name, double start) = Open[^1];
        Open.RemoveAt(Open.Count - 1);
        Credit(name, now - start);
        _lastEndMs = now;
        if (Open.Count > 0) Open[^1] = (Open[^1].Name, now);   // the outer stage resumes
    }

    /// <summary>
    /// Add a figure measured by a stopwatch the verb already had (estimateClock, firstSolveClock, harmonicClock), so no
    /// second clock runs beside it. Only for a stopwatch that does not run inside an open span.
    /// </summary>
    public static void Add(string stage, double milliseconds)
    {
        if (!Enabled || Environment.CurrentManagedThreadId != _mainThread || _sealed is not null) return;
        Credit(stage, milliseconds);
        _lastEndMs = Run.Elapsed.TotalMilliseconds;
    }

    private static void Credit(string stage, double ms)
    {
        if (ms < 0.0) ms = 0.0;
        Stages[stage] = Stages.TryGetValue(stage, out double have) ? have + ms : ms;
    }

    /// <summary>Say that <paramref name="stages"/> are folded into one figure (usually the "solve" one) in this verb.</summary>
    public static void Merged(params string[] stages)
    {
        if (!Enabled) return;
        foreach (string s in stages)
            if (!MergedStages.Contains(s, StringComparer.Ordinal)) MergedStages.Add(s);
    }

    /// <summary>Record a count (iterations, q_limit_passes); the last value set for a name wins.</summary>
    public static void Count(string name, long value)
    {
        if (!Enabled) return;
        int i = Counts.FindIndex(c => c.Key == name);
        var kv = new KeyValuePair<string, long>(name, value);
        if (i >= 0) Counts[i] = kv; else Counts.Add(kv);
    }

    /// <summary>
    /// Fix the figures now (idempotent: the first call wins) and return them. write is the time since the last measured
    /// stage ended; total adds the startup time to the time since the CLI started.
    /// </summary>
    public static Snapshot Seal()
    {
        if (_sealed is not null) return _sealed;
        while (Open.Count > 0) End();
        double now = Run.Elapsed.TotalMilliseconds;
        double write = Math.Max(0.0, now - _lastEndMs);
        var stages = new Dictionary<string, double>(Stages, StringComparer.Ordinal);
        _sealed = new Snapshot(_startupMs, stages, write, _startupMs + now, MergedStages.ToArray(), Counts.ToArray());
        return _sealed;
    }

    /// <summary>The stage names (of the six measurable ones) a run neither measured nor folded into another.</summary>
    internal static IReadOnlyList<string> NotMeasured(Snapshot s) =>
        new[] { "parse", "build", "assemble", "factor", "solve" }
            .Where(n => !s.Stages.ContainsKey(n) && !s.Merged.Contains(n, StringComparer.Ordinal)).ToArray();

    /// <summary>Write the "timings" object of the result document.</summary>
    internal static void WriteJson(System.Text.Json.Utf8JsonWriter w, Snapshot s)
    {
        w.WriteStartObject("timings");
        w.WriteString("unit", "ms");
        w.WriteNumber("startup", Round(s.StartupMs));
        foreach (string n in new[] { "parse", "build", "assemble", "factor", "solve" })
            if (s.Stages.TryGetValue(n, out double v)) w.WriteNumber(n, Round(v));
        w.WriteNumber("write", Round(s.WriteMs));
        w.WriteNumber("total", Round(s.TotalMs));
        w.WriteStartArray("stages_merged");
        foreach (string m in s.Merged) w.WriteStringValue(m);
        w.WriteEndArray();
        w.WriteStartArray("stages_not_measured");
        foreach (string m in NotMeasured(s)) w.WriteStringValue(m);
        w.WriteEndArray();
        foreach (KeyValuePair<string, long> c in s.Counts) w.WriteNumber(c.Key, c.Value);
        w.WriteEndObject();
    }

    // Milliseconds to the microsecond: the clock has no better resolution worth reporting.
    private static double Round(double ms) => Math.Round(ms, 3, MidpointRounding.AwayFromZero);

    /// <summary>The stderr table (called at process exit).</summary>
    private static void PrintTable()
    {
        if (!Enabled) return;
        try
        {
            Snapshot s = Seal();
            var sb = new StringBuilder();
            sb.AppendLine("timings (wall-clock ms):");
            void Row(string name, double ms, string note = "") =>
                sb.AppendLine(string.Create(Inv, $"  {name,-9} {ms,12:F3}{note}"));
            Row("startup", s.StartupMs);
            foreach (string n in new[] { "parse", "build", "assemble", "factor", "solve" })
            {
                if (!s.Stages.TryGetValue(n, out double v)) continue;
                string note = n == "solve" && s.Merged.Count > 0 ? "  (combined: " + string.Join(", ", s.Merged) + ")" : "";
                Row(n, v, note);
            }
            Row("write", s.WriteMs);
            Row("total", s.TotalMs);
            foreach (KeyValuePair<string, long> c in s.Counts) sb.AppendLine(string.Create(Inv, $"  {c.Key,-14} {c.Value}"));
            IReadOnlyList<string> missing = NotMeasured(s);
            if (missing.Count > 0) sb.AppendLine("  not measured for this verb: " + string.Join(", ", missing));
            Console.Error.Write(sb.ToString());
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException) { /* stderr closed: nothing to say it to */ }
    }
}
