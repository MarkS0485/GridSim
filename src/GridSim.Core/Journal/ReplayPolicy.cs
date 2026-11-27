namespace GridSim.Core.Journal;

/// <summary>
/// Which journaled runs <c>breaking-change validate</c> may run again. An allow-list keyed by canonical verb: a verb
/// that is not listed is never re-run ("not yet classified").
///
/// A run is re-run only in a scratch folder, with every output path rewritten into it. That is safe only for a verb
/// whose every written file is named by an output flag (<c>--out</c>, <c>--json</c>, <c>--csv</c>, <c>--export</c>,
/// ...) or lies under its working folder, so the rewrite catches it. The verbs and options below that write somewhere
/// else (a positional output, <c>--dir</c>, <c>--report</c>, <c>--emit</c>) are refused by name.
/// </summary>
public static class ReplayPolicy
{
    /// <summary>The study verbs with a result document of their own (their own <c>--out</c>), besides the shared
    /// writer's verbs.</summary>
    public static readonly IReadOnlyList<string> StudyVerbs =
    [
        "transmission-security", "transfer-capability", "motor-start", "dynamics-study", "corrective-sequence",
        "planning-study", "cgmes-study", "restoration-study", "reliability-study", "emt-line", "arc-flash",
        "cable-rating", "cable-size", "user-model",
        // RC5 WP27: each writes only its --out JSON (and cosim its --boundary-csv, see OutputPathFlags); no time, host or
        // build field. Their inputs are command-line files (case, spectra, measurements, study, cut file), hashed as tokens.
        "harmonic-pf", "harmonic-se", "cosim", "emt-partition",
        // RC5 WP28: gic writes only its --out JSON (the gic file's SHA-256 inside); its inputs are the case and --gic file
        // tokens, hashed as tokens.
        "gic",
        // RC5 WP30: scl-import writes only its --out node-breaker file (no time, host or build field); its inputs are the
        // SCL file and the --params file tokens, hashed as tokens.
        "scl-import",
        // RC5 WP33: dist-opt writes only its --out result table (no time, host or build field); its inputs are the case and
        // the --params file tokens, hashed as tokens.
        "dist-opt",
    ];

    /// <summary>
    /// 2.1.127: the dynamics verbs re-run although they are neither shared writer verbs nor study verbs. Each was audited:
    /// every file it writes goes through the output policy (<c>CliFiles.Output</c>, or its replay-scratch guard), and is
    /// named by an output flag, by a flag of <see cref="OutputPathFlags"/>, or lies under the working folder.
    /// <list type="bullet">
    /// <item><c>stability</c>: <c>--export</c> (the classical trajectory) and <c>--events-out</c> (the RMS event export
    /// and its run metadata). Its DYR or dynamics file is a command-line input (<c>--dyn-data</c>).</item>
    /// <item><c>long-term</c>: its CSV, only when <c>--out</c> names it (without <c>--out</c> it is written beside the
    /// input, outside the output flags, so such a run is refused by <see cref="Refusal"/>). Its input file names no other
    /// file.</item>
    /// <item><c>trip</c>: <c>--export</c>, the frequency trajectory, time-stamped from a fixed epoch (2000-01-01), not the
    /// clock.</item>
    /// <item><c>modal</c>: <c>--export</c>, the small-signal export (no time, host or build field).</item>
    /// </list>
    /// <c>dynamics-study</c> is a study verb (<see cref="StudyVerbs"/>).
    /// </summary>
    public static readonly IReadOnlyList<string> DynamicsVerbs = ["stability", "long-term", "trip", "modal"];

    /// <summary>
    /// 2.1.127: options whose value is an output path, for verbs whose output is not named by one of the output policy's
    /// own output flags (<c>CliFiles.OutputFlags</c>, which is pinned). A re-run rewrites the value into its scratch
    /// folder like an output-flag value.
    /// </summary>
    public static readonly IReadOnlyList<(string Verb, string Option)> OutputPathFlags = [("stability", "--events-out"), ("cosim", "--boundary-csv")];

    // Verbs never re-run, with the reason (checked before the allow-list).
    private static readonly (string Verb, string Reason)[] NeverVerbs =
    [
        ("serve", "a live or network verb"), ("rpc", "a live or network verb"), ("interpose", "a live or network verb"),
        ("gda", "it reads an external bridge"), ("gda-replay", "it reads an external bridge"),
        ("crossval", "it runs an external Python tool"),
        ("cycle", "its results depend on wall-clock timing (a real-time scheduler: what completes in each window)"),
        ("bench", "it measures timing and hardware"), ("gpucheck", "it measures timing and hardware"),
        ("simdcheck", "it measures timing and hardware"), ("batchsolve", "it measures timing and hardware"),
        ("synth", "it writes the bundled data"),
        ("audit", "it reads and writes audit logs and keys"),
        ("licence", "no study result"), ("compliance", "no study result"), ("ref", "no study result"), ("help", "no study result"),
        ("breaking-change", "no study result"),
        ("convert", "it writes its converted case to a positional path, outside the output flags"),
        ("cgmes-export", "it writes its documents to --dir, outside the output flags"),
        ("dgs-import", "it writes its import to --dir, outside the output flags"),
        // 2.1.127: the other dynamics verbs, each with why it is not re-run.
        ("transient", "its timeseries.csv carries each slice's solve time and its summary.md the wall time and the run header, and markdown is compared byte for byte, so two runs on one build differ"),
        ("emt", "its summary.md records the wall time, and markdown is compared byte for byte, so two runs on one build differ"),
        ("replay-event", "its summary.md carries the run header (time, host, build), and markdown is compared byte for byte"),
        ("scenario", "its summary.md carries the run header (time, host, build), and markdown is compared byte for byte"),
        ("h0-study", "its summary.md carries the run header (time, host, build), and markdown is compared byte for byte"),
        ("evidence", "its summary.md carries the run header (time, host, build), and markdown is compared byte for byte"),
        ("impedance-stability", "its export records the time and host, in a schema the volatile-field list does not cover"),
        ("replay", "it plays a recorded trace on the console and writes no result file"),
        ("fit-frequency", "it prints its fit on the console and writes no result file"),
        ("fleet", "not yet shown to give the same result twice: its gate reads an event's recorded trace, and no two-run census covers it"),
    ];

    // Options that make an otherwise safe verb unsafe: they write a file named outside the output flags, or touch
    // external logs and keys.
    private static readonly (string Verb, string Option, string Reason)[] NeverOptions =
    [
        ("*", "--audit-forward", "--audit-forward writes to an external log"),
        ("*", "--incident-log", "--incident-log writes to an external log"),
        ("*", "--audit-sign-key", "--audit-sign-key uses a signing key"),
        ("*", "--audit-verify-key", "--audit-verify-key uses a verification key"),
        ("substation-state", "--emit", "--emit writes its case outside the output flags"),
        ("cgmes-shacl", "--report", "--report writes its report outside the output flags"),
        ("dist3ph", "to-v2", "dist3ph to-v2 writes its feeder to a positional path"),
        ("harmonic-assess", "--template", "--template writes a template, not a study result"),
        // SEC-27: a re-run never loads code. A journal line or an imported result file carries its own command line, so a
        // plug-in configuration (and its assembly) could arrive with it; the user never typed --model-plugins for the re-run.
        // Plug-in jobs are run by hand, never re-run by validate (an imported entry included). No other option of the CLI
        // loads code or names a program to start (crossval, which starts Python, is a never-verb).
        ("*", "--model-plugins", "--model-plugins loads plug-in code, and a re-run never loads code"),
        ("user-model", "check-plugin", "user-model check-plugin loads plug-in code, and a re-run never loads code"),
    ];

    /// <summary>
    /// The replay class of a run: null when it may be re-run, else the reason it may not.
    /// </summary>
    /// <param name="verb">The canonical verb ("run" for the no-verb case run).</param>
    /// <param name="argv">The run's command line.</param>
    /// <param name="writerVerbs">The verbs of the shared result writer (canonical names).</param>
    public static string? Refusal(string verb, IReadOnlyList<string> argv, IReadOnlySet<string> writerVerbs)
    {
        ArgumentNullException.ThrowIfNull(verb);
        ArgumentNullException.ThrowIfNull(argv);
        ArgumentNullException.ThrowIfNull(writerVerbs);
        foreach ((string v, string reason) in NeverVerbs)
            if (v == verb) return reason;
        foreach ((string v, string option, string reason) in NeverOptions)
            // SEC-27: matched without regard to case (the verbs read some of these tokens so), which only refuses more.
            if ((v == "*" || v == verb) && argv.Any(a => string.Equals(a, option, StringComparison.OrdinalIgnoreCase)
                    || a.StartsWith(option + "=", StringComparison.OrdinalIgnoreCase)))
                return reason;
        if (verb == "run")
        {
            bool export = argv.Any(a => a == "--enableExport" || a.StartsWith("--enableExport=", StringComparison.Ordinal));
            bool once = argv.Contains("--once") || argv.Contains("--ticks");
            return export && once ? null : "a case run without --once (or --ticks) and --enableExport has no result file";
        }
        if (verb == "long-term" && !argv.Any(a => a == "--out" || a.StartsWith("--out=", StringComparison.Ordinal)))
            return "long-term without --out writes its CSV beside its input, outside the output flags (give --out to make it re-runnable)";
        if (writerVerbs.Contains(verb) || StudyVerbs.Contains(verb) || DynamicsVerbs.Contains(verb)) return null;
        return "not yet classified";
    }
}
