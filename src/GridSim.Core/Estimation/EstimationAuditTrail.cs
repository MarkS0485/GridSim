using System.Globalization;
using GridSim.Core.Diagnostics;

namespace GridSim.Core.Estimation;

/// <summary>
/// Records estimates into a tamper-evident <see cref="AuditLog"/> - the evidence trail a critical-national-
/// infrastructure estimator needs (NCSC CAF objective C). Each entry captures the estimate's outcome (method, status,
/// observability, objective, chi-square verdict, degrees of freedom, removals, measurement count) at its run time,
/// for the grid instant it describes (the present/past state time), tagged <see cref="ProvenanceTag.DerivedFromRecorded"/>
/// because an estimate is computed from recorded telemetry. Any later alteration of an entry breaks the hash chain.
///
/// <para><see cref="Outcome"/> is the ONE source of the outcome text. The CLI's <c>estimate --audit-log</c> puts it in
/// its run entry (through <see cref="AuditRunRecorder.SetOutcome"/>), and <see cref="Record"/> writes it as a
/// stand-alone entry for code that embeds the estimator. Until October 2026 the CLI built its own line and
/// <see cref="Record"/>, which nothing called, built a different, shorter one; the two could drift apart.</para>
/// </summary>
public static class EstimationAuditTrail
{
    /// <summary>
    /// The audit outcome text of one estimate:
    /// <c>method=&lt;m&gt; status=&lt;s&gt; observable=&lt;bool&gt; J=&lt;round-trip&gt; chi2=PASS|FAIL|not-run dof=&lt;n&gt; removed=&lt;n&gt; measurements=&lt;n&gt;</c>.
    /// J is written round-trip ("R") so the logged objective is the exact double. chi2 is <c>not-run</c> (N208; was n/a) when the
    /// estimate did not converge, when the chi-square threshold is undefined (no redundancy) or when the test did not
    /// run (R261: a test that never ran used to be recorded as PASS). <paramref name="extra"/> (for example the HTI and
    /// FDIA fields) is appended verbatim; pass "" for none, or begin it with a space.
    /// <para>N064 (October 2026): when a robust method's re-weighting stopped on its cap
    /// (<see cref="EstimationResult.IrlsCapReached"/>), <c> irls_cap_reached=true</c> follows <c>measurements=</c> (before
    /// <paramref name="extra"/>). The field is written ONLY when the flag is true, so every outcome line of a run that did
    /// not hit the cap (every WLS run, every settled robust run) is byte-for-byte what it was before; a log reader
    /// treats the field's absence as false.</para>
    /// </summary>
    public static string Outcome(string method, EstimationResult estimate, int measurementCount, string extra = "")
    {
        ArgumentNullException.ThrowIfNull(estimate);
        // N208 (Ruling R1 follow-up, 2026-10-04): a test that did not run is recorded as chi2=not-run (was chi2=n/a),
        // never as a verdict. One token, because the line is space-separated key=value fields.
        string chi2 = estimate.Converged && !double.IsNaN(estimate.ChiSquareThreshold) ? ChiText(estimate.ChiSquareTest) : NotRunText;
        // CF6 (main's ruling on CF6-F1, Ruling R1): a LAV estimate gives no chi-square verdict anywhere, so its chi2 field
        // is "no-verdict" (never PASS or FAIL) and the LAV objective follows it as lav_objective=<round-trip> (written only
        // when the estimate converged). Every non-LAV outcome line is byte-for-byte what it was.
        string lav = "";
        if (!estimate.ChiSquareIsVerdict)
        {
            chi2 = NoVerdictText;
            if (estimate.LavObjective is double lo) lav = string.Create(CultureInfo.InvariantCulture, $" lav_objective={lo:R}");
        }
        string irls = estimate.IrlsCapReached ? " irls_cap_reached=true" : "";
        return string.Create(CultureInfo.InvariantCulture,
            $"method={method} status={estimate.Status} observable={estimate.Observability.Observable} J={estimate.ObjectiveJ:R} chi2={chi2}{lav} dof={estimate.DegreesOfFreedom} removed={estimate.Removed.Count} measurements={measurementCount}{irls}{extra}");
    }

    /// <summary>Append a stand-alone record of <paramref name="estimate"/> to <paramref name="log"/>: the text is
    /// <c>estimate </c> followed by <see cref="Outcome"/>.</summary>
    /// <param name="method">The estimation method name; "unspecified" when the caller does not say (an
    /// <see cref="EstimationResult"/> does not carry its method).</param>
    /// <param name="measurementCount">The size of the measurement set the estimate was given; when null, the rows the
    /// estimate accounts for (its residual table plus the rows bad-data removal took out).</param>
    public static AuditLogEntry Record(AuditLog log, DateTime runTimeUtc, DateTime? stateTimeUtc, EstimationResult estimate,
        string method = "unspecified", int? measurementCount = null)
    {
        ArgumentNullException.ThrowIfNull(log);
        ArgumentNullException.ThrowIfNull(estimate);
        int count = measurementCount ?? estimate.Residuals.Count + estimate.Removed.Count;
        return log.Append(runTimeUtc, stateTimeUtc, ProvenanceTag.DerivedFromRecorded,
            "estimate " + Outcome(method, estimate, count));
    }

    /// <summary>CF6-F1: the audit chi2 text of a LAV estimate, which gives no chi-square verdict.</summary>
    public const string NoVerdictText = "no-verdict";

    /// <summary>The audit text for a chi-square test that did not run (N208).</summary>
    public const string NotRunText = "not-run";

    /// <summary>The chi-square verdict as audit text: PASS / FAIL when the test ran, not-run when it did not.</summary>
    private static string ChiText(ChiSquareOutcome o) => o switch
    {
        ChiSquareOutcome.Passed => "PASS",
        ChiSquareOutcome.Failed => "FAIL",
        _ => NotRunText,
    };
}
