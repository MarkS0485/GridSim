using System;
using GridSim.Core.Diagnostics;
using GridSim.Core.Diagnostics.Incident;
using Xunit;

namespace GridSim.Tests;

/// <summary>
/// The incident router (NCSC CAF C1/D1; NIS Regulation 11): a detected security event becomes a structured,
/// restriction-safe <see cref="IncidentReport"/> published to a deploying OES's sink. A raised incident reaches
/// the sink; a horizon-violating grid instant is refused at construction (no incident may carry a future-dated
/// grid claim); a permitted-age grid instant is accepted; and identical inputs produce an identical, content
/// derived incident id (determinism).
/// </summary>
public sealed class IncidentRouterTests
{
    private static readonly DateTime Raised = new(2026, 6, 1, 12, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void A_raised_incident_reaches_the_sink_classified_by_policy()
    {
        var sink = new BufferedIncidentSink();
        var router = new IncidentRouter(sink);

        IncidentReport returned = router.Raise(Raised, SecurityEventKind.SuspectedFdia,
            "chi-square PASSED but the state is out of band");

        IncidentReport published = Assert.Single(sink.Reports);
        Assert.Equal(returned, published);
        // The policy maps SuspectedFdia to the SuspectedFdia category at the Significant baseline.
        (IncidentCategory category, SecuritySeverity severity) = IncidentPolicy.Classify(SecurityEventKind.SuspectedFdia);
        Assert.Equal(category, published.Category);
        Assert.Equal(severity, published.Severity);
    }

    [Fact]
    public void A_grid_instant_newer_than_the_horizon_is_refused()
    {
        var sink = new BufferedIncidentSink();
        var router = new IncidentRouter(sink);

        // A state time of "now" is inside the 169 h horizon, so it is a future-dated grid claim and is refused.
        DateTime now = DateTime.UtcNow;
        Assert.Throws<ForwardInferenceException>(() =>
            router.Raise(Raised, SecurityEventKind.HorizonViolation, "too fresh",
                stateTimeUtc: now, utcNowForHorizon: now));
        Assert.Empty(sink.Reports);
    }

    [Fact]
    public void A_permitted_age_grid_instant_is_accepted()
    {
        var sink = new BufferedIncidentSink();
        var router = new IncidentRouter(sink);

        DateTime now = DateTime.UtcNow;
        DateTime oldEnough = now.AddHours(-200);   // comfortably older than the 169 h horizon
        IncidentReport report = router.Raise(Raised, SecurityEventKind.InfeasibleState, "infeasible converged state",
            stateTimeUtc: oldEnough, utcNowForHorizon: now);

        Assert.Single(sink.Reports);
        Assert.Equal(oldEnough, report.StateTimeUtc);
    }

    [Fact]
    public void Identical_inputs_produce_an_identical_incident_id()
    {
        IncidentReport a = IncidentReport.Create(Raised, IncidentCategory.SuspectedFdia, SecuritySeverity.Significant,
            "same summary", detailJson: "{\"a\":1}", auditHash: "HASH");
        IncidentReport b = IncidentReport.Create(Raised, IncidentCategory.SuspectedFdia, SecuritySeverity.Significant,
            "same summary", detailJson: "{\"a\":1}", auditHash: "HASH");

        Assert.Equal(a.Id, b.Id);

        // A changed field changes the id.
        IncidentReport c = IncidentReport.Create(Raised, IncidentCategory.SuspectedFdia, SecuritySeverity.Significant,
            "different summary", detailJson: "{\"a\":1}", auditHash: "HASH");
        Assert.NotEqual(a.Id, c.Id);
    }

    [Fact]
    public void A_severity_override_replaces_the_policy_baseline()
    {
        var sink = new BufferedIncidentSink();
        var router = new IncidentRouter(sink);

        IncidentReport report = router.Raise(Raised, SecurityEventKind.SuspectedFdia, "escalated burst",
            severityOverride: SecuritySeverity.Critical);
        Assert.Equal(SecuritySeverity.Critical, report.Severity);
    }

    [Fact]
    public void An_incident_detail_that_is_not_json_is_refused()
    {
        // GR-850: a sink parsing DetailJson must never meet malformed text.
        Assert.Throws<ArgumentException>(() => IncidentReport.Create(Raised, IncidentCategory.SuspectedFdia,
            SecuritySeverity.Significant, "bad detail", detailJson: "{oops"));
        Assert.Equal("{\"a\":1}", IncidentReport.Create(Raised, IncidentCategory.SuspectedFdia,
            SecuritySeverity.Significant, "good detail", detailJson: "{\"a\":1}").DetailJson);
    }

    [Fact]
    public void A_negative_burst_threshold_is_refused_on_construction_and_on_a_copy()
    {
        // GR-850: a threshold is a count of events; 0 still means "not escalated".
        Assert.Throws<ArgumentOutOfRangeException>(() => new IncidentPolicy(HorizonViolationBurst: -1));
        Assert.Throws<ArgumentOutOfRangeException>(() => IncidentPolicy.Default with { SuspectedFdiaBurst = -1 });
        Assert.Equal(0, new IncidentPolicy(AuditChainBrokenBurst: 0).AuditChainBrokenBurst);
        Assert.Equal(20, IncidentPolicy.Default.HorizonViolationBurst);
    }
}
