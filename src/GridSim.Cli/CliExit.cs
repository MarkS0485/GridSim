namespace GridSim.Cli;

/// <summary>
/// DOC-1 (October 2026, docs-session findings D-DOC-001..015, 053, 085, 130, 159): the one place that names the CLI's
/// exit codes and maps a failure to one of them, so every verb ends with the code the CLI reference (user manual ch. 19,
/// "Parsing rules and exit codes") documents, and never with an unhandled exception or a false exit 0.
///
/// The codes, in plain words:
/// <list type="bullet">
/// <item>0: the run did what was asked and every check passed.</item>
/// <item>1: input error. A file or case folder that is missing or cannot be loaded, invalid data inside it (for
/// example a generator whose Pmin is above its Pmax), or a requested output that cannot be written.</item>
/// <item>2: usage error. Something on the command line names nothing that exists or is not allowed: an unknown flag or
/// verb, a bad flag value, an unknown case NAME, a bus id that is not in the case, a forward-inference rejection.</item>
/// <item>3: a verdict or convergence failure. The study ran and its answer is a failure (did not converge, not
/// reproduced, infeasible, a preflight probe that failed).</item>
/// </list>
///
/// This file has no dependency on the rest of the CLI, so the test project compiles it in (see GridSim.Tests.csproj)
/// and tests the mapping rules directly, beside the process-level tests that run the real binary.
/// </summary>
internal static class CliExit
{
    /// <summary>Success.</summary>
    public const int Ok = 0;

    /// <summary>Input error: missing or unloadable input, invalid data in it, or an output that cannot be written.</summary>
    public const int InputError = 1;

    /// <summary>Usage error: the command line names something that does not exist or is not allowed.</summary>
    public const int Usage = 2;

    /// <summary>Verdict or convergence failure: the study ran and its answer is a failure.</summary>
    public const int Verdict = 3;

    /// <summary>
    /// D-DOC-130 (one rule for every verb): the exit code for a case that failed to load. An argument that names no
    /// case at all (<see cref="UnknownCaseException"/>: not a built-in name, not an existing folder, not a .m or .json
    /// path) is a usage error, exit 2, exactly as the verbs that already used the shared case selection did. Anything
    /// else (a missing file or folder, content that cannot be loaded) is an input error, exit 1.
    /// </summary>
    public static int CaseLoadFailure(Exception ex)
    {
        ArgumentNullException.ThrowIfNull(ex);
        return ex is UnknownCaseException ? Usage : InputError;
    }

    /// <summary>
    /// D-DOC-159: an <see cref="ArgumentException"/> message ends with " (Parameter 'name')", the name of a C# method
    /// parameter, which means nothing to a user ("fault bus 99 is not in the model (Parameter 'faultBusId')"). This
    /// returns the message without that suffix, so the text a verb prints names what is wrong in the user's terms.
    /// Any other message is returned unchanged.
    /// </summary>
    public static string UserMessage(Exception ex)
    {
        ArgumentNullException.ThrowIfNull(ex);
        string msg = ex.Message;
        if (ex is ArgumentException { ParamName: { Length: > 0 } p })
        {
            string suffix = $" (Parameter '{p}')";
            if (msg.EndsWith(suffix, StringComparison.Ordinal)) msg = msg[..^suffix.Length];
        }
        return msg;
    }
}

/// <summary>
/// D-DOC-130: thrown by the case resolver (Program.cs LoadNamed) when an argument names no case at all. It is an
/// <see cref="ArgumentException"/>, as the old exception was, so every existing handler that caught that still catches
/// this; the handlers that report a load failure use <see cref="CliExit.CaseLoadFailure"/> to tell it (exit 2) apart
/// from a missing or unloadable file (exit 1). The message is built by the resolver: it names the case and lists the
/// built-in names.
/// </summary>
internal sealed class UnknownCaseException : ArgumentException
{
    public UnknownCaseException(string message) : base(message) { }
}

/// <summary>
/// D-DOC-011 / D-DOC-012: a tally of the failures a multi-part run met (the GPU preflight's probes, the benchmark's
/// cases and engines), so the run can print every failure, keep going, and still end with the documented code instead
/// of exit 0. The rule is the batch verbs' rule (manual ch. 19, nminus1-sweep and stress): any part that could not be
/// loaded makes the run an input error (exit 1); otherwise any part that ran and failed (did not converge, threw) makes
/// it a verdict failure (exit 3); a clean run is 0. Skipped parts (an absent GPU, a meshed case for the radial sweep)
/// are not failures and are not recorded here.
/// </summary>
internal sealed class FailureTally
{
    private readonly List<string> _loadFailures = new();
    private readonly List<string> _runFailures = new();
    private readonly object _gate = new();   // the benchmark records from parallel workers

    /// <summary>Record a part that could not be loaded (exit 1 at the end).</summary>
    public void LoadFailed(string what)
    {
        lock (_gate) _loadFailures.Add(what);
    }

    /// <summary>Record a part that ran and failed: did not converge, threw, gave a wrong answer (exit 3 at the end).</summary>
    public void RunFailed(string what)
    {
        lock (_gate) _runFailures.Add(what);
    }

    /// <summary>Parts that could not be loaded, in the order recorded.</summary>
    public IReadOnlyList<string> LoadFailures { get { lock (_gate) return _loadFailures.ToArray(); } }

    /// <summary>Parts that ran and failed, in the order recorded.</summary>
    public IReadOnlyList<string> RunFailures { get { lock (_gate) return _runFailures.ToArray(); } }

    /// <summary>The exit code: 1 when anything failed to load, else 3 when anything failed, else 0.</summary>
    public int ExitCode
    {
        get
        {
            lock (_gate)
                return _loadFailures.Count > 0 ? CliExit.InputError : _runFailures.Count > 0 ? CliExit.Verdict : CliExit.Ok;
        }
    }

    /// <summary>
    /// The lines a run prints on stderr at the end when anything failed (none when clean): one line per kind, naming
    /// every failed part in sorted order (the benchmark records from parallel workers, so the recorded order is not
    /// repeatable; sorting makes the message the same on every run), and the exit code.
    /// </summary>
    public IReadOnlyList<string> SummaryLines(string verb)
    {
        lock (_gate)
        {
            var lines = new List<string>();
            if (_loadFailures.Count > 0)
                lines.Add($"error: {verb}: {_loadFailures.Count} part(s) could not be loaded: " +
                          string.Join("; ", _loadFailures.Order(StringComparer.Ordinal)) + ".");
            if (_runFailures.Count > 0)
                lines.Add($"error: {verb}: {_runFailures.Count} part(s) failed: " +
                          string.Join("; ", _runFailures.Order(StringComparer.Ordinal)) + ".");
            if (lines.Count > 0)
                lines.Add($"error: {verb}: exit {(_loadFailures.Count > 0 ? CliExit.InputError : CliExit.Verdict)} " +
                          "(1 when anything could not be loaded, otherwise 3 when anything failed).");
            return lines;
        }
    }
}
