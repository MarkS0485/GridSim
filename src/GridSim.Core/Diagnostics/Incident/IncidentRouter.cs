namespace GridSim.Core.Diagnostics.Incident;

/// <summary>
/// Turns a detected security event into a structured <see cref="IncidentReport"/> and publishes it to the
/// deploying OES's <see cref="IIncidentSink"/> (NCSC CAF C1/D1; NIS Regulation 11). It applies an
/// <see cref="IncidentPolicy"/> to choose the category and baseline severity, then builds a restriction-safe
/// report (its grid instant, if any, is horizon-checked by <see cref="IncidentReport.Create"/>).
///
/// <para>The router is deterministic: given the same events with the same timestamps it produces the same
/// incident sequence (the report ids are content-derived), so incident evidence is reproducible under replay.
/// It never reads the wall clock itself; the caller supplies the raised instant (in the resident loop, the
/// window's run timestamp).</para>
/// </summary>
public sealed class IncidentRouter
{
    private readonly IIncidentSink _sink;
    private readonly IncidentPolicy _policy;

    /// <summary>Create a router that publishes to <paramref name="sink"/> using <paramref name="policy"/>
    /// (the conservative default when null).</summary>
    public IncidentRouter(IIncidentSink sink, IncidentPolicy? policy = null)
    {
        _sink = sink ?? throw new ArgumentNullException(nameof(sink));
        _policy = policy ?? IncidentPolicy.Default;
    }

    /// <summary>The policy this router applies.</summary>
    public IncidentPolicy Policy => _policy;

    /// <summary>
    /// Raise an incident from a single security event: classify it, build the report, publish it, and return it.
    /// </summary>
    /// <param name="raisedUtc">The instant the event was raised (the window's run timestamp in the resident loop,
    /// an injected value in tests), used as the report's <see cref="IncidentReport.RaisedUtc"/>.</param>
    /// <param name="kind">The security event kind.</param>
    /// <param name="summary">A one-line, secret-free description.</param>
    /// <param name="detailJson">Structured, secret-free detail (JSON).</param>
    /// <param name="auditHash">The hash of the audit entry that evidences this event, or empty.</param>
    /// <param name="stateTimeUtc">A permitted-age grid instant the event concerns, or null (horizon-checked).</param>
    /// <param name="utcNowForHorizon">The "now" for the horizon check of <paramref name="stateTimeUtc"/>.</param>
    /// <param name="severityOverride">Use this severity instead of the policy baseline (for an escalated burst).</param>
    public IncidentReport Raise(
        DateTime raisedUtc,
        SecurityEventKind kind,
        string summary,
        string detailJson = "{}",
        string auditHash = "",
        DateTime? stateTimeUtc = null,
        DateTime? utcNowForHorizon = null,
        SecuritySeverity? severityOverride = null)
    {
        (IncidentCategory category, SecuritySeverity baseline) = IncidentPolicy.Classify(kind);
        SecuritySeverity severity = severityOverride ?? baseline;
        IncidentReport report = IncidentReport.Create(
            raisedUtc, category, severity, summary, stateTimeUtc, detailJson, auditHash, utcNowForHorizon);
        Publish(report);
        return report;
    }

    /// <summary>Publish a prepared incident to the sink. A sink failure is the sink's own concern (it must not
    /// throw into the caller), so this never lets a reporting-channel problem stop the engine.</summary>
    public void Publish(IncidentReport report)
    {
        ArgumentNullException.ThrowIfNull(report);
        _sink.Publish(report);
    }
}
