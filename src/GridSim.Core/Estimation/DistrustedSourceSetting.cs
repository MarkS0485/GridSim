using System.Globalization;
using GridSim.Core.Diagnostics;

namespace GridSim.Core.Estimation;

// ---------------------------------------------------------------------------------------------------------------------
// CAF-9 / gap B2 (November 2026): D1 distrusted-source containment, applied.
//
// DistrustedSources is the pure primitive (it demotes a distrusted source's rows to pseudo-measurements). Until now no
// product path applied it: nothing could name a source, nothing wrote the DistrustedSource security event, and the
// demoted rows still counted towards the chi-square redundancy. This file holds the two pieces every host shares, so the
// CLI (`estimate --distrust`, `serve --distrust`) and the web host (`Realtime:DistrustedSources`) behave the same way:
//
//   1. Parse: the one reading and validation of the operator's list. A malformed value fails loud, naming the flag or
//      setting it came from; it is never trimmed into something the operator did not write.
//   2. RecordApplied: the one place the DistrustedSource security events are written, one per distrusted source per
//      run, through the caller's audit path (AuditRunRecorder.RecordSecurityEvent). Its baseline severity is
//      Significant (IncidentPolicy.Classify), so with incident wiring each one is raised as an incident.
//
// The redundancy change itself lives in the estimator (WlsStateEstimator.CountsAsRedundancy with a control), driven by
// EstimationOptions.DistrustedSources.
// ---------------------------------------------------------------------------------------------------------------------

/// <summary>
/// Reading the operator's distrusted-source list (CLI <c>--distrust</c>, web setting <c>Realtime:DistrustedSources</c>)
/// and recording the <see cref="SecurityEventKind.DistrustedSource"/> events when a run applies it (NCSC CAF D1).
/// </summary>
public static class DistrustedSourceSetting
{
    /// <summary>The CLI flag of <c>gridsim estimate</c> and <c>gridsim serve</c> that names the distrusted sources.</summary>
    public const string CliFlag = "--distrust";

    /// <summary>The web host's setting for its resident estimator (environment variable
    /// <c>Realtime__DistrustedSources</c>).</summary>
    public const string WebSetting = "Realtime:DistrustedSources";

    /// <summary>The longest source tag accepted. Real tags are short (<c>scada</c>, <c>pmu</c>,
    /// <c>pseudo-demand+comp-range</c>); a longer value is a pasted mistake, refused rather than carried into the audit
    /// log.</summary>
    public const int MaxSourceNameLength = 128;

    /// <summary>
    /// Source tags GridSim itself writes on rows it derives, which are not measurement feeds an operator can distrust:
    /// <c>prior-solved</c> (the solved voltage prior, already left out of the redundancy, N078) and
    /// <c>removal-backfill-prior</c> (a pseudo-row the bad-data loop writes in place of a removed row). Naming one is
    /// refused: it would claim a containment that contains nothing.
    /// </summary>
    public static IReadOnlyList<string> ReservedSources { get; } =
        new[] { MeasuredInjectionPrior.SolvedPriorSource, WlsStateEstimator.BackfillSource };

    /// <summary>
    /// Read a comma-separated list of distrusted source tags, as the operator wrote it, validating every name.
    /// Returns the names in the order given (trimmed). Throws <see cref="ArgumentException"/>, with a message that
    /// begins with <paramref name="settingName"/>, when the value names no source, has an empty item (a doubled,
    /// leading or trailing comma), a name with a space or a control character inside it, a name longer than
    /// <see cref="MaxSourceNameLength"/>, a reserved tag (<see cref="ReservedSources"/>), or the same name twice
    /// (compared without regard to case, as <see cref="DistrustedSources"/> matches).
    /// </summary>
    /// <param name="value">The raw value (for example <c>scada,pmu</c>).</param>
    /// <param name="settingName">The flag or setting the value came from, named in every error.</param>
    public static IReadOnlyList<string> ParseNames(string? value, string settingName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(settingName);
        if (value is null || value.Trim().Length == 0)
            throw new ArgumentException(
                $"{settingName} names no source: give one or more measurement source tags separated by commas (for example scada or scada,pmu).");

        var names = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        string[] items = value.Split(',');
        for (int i = 0; i < items.Length; i++)
        {
            string name = items[i].Trim();
            // An empty item is a typing slip (",," or a trailing comma); refusing it keeps the list exactly as meant.
            if (name.Length == 0)
                throw new ArgumentException(
                    $"{settingName} '{value}': item {i + 1} is empty (a doubled, leading or trailing comma); give source tags separated by single commas.");
            foreach (char c in name)
                if (char.IsWhiteSpace(c) || char.IsControl(c))
                    throw new ArgumentException(
                        $"{settingName} '{value}': source tag '{name}' contains a space or a control character; a source tag is one word (for example scada or pseudo-demand+comp-range).");
            if (name.Length > MaxSourceNameLength)
                throw new ArgumentException(
                    $"{settingName}: source tag item {i + 1} is {name.Length.ToString(CultureInfo.InvariantCulture)} characters long; the limit is {MaxSourceNameLength.ToString(CultureInfo.InvariantCulture)}.");
            foreach (string reserved in ReservedSources)
                if (string.Equals(name, reserved, StringComparison.OrdinalIgnoreCase))
                    throw new ArgumentException(
                        $"{settingName} '{value}': '{name}' is a tag GridSim writes on rows it derives, not a measurement feed, so it cannot be distrusted.");
            if (!seen.Add(name))
                throw new ArgumentException(
                    $"{settingName} '{value}': source tag '{name}' is given more than once (tags match without regard to case).");
            names.Add(name);
        }
        return names;
    }

    /// <summary>
    /// <see cref="ParseNames"/>, then the control for those names with the default sigma inflation
    /// (<see cref="DistrustedSources.DefaultSigmaInflation"/>). Never returns an empty control.
    /// </summary>
    public static DistrustedSources Parse(string? value, string settingName) => new(ParseNames(value, settingName));

    /// <summary>The control's sources in a fixed order (ordinal, ignoring case, then ordinal), so the audit entries
    /// of a run come out in the same order every time whatever order the operator wrote them in.</summary>
    public static IReadOnlyList<string> OrderedSources(DistrustedSources control)
    {
        ArgumentNullException.ThrowIfNull(control);
        return control.Sources
            .OrderBy(s => s, StringComparer.OrdinalIgnoreCase)
            .ThenBy(s => s, StringComparer.Ordinal)
            .ToList();
    }

    /// <summary>The number of rows of <paramref name="set"/> whose source tag is <paramref name="source"/> (trimmed,
    /// without regard to case: the rule <see cref="DistrustedSources.IsDistrusted"/> applies).</summary>
    public static int RowsFrom(MeasurementSet set, string source)
    {
        ArgumentNullException.ThrowIfNull(set);
        ArgumentException.ThrowIfNullOrWhiteSpace(source);
        string wanted = source.Trim();
        int count = 0;
        foreach (Measurement m in set.Measurements)
            if (m.Source is not null && string.Equals(m.Source.Trim(), wanted, StringComparison.OrdinalIgnoreCase))
                count++;
        return count;
    }

    /// <summary>
    /// The one-line reason written with a <see cref="SecurityEventKind.DistrustedSource"/> event: which source, who
    /// applied it, how many rows were demoted (or "every window" for a stream), the sigma factor, and that the rows are
    /// left out of the chi-square redundancy.
    /// </summary>
    public static string EventReason(string source, string appliedBy, int? rowsDemoted, double sigmaInflation)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(source);
        ArgumentException.ThrowIfNullOrWhiteSpace(appliedBy);
        string rows = rowsDemoted is int n
            ? $"{n.ToString(CultureInfo.InvariantCulture)} row(s)"
            : "its rows in every window";
        return $"measurement source '{source}' distrusted by {appliedBy}: {rows} demoted to pseudo-measurements " +
               $"(sigma x {sigmaInflation.ToString("R", CultureInfo.InvariantCulture)}) and left out of the chi-square redundancy";
    }

    /// <summary>
    /// Write exactly one <see cref="SecurityEventKind.DistrustedSource"/> event for each source of
    /// <paramref name="control"/>, in <see cref="OrderedSources"/> order, through <paramref name="record"/> (the host's
    /// audit path, normally <see cref="AuditRunRecorder.RecordSecurityEvent"/>). Call it once per run, when the run
    /// applies the control. Returns the sources recorded, in the order written.
    /// </summary>
    /// <param name="control">The control the run applies; must name at least one source (an empty control applies
    /// nothing, so there is nothing to record: refused).</param>
    /// <param name="appliedBy">What applied it, for the reason text (for example <c>estimate --distrust</c>).</param>
    /// <param name="rowsDemoted">The number of rows demoted per source, or null for a stream (serve, the resident
    /// estimator) where the control applies to every window.</param>
    /// <param name="record">Writes one security event.</param>
    public static IReadOnlyList<string> RecordApplied(
        DistrustedSources control, string appliedBy, Func<string, int>? rowsDemoted, Action<SecurityEventKind, string> record)
    {
        ArgumentNullException.ThrowIfNull(control);
        ArgumentNullException.ThrowIfNull(record);
        ArgumentException.ThrowIfNullOrWhiteSpace(appliedBy);
        if (control.IsEmpty)
            throw new ArgumentException("An empty distrusted-source control applies nothing, so it has no event to record.", nameof(control));

        IReadOnlyList<string> sources = OrderedSources(control);
        foreach (string source in sources)
            record(SecurityEventKind.DistrustedSource,
                EventReason(source, appliedBy, rowsDemoted?.Invoke(source), control.SigmaInflation));
        return sources;
    }
}
